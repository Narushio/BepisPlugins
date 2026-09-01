using Sideloader.AutoResolver;
using Sideloader.ListLoader;
using Studio;
using ChaCustom;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using CategoryNo = ChaListDefine.CategoryNo;

namespace Sideloader
{
    public partial class Sideloader
    {
        [Flags]
        private enum CharacterMaterialRefreshScope
        {
            None = 0,
            Clothes = 1,
            Accessories = 2,
            Hair = 4,
            Body = 8
        }

        private sealed class CharacterAssetRefreshTargets
        {
            internal readonly HashSet<int> Clothes = new HashSet<int>();
            internal readonly HashSet<int> Accessories = new HashSet<int>();
            internal readonly HashSet<int> Hair = new HashSet<int>();
            internal bool Head;
        }

        private static readonly BindingFlags HotReloadInstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly FieldInfo StudioGuideDictionaryField = typeof(GuideObjectManager)
            .GetField("dicGuideObject", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo StudioRootNodesField = typeof(TreeNodeCtrl)
            .GetField("m_TreeNodeObject", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo MakerSelectKindInitializedField = typeof(CustomSelectKind)
            .GetField("InitEnd", HotReloadInstanceFields);

        private static readonly FieldInfo MakerSelectKindListControllerField = typeof(CustomSelectKind)
            .GetField("listCtrl", HotReloadInstanceFields);

        private static readonly FieldInfo MakerSelectKindTypeField = typeof(CustomSelectKind)
            .GetField("type", HotReloadInstanceFields);

        private static readonly FieldInfo MakerSelectInfoListField = typeof(CustomSelectListCtrl)
            .GetField("lstSelectInfo", HotReloadInstanceFields);

        private static readonly FieldInfo MakerSelectListContentField = typeof(CustomSelectListCtrl)
            .GetField("objContent", HotReloadInstanceFields);

        private static readonly FieldInfo MakerSelectListTemplateField = typeof(CustomSelectListCtrl)
            .GetField("objTemp", HotReloadInstanceFields);

        private static readonly MethodInfo MakerSetToggleHandlerMethod = typeof(CustomSelectListCtrl)
            .GetMethod("SetToggleHandler", HotReloadInstanceFields);

        /// <summary>
        /// Unity 5.6's Object.FindObjectsOfType omits inactive components. Hot reload must also update
        /// live objects hidden by Maker or CharaStudio, while Resources.FindObjectsOfTypeAll additionally
        /// returns prefab/assets which must never be mutated. Limit the result to loaded scene instances.
        /// </summary>
        private static T[] FindLoadedSceneComponents<T>() where T : Component
        {
            return Resources.FindObjectsOfTypeAll<T>()
                .Where(component =>
                {
                    if (component == null || component.gameObject == null)
                        return false;

                    var scene = component.gameObject.scene;
                    return scene.IsValid() && scene.isLoaded;
                })
                .ToArray();
        }

        partial void RefreshHotReloadedAssets(ZipmodHotReloadResult result)
        {
            try
            {
                RefreshMakerSelectionLists(result);
            }
            catch (Exception ex)
            {
                result.AssetRefreshFailureCount++;
                Logger.LogWarning("Maker selection lists could not be synchronized; character and CharaStudio " +
                                  "asset refresh will continue: " + (ex.InnerException ?? ex));
            }

            if (result.AffectedCharacterResolveInfos.Count == 0 && result.AffectedStudioItemSlots.Count == 0)
                return;

            var affectedCharacterSlots = result.AffectedCharacterResolveInfos
                .GroupBy(x => x.CategoryNo)
                .ToDictionary(x => x.Key, x => new HashSet<int>(x.Select(y => y.LocalSlot)));

            if (affectedCharacterSlots.Count > 0)
            {
                foreach (var chaControl in FindLoadedSceneComponents<ChaControl>())
                {
                    try
                    {
                        if (RefreshCharacterAssets(chaControl, affectedCharacterSlots, result))
                            result.RefreshedCharacterCount++;
                    }
                    catch (Exception ex)
                    {
                        result.AssetRefreshFailureCount++;
                        Logger.LogWarning("Failed to refresh hot-reloaded assets on character [" +
                                          chaControl.name + "]: " + ex);
                    }
                }
            }

            RefreshStudioItemAssets(result);
        }

        /// <summary>
        /// Koikatu's Maker takes a one-time snapshot of each item category when its selection page is
        /// first opened. Updating ChaListControl alone therefore leaves previously visited pages stale,
        /// while pages opened after a hot reload see the new entries. Initialized pages are commonly
        /// deactivated when the user switches to another slot, so they must be found independently of
        /// active state. Synchronize each loaded scene instance in place: destroying and recreating the
        /// entire list also destroys state owned by the Maker and UI translation plugins, and can leave
        /// the selection window empty.
        /// </summary>
        private static void RefreshMakerSelectionLists(ZipmodHotReloadResult result)
        {
            if (!result.CharacterListsInjected || result.AffectedCharacterListCategories.Count == 0)
                return;

            var selectKinds = FindLoadedSceneComponents<CustomSelectKind>();
            if (selectKinds.Length == 0)
                return;

            if (MakerSelectKindInitializedField == null || MakerSelectKindListControllerField == null ||
                MakerSelectKindTypeField == null || MakerSelectInfoListField == null ||
                MakerSelectListContentField == null || MakerSelectListTemplateField == null ||
                MakerSetToggleHandlerMethod == null)
            {
                result.AssetRefreshFailureCount++;
                Logger.LogWarning("Maker selection list hot refresh is not compatible with this game build.");
                return;
            }

            var processedControllers = new HashSet<int>();
            var processedSurfaces = new HashSet<long>();
            foreach (var selectKind in selectKinds)
            {
                if (selectKind == null || !(bool)MakerSelectKindInitializedField.GetValue(selectKind))
                    continue;

                var listController = MakerSelectKindListControllerField.GetValue(selectKind) as CustomSelectListCtrl;
                var entries = listController == null ? null : MakerSelectInfoListField.GetValue(listController) as IList;
                if (listController == null || entries == null)
                    continue;
                if (!processedControllers.Add(listController.GetInstanceID()))
                    continue;

                var category = GetMakerSelectionCategory(selectKind, entries);
                if (category < 0 || !result.AffectedCharacterListCategories.Contains(category))
                    continue;

                // Some Maker panels expose separate CustomSelectKind/CustomSelectListCtrl instances over
                // the same content object. Treat the visible surface and category as the identity of a list;
                // otherwise each wrapper appends its own copy of a newly hot-added item.
                var content = MakerSelectListContentField.GetValue(listController) as GameObject;
                if (content != null)
                {
                    var surfaceKey = ((long)content.GetInstanceID() << 32) ^ (uint)category;
                    if (!processedSurfaces.Add(surfaceKey))
                        continue;
                }

                try
                {
                    if (SynchronizeMakerSelectionList(listController, entries, category))
                        result.RefreshedMakerListCount++;
                }
                catch (Exception ex)
                {
                    result.AssetRefreshFailureCount++;
                    Logger.LogWarning("Failed to synchronize a hot-reloaded Maker selection list: " +
                                      (ex.InnerException ?? ex));
                }
            }

            if (result.RefreshedMakerListCount > 0)
                Logger.LogDebug("Synchronized " + result.RefreshedMakerListCount +
                                " initialized Maker selection list(s) after zipmod hot reload.");
        }

        private static int GetMakerSelectionCategory(CustomSelectKind selectKind, IList entries)
        {
            foreach (var entry in entries)
            {
                var selectInfo = entry as CustomSelectInfo;
                if (selectInfo != null)
                    return selectInfo.category;
            }

            // Empty pages have no snapshot from which to infer their category. This is the category table
            // used by CustomSelectKind.Initialize in Koikatu, expressed compactly for the repeated clothes
            // pattern/emblem groups.
            var selectType = Convert.ToInt32(MakerSelectKindTypeField.GetValue(selectKind));
            switch (selectType)
            {
                case 0: return (int)CategoryNo.mt_face_detail;
                case 1: return (int)CategoryNo.mt_eyebrow;
                case 2: return (int)CategoryNo.mt_eyeline_up;
                case 3: return (int)CategoryNo.mt_eyeline_down;
                case 4: return (int)CategoryNo.mt_eye_white;
                case 5: return (int)CategoryNo.mt_eye_hi_up;
                case 6: return (int)CategoryNo.mt_eye_hi_down;
                case 7: return (int)CategoryNo.mt_eye;
                case 8: return (int)CategoryNo.mt_eye_gradation;
                case 9: return (int)CategoryNo.mt_nose;
                case 10: return (int)CategoryNo.mt_lipline;
                case 11: return (int)CategoryNo.mt_mole;
                case 12: return (int)CategoryNo.mt_eyeshadow;
                case 13: return (int)CategoryNo.mt_cheek;
                case 14: return (int)CategoryNo.mt_lip;
                case 15:
                case 16: return (int)CategoryNo.mt_face_paint;
                case 17: return (int)CategoryNo.mt_body_detail;
                case 18: return (int)CategoryNo.mt_nip;
                case 19: return (int)CategoryNo.mt_underhair;
                case 20: return (int)CategoryNo.mt_sunburn;
                case 21:
                case 22: return (int)CategoryNo.mt_body_paint;
                case 23:
                case 24: return (int)CategoryNo.bodypaint_layout;
                case 25: return (int)CategoryNo.bo_hair_b;
                case 26: return (int)CategoryNo.bo_hair_f;
                case 27: return (int)CategoryNo.bo_hair_s;
                case 28: return (int)CategoryNo.bo_hair_o;
                case 29: return (int)CategoryNo.co_top;
                case 30: return (int)CategoryNo.cpo_sailor_a;
                case 31: return (int)CategoryNo.cpo_sailor_b;
                case 32: return (int)CategoryNo.cpo_sailor_c;
                case 33: return (int)CategoryNo.cpo_jacket_a;
                case 34: return (int)CategoryNo.cpo_jacket_b;
                case 35: return (int)CategoryNo.cpo_jacket_c;
                case 89: return (int)CategoryNo.mt_hairgloss;
                case 90: return (int)CategoryNo.bo_head;
            }

            if (selectType >= 36 && selectType <= 40)
                return selectType == 40 ? (int)CategoryNo.mt_emblem : (int)CategoryNo.mt_pattern;
            if (selectType >= 41 && selectType <= 88)
            {
                var group = (selectType - 41) / 6;
                var position = (selectType - 41) % 6;
                if (position == 0)
                    return Math.Min((int)CategoryNo.co_shoes, (int)CategoryNo.co_bot + group);
                return position == 5 ? (int)CategoryNo.mt_emblem : (int)CategoryNo.mt_pattern;
            }
            if (selectType >= 91 && selectType <= 99)
                return (int)CategoryNo.mt_emblem;
            return -1;
        }

        private static bool SynchronizeMakerSelectionList(CustomSelectListCtrl listController, IList entries,
            int category)
        {
            if (!Lists.InternalDataList.TryGetValue((CategoryNo)category, out var categoryData))
                return false;

            var content = MakerSelectListContentField.GetValue(listController) as GameObject;
            var template = MakerSelectListTemplateField.GetValue(listController) as GameObject;
            if (content == null || template == null)
                throw new InvalidOperationException("Maker selection list content or item template is unavailable.");

            // MakerOptimizations virtualizes the UI with a small reusable component pool. Its backing item
            // list may be updated in place, but pooled components must not be created or destroyed here.
            var virtualized = TryMarkMakerVirtualListDirty(listController);
            var changed = ReconcileMakerSelectionDuplicates(entries, content, template, category, virtualized);
            var desired = categoryData.Values
                .Where(info => MakerListInfoAppliesToCurrentCharacter(category, info))
                .ToDictionary(info => info.Id);
            var existing = entries.Cast<CustomSelectInfo>()
                .Where(info => info != null && info.category == category)
                .GroupBy(info => info.index)
                .ToDictionary(group => group.Key, group => group.First());
            var additions = desired.Values.Where(info => !existing.ContainsKey(info.Id)).ToList();

            // Create additions first. If an item cannot be created, discard only those new objects and keep
            // the old menu intact instead of clearing the page before a potentially failing rebuild.
            var created = new List<CustomSelectInfo>();
            try
            {
                foreach (var listInfo in additions)
                {
                    var selectInfo = CreateMakerSelectInfo(listInfo);
                    if (!virtualized)
                        CreateMakerSelectComponent(listController, content, template, selectInfo);
                    created.Add(selectInfo);
                }
            }
            catch
            {
                foreach (var selectInfo in created)
                {
                    if (!virtualized && selectInfo.sic != null)
                        Object.Destroy(selectInfo.sic.gameObject);
                }
                throw;
            }

            changed |= created.Count > 0;
            foreach (var pair in existing)
            {
                if (desired.TryGetValue(pair.Key, out var listInfo))
                {
                    changed |= UpdateMakerSelectInfo(pair.Value, listInfo);
                    continue;
                }

                entries.Remove(pair.Value);
                if (!virtualized && pair.Value.sic != null)
                    Object.Destroy(pair.Value.sic.gameObject);
                changed = true;
            }

            foreach (var selectInfo in created)
                entries.Add(selectInfo);

            if (!changed)
                return false;

            if (!virtualized)
            {
                listController.imgRaycast = entries.Cast<CustomSelectInfo>()
                    .Where(info => info != null && info.sic != null && info.sic.img != null)
                    .Select(info => info.sic.img)
                    .ToArray();
            }
            listController.UpdateCategory();
            listController.UpdateStateNew();
            if (Singleton<CustomBase>.IsInstance())
                Singleton<CustomBase>.Instance.updateCustomUI = true;
            return true;
        }

        private static bool TryMarkMakerVirtualListDirty(CustomSelectListCtrl listController)
        {
            try
            {
                var virtualListType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(
                        "IllusionFixes.MakerOptimizations+VirtualizeMakerLists", false))
                    .FirstOrDefault(type => type != null);
                if (virtualListType == null)
                    return false;

                var cacheField = virtualListType.GetField("_listCache",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var cache = cacheField?.GetValue(null) as IDictionary;
                if (cache == null || !cache.Contains(listController))
                    return false;

                var markDirty = virtualListType.GetMethod("MarkListDirty",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (markDirty == null)
                    return false;

                markDirty.Invoke(null, new object[] { listController });
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not notify MakerOptimizations about a hot-reloaded list: " +
                                  (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        private static bool ReconcileMakerSelectionDuplicates(IList entries, GameObject content,
            GameObject template, int category, bool virtualized)
        {
            var changed = false;
            if (!virtualized)
            {
                var visualGroups = content.GetComponentsInChildren<CustomSelectInfoComponent>(true)
                    .Where(component => component != null && component.gameObject != template &&
                                        component.info != null && component.info.category == category)
                    .GroupBy(component => component.info.index)
                    .ToArray();

                foreach (var group in visualGroups)
                {
                    var components = group.ToArray();
                    var keeper = components.FirstOrDefault(component =>
                                     component.tgl != null && component.tgl.isOn) ??
                                 components.FirstOrDefault(component => entries.Contains(component.info)) ??
                                 components[0];

                    var matchingEntries = entries.Cast<CustomSelectInfo>()
                        .Where(info => info != null && info.category == category && info.index == group.Key)
                        .ToArray();
                    foreach (var info in matchingEntries)
                    {
                        if (ReferenceEquals(info, keeper.info))
                            continue;

                        entries.Remove(info);
                        if (info.sic != null && info.sic != keeper && !components.Contains(info.sic))
                            Object.Destroy(info.sic.gameObject);
                        changed = true;
                    }

                    if (!entries.Contains(keeper.info))
                    {
                        entries.Add(keeper.info);
                        changed = true;
                    }
                    keeper.info.sic = keeper;

                    foreach (var component in components)
                    {
                        if (component == keeper)
                            continue;

                        if (component.info != null && ReferenceEquals(component.info.sic, component))
                            component.info.sic = null;
                        Object.Destroy(component.gameObject);
                        changed = true;
                    }
                }
            }

            // Also cover malformed backing-list duplicates which have no surviving component under the
            // current content object. They are invisible now but would otherwise reappear on the next rebuild.
            var entryGroups = entries.Cast<CustomSelectInfo>()
                .Where(info => info != null && info.category == category)
                .GroupBy(info => info.index)
                .Where(group => group.Count() > 1)
                .ToArray();
            foreach (var group in entryGroups)
            {
                var infos = group.ToArray();
                var keeper = infos.FirstOrDefault(info => info.sic != null && info.sic.tgl != null &&
                                                          info.sic.tgl.isOn) ?? infos[0];
                foreach (var info in infos)
                {
                    if (ReferenceEquals(info, keeper))
                        continue;

                    entries.Remove(info);
                    if (!virtualized && info.sic != null && info.sic != keeper.sic)
                        Object.Destroy(info.sic.gameObject);
                    changed = true;
                }
            }

            return changed;
        }

        private static bool MakerListInfoAppliesToCurrentCharacter(int category, ListInfoBase listInfo)
        {
            if (category != (int)CategoryNo.co_top && category != (int)CategoryNo.co_bra)
                return true;
            if (!Singleton<CustomBase>.IsInstance() || Singleton<CustomBase>.Instance.chaCtrl == null)
                return true;

            var itemSex = listInfo.GetInfoInt(ChaListDefine.KeyType.Sex);
            var characterSex = Singleton<CustomBase>.Instance.chaCtrl.fileParam.sex;
            return (characterSex != 0 || itemSex != 3) && (characterSex != 1 || itemSex != 2);
        }

        private static CustomSelectInfo CreateMakerSelectInfo(ListInfoBase listInfo)
        {
            return new CustomSelectInfo
            {
                category = listInfo.Category,
                index = listInfo.Id,
                name = listInfo.Name,
                assetBundle = listInfo.GetInfo(ChaListDefine.KeyType.ThumbAB),
                assetName = listInfo.GetInfo(ChaListDefine.KeyType.ThumbTex)
            };
        }

        private static void CreateMakerSelectComponent(CustomSelectListCtrl listController, GameObject content,
            GameObject template, CustomSelectInfo selectInfo)
        {
            GameObject itemObject = null;
            try
            {
                itemObject = Object.Instantiate(template);
                var component = itemObject.GetComponent<CustomSelectInfoComponent>();
                if (component == null)
                    throw new InvalidOperationException("Maker selection item template has no CustomSelectInfoComponent.");

                component.info = selectInfo;
                selectInfo.sic = component;
                component.img = itemObject.GetComponent<Image>();
                if (component.tgl != null)
                    component.tgl.group = content.GetComponent<ToggleGroup>();
                itemObject.transform.SetParent(content.transform, false);
                MakerSetToggleHandlerMethod.Invoke(listController, new object[] { itemObject });
                UpdateMakerSelectThumbnail(selectInfo);

                var newMarker = itemObject.transform.Find("New");
                component.objNew = newMarker == null ? null : newMarker.gameObject;
                if (component.objNew != null)
                {
                    var itemState = Singleton<Manager.Character>.Instance.chaListCtrl
                        .CheckItemID(selectInfo.category, selectInfo.index);
                    component.objNew.SetActive(itemState == 1);
                }
                component.Disvisible(selectInfo.disvisible);
                component.Disable(selectInfo.disable);
            }
            catch
            {
                if (itemObject != null)
                    Object.Destroy(itemObject);
                selectInfo.sic = null;
                throw;
            }
        }

        private static bool UpdateMakerSelectInfo(CustomSelectInfo selectInfo, ListInfoBase listInfo)
        {
            var name = listInfo.Name;
            var assetBundle = listInfo.GetInfo(ChaListDefine.KeyType.ThumbAB);
            var assetName = listInfo.GetInfo(ChaListDefine.KeyType.ThumbTex);
            var thumbnailChanged = selectInfo.assetBundle != assetBundle || selectInfo.assetName != assetName;
            var changed = selectInfo.name != name || thumbnailChanged;
            selectInfo.name = name;
            selectInfo.assetBundle = assetBundle;
            selectInfo.assetName = assetName;
            if (thumbnailChanged)
                UpdateMakerSelectThumbnail(selectInfo);
            return changed;
        }

        private static void UpdateMakerSelectThumbnail(CustomSelectInfo selectInfo)
        {
            if (selectInfo.sic == null || selectInfo.sic.img == null ||
                string.IsNullOrEmpty(selectInfo.assetBundle) || string.IsNullOrEmpty(selectInfo.assetName))
                return;

            try
            {
                var texture = CommonLib.LoadAsset<Texture2D>(selectInfo.assetBundle, selectInfo.assetName, false,
                    string.Empty);
                if (texture != null)
                {
                    selectInfo.sic.img.sprite = Sprite.Create(texture,
                        new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Failed to refresh Maker item thumbnail [" + selectInfo.assetBundle + "/" +
                                  selectInfo.assetName + "]: " + ex.Message);
            }
        }

        private bool RefreshCharacterAssets(ChaControl chaControl,
            IDictionary<CategoryNo, HashSet<int>> affectedSlots, ZipmodHotReloadResult result)
        {
            if (chaControl == null || chaControl.chaFile == null)
                return false;

            var refreshShadersAndDynamicBones = HotReloadRefreshShadersAndDynamicBones == null ||
                                                HotReloadRefreshShadersAndDynamicBones.Value;
            // In Maker, MaterialEditor treats ChangeClothes/ChangeHair/ChangeAccessory as a user
            // selection change and deletes the current slot's edits. Mark this operation as a load
            // so its in-memory shader/material state survives the prefab replacement. The GUI option
            // can disable this optional bridge for compatibility with older related plugins.
            var materialEditorController = refreshShadersAndDynamicBones
                ? BeginMaterialEditorAssetRefresh(chaControl)
                : null;
            var refreshed = false;
            var materialScope = CharacterMaterialRefreshScope.None;
            var refreshTargets = new CharacterAssetRefreshTargets();
            var face = chaControl.fileFace;
            var body = chaControl.fileBody;
            var hair = chaControl.fileHair;
            var coordinate = chaControl.nowCoordinate;
            var attachmentHierarchyChanged = false;
            var postRefreshScheduled = false;

            try
            {
                if (face != null && SlotIsAffected(affectedSlots, CategoryNo.bo_head, face.headId))
                {
                    chaControl.ChangeHead(true);
                    refreshTargets.Head = true;
                    refreshed = true;
                    attachmentHierarchyChanged = true;
                    materialScope |= CharacterMaterialRefreshScope.Body;
                }

            if (hair != null)
            {
                var hairGeometryChanged = RefreshHairPart(chaControl, hair, affectedSlots, refreshTargets,
                                              CategoryNo.bo_hair_b, (int)ChaFileDefine.HairKind.back) |
                                          RefreshHairPart(chaControl, hair, affectedSlots, refreshTargets,
                                              CategoryNo.bo_hair_f, (int)ChaFileDefine.HairKind.front) |
                                          RefreshHairPart(chaControl, hair, affectedSlots, refreshTargets,
                                              CategoryNo.bo_hair_s, (int)ChaFileDefine.HairKind.side) |
                                          RefreshHairPart(chaControl, hair, affectedSlots, refreshTargets,
                                              CategoryNo.bo_hair_o, (int)ChaFileDefine.HairKind.option);
                refreshed |= hairGeometryChanged;
                attachmentHierarchyChanged |= hairGeometryChanged;
                if (hairGeometryChanged)
                    materialScope |= CharacterMaterialRefreshScope.Hair;

                if (SlotIsAffected(affectedSlots, CategoryNo.mt_hairgloss, hair.glossId))
                {
                    chaControl.ChangeSettingHairGlossMaskAll();
                    refreshed = true;
                    materialScope |= CharacterMaterialRefreshScope.Hair;
                }
            }

            var clothesChanged = coordinate != null && RefreshClothes(chaControl, coordinate.clothes,
                affectedSlots, refreshTargets);
            refreshed |= clothesChanged;
            attachmentHierarchyChanged |= clothesChanged;
            if (clothesChanged)
                materialScope |= CharacterMaterialRefreshScope.Clothes;

            if (coordinate != null)
            {
                var accessoriesChanged = RefreshAccessories(chaControl, coordinate.accessory, affectedSlots,
                    refreshTargets);
                refreshed |= accessoriesChanged;
                if (accessoriesChanged)
                    materialScope |= CharacterMaterialRefreshScope.Accessories;

                // Replacing a garment can replace attachment bones. Reconnect existing accessories without
                // recreating their assets unless their own resolved slot was affected as well.
                if (attachmentHierarchyChanged && coordinate.accessory != null)
                {
                    for (var slot = 0; slot < coordinate.accessory.parts.Length; slot++)
                    {
                        var accessory = coordinate.accessory.parts[slot];
                        if (accessory != null && slot < chaControl.objAccessory.Length &&
                            chaControl.objAccessory[slot] != null)
                            chaControl.ChangeAccessoryParent(slot, accessory.parentKey);
                    }
                }
            }

            var faceTextureChanged = face != null &&
                                     UsesAffectedSlotExcept(StructReference.ChaFileFaceProperties, face,
                                         affectedSlots, CategoryNo.bo_head);
            faceTextureChanged |= face != null && face.baseMakeup != null &&
                                  UsesAffectedSlot(StructReference.ChaFileMakeupProperties, face.baseMakeup,
                                      affectedSlots);
            if (faceTextureChanged)
            {
                chaControl.CreateFaceTexture();
                chaControl.ChangeCustomFaceWithoutCustomTexture();
                refreshed = true;
                materialScope |= CharacterMaterialRefreshScope.Body;
            }

            if (body != null && UsesAffectedSlot(StructReference.ChaFileBodyProperties, body, affectedSlots))
            {
                chaControl.CreateBodyTexture();
                chaControl.ChangeCustomBodyWithoutCustomTexture();
                refreshed = true;
                materialScope |= CharacterMaterialRefreshScope.Body;
            }

                if (refreshed)
                    RefreshStudioCharacterRuntimeReferences(chaControl, refreshTargets);

                if (refreshed && refreshShadersAndDynamicBones)
                {
                    _hotReloadPendingAssetRefreshes++;
                    try
                    {
                        StartCoroutine(ReapplyCharacterRenderAndPhysicsState(chaControl, materialEditorController,
                            materialScope, refreshTargets, result));
                        postRefreshScheduled = true;
                    }
                    catch
                    {
                        _hotReloadPendingAssetRefreshes--;
                        throw;
                    }
                }

                return refreshed;
            }
            finally
            {
                // BeginMaterialEditorAssetRefresh marks a controller as loading. Always release that
                // state when this character did not need a delayed post-refresh pass or when a change failed.
                if (!postRefreshScheduled)
                    EndMaterialEditorAssetRefresh(materialEditorController);
            }
        }

        private static object BeginMaterialEditorAssetRefresh(ChaControl chaControl)
        {
            try
            {
                var materialEditorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(x => x.GetType("KK_Plugins.MaterialEditor.MaterialEditorPlugin", false) != null);
                var pluginType = materialEditorAssembly?.GetType(
                    "KK_Plugins.MaterialEditor.MaterialEditorPlugin", false);
                var controller = pluginType?.GetMethod("GetCharaController",
                    BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { chaControl });
                if (controller == null)
                    return null;

                var controllerType = controller.GetType();
                var coordinateChanging = controllerType.GetProperty("CoordinateChanging",
                    BindingFlags.Public | BindingFlags.Instance);
                var characterLoading = controllerType.GetProperty("CharacterLoading",
                    BindingFlags.Public | BindingFlags.Instance);
                if (coordinateChanging == null || characterLoading == null)
                    throw new MissingMemberException(controllerType.FullName,
                        "CoordinateChanging / CharacterLoading");

                coordinateChanging.SetValue(controller, true, null);
                characterLoading.SetValue(controller, true, null);
                return controller;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not enter MaterialEditor-safe asset refresh mode; the character was left unchanged.",
                    ex.InnerException ?? ex);
            }
        }

        private static void EndMaterialEditorAssetRefresh(object controller)
        {
            if (controller == null)
                return;

            try
            {
                var controllerType = controller.GetType();
                controllerType.GetProperty("CoordinateChanging", BindingFlags.Public | BindingFlags.Instance)
                    ?.SetValue(controller, false, null);
                controllerType.GetProperty("CharacterLoading", BindingFlags.Public | BindingFlags.Instance)
                    ?.SetValue(controller, false, null);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not leave MaterialEditor-safe asset refresh mode: " +
                                  (ex.InnerException ?? ex).Message);
            }
        }

        private IEnumerator ReapplyCharacterRenderAndPhysicsState(ChaControl chaControl,
            object materialEditorController, CharacterMaterialRefreshScope scope,
            CharacterAssetRefreshTargets refreshTargets,
            ZipmodHotReloadResult result)
        {
            try
            {
                // Wait until all vanilla change hooks for this frame have finished before restoring edits.
                yield return null;

                IEnumerator loadData = null;
                if (materialEditorController != null)
                {
                    try
                    {
                        var loadDataMethod = materialEditorController.GetType().GetMethods(
                                BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(x => x.Name == "LoadData" && x.GetParameters().Length == 4);
                        if (loadDataMethod == null)
                            throw new MissingMethodException(materialEditorController.GetType().FullName,
                                "LoadData(bool, bool, bool, bool)");
                        loadData = loadDataMethod.Invoke(materialEditorController, new object[]
                        {
                            (scope & CharacterMaterialRefreshScope.Clothes) != 0,
                            (scope & CharacterMaterialRefreshScope.Accessories) != 0,
                            (scope & CharacterMaterialRefreshScope.Hair) != 0,
                            (scope & CharacterMaterialRefreshScope.Body) != 0
                        }) as IEnumerator;
                    }
                    catch (Exception ex)
                    {
                        result.AssetRefreshFailureCount++;
                        Logger.LogWarning("Could not start MaterialEditor state reapply after asset refresh: " +
                                          (ex.InnerException ?? ex).Message);
                    }
                }

                if (loadData != null)
                {
                    while (true)
                    {
                        bool hasNext;
                        object current = null;
                        try
                        {
                            hasNext = loadData.MoveNext();
                            if (hasNext)
                                current = loadData.Current;
                        }
                        catch (Exception ex)
                        {
                            result.AssetRefreshFailureCount++;
                            Logger.LogWarning("MaterialEditor state reapply failed after asset refresh: " +
                                              (ex.InnerException ?? ex).Message);
                            break;
                        }

                        if (!hasNext)
                            break;
                        yield return current;
                    }
                }

                TryUpdateShaderSwapper(chaControl, result);
                if (refreshTargets.Accessories.Count > 0)
                    TryReapplyDynamicBoneEditor(chaControl, result);

                // DynamicBone.Start runs on the first active frame of the replacement prefab. Wait once
                // more, restore its captured local pose through OnDisable, then seed particles from it.
                yield return null;
                ReinitializeDynamicBones(chaControl, refreshTargets, result);
            }
            finally
            {
                EndMaterialEditorAssetRefresh(materialEditorController);
                if (_hotReloadPendingAssetRefreshes > 0)
                    _hotReloadPendingAssetRefreshes--;
            }
        }

        private static void TryUpdateShaderSwapper(ChaControl chaControl, ZipmodHotReloadResult result)
        {
            try
            {
                var shaderSwapperAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(x => x.GetType("KK_Plugins.ShaderSwapper", false) != null);
                var shaderSwapperType = shaderSwapperAssembly?.GetType("KK_Plugins.ShaderSwapper", false);
                if (shaderSwapperType == null)
                    return;

                var instance = shaderSwapperType.GetField("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                var updateMethod = shaderSwapperType.GetMethod("UpdateCharShaders",
                    BindingFlags.Public | BindingFlags.Instance);
                if (instance != null && updateMethod != null)
                    updateMethod.Invoke(instance, new object[] { chaControl, false });
            }
            catch (Exception ex)
            {
                result.AssetRefreshFailureCount++;
                Logger.LogWarning("ShaderSwapper could not restore shaders after asset refresh: " +
                                  (ex.InnerException ?? ex).Message);
            }
        }

        private static void RefreshStudioCharacterRuntimeReferences(ChaControl chaControl,
            CharacterAssetRefreshTargets refreshTargets)
        {
            if (!Singleton<global::Studio.Studio>.IsInstance())
                return;

            var studioCharacter = Singleton<global::Studio.Studio>.Instance.dicObjectCtrl.Values
                .OfType<OCIChar>()
                .FirstOrDefault(x => x.charInfo == chaControl);
            if (studioCharacter == null)
                return;

            // Studio caches the hair/skirt DynamicBone arrays when a character is first created.
            // ChangeHair/ChangeClothes replaces those components, so refresh only these references;
            // rebuilding OCIChar would reset animation, FK/IK, clothing state, and scene controls.
            if (refreshTargets.Hair.Count > 0)
                studioCharacter.hairDynamic = AddObjectFemale.GetHairDynamic(chaControl.objHair);

            if (refreshTargets.Clothes.Count > 0)
            {
                studioCharacter.skirtDynamic = chaControl.chaFile.parameter.sex == 1
                    ? AddObjectFemale.GetSkirtDynamic(chaControl.objClothes)
                    : null;
            }
        }

        private static void TryReapplyDynamicBoneEditor(ChaControl chaControl, ZipmodHotReloadResult result)
        {
            try
            {
                var editorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(x => x.GetType("KK_Plugins.DynamicBoneEditor.Plugin", false) != null);
                var pluginType = editorAssembly?.GetType("KK_Plugins.DynamicBoneEditor.Plugin", false);
                if (pluginType == null)
                    return;

                var getController = pluginType.GetMethod("GetCharaController",
                    BindingFlags.Public | BindingFlags.Static);
                var controller = getController?.Invoke(null, new object[] { chaControl });
                if (controller == null)
                    return;

                var applyCurrentData = controller.GetType().GetMethod("CoordinateChangeEvent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (applyCurrentData == null)
                    throw new MissingMethodException(controller.GetType().FullName, "CoordinateChangeEvent");
                applyCurrentData.Invoke(controller, null);
            }
            catch (Exception ex)
            {
                result.AssetRefreshFailureCount++;
                Logger.LogWarning("DynamicBoneEditor could not restore its current settings after asset refresh: " +
                                  (ex.InnerException ?? ex).Message);
            }
        }

        private static void ReinitializeDynamicBones(ChaControl chaControl,
            CharacterAssetRefreshTargets refreshTargets, ZipmodHotReloadResult result)
        {
            try
            {
                var roots = new HashSet<GameObject>();
                if (refreshTargets.Head && chaControl.objHead != null)
                    roots.Add(chaControl.objHead);

                foreach (var part in refreshTargets.Hair)
                {
                    if (part >= 0 && part < chaControl.objHair.Length && chaControl.objHair[part] != null)
                        roots.Add(chaControl.objHair[part]);
                }

                foreach (var kind in refreshTargets.Clothes)
                {
                    if (kind >= 0 && kind < chaControl.objClothes.Length && chaControl.objClothes[kind] != null)
                        roots.Add(chaControl.objClothes[kind]);
                }

                foreach (var slot in refreshTargets.Accessories)
                {
                    if (slot >= 0 && slot < chaControl.objAccessory.Length && chaControl.objAccessory[slot] != null)
                        roots.Add(chaControl.objAccessory[slot]);
                }

                var dynamicBones = new HashSet<DynamicBone>(roots.SelectMany(x =>
                    x.GetComponentsInChildren<DynamicBone>(true)));
                foreach (var dynamicBone in dynamicBones)
                {
                    if (dynamicBone == null)
                        continue;

                    // OnDisable restores the local pose captured by SetupParticles; OnEnable seeds
                    // the particle positions from that pose. Keep originally disabled components off.
                    var wasEnabled = dynamicBone.enabled;
                    if (wasEnabled)
                    {
                        dynamicBone.enabled = false;
                        dynamicBone.enabled = true;
                    }
                    else
                        dynamicBone.ResetParticlesPosition();
                }

                var dynamicBonesV2 = new HashSet<DynamicBone_Ver02>(roots.SelectMany(x =>
                    x.GetComponentsInChildren<DynamicBone_Ver02>(true)));
                foreach (var dynamicBone in dynamicBonesV2)
                {
                    if (dynamicBone == null)
                        continue;

                    var wasEnabled = dynamicBone.enabled;
                    if (wasEnabled)
                    {
                        dynamicBone.enabled = false;
                        dynamicBone.enabled = true;
                    }
                    else
                        dynamicBone.ResetParticlesPosition();
                }
            }
            catch (Exception ex)
            {
                result.AssetRefreshFailureCount++;
                Logger.LogWarning("Dynamic bones could not be reinitialized after asset refresh: " + ex);
            }
        }

        private static bool RefreshHairPart(ChaControl chaControl, ChaFileHair hair,
            IDictionary<CategoryNo, HashSet<int>> affectedSlots, CharacterAssetRefreshTargets refreshTargets,
            CategoryNo category, int part)
        {
            if (part < 0 || part >= hair.parts.Length || !SlotIsAffected(affectedSlots, category, hair.parts[part].id))
                return false;

            chaControl.ChangeHair(part, hair.parts[part].id, true);
            refreshTargets.Hair.Add(part);
            return true;
        }

        private static bool RefreshClothes(ChaControl chaControl, ChaFileClothes clothes,
            IDictionary<CategoryNo, HashSet<int>> affectedSlots, CharacterAssetRefreshTargets refreshTargets)
        {
            if (clothes == null)
                return false;

            var refreshed = false;
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_top,
                (int)ChaFileDefine.ClothesKind.top);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_bot,
                (int)ChaFileDefine.ClothesKind.bot);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_bra,
                (int)ChaFileDefine.ClothesKind.bra);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_shorts,
                (int)ChaFileDefine.ClothesKind.shorts);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_gloves,
                (int)ChaFileDefine.ClothesKind.gloves);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_panst,
                (int)ChaFileDefine.ClothesKind.panst);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_socks,
                (int)ChaFileDefine.ClothesKind.socks);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_shoes,
                (int)ChaFileDefine.ClothesKind.shoes_inner);
            refreshed |= RefreshClothesPart(chaControl, clothes, affectedSlots, refreshTargets, CategoryNo.co_shoes,
                (int)ChaFileDefine.ClothesKind.shoes_outer);

            if (UsesAffectedSlotForCategories(StructReference.ChaFileClothesProperties, clothes,
                    affectedSlots, CategoryNo.cpo_jacket_a, CategoryNo.cpo_jacket_b,
                    CategoryNo.cpo_jacket_c, CategoryNo.cpo_sailor_a,
                    CategoryNo.cpo_sailor_b, CategoryNo.cpo_sailor_c))
            {
                RefreshClothesPartUnconditionally(chaControl, clothes, refreshTargets,
                    (int)ChaFileDefine.ClothesKind.top);
                refreshed = true;
            }

            if (affectedSlots.TryGetValue(CategoryNo.mt_emblem, out var emblemSlots))
            {
                for (var kind = 0; kind < clothes.parts.Length; kind++)
                {
                    if (emblemSlots.Contains(clothes.parts[kind].emblemeId))
                    {
                        chaControl.ChangeCustomEmblem(kind, 0);
                        refreshed = true;
                    }
                    if (emblemSlots.Contains(clothes.parts[kind].emblemeId2))
                    {
                        chaControl.ChangeCustomEmblem(kind, 1);
                        refreshed = true;
                    }
                }
            }

            return refreshed;
        }

