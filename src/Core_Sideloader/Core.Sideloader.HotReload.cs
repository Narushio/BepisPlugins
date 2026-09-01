using BepInEx.Logging;
using Sideloader.AutoResolver;
using Sideloader.ListLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sideloader
{
    /// <summary>
    /// Result of a zipmod hot reload pass.
    /// </summary>
    public sealed class ZipmodHotReloadResult
    {
        internal readonly List<string> AddedList = new List<string>();
        internal readonly List<string> UpdatedList = new List<string>();
        internal readonly List<string> RemovedList = new List<string>();
        internal readonly List<string> FailedFileList = new List<string>();
        internal readonly List<string> FailureDetailList = new List<string>();
        internal readonly List<ResolveInfo> AffectedCharacterResolveInfos = new List<ResolveInfo>();
        internal readonly HashSet<int> AffectedCharacterListCategories = new HashSet<int>();
        internal readonly HashSet<int> AffectedStudioItemSlots = new HashSet<int>();
        /// <summary>Whether all runtime registries were committed successfully.</summary>
        public bool CommitSucceeded { get; internal set; }

        /// <summary>Number of active mods added by this pass.</summary>
        public int AddedCount => AddedList.Count;
        /// <summary>Number of active mods replaced or switched to another archive by this pass.</summary>
        public int UpdatedCount => UpdatedList.Count;
        /// <summary>Number of active mods removed by this pass.</summary>
        public int RemovedCount => RemovedList.Count;
        /// <summary>Number of active mods added or updated. Kept for compatibility with the add-only prototype.</summary>
        public int LoadedCount => AddedCount + UpdatedCount;
        /// <summary>Number of archive parse failures plus a possible runtime commit failure.</summary>
        public int FailedCount => FailedFileList.Count;
        /// <summary>Number of active mods updated or removed. Kept for compatibility with the add-only prototype.</summary>
        public int ChangedOrRemovedCount => UpdatedCount + RemovedCount;
        /// <summary>Whether character lists were updated in an already initialized list controller.</summary>
        public bool CharacterListsInjected { get; internal set; }
        /// <summary>Whether at least one affected archive contains Studio-only list data.</summary>
        public bool ContainsStudioData { get; internal set; }
        /// <summary>Whether Studio list/category changes were deferred to the next Studio start.</summary>
        public bool StudioListChangesDeferred { get; internal set; }
        /// <summary>Number of characters whose currently instantiated zipmod assets were refreshed.</summary>
        public int RefreshedCharacterCount { get; internal set; }
        /// <summary>Number of already initialized Maker selection lists synchronized after list injection.</summary>
        public int RefreshedMakerListCount { get; internal set; }
        /// <summary>Number of Studio item instances whose zipmod prefab was refreshed.</summary>
        public int RefreshedStudioItemCount { get; internal set; }
        /// <summary>Number of instantiated assets that could not be refreshed after a successful registry update.</summary>
        public int AssetRefreshFailureCount { get; internal set; }
        /// <summary>Active archive paths added by this pass.</summary>
        public string[] AddedFiles => AddedList.ToArray();
        /// <summary>Active archive paths updated by this pass.</summary>
        public string[] UpdatedFiles => UpdatedList.ToArray();
        /// <summary>Active archive paths removed by this pass.</summary>
        public string[] RemovedFiles => RemovedList.ToArray();
        /// <summary>Archive paths added or updated by this pass.</summary>
        public string[] LoadedFiles => AddedList.Concat(UpdatedList).ToArray();
        /// <summary>Archive paths rejected by this pass.</summary>
        public string[] FailedFiles => FailedFileList.ToArray();
        /// <summary>Human-readable parse or commit failure details.</summary>
        public string[] FailureDetails => FailureDetailList.ToArray();
    }

    public partial class Sideloader
    {
        private static readonly StringComparer HotReloadStringComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly IEqualityComparer<ResolveInfo> ResolveIdentityComparer = new ResolveInfoIdentityComparer();
        private static readonly IEqualityComparer<StudioResolveInfo> StudioResolveIdentityComparer = new StudioResolveInfoIdentityComparer();

        /// <summary>
        /// Raised after a hot reload pass. Other plugins can use this to refresh their own zipmod-backed data.
        /// </summary>
        public static event Action<ZipmodHotReloadResult> ZipmodsHotReloaded;

        /// <summary>
        /// Compatibility alias for the original add-only prototype. It now applies additions, updates, and removals.
        /// Must be called from Unity's main thread.
        /// </summary>
        public ZipmodHotReloadResult ReloadNewZipmods() => ReloadZipmods();

        /// <summary>
        /// Scan configured mod directories and apply added, updated, and removed zipmods.
        /// Existing instantiated Unity objects are kept alive; future asset requests use the new registry.
        /// Must be called from Unity's main thread.
        /// </summary>
        public ZipmodHotReloadResult ReloadZipmods()
        {
            var result = new ZipmodHotReloadResult();
            var stopwatch = Stopwatch.StartNew();
            var foundPaths = FindZipmodPaths();
            var existingByPath = new Dictionary<string, ZipmodInfo>(HotReloadStringComparer);
            foreach (var pair in Zipmods)
                existingByPath[pair.Key] = pair.Value;

            var removedPaths = new HashSet<string>(existingByPath.Keys.Where(x => !foundPaths.Contains(x)), HotReloadStringComparer);
            var addedPaths = new HashSet<string>(foundPaths.Where(x => !existingByPath.ContainsKey(x)), HotReloadStringComparer);
            var changedPaths = new HashSet<string>(foundPaths.Where(existingByPath.ContainsKey)
                .Where(path => HasArchiveChanged(path, existingByPath[path])), HotReloadStringComparer);

            var parsedReplacements = new Dictionary<string, ZipmodInfo>(HotReloadStringComparer);
            foreach (var path in addedPaths.Concat(changedPaths).OrderBy(x => x, HotReloadStringComparer))
            {
                if (TryParseZipmod(path, out var parsed, out var error))
                    parsedReplacements[path] = parsed;
                else
                {
                    var relativePath = GetRelativeArchiveDir(path);
                    result.FailedFileList.Add(relativePath);
                    result.FailureDetailList.Add(relativePath + " — " + error);
                    Logger.LogError("Hot reload kept the previous state for [" + relativePath + "]: " + error);
                }
            }

            // A failed update keeps its previous in-memory entry. A failed addition is not committed.
            var candidateByPath = new Dictionary<string, ZipmodInfo>(HotReloadStringComparer);
            foreach (var pair in existingByPath)
            {
                if (removedPaths.Contains(pair.Key))
                    continue;
                if (parsedReplacements.ContainsKey(pair.Key))
                    continue;
                candidateByPath[pair.Key] = pair.Value;
            }
            foreach (var pair in parsedReplacements)
                candidateByPath[pair.Key] = pair.Value;

            var oldActiveByGuid = GetOldActiveZipmods(existingByPath);
            var newActiveByGuid = SelectActiveZipmods(candidateByPath.Values);
            var affectedGuids = GetAffectedGuids(oldActiveByGuid, newActiveByGuid, parsedReplacements.Keys);

            // Previously inactive archives may contain list data that was resolved during an earlier hot-reload pass.
            // Reparse any archive promoted to active so registration always starts from original IDs.
            var promotionAttempts = new HashSet<string>(HotReloadStringComparer);
            while (true)
            {
                var candidatesToRefresh = affectedGuids
                    .Where(newActiveByGuid.ContainsKey)
                    .Select(guid => newActiveByGuid[guid])
                    .Where(selected => !parsedReplacements.ContainsKey(selected.FileName))
                    .Where(selected => !oldActiveByGuid.TryGetValue(selected.Manifest.GUID, out var oldSelected) ||
                                       !ReferenceEquals(oldSelected, selected))
                    .Where(selected => promotionAttempts.Add(selected.FileName))
                    .Distinct()
                    .ToList();
                if (candidatesToRefresh.Count == 0)
                    break;

                foreach (var selected in candidatesToRefresh)
                {
                    if (TryParseZipmod(selected.FileName, out var fresh, out var error))
                    {
                        candidateByPath[selected.FileName] = fresh;
                        parsedReplacements[selected.FileName] = fresh;
                    }
                    else
                    {
                        var relativePath = GetRelativeArchiveDir(selected.FileName);
                        result.FailedFileList.Add(relativePath);
                        result.FailureDetailList.Add(relativePath + " — promotion failed: " + error);
                        Logger.LogError("Hot reload could not promote archive [" +
                                        GetRelativeArchiveDir(selected.FileName) + "]: " + error);
                        candidateByPath.Remove(selected.FileName);
                    }
                }

                newActiveByGuid = SelectActiveZipmods(candidateByPath.Values);
                affectedGuids = GetAffectedGuids(oldActiveByGuid, newActiveByGuid, parsedReplacements.Keys);
            }
            PopulateResultTransitions(result, affectedGuids, oldActiveByGuid, newActiveByGuid);

            var oldObjects = existingByPath.Values.ToList();
            if (affectedGuids.Count == 0)
            {
                ReplaceZipmodCatalog(candidateByPath, newActiveByGuid);
                DisposeRetiredZipmods(oldObjects, candidateByPath.Values);
                result.CommitSucceeded = true;
                TryRefreshHotReloadedAssets(result);
                LogHotReloadSummary(result, stopwatch.ElapsedMilliseconds);
                RaiseHotReloaded(result);
                return result;
            }

            var oldAffectedActive = affectedGuids
                .Where(oldActiveByGuid.ContainsKey)
                .Select(x => oldActiveByGuid[x])
                .Distinct()
                .ToList();
            var oldResolveInfos = UniversalAutoResolver.LoadedResolutionInfo.ToList();
            var oldSaveOnlyResolveInfos = UniversalAutoResolver.SaveOnlyResolutionInfo.ToList();
            var removedResolveInfos = oldResolveInfos.Where(x => affectedGuids.Contains(x.GUID)).ToList();
            var oldStudioResolveInfos = UniversalAutoResolver.LoadedStudioResolutionInfos
                .Where(x => affectedGuids.Contains(x.GUID))
                .ToList();
            var preserveStudioRuntimeLists = IsStudioRuntimeActive();

            try
            {
                ReplaceZipmodCatalog(candidateByPath, newActiveByGuid);
                RebuildManifestCatalog(newActiveByGuid);

                var activeOrdered = newActiveByGuid.OrderBy(x => x.Key, HotReloadStringComparer).Select(x => x.Value).ToList();
                RebuildExternalListCatalogs(activeOrdered, affectedGuids, oldResolveInfos,
                    oldSaveOnlyResolveInfos, oldStudioResolveInfos, preserveStudioRuntimeLists);

                var newAffected = activeOrdered.Where(x => affectedGuids.Contains(x.Manifest.GUID)).ToList();
                var newResolveInfos = UniversalAutoResolver.LoadedResolutionInfo
                    .Where(x => affectedGuids.Contains(x.GUID))
                    .ToList();

                // Preserve removed entries only in the LocalSlot -> ResolveInfo direction used while saving.
                // Putting these tombstones back into the normal resolver makes a later Character/Coordinate
                // Card load treat a deleted zipmod as installed and leaves the old clothes instance in place.
                var newResolveIdentities = new HashSet<ResolveInfo>(newResolveInfos, ResolveIdentityComparer);
                var saveOnlyResolveInfos = BuildIdentityLookup(
                        removedResolveInfos.Where(old => !newResolveIdentities.Contains(old))
                            .Concat(oldSaveOnlyResolveInfos),
                        ResolveIdentityComparer)
                    .Values
                    .ToList();
                UniversalAutoResolver.SetResolveInfos(
                    UniversalAutoResolver.LoadedResolutionInfo.ToList(), saveOnlyResolveInfos);
                UniversalAutoResolver.SetMigrationInfos(Manifests.Values.SelectMany(x => x.MigrationList).ToList());

                var currentStudioInfos = UniversalAutoResolver.LoadedStudioResolutionInfos
                    .Where(x => affectedGuids.Contains(x.GUID))
                    .ToList();
                if (!preserveStudioRuntimeLists)
                {
                    var currentStudioIdentities = new HashSet<StudioResolveInfo>(currentStudioInfos,
                        StudioResolveIdentityComparer);
                    foreach (var tombstone in oldStudioResolveInfos.Where(old => !currentStudioIdentities.Contains(old)))
                        UniversalAutoResolver.AddStudioResolutionInfo(tombstone);
                    UniversalAutoResolver.RebuildStudioResolutionLookups();
                }

                var preferredBundleIndexes = BundleManager.RemoveBundleLoaders(oldAffectedActive.Select(x => x.FileName));
                var newBundleInfos = newAffected.SelectMany(x => x.BundleInfos).ToList();
                AddBundles(newBundleInfos, preferredBundleIndexes);
                RebuildPngIndexes(activeOrdered);

                result.CharacterListsInjected = Lists.ApplyHotReloadedLists(removedResolveInfos, newAffected.SelectMany(x => x.CharaLists));
                foreach (var category in removedResolveInfos.Select(x => (int)x.CategoryNo)
                             .Concat(newAffected.SelectMany(x => x.CharaLists).Select(x => x.categoryNo)))
                    result.AffectedCharacterListCategories.Add(category);
                result.ContainsStudioData = oldAffectedActive.Concat(newAffected)
                    .Any(x => x.StudioLists.Count > 0 || x.BoneLists.Count > 0 || x.MapLists.Count > 0);
                result.StudioListChangesDeferred = preserveStudioRuntimeLists && result.ContainsStudioData;

                // Keep only references which still have a loadable definition after the commit. Removed
                // definitions stay instantiated and are deliberately not replaced with a vanilla fallback.
                result.AffectedCharacterResolveInfos.AddRange(newResolveInfos);
                AddAffectedStudioItemSlots(result, newAffected, currentStudioInfos, oldStudioResolveInfos);

#pragma warning disable CS0618
                LoadedManifests = Manifests.Values.ToList();
#pragma warning restore CS0618

                DisposeRetiredZipmods(oldObjects, candidateByPath.Values);
                result.CommitSucceeded = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Zipmod hot reload failed while committing state. Restart the game before saving cards or scenes. Error: " + ex);
                result.FailedFileList.Add("<commit>");
                result.FailureDetailList.Add("<commit> — " + ex.Message);
            }

            if (result.CommitSucceeded)
                TryRefreshHotReloadedAssets(result);

            LogHotReloadSummary(result, stopwatch.ElapsedMilliseconds);
            RaiseHotReloaded(result);
            return result;
        }

        private void TryRefreshHotReloadedAssets(ZipmodHotReloadResult result)
        {
            try
            {
                RefreshHotReloadedAssets(result);
            }
            catch (Exception ex)
            {
                result.AssetRefreshFailureCount++;
                Logger.LogWarning("Zipmod registry was updated, but selective live asset refresh failed: " + ex);
            }
        }

        private static bool HasArchiveChanged(string path, ZipmodInfo known)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Length != known.FileSize || file.LastWriteTimeUtc != known.LastWriteTime;
            }
            catch
            {
                return true;
            }
        }

        private static bool TryParseZipmod(string path, out ZipmodInfo info, out string error)
        {
            info = null;
            error = null;
            try
            {
                // Do not consume a file while a mod manager is still copying it.
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }

                info = new ZipmodInfo(path);
                var archive = info.GetZipFile();
                info.Manifest = Manifest.LoadFromZip(archive);
                if (string.IsNullOrEmpty(info.Manifest.GUID))
                    // Unity 5.6's bundled Mono profile does not expose InvalidDataException,
                    // even though it is present in the reference assemblies used by the build.
                    throw new FormatException("manifest.xml has no GUID");
                if (info.Manifest.Games.Count != 0 && !info.Manifest.Games.Select(x => x.ToLowerInvariant()).Any(GameNameList.Contains))
                    throw new PlatformNotSupportedException("archive is intended for " + string.Join(", ", info.Manifest.Games.ToArray()));

                info.LoadAllLists(true);

                var currentFile = new FileInfo(path);
                if (currentFile.Length != info.FileSize || currentFile.LastWriteTimeUtc != info.LastWriteTime)
                    throw new IOException("archive changed while it was being parsed; wait for the copy to finish and reload again");
                return true;
            }
            catch (Exception ex)
            {
                info?.Dispose();
                info = null;
                error = ex.Message;
                return false;
            }
        }

        private static Dictionary<string, ZipmodInfo> GetOldActiveZipmods(Dictionary<string, ZipmodInfo> existingByPath)
        {
            var result = new Dictionary<string, ZipmodInfo>(HotReloadStringComparer);
            foreach (var pair in ZipArchives)
            {
                if (existingByPath.TryGetValue(pair.Value, out var info))
                    result[pair.Key] = info;
            }
            return result;
        }

        private static Dictionary<string, ZipmodInfo> SelectActiveZipmods(IEnumerable<ZipmodInfo> candidates)
        {
            var result = new Dictionary<string, ZipmodInfo>(HotReloadStringComparer);
            foreach (var group in candidates.Where(x => x.Valid && !string.IsNullOrEmpty(x.Manifest.GUID)).GroupBy(x => x.Manifest.GUID, HotReloadStringComparer))
            {
                List<ZipmodInfo> ordered;
                try
                {
                    ordered = group.All(x => !string.IsNullOrEmpty(x.Manifest.Version))
                        ? group.OrderByDescending(x => x.Manifest.Version, new ManifestVersionComparer()).ThenByDescending(x => x.FileName.Length).ToList()
                        : group.OrderByDescending(x => x.LastWriteTime).ToList();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Failed to sort hot reload candidates for GUID [" + group.Key + "]: " + ex.Message);
                    ordered = group.ToList();
                }

                result[group.Key] = ordered[0];
            }
            return result;
        }

        private static HashSet<string> GetAffectedGuids(
            Dictionary<string, ZipmodInfo> oldActive,
            Dictionary<string, ZipmodInfo> newActive,
            IEnumerable<string> successfullyParsedPaths)
        {
            var parsedPaths = new HashSet<string>(successfullyParsedPaths, HotReloadStringComparer);
            var guids = new HashSet<string>(oldActive.Keys, HotReloadStringComparer);
            guids.UnionWith(newActive.Keys);
            guids.RemoveWhere(guid =>
            {
                if (!oldActive.TryGetValue(guid, out var oldInfo) || !newActive.TryGetValue(guid, out var newInfo))
                    return false;
                if (!HotReloadStringComparer.Equals(oldInfo.FileName, newInfo.FileName))
                    return false;
                return !parsedPaths.Contains(newInfo.FileName);
            });
            return guids;
        }

        private static void PopulateResultTransitions(
            ZipmodHotReloadResult result,
            IEnumerable<string> affectedGuids,
            Dictionary<string, ZipmodInfo> oldActive,
            Dictionary<string, ZipmodInfo> newActive)
        {
            foreach (var guid in affectedGuids.OrderBy(x => x, HotReloadStringComparer))
            {
                var hadOld = oldActive.TryGetValue(guid, out var oldInfo);
                var hasNew = newActive.TryGetValue(guid, out var newInfo);
                if (!hadOld && hasNew)
                    result.AddedList.Add(newInfo.RelativeFileName);
                else if (hadOld && !hasNew)
                    result.RemovedList.Add(oldInfo.RelativeFileName);
                else if (hadOld && hasNew)
                    result.UpdatedList.Add(newInfo.RelativeFileName);
            }
        }

        private static void ReplaceZipmodCatalog(
            Dictionary<string, ZipmodInfo> candidates,
            Dictionary<string, ZipmodInfo> activeByGuid)
        {
            var activePaths = new HashSet<string>(activeByGuid.Values.Select(x => x.FileName), HotReloadStringComparer);
            Zipmods.Clear();
            foreach (var pair in candidates)
            {
                pair.Value.Loaded = activePaths.Contains(pair.Key);
                Zipmods[pair.Key] = pair.Value;
            }
        }

        private static void RebuildManifestCatalog(Dictionary<string, ZipmodInfo> activeByGuid)
        {
            ZipArchives.Clear();
            Manifests.Clear();
            foreach (var pair in activeByGuid)
            {
                ZipArchives[pair.Key] = pair.Value.FileName;
                Manifests[pair.Key] = pair.Value.Manifest;
            }
        }

        private static void RebuildExternalListCatalogs(
            IList<ZipmodInfo> activeOrdered,
            HashSet<string> affectedGuids,
            IList<ResolveInfo> oldResolveInfos,
            IList<ResolveInfo> oldSaveOnlyResolveInfos,
            IList<StudioResolveInfo> oldStudioResolveInfos,
            bool preserveStudioRuntimeLists)
        {
            Lists.ExternalDataList.Clear();
            if (!preserveStudioRuntimeLists)
                Lists.ExternalStudioDataList.Clear();
            Lists.ExternalExcelData.Clear();

            var unaffectedResolveInfos = oldResolveInfos.Where(x => !affectedGuids.Contains(x.GUID)).ToList();
            // Save-only entries allow a removed archive that is re-added later to reclaim its former
            // LocalSlot. Existing instantiated objects then remain saveable and become refreshable again.
            var oldResolveLookup = BuildIdentityLookup(
                oldResolveInfos.Concat(oldSaveOnlyResolveInfos), ResolveIdentityComparer);
            var oldStudioResolveLookup = BuildIdentityLookup(oldStudioResolveInfos, StudioResolveIdentityComparer);
            UniversalAutoResolver.SetResolveInfos(unaffectedResolveInfos);
            if (!preserveStudioRuntimeLists)
                UniversalAutoResolver.RemoveStudioResolutionInfos(affectedGuids);

            foreach (var zipmod in activeOrdered.Where(x => !affectedGuids.Contains(x.Manifest.GUID)))
                RegisterAlreadyResolvedLists(zipmod, preserveStudioRuntimeLists);

            foreach (var zipmod in activeOrdered.Where(x => affectedGuids.Contains(x.Manifest.GUID)))
                RegisterHotReloadedLists(zipmod, unaffectedResolveInfos, oldResolveLookup, oldStudioResolveLookup,
                    preserveStudioRuntimeLists);

            UniversalAutoResolver.SetResolveInfos(unaffectedResolveInfos);
            if (!preserveStudioRuntimeLists)
                UniversalAutoResolver.RebuildStudioResolutionLookups();
        }

        private static void RegisterAlreadyResolvedLists(ZipmodInfo zipmod, bool preserveStudioRuntimeLists)
        {
            Lists.ExternalDataList.AddRange(zipmod.CharaLists);
            if (!preserveStudioRuntimeLists)
            {
                foreach (var list in zipmod.StudioLists)
                    AddExternalStudioList(list);
                foreach (var list in zipmod.BoneLists)
                    AddExternalStudioList(list);
            }
            foreach (var list in zipmod.MapLists)
                Lists.AddExcelDataCSV(list);
        }

        private static void RegisterHotReloadedLists(
            ZipmodInfo zipmod,
            List<ResolveInfo> resolveResults,
            IDictionary<ResolveInfo, ResolveInfo> oldResolveLookup,
            IDictionary<StudioResolveInfo, StudioResolveInfo> oldStudioResolveLookup,
            bool preserveStudioRuntimeLists)
        {
            foreach (var data in zipmod.CharaLists)
            {
                var start = resolveResults.Count;
                UniversalAutoResolver.GenerateResolutionInfo(zipmod.Manifest, data, resolveResults);
                ReuseCharacterSlots(data, resolveResults.Skip(start), oldResolveLookup);
                Lists.ExternalDataList.Add(data);
            }

            if (!preserveStudioRuntimeLists)
            {
                // Item lists must be registered before bone lists so bone IDs can follow their item IDs.
                foreach (var data in zipmod.StudioLists)
                {
                    RegisterStudioListWithSlotReuse(zipmod.Manifest, data, oldStudioResolveLookup);
                    AddExternalStudioList(data);
                }
                foreach (var data in zipmod.BoneLists)
                {
                    RegisterStudioListWithSlotReuse(zipmod.Manifest, data, oldStudioResolveLookup);
                    AddExternalStudioList(data);
                }
            }
            foreach (var data in zipmod.MapLists)
                Lists.AddExcelDataCSV(data);
        }

        private static void ReuseCharacterSlots(ChaListData data, IEnumerable<ResolveInfo> generated, IDictionary<ResolveInfo, ResolveInfo> oldLookup)
        {
            var replacements = new Dictionary<int, int>();
            foreach (var current in generated)
            {
                if (!oldLookup.TryGetValue(current, out var old))
                    continue;
                replacements[current.LocalSlot] = old.LocalSlot;
                current.LocalSlot = old.LocalSlot;
            }

            ReplaceFirstColumnIds(data.dictList.Values, replacements);
        }

        private static void RegisterStudioListWithSlotReuse(Manifest manifest, Lists.StudioListData data, IDictionary<StudioResolveInfo, StudioResolveInfo> oldLookup)
        {
            var start = UniversalAutoResolver.LoadedStudioResolutionInfos.Count;
            UniversalAutoResolver.GenerateStudioResolutionInfo(manifest, data);
            var generated = UniversalAutoResolver.LoadedStudioResolutionInfos;
            var replacements = new Dictionary<int, int>();

            for (var i = start; i < generated.Count; i++)
            {
                var current = generated[i];
                if (!oldLookup.TryGetValue(current, out var old))
                    continue;
                replacements[current.LocalSlot] = old.LocalSlot;
                current.LocalSlot = old.LocalSlot;
            }

            ReplaceFirstColumnIds(data.Entries, replacements);
        }

        private static Dictionary<T, T> BuildIdentityLookup<T>(IEnumerable<T> values, IEqualityComparer<T> comparer)
        {
            var result = new Dictionary<T, T>(comparer);
            foreach (var value in values)
            {
                // Callers order definitions by preference: the current active definition first when
                // reusing a slot, and the most recently removed definition first for save-only records.
                if (!result.ContainsKey(value))
                    result.Add(value, value);
            }
            return result;
        }

        private static void ReplaceFirstColumnIds(IEnumerable<List<string>> rows, Dictionary<int, int> replacements)
        {
            foreach (var row in rows)
            {
                if (row.Count > 0 && int.TryParse(row[0], out var id) && replacements.TryGetValue(id, out var replacement))
                    row[0] = replacement.ToString();
            }
        }

        private static bool SameResolveIdentity(ResolveInfo left, ResolveInfo right) =>
            HotReloadStringComparer.Equals(left.GUID, right.GUID) &&
            left.Slot == right.Slot &&
            left.CategoryNo == right.CategoryNo &&
            string.Equals(left.Property, right.Property, StringComparison.Ordinal);

        private static bool SameStudioResolveIdentity(StudioResolveInfo left, StudioResolveInfo right) =>
            HotReloadStringComparer.Equals(left.GUID, right.GUID) &&
            left.Slot == right.Slot &&
            left.ResolveItem == right.ResolveItem &&
            left.Group == right.Group &&
            left.Category == right.Category;

        private sealed class ResolveInfoIdentityComparer : IEqualityComparer<ResolveInfo>
        {
            public bool Equals(ResolveInfo left, ResolveInfo right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (ReferenceEquals(left, null) || ReferenceEquals(right, null)) return false;
                return SameResolveIdentity(left, right);
            }

            public int GetHashCode(ResolveInfo value)
            {
                if (ReferenceEquals(value, null)) return 0;
                unchecked
                {
                    var hash = HotReloadStringComparer.GetHashCode(value.GUID ?? string.Empty);
                    hash = (hash * 397) ^ value.Slot;
                    hash = (hash * 397) ^ value.CategoryNo.GetHashCode();
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(value.Property ?? string.Empty);
                    return hash;
                }
            }
        }

        private sealed class StudioResolveInfoIdentityComparer : IEqualityComparer<StudioResolveInfo>
        {
            public bool Equals(StudioResolveInfo left, StudioResolveInfo right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (ReferenceEquals(left, null) || ReferenceEquals(right, null)) return false;
                return SameStudioResolveIdentity(left, right);
            }

            public int GetHashCode(StudioResolveInfo value)
            {
                if (ReferenceEquals(value, null)) return 0;
                unchecked
                {
                    var hash = HotReloadStringComparer.GetHashCode(value.GUID ?? string.Empty);
                    hash = (hash * 397) ^ value.Slot;
                    hash = (hash * 397) ^ value.ResolveItem.GetHashCode();
                    hash = (hash * 397) ^ value.Group;
                    hash = (hash * 397) ^ value.Category;
                    return hash;
                }
            }
        }

        private static void AddExternalStudioList(Lists.StudioListData data)
        {
            if (!Lists.ExternalStudioDataList.TryGetValue(data.AssetBundleName, out var lists))
            {
                lists = new List<Lists.StudioListData>();
                Lists.ExternalStudioDataList[data.AssetBundleName] = lists;
            }
            lists.Add(data);
        }

        private static void AddAffectedStudioItemSlots(ZipmodHotReloadResult result,
            IEnumerable<ZipmodInfo> affectedZipmods,
            IEnumerable<StudioResolveInfo> currentInfos,
            IEnumerable<StudioResolveInfo> oldInfos)
        {
            var resolverInfos = currentInfos.Concat(oldInfos).Distinct().ToList();
            foreach (var zipmod in affectedZipmods)
            {
                foreach (var list in zipmod.StudioLists.Where(IsStudioItemList))
                {
                    foreach (var row in list.Entries)
                    {
                        if (row.Count == 0 || !int.TryParse(row[0], out var slot))
                            continue;

                        // In an active Studio process list rebuilding is intentionally deferred, so the
                        // CSV still contains its original slot. Otherwise it already contains LocalSlot.
                        if (!result.StudioListChangesDeferred)
                        {
                            result.AffectedStudioItemSlots.Add(slot);
                            continue;
                        }

                        foreach (var info in resolverInfos.Where(x => x.ResolveItem && x.Slot == slot &&
                                     HotReloadStringComparer.Equals(x.GUID, zipmod.Manifest.GUID)))
                            result.AffectedStudioItemSlots.Add(info.LocalSlot);
                    }
                }
            }
        }

        private static bool IsStudioItemList(Lists.StudioListData data)
        {
            var name = data.FileNameWithoutExtension;
            if (string.IsNullOrEmpty(name))
                return false;
            var separator = name.IndexOf('_');
            var listType = separator >= 0 ? name.Substring(0, separator) : name;
            return string.Equals(listType, "itemlist", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStudioRuntimeActive()
        {
#if KK
            return BepisPlugins.Constants.InsideStudio;
#else
            return false;
#endif
        }

        private void RebuildPngIndexes(IEnumerable<ZipmodInfo> activeZipmods)
        {
            PngList.Clear();
            PngFolderList.Clear();
            PngFolderOnlyList.Clear();
            foreach (var zipmod in activeZipmods)
                BuildPngFolderList(zipmod);
            BuildPngOnlyFolderList();
        }

        private static void DisposeRetiredZipmods(IEnumerable<ZipmodInfo> oldObjects, IEnumerable<ZipmodInfo> retainedObjects)
        {
            var retained = new HashSet<ZipmodInfo>(retainedObjects);
            foreach (var old in oldObjects.Where(x => !retained.Contains(x)))
                old.Dispose();
        }

        private static HashSet<string> FindZipmodPaths()
        {
            var result = new HashSet<string>(HotReloadStringComparer);
            var roots = new[] { ModsDirectory, AdditionalModsDirectory.Value };
            foreach (var root in roots.Where(x => !string.IsNullOrEmpty(x)).Distinct(HotReloadStringComparer))
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zipmod", StringComparison.OrdinalIgnoreCase))
                        result.Add(path);
                }
            }
            return result;
        }

        private static void LogHotReloadSummary(ZipmodHotReloadResult result, long elapsedMilliseconds)
        {
            var text = "Zipmod hot reload: added " + result.AddedCount + ", updated " + result.UpdatedCount +
                       ", removed " + result.RemovedCount + ", failed " + result.FailedCount +
                       " in " + elapsedMilliseconds + "ms.";
            if ((result.LoadedCount > 0 || result.RemovedCount > 0) && !result.CharacterListsInjected)
                text += " Character lists will update when their controller is initialized.";
            if (result.StudioListChangesDeferred)
                text += " Studio asset routing was updated; Studio list/category changes were deferred until CharaStudio restarts.";
            if (result.RefreshedMakerListCount > 0)
                text += " Synchronized " + result.RefreshedMakerListCount + " initialized Maker selection lists.";
            if (result.UpdatedCount > 0)
                text += " Selective live asset refresh: " + result.RefreshedCharacterCount + " characters, " +
                        result.RefreshedStudioItemCount + " Studio items, " +
                        result.AssetRefreshFailureCount + " refresh failures.";
            if (result.RemovedCount > 0)
                text += " Instances whose definitions were removed stay alive instead of being replaced by fallback assets.";

            Logger.Log(LogLevel.Info | LogLevel.Message, text);
        }

        private static void RaiseHotReloaded(ZipmodHotReloadResult result)
        {
            var handlers = ZipmodsHotReloaded;
            if (handlers == null)
                return;

            foreach (Action<ZipmodHotReloadResult> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(result);
                }
                catch (Exception ex)
                {
                    Logger.LogError("A ZipmodsHotReloaded subscriber failed: " + ex);
                }
            }
        }
    }
}
