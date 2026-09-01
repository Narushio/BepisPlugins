using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using System.Reflection;
using ICSharpCode.SharpZipLib.Zip;
using MessagePack;
using Sideloader.ListLoader;
using UnityEngine;
using XUnity.ResourceRedirector;
#if AI || HS2
using AIChara;
#endif

namespace Sideloader
{
    [MessagePackObject]
    internal class ZipmodInfo : IDisposable
    {
        [Key(0)] public Manifest Manifest;
        [Key(1)] public string FileName;
        [Key(2)] public string RelativeFileName;
        [Key(3)] public DateTime LastWriteTime;
        [Key(4)] public long FileSize;
        [Key(5)] public string Error;

        [Key(6)] public List<string> PngNames = new List<string>();
        [Key(7)] public List<ChaListData> CharaLists = new List<ChaListData>();
        [Key(8)] public List<BundleLoadInfo> BundleInfos = new List<BundleLoadInfo>();
#if !EC
        [Key(9)] public List<Lists.StudioListData> BoneLists = new List<Lists.StudioListData>();
        [Key(10)] public List<Lists.StudioListData> StudioLists = new List<Lists.StudioListData>();
        [Key(11)] public List<Lists.StudioListData> MapLists = new List<Lists.StudioListData>();
#endif

        [IgnoreMember] private ZipFile _zipFile;
        [IgnoreMember] private readonly object _zipFileSync = new object();
        [IgnoreMember] public bool Loaded;
        [IgnoreMember] public bool Valid => Manifest != null && Error == null;

        [SerializationConstructor]
        public ZipmodInfo() { }

        public ZipmodInfo(string fileName)
        {
            FileName = fileName;
            RelativeFileName = Sideloader.GetRelativeArchiveDir(fileName);

            var fi = new FileInfo(fileName);
            LastWriteTime = fi.LastWriteTimeUtc;
            FileSize = fi.Length;
        }

        public ZipFile GetZipFile()
        {
            lock (_zipFileSync)
            {
                if (_zipFile == null)
                {
                    _zipFile = OpenSharedZipFile();
                }
                else if (_zipFile.Count == 0)
                {
                    // Disposing makes entry count = 0, but it should be at least 1 for the manifest.
                    // Still try to dispose the old zipfile just to be safe.
                    _zipFile.Close();
                    _zipFile = OpenSharedZipFile();
                }

                return _zipFile;
            }
        }

        internal TResult WithZipFile<TResult>(Func<ZipFile, TResult> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            lock (_zipFileSync)
            {
                var archive = GetZipFile();
                try
                {
                    return action(archive);
                }
                finally
                {
                    DisposeZipFile();
                }
            }
        }