        private static bool RefreshClothesPart(ChaControl chaControl, ChaFileClothes clothes,
            IDictionary<CategoryNo, HashSet<int>> affectedSlots, CharacterAssetRefreshTargets refreshTargets,
            CategoryNo category, int kind)
        {
            if (kind < 0 || kind >= clothes.parts.Length ||
                !SlotIsAffected(affectedSlots, category, clothes.parts[kind].id))
                return false;

            RefreshClothesPartUnconditionally(chaControl, clothes, refreshTargets, kind);
            return true;
        }

        private static void RefreshClothesPartUnconditionally(ChaControl chaControl, ChaFileClothes clothes,
            CharacterAssetRefreshTargets refreshTargets, int kind)
        {
            var subParts = clothes.subPartsId;
            chaControl.ChangeClothes(kind, clothes.parts[kind].id,
                subParts.Length > 0 ? subParts[0] : 0,
                subParts.Length > 1 ? subParts[1] : 0,
                subParts.Length > 2 ? subParts[2] : 0, true);
            refreshTargets.Clothes.Add(kind);
        }

        private static bool RefreshAccessories(ChaControl chaControl, ChaFileAccessory accessories,
            IDictionary<CategoryNo, HashSet<int>> affectedSlots, CharacterAssetRefreshTargets refreshTargets)
        {
            if (accessories == null)
                return false;

            var refreshed = false;
            for (var slot = 0; slot < accessories.parts.Length; slot++)
            {
                var accessory = accessories.parts[slot];
                if (accessory == null || !Enum.IsDefined(typeof(CategoryNo), accessory.type) ||
                    !SlotIsAffected(affectedSlots, (CategoryNo)accessory.type, accessory.id))
                    continue;

                chaControl.ChangeAccessory(slot, accessory.type, accessory.id, accessory.parentKey, true);
                refreshTargets.Accessories.Add(slot);
                refreshed = true;
            }
            return refreshed;
        }

