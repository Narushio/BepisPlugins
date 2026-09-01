using BepInEx;
using ICSharpCode.SharpZipLib.Zip;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Sideloader
{
    internal sealed class BundleDiskCacheClearResult
    {
        internal bool Available { get; set; }
        internal int DeletedFiles { get; set; }
        internal long DeletedBytes { get; set; }
        internal int DeferredFiles { get; set; }
        internal long DeferredBytes { get; set; }
        internal int FailedFiles { get; set; }
        internal long RemainingBytes { get; set; }
    }

    /// <summary>
    /// Provides immutable, versioned disk shadows for AssetBundles stored inside zipmods.
    /// Unity may keep these files open, while the source archives remain replaceable.
    /// </summary>
    internal static class BundleDiskCache
    {
        private const string CacheDirectoryName = "SideloaderHotReloadBundles-v1";
        private const string ClearRequestFileName = ".clear-on-next-start";
        private const int CopyBufferSize = 128 * 1024;
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, string> BundlePaths = new Dictionary<int, string>();
        private static readonly Dictionary<string, int> PinCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DeferredDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _cacheDirectory;
        private static long _maxBytes;
        private static long _estimatedCacheBytes;
        private static int _retentionDays;
        private static bool _available;

        internal static string CacheDirectory => _cacheDirectory;
        internal static long EstimatedBytes => _estimatedCacheBytes;
        internal static long MaximumBytes => _maxBytes;

        internal static void Initialize(int maxGigabytes, int retentionDays)
        {
            lock (Sync)
            {
                _maxBytes = Math.Max(1L, maxGigabytes) * 1024L * 1024L * 1024L;
                _retentionDays = Math.Max(0, retentionDays);
                _cacheDirectory = Path.Combine(Paths.CachePath, CacheDirectoryName);
                try
                {
                    Directory.CreateDirectory(_cacheDirectory);
                    DeleteTemporaryFiles();
                    ProcessPendingClearRequest();
                    _estimatedCacheBytes = 0;
                    Prune(0, true);
                    _available = true;
                    Sideloader.Logger.LogInfo($"Hot-reload bundle disk cache ready at \"{_cacheDirectory}\" (target {Math.Max(1, maxGigabytes)} GB; source archives use transient handles)");
                }
                catch (Exception ex)
                {
                    _available = false;
                    Sideloader.Logger.LogWarning("Could not initialize hot-reload bundle disk cache; memory fallback will be used: " + ex.Message);
                }
            }
        }

        internal static bool TryGetOrCreate(ZipmodInfo zipmod, ZipFile archive, ZipEntry entry, out string cachePath)
        {
            cachePath = null;
            if (!_available || entry == null || entry.Size < 0)
                return false;

            lock (Sync)
            {
                try
                {
                    var key = CreateCacheKey(zipmod, entry);
                    cachePath = Path.Combine(_cacheDirectory, key + ".bundle");
                    if (IsValid(cachePath, entry.Size))
                    {
                        Touch(cachePath);
                        return true;
                    }

                    TryDeleteCacheFile(cachePath);
                    var allowedBytes = Math.Max(_maxBytes, entry.Size);
                    if (_estimatedCacheBytes > allowedBytes - entry.Size)
                        Prune(entry.Size, false);
                    WriteAtomically(archive, entry, cachePath);
                    if (!IsValid(cachePath, entry.Size))
                        return false;

                    _estimatedCacheBytes += entry.Size;
                    return true;
                }
                catch (Exception ex)
                {
                    Sideloader.Logger.LogWarning($"Could not prepare disk shadow for \"{entry.Name}\": {ex.Message}");
                    cachePath = null;
                    return false;
                }
            }
        }

        internal static void Pin(AssetBundle bundle, string cachePath)
        {
            if (ReferenceEquals(bundle, null) || string.IsNullOrEmpty(cachePath))
                return;

            lock (Sync)
            {
                var instanceId = bundle.GetInstanceID();
                if (BundlePaths.TryGetValue(instanceId, out var oldPath))
                    DecrementPin(oldPath);
                BundlePaths[instanceId] = cachePath;
                PinCounts[cachePath] = PinCounts.TryGetValue(cachePath, out var count) ? count + 1 : 1;
            }
        }

        internal static void Release(AssetBundle bundle)
        {
            if (ReferenceEquals(bundle, null))
                return;

            lock (Sync)
            {
                var instanceId = bundle.GetInstanceID();
                if (!BundlePaths.TryGetValue(instanceId, out var cachePath))
                    return;
                BundlePaths.Remove(instanceId);
                DecrementPin(cachePath);
                TryDeleteDeferredPath(cachePath);
            }
        }

        internal static BundleDiskCacheClearResult ClearUnused()
        {
            lock (Sync)
            {
                var result = new BundleDiskCacheClearResult
                {
                    Available = !string.IsNullOrEmpty(_cacheDirectory) && Directory.Exists(_cacheDirectory)
                };
                if (!result.Available)
                    return result;

                DeleteTemporaryFiles();
                foreach (var path in Directory.GetFiles(_cacheDirectory, "*.bundle"))
                {
                    var length = GetFileLength(path);
                    if (IsPinned(path))
                    {
                        DeferredDeletes.Add(path);
                        result.DeferredFiles++;
                        result.DeferredBytes += length;
                        continue;
                    }

                    if (TryDeleteCacheFile(path))
                    {
                        DeferredDeletes.Remove(path);
                        result.DeletedFiles++;
                        result.DeletedBytes += length;
                    }
                    else
                    {
                        DeferredDeletes.Add(path);
                        result.FailedFiles++;
                    }
                }

                UpdateClearRequestMarker();
                result.RemainingBytes = RecalculateEstimatedBytes();
                return result;
            }
        }

        internal static void SweepDeferredDeletes()
        {
            lock (Sync)
            {
                foreach (var path in DeferredDeletes.ToArray())
                    TryDeleteDeferredPath(path);
                UpdateClearRequestMarker();
                RecalculateEstimatedBytes();
            }
        }

        internal static void Invalidate(string cachePath)
        {
            if (string.IsNullOrEmpty(cachePath))
                return;
            lock (Sync)
            {
                if (!PinCounts.ContainsKey(cachePath))
                    TryDeleteCacheFile(cachePath);
            }
        }

        private static void DecrementPin(string cachePath)
        {
            if (!PinCounts.TryGetValue(cachePath, out var count))
                return;
            if (count <= 1)
                PinCounts.Remove(cachePath);
            else
                PinCounts[cachePath] = count - 1;
        }

        private static void TryDeleteDeferredPath(string cachePath)
        {
            if (string.IsNullOrEmpty(cachePath) || IsPinned(cachePath) || !DeferredDeletes.Contains(cachePath))
                return;
            if (TryDeleteCacheFile(cachePath))
                DeferredDeletes.Remove(cachePath);
        }

        private static void ProcessPendingClearRequest()
        {
            var markerPath = Path.Combine(_cacheDirectory, ClearRequestFileName);
            if (!File.Exists(markerPath))
                return;

            foreach (var path in Directory.GetFiles(_cacheDirectory, "*.bundle"))
            {
                if (IsPinned(path) || !TryDeleteCacheFile(path))
                    DeferredDeletes.Add(path);
                else
                    DeferredDeletes.Remove(path);
            }
            UpdateClearRequestMarker();
        }

        private static void UpdateClearRequestMarker()
        {
            if (string.IsNullOrEmpty(_cacheDirectory))
                return;

            DeferredDeletes.RemoveWhere(path => !File.Exists(path) && !IsPinned(path));
            var markerPath = Path.Combine(_cacheDirectory, ClearRequestFileName);
            if (DeferredDeletes.Count == 0)
            {
                TryDelete(markerPath);
                return;
            }

            try
            {
                File.WriteAllText(markerPath,
                    "Sideloader will delete AssetBundle cache files that were in use when cache clearing was requested.");
            }
            catch (Exception ex)
            {
                Sideloader.Logger.LogWarning("Could not persist the deferred AssetBundle cache clear request: " + ex.Message);
            }
        }

        private static string CreateCacheKey(ZipmodInfo zipmod, ZipEntry entry)
        {
            var manifest = zipmod.Manifest;
            var identity = string.Join("\n", new[]
            {
                CacheDirectoryName,
                manifest?.GUID ?? string.Empty,
                entry.Name ?? string.Empty,
                entry.Crc.ToString(CultureInfo.InvariantCulture),
                entry.Size.ToString(CultureInfo.InvariantCulture),
                entry.CompressedSize.ToString(CultureInfo.InvariantCulture)
            });

            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static void WriteAtomically(ZipFile archive, ZipEntry entry, string cachePath)
        {
            var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                long written = 0;
                var buffer = new byte[CopyBufferSize];
                using (var input = archive.GetInputStream(entry))
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                           CopyBufferSize))
                {
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                            break;
                        output.Write(buffer, 0, read);
                        written += read;
                    }
                    output.Flush();
                }

                if (written != entry.Size)
                    throw new EndOfStreamException($"Expected {entry.Size} bytes but extracted {written} bytes from {entry.Name}");

                try
                {
                    File.Move(temporaryPath, cachePath);
                }
                catch (IOException)
                {
                    if (!IsValid(cachePath, entry.Size))
                        throw;
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static void DeleteTemporaryFiles()
        {
            foreach (var path in Directory.GetFiles(_cacheDirectory, "*.tmp"))
                TryDelete(path);
        }

        private static void Prune(long requiredBytes, bool applyRetention)
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.bundle")
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            if (applyRetention && _retentionDays > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
                foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff).ToArray())
                {
                    if (IsPinned(file.FullName) || !TryDelete(file.FullName))
                        continue;
                    files.Remove(file);
                }
            }

            var totalBytes = files.Sum(file => file.Length);
            var allowedBytes = Math.Max(_maxBytes, requiredBytes);
            foreach (var file in files)
            {
                if (totalBytes + requiredBytes <= allowedBytes)
                    break;
                if (IsPinned(file.FullName) || !TryDelete(file.FullName))
                    continue;
                totalBytes -= file.Length;
            }

            _estimatedCacheBytes = totalBytes;
        }

        private static bool IsPinned(string path) => PinCounts.ContainsKey(path);

        private static bool IsValid(string path, long expectedSize)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists && file.Length == expectedSize;
            }
            catch
            {
                return false;
            }
        }

        private static void Touch(string path)
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch { }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteCacheFile(string path)
        {
            long length = 0;
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                    length = file.Length;
            }
            catch { }

            if (!TryDelete(path))
                return false;

            _estimatedCacheBytes = Math.Max(0, _estimatedCacheBytes - length);
            return true;
        }

        private static long GetFileLength(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static long RecalculateEstimatedBytes()
        {
            try
            {
                _estimatedCacheBytes = Directory.GetFiles(_cacheDirectory, "*.bundle")
                    .Sum(GetFileLength);
            }
            catch
            {
                _estimatedCacheBytes = 0;
            }
            return _estimatedCacheBytes;
        }
    }
}
