using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Checksums;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BepInEx
{
    internal static class Paths
    {
        internal static string CachePath { get; set; }
    }
}

namespace UnityEngine
{
    internal sealed class AssetBundle
    {
        private static int _nextId;
        private readonly int _id = ++_nextId;
        public int GetInstanceID() => _id;
    }
}

namespace Sideloader
{
    internal sealed class TestLogger
    {
        internal void LogDebug(object value) { }
        internal void LogInfo(object value) { }
        internal void LogWarning(object value) => Console.WriteLine("WARN " + value);
    }

    internal static class Sideloader
    {
        internal static readonly TestLogger Logger = new TestLogger();
    }

    internal sealed class Manifest
    {
        internal string GUID { get; set; }
        internal string Version { get; set; }
    }

    internal sealed class ZipmodInfo
    {
        internal Manifest Manifest { get; set; }
    }

    internal static class Program
    {
        private const string EntryName = "abdata/chara/test/deflated.unity3d";
        private const string StoredEntryName = "abdata/chara/test/stored.unity3d";

        private static int Main()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var root = Path.Combine(Path.GetTempPath(), "SideloaderBundleDiskCacheHarness-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            BepInEx.Paths.CachePath = root;
            var source = Path.Combine(root, "sample.zipmod");
            var moved = Path.Combine(root, "sample.old.zipmod");
            CreateSampleZip(source);

            FileStream stream = null;
            ZipFile zip = null;
            try
            {
                stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                zip = new ZipFile(stream) { IsStreamOwner = true };
                var entry = zip.GetEntry(EntryName);
                Require(entry != null, "sample bundle entry is missing");

                BundleDiskCache.Initialize(1, 30);
                var infoV1 = new ZipmodInfo { Manifest = new Manifest { GUID = "test.bundle", Version = "1" } };
                Require(BundleDiskCache.TryGetOrCreate(infoV1, zip, entry, out var cacheV1), "first cache create failed");
                Require(BundleDiskCache.EstimatedBytes == entry.Size, "cache byte estimate was not updated after creation");
                var firstHash = HashFile(cacheV1);
                var firstCreation = File.GetCreationTimeUtc(cacheV1);

                Require(BundleDiskCache.TryGetOrCreate(infoV1, zip, entry, out var hitPath), "cache hit failed");
                Require(string.Equals(cacheV1, hitPath, StringComparison.OrdinalIgnoreCase), "cache hit changed its path");
                Require(File.GetCreationTimeUtc(cacheV1) == firstCreation, "cache hit rewrote the file");
                Require(BundleDiskCache.EstimatedBytes == entry.Size, "cache hit double-counted bytes");

                using (var corrupt = new FileStream(cacheV1, FileMode.Append, FileAccess.Write, FileShare.Read))
                    corrupt.WriteByte(0x7f);
                Require(BundleDiskCache.TryGetOrCreate(infoV1, zip, entry, out var repairedPath), "cache repair failed");
                Require(HashFile(repairedPath) == firstHash, "cache repair did not restore the content");
                Require(new FileInfo(repairedPath).Length == entry.Size, "cache length differs from the zip entry");
                Require(HashEntry(zip, entry) == firstHash, "cached bytes differ from the zip entry");

                var pinnedBundle = new UnityEngine.AssetBundle();
                BundleDiskCache.Pin(pinnedBundle, repairedPath);
                File.SetLastWriteTimeUtc(repairedPath, DateTime.UtcNow.AddDays(-60));
                BundleDiskCache.Initialize(1, 30);
                Require(File.Exists(repairedPath), "retention cleanup removed a pinned cache file");
                BundleDiskCache.Release(pinnedBundle);
                BundleDiskCache.Initialize(1, 30);
                Require(!File.Exists(repairedPath), "retention cleanup kept an unpinned expired file");
                Require(BundleDiskCache.EstimatedBytes == 0, "retention cleanup left a stale byte estimate");

                Require(BundleDiskCache.TryGetOrCreate(infoV1, zip, entry, out cacheV1), "cache recreate failed");
                var infoV2 = new ZipmodInfo { Manifest = new Manifest { GUID = "test.bundle", Version = "2" } };
                Require(BundleDiskCache.TryGetOrCreate(infoV2, zip, entry, out var cacheV2), "versioned cache create failed");
                Require(string.Equals(cacheV1, cacheV2, StringComparison.OrdinalIgnoreCase), "identical bundle content was duplicated after a manifest-only version change");
                VerifyAdditionalArchive(source, StoredEntryName);
                Require(!Directory.GetFiles(BundleDiskCache.CacheDirectory, "*.tmp").Any(), "atomic write left temporary files");

                var activeBundle = new UnityEngine.AssetBundle();
                BundleDiskCache.Pin(activeBundle, cacheV1);
                var clearResult = BundleDiskCache.ClearUnused();
                Require(clearResult.Available, "cache clear reported an unavailable directory");
                Require(clearResult.DeletedFiles >= 1, "cache clear did not delete an unused bundle");
                Require(clearResult.DeferredFiles == 1, "cache clear did not defer the active bundle");
                Require(File.Exists(cacheV1), "cache clear deleted an active bundle");
                Require(BundleDiskCache.EstimatedBytes == entry.Size, "cache clear reported an incorrect retained size");
                BundleDiskCache.Release(activeBundle);
                BundleDiskCache.SweepDeferredDeletes();
                Require(!File.Exists(cacheV1), "deferred cache clear did not remove the released bundle");
                Require(BundleDiskCache.EstimatedBytes == 0, "deferred cache clear left a stale byte estimate");

                File.Move(source, moved);
                CreateSampleZip(source);
                File.Delete(moved);
                Require(File.Exists(source) && !File.Exists(moved), "source archive could not be replaced while open");

                Console.WriteLine("PASS deflated stored create hit repair bytes version pin retention clear atomic replace");
                Console.WriteLine("CacheBytes=" + BundleDiskCache.EstimatedBytes + " CacheFiles=" + Directory.GetFiles(BundleDiskCache.CacheDirectory, "*.bundle").Length);
                return 0;
            }
            finally
            {
                if (zip != null)
                    zip.Close();
                else if (stream != null)
                    stream.Dispose();
                Cleanup(root);
            }
        }