        private static bool SlotIsAffected(IDictionary<CategoryNo, HashSet<int>> affectedSlots,
            CategoryNo category, int slot)
        {
            return affectedSlots.TryGetValue(category, out var slots) && slots.Contains(slot);
        }

        private static bool UsesAffectedSlot(Dictionary<CategoryProperty, StructValue<int>> references,
            object structure, IDictionary<CategoryNo, HashSet<int>> affectedSlots, params CategoryNo[] onlyCategories)
        {
            var categoryFilter = onlyCategories != null && onlyCategories.Length > 0
                ? new HashSet<CategoryNo>(onlyCategories)
                : null;

            return references.Any(reference =>
                (categoryFilter == null || categoryFilter.Contains(reference.Key.Category)) &&
                SlotIsAffected(affectedSlots, reference.Key.Category, reference.Value.GetMethod(structure)));
        }

        private static bool UsesAffectedSlotExcept(Dictionary<CategoryProperty, StructValue<int>> references,
            object structure, IDictionary<CategoryNo, HashSet<int>> affectedSlots, params CategoryNo[] excludedCategories)
        {
            var exclusions = new HashSet<CategoryNo>(excludedCategories ?? new CategoryNo[0]);
            return references.Any(reference => !exclusions.Contains(reference.Key.Category) &&
                                               SlotIsAffected(affectedSlots, reference.Key.Category,
                                                   reference.Value.GetMethod(structure)));
        }