        private ZipFile OpenSharedZipFile()
        {
            // Keep reading the currently loaded archive while allowing a mod manager to atomically
            // replace or delete its directory entry. The old stream remains valid until hot reload
            // retires this ZipmodInfo, and new requests are routed to the replacement archive.
            var stream = new FileStream(FileName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            try
            {
                return new ZipFile(stream) { IsStreamOwner = true };
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            lock (_zipFileSync)
                DisposeZipFile();
        }

        private void DisposeZipFile()
        {
            if (_zipFile != null)
            {
                _zipFile.Close();
                _zipFile = null;
            }
        }

        private static readonly MethodInfo _LocateZipEntryMethodInfo = typeof(ZipFile).GetMethod("LocateEntry", AccessTools.all);
        public void LoadAllLists(bool strict = false)
        {
            var zipmod = this;
            var arc = zipmod.GetZipFile();
            var manifest = zipmod.Manifest;

            try
            {
                foreach (ZipEntry entry in arc)
                {
                    var fullName = entry.Name;
                    // Find bundles in the archive
                    if (fullName.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
                    {
                        string assetBundlePath = fullName;

                        if (assetBundlePath.Contains('/'))
                            assetBundlePath = assetBundlePath.Remove(0, assetBundlePath.IndexOf('/') + 1);

                        if (entry.CompressionMethod == CompressionMethod.Stored)
                        {
                            long index = (long)_LocateZipEntryMethodInfo.Invoke(arc, new object[] { entry });
                            zipmod.BundleInfos.Add(new BundleLoadInfo(arc.Name, index, fullName, assetBundlePath));
                        }
                        else
                        {
                            zipmod.BundleInfos.Add(new BundleLoadInfo(arc.Name, -1, fullName, assetBundlePath));
                        }
                    }
                    // Find all list files in the archive
                    else if (fullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (fullName.StartsWith("abdata/list/characustom", StringComparison.OrdinalIgnoreCase))
                            {
                                ChaListData chaListData;
                                using (var stream = arc.GetInputStream(entry))
                                    chaListData = Lists.LoadCSV(stream);

                                SetPossessNew(chaListData);

                                zipmod.CharaLists.Add(chaListData);
                            }
#if !EC
                            else if (fullName.StartsWith("abdata/studio/info", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Path.GetFileNameWithoutExtension(fullName).ToLower().StartsWith("itembonelist_"))
                                {
                                    Lists.StudioListData studioListData;
                                    using (var stream = arc.GetInputStream(entry))
                                        studioListData = Lists.LoadStudioCSV(stream, fullName, manifest.GUID);

                                    zipmod.BoneLists.Add(studioListData);
                                }
                                else
                                {
                                    Lists.StudioListData studioListData;
                                    using (var stream = arc.GetInputStream(entry))
                                        studioListData = Lists.LoadStudioCSV(stream, fullName, manifest.GUID);

                                    zipmod.StudioLists.Add(studioListData);
                                }
                            }
#endif
#if AI || HS2
                            else if (fullName.StartsWith("abdata/list/map/", StringComparison.OrdinalIgnoreCase))
                            {
                                Lists.StudioListData data;
                                using (var stream = arc.GetInputStream(entry))
                                    data = Lists.LoadExcelDataCSV(stream, fullName);
                                zipmod.MapLists.Add(data);
                            }
#endif
                        }
                        catch (Exception ex)
                        {
                            if (strict)
                                throw new FormatException("Failed to load list file \"" + fullName + "\" from archive \"" +
                                                          Sideloader.GetRelativeArchiveDir(arc.Name) + "\"", ex);
                            Sideloader.Logger.LogError($"Failed to load list file \"{fullName}\" from archive \"{Sideloader.GetRelativeArchiveDir(arc.Name)}\" with error: {ex}");
                        }
                    }
                    // Find all png files in the archive
                    else if (fullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only list folders for .pngs in abdata folder, i.e. skip preview pics or character cards that might be included with the mod
                        if (fullName.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase))
                        {
                            zipmod.PngNames.Add(fullName);
                        }
                    }
                }
            }
            finally
            {
                // Never keep a source zipmod handle alive between actual reads. This also permits
                // replacing or deleting the parent mod folder, not just the individual archive.
                zipmod.Dispose();
            }
        }

        private static void SetPossessNew(ChaListData data)
        {
            for (int i = 0; i < data.lstKey.Count; i++)
            {
                if (data.lstKey[i] == "Possess")
                {
                    foreach (var kv in data.dictList)
                        kv.Value[i] = "1";
                    break;
                }
            }
        }
    }

    [MessagePackObject]
    internal class BundleLoadInfo
    {
        [SerializationConstructor]
        public BundleLoadInfo(string archiveFilename, long streamOffset, string bundleFullPath, string bundleTrimmedPath)
        {
            ArchiveFilename = archiveFilename;
            StreamOffset = streamOffset;
            BundleFullPath = bundleFullPath;
            BundleTrimmedPath = bundleTrimmedPath;
        }
        [Key(0)] public string ArchiveFilename { get; }
        [Key(1)] public long StreamOffset { get; }
        [IgnoreMember] public bool CanBeStreamed => StreamOffset > 0;
        [Key(2)] public string BundleFullPath { get; }
        [Key(3)] public string BundleTrimmedPath { get; }