        private static void CreateSampleZip(string path)
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var output = new ZipOutputStream(file))
            {
                WriteEntry(output, EntryName, CreatePayload(8192, 17), CompressionMethod.Deflated);
                WriteEntry(output, StoredEntryName, CreatePayload(4096, 29), CompressionMethod.Stored);
                output.Finish();
            }
        }

        private static void WriteEntry(ZipOutputStream output, string name, byte[] bytes, CompressionMethod method)
        {
            var crc = new Crc32();
            crc.Update(bytes);
            var entry = new ZipEntry(name)
            {
                DateTime = new DateTime(2026, 1, 1),
                CompressionMethod = method,
                Size = bytes.Length,
                Crc = crc.Value
            };
            if (method == CompressionMethod.Stored)
                entry.CompressedSize = bytes.Length;

            output.PutNextEntry(entry);
            output.Write(bytes, 0, bytes.Length);
            output.CloseEntry();
        }

        private static byte[] CreatePayload(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        private static string HashFile(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(input));
        }

        private static string HashEntry(ZipFile zip, ZipEntry entry)
        {
            using (var input = zip.GetInputStream(entry))
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(input));
        }

        private static void VerifyAdditionalArchive(string zipPath, string entryName)
        {
            using (var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var zip = new ZipFile(file) { IsStreamOwner = false })
            {
                var entry = zip.GetEntry(entryName);
                Require(entry != null, "additional sample entry is missing");
                var info = new ZipmodInfo { Manifest = new Manifest { GUID = "test.stored", Version = "1" } };
                Require(BundleDiskCache.TryGetOrCreate(info, zip, entry, out var cachePath), "additional cache create failed");
                Require(HashFile(cachePath) == HashEntry(zip, entry), "additional cached bytes differ from the zip entry");
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static void Cleanup(string root)
        {
            var fullRoot = Path.GetFullPath(root);
            var safePrefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "SideloaderBundleDiskCacheHarness-");
            if (!fullRoot.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path: " + root);
            if (Directory.Exists(fullRoot))
                Directory.Delete(fullRoot, true);
        }
    }
}
