using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step 3: per avatar, which skinned meshes and which humanoid rig the copied prefab takes from the
    /// edited FBX (matched by path, object name or mesh name; bones remapped by <see cref="BoneMapper"/>).
    /// </summary>
    public partial class AvatarSetupWizard
    {
        private class RendererInfo
        {
            public SkinnedMeshRenderer Renderer;
            public string ObjectName;
            public string RelativePath;
            public string MeshName;
        }

        /// <summary>
        /// Result of matching an FBX renderer's bones against a prefab renderer. When
        /// <see cref="BonesDiffer"/> is false the prefab's bones can be kept as they are.
        /// </summary>
        private class BoneRemap
        {
            public bool BonesDiffer;
            public Transform[] Bones;
            public Transform RootBone;
            public Bounds LocalBounds;
            public string Issue;
            public bool CanRemap => BonesDiffer && string.IsNullOrEmpty(Issue);
        }

        /// <summary>
        /// Maps an FBX renderer's bones onto the transforms of a prefab hierarchy so a mesh whose
        /// bones were added, removed or reordered still skins correctly after the swap. A bone is
        /// matched by its path below the root first, then by a name that is unique within the
        /// prefab renderer's armature (or, failing that, within the whole prefab).
        /// </summary>
        private class BoneMapper
        {
            private const int MaxListedBones = 5;

            private readonly Dictionary<string, Transform> transformsByPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
            private readonly Dictionary<string, List<Transform>> transformsByName = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);

            public BoneMapper(GameObject prefabRoot)
            {
                foreach (Transform transform in prefabRoot.GetComponentsInChildren<Transform>(true))
                {
                    string path = GetRelativeTransformPath(transform);
                    if (!transformsByPath.ContainsKey(path))
                    {
                        transformsByPath.Add(path, transform);
                    }

                    if (!transformsByName.TryGetValue(transform.name, out List<Transform> sameName))
                    {
                        sameName = new List<Transform>();
                        transformsByName.Add(transform.name, sameName);
                    }

                    sameName.Add(transform);
                }
            }

            public BoneRemap Resolve(SkinnedMeshRenderer fbxRenderer, SkinnedMeshRenderer prefabRenderer)
            {
                Transform[] fbxBones = fbxRenderer.bones;
                Transform[] prefabBones = prefabRenderer.bones;
                int bindposeCount = fbxRenderer.sharedMesh != null ? fbxRenderer.sharedMesh.bindposes.Length : fbxBones.Length;

                var result = new BoneRemap
                {
                    BonesDiffer = prefabBones.Length != bindposeCount || !HaveSameBoneNames(fbxBones, prefabBones)
                };

                if (!result.BonesDiffer)
                {
                    return result;
                }

                if (fbxBones.Length != bindposeCount)
                {
                    result.Issue = $"the FBX renderer has {fbxBones.Length} bones but its mesh has {bindposeCount} bind poses";
                    return result;
                }

                Transform armatureScope = GetArmatureScope(prefabRenderer);
                var unresolvedNames = new List<string>();
                var bones = new Transform[fbxBones.Length];

                for (int i = 0; i < fbxBones.Length; i++)
                {
                    if (fbxBones[i] == null)
                    {
                        continue;
                    }

                    bones[i] = MapTransform(fbxBones[i], armatureScope);
                    if (bones[i] == null)
                    {
                        unresolvedNames.Add(fbxBones[i].name);
                    }
                }

                Transform rootBone = prefabRenderer.rootBone;
                if (fbxRenderer.rootBone != null)
                {
                    rootBone = MapTransform(fbxRenderer.rootBone, armatureScope);
                    if (rootBone == null)
                    {
                        unresolvedNames.Add(fbxRenderer.rootBone.name + " (root bone)");
                    }
                }

                if (unresolvedNames.Count > 0)
                {
                    List<string> distinctNames = unresolvedNames.Distinct().ToList();
                    string listed = string.Join(", ", distinctNames.Take(MaxListedBones));
                    int remaining = distinctNames.Count - MaxListedBones;
                    result.Issue = $"{distinctNames.Count} bone(s) are missing or ambiguous on the prefab: {listed}{(remaining > 0 ? $" and {remaining} more" : string.Empty)}";
                    return result;
                }

                result.Bones = bones;
                result.RootBone = rootBone;
                result.LocalBounds = fbxRenderer.localBounds;
                return result;
            }

            private Transform MapTransform(Transform fbxTransform, Transform armatureScope)
            {
                if (transformsByPath.TryGetValue(GetRelativeTransformPath(fbxTransform), out Transform byPath) &&
                    string.Equals(byPath.name, fbxTransform.name, StringComparison.Ordinal))
                {
                    return byPath;
                }

                if (!transformsByName.TryGetValue(fbxTransform.name, out List<Transform> candidates))
                {
                    return null;
                }

                if (armatureScope != null)
                {
                    List<Transform> inScope = candidates.Where(candidate => candidate.IsChildOf(armatureScope)).ToList();
                    if (inScope.Count > 0)
                    {
                        return inScope.Count == 1 ? inScope[0] : null;
                    }
                }

                return candidates.Count == 1 ? candidates[0] : null;
            }

            /// <summary>
            /// Returns the top-level child of the prefab root that holds the prefab renderer's
            /// current skeleton (its armature), so outfit armatures with the same bone names are
            /// not picked by mistake. Returns null when the renderer has no bones.
            /// </summary>
            private static Transform GetArmatureScope(SkinnedMeshRenderer prefabRenderer)
            {
                Transform anchor = prefabRenderer.rootBone != null
                    ? prefabRenderer.rootBone
                    : prefabRenderer.bones.FirstOrDefault(bone => bone != null);

                if (anchor == null || anchor.parent == null)
                {
                    return null;
                }

                while (anchor.parent.parent != null)
                {
                    anchor = anchor.parent;
                }

                return anchor;
            }

            private static bool HaveSameBoneNames(Transform[] first, Transform[] second)
            {
                if (first.Length != second.Length)
                {
                    return false;
                }

                for (int i = 0; i < first.Length; i++)
                {
                    string firstName = first[i] != null ? first[i].name : null;
                    string secondName = second[i] != null ? second[i].name : null;
                    if (!string.Equals(firstName, secondName, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private class AnimatorInfo
        {
            public Animator Animator;
            public string ObjectName;
            public string RelativePath;
            public Avatar Avatar;
            public int PriorityScore;
        }

        // =====================================================================
        // Drawing
        // =====================================================================

        /// <summary>Replacements: the "Files" foldout under the avatar's rows is open.</summary>
        private bool showReplacementFiles;

        private void DrawMeshSelectionStep()
        {
            AvatarEntry entry = GetSelectedEntry();
            if (entry == null)
            {
                return;
            }

            int reviewedCount = avatarEntries.Count(item => item.isMeshReviewComplete);
            if (DrawAvatarList("Avatars", $"{reviewedCount}/{avatarEntries.Count} reviewed", GetReviewBadge))
            {
                GUIUtility.ExitGUI();
            }

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(GetEntryDisplayName(entry), PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();
                    var badge = GetReviewBadge(entry);
                    PawlygonEditorUI.DrawBadge(badge.Text, badge.Kind, badge.Tooltip);
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Tick what the copied prefab should take from the edited FBX. Rows that need a look are open; the rest are fine as they are.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                if (entry.isMeshReviewComplete)
                {
                    EditorGUILayout.HelpBox(
                        entry.reviewResultLabel == "Skipped"
                            ? "Skipped: the prefab keeps its own meshes and rig. Use Review Again to change that."
                            : "Applied: the prefab uses the ticked meshes and rig of the edited FBX. Use Review Again to change the selection.",
                        MessageType.Info);
                    EditorGUILayout.Space(4f);
                }

                using (new EditorGUI.DisabledScope(entry.isMeshReviewComplete))
                {
                    if (entry.meshSelections.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No skinned meshes were found in the edited FBX, so there is nothing to replace.", MessageType.Warning);
                    }
                    else
                    {
                        DrawMeshSelectionToolbar(entry);
                    }

                    DrawAnimatorReplacementRow(entry.animatorReplacement);

                    foreach (MeshSelectionState meshSelection in entry.meshSelections)
                    {
                        DrawMeshSelectionRow(meshSelection);
                    }
                }

                EditorGUILayout.Space(4f);

                // Draw with this event's value so Layout and Repaint match; the change shows on the next event.
                bool showFiles = showReplacementFiles;
                bool newShowFiles = EditorGUILayout.Foldout(showFiles, "Files", true);
                if (showFiles)
                {
                    DrawPathRow("Edited FBX", entry.copiedFbxPath);
                    DrawPathRow("Prefab", entry.copiedPrefabPath);
                }

                showReplacementFiles = newShowFiles;
            }
        }

        private void DrawReplacementActions()
        {
            DrawBackButton(WizardStep.WaitForImport);

            AvatarEntry entry = GetSelectedEntry();
            if (entry == null)
            {
                return;
            }

            if (!entry.isMeshReviewComplete)
            {
                if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Skip This Avatar", "Keep the prefab's own meshes and rig for this avatar."), ActionButtonHeight))
                {
                    SkipEntryReview(entry);
                    GUIUtility.ExitGUI();
                }

                GUILayout.FlexibleSpace();

                bool anySelected = HasAnySelectedReplacement(entry);
                string tooltip = entry.needsProcessing
                    ? "This avatar's FBX changed: update its patch first (banner above)."
                    : anySelected
                        ? "Save the ticked meshes and rig into the copied prefab."
                        : "Tick at least one mesh or the rig, or skip this avatar.";

                using (new EditorGUI.DisabledScope(!anySelected || entry.needsProcessing))
                {
                    if (PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Apply Replacements", tooltip), ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
                    {
                        ApplySelectedReplacementsToPrefab(entry);
                        // Saves the prefab, may show a dialog and may change the step.
                        GUIUtility.ExitGUI();
                    }
                }

                return;
            }

            if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Review Again", "Change the selection of this avatar and apply it again."), ActionButtonHeight))
            {
                entry.isMeshReviewComplete = false;
                entry.reviewResultLabel = string.Empty;
                status.Clear();
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            int nextIndex = FindNextIncompleteEntryIndex(selectedEntryIndex + 1);
            if (nextIndex < 0)
            {
                nextIndex = FindNextIncompleteEntryIndex(0);
            }

            if (nextIndex >= 0)
            {
                if (PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Next Avatar", $"Review '{GetEntryDisplayName(avatarEntries[nextIndex])}'."),
                        ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
                {
                    selectedEntryIndex = nextIndex;
                    scrollPosition = Vector2.zero;
                    status.Clear();
                    GUIUtility.ExitGUI();
                }
            }
            else if (PawlygonEditorUI.DrawPrimaryButton("Continue", ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
            {
                GoToStep(WizardStep.Prefabs);
                GUIUtility.ExitGUI();
            }
        }

        private (string Text, PawlygonEditorUI.BadgeKind Kind, string Tooltip) GetReviewBadge(AvatarEntry entry)
        {
            if (entry.needsProcessing)
            {
                return ("Out of date", PawlygonEditorUI.BadgeKind.Warning, "The FBX changed after its patch was made. Update it from the banner.");
            }

            if (entry.isMeshReviewComplete)
            {
                return entry.reviewResultLabel == "Skipped"
                    ? ("Skipped", PawlygonEditorUI.BadgeKind.Neutral, "The prefab keeps its own meshes and rig.")
                    : ("Applied", PawlygonEditorUI.BadgeKind.Ok, "The replacements were saved into the prefab.");
            }

            return entry.meshSelections.Any(MeshNeedsAttention)
                ? ("Needs attention", PawlygonEditorUI.BadgeKind.Warning, "Some meshes have no match, bones that can't be remapped or missing blendshapes.")
                : ("Pending", PawlygonEditorUI.BadgeKind.Info, "Not reviewed yet.");
        }

        private static bool MeshNeedsAttention(MeshSelectionState meshSelection)
        {
            return !meshSelection.hasMatch || meshSelection.bonesUnresolved || HasMissingUnifiedBlendshapesWarning(meshSelection);
        }

        private void DrawMeshSelectionToolbar(AvatarEntry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Select All", "Tick every matched mesh (not those whose bones can't be remapped)."), EditorStyles.miniButtonLeft, GUILayout.Width(80f)))
                {
                    SetMeshSelectionState(entry, true);
                }

                if (GUILayout.Button("Deselect All", EditorStyles.miniButtonRight, GUILayout.Width(80f)))
                {
                    SetMeshSelectionState(entry, false);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{GetSelectedReplacementCount(entry)} selected", mutedMiniStyle);
            }

            EditorGUILayout.Space(2f);
        }

        private void DrawAnimatorReplacementRow(AnimatorReplacementState animatorReplacement)
        {
            animatorReplacement ??= new AnimatorReplacementState();
            bool available = animatorReplacement.hasPrefabAnimator && animatorReplacement.hasHumanoidAvatar;

            using (new EditorGUILayout.VerticalScope(cardStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(14f);

                    using (new EditorGUI.DisabledScope(!available))
                    {
                        string label = string.IsNullOrEmpty(animatorReplacement.fbxAvatarName)
                            ? "Humanoid rig"
                            : $"Humanoid rig ({animatorReplacement.fbxAvatarName})";
                        animatorReplacement.selected = EditorGUILayout.ToggleLeft(
                            new GUIContent(label, "Gives the prefab's main Animator the edited FBX's humanoid Avatar (bone mapping)."),
                            animatorReplacement.selected, GUILayout.MinWidth(60f));
                    }

                    if (available)
                    {
                        PawlygonEditorUI.DrawBadge("Ready", PawlygonEditorUI.BadgeKind.Ok, animatorReplacement.matchReason);
                    }
                    else
                    {
                        PawlygonEditorUI.DrawBadge("Unavailable", PawlygonEditorUI.BadgeKind.Neutral,
                            !animatorReplacement.hasPrefabAnimator
                                ? "The prefab has no Animator to update."
                                : "The edited FBX has no valid humanoid Avatar. Set its rig to Humanoid in the import settings.");
                    }
                }
            }
        }

        private void DrawMeshSelectionRow(MeshSelectionState meshSelection)
        {
            using (new EditorGUILayout.VerticalScope(cardStyle))
            {
                // Draw with this event's value so Layout and Repaint match; the change shows on the next event.
                bool show = meshSelection.showDetails;
                bool newShow;

                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect foldoutRect = GUILayoutUtility.GetRect(14f, EditorGUIUtility.singleLineHeight, GUILayout.Width(14f));
                    newShow = EditorGUI.Foldout(foldoutRect, show, GUIContent.none, true);

                    using (new EditorGUI.DisabledScope(!meshSelection.hasMatch))
                    {
                        string meshLabel = string.IsNullOrEmpty(meshSelection.fbxMeshName) || meshSelection.fbxMeshName == meshSelection.fbxObjectName
                            ? meshSelection.fbxObjectName
                            : $"{meshSelection.fbxObjectName} ({meshSelection.fbxMeshName})";
                        meshSelection.selected = EditorGUILayout.ToggleLeft(
                            new GUIContent(meshLabel, meshSelection.fbxRelativePath), meshSelection.selected, GUILayout.MinWidth(60f));
                    }

                    DrawMeshBadge(meshSelection);
                }

                if (show)
                {
                    DrawMeshDetails(meshSelection);
                }

                meshSelection.showDetails = newShow;
            }
        }

        private static void DrawMeshBadge(MeshSelectionState meshSelection)
        {
            if (!meshSelection.hasMatch)
            {
                PawlygonEditorUI.DrawBadge("No match", PawlygonEditorUI.BadgeKind.Warning,
                    "The prefab has no skinned mesh with the same path, object name or mesh name, so there is nothing to replace.");
            }
            else if (meshSelection.bonesUnresolved)
            {
                PawlygonEditorUI.DrawBadge("Bones differ", PawlygonEditorUI.BadgeKind.Error, meshSelection.boneIssue);
            }
            else if (HasMissingUnifiedBlendshapesWarning(meshSelection))
            {
                PawlygonEditorUI.DrawBadge($"{meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length} blendshapes missing", PawlygonEditorUI.BadgeKind.Warning,
                    "This body mesh lacks Unified Expressions blendshapes that face tracking needs.");
            }
            else if (meshSelection.bonesDiffer)
            {
                PawlygonEditorUI.DrawBadge("Bones remapped", PawlygonEditorUI.BadgeKind.Info,
                    "The mesh uses other bones than the prefab's; they are matched by name onto the prefab's armature.");
            }
            else
            {
                PawlygonEditorUI.DrawBadge("Matched", PawlygonEditorUI.BadgeKind.Ok, $"Matched by {meshSelection.matchReason}.");
            }
        }

        private void DrawMeshDetails(MeshSelectionState meshSelection)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField($"<b>From the FBX:</b> {meshSelection.fbxRelativePath}", PawlygonEditorUI.RichMiniLabelStyle);
            EditorGUILayout.LabelField(meshSelection.hasMatch
                    ? $"<b>Replaces on the prefab:</b> {meshSelection.prefabRelativePath} ({meshSelection.prefabMeshName}), matched by {meshSelection.matchReason}"
                    : "<b>Replaces on the prefab:</b> nothing, no matching skinned mesh was found",
                PawlygonEditorUI.RichMiniLabelStyle);

            if (meshSelection.hasMatch && meshSelection.bonesDiffer)
            {
                DrawBoneStatus(meshSelection);
            }

            if (HasMissingUnifiedBlendshapesWarning(meshSelection))
            {
                bool showList = meshSelection.showUnifiedBlendshapeWarningDetails;
                bool newShowList = EditorGUILayout.Foldout(showList,
                    $"Missing Unified Expressions blendshapes ({meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length})", true);

                if (showList)
                {
                    EditorGUILayout.LabelField(string.Join(", ", meshSelection.missingRequiredUnifiedBlendshapesOnFbx), PawlygonEditorUI.RichMiniLabelStyle);
                }

                meshSelection.showUnifiedBlendshapeWarningDetails = newShowList;
            }
            else if (HasCompleteUnifiedBlendshapesInfo(meshSelection))
            {
                EditorGUILayout.LabelField("All Unified Expressions blendshapes found.", PawlygonEditorUI.RichMiniLabelStyle);
            }

            EditorGUI.indentLevel--;
        }

        private static void DrawBoneStatus(MeshSelectionState meshSelection)
        {
            if (meshSelection.bonesUnresolved)
            {
                EditorGUILayout.HelpBox(
                    $"The FBX mesh's bones differ from the prefab's and can't be remapped: {meshSelection.boneIssue}. " +
                    "Replacing it would keep the prefab's bones and skin it incorrectly, so it isn't ticked " +
                    "(ticking it asks for confirmation when applying).",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField("Its bones differ from the prefab's; they will be matched by name onto the prefab's armature.", PawlygonEditorUI.RichMiniLabelStyle);
        }

        // =====================================================================
        // Loading and applying
        // =====================================================================

        private void LoadMeshSelections(AvatarEntry entry)
        {
            entry.animatorReplacement = new AnimatorReplacementState();
            entry.meshSelections = new List<MeshSelectionState>();

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath);
            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);

            if (fbxRoot == null)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not load FBX asset at '{entry.copiedFbxPath}'.");
                return;
            }

            if (prefabRoot == null)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not load prefab asset at '{entry.copiedPrefabPath}'.");
                return;
            }

            entry.animatorReplacement = CreateAnimatorReplacementState(fbxRoot, prefabRoot, entry.copiedFbxPath);

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            List<RendererInfo> fbxRenderers = GetRendererInfos(fbxRoot, fbxMeshSubAssets);
            List<RendererInfo> prefabRenderers = GetRendererInfos(prefabRoot, meshSubAssets: null);
            var usedPrefabPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var boneMapper = new BoneMapper(prefabRoot);

            entry.meshSelections = fbxRenderers
                .Select(fbxRenderer => CreateMeshSelectionState(fbxRenderer, prefabRenderers, usedPrefabPaths, boneMapper))
                .OrderBy(selection => selection.fbxRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            PopulateUnifiedBlendshapeWarnings(entry.meshSelections, fbxMeshSubAssets, PawlygonEditorUtils.RequiredUnifiedExpressionBlendshapes);

            // Rows that need a look start open; the rest collapse to one line.
            foreach (MeshSelectionState meshSelection in entry.meshSelections)
            {
                meshSelection.showDetails = MeshNeedsAttention(meshSelection);
            }
        }

        private void ApplySelectedReplacementsToPrefab(AvatarEntry entry)
        {
            List<MeshSelectionState> selectedMappings = entry.meshSelections
                .Where(selection => selection.selected && selection.hasMatch)
                .ToList();

            bool shouldReplaceAnimator = entry.animatorReplacement != null &&
                entry.animatorReplacement.selected &&
                entry.animatorReplacement.hasPrefabAnimator &&
                entry.animatorReplacement.hasHumanoidAvatar;

            if (selectedMappings.Count == 0 && !shouldReplaceAnimator)
            {
                status.Warning("Tick at least one mesh or the humanoid rig, or skip this avatar.");
                return;
            }

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            if (selectedMappings.Count > 0 && fbxMeshSubAssets.Count == 0)
            {
                status.Error($"No meshes could be loaded from '{entry.copiedFbxPath}'. Check that the edited FBX imported correctly.");
                return;
            }

            Avatar replacementAvatar = shouldReplaceAnimator
                ? LoadReplacementAvatar(entry.animatorReplacement, entry.copiedFbxPath)
                : null;

            if (shouldReplaceAnimator && !IsValidHumanoidAvatar(replacementAvatar))
            {
                status.Error($"No valid humanoid Avatar could be loaded from '{entry.copiedFbxPath}'. Untick the rig or set the FBX's rig to Humanoid.");
                return;
            }

            // Meshes whose bones were flagged as not remappable during review were deselected by
            // default; the user ticked them again, so make the consequence explicit.
            bool replaceUnresolvedBoneMeshes = false;
            List<MeshSelectionState> unresolvedBoneMappings = selectedMappings.Where(mapping => mapping.bonesUnresolved).ToList();
            if (unresolvedBoneMappings.Count > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Bones Cannot Be Remapped",
                    "These meshes use bones that cannot all be found on the prefab:\n\n" +
                    string.Join("\n", unresolvedBoneMappings.Select(mapping => $"• {mapping.fbxObjectName}: {mapping.boneIssue}")) +
                    "\n\nReplacing them anyway keeps the prefab's bones, so they will skin incorrectly.",
                    "Skip These Meshes",
                    "Cancel",
                    "Replace Anyway");

                if (choice == 1)
                {
                    return;
                }

                replaceUnresolvedBoneMeshes = choice == 2;
            }

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath);
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);

            int replacedCount = 0;
            int remappedCount = 0;
            int skippedBoneCount = 0;
            bool replacedAnimator = false;

            try
            {
                Dictionary<string, SkinnedMeshRenderer> prefabRendererLookup = BuildFirstComponentByPathLookup<SkinnedMeshRenderer>(prefabRoot);
                Dictionary<string, Animator> prefabAnimatorLookup = BuildFirstComponentByPathLookup<Animator>(prefabRoot);
                Dictionary<string, SkinnedMeshRenderer> fbxRendererLookup = fbxRoot != null
                    ? BuildFirstComponentByPathLookup<SkinnedMeshRenderer>(fbxRoot)
                    : new Dictionary<string, SkinnedMeshRenderer>();
                var boneMapper = new BoneMapper(prefabRoot);

                foreach (MeshSelectionState mapping in selectedMappings)
                {
                    if (string.IsNullOrEmpty(mapping.fbxMeshName))
                    {
                        continue;
                    }

                    if (!fbxMeshSubAssets.TryGetValue(mapping.fbxMeshName, out Mesh fbxMesh))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] Mesh sub-asset '{mapping.fbxMeshName}' not found in FBX '{entry.copiedFbxPath}'.");
                        continue;
                    }

                    if (!prefabRendererLookup.TryGetValue(mapping.prefabRelativePath ?? string.Empty, out SkinnedMeshRenderer prefabRenderer))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] No SkinnedMeshRenderer at relative path '{mapping.prefabRelativePath}' in prefab.");
                        continue;
                    }

                    // Bones are compared again against the prefab as it is now. When they match,
                    // only the mesh is swapped, exactly as before.
                    BoneRemap boneRemap = fbxRendererLookup.TryGetValue(mapping.fbxRelativePath ?? string.Empty, out SkinnedMeshRenderer fbxRenderer)
                        ? boneMapper.Resolve(fbxRenderer, prefabRenderer)
                        : null;

                    if (boneRemap != null && boneRemap.CanRemap)
                    {
                        prefabRenderer.bones = boneRemap.Bones;
                        prefabRenderer.rootBone = boneRemap.RootBone;
                        prefabRenderer.localBounds = boneRemap.LocalBounds;
                        remappedCount++;
                    }
                    else if (boneRemap != null && boneRemap.BonesDiffer)
                    {
                        if (!replaceUnresolvedBoneMeshes || !mapping.bonesUnresolved)
                        {
                            Debug.LogWarning($"[AvatarSetupWizard] Skipped mesh '{mapping.fbxObjectName}' on '{entry.copiedPrefabPath}': its bones cannot be remapped ({boneRemap.Issue}).");
                            skippedBoneCount++;
                            continue;
                        }

                        Debug.LogWarning($"[AvatarSetupWizard] Replaced mesh '{mapping.fbxObjectName}' on '{entry.copiedPrefabPath}' although its bones cannot be remapped ({boneRemap.Issue}). It will skin incorrectly.");
                    }

                    prefabRenderer.sharedMesh = fbxMesh;
                    replacedCount++;
                }

                if (shouldReplaceAnimator)
                {
                    if (!prefabAnimatorLookup.TryGetValue(entry.animatorReplacement.prefabAnimatorRelativePath ?? string.Empty, out Animator prefabAnimator))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] No Animator at relative path '{entry.animatorReplacement.prefabAnimatorRelativePath}' in prefab.");
                    }
                    else
                    {
                        prefabAnimator.avatar = replacementAvatar;
                        replacedAnimator = true;
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            bool movedOn = CompleteEntryReview(entry, "Applied");

            string message = BuildReplacementStatusMessage(entry, replacedCount, replacedAnimator);
            if (remappedCount > 0)
            {
                message += $" Remapped the bones of {Plural(remappedCount, "mesh", "meshes")} onto the prefab's armature.";
            }

            if (skippedBoneCount > 0)
            {
                message += $" Skipped {Plural(skippedBoneCount, "mesh", "meshes")} whose bones couldn't be remapped (see the Console).";
            }

            if (movedOn)
            {
                message += " Every avatar is reviewed.";
            }

            Action ping = PawlygonStatus.Ping(AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath));
            if (skippedBoneCount > 0) status.Warning(message, "Ping Prefab", ping);
            else status.Info(message, "Ping Prefab", ping);
        }

        /// <summary>
        /// Maps each component's sibling-name path (case-insensitive) to the first component found
        /// at that path in hierarchy order. Objects can share a name, so several components may
        /// resolve to the same path; the first one is kept, matching how mesh selections are paired
        /// (<see cref="FindBestRendererMatch"/> also takes the first renderer per path).
        /// </summary>
        private static Dictionary<string, T> BuildFirstComponentByPathLookup<T>(GameObject root) where T : Component
        {
            var lookup = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            foreach (T component in root.GetComponentsInChildren<T>(true))
            {
                string relativePath = GetRelativeTransformPath(component.transform);
                if (lookup.ContainsKey(relativePath))
                {
                    Debug.LogWarning($"[AvatarSetupWizard] Several objects with a {typeof(T).Name} share the path '{relativePath}' in prefab '{root.name}'. Only the first one can be updated.");
                    continue;
                }

                lookup.Add(relativePath, component);
            }

            return lookup;
        }

        /// <summary>Marks the avatar as skipped (the prefab keeps its meshes and rig). Undone with Review Again.</summary>
        private void SkipEntryReview(AvatarEntry entry)
        {
            bool movedOn = CompleteEntryReview(entry, "Skipped");
            status.Info($"Skipped the replacements of '{GetEntryDisplayName(entry)}': its prefab keeps its own meshes and rig." +
                        (movedOn ? " Every avatar is reviewed." : string.Empty));
        }

        /// <summary>
        /// Marks an avatar as reviewed and selects the next one to review. The first time every avatar is
        /// reviewed, moves on to Prefabs and returns true; when revisiting the step, stays put.
        /// </summary>
        private bool CompleteEntryReview(AvatarEntry entry, string reviewResultLabel)
        {
            entry.isMeshReviewComplete = true;
            entry.reviewResultLabel = reviewResultLabel;

            if (avatarEntries.All(item => item.isMeshReviewComplete))
            {
                if (furthestStep == WizardStep.SelectMeshes)
                {
                    GoToStep(WizardStep.Prefabs);
                    return true;
                }

                return false;
            }

            int nextIndex = FindNextIncompleteEntryIndex(selectedEntryIndex + 1);
            if (nextIndex < 0)
            {
                nextIndex = FindNextIncompleteEntryIndex(0);
            }

            if (nextIndex >= 0)
            {
                selectedEntryIndex = nextIndex;
                scrollPosition = Vector2.zero;
            }

            return false;
        }

        private int FindNextIncompleteEntryIndex(int startIndex)
        {
            for (int i = startIndex; i < avatarEntries.Count; i++)
            {
                if (!avatarEntries[i].isMeshReviewComplete)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void SetMeshSelectionState(AvatarEntry entry, bool selected)
        {
            foreach (MeshSelectionState meshSelection in entry.meshSelections)
            {
                // "Select All" never picks meshes whose bones cannot be remapped; those must be
                // ticked individually (and are confirmed when applying).
                if (meshSelection.hasMatch && (!selected || !meshSelection.bonesUnresolved))
                {
                    meshSelection.selected = selected;
                }
            }
        }

        private static bool HasAnySelectedReplacement(AvatarEntry entry)
        {
            return GetSelectedReplacementCount(entry) > 0;
        }

        private static int GetSelectedReplacementCount(AvatarEntry entry)
        {
            if (entry == null)
            {
                return 0;
            }

            int selectedMeshCount = entry.meshSelections.Count(selection => selection.selected && selection.hasMatch);
            int selectedRigCount = entry.animatorReplacement != null &&
                entry.animatorReplacement.selected &&
                entry.animatorReplacement.hasPrefabAnimator &&
                entry.animatorReplacement.hasHumanoidAvatar
                ? 1
                : 0;

            return selectedMeshCount + selectedRigCount;
        }

        private static string BuildReplacementStatusMessage(AvatarEntry entry, int replacedMeshCount, bool replacedAnimator)
        {
            string displayName = GetEntryDisplayName(entry);

            if (replacedMeshCount > 0 && replacedAnimator)
            {
                return $"Updated {Plural(replacedMeshCount, "mesh", "meshes")} and the humanoid rig on '{displayName}'.";
            }

            if (replacedMeshCount > 0)
            {
                return $"Updated {Plural(replacedMeshCount, "mesh", "meshes")} on '{displayName}'.";
            }

            if (replacedAnimator)
            {
                return $"Updated the humanoid rig on '{displayName}'.";
            }

            return $"No meshes or rig were updated on '{displayName}'.";
        }

        private static bool HasMissingUnifiedBlendshapesWarning(MeshSelectionState meshSelection)
        {
            return meshSelection != null &&
                meshSelection.isBodyMeshCandidate &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx != null &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length > 0;
        }

        private static bool HasCompleteUnifiedBlendshapesInfo(MeshSelectionState meshSelection)
        {
            return meshSelection != null &&
                meshSelection.isBodyMeshCandidate &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx != null &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length == 0;
        }

        private static Dictionary<string, Mesh> LoadMeshSubAssets(string fbxAssetPath)
        {
            var result = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);

            if (allSubAssets == null || allSubAssets.Length == 0)
            {
                return result;
            }

            foreach (UnityEngine.Object subAsset in allSubAssets)
            {
                if (subAsset is Mesh mesh && !string.IsNullOrEmpty(mesh.name))
                {
                    result[mesh.name] = mesh;
                }
            }

            return result;
        }

        private static void PopulateUnifiedBlendshapeWarnings(List<MeshSelectionState> meshSelections, Dictionary<string, Mesh> fbxMeshSubAssets, string[] requiredBlendshapes)
        {
            if (meshSelections == null)
            {
                return;
            }

            foreach (MeshSelectionState meshSelection in meshSelections)
            {
                meshSelection.isBodyMeshCandidate = IsBodyMeshSelection(meshSelection);
                meshSelection.showUnifiedBlendshapeWarningDetails = false;
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx = Array.Empty<string>();

                if (!meshSelection.isBodyMeshCandidate)
                {
                    continue;
                }

                Mesh fbxMesh = ResolveFbxMeshForSelection(meshSelection, fbxMeshSubAssets);
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx = PawlygonEditorUtils.GetMissingRequiredUnifiedBlendshapes(fbxMesh, requiredBlendshapes);
            }
        }

        private static Mesh ResolveFbxMeshForSelection(MeshSelectionState meshSelection, Dictionary<string, Mesh> fbxMeshSubAssets)
        {
            if (meshSelection == null || fbxMeshSubAssets == null || fbxMeshSubAssets.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(meshSelection.fbxMeshName) && fbxMeshSubAssets.TryGetValue(meshSelection.fbxMeshName, out Mesh meshByName))
            {
                return meshByName;
            }

            if (!string.IsNullOrEmpty(meshSelection.fbxObjectName) && fbxMeshSubAssets.TryGetValue(meshSelection.fbxObjectName, out Mesh meshByObjectName))
            {
                return meshByObjectName;
            }

            return null;
        }

        private static List<Avatar> LoadHumanoidAvatarSubAssets(string fbxAssetPath)
        {
            var result = new List<Avatar>();
            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);

            if (allSubAssets == null || allSubAssets.Length == 0)
            {
                return result;
            }

            foreach (UnityEngine.Object subAsset in allSubAssets)
            {
                if (subAsset is Avatar avatar && IsValidHumanoidAvatar(avatar))
                {
                    result.Add(avatar);
                }
            }

            return result;
        }

        private static AnimatorReplacementState CreateAnimatorReplacementState(GameObject fbxRoot, GameObject prefabRoot, string fbxAssetPath)
        {
            AnimatorInfo prefabAnimator = GetPrimaryAnimatorInfo(prefabRoot);
            AnimatorInfo fbxAnimator = GetPrimaryAnimatorInfo(fbxRoot);
            Avatar replacementAvatar = null;
            string matchReason;

            if (IsValidHumanoidAvatar(fbxAnimator?.Avatar))
            {
                replacementAvatar = fbxAnimator.Avatar;
                matchReason = "Using the duplicated FBX's primary Animator avatar.";
            }
            else
            {
                List<Avatar> avatars = LoadHumanoidAvatarSubAssets(fbxAssetPath);
                replacementAvatar = avatars.FirstOrDefault();
                matchReason = replacementAvatar != null
                    ? "Using the duplicated FBX's humanoid Avatar sub-asset."
                    : "No valid humanoid Avatar was found on the duplicated FBX.";
            }

            return new AnimatorReplacementState
            {
                prefabAnimatorObjectName = prefabAnimator?.ObjectName ?? string.Empty,
                prefabAnimatorRelativePath = prefabAnimator?.RelativePath ?? string.Empty,
                fbxAnimatorObjectName = fbxAnimator?.ObjectName ?? string.Empty,
                fbxAnimatorRelativePath = fbxAnimator?.RelativePath ?? string.Empty,
                fbxAvatarName = replacementAvatar != null ? replacementAvatar.name : string.Empty,
                matchReason = matchReason,
                hasPrefabAnimator = prefabAnimator != null,
                hasHumanoidAvatar = replacementAvatar != null,
                selected = prefabAnimator != null && replacementAvatar != null
            };
        }

        private static List<RendererInfo> GetRendererInfos(GameObject root, Dictionary<string, Mesh> meshSubAssets)
        {
            var results = new List<RendererInfo>();
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                string objectName = renderer.gameObject.name;
                string relativePath = GetRelativeTransformPath(renderer.transform);
                string meshName = string.Empty;

                if (renderer.sharedMesh != null)
                {
                    meshName = renderer.sharedMesh.name;
                }
                else if (meshSubAssets != null)
                {
                    if (meshSubAssets.TryGetValue(objectName, out Mesh _))
                    {
                        meshName = objectName;
                    }
                    else
                    {
                        string found = meshSubAssets.Keys.FirstOrDefault(
                            key => key.IndexOf(objectName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   objectName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (found != null)
                        {
                            meshName = found;
                        }
                    }
                }

                results.Add(new RendererInfo
                {
                    Renderer = renderer,
                    ObjectName = objectName,
                    RelativePath = relativePath,
                    MeshName = meshName
                });
            }

            return results;
        }

        private static AnimatorInfo GetPrimaryAnimatorInfo(GameObject root)
        {
            if (root == null)
            {
                return null;
            }

            return root
                .GetComponentsInChildren<Animator>(true)
                .Select(animator => new AnimatorInfo
                {
                    Animator = animator,
                    ObjectName = animator.gameObject.name,
                    RelativePath = GetRelativeTransformPath(animator.transform),
                    Avatar = animator.avatar,
                    PriorityScore = GetAnimatorPriorityScore(root.transform, animator)
                })
                .OrderByDescending(info => info.PriorityScore)
                .ThenBy(info => info.RelativePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int GetAnimatorPriorityScore(Transform rootTransform, Animator animator)
        {
            if (animator == null)
            {
                return int.MinValue;
            }

            int score = 0;

            if (IsValidHumanoidAvatar(animator.avatar))
            {
                score += 1000;
            }

            if (animator.transform == rootTransform)
            {
                score += 500;
            }
            else if (animator.transform.parent == rootTransform)
            {
                score += 250;
            }

            score += animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length * 10;
            score -= GetTransformDepth(animator.transform, rootTransform);
            return score;
        }

        private static int GetTransformDepth(Transform transform, Transform rootTransform)
        {
            int depth = 0;
            Transform current = transform;

            while (current != null && current != rootTransform)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static MeshSelectionState CreateMeshSelectionState(RendererInfo fbxRenderer, List<RendererInfo> prefabRenderers, ISet<string> usedPrefabPaths, BoneMapper boneMapper)
        {
            RendererInfo matchedRenderer = FindBestRendererMatch(fbxRenderer, prefabRenderers, usedPrefabPaths, out string matchReason);
            BoneRemap boneRemap = null;

            if (matchedRenderer != null)
            {
                usedPrefabPaths.Add(matchedRenderer.RelativePath);
                boneRemap = boneMapper.Resolve(fbxRenderer.Renderer, matchedRenderer.Renderer);
            }

            bool bonesUnresolved = boneRemap != null && boneRemap.BonesDiffer && !boneRemap.CanRemap;

            return new MeshSelectionState
            {
                fbxObjectName = fbxRenderer.ObjectName,
                fbxRelativePath = fbxRenderer.RelativePath,
                fbxMeshName = fbxRenderer.MeshName,
                prefabObjectName = matchedRenderer?.ObjectName ?? string.Empty,
                prefabRelativePath = matchedRenderer?.RelativePath ?? string.Empty,
                prefabMeshName = matchedRenderer?.MeshName ?? string.Empty,
                matchReason = matchReason,
                hasMatch = matchedRenderer != null,
                // A mesh whose bones cannot be remapped would skin incorrectly; leave it unticked.
                selected = matchedRenderer != null && !bonesUnresolved,
                bonesDiffer = boneRemap != null && boneRemap.BonesDiffer,
                bonesUnresolved = bonesUnresolved,
                boneIssue = boneRemap?.Issue ?? string.Empty
            };
        }

        private static bool IsBodyMeshSelection(MeshSelectionState meshSelection)
        {
            if (meshSelection == null)
            {
                return false;
            }

            return string.Equals(meshSelection.fbxObjectName, "Body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(meshSelection.fbxMeshName, "Body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetLastPathSegment(meshSelection.fbxRelativePath), "Body", StringComparison.OrdinalIgnoreCase);
        }

        private static RendererInfo FindBestRendererMatch(RendererInfo fbxRenderer, List<RendererInfo> prefabRenderers, ISet<string> usedPrefabPaths, out string matchReason)
        {
            RendererInfo relativePathMatch = prefabRenderers.FirstOrDefault(renderer =>
                !usedPrefabPaths.Contains(renderer.RelativePath) &&
                string.Equals(renderer.RelativePath, fbxRenderer.RelativePath, StringComparison.OrdinalIgnoreCase));

            if (relativePathMatch != null)
            {
                matchReason = "Relative path";
                return relativePathMatch;
            }

            RendererInfo objectNameMatch = prefabRenderers.FirstOrDefault(renderer =>
                !usedPrefabPaths.Contains(renderer.RelativePath) &&
                string.Equals(renderer.ObjectName, fbxRenderer.ObjectName, StringComparison.OrdinalIgnoreCase));

            if (objectNameMatch != null)
            {
                matchReason = "GameObject name";
                return objectNameMatch;
            }

            if (!string.IsNullOrEmpty(fbxRenderer.MeshName))
            {
                RendererInfo meshNameMatch = prefabRenderers.FirstOrDefault(renderer =>
                    !usedPrefabPaths.Contains(renderer.RelativePath) &&
                    !string.IsNullOrEmpty(renderer.MeshName) &&
                    string.Equals(renderer.MeshName, fbxRenderer.MeshName, StringComparison.OrdinalIgnoreCase));

                if (meshNameMatch != null)
                {
                    matchReason = "Mesh name";
                    return meshNameMatch;
                }
            }

            matchReason = string.Empty;
            return null;
        }

        private static string GetRelativeTransformPath(Transform transform)
        {
            var names = new List<string>();
            Transform current = transform;

            while (current != null && current.parent != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            if (names.Count == 0)
            {
                return transform.name;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static string GetLastPathSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            int separatorIndex = path.LastIndexOf('/');
            return separatorIndex >= 0 ? path.Substring(separatorIndex + 1) : path;
        }

        private static Transform FindTransformByRelativePath(Transform root, string relativePath)
        {
            if (root == null || string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            if (string.Equals(relativePath, root.name, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            string rootPrefix = root.name + "/";
            string localPath = relativePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? relativePath.Substring(rootPrefix.Length)
                : relativePath;

            return root.Find(localPath);
        }

        private static Avatar LoadReplacementAvatar(AnimatorReplacementState animatorReplacement, string fbxAssetPath)
        {
            if (animatorReplacement == null)
            {
                return null;
            }

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (fbxRoot != null && !string.IsNullOrEmpty(animatorReplacement.fbxAnimatorRelativePath))
            {
                Transform animatorTransform = FindTransformByRelativePath(fbxRoot.transform, animatorReplacement.fbxAnimatorRelativePath);
                Animator fbxAnimator = animatorTransform != null ? animatorTransform.GetComponent<Animator>() : null;
                if (IsValidHumanoidAvatar(fbxAnimator?.avatar))
                {
                    return fbxAnimator.avatar;
                }
            }

            List<Avatar> avatars = LoadHumanoidAvatarSubAssets(fbxAssetPath);
            return avatars.FirstOrDefault(avatar => string.Equals(avatar.name, animatorReplacement.fbxAvatarName, StringComparison.OrdinalIgnoreCase))
                ?? avatars.FirstOrDefault();
        }

        private static bool IsValidHumanoidAvatar(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }
    }
}