        private static bool UsesAffectedSlotForCategories(Dictionary<CategoryProperty, StructValue<int>> references,
            object structure, IDictionary<CategoryNo, HashSet<int>> affectedSlots, params CategoryNo[] categories)
        {
            return UsesAffectedSlot(references, structure, affectedSlots, categories);
        }

        private static void RefreshStudioItemAssets(ZipmodHotReloadResult result)
        {
            if (result.AffectedStudioItemSlots.Count == 0 || !Singleton<global::Studio.Studio>.IsInstance())
                return;

            var studio = Singleton<global::Studio.Studio>.Instance;
            var affectedItems = studio.dicObjectCtrl.Values.OfType<OCIItem>()
                .Where(x => x.itemInfo != null && result.AffectedStudioItemSlots.Contains(x.itemInfo.no))
                .ToArray();
            var refreshedItems = new List<OCIItem>();

            foreach (var item in affectedItems)
            {
                try
                {
                    if (RefreshStudioItemAsset(studio, item))
                    {
                        result.RefreshedStudioItemCount++;
                        refreshedItems.Add(item);
                    }
                    else
                        result.AssetRefreshFailureCount++;
                }
                catch (Exception ex)
                {
                    result.AssetRefreshFailureCount++;
                    Logger.LogWarning("Failed to refresh hot-reloaded Studio item [" + item.itemInfo.dicKey +
                                      ", slot " + item.itemInfo.no + "]: " + ex);
                }
            }

            if (refreshedItems.Count > 0 &&
                (HotReloadRefreshShadersAndDynamicBones == null || HotReloadRefreshShadersAndDynamicBones.Value) &&
                !TryReapplyMaterialEditorStudioState(refreshedItems))
                result.AssetRefreshFailureCount++;
        }

