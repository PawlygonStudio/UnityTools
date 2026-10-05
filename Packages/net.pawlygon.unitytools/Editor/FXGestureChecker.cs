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

                EditorGUI.BeginChangeCheck();
                selectedAvatar = (GameObject)EditorGUILayout.ObjectField("Selected Avatar", selectedAvatar, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    // The results (and cached descriptor) belong to the previously analyzed avatar.
                    // Drop them so an apply can never write into another avatar's controller.
                    ClearAnalysis();
                    GUIUtility.ExitGUI();
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

                    // The results sections below change shape; restart the event so IMGUI
                    // re-lays out instead of reporting control position errors.
                    GUIUtility.ExitGUI();
                }
            }
        }

        // =====================================================================
        // Drawing: Copy options
        // =====================================================================

        private void DrawCopyOptions()
        {
            EditorGUI.BeginChangeCheck();
            workOnCopy = EditorGUILayout.ToggleLeft(
                "Work on a copy (keeps the original FX controller unchanged)",
                workOnCopy);
            if (EditorGUI.EndChangeCheck())
            {
                // Toggling adds/removes the output folder row and the apply sections' help boxes
                GUIUtility.ExitGUI();
            }

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
                            string projectPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
                            selected = selected.Replace('\\', '/').TrimEnd('/');

                            // Must be Assets itself or a folder inside it (not a sibling such as
                            // ".../AssetsBackup", which a plain prefix check would accept).
                            bool isAssetsFolder = string.Equals(selected, projectPath, StringComparison.OrdinalIgnoreCase);
                            bool isInsideAssets = selected.StartsWith(projectPath + "/", StringComparison.OrdinalIgnoreCase);

                            if (isAssetsFolder || isInsideAssets)
                            {
                                copyOutputFolder = "Assets" + selected.Substring(projectPath.Length);
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Invalid Folder",
                                    "The selected folder must be inside the project's Assets folder.", "OK");
                            }
                        }

                        // The modal folder panel invalidates the current layout group
                        GUIUtility.ExitGUI();
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

            FXGestureCheckerUI.DrawGestureLayers(layers, expandedLayers);
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
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button("Deselect All", GUILayout.Height(26f), GUILayout.Width(120f)))
                {
                    FXGestureCheckerCore.DeselectAll(layers);
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space(4f);

            string readOnlyReason = GetReadOnlyBlockReason();
            string parameterError = FXGestureCheckerCore.GetLayerGuardParameterError(fxController);

            using (new EditorGUI.DisabledScope(!anySelected || readOnlyReason != null || parameterError != null))
            {
                string buttonLabel = workOnCopy
                    ? "Copy FX Controller & Apply Selected Fixes"
                    : "Apply Selected Fixes";

                if (PawlygonEditorUI.DrawPrimaryButton(buttonLabel))
                {
                    ApplySelectedFixes();
                    GUIUtility.ExitGUI();
                }
            }

            if (readOnlyReason != null)
            {
                EditorGUILayout.HelpBox(readOnlyReason, MessageType.Error);
            }

            if (parameterError != null)
            {
                EditorGUILayout.HelpBox(parameterError, MessageType.Error);
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
            FXGestureCheckerUI.DrawBlinkSection(blinkLayers, expandedBlinkLayers, ref showAllBlinkLayers);
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
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button("Deselect All", GUILayout.Height(26f), GUILayout.Width(120f)))
                {
                    FXGestureCheckerCore.DeselectAllBlink(blinkLayers);
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space(4f);

            string readOnlyReason = GetReadOnlyBlockReason();
            string parameterError = FXGestureCheckerCore.GetBlinkGuardParameterError(fxController);

            using (new EditorGUI.DisabledScope(!anySelected || readOnlyReason != null || parameterError != null))
            {
                string buttonLabel = workOnCopy
                    ? "Copy FX Controller & Apply Blink Guards"
                    : "Apply Blink Guards";

                if (PawlygonEditorUI.DrawPrimaryButton(buttonLabel))
                {
                    ApplySelectedBlinkGuards();
                    GUIUtility.ExitGUI();
                }
            }

            if (readOnlyReason != null)
            {
                EditorGUILayout.HelpBox(readOnlyReason, MessageType.Error);
            }

            if (parameterError != null)
            {
                EditorGUILayout.HelpBox(parameterError, MessageType.Error);
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

        /// <summary>
        /// Analyzes the FX controller of the avatar currently in the "Selected Avatar" field and
        /// caches its VRCAvatarDescriptor for later copy assignment.
        /// </summary>
        private void AnalyzeFXController()
        {
            ClearAnalysis();

            FXGestureCheckerCore.AnalysisResult result = FXGestureCheckerCore.Analyze(selectedAvatar);
            cachedDescriptor = result.Descriptor;
            cachedDescriptorTypeInstance = result.DescriptorType;
            ShowAnalysisResult(result);
        }

        /// <summary>
        /// Re-analyzes the controller that was just modified, directly (not through the avatar
        /// field, which may no longer point at the avatar that owns it). Keeps the cached descriptor.
        /// </summary>
        private void RefreshAnalysis()
        {
            if (fxController == null) return;

            ShowAnalysisResult(FXGestureCheckerCore.Analyze(fxController));
        }

        /// <summary>
        /// Stores an analysis result in the window state: controller, gesture and blink layers
        /// (blink layers sorted by confidence), status message, and default foldout expansion.
        /// </summary>
        private void ShowAnalysisResult(FXGestureCheckerCore.AnalysisResult result)
        {
            fxController = result.FXController;
            layers = result.Layers;
            blinkLayers = result.BlinkLayers;
            statusMessage = result.StatusMessage;
            statusMessageType = result.StatusMessageType;
            expandedLayers.Clear();
            expandedBlinkLayers.Clear();

            if (!result.Success) return;

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

        /// <summary>
        /// Forgets all analysis results and the cached descriptor (e.g. when the avatar changes).
        /// </summary>
        private void ClearAnalysis()
        {
            layers = null;
            blinkLayers = null;
            showAllBlinkLayers = false;
            fxController = null;
            cachedDescriptor = null;
            cachedDescriptorTypeInstance = null;
            statusMessage = null;
            expandedLayers.Clear();
            expandedBlinkLayers.Clear();
        }

        /// <summary>
        /// Returns why the analyzed controller cannot be modified right now, or null if it can.
        /// The controller is blocked when it lives in an immutable package and "Work on a copy" is off.
        /// Evaluated on every draw and again at apply time, so toggling the copy option after the
        /// analysis is always respected.
        /// </summary>
        private string GetReadOnlyBlockReason()
        {
            if (workOnCopy || fxController == null) return null;
            if (!FXGestureCheckerCore.IsControllerReadOnly(fxController)) return null;

            return $"FX controller '{fxController.name}' is inside a read-only package and cannot be modified in place. " +
                   "Enable 'Work on a copy' to apply the fixes to an editable copy.";
        }

        // =====================================================================
        // Applying fixes
        // =====================================================================

        /// <summary>
        /// Validates that an analyzed, editable controller is available before applying, and that
        /// its guard parameter can express the guard (checked before any copy is made).
        /// </summary>
        /// <param name="parameterError">The guard parameter error for the guards being applied, or null.</param>
        private bool CanApply(string parameterError)
        {
            if (fxController == null)
            {
                SetStatus("No FX controller loaded. Run analysis first.", MessageType.Error);
                return false;
            }

            if (parameterError != null)
            {
                SetStatus(parameterError, MessageType.Error);
                return false;
            }

            string readOnlyReason = GetReadOnlyBlockReason();
            if (readOnlyReason != null)
            {
                SetStatus(readOnlyReason, MessageType.Error);
                return false;
            }

            return true;
        }

        private void ApplySelectedFixes()
        {
            if (!CanApply(FXGestureCheckerCore.GetLayerGuardParameterError(fxController))) return;

            // --- Copy mode: duplicate the controller and switch to the copy ---
            if (workOnCopy && !SwitchToCopy()) return;

            if (layers == null)
            {
                SetStatus("No gesture analysis is available for the FX controller. Run analysis again.", MessageType.Error);
                return;
            }

            AnimatorController target = fxController;
            var (transitionFixes, layerFixes) = FXGestureCheckerCore.ApplySelectedFixes(target, layers);

            // Re-analyze the modified controller to refresh state
            RefreshAnalysis();
            SetStatus($"Applied {transitionFixes} transition guard(s) and {layerFixes} layer guard(s) to '{target.name}'.", MessageType.Info);
        }

        // =====================================================================
        // Applying blink guards
        // =====================================================================

        private void ApplySelectedBlinkGuards()
        {
            if (!CanApply(FXGestureCheckerCore.GetBlinkGuardParameterError(fxController))) return;

            // --- Copy mode: duplicate the controller and switch to the copy ---
            if (workOnCopy && !SwitchToCopy()) return;

            if (blinkLayers == null)
            {
                SetStatus("No blink layer analysis is available for the FX controller. Run analysis again.", MessageType.Error);
                return;
            }

            AnimatorController target = fxController;
            int guardCount = FXGestureCheckerCore.ApplySelectedBlinkGuards(target, blinkLayers);

            // Re-analyze the modified controller to refresh state
            RefreshAnalysis();
            SetStatus($"Applied {guardCount} blink guard(s) to '{target.name}'.", MessageType.Info);
        }

        // =====================================================================
        // Copy mode
        // =====================================================================

        /// <summary>
        /// Copies the analyzed FX controller, assigns the copy to the analyzed avatar's descriptor,
        /// and analyzes the copy directly so later fixes target the copy's sub-assets. The user's
        /// gesture and blink selections are carried over exactly. Never re-reads the avatar field,
        /// so a different avatar selected in the meantime cannot be modified.
        /// </summary>
        /// <returns>True when the window now points at an analyzed copy; false (with a status
        /// message) on failure.</returns>
        private bool SwitchToCopy()
        {
            if (cachedDescriptor == null)
            {
                SetStatus("The analyzed avatar's VRCAvatarDescriptor is no longer available. Run analysis again.", MessageType.Error);
                return false;
            }

            // Capture user selections before re-analysis resets them
            var selectedTransitionKeys = new HashSet<string>();
            var selectedLayerIndices = new HashSet<int>();
            var selectedBlinkIndices = new HashSet<int>();

            if (layers != null)
            {
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
                            selectedTransitionKeys.Add(GetTransitionKey(layer, t));
                        }
                    }
                }
            }

            if (blinkLayers != null)
            {
                foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in blinkLayers)
                {
                    if (bl.SelectedForGuard)
                    {
                        selectedBlinkIndices.Add(bl.LayerIndex);
                    }
                }
            }

            AnimatorController copy = FXGestureCheckerCore.CopyFXController(fxController, copyOutputFolder, out string errorMessage);
            if (copy == null)
            {
                SetStatus(errorMessage ?? "Failed to copy FX controller.", MessageType.Error);
                return false;
            }

            // Assign the copy to the VRCAvatarDescriptor
            if (!FXGestureCheckerCore.AssignFXControllerToDescriptor(cachedDescriptor, cachedDescriptorTypeInstance, copy))
            {
                SetStatus("Failed to assign the copied FX controller to the VRCAvatarDescriptor.", MessageType.Error);
                return false;
            }

            // Analyze the copy itself so transition references point to the new asset
            FXGestureCheckerCore.AnalysisResult copyResult = FXGestureCheckerCore.Analyze(copy);
            ShowAnalysisResult(copyResult);

            if (!copyResult.Success)
            {
                SetStatus("Failed to analyze the copied FX controller.", MessageType.Error);
                return false;
            }

            // Restore user selections exactly (including deselected, auto-selected entries)
            if (layers != null)
            {
                foreach (FXGestureCheckerCore.LayerAnalysis layer in layers)
                {
                    layer.SelectedForLayerDisable = selectedLayerIndices.Contains(layer.LayerIndex);

                    foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                    {
                        t.SelectedForFix = selectedTransitionKeys.Contains(GetTransitionKey(layer, t));
                    }
                }
            }

            if (blinkLayers != null)
            {
                foreach (FXGestureCheckerCore.BlinkLayerAnalysis bl in blinkLayers)
                {
                    bl.SelectedForGuard = selectedBlinkIndices.Contains(bl.LayerIndex);
                }
            }

            return true;
        }

        /// <summary>
        /// Key that matches a transition between the original controller and its copy. Uses the
        /// core's per-transition key, which stays unique when two transitions share the same
        /// source, destination and gesture parameter.
        /// </summary>
        private static string GetTransitionKey(FXGestureCheckerCore.LayerAnalysis layer, FXGestureCheckerCore.TransitionAnalysis t)
        {
            return t.Key ?? $"{layer.LayerIndex}:{t.SourceName}->{t.DestinationName}:{t.GestureParameter}";
        }

        // =====================================================================
        // UI utilities
        // =====================================================================

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusMessageType = type;
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
