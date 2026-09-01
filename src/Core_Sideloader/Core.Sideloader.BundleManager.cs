using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Sideloader
{
    internal static class BundleManager
    {
        internal static Dictionary<string, List<LazyCustom<AssetBundle>>> Bundles = new Dictionary<string, List<LazyCustom<AssetBundle>>>();
        private static readonly Dictionary<string, List<string>> BundleOwners = new Dictionary<string, List<string>>();

        private static long CABCounter;

        // Only ASCII chars or we'll explode
        internal static string GenerateCAB() => "CAB-" + Interlocked.Increment(ref CABCounter).ToString("x32");

        internal static void RandomizeCAB(byte[] assetBundleData)
        {
            var ascii = Encoding.ASCII.GetString(assetBundleData, 0, Mathf.Min(1024, assetBundleData.Length - 4));

            var origCabIndex = ascii.IndexOf("CAB-", StringComparison.Ordinal);

            if (origCabIndex < 0)
                return;

            var origCabLength = ascii.Substring(origCabIndex).IndexOf('\0');

            if (origCabLength < 0)
                return;

            var CAB = GenerateCAB().Substring(4);
            var cabBytes = Encoding.ASCII.GetBytes(CAB);

            if (origCabLength > 36)
                return;

            Buffer.BlockCopy(cabBytes, 36 - origCabLength, assetBundleData, origCabIndex + 4, origCabLength - 4);
        }

        internal static void AddBundleLoader(Func<AssetBundle> func, string path, string archiveFilename, int preferredIndex = -1)
        {
            if (!Bundles.TryGetValue(path, out var lazyList))
            {
                lazyList = new List<LazyCustom<AssetBundle>>();
                Bundles.Add(path, lazyList);
            }

            if (!BundleOwners.TryGetValue(path, out var owners))
            {
                owners = new List<string>();
                BundleOwners.Add(path, owners);
            }

            var lazy = LazyCustom<AssetBundle>.Create(func);
            if (preferredIndex >= 0 && preferredIndex <= lazyList.Count)
            {
                lazyList.Insert(preferredIndex, lazy);
                owners.Insert(preferredIndex, archiveFilename);
            }
            else
            {
                lazyList.Add(lazy);
                owners.Add(archiveFilename);
            }
        }

        internal static Dictionary<string, int> RemoveBundleLoaders(IEnumerable<string> archiveFilenames)
        {
            var archives = new HashSet<string>(archiveFilenames, StringComparer.OrdinalIgnoreCase);
            var preferredIndexes = new Dictionary<string, int>();

            foreach (var path in Bundles.Keys.ToArray())
            {
                if (!BundleOwners.TryGetValue(path, out var owners))
                    continue;

                var lazyList = Bundles[path];
                var removedIndexes = Enumerable.Range(0, owners.Count)
                    .Where(i => archives.Contains(owners[i]))
                    .ToArray();
                if (removedIndexes.Length == 0)
                    continue;

                preferredIndexes[path] = removedIndexes[0];

                // Release disk-cache pins while the Unity objects are still alive. The game's
                // AssetBundleManager also caches the bundle returned by our loading hook; evict
                // that cache before replacing the loader or it will keep returning the old,
                // destroyed AssetBundle on the next character/coordinate load.
                foreach (var i in removedIndexes)
                {
                    try
                    {
                        if (lazyList[i].TryGetCreated(out var bundle) && bundle != null)
                            BundleDiskCache.Release(bundle);
                    }
                    catch (Exception ex)
                    {
                        Sideloader.Logger.LogWarning($"Failed to unpin old asset bundle [{path}] during hot reload: {ex.Message}");
                    }
                }

#if KK
                try
                {
                    // Force the game's reference counter to zero, but keep instantiated assets
                    // alive. Future requests will pass through AssetBundleLoadingHook again and
                    // receive the newly registered loader.
                    AssetBundleManager.UnloadAssetBundle(path, true, null, false);
                }
                catch (Exception ex)
                {
                    Sideloader.Logger.LogWarning($"Failed to evict game asset bundle cache [{path}] during hot reload: {ex.Message}");
                }
#endif

                for (var removed = removedIndexes.Length - 1; removed >= 0; removed--)
                {
                    var i = removedIndexes[removed];
                    try
                    {
                        if (lazyList[i].TryGetCreated(out var bundle) && bundle != null)
                            bundle.Unload(false);
                    }
                    catch (Exception ex)
                    {
                        Sideloader.Logger.LogWarning($"Failed to release old asset bundle [{path}] during hot reload: {ex.Message}");
                    }
                    lazyList.RemoveAt(i);
                    owners.RemoveAt(i);
                }

                // A cache-clear request may have deferred files that Unity kept open until
                // AssetBundle.Unload. Retry those deletions after all removed bundles are released.
                BundleDiskCache.SweepDeferredDeletes();

                if (lazyList.Count == 0)
                {
                    Bundles.Remove(path);
                    BundleOwners.Remove(path);
                }
            }

            return preferredIndexes;
        }

        internal static bool TryGetObjectFromName<T>(string name, string assetBundle, out T obj) where T : UnityEngine.Object
        {
            var result = TryGetObjectFromName(name, assetBundle, typeof(T), out var tObj);

            obj = (T)tObj;

            return result;
        }

        internal static bool TryGetObjectFromName(string name, string assetBundle, Type type, out UnityEngine.Object obj)
        {
            obj = null;

            if (Bundles.TryGetValue(assetBundle, out var lazyBundleList))
            {
                var found = -1;
                for (int i = 0; i < lazyBundleList.Count; i++)
                {
                    AssetBundle bundle = lazyBundleList[i];
                    if (bundle == null)
                        continue;
                    if (bundle.Contains(name))
                    {
                        // If using debug logging, check all override bundles for this asset and warn if multiple copies exist.
                        // This will force all override bundles to load so it's slower.
                        if (Sideloader.DebugLoggingModLoading.Value)
                        {
                            if (found >= 0)
                            {
                                Sideloader.Logger.LogWarning($"Asset [{name}] in bundle [{assetBundle}] is overridden by multiple zipmods! " +
                                                             $"Only asset from override #{found + 1} will be used! It also exists in override #{i + 1}.");
                            }
                            else
                            {
                                found = i;
                                obj = bundle.LoadAsset(name, type);
                            }

                            continue;
                        }

                        obj = bundle.LoadAsset(name, type);
                        return true;
                    }
                }

                // Needed for the logging codepath
                if (found >= 0) return true;
            }

            return false;
        }
    }
}