        private static bool RefreshStudioItemAsset(global::Studio.Studio studio, OCIItem item)
        {
            if (StudioGuideDictionaryField == null || StudioRootNodesField == null ||
                item.objectItem == null || item.guideObject == null || item.treeNodeObject == null)
                return false;

            var info = item.itemInfo;
            var oldRoot = item.objectItem;
            var oldRootParent = oldRoot.transform.parent;
            var oldGuide = item.guideObject;
            var oldTreeNode = item.treeNodeObject;
            var oldParentInfo = item.parentInfo;
            var guideManager = Singleton<GuideObjectManager>.Instance;
            var oldGuideWasSelected = guideManager.selectObjects.Contains(oldGuide);

            OCIItem replacement = null;
            try
            {
                replacement = AddObjectItem.Load(info, null, null, false, -1);
                if (replacement == null || replacement.objectItem == null)
                    return false;

                RemoveTemporaryStudioTreeNode(studio, replacement.treeNodeObject);
                if (replacement.guideObject != null)
                    // Keep the newly created FK bone guides alive. They are adopted by oldGuide below.
                    guideManager.Delete(replacement.guideObject, false);

                var replacementRoot = replacement.objectItem;
                replacementRoot.transform.SetParent(oldRootParent, false);

                var guideDictionary = (Dictionary<Transform, GuideObject>)StudioGuideDictionaryField.GetValue(guideManager);
                guideManager.Delete(oldGuide, false);
                oldGuide.transformTarget = replacementRoot.transform;
                guideDictionary[replacementRoot.transform] = oldGuide;

                if (replacement.listBones != null)
                {
                    foreach (var bone in replacement.listBones)
                    {
                        if (bone != null && bone.guideObject != null)
                            bone.guideObject.parentGuide = oldGuide;
                    }
                }

                if (item.listBones != null)
                {
                    foreach (var bone in item.listBones)
                    {
                        if (bone != null && bone.guideObject != null)
                            guideManager.Delete(bone.guideObject, true);
                    }
                }

                CopyDeclaredInstanceFields(typeof(OCIItem), replacement, item);
                item.objectInfo = info;
                item.guideObject = oldGuide;
                item.treeNodeObject = oldTreeNode;
                item.parentInfo = oldParentInfo;

                foreach (var childInfo in info.child.ToArray())
                {
                    var child = global::Studio.Studio.GetCtrlInfo(childInfo.dicKey);
                    if (child == null || child.guideObject == null)
                        continue;

                    child.guideObject.transformTarget.SetParent(item.childRoot, false);
                    child.guideObject.parent = item.childRoot;
                    child.parentInfo = item;
                }

                studio.dicObjectCtrl[info.dicKey] = item;
                studio.dicInfo[oldTreeNode] = item;
                info.changeAmount.OnChange();
                oldGuide.ForceUpdate();
                if (oldGuideWasSelected)
                    guideManager.AddSelectMultiple(oldGuide);

                Object.Destroy(oldRoot);
                return true;
            }
            catch
            {
                if (replacement != null)
                    CleanupFailedStudioReplacement(studio, replacement, item);
                throw;
            }
        }