        public AssetBundle LoadBundle()
        {
            AssetBundle bundle = null;

            // Unity keeps an opaque, non-delete-shared handle alive for bundles loaded directly from
            // a zipmod. Hot reload loads a content-versioned disk shadow instead: Unity gets its normal
            // low-overhead file-backed path while the source zipmod remains freely replaceable.
            if (Sideloader.HotReloadEnabled.Value)
            {
                var zipmod = Sideloader.Zipmods[ArchiveFilename];
                var cachePath = zipmod.WithZipFile(arc =>
                {
                    var entry = arc.GetEntry(BundleFullPath);
                    BundleDiskCache.TryGetOrCreate(zipmod, arc, entry, out var preparedPath);
                    return preparedPath;
                });

                if (!string.IsNullOrEmpty(cachePath))
                {
                    try
                    {
                        if (Sideloader.DebugLogging.Value)
                            Sideloader.Logger.LogDebug($"Loading \"{BundleFullPath}\" from hot-reload disk cache \"{cachePath}\"");
                        bundle = AssetBundle.LoadFromFile(cachePath);
                        if (bundle != null)
                            BundleDiskCache.Pin(bundle, cachePath);
                        else
                            BundleDiskCache.Invalidate(cachePath);
                    }
                    catch (Exception ex)
                    {
                        BundleDiskCache.Invalidate(cachePath);
                        Sideloader.Logger.LogWarning($"Failed to load cached bundle \"{BundleFullPath}\"; falling back to memory: {ex.Message}");
                    }
                }

                if (bundle == null)
                {
                    Sideloader.Logger.LogWarning($"Using memory fallback for \"{BundleFullPath}\" because its disk shadow cache was unavailable");
                    bundle = LoadBundleFromMemory();
                }
            }
            else if (CanBeStreamed)
            {
                if (Sideloader.DebugLogging.Value)
                    Sideloader.Logger.LogDebug($"Streaming \"{BundleFullPath}\" ({Sideloader.GetRelativeArchiveDir(ArchiveFilename)}) unity3d file from disk, offset {StreamOffset}");

                bundle = AssetBundle.LoadFromFile(ArchiveFilename, 0, (ulong)StreamOffset);
            }
            else
            {
                Sideloader.Logger.LogDebug($"Cannot stream \"{BundleFullPath}\" ({Sideloader.GetRelativeArchiveDir(ArchiveFilename)}) unity3d file from disk, loading to RAM instead");
                bundle = LoadBundleFromMemory();
            }

            if (bundle == null)
            {
                Sideloader.Logger.LogError($"Asset bundle \"{BundleFullPath}\" ({Sideloader.GetRelativeArchiveDir(ArchiveFilename)}) failed to load. It might have a conflicting CAB string.");
            }

            return bundle;
        }

        private AssetBundle LoadBundleFromMemory()
        {
            var zipmod = Sideloader.Zipmods[ArchiveFilename];
            return zipmod.WithZipFile(arc =>
            {
                var entry = arc.GetEntry(BundleFullPath);
                var bufferLength = checked((int)entry.Size);
                var buffer = new byte[bufferLength];
                using (var stream = arc.GetInputStream(entry))
                {
                    var bytesRead = 0;
                    while (bytesRead < bufferLength)
                    {
                        var read = stream.Read(buffer, bytesRead, bufferLength - bytesRead);
                        if (read == 0)
                            throw new EndOfStreamException($"Unexpected end of zipmod entry {BundleFullPath}");
                        bytesRead += read;
                    }
                }

                return AssetBundleHelper.LoadFromMemory($"\"{BundleFullPath}\" ({Sideloader.GetRelativeArchiveDir(ArchiveFilename)})", buffer, 0);
            });
        }
    }
}
