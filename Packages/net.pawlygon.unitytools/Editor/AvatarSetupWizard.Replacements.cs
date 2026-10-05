using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Pawlygon.UnityTools.Editor
{
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

        private void DrawMeshSelectionStep()
        {
            AvatarEntry selectedEntry = GetSelectedEntry();
            if (selectedEntry == null)
            {
                statusMessage = "No avatar entries are available for mesh review.";
                currentStep = WizardStep.Setup;
                return;
            }

            PawlygonEditorUI.DrawSection(
                "Select Replacements",
                "Review one avatar at a time. Apply the selected mesh and humanoid rig replacements or explicitly skip that avatar.",
                () =>
                {
                    DrawEntrySelectionToolbar();
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    DrawMeshReviewSummary(selectedEntry);
                    EditorGUILayout.Space(SectionSpacing);

                    if (selectedEntry.meshSelections.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No skinned mesh renderer mappings were found between the duplicated FBX and prefab.", MessageType.Warning);
                    }
                    else
                    {
                        DrawMeshSelectionToolbar(selectedEntry);
                        EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                        using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                        {
                            DrawAnimatorReplacementRow(selectedEntry.animatorReplacement);

                            foreach (MeshSelectionState meshSelection in selectedEntry.meshSelections)
                            {
                                DrawMeshSelectionRow(meshSelection);
                            }
                        }
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Skip This Avatar", GUILayout.Height(34f)))
                        {
                            SkipEntryReview(selectedEntry);
                            // Shows a modal dialog and may change the step.
                            GUIUtility.ExitGUI();
                        }

                        using (new EditorGUI.DisabledScope(!HasAnySelectedReplacement(selectedEntry)))
                        {
                            if (PawlygonEditorUI.DrawPrimaryButton("Apply Selected Replacements", 34f))
                            {
                                ApplySelectedReplacementsToPrefab(selectedEntry);
                                // Saves the prefab, may show a dialog and may change the step.
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                });
        }

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
                EditorUtility.DisplayDialog("No Replacements Selected", "Select at least one mapped skinned mesh renderer or the humanoid rig replacement to update on the prefab.", "OK");
                return;
            }

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            if (selectedMappings.Count > 0 && fbxMeshSubAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("FBX Missing Meshes", "No mesh sub-assets could be loaded from the duplicated FBX.", "OK");
                return;
            }

            Avatar replacementAvatar = shouldReplaceAnimator
                ? LoadReplacementAvatar(entry.animatorReplacement, entry.copiedFbxPath)
                : null;

            if (shouldReplaceAnimator && !IsValidHumanoidAvatar(replacementAvatar))
            {
                EditorUtility.DisplayDialog("FBX Missing Humanoid Avatar", "No valid humanoid avatar could be loaded from the duplicated FBX.", "OK");
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

            try
            {
                Dictionary<string, SkinnedMeshRenderer> prefabRendererLookup = BuildFirstComponentByPathLookup<SkinnedMeshRenderer>(prefabRoot);
                Dictionary<string, Animator> prefabAnimatorLookup = BuildFirstComponentByPathLookup<Animator>(prefabRoot);
                Dictionary<string, SkinnedMeshRenderer> fbxRendererLookup = fbxRoot != null
                    ? BuildFirstComponentByPathLookup<SkinnedMeshRenderer>(fbxRoot)
                    : new Dictionary<string, SkinnedMeshRenderer>();
                var boneMapper = new BoneMapper(prefabRoot);

                int replacedCount = 0;
                int remappedCount = 0;
                int skippedBoneCount = 0;
                bool replacedAnimator = false;

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
                CompleteEntryReview(entry, "Applied");
                statusMessage = BuildReplacementStatusMessage(entry, replacedCount, replacedAnimator);

                if (remappedCount > 0)
                {
                    statusMessage += $" Remapped the bones of {remappedCount} mesh(es) onto the prefab's armature.";
                }

                if (skippedBoneCount > 0)
                {
                    statusMessage += $" Skipped {skippedBoneCount} mesh(es) whose bones could not be remapped (see the Console).";
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
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

        private void SkipEntryReview(AvatarEntry entry)
        {
            if (!EditorUtility.DisplayDialog("Skip Avatar Review", $"Skip mesh and rig replacement for '{GetEntryDisplayName(entry)}'?", "Skip", "Cancel"))
            {
                return;
            }

            CompleteEntryReview(entry, "Skipped");
            statusMessage = $"Skipped mesh and rig replacement for '{GetEntryDisplayName(entry)}'.";
        }

        private void CompleteEntryReview(AvatarEntry entry, string reviewResultLabel)
        {
            entry.isMeshReviewComplete = true;
            entry.reviewResultLabel = reviewResultLabel;

            if (avatarEntries.All(item => item.isMeshReviewComplete))
            {
                currentStep = WizardStep.Prefabs;
                statusMessage = string.Empty;
                return;
            }

            int nextIndex = FindNextIncompleteEntryIndex(selectedEntryIndex + 1);
            if (nextIndex < 0)
            {
                nextIndex = FindNextIncompleteEntryIndex(0);
            }

            if (nextIndex >= 0)
            {
                selectedEntryIndex = nextIndex;
            }
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

        private void DrawEntrySelectionToolbar()
        {
            string[] labels = avatarEntries
                .Select(entry =>
                {
                    string prefix = entry.isMeshReviewComplete ? $"[{entry.reviewResultLabel}] " : string.Empty;
                    return prefix + GetEntryDisplayName(entry);
                })
                .ToArray();

            selectedEntryIndex = GUILayout.Toolbar(Mathf.Clamp(selectedEntryIndex, 0, labels.Length - 1), labels);
        }

        private void DrawMeshReviewSummary(AvatarEntry entry)
        {
            int matchedCount = entry.meshSelections.Count(selection => selection.hasMatch);
            int selectedCount = entry.meshSelections.Count(selection => selection.selected && selection.hasMatch);
            string rigStatus = GetAnimatorSelectionSummary(entry.animatorReplacement);
            bool hasMissingUnifiedBlendshapes = entry.meshSelections.Any(HasMissingUnifiedBlendshapesWarning);
            bool hasCompleteUnifiedBlendshapes = entry.meshSelections.Any(HasCompleteUnifiedBlendshapesInfo);

            using (new EditorGUILayout.VerticalScope(helpBoxPadding10_8))
            {
                EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{matchedCount} matched renderer{(matchedCount == 1 ? string.Empty : "s")}, {selectedCount} selected", PawlygonEditorUI.RichMiniLabelStyle);
                EditorGUILayout.LabelField($"Humanoid rig: {rigStatus}", PawlygonEditorUI.RichMiniLabelStyle);
                if (hasMissingUnifiedBlendshapes)
                {
                    EditorGUILayout.LabelField("Warning: Missing Unified Expression Blendshapes", PawlygonEditorUI.RichMiniLabelStyle);
                }
                else if (hasCompleteUnifiedBlendshapes)
                {
                    EditorGUILayout.LabelField("Unified Expression Blendshapes: Complete", PawlygonEditorUI.RichMiniLabelStyle);
                }

                DrawReadOnlyPathField("Modified FBX", entry.copiedFbxPath);
                DrawReadOnlyPathField("Target Prefab", entry.copiedPrefabPath);
            }
        }

        private void DrawMeshSelectionToolbar(AvatarEntry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(90f)))
                {
                    SetMeshSelectionState(entry, true);
                }

                if (GUILayout.Button("Deselect All", GUILayout.Width(90f)))
                {
                    SetMeshSelectionState(entry, false);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{GetSelectedReplacementCount(entry)} selected", EditorStyles.miniBoldLabel);
            }
        }

        private void DrawAnimatorReplacementRow(AnimatorReplacementState animatorReplacement)
        {
            animatorReplacement ??= new AnimatorReplacementState();

            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = animatorReplacement.hasPrefabAnimator && animatorReplacement.hasHumanoidAvatar
                ? originalColor
                : new Color(1f, 0.9f, 0.7f, 0.5f);

            using (new EditorGUILayout.VerticalScope(helpBoxPadding8_6))
            {
                GUI.backgroundColor = originalColor;

                using (new EditorGUI.DisabledScope(!animatorReplacement.hasPrefabAnimator || !animatorReplacement.hasHumanoidAvatar))
                {
                    string label = string.IsNullOrEmpty(animatorReplacement.fbxAvatarName)
                        ? "Primary Animator Rig"
                        : $"Primary Animator Rig ({animatorReplacement.fbxAvatarName})";
                    animatorReplacement.selected = EditorGUILayout.ToggleLeft(label, animatorReplacement.selected, EditorStyles.boldLabel);
                }

                GUIContent statusIcon = animatorReplacement.hasPrefabAnimator && animatorReplacement.hasHumanoidAvatar
                    ? EditorGUIUtility.IconContent("TestPassed")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                string matchText;
                if (!animatorReplacement.hasPrefabAnimator)
                {
                    matchText = "No primary Animator found on the prefab";
                }
                else if (!animatorReplacement.hasHumanoidAvatar)
                {
                    matchText = "No humanoid FBX avatar found";
                }
                else
                {
                    matchText = "Ready to replace the primary humanoid rig";
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(statusIcon, GUILayout.Width(18f), GUILayout.Height(16f));
                    EditorGUILayout.LabelField(matchText, PawlygonEditorUI.RichMiniLabelStyle);
                }

            }

            EditorGUILayout.Space(2f);
        }

        private void DrawMeshSelectionRow(MeshSelectionState meshSelection)
        {
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = meshSelection.hasMatch ? originalColor : new Color(1f, 0.9f, 0.7f, 0.5f);

            using (new EditorGUILayout.VerticalScope(helpBoxPadding8_6))
            {
                GUI.backgroundColor = originalColor;

                using (new EditorGUI.DisabledScope(!meshSelection.hasMatch))
                {
                    string meshLabel = string.IsNullOrEmpty(meshSelection.fbxMeshName)
                        ? meshSelection.fbxObjectName
                        : $"{meshSelection.fbxObjectName} ({meshSelection.fbxMeshName})";
                    meshSelection.selected = EditorGUILayout.ToggleLeft(meshLabel, meshSelection.selected, EditorStyles.boldLabel);
                }

                EditorGUILayout.LabelField($"FBX: {meshSelection.fbxRelativePath}", PawlygonEditorUI.RichMiniLabelStyle);

                GUIContent statusIcon = meshSelection.hasMatch
                    ? EditorGUIUtility.IconContent("TestPassed")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                string matchText = meshSelection.hasMatch
                    ? $"<b>Prefab:</b> {meshSelection.prefabRelativePath} ({meshSelection.prefabMeshName}) [{meshSelection.matchReason}]"
                    : "<b>Prefab:</b> <color=#c27725>No matching skinned mesh renderer found</color>";

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(statusIcon, GUILayout.Width(18f), GUILayout.Height(16f));
                    EditorGUILayout.LabelField(matchText, PawlygonEditorUI.RichMiniLabelStyle);
                }

                if (meshSelection.hasMatch && meshSelection.bonesDiffer)
                {
                    DrawBoneStatus(meshSelection);
                }

                if (HasMissingUnifiedBlendshapesWarning(meshSelection))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(16f));
                        EditorGUILayout.LabelField("Missing Unified Expression Blendshapes", PawlygonEditorUI.RichMiniLabelStyle);
                    }

                    meshSelection.showUnifiedBlendshapeWarningDetails = EditorGUILayout.Foldout(
                        meshSelection.showUnifiedBlendshapeWarningDetails,
                        $"Show missing blendshapes ({meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length})",
                        true);

                    if (meshSelection.showUnifiedBlendshapeWarningDetails)
                    {
                        using (new EditorGUILayout.VerticalScope(helpBoxPadding10_6))
                        {
                            foreach (string blendshapeName in meshSelection.missingRequiredUnifiedBlendshapesOnFbx)
                            {
                                EditorGUILayout.LabelField($"- {blendshapeName}", PawlygonEditorUI.RichMiniLabelStyle);
                            }
                        }
                    }
                }
                else if (HasCompleteUnifiedBlendshapesInfo(meshSelection))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("TestPassed"), GUILayout.Width(18f), GUILayout.Height(16f));
                        EditorGUILayout.LabelField("All Unified Expression Blendshapes found", PawlygonEditorUI.RichMiniLabelStyle);
                    }
                }
            }

            EditorGUILayout.Space(2f);
        }

        private static void DrawBoneStatus(MeshSelectionState meshSelection)
        {
            if (meshSelection.bonesUnresolved)
            {
                EditorGUILayout.HelpBox(
                    $"The FBX mesh's bones differ from the prefab's and cannot be remapped: {meshSelection.boneIssue}. " +
                    "Replacing this mesh would keep the prefab's bones and skin it incorrectly, so it is not selected by default " +
                    "(selecting it asks for confirmation when applying).",
                    MessageType.Warning);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(EditorGUIUtility.IconContent("console.infoicon.sml"), GUILayout.Width(18f), GUILayout.Height(16f));
                EditorGUILayout.LabelField("Bones differ from the prefab; they will be remapped by name onto the prefab's armature.", PawlygonEditorUI.RichMiniLabelStyle);
            }
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
            if (entry == null)
            {
                return false;
            }

            return entry.meshSelections.Any(selection => selection.selected && selection.hasMatch) ||
                   (entry.animatorReplacement != null &&
                    entry.animatorReplacement.selected &&
                    entry.animatorReplacement.hasPrefabAnimator &&
                    entry.animatorReplacement.hasHumanoidAvatar);
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

        private static string GetAnimatorSelectionSummary(AnimatorReplacementState animatorReplacement)
        {
            if (animatorReplacement == null || !animatorReplacement.hasPrefabAnimator)
            {
                return "unavailable";
            }

            if (!animatorReplacement.hasHumanoidAvatar)
            {
                return "no humanoid FBX avatar found";
            }

            return animatorReplacement.selected
                ? $"selected ({animatorReplacement.fbxAvatarName})"
                : $"available ({animatorReplacement.fbxAvatarName})";
        }

        private static string BuildReplacementStatusMessage(AvatarEntry entry, int replacedMeshCount, bool replacedAnimator)
        {
            string displayName = GetEntryDisplayName(entry);

            if (replacedMeshCount > 0 && replacedAnimator)
            {
                return $"Updated {replacedMeshCount} mesh reference(s) and the primary Animator rig on '{displayName}'.";
            }

            if (replacedMeshCount > 0)
            {
                return $"Updated {replacedMeshCount} mesh reference(s) on '{displayName}'.";
            }

            if (replacedAnimator)
            {
                return $"Updated the primary Animator rig on '{displayName}'.";
            }

            return $"No mapped skinned mesh renderers or humanoid rig were updated on '{displayName}'.";
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
