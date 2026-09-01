using BepInEx.Configuration;
using BepInEx.Logging;
using Shared;
using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace Sideloader
{
    public partial class Sideloader
    {
        internal static ConfigEntry<bool> HotReloadShowWindow { get; private set; }
        internal static ConfigEntry<KeyboardShortcut> HotReloadWindowShortcut { get; private set; }

        private readonly int _hotReloadWindowId = (GUID + ".hotreload.window").GetHashCode();
        private Rect _hotReloadWindowRect;
        private Vector2 _hotReloadFailureScroll;
        private bool _hotReloadGuiRequested;
        private string _hotReloadGuiStatus = "Ready. Replace a .zipmod archive, then reload.";
        private string _hotReloadCacheStatus;
        private ZipmodHotReloadResult _hotReloadGuiLastResult;
        private GUIStyle _hotReloadWrappedBox;

        private void InitializeHotReloadGui()
        {
            HotReloadShowWindow = Config.Bind("Hot Reload", "Show hot reload window", false,
                "Show a compact window with manual reload, status, failure details, and disk-cache usage.");
            HotReloadWindowShortcut = Config.Bind("Hot Reload", "Toggle hot reload window", new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl),
                "Show or hide the Sideloader hot reload window.");

            var width = Mathf.Min(500f, Mathf.Max(320f, Screen.width - 20f));
            var height = Mathf.Min(470f, Mathf.Max(320f, Screen.height - 60f));
            _hotReloadWindowRect = new Rect(Mathf.Max(10f, Screen.width - width - 20f), 40f, width, height);
        }

        private void HandleHotReloadGuiInput()
        {
            if (HotReloadWindowShortcut.Value.IsDown())
                HotReloadShowWindow.Value = !HotReloadShowWindow.Value;
        }

        private bool ConsumeHotReloadGuiRequest()
        {
            if (!_hotReloadGuiRequested)
                return false;
            _hotReloadGuiRequested = false;
            return true;
        }

        private IEnumerator ReloadZipmodsWithGuiFeedback()
        {
            _hotReloadInProgress = true;
            _hotReloadGuiStatus = "Scanning Koikatu mod directories and preparing changes...";

            // Let IMGUI draw the busy state before the synchronous metadata scan begins.
            yield return null;

            var stopwatch = Stopwatch.StartNew();
            ZipmodHotReloadResult result = null;
            try
            {
                result = ReloadZipmods();
            }
            catch (Exception ex)
            {
                _hotReloadGuiStatus = "Reload aborted: " + ex.Message;
                HotReloadShowWindow.Value = true;
                Logger.LogError("Zipmod hot reload aborted: " + ex);
                Logger.Log(LogLevel.Error | LogLevel.Message, "Zipmod hot reload aborted: " + ex.Message);
                _hotReloadInProgress = false;
            }

            if (result == null)
                yield break;

            _hotReloadGuiLastResult = result;
            while (result.CommitSucceeded && _hotReloadPendingAssetRefreshes > 0)
            {
                _hotReloadGuiStatus = "Zipmod registry updated. Restoring MaterialEditor data, shaders, and " +
                                      "Dynamic Bones for " + _hotReloadPendingAssetRefreshes + " character(s)...";
                yield return null;
            }

            stopwatch.Stop();
            _hotReloadGuiStatus = BuildHotReloadStatus(result, stopwatch.ElapsedMilliseconds);
            if (result.FailedCount > 0 || result.AssetRefreshFailureCount > 0)
                HotReloadShowWindow.Value = true;
            _hotReloadInProgress = false;
        }

        private void OnGUI()
        {
            if (HotReloadShowWindow == null || !HotReloadShowWindow.Value)
                return;

            _hotReloadWindowRect.width = Mathf.Min(_hotReloadWindowRect.width,
                Mathf.Max(300f, Screen.width - 8f));
            _hotReloadWindowRect.height = Mathf.Min(_hotReloadWindowRect.height,
                Mathf.Max(260f, Screen.height - 8f));
            IMGUIUtils.DrawSolidBox(_hotReloadWindowRect);
            _hotReloadWindowRect = GUILayout.Window(_hotReloadWindowId, _hotReloadWindowRect,
                DrawHotReloadWindow, "Sideloader Hot Reload");
            _hotReloadWindowRect.x = Mathf.Clamp(_hotReloadWindowRect.x, 0f, Mathf.Max(0f, Screen.width - _hotReloadWindowRect.width));
            _hotReloadWindowRect.y = Mathf.Clamp(_hotReloadWindowRect.y, 0f, Mathf.Max(0f, Screen.height - 30f));
            IMGUIUtils.EatInputInRect(_hotReloadWindowRect);
        }

        private void DrawHotReloadWindow(int windowId)
        {
            if (_hotReloadWrappedBox == null)
            {
                _hotReloadWrappedBox = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = true
                };
            }
            var wrappedBox = _hotReloadWrappedBox;

            var settingsEnabled = GUI.enabled;
            GUI.enabled = settingsEnabled && !_hotReloadInProgress;
            var enabled = GUILayout.Toggle(HotReloadEnabled.Value, "Enable zipmod hot reload");
            if (enabled != HotReloadEnabled.Value)
                HotReloadEnabled.Value = enabled;

            var refreshShadersAndBones = GUILayout.Toggle(HotReloadRefreshShadersAndDynamicBones.Value,
                "Restore MaterialEditor, shaders, and Dynamic Bones");
            if (refreshShadersAndBones != HotReloadRefreshShadersAndDynamicBones.Value)
                HotReloadRefreshShadersAndDynamicBones.Value = refreshShadersAndBones;
            GUI.enabled = settingsEnabled;

            if (!HotReloadRefreshShadersAndDynamicBones.Value)
                GUILayout.Label("Runtime state restore is disabled. Assets are still replaced selectively, but " +
                                "MaterialEditor settings, shaders, and Dynamic Bones may require reloading the " +
                                "Coordinate or Character Card.",
                    wrappedBox);

            GUILayout.Label(_hotReloadGuiStatus, wrappedBox, GUILayout.MinHeight(44f));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Archives scanned: " + Zipmods.Count);
            GUILayout.Label("Active zipmods: " + ZipArchives.Count);
            GUILayout.EndHorizontal();
            GUILayout.Label("AssetBundle cache: " + FormatHotReloadBytes(BundleDiskCache.EstimatedBytes) + " / " +
                            FormatHotReloadBytes(BundleDiskCache.MaximumBytes));

            GUILayout.BeginHorizontal();
            var cacheButtonsEnabled = GUI.enabled;
            GUI.enabled = cacheButtonsEnabled && !_hotReloadInProgress;
            if (GUILayout.Button("Clear AssetBundle cache"))
                ClearAssetBundleCacheFromGui();
            if (GUILayout.Button("Rebuild metadata next launch"))
                ClearZipmodMetadataCacheFromGui();
            GUI.enabled = cacheButtonsEnabled;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_hotReloadCacheStatus))
                GUILayout.Label(_hotReloadCacheStatus, wrappedBox);

            if (_hotReloadGuiLastResult != null)
            {
                var result = _hotReloadGuiLastResult;
                GUILayout.Label((result.CommitSucceeded ? "Last reload applied" : "Last commit failed; runtime state may be incomplete") +
                                "  |  Added " + result.AddedCount + "  Updated " + result.UpdatedCount +
                                "  Removed " + result.RemovedCount + "  Failed " + result.FailedCount,
                    wrappedBox);
                GUILayout.Label("Live refresh: Maker lists " + result.RefreshedMakerListCount +
                                "  Characters " + result.RefreshedCharacterCount +
                                "  CharaStudio items " + result.RefreshedStudioItemCount +
                                "  Restore failures " + result.AssetRefreshFailureCount);

                if (result.FailedCount > 0)
                {
                    GUILayout.Label(result.CommitSucceeded
                        ? "Archives rejected during parsing (the previous valid state was kept):"
                        : "Failed archive or commit stage (restart before saving a Card or Studio scene):");
                    _hotReloadFailureScroll = GUILayout.BeginScrollView(_hotReloadFailureScroll,
                        GUILayout.Height(Mathf.Min(90f, Mathf.Max(48f, Screen.height - 330f))));
                    var details = result.FailureDetailList.Count > 0
                        ? result.FailureDetailList
                        : result.FailedFileList;
                    foreach (var detail in details)
                        GUILayout.Label(detail, wrappedBox);
                    GUILayout.EndScrollView();
                }

                if (result.AssetRefreshFailureCount > 0)
                        GUILayout.Label("Some live assets were not restored completely. Check the BepInEx log for " +
                                        "the affected plugin or Koikatu asset.",
                        wrappedBox);

                if (result.StudioListChangesDeferred)
                    GUILayout.Label("CharaStudio AssetBundle routing was updated. To preserve the current Item, " +
                                    "Animation, Sound, Clothes, and Joint Correction lists, category changes " +
                                    "will be applied the next time CharaStudio starts.", wrappedBox);
            }

            GUILayout.Label("Only active Koikatu character parts, accessories, and CharaStudio items using affected " +
                            "resolved slots are rebuilt. Character Cards, Coordinate Cards, and the current Studio " +
                            "scene are not reloaded.",
                wrappedBox);

            GUILayout.BeginHorizontal();
            var oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && HotReloadEnabled.Value && !_hotReloadInProgress;
            if (GUILayout.Button("Reload now (" + HotReloadShortcut.Value + ")", GUILayout.Height(30f)))
                _hotReloadGuiRequested = true;
            GUI.enabled = oldEnabled;

            if (GUILayout.Button("Close", GUILayout.Width(72f), GUILayout.Height(30f)))
                HotReloadShowWindow.Value = false;
            GUILayout.EndHorizontal();

            GUILayout.Label("Window shortcut: " + HotReloadWindowShortcut.Value);
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private static string BuildHotReloadStatus(ZipmodHotReloadResult result, long elapsedMilliseconds)
        {
            if (!result.CommitSucceeded)
                return "Commit failed (" + elapsedMilliseconds +
                       " ms). Runtime registries may be incomplete; restart before saving a Card or Studio scene.";

            if (result.AddedCount == 0 && result.UpdatedCount == 0 && result.RemovedCount == 0 &&
                result.FailedCount == 0)
                return "Scan complete (" + elapsedMilliseconds + " ms). No zipmod changes were found.";

            return "Complete (" + elapsedMilliseconds + " ms): added " + result.AddedCount +
                   ", updated " + result.UpdatedCount + ", removed " + result.RemovedCount +
                   ", failed " + result.FailedCount + "; synchronized Maker lists " + result.RefreshedMakerListCount +
                   ", refreshed characters " + result.RefreshedCharacterCount +
                   ", CharaStudio items " + result.RefreshedStudioItemCount +
                   ", restore failures " + result.AssetRefreshFailureCount + ".";
        }

        private static string FormatHotReloadBytes(long bytes)
        {
            if (bytes < 1024L)
                return bytes + " B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.00") + " GB";
        }

        private void ClearAssetBundleCacheFromGui()
        {
            try
            {
                var result = BundleDiskCache.ClearUnused();
                if (!result.Available)
                {
                    _hotReloadCacheStatus = "The Sideloader AssetBundle cache is unavailable. No files were removed.";
                    return;
                }

                _hotReloadCacheStatus = "AssetBundle cache: removed " + result.DeletedFiles + " file(s), " +
                                        FormatHotReloadBytes(result.DeletedBytes) + ".";
                if (result.DeferredFiles > 0)
                    _hotReloadCacheStatus += " " + result.DeferredFiles + " active file(s), " +
                                             FormatHotReloadBytes(result.DeferredBytes) +
                                             ", will be removed after their AssetBundles unload or on the next launch.";
                if (result.FailedFiles > 0)
                    _hotReloadCacheStatus += " " + result.FailedFiles +
                                             " locked file(s) could not be removed now and will be retried on the next launch.";
            }
            catch (Exception ex)
            {
                _hotReloadCacheStatus = "Could not clear the Sideloader AssetBundle cache: " + ex.Message;
                Logger.LogWarning(_hotReloadCacheStatus);
            }
        }

        private void ClearZipmodMetadataCacheFromGui()
        {
            var deletedFiles = 0;
            var failedFiles = 0;
            try
            {
                foreach (var path in System.IO.Directory.GetFiles(_CacheDirectory, _CacheName + ".*"))
                {
                    try
                    {
                        System.IO.File.Delete(path);
                        deletedFiles++;
                    }
                    catch (Exception ex)
                    {
                        failedFiles++;
                        Logger.LogWarning("Could not delete zipmod metadata cache file [" + path + "]: " + ex.Message);
                    }
                }

                _hotReloadCacheStatus = "Zipmod metadata cache: removed " + deletedFiles +
                                        " file(s). All .zipmod metadata will be parsed again on the next launch.";
                if (failedFiles > 0)
                    _hotReloadCacheStatus += " " + failedFiles + " file(s) could not be removed; see the BepInEx log.";
            }
            catch (Exception ex)
            {
                _hotReloadCacheStatus = "Could not clear the zipmod metadata cache: " + ex.Message;
                Logger.LogWarning(_hotReloadCacheStatus);
            }
        }

        partial void RefreshHotReloadedAssets(ZipmodHotReloadResult result);
    }
}
