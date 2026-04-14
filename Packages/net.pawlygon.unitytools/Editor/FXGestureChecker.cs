using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window that analyzes the FX AnimatorController on a VRCAvatarDescriptor,
    /// identifies gesture-based facial expression transitions (GestureLeft/GestureRight),
    /// and allows applying a FacialExpressionsDisabled guard to prevent them from triggering.
    /// Delegates all analysis and fix logic to <see cref="FXGestureCheckerCore"/>.
    /// </summary>
    public class FXGestureChecker : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/FX Gesture Checker";
        private const float SectionSpacing = 10f;

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedAvatar;
        private AnimatorController fxController;
        private List<FXGestureCheckerCore.LayerAnalysis> layers;
        private List<FXGestureCheckerCore.BlinkLayerAnalysis> blinkLayers;
        private bool showAllBlinkLayers;
        private string statusMessage;
        private MessageType statusMessageType;
        private readonly HashSet<int> expandedLayers = new HashSet<int>();
        private readonly HashSet<int> expandedBlinkLayers = new HashSet<int>();

        // --- Copy mode ---
        private bool workOnCopy = true;
        private string copyOutputFolder = "Assets";
        private Component cachedDescriptor;
        private Type cachedDescriptorTypeInstance;

        // --- Styles ---
        private GUIStyle layerHeaderStyle;
        private GUIStyle guardedLabelStyle;
        private GUIStyle confidenceHighStyle;
        private GUIStyle confidenceMediumStyle;
        private GUIStyle confidenceLowStyle;

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            FXGestureChecker window = GetWindow<FXGestureChecker>();
            window.titleContent = new GUIContent("FX Gesture Checker");
            window.minSize = new Vector2(520f, 400f);
        }

        private void OnEnable()
        {
            AutoSelectFirstSceneRoot();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();

            PawlygonEditorUI.DrawHeader(
                "FX Gesture Checker",
                "Analyze the FX controller for gesture-based facial expression transitions and apply FacialExpressionsDisabled guards.");
            EditorGUILayout.Space(SectionSpacing);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            DrawAvatarSelection();
            EditorGUILayout.Space(SectionSpacing);

            if (layers != null && layers.Count > 0)
            {
                DrawResults();
                EditorGUILayout.Space(SectionSpacing);
                DrawApplySection();
            }
            else if (layers != null && layers.Count == 0)
            {
                EditorGUILayout.HelpBox("No gesture-based facial expression transitions found in the FX controller.", MessageType.Info);
            }

            // Blink layer detection section
            if (blinkLayers != null && blinkLayers.Count > 0)
            {
                EditorGUILayout.Space(SectionSpacing);
                DrawBlinkResults();
                EditorGUILayout.Space(SectionSpacing);
                DrawBlinkApplySection();
            }
            else if (blinkLayers != null && blinkLayers.Count == 0 && fxController != null)
            {
                EditorGUILayout.Space(SectionSpacing);
                EditorGUILayout.HelpBox("No blink layers detected in the FX controller.", MessageType.Info);
            }

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(statusMessage, statusMessageType);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(8f);
            PawlygonEditorUI.DrawFooter();
        }

        // =====================================================================
        // Drawing: Avatar selection + Analyze button
        // =====================================================================

        private void DrawAvatarSelection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Avatar Selection", EditorStyles.boldLabel);
                EditorGUILayout.Space(4f);

                selectedAvatar = (GameObject)EditorGUILayout.ObjectField("Selected Avatar", selectedAvatar, typeof(GameObject), true);

                if (fxController != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField("FX Controller", fxController, typeof(AnimatorController), false);
                    }
                }

                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(4f);

                DrawCopyOptions();

                EditorGUILayout.Space(4f);

                if (PawlygonEditorUI.DrawPrimaryButton("Analyze FX Controller"))
                {
                    AnalyzeFXController();
                }
            }
        }

        // =====================================================================
        // Drawing: Copy options
        // =====================================================================

        private void DrawCopyOptions()
        {
            workOnCopy = EditorGUILayout.ToggleLeft(
                "Work on a copy (keeps the original FX controller unchanged)",
                workOnCopy);

            if (workOnCopy)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Output Folder");

                    string displayPath = string.IsNullOrEmpty(copyOutputFolder) ? "Assets" : copyOutputFolder;
                    EditorGUILayout.LabelField(displayPath, EditorStyles.textField, GUILayout.ExpandWidth(true));

                    if (GUILayout.Button("Browse", GUILayout.Width(60f)))
                    {
                        string selected = EditorUtility.OpenFolderPanel("Select Output Folder for FX Copy", copyOutputFolder, "");
                        if (!string.IsNullOrEmpty(selected))
                        {
                            string projectPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
                            selected = selected.Replace('\\', '/');

                            if (selected.StartsWith(projectPath))
                            {
                                copyOutputFolder = "Assets" + selected.Substring(projectPath.Length);
                                if (string.IsNullOrEmpty(copyOutputFolder)) copyOutputFolder = "Assets";
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Invalid Folder",
                                    "The selected folder must be inside the project's Assets folder.", "OK");
                            }
                        }
                    }
                }
            }
        }

        // =====================================================================
        // Drawing: Results list
        // =====================================================================

        private void DrawResults()
        {
            EditorGUILayout.LabelField("Analysis Results", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            for (int i = 0; i < layers.Count; i++)
            {
                DrawLayerAnalysis(layers[i], i);
                EditorGUILayout.Space(4f);
            }
        }

        private void DrawLayerAnalysis(FXGestureCheckerCore.LayerAnalysis layer, int displayIndex)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                // Foldout header
                bool isExpanded = expandedLayers.Contains(layer.LayerIndex);
                string gestureCount = $"{layer.GestureTransitions.Count} gesture transition{(layer.GestureTransitions.Count != 1 ? "s" : "")}";

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newExpanded = EditorGUILayout.Foldout(isExpanded, "", true);
                    EditorGUILayout.LabelField($"Layer: {layer.LayerName}", layerHeaderStyle);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"({gestureCount})", EditorStyles.miniLabel, GUILayout.Width(150f));

                    if (newExpanded != isExpanded)
                    {
                        if (newExpanded) expandedLayers.Add(layer.LayerIndex);
                        else expandedLayers.Remove(layer.LayerIndex);
                    }
                }

                if (!isExpanded) return;

                EditorGUI.indentLevel++;

                // Layer-level disable option
                EditorGUILayout.Space(4f);
                if (layer.AlreadyHasLayerGuard)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ToggleLeft("Disable entire layer when FacialExpressionsDisabled", true);
                        }

                        EditorGUILayout.LabelField("[Applied]", guardedLabelStyle, GUILayout.Width(60f));
                    }
                }
                else
                {
                    layer.SelectedForLayerDisable = EditorGUILayout.ToggleLeft(
                        "Disable entire layer when FacialExpressionsDisabled",
                        layer.SelectedForLayerDisable);
                }

                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(4f);

                // Individual transitions
                foreach (FXGestureCheckerCore.TransitionAnalysis transition in layer.GestureTransitions)
                {
                    DrawTransitionRow(transition);
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawTransitionRow(FXGestureCheckerCore.TransitionAnalysis transition)
        {
            string gestureName = FXGestureCheckerCore.GetGestureName(transition.GestureValue);
            string label = $"{transition.SourceName} -> {transition.DestinationName} ({transition.GestureParameter}={gestureName})";

            using (new EditorGUILayout.HorizontalScope())
            {
                if (transition.HasDisabledGuard)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(label, true);
                    }

                    EditorGUILayout.LabelField("[Applied]", guardedLabelStyle, GUILayout.Width(60f));
                }
                else
                {
                    transition.SelectedForFix = EditorGUILayout.ToggleLeft(label, transition.SelectedForFix);
                }
            }
        }

        // =====================================================================
        // Drawing: Apply section
        // =====================================================================

        private void DrawApplySection()
        {
            bool anySelected = layers.Any(l =>
                l.SelectedForLayerDisable ||
                l.GestureTransitions.Any(t => t.SelectedForFix));

            bool allGuarded = layers.All(l =>
                l.AlreadyHasLayerGuard &&
                l.GestureTransitions.All(t => t.HasDisabledGuard));

            if (allGuarded)
            {
                EditorGUILayout.HelpBox("All gesture transitions and layers are already guarded with FacialExpressionsDisabled.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Select All Unguarded", GUILayout.Height(26f), GUILayout.Width(160f)))
                {
                    FXGestureCheckerCore.SelectAllUnguarded(layers);
                }

                if (GUILayout.Button("Deselect All", GUILayout.Height(26f), GUILayout.Width(120f)))
                {
                    FXGestureCheckerCore.DeselectAll(layers);
                }
            }

            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(!anySelected))
            {
                string buttonLabel = workOnCopy
                    ? "Copy FX Controller & Apply Selected Fixes"
                    : "Apply Selected Fixes";

                if (PawlygonEditorUI.DrawPrimaryButton(buttonLabel))
                {
                    ApplySelectedFixes();
                }
            }

            if (workOnCopy && anySelected)
            {
                string folder = string.IsNullOrEmpty(copyOutputFolder) ? "Assets" : copyOutputFolder;
                EditorGUILayout.HelpBox($"A copy of the FX controller will be saved to '{folder}' and assigned to the avatar.", MessageType.Info);
            }

            if (!anySelected)
            {
                EditorGUILayout.HelpBox("Select transitions or layers to apply the FacialExpressionsDisabled guard.", MessageType.Info);
            }
        }

        // =====================================================================
        // Drawing: Blink layer results
        // =====================================================================

        private void DrawBlinkResults()
        {
            EditorGUILayout.LabelField("Blink Layer Detection", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            // Determine which layers to show: by default only the highest confidence tier
            FXGestureCheckerCore.BlinkConfidence topConfidence = blinkLayers[0].Confidence;
            int topCount = blinkLayers.Count(l => l.Confidence == topConfidence);
            int lowerCount = blinkLayers.Count - topCount;

            int visibleCount = showAllBlinkLayers ? blinkLayers.Count : topCount;

            for (int i = 0; i < visibleCount; i++)
            {
                DrawBlinkLayerAnalysis(blinkLayers[i]);
                EditorGUILayout.Space(4f);
            }

            if (lowerCount > 0)
            {
                showAllBlinkLayers = EditorGUILayout.ToggleLeft(
                    $"Show {lowerCount} more layer{(lowerCount != 1 ? "s" : "")} with lower confidence",
                    showAllBlinkLayers);
            }
        }

        private void DrawBlinkLayerAnalysis(FXGestureCheckerCore.BlinkLayerAnalysis blinkLayer)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                bool isExpanded = expandedBlinkLayers.Contains(blinkLayer.LayerIndex);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newExpanded = EditorGUILayout.Foldout(isExpanded, "", true);

                    EditorGUILayout.LabelField($"Layer: {blinkLayer.LayerName}", layerHeaderStyle);
                    GUILayout.FlexibleSpace();

                    // Confidence badge
                    GUIStyle badgeStyle = GetConfidenceStyle(blinkLayer.Confidence);
                    string badgeText = blinkLayer.Confidence.ToString();
                    EditorGUILayout.LabelField(badgeText, badgeStyle, GUILayout.Width(60f));

                    if (newExpanded != isExpanded)
                    {
                        if (newExpanded) expandedBlinkLayers.Add(blinkLayer.LayerIndex);
                        else expandedBlinkLayers.Remove(blinkLayer.LayerIndex);
                    }
                }

                if (!isExpanded) return;

                EditorGUI.indentLevel++;

                // Guard toggle
                EditorGUILayout.Space(4f);
                if (blinkLayer.AlreadyHasBlinkGuard)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ToggleLeft("Disable blink when EyeTrackingActive > 0.5", true);
                        }

                        EditorGUILayout.LabelField("[Applied]", guardedLabelStyle, GUILayout.Width(60f));
                    }
                }
                else
                {
                    blinkLayer.SelectedForGuard = EditorGUILayout.ToggleLeft(
                        "Disable blink when EyeTrackingActive > 0.5",
                        blinkLayer.SelectedForGuard);
                }

                // Detection reasons
                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(4f);

                EditorGUILayout.LabelField("Detection reasons:", EditorStyles.miniLabel);
                foreach (string reason in blinkLayer.DetectionReasons)
                {
                    EditorGUILayout.LabelField($"  \u2022 {reason}", PawlygonEditorUI.RichMiniLabelStyle);
                }

                EditorGUI.indentLevel--;
            }
        }

        private GUIStyle GetConfidenceStyle(FXGestureCheckerCore.BlinkConfidence confidence)
        {
            switch (confidence)
            {
                case FXGestureCheckerCore.BlinkConfidence.High:
                    return confidenceHighStyle;
                case FXGestureCheckerCore.BlinkConfidence.Medium:
                    return confidenceMediumStyle;
                default:
                    return confidenceLowStyle;
            }
        }

        // =====================================================================
        // Drawing: Blink apply section
        // =====================================================================

        private void DrawBlinkApplySection()
        {
            bool anySelected = blinkLayers.Any(l => l.SelectedForGuard);
            bool allGuarded = blinkLayers.All(l => l.AlreadyHasBlinkGuard);

            if (allGuarded)
            {
                EditorGUILayout.HelpBox("All detected blink layers are already guarded with EyeTrackingActive.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Select All Unguarded", GUILayout.Height(26f), GUILayout.Width(160f)))
                {
                    FXGestureCheckerCore.SelectAllUnguardedBlink(blinkLayers);
                }

                if (GUILayout.Button("Deselect All", GUILayout.Height(26f), GUILayout.Width(120f)))
                {
                    FXGestureCheckerCore.DeselectAllBlink(blinkLayers);
                }
            }

            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(!anySelected))
            {
                string buttonLabel = workOnCopy
                    ? "Copy FX Controller & Apply Blink Guards"
                    : "Apply Blink Guards";

                if (PawlygonEditorUI.DrawPrimaryButton(buttonLabel))
                {
                    ApplySelectedBlinkGuards();
                }
            }

            if (workOnCopy && anySelected)
            {
                string folder = string.IsNullOrEmpty(copyOutputFolder) ? "Assets" : copyOutputFolder;
                EditorGUILayout.HelpBox($"A copy of the FX controller will be saved to '{folder}' and assigned to the avatar.", MessageType.Info);
            }

            if (!anySelected)
            {
                EditorGUILayout.HelpBox("Select blink layers to apply the EyeTrackingActive guard.", MessageType.Info);
            }
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        private void AnalyzeFXController()
        {
            layers = null;
            blinkLayers = null;
            showAllBlinkLayers = false;
            fxController = null;
            statusMessage = null;
            expandedLayers.Clear();
            expandedBlinkLayers.Clear();

            FXGestureCheckerCore.AnalysisResult result = FXGestureCheckerCore.Analyze(selectedAvatar);
            fxController = result.FXController;
            layers = result.Layers;
            blinkLayers = result.BlinkLayers;
            cachedDescriptor = result.Descriptor;
            cachedDescriptorTypeInstance = result.DescriptorType;
            statusMessage = result.StatusMessage;
            statusMessageType = result.StatusMessageType;

            if (!result.Success) return;

            // Check if controller asset is editable (skip when working on a copy)
            if (fxController != null)
            {
                string controllerPath = AssetDatabase.GetAssetPath(fxController);
                if (!workOnCopy && !string.IsNullOrEmpty(controllerPath) && controllerPath.StartsWith("Packages/"))
                {
                    SetStatus($"FX controller '{fxController.name}' is inside a read-only Packages folder. Enable 'Work on a copy' or copy it to Assets manually.", MessageType.Error);
                    return;
                }
            }

            if (layers != null)
            {
                foreach (FXGestureCheckerCore.LayerAnalysis l in layers)
                {
                    expandedLayers.Add(l.LayerIndex);
                }
            }

            if (blinkLayers != null)
            {
                // Sort by confidence score descending so the most likely blink layer appears first
                blinkLayers.Sort((a, b) => b.ConfidenceScore.CompareTo(a.ConfidenceScore));

                foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in blinkLayers)
                {
                    expandedBlinkLayers.Add(bl.LayerIndex);
                }
            }
        }

        // =====================================================================
        // Applying fixes
        // =====================================================================

        private void ApplySelectedFixes()
        {
            if (fxController == null)
            {
                SetStatus("No FX controller loaded. Run analysis first.", MessageType.Error);
                return;
            }

            // --- Copy mode: duplicate the controller and switch to the copy ---
            if (workOnCopy)
            {
                // Capture user selections before re-analysis resets them
                var selectedTransitionKeys = new HashSet<string>();
                var selectedLayerIndices = new HashSet<int>();

                foreach (FXGestureCheckerCore.LayerAnalysis layer in layers)
                {
                    if (layer.SelectedForLayerDisable)
                    {
                        selectedLayerIndices.Add(layer.LayerIndex);
                    }

                    foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                    {
                        if (t.SelectedForFix)
                        {
                            selectedTransitionKeys.Add($"{layer.LayerIndex}:{t.SourceName}->{t.DestinationName}:{t.GestureParameter}");
                        }
                    }
                }

                AnimatorController copy = FXGestureCheckerCore.CopyFXController(fxController, copyOutputFolder, out string errorMessage);
                if (copy == null)
                {
                    SetStatus(errorMessage ?? "Failed to copy FX controller.", MessageType.Error);
                    return;
                }

                // Assign the copy to the VRCAvatarDescriptor
                if (!FXGestureCheckerCore.AssignFXControllerToDescriptor(cachedDescriptor, cachedDescriptorTypeInstance, copy))
                {
                    SetStatus("Failed to assign the copied FX controller to the VRCAvatarDescriptor.", MessageType.Error);
                    return;
                }

                fxController = copy;

                // Re-analyze on the copy so transition references point to the new asset
                AnalyzeFXController();

                // Restore user selections on the re-analyzed layers
                if (layers != null)
                {
                    foreach (FXGestureCheckerCore.LayerAnalysis layer in layers)
                    {
                        if (selectedLayerIndices.Contains(layer.LayerIndex))
                        {
                            layer.SelectedForLayerDisable = true;
                        }

                        foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                        {
                            string key = $"{layer.LayerIndex}:{t.SourceName}->{t.DestinationName}:{t.GestureParameter}";
                            if (selectedTransitionKeys.Contains(key))
                            {
                                t.SelectedForFix = true;
                            }
                        }
                    }
                }
            }

            var (transitionFixes, layerFixes) = FXGestureCheckerCore.ApplySelectedFixes(fxController, layers);
            SetStatus($"Applied {transitionFixes} transition guard(s) and {layerFixes} layer guard(s). Re-analyzing...", MessageType.Info);

            // Re-analyze to refresh state
            AnalyzeFXController();
        }

        // =====================================================================
        // Applying blink guards
        // =====================================================================

        private void ApplySelectedBlinkGuards()
        {
            if (fxController == null)
            {
                SetStatus("No FX controller loaded. Run analysis first.", MessageType.Error);
                return;
            }

            // --- Copy mode: duplicate the controller and switch to the copy ---
            if (workOnCopy)
            {
                // Capture user selections before re-analysis resets them
                var selectedBlinkIndices = new HashSet<int>();

                foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in blinkLayers)
                {
                    if (bl.SelectedForGuard)
                    {
                        selectedBlinkIndices.Add(bl.LayerIndex);
                    }
                }

                AnimatorController copy = FXGestureCheckerCore.CopyFXController(fxController, copyOutputFolder, out string errorMessage);
                if (copy == null)
                {
                    SetStatus(errorMessage ?? "Failed to copy FX controller.", MessageType.Error);
                    return;
                }

                if (!FXGestureCheckerCore.AssignFXControllerToDescriptor(cachedDescriptor, cachedDescriptorTypeInstance, copy))
                {
                    SetStatus("Failed to assign the copied FX controller to the VRCAvatarDescriptor.", MessageType.Error);
                    return;
                }

                fxController = copy;

                // Re-analyze on the copy so references point to the new asset
                AnalyzeFXController();

                // Restore user selections on the re-analyzed blink layers
                if (blinkLayers != null)
                {
                    foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in blinkLayers)
                    {
                        if (selectedBlinkIndices.Contains(bl.LayerIndex))
                        {
                            bl.SelectedForGuard = true;
                        }
                    }
                }
            }

            int guardCount = FXGestureCheckerCore.ApplySelectedBlinkGuards(fxController, blinkLayers);
            SetStatus($"Applied {guardCount} blink guard(s). Re-analyzing...", MessageType.Info);

            // Re-analyze to refresh state
            AnalyzeFXController();
        }

        // =====================================================================
        // UI utilities
        // =====================================================================

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusMessageType = type;
        }

        private void EnsureStyles()
        {
            if (layerHeaderStyle != null) return;

            layerHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };

            guardedLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Italic
            };

            guardedLabelStyle.normal.textColor = new Color(0.3f, 0.75f, 0.3f);

            confidenceHighStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight
            };
            confidenceHighStyle.normal.textColor = new Color(0.3f, 0.85f, 0.3f);

            confidenceMediumStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight
            };
            confidenceMediumStyle.normal.textColor = new Color(0.9f, 0.75f, 0.2f);

            confidenceLowStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight
            };
            confidenceLowStyle.normal.textColor = new Color(0.85f, 0.4f, 0.3f);
        }

        private void AutoSelectFirstSceneRoot()
        {
            if (selectedAvatar != null)
            {
                return;
            }

            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                return;
            }

            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            if (rootObjects == null || rootObjects.Length == 0)
            {
                return;
            }

            // Prefer a root object with a VRCAvatarDescriptor
            Type descriptorType = PawlygonEditorUtils.FindVRCAvatarDescriptorType();

            if (descriptorType != null)
            {
                foreach (GameObject root in rootObjects)
                {
                    if (root.GetComponentInChildren(descriptorType, true) != null)
                    {
                        selectedAvatar = root;
                        return;
                    }
                }
            }

            // Fall back to first root with a SkinnedMeshRenderer
            foreach (GameObject root in rootObjects)
            {
                if (root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                {
                    selectedAvatar = root;
                    return;
                }
            }
        }
    }
}
