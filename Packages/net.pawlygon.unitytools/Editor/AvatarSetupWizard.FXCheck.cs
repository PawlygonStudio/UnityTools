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
        // =====================================================================
        // FX Gesture Check step
        // =====================================================================

        private bool fxCheckAnalyzed;
        private readonly HashSet<int> fxExpandedLayers = new HashSet<int>();
        private readonly HashSet<int> fxExpandedBlinkLayers = new HashSet<int>();
        private bool fxShowAllBlinkLayers;

        private void OnEnterFXCheck()
        {
            SelectFirstEntry(entry => !entry.fxCheckComplete);
        }

        private void ResetFXCheckState()
        {
            fxCheckAnalyzed = false;
            fxExpandedLayers.Clear();
            fxExpandedBlinkLayers.Clear();
            fxShowAllBlinkLayers = false;
        }

        private static void ResetEntryFXState(AvatarEntry entry)
        {
            entry.fxAnalysisResult = null;
            entry.fxCheckComplete = false;
        }

        private static bool IsFxComplete(AvatarEntry entry)
        {
            return entry.fxCheckComplete;
        }

        private (string Text, PawlygonEditorUI.BadgeKind Kind, string Tooltip) GetFxBadge(AvatarEntry entry)
        {
            if (!entry.fxCheckComplete) return ("Pending", PawlygonEditorUI.BadgeKind.Info, "Not checked yet.");
            return string.IsNullOrEmpty(entry.copiedFxControllerPath)
                ? ("Unchanged", PawlygonEditorUI.BadgeKind.Neutral, "The FX controller was left as it is.")
                : ("Fixed", PawlygonEditorUI.BadgeKind.Ok, "The prefab uses a guarded copy of its FX controller.");
        }

        private void DrawFXCheckActions()
        {
            DrawBackButton(WizardStep.Prefabs);
        }

        private void DrawFXCheckStep()
        {
            PawlygonEditorUI.DrawSection(
                "FX Gesture Check",
                "Analyze and guard gesture-based facial expression transitions and eye-blink layers in the FX controller.",
                () =>
                {
                    // Check VRChat SDK availability
                    if (PawlygonEditorUtils.FindVRCAvatarDescriptorType() == null)
                    {
                        EditorGUILayout.HelpBox(
                            "VRChat SDK not detected. Skipping FX gesture check.",
                            MessageType.Info);
                        EditorGUILayout.Space(SectionSpacing);
                        if (PawlygonEditorUI.DrawPrimaryButton("Continue to Finish", 34f))
                        {
                            GoToStep(WizardStep.Complete);
                            GUIUtility.ExitGUI();
                        }
                        return;
                    }

                    // Auto-analyze on first visit
                    if (!fxCheckAnalyzed)
                    {
                        AnalyzeAllEntries();
                        fxCheckAnalyzed = true;
                    }

                    // Entry selection tabs (FX-specific labels)
                    DrawFXEntrySelectionToolbar();
                    EditorGUILayout.Space(SectionSpacing);

                    // Current entry results
                    AvatarEntry entry = avatarEntries[selectedEntryIndex];
                    if (entry.fxAnalysisResult == null)
                    {
                        EditorGUILayout.HelpBox("No analysis result available.", MessageType.Warning);
                    }
                    else if (!entry.fxAnalysisResult.Success)
                    {
                        EditorGUILayout.HelpBox(entry.fxAnalysisResult.StatusMessage,
                            entry.fxAnalysisResult.StatusMessageType);
                    }
                    else
                    {
                        bool hasGestureLayers = entry.fxAnalysisResult.Layers != null &&
                                                entry.fxAnalysisResult.Layers.Count > 0;
                        bool hasBlinkLayers = entry.fxAnalysisResult.BlinkLayers != null &&
                                              entry.fxAnalysisResult.BlinkLayers.Count > 0;

                        if (!hasGestureLayers && !hasBlinkLayers)
                        {
                            EditorGUILayout.HelpBox(
                                "No gesture-based facial expression transitions or blink layers found.",
                                MessageType.Info);
                        }
                        else
                        {
                            if (hasGestureLayers)
                            {
                                DrawFXResultsForEntry(entry);
                            }

                            if (hasBlinkLayers)
                            {
                                EditorGUILayout.Space(SectionSpacing);
                                FXGestureCheckerUI.DrawBlinkSection(
                                    entry.fxAnalysisResult.BlinkLayers, fxExpandedBlinkLayers, ref fxShowAllBlinkLayers);
                            }

                            EditorGUILayout.Space(SectionSpacing);
                            DrawFXApplySectionForEntry(entry);
                        }
                    }

                    // Navigation buttons
                    EditorGUILayout.Space(SectionSpacing);
                    bool allProcessed = avatarEntries.All(e => e.fxCheckComplete);

                    if (!allProcessed)
                    {
                        if (GUILayout.Button("Skip FX Check", GUILayout.Height(28f)))
                        {
                            foreach (AvatarEntry e in avatarEntries) e.fxCheckComplete = true;
                            GoToStep(WizardStep.Complete);
                            GUIUtility.ExitGUI();
                        }
                    }

                    if (allProcessed)
                    {
                        if (PawlygonEditorUI.DrawPrimaryButton("Continue to Finish", 34f))
                        {
                            GoToStep(WizardStep.Complete);
                            GUIUtility.ExitGUI();
                        }
                    }
                });
        }

        private void AnalyzeAllEntries()
        {
            foreach (AvatarEntry entry in avatarEntries)
            {
                // Validate prefab exists
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);
                if (prefabAsset == null)
                {
                    entry.fxAnalysisResult = new FXGestureCheckerCore.AnalysisResult
                    {
                        StatusMessage = $"Could not load prefab at '{entry.copiedPrefabPath}'.",
                        StatusMessageType = MessageType.Error,
                        Success = false
                    };
                    entry.fxCheckComplete = true;
                    continue;
                }

                // Open prefab contents to get a live GameObject for analysis
                GameObject prefabRoot = null;
                try
                {
                    prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                    entry.fxAnalysisResult = FXGestureCheckerCore.Analyze(prefabRoot);
                }
                finally
                {
                    if (prefabRoot != null)
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                // Store original FX controller path for shared controller detection
                if (entry.fxAnalysisResult.FXController != null)
                    entry.originalFxControllerPath = AssetDatabase.GetAssetPath(entry.fxAnalysisResult.FXController);

                // Pre-expand gesture layers so their transitions are visible without a click.
                if (entry.fxAnalysisResult.Layers != null)
                {
                    foreach (FXGestureCheckerCore.LayerAnalysis l in entry.fxAnalysisResult.Layers)
                        fxExpandedLayers.Add(l.LayerIndex);
                }

                // Sort detected blink layers by confidence (most likely first) and pre-expand them.
                if (entry.fxAnalysisResult.BlinkLayers != null && entry.fxAnalysisResult.BlinkLayers.Count > 0)
                {
                    entry.fxAnalysisResult.BlinkLayers.Sort((a, b) => b.ConfidenceScore.CompareTo(a.ConfidenceScore));
                    foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in entry.fxAnalysisResult.BlinkLayers)
                        fxExpandedBlinkLayers.Add(bl.LayerIndex);
                }

                // Auto-skip entries with no actionable results (no gesture layers and no blink layers)
                bool hasGestureLayers = entry.fxAnalysisResult.Layers != null &&
                                        entry.fxAnalysisResult.Layers.Count > 0;
                bool hasBlinkLayers = entry.fxAnalysisResult.BlinkLayers != null &&
                                      entry.fxAnalysisResult.BlinkLayers.Count > 0;
                if (!entry.fxAnalysisResult.Success || (!hasGestureLayers && !hasBlinkLayers))
                {
                    entry.fxCheckComplete = true;
                }
            }
        }

        private void DrawFXEntrySelectionToolbar()
        {
            if (avatarEntries.Count <= 1) return;

            string[] labels = new string[avatarEntries.Count];
            for (int i = 0; i < avatarEntries.Count; i++)
            {
                AvatarEntry entry = avatarEntries[i];
                string name = GetEntryDisplayName(entry);
                if (entry.fxCheckComplete)
                {
                    bool hadFixes = !string.IsNullOrEmpty(entry.copiedFxControllerPath);
                    name += hadFixes ? " [Fixed]" : " [Skipped]";
                }
                labels[i] = name;
            }

            selectedEntryIndex = GUILayout.Toolbar(
                Mathf.Clamp(selectedEntryIndex, 0, labels.Length - 1), labels);
        }

        private void DrawFXResultsForEntry(AvatarEntry entry)
        {
            FXGestureCheckerCore.AnalysisResult result = entry.fxAnalysisResult;

            // Summary
            int totalTransitions = result.Layers.Sum(l => l.GestureTransitions.Count);
            int guardedTransitions = result.Layers.Sum(l =>
                l.GestureTransitions.Count(t => t.HasDisabledGuard));
            EditorGUILayout.HelpBox(
                $"Found {totalTransitions} gesture transition(s) across {result.Layers.Count} layer(s). " +
                $"{guardedTransitions} already guarded.",
                MessageType.Info);
            EditorGUILayout.Space(4f);

            FXGestureCheckerUI.DrawGestureLayers(result.Layers, fxExpandedLayers);
        }

        private void DrawFXApplySectionForEntry(AvatarEntry entry)
        {
            FXGestureCheckerCore.AnalysisResult result = entry.fxAnalysisResult;
            List<FXGestureCheckerCore.LayerAnalysis> layers =
                result.Layers ?? new List<FXGestureCheckerCore.LayerAnalysis>();
            List<FXGestureCheckerCore.BlinkLayerAnalysis> blinkLayers =
                result.BlinkLayers ?? new List<FXGestureCheckerCore.BlinkLayerAnalysis>();

            bool anySelected =
                layers.Any(l => l.SelectedForLayerDisable || l.GestureTransitions.Any(t => t.SelectedForFix)) ||
                blinkLayers.Any(b => b.SelectedForGuard);

            bool allGuarded =
                layers.All(l => l.AlreadyHasLayerGuard && l.GestureTransitions.All(t => t.HasDisabledGuard)) &&
                blinkLayers.All(b => b.AlreadyHasBlinkGuard);

            if (allGuarded)
            {
                EditorGUILayout.HelpBox(
                    "All gesture transitions, layers, and blink layers are already guarded.",
                    MessageType.Info);
                entry.fxCheckComplete = true;
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Select All Unguarded", GUILayout.Height(26f), GUILayout.Width(160f)))
                {
                    FXGestureCheckerCore.SelectAllUnguarded(layers);
                    FXGestureCheckerCore.SelectAllUnguardedBlink(blinkLayers);
                }
                if (GUILayout.Button("Deselect All", GUILayout.Height(26f), GUILayout.Width(120f)))
                {
                    FXGestureCheckerCore.DeselectAll(layers);
                    FXGestureCheckerCore.DeselectAllBlink(blinkLayers);
                }
            }

            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(!anySelected))
            {
                if (PawlygonEditorUI.DrawPrimaryButton("Apply Fixes"))
                {
                    ApplyFXFixesForEntry(entry);
                    // Copies the FX controller and saves prefabs.
                    GUIUtility.ExitGUI();
                }
            }

            if (!anySelected)
            {
                EditorGUILayout.HelpBox(
                    "Select gesture transitions, layers, or blink layers to apply their guards.",
                    MessageType.Info);
            }
        }

        private void ApplyFXFixesForEntry(AvatarEntry entry)
        {
            FXGestureCheckerCore.AnalysisResult result = entry.fxAnalysisResult;
            if (result.FXController == null) return;

            // Capture user selections before re-analysis resets them
            var selectedTransitionKeys = new HashSet<string>();
            var selectedLayerIndices = new HashSet<int>();
            if (result.Layers != null)
            {
                foreach (FXGestureCheckerCore.LayerAnalysis layer in result.Layers)
                {
                    if (layer.SelectedForLayerDisable)
                        selectedLayerIndices.Add(layer.LayerIndex);
                    foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                    {
                        if (t.SelectedForFix)
                            selectedTransitionKeys.Add(t.Key);
                    }
                }
            }

            var selectedBlinkIndices = new HashSet<int>();
            if (result.BlinkLayers != null)
            {
                foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in result.BlinkLayers)
                {
                    if (bl.SelectedForGuard)
                        selectedBlinkIndices.Add(bl.LayerIndex);
                }
            }

            // 1. Copy FX controller to VRChat subfolder
            string vrchatFolder = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "VRChat");
            AnimatorController copy = FXGestureCheckerCore.CopyFXController(
                result.FXController, vrchatFolder, out string copyError);
            if (copy == null)
            {
                status.Error(copyError ?? "Failed to copy FX controller.");
                return;
            }
            entry.copiedFxControllerPath = AssetDatabase.GetAssetPath(copy);

            // 2. Re-analyze on the copy so the analysis references point to the new asset
            FXGestureCheckerCore.AnalysisResult copyResult = FXGestureCheckerCore.Analyze(copy);
            if (!copyResult.Success)
            {
                status.Error("Failed to analyze copied FX controller.");
                return;
            }

            // Restore the user's selections exactly. Analysis pre-selects some items (outdated
            // guard repairs, high-confidence blink layers), so anything the user unticked must be
            // explicitly cleared on the re-analyzed copy, not just left at its default.
            if (copyResult.Layers != null)
            {
                foreach (FXGestureCheckerCore.LayerAnalysis layer in copyResult.Layers)
                {
                    layer.SelectedForLayerDisable = selectedLayerIndices.Contains(layer.LayerIndex);
                    foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                    {
                        // Key is unique per transition and stable across the controller copy.
                        t.SelectedForFix = selectedTransitionKeys.Contains(t.Key);
                    }
                }
            }

            if (copyResult.BlinkLayers != null)
            {
                foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in copyResult.BlinkLayers)
                {
                    bl.SelectedForGuard = selectedBlinkIndices.Contains(bl.LayerIndex);
                }
            }

            // 3. Apply gesture and blink guards on the copy
            var (tFixes, lFixes) = FXGestureCheckerCore.ApplySelectedFixes(
                copy, copyResult.Layers ?? new List<FXGestureCheckerCore.LayerAnalysis>());
            int bFixes = copyResult.BlinkLayers != null
                ? FXGestureCheckerCore.ApplySelectedBlinkGuards(copy, copyResult.BlinkLayers)
                : 0;

            // 4. Assign copy to descriptor — must reopen prefab to get fresh descriptor
            GameObject prefabRoot = null;
            try
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                Type descriptorType = result.DescriptorType;
                if (descriptorType != null)
                {
                    Component descriptor = prefabRoot.GetComponent(descriptorType);
                    if (descriptor != null)
                    {
                        FXGestureCheckerCore.AssignFXControllerToDescriptor(
                            descriptor, descriptorType, copy);
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
            }
            finally
            {
                if (prefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            // 5. Re-analyze to refresh UI (shows [Applied] labels)
            GameObject finalPrefabRoot = null;
            try
            {
                finalPrefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                entry.fxAnalysisResult = FXGestureCheckerCore.Analyze(finalPrefabRoot);
            }
            finally
            {
                if (finalPrefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(finalPrefabRoot);
            }

            entry.fxCheckComplete = true;
            status.Info($"Applied {tFixes} transition guard(s), {lFixes} layer guard(s), and {bFixes} blink guard(s).");

            // Propagate to other entries sharing the same original FX controller
            PropagateSharedFXFixes(entry, copy);
        }

        private void PropagateSharedFXFixes(AvatarEntry sourceEntry, AnimatorController fixedCopy)
        {
            if (string.IsNullOrEmpty(sourceEntry.originalFxControllerPath)) return;

            foreach (AvatarEntry otherEntry in avatarEntries)
            {
                if (otherEntry == sourceEntry) continue;
                if (otherEntry.fxCheckComplete) continue;
                if (string.IsNullOrEmpty(otherEntry.originalFxControllerPath)) continue;

                // Check if they share the same original FX controller
                if (otherEntry.originalFxControllerPath != sourceEntry.originalFxControllerPath) continue;

                // Assign the same fixed copy to this entry's prefab
                otherEntry.copiedFxControllerPath = AssetDatabase.GetAssetPath(fixedCopy);

                GameObject prefabRoot = null;
                try
                {
                    prefabRoot = PrefabUtility.LoadPrefabContents(otherEntry.copiedPrefabPath);
                    Type descriptorType = otherEntry.fxAnalysisResult?.DescriptorType;
                    if (descriptorType != null)
                    {
                        Component descriptor = prefabRoot.GetComponent(descriptorType);
                        if (descriptor != null)
                        {
                            FXGestureCheckerCore.AssignFXControllerToDescriptor(
                                descriptor, descriptorType, fixedCopy);
                        }
                    }
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, otherEntry.copiedPrefabPath);
                }
                finally
                {
                    if (prefabRoot != null)
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                // Re-analyze
                GameObject finalRoot = null;
                try
                {
                    finalRoot = PrefabUtility.LoadPrefabContents(otherEntry.copiedPrefabPath);
                    otherEntry.fxAnalysisResult = FXGestureCheckerCore.Analyze(finalRoot);
                }
                finally
                {
                    if (finalRoot != null)
                        PrefabUtility.UnloadPrefabContents(finalRoot);
                }

                otherEntry.fxCheckComplete = true;
            }
        }
    }
}
