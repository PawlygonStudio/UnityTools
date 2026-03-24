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
        private const string MenuPath = "!Pawlygon/FX Gesture Checker";
        private const float SectionSpacing = 10f;

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedAvatar;
        private AnimatorController fxController;
        private List<FXGestureCheckerCore.LayerAnalysis> layers;
        private string statusMessage;
        private MessageType statusMessageType;
        private readonly HashSet<int> expandedLayers = new HashSet<int>();

        // --- Copy mode ---
        private bool workOnCopy = true;
        private string copyOutputFolder = "Assets";
        private Component cachedDescriptor;
        private Type cachedDescriptorTypeInstance;

        // --- Styles ---
        private GUIStyle layerHeaderStyle;
        private GUIStyle guardedLabelStyle;

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

                // Auto-populate from scene selection
                GameObject sceneSelection = Selection.activeGameObject;
                if (sceneSelection != null && sceneSelection.scene.IsValid())
                {
                    selectedAvatar = sceneSelection;
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Selected Avatar", selectedAvatar, typeof(GameObject), true);
                }

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
        // Analysis
        // =====================================================================

        private void AnalyzeFXController()
        {
            layers = null;
            fxController = null;
            statusMessage = null;
            expandedLayers.Clear();

            FXGestureCheckerCore.AnalysisResult result = FXGestureCheckerCore.Analyze(selectedAvatar);
            fxController = result.FXController;
            layers = result.Layers;
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
        }
    }
}