        private static void RemoveTemporaryStudioTreeNode(global::Studio.Studio studio, TreeNodeObject treeNode)
        {
            if (treeNode == null)
                return;

            studio.dicInfo.Remove(treeNode);
            var rootNodes = StudioRootNodesField.GetValue(studio.treeNodeCtrl) as IList;
            rootNodes?.Remove(treeNode);
            Object.Destroy(treeNode.gameObject);
        }

        private static void CleanupFailedStudioReplacement(global::Studio.Studio studio, OCIItem replacement,
            OCIItem original)
        {
            try
            {
                RemoveTemporaryStudioTreeNode(studio, replacement.treeNodeObject);
                if (Singleton<GuideObjectManager>.IsInstance())
                {
                    var guideManager = Singleton<GuideObjectManager>.Instance;
                    if (replacement.listBones != null)
                    {
                        foreach (var bone in replacement.listBones)
                        {
                            if (bone != null && bone.guideObject != null)
                                guideManager.Delete(bone.guideObject, true);
                        }
                    }
                    if (replacement.guideObject != null)
                        guideManager.Delete(replacement.guideObject, true);
                }
                if (replacement.objectItem != null && replacement.objectItem != original.objectItem)
                    Object.Destroy(replacement.objectItem);
                studio.dicObjectCtrl[original.itemInfo.dicKey] = original;
                studio.dicInfo[original.treeNodeObject] = original;
            }
            catch (Exception cleanupException)
            {
                Logger.LogWarning("Studio item hot-reload cleanup failed: " + cleanupException.Message);
            }
        }

