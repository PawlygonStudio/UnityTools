using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for adjusting humanoid eye muscle limit settings on an avatar.
    /// Auto-selects the first VRChat avatar in the scene and reads the current eye muscle values
    /// from the model's <see cref="ModelImporter"/>. Provides sliders to adjust In/Out/Up/Down
    /// for both eyes and a preview to visualize eye rotation at Face Tracking animation values.
    /// Delegates read/write logic to <see cref="EyeMuscleSettingsCore"/>.
    /// </summary>
    public class EyeMuscleSettings : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Eye Muscle Settings";
        private const float SectionSpacing = 10f;
        private const float SliderMin = 0f;
        private const float SliderMax = 50f;

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedAvatar;
        private EyeMuscleSettingsCore.AnalysisResult analysisResult;
        private EyeMuscleSettingsCore.EyeMuscleValues leftEye;
        private EyeMuscleSettingsCore.EyeMuscleValues rightEye;
        private string statusMessage;
        private MessageType statusMessageType;
        private bool hasUnsavedChanges;
        private bool splitLeftRight;

        // --- Preview ---
        private enum PreviewDirection { None, In, Out, Up, Down }
        private PreviewDirection activePreview = PreviewDirection.None;
        private bool isPreviewActive;
        private Animator previewAnimator;
        private Transform leftEyeBone;
        private Transform rightEyeBone;
        private Quaternion leftEyeOriginalRotation;
        private Quaternion rightEyeOriginalRotation;

        // --- Blendshape preview ---
        /// <summary>
        /// A cached reference to a single blendshape on a SkinnedMeshRenderer,
        /// including its original weight so we can restore it after preview.
        /// </summary>
        private struct BlendshapeRef
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public float OriginalWeight;
        }

        /// <summary>
        /// Maps each preview direction to the list of blendshape refs that should be
        /// set to 100 when that direction is active.
        /// </summary>
        private Dictionary<PreviewDirection, List<BlendshapeRef>> blendshapesByDirection;

        // --- Styles ---
        private GUIStyle previewButtonActiveStyle;

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            EyeMuscleSettings window = GetWindow<EyeMuscleSettings>();
            window.titleContent = new GUIContent("Eye Muscle Settings");
            window.minSize = new Vector2(520f, 400f);
        }

        private void OnEnable()
        {
            AutoSelectFirstSceneAvatar();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            StopPreview();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Restore bone rotations before Unity serializes the scene for play mode
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                StopPreview();
            }
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();

            PawlygonEditorUI.DrawHeader(
                "Eye Muscle Settings",
                "Adjust humanoid eye muscle limits for face tracking compatibility.");
            EditorGUILayout.Space(SectionSpacing);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            DrawAvatarSelection();
            EditorGUILayout.Space(SectionSpacing);

            if (analysisResult != null && analysisResult.Success && leftEye != null && rightEye != null)
            {
                DrawMuscleSettings();
                EditorGUILayout.Space(SectionSpacing);
                DrawPreviewSection();
                EditorGUILayout.Space(SectionSpacing);
                DrawApplySection();
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
        // Drawing: Avatar selection
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
                    // Clear results when avatar changes
                    analysisResult = null;
                    leftEye = null;
                    rightEye = null;
                    hasUnsavedChanges = false;
                    splitLeftRight = false;
                    StopPreview();
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("Select an avatar from the scene with a Humanoid rig.", PawlygonEditorUI.SubLabelStyle);

                EditorGUILayout.Space(8f);

                using (new EditorGUI.DisabledScope(selectedAvatar == null))
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Load Eye Muscle Settings", 32f))
                    {
                        LoadSettings();
                    }
                }
            }
        }

        // =====================================================================
        // Drawing: Muscle settings
        // =====================================================================

        private void DrawMuscleSettings()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Eye Muscle Limits", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Adjust the muscle range limits for each eye direction.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                // Split toggle
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    EditorGUI.BeginChangeCheck();
                    splitLeftRight = GUILayout.Toggle(splitLeftRight, " Split Left / Right", GUILayout.Height(24f));
                    if (EditorGUI.EndChangeCheck() && !splitLeftRight)
                    {
                        // Snap right eye to left when disabling split
                        rightEye.In = leftEye.In;
                        rightEye.Out = leftEye.Out;
                        rightEye.Up = leftEye.Up;
                        rightEye.Down = leftEye.Down;
                        hasUnsavedChanges = true;
                    }
                }

                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(8f);

                // Muscle sliders
                // In and Down are stored as negative values internally but
                // displayed as positive magnitudes in the UI for clarity.
                EditorGUI.BeginChangeCheck();

                DrawMuscleRow("In", ref leftEye.In, ref rightEye.In, true);
                DrawMuscleRow("Out", ref leftEye.Out, ref rightEye.Out, false);
                DrawMuscleRow("Up", ref leftEye.Up, ref rightEye.Up, false);
                DrawMuscleRow("Down", ref leftEye.Down, ref rightEye.Down, true);

                if (EditorGUI.EndChangeCheck())
                {
                    hasUnsavedChanges = true;

                    if (isPreviewActive)
                    {
                        UpdatePreview();
                    }
                }
            }
        }

        /// <summary>
        /// Draws a single muscle direction row. In synced mode, a single slider
        /// controls both eyes. In split mode, Left/Right sub-sliders are shown.
        /// When <paramref name="negated"/> is true, the stored value is negative
        /// but the slider displays the positive magnitude.
        /// </summary>
        private void DrawMuscleRow(string label, ref float leftValue, ref float rightValue, bool negated)
        {
            if (splitLeftRight)
            {
                // Split mode: direction label, then Left/Right sub-sliders
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

                using (new EditorGUI.IndentLevelScope())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Left", GUILayout.Width(60f));
                        leftValue = DrawMagnitudeSlider(leftValue, negated);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Right", GUILayout.Width(60f));
                        rightValue = DrawMagnitudeSlider(rightValue, negated);
                    }
                }

                EditorGUILayout.Space(4f);
            }
            else
            {
                // Synced mode: single slider controls both eyes
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(50f));
                    leftValue = DrawMagnitudeSlider(leftValue, negated);
                }

                rightValue = leftValue;

                EditorGUILayout.Space(2f);
            }
        }

        /// <summary>
        /// Draws a slider that shows the magnitude (0..50) of a value.
        /// If <paramref name="negated"/> is true, the stored value is negative
        /// but displayed/edited as positive. Returns the signed value.
        /// </summary>
        private float DrawMagnitudeSlider(float signedValue, bool negated)
        {
            float display = negated ? -signedValue : signedValue;
            display = Mathf.Max(0f, display); // Clamp to non-negative for display
            float newDisplay = EditorGUILayout.Slider(display, SliderMin, SliderMax);
            return negated ? -newDisplay : newDisplay;
        }

        // =====================================================================
        // Drawing: Preview section
        // =====================================================================

        private void DrawPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Preview", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Preview the effective eye rotation with Face Tracking animation values applied " +
                    "to the current muscle limits. Click a direction to see the result in the Scene view.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                if (!CanPreview())
                {
                    EditorGUILayout.HelpBox(
                        "Preview requires the avatar to be in the scene with valid eye bones in the humanoid rig.",
                        MessageType.Info);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();

                    DrawPreviewButton("In", PreviewDirection.In);
                    GUILayout.Space(4f);
                    DrawPreviewButton("Out", PreviewDirection.Out);
                    GUILayout.Space(4f);
                    DrawPreviewButton("Up", PreviewDirection.Up);
                    GUILayout.Space(4f);
                    DrawPreviewButton("Down", PreviewDirection.Down);

                    GUILayout.Space(12f);

                    using (new EditorGUI.DisabledScope(!isPreviewActive))
                    {
                        if (GUILayout.Button("Reset", GUILayout.Height(28f), GUILayout.Width(70f)))
                        {
                            StopPreview();
                        }
                    }

                    GUILayout.FlexibleSpace();
                }

                if (isPreviewActive)
                {
                    EditorGUILayout.Space(4f);

                    string directionLabel = activePreview.ToString();
                    float leftVal = GetMuscleValueForDirection(leftEye, activePreview);
                    float rightVal = GetMuscleValueForDirection(rightEye, activePreview);
                    float ftAnimValue = GetFTAnimValueForDirection(activePreview);
                    EditorGUILayout.HelpBox(
                        $"Previewing: {directionLabel}  (FT Anim Value: {ftAnimValue:F1})\n" +
                        $"Left Eye: {leftVal:F1} x {ftAnimValue:F1} = {leftVal * ftAnimValue:F1} deg  |  " +
                        $"Right Eye: {rightVal:F1} x {ftAnimValue:F1} = {rightVal * ftAnimValue:F1} deg",
                        MessageType.Info);
                }
            }
        }

        private void DrawPreviewButton(string label, PreviewDirection direction)
        {
            bool isActive = isPreviewActive && activePreview == direction;
            GUIStyle style = isActive ? previewButtonActiveStyle : GUI.skin.button;

            if (GUILayout.Button(label, style, GUILayout.Height(28f), GUILayout.Width(60f)))
            {
                if (isActive)
                {
                    StopPreview();
                }
                else
                {
                    StartPreview(direction);
                }
            }
        }

        // =====================================================================
        // Drawing: Apply section
        // =====================================================================

        private void DrawApplySection()
        {
            using (new EditorGUI.DisabledScope(!hasUnsavedChanges))
            {
                if (PawlygonEditorUI.DrawPrimaryButton("Apply Eye Muscle Settings"))
                {
                    ApplySettings();
                }
            }

            if (hasUnsavedChanges)
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.HelpBox(
                    "You have unsaved changes. Click 'Apply Eye Muscle Settings' to write them to the model and reimport.",
                    MessageType.Warning);
            }
        }

        // =====================================================================
        // Load / Apply
        // =====================================================================

        private void LoadSettings()
        {
            StopPreview();
            analysisResult = null;
            leftEye = null;
            rightEye = null;
            statusMessage = null;
            hasUnsavedChanges = false;

            analysisResult = EyeMuscleSettingsCore.Analyze(selectedAvatar);
            statusMessage = analysisResult.StatusMessage;
            statusMessageType = analysisResult.StatusMessageType;

            if (analysisResult.Success)
            {
                leftEye = analysisResult.LeftEye.Clone();
                rightEye = analysisResult.RightEye.Clone();

                // Auto-enable split mode if loaded values differ between eyes
                splitLeftRight = !leftEye.Equals(rightEye);

                CacheEyeBones();
            }
        }

        private void ApplySettings()
        {
            if (analysisResult == null || !analysisResult.Success || analysisResult.Importer == null)
            {
                SetStatus("No model loaded. Load eye muscle settings first.", MessageType.Error);
                return;
            }

            StopPreview();

            bool success = EyeMuscleSettingsCore.ApplyEyeMuscleValues(analysisResult.Importer, leftEye, rightEye);

            if (success)
            {
                hasUnsavedChanges = false;
                SetStatus("Eye muscle settings applied and model reimported successfully.", MessageType.Info);

                // Reload to reflect the reimported values
                analysisResult = EyeMuscleSettingsCore.Analyze(selectedAvatar);
                if (analysisResult.Success)
                {
                    leftEye = analysisResult.LeftEye.Clone();
                    rightEye = analysisResult.RightEye.Clone();
                }
            }
            else
            {
                SetStatus("Failed to apply eye muscle settings. Check the Console for details.", MessageType.Error);
            }
        }

        // =====================================================================
        // Preview
        // =====================================================================

        private bool CanPreview()
        {
            if (selectedAvatar == null) return false;

            Animator animator = selectedAvatar.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return false;

            return animator.GetBoneTransform(HumanBodyBones.LeftEye) != null ||
                   animator.GetBoneTransform(HumanBodyBones.RightEye) != null;
        }

        private void CacheEyeBones()
        {
            if (selectedAvatar == null) return;

            previewAnimator = selectedAvatar.GetComponent<Animator>();
            if (previewAnimator == null || !previewAnimator.avatar.isHuman) return;

            leftEyeBone = previewAnimator.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEyeBone = previewAnimator.GetBoneTransform(HumanBodyBones.RightEye);

            if (leftEyeBone != null) leftEyeOriginalRotation = leftEyeBone.localRotation;
            if (rightEyeBone != null) rightEyeOriginalRotation = rightEyeBone.localRotation;

            CacheBlendshapes();
        }

        // =====================================================================
        // Blendshape caching
        // =====================================================================

        /// <summary>
        /// Blendshape name patterns for each direction.
        /// Split names are per-eye (e.g. "lookupleft"), combined are shared (e.g. "lookup").
        /// All matching is done lowercase to handle casing variations.
        /// We also handle known misspellings like "eyelood" for "eyelookd".
        /// </summary>
        private static readonly string[][] SplitPatterns =
        {
            // Index maps to PreviewDirection enum: 0=None(unused), 1=In, 2=Out, 3=Up, 4=Down
            null, // None
            new[] { "lookinleft", "lookinright", "loodinleft", "loodinright" },   // In
            new[] { "lookoutleft", "lookoutright", "loodoutleft", "loodoutright" }, // Out
            new[] { "lookupleft", "lookupright", "loodupleft", "loodupright" },     // Up
            new[] { "lookdownleft", "lookdownright", "looddownleft", "looddownright" }, // Down
        };

        private static readonly string[][] CombinedPatterns =
        {
            null, // None
            new[] { "lookin", "loodin" },     // In
            new[] { "lookout", "loodout" },   // Out
            new[] { "lookup", "loodup" },     // Up
            new[] { "lookdown", "looddown" }, // Down
        };

        private void CacheBlendshapes()
        {
            blendshapesByDirection = new Dictionary<PreviewDirection, List<BlendshapeRef>>
            {
                { PreviewDirection.In, new List<BlendshapeRef>() },
                { PreviewDirection.Out, new List<BlendshapeRef>() },
                { PreviewDirection.Up, new List<BlendshapeRef>() },
                { PreviewDirection.Down, new List<BlendshapeRef>() },
            };

            if (selectedAvatar == null) return;

            SkinnedMeshRenderer[] renderers = selectedAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0) return;

            // First pass: check if split (per-eye) blendshapes exist on any renderer
            bool hasSplit = false;
            foreach (SkinnedMeshRenderer smr in renderers)
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i).ToLowerInvariant();
                    // Check if any split pattern matches
                    if (MatchesAnyPattern(name, SplitPatterns[1]) ||
                        MatchesAnyPattern(name, SplitPatterns[2]) ||
                        MatchesAnyPattern(name, SplitPatterns[3]) ||
                        MatchesAnyPattern(name, SplitPatterns[4]))
                    {
                        hasSplit = true;
                        break;
                    }
                }
                if (hasSplit) break;
            }

            string[][] patterns = hasSplit ? SplitPatterns : CombinedPatterns;

            // Second pass: collect matching blendshapes for each direction
            foreach (SkinnedMeshRenderer smr in renderers)
            {
                Mesh mesh = smr.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i).ToLowerInvariant();

                    for (int dir = 1; dir <= 4; dir++)
                    {
                        if (MatchesAnyPattern(name, patterns[dir]))
                        {
                            PreviewDirection direction = (PreviewDirection)dir;
                            blendshapesByDirection[direction].Add(new BlendshapeRef
                            {
                                Renderer = smr,
                                Index = i,
                                OriginalWeight = smr.GetBlendShapeWeight(i)
                            });
                            break; // A blendshape should only match one direction
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a lowercase blendshape name contains any of the given patterns.
        /// Patterns are substrings to match against (already lowercase).
        /// </summary>
        private static bool MatchesAnyPattern(string lowerName, string[] patterns)
        {
            if (patterns == null) return false;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (lowerName.Contains(patterns[i])) return true;
            }
            return false;
        }

        private void StartPreview(PreviewDirection direction)
        {
            if (!CanPreview()) return;

            // Cache original rotations if not yet cached
            if (leftEyeBone == null && rightEyeBone == null)
            {
                CacheEyeBones();
            }

            // Store original rotations before first preview
            if (!isPreviewActive)
            {
                if (leftEyeBone != null) leftEyeOriginalRotation = leftEyeBone.localRotation;
                if (rightEyeBone != null) rightEyeOriginalRotation = rightEyeBone.localRotation;
            }

            isPreviewActive = true;
            activePreview = direction;

            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (!isPreviewActive || leftEye == null || rightEye == null) return;

            string directionName = activePreview.ToString();
            float leftValue = GetMuscleValueForDirection(leftEye, activePreview);
            float rightValue = GetMuscleValueForDirection(rightEye, activePreview);

            Quaternion leftRotation = EyeMuscleSettingsCore.GetEyeRotation(directionName, leftValue, true);
            Quaternion rightRotation = EyeMuscleSettingsCore.GetEyeRotation(directionName, rightValue, false);

            if (leftEyeBone != null)
            {
                leftEyeBone.localRotation = leftEyeOriginalRotation * leftRotation;
            }

            if (rightEyeBone != null)
            {
                rightEyeBone.localRotation = rightEyeOriginalRotation * rightRotation;
            }

            // Apply blendshapes: set active direction's shapes to 100, restore all others
            ApplyBlendshapesForDirection(activePreview);

            SceneView.RepaintAll();
        }

        private void StopPreview()
        {
            bool wasActive = isPreviewActive;
            isPreviewActive = false;
            activePreview = PreviewDirection.None;

            // Always attempt to restore original rotations if we have cached references,
            // even if isPreviewActive was already false (e.g. state was lost during
            // domain reload or play mode transition).
            if (leftEyeBone != null)
            {
                leftEyeBone.localRotation = leftEyeOriginalRotation;
            }

            if (rightEyeBone != null)
            {
                rightEyeBone.localRotation = rightEyeOriginalRotation;
            }

            RestoreAllBlendshapes();

            if (wasActive)
            {
                SceneView.RepaintAll();
            }
        }

        // =====================================================================
        // Blendshape preview helpers
        // =====================================================================

        /// <summary>
        /// Sets blendshapes for the given direction to 100 and restores all other
        /// directions' blendshapes to their original values.
        /// </summary>
        private void ApplyBlendshapesForDirection(PreviewDirection direction)
        {
            if (blendshapesByDirection == null) return;

            foreach (var kvp in blendshapesByDirection)
            {
                bool isActive = kvp.Key == direction;

                foreach (BlendshapeRef bsRef in kvp.Value)
                {
                    if (bsRef.Renderer == null) continue;
                    bsRef.Renderer.SetBlendShapeWeight(bsRef.Index, isActive ? 100f : bsRef.OriginalWeight);
                }
            }
        }

        /// <summary>
        /// Restores all cached blendshapes to their original weights.
        /// </summary>
        private void RestoreAllBlendshapes()
        {
            if (blendshapesByDirection == null) return;

            foreach (var kvp in blendshapesByDirection)
            {
                foreach (BlendshapeRef bsRef in kvp.Value)
                {
                    if (bsRef.Renderer == null) continue;
                    bsRef.Renderer.SetBlendShapeWeight(bsRef.Index, bsRef.OriginalWeight);
                }
            }
        }

        private static float GetMuscleValueForDirection(EyeMuscleSettingsCore.EyeMuscleValues eye, PreviewDirection direction)
        {
            switch (direction)
            {
                case PreviewDirection.In: return eye.In;
                case PreviewDirection.Out: return eye.Out;
                case PreviewDirection.Up: return eye.Up;
                case PreviewDirection.Down: return eye.Down;
                default: return 0f;
            }
        }

        private static float GetFTAnimValueForDirection(PreviewDirection direction)
        {
            switch (direction)
            {
                case PreviewDirection.In: return EyeMuscleSettingsCore.FaceTrackingPreset.In;
                case PreviewDirection.Out: return EyeMuscleSettingsCore.FaceTrackingPreset.Out;
                case PreviewDirection.Up: return EyeMuscleSettingsCore.FaceTrackingPreset.Up;
                case PreviewDirection.Down: return EyeMuscleSettingsCore.FaceTrackingPreset.Down;
                default: return 1f;
            }
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
            if (previewButtonActiveStyle != null) return;

            previewButtonActiveStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold
            };

            previewButtonActiveStyle.normal.textColor = new Color(0.3f, 0.85f, 0.3f);
            previewButtonActiveStyle.hover.textColor = new Color(0.3f, 0.85f, 0.3f);
        }

        private void AutoSelectFirstSceneAvatar()
        {
            if (selectedAvatar != null) return;

            selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
        }
    }
}