        private static void CopyDeclaredInstanceFields(Type type, object source, object destination)
        {
            foreach (var field in type.GetFields(HotReloadInstanceFields))
            {
                if (!field.IsStatic)
                    field.SetValue(destination, field.GetValue(source));
            }
        }

        /// <summary>
        /// MaterialEditor stores Studio edits by scene dictionary key. The key and OCIItem stay unchanged
        /// during the prefab swap, but the new renderers still need those in-memory edits applied once.
        /// This optional reflection bridge avoids a hard KKAPI/MaterialEditor dependency.
        /// </summary>
        private static bool TryReapplyMaterialEditorStudioState(ICollection<OCIItem> refreshedItems)
        {
            var materialEditorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(x => x.GetType("KK_Plugins.MaterialEditor.MEStudio", false) != null);
            if (materialEditorAssembly == null)
                return true;

            var meStudioType = materialEditorAssembly.GetType("KK_Plugins.MaterialEditor.MEStudio", false);
            var sceneControllerType = materialEditorAssembly.GetType("KK_Plugins.MaterialEditor.SceneController", false);
            if (meStudioType == null || sceneControllerType == null)
                return true;

            var getController = meStudioType.GetMethod("GetSceneController",
                BindingFlags.Public | BindingFlags.Static);
            var controller = getController?.Invoke(null, null);
            if (controller == null)
                return true;

            var onSceneSave = sceneControllerType.GetMethod("OnSceneSave",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var onSceneLoad = sceneControllerType.GetMethod("OnSceneLoad",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (onSceneSave == null || onSceneLoad == null || onSceneLoad.GetParameters().Length != 2)
            {
                Logger.LogWarning("MaterialEditor Studio API is not compatible with selective asset refresh.");
                return false;
            }

            var propertyLists = sceneControllerType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(x => typeof(IList).IsAssignableFrom(x.FieldType) && x.Name.EndsWith("List"))
                .ToArray();
            var listBackups = new Dictionary<FieldInfo, object[]>();
            var affectedKeys = new HashSet<int>(refreshedItems.Select(x => x.itemInfo.dicKey));

            try
            {
                // Serialize the current in-memory state first; no scene/card file is written.
                onSceneSave.Invoke(controller, null);

                foreach (var field in propertyLists)
                {
                    var list = field.GetValue(controller) as IList;
                    if (list == null)
                        continue;

                    listBackups[field] = list.Cast<object>().ToArray();
                    for (var i = list.Count - 1; i >= 0; i--)
                    {
                        var entry = list[i];
                        var idField = entry?.GetType().GetField("ID",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        // Feed OnSceneLoad only the records belonging to the refreshed items. Keeping
                        // unrelated records here can make MaterialEditor apply scene-wide state twice.
                        if (idField == null || idField.FieldType != typeof(int) ||
                            !affectedKeys.Contains((int)idField.GetValue(entry)))
                            list.RemoveAt(i);
                    }
                }

                var loadedItems = refreshedItems.ToDictionary(x => x.itemInfo.dicKey,
                    x => (ObjectCtrlInfo)x);
                var parameters = onSceneLoad.GetParameters();
                var operationType = parameters[0].ParameterType;
                var readOnlyDictionaryType = parameters[1].ParameterType;
                var readOnlyDictionary = Activator.CreateInstance(readOnlyDictionaryType, loadedItems);
                var importOperation = Enum.ToObject(operationType, 1);
                onSceneLoad.Invoke(controller, new[] { importOperation, readOnlyDictionary });
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Studio assets were refreshed, but MaterialEditor state could not be reapplied: " +
                                  (ex.InnerException ?? ex).Message);
                return false;
            }
            finally
            {
                // The filtered lists are only an input view for OnSceneLoad. Restore the controller's
                // complete in-memory scene data on both success and failure.
                foreach (var backup in listBackups)
                {
                    var list = backup.Key.GetValue(controller) as IList;
                    if (list == null)
                        continue;
                    list.Clear();
                    foreach (var entry in backup.Value)
                        list.Add(entry);
                }
            }
        }
    }
}
