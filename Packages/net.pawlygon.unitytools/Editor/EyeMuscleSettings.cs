using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for adjusting humanoid eye muscle limit settings on an avatar.
    /// Opens on the avatar last used in any Pawlygon tool (or the first one in the scene) and reads its eye
    /// muscle limits from the model's <see cref="ModelImporter"/> straight away. Sliders adjust In/Out/Up/Down
    /// for both eyes (or each eye separately), showing the saved value and Unity's default, and a preview turns
    /// the eyes in the Scene view the way face tracking will. Delegates read/write logic to
    /// <see cref="EyeMuscleSettingsCore"/>.
    /// </summary>
    public class EyeMuscleSettings : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Eye Muscle Settings";
        private const string WindowTitle = "Eye Muscle Settings";
        private const float SectionSpacing = 10f;
        private const float SliderMin = 0f;
        private const float SliderMax = 50f;
        private const float RowLabelWidth = 76f;
        private const float RowButtonWidth = 78f;

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedAvatar;
        private EyeMuscleSettingsCore.AnalysisResult analysisResult;

        /// <summary>The values being edited. Written to the model only by "Apply to Model".</summary>
        private EyeMuscleSettingsCore.EyeMuscleValues leftEye;
        private EyeMuscleSettingsCore.EyeMuscleValues rightEye;

        /// <summary>Unity's built-in limits, what a model uses until its eye limits are customized.</summary>
        private EyeMuscleSettingsCore.EyeMuscleValues defaultLeftEye;
        private EyeMuscleSettingsCore.EyeMuscleValues defaultRightEye;

        private bool splitLeftRight;
        private readonly PawlygonStatus status = new PawlygonStatus();

        /// <summary>Set while this window writes the model, so its own reimport doesn't trigger a reload.</summary>
        private bool isWritingModel;

        /// <summary>
        /// The model's eye limits as they were before this window first applied changes to it in
        /// this session; "Revert" writes them back. Unity cannot undo import settings, so this is
        /// the way back. Only offered while the same model is loaded.
        /// </summary>
        private EyeMuscleSettingsCore.EyeLimitSnapshot revertSnapshot;

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
        private GUIStyle changedLabelStyle;

        private static readonly (PreviewDirection Direction, string Label, string Tooltip)[] Directions =
        {
            (PreviewDirection.In, "In", "How far each eye can turn toward the nose."),
            (PreviewDirection.Out, "Out", "How far each eye can turn away from the nose, toward the ear."),
            (PreviewDirection.Up, "Up", "How far the eyes can look up."),
            (PreviewDirection.Down, "Down", "How far the eyes can look down."),
        };

        private bool IsLoaded => analysisResult != null && analysisResult.Success && leftEye != null && rightEye != null;

        /// <summary>The sliders differ from what is saved in the model.</summary>
        private bool HasUnsavedChanges => IsLoaded &&
            (!leftEye.Equals(analysisResult.LeftEye) || !rightEye.Equals(analysisResult.RightEye));

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath, priority = 40)] // Tools: Tune
        public static void ShowWindow()
        {
            EyeMuscleSettings window = GetWindow<EyeMuscleSettings>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(520f, 460f);
        }

        private void OnEnable()
        {
            AutoSelectFirstSceneAvatar();
            LoadSettings();

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            FBXImportDetector.FbxReimported += OnFbxReimported;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            FBXImportDetector.FbxReimported -= OnFbxReimported;
            StopPreview();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        /// <summary>
        /// The avatar was deleted or its scene closed: open on whichever avatar is in the scene now.
        /// </summary>
        private void OnHierarchyChange()
        {
            if (selectedAvatar == null && analysisResult != null)
            {
                selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
                status.Clear();
                LoadSettings();
            }

            Repaint();
        }

        /// <summary>
        /// The loaded model was reimported outside this window (e.g. its import settings were edited in the
        /// Inspector): re-read the limits, or offer to when that would throw away slider changes.
        /// </summary>
        private void OnFbxReimported(string assetPath)
        {
            if (isWritingModel || !IsLoaded || assetPath != analysisResult.ModelAssetPath) return;

            if (HasUnsavedChanges)
            {
                status.Warning(
                    $"'{System.IO.Path.GetFileName(assetPath)}' was reimported, so the saved values may have changed. Reload to see them (this discards your slider changes).",
                    "Reload", LoadSettings);
            }
            else
            {
                LoadSettings();
            }

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

        /// <summary>
        /// Restores the previewed eye pose before the scene is written to disk, so a save
        /// during a preview never persists the preview rotation or look blendshapes.
        /// </summary>
        private void OnSceneSaving(Scene scene, string path)
        {
            StopPreview();
        }

        /// <summary>
        /// Restores the previewed eye pose before the scene closes, while the cached
        /// bone and renderer references are still valid.
        /// </summary>
        private void OnSceneClosing(Scene scene, bool removingScene)
        {
            StopPreview();
        }

        /// <summary>
        /// Restores the previewed eye pose before a domain reload wipes the cached
        /// original rotations and blendshape weights (they are not serialized).
        /// </summary>
        private void OnBeforeAssemblyReload()
        {
            StopPreview();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();

            // Read once per event: controls drawn after a slider must not appear or disappear in the
            // same event the slider changes a value, or IMGUI's layout gets out of step.
            bool loaded = IsLoaded;
            bool unsaved = HasUnsavedChanges;

            PawlygonEditorUI.DrawHeader(
                WindowTitle,
                "Set how far the eyes can turn, so face tracking looks natural.",
                PawlygonEditorUI.DocumentationUrl);

            if (PawlygonEditorUI.DrawAvatarBar(this, ref selectedAvatar, "Avatar"))
            {
                status.Clear();
                LoadSettings();
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(6f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            if (selectedAvatar == null)
            {
                DrawNoAvatar();
            }
            else if (!loaded)
            {
                DrawLoadProblem();
            }
            else
            {
                DrawModelSection();
                EditorGUILayout.Space(SectionSpacing);
                DrawMuscleSettings();
                EditorGUILayout.Space(SectionSpacing);
                DrawPreviewSection();
            }

            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);
            if (loaded)
            {
                DrawActionBar(unsaved);
            }

            PawlygonEditorUI.DrawFooter();
        }

        // =====================================================================
        // Drawing: Empty states
        // =====================================================================

        private void DrawNoAvatar()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Pick an Avatar",
                    "Choose your avatar above: drag it from the Hierarchy, pick it from the Scene list, or select it and click Use Selection. It needs a Humanoid rig.");
            }
        }

        private void DrawLoadProblem()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader("Can't Read the Eye Limits");

                string message = analysisResult?.StatusMessage ?? "The eye muscle settings could not be read.";
                MessageType type = analysisResult?.StatusMessageType ?? MessageType.Error;
                EditorGUILayout.HelpBox(message, type == MessageType.None ? MessageType.Info : type);

                EditorGUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (PawlygonEditorUI.DrawSecondaryButton("Try Again", 24f, GUILayout.Width(100f)))
                    {
                        LoadSettings();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        // =====================================================================
        // Drawing: Model
        // =====================================================================

        private void DrawModelSection()
        {
            string modelPath = analysisResult.ModelAssetPath;
            string modelName = System.IO.Path.GetFileName(modelPath);
            string readOnlyReason = analysisResult.ReadOnlyReason;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string muted = ColorUtility.ToHtmlStringRGB(PawlygonEditorUI.MutedColor);
                    EditorGUILayout.LabelField(
                        new GUIContent($"Model: <b>{modelName}</b>  <color=#{muted}>— shared by every prefab that uses it</color>", modelPath),
                        PawlygonEditorUI.RichLabelStyle);

                    if (readOnlyReason != null)
                    {
                        PawlygonEditorUI.DrawBadge("Read-only", PawlygonEditorUI.BadgeKind.Error, "This model's import settings can't be changed.");
                    }

                    if (GUILayout.Button(new GUIContent("Ping", "Show the model in the Project window."), EditorStyles.miniButton, GUILayout.Width(44f)))
                    {
                        PawlygonStatus.Ping(AssetDatabase.LoadMainAssetAtPath(modelPath))();
                    }

                    if (GUILayout.Button(new GUIContent("Reload", "Read the eye limits from the model again."), EditorStyles.miniButton, GUILayout.Width(56f)))
                    {
                        ReloadFromModel();
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Eye limits are stored in the model's import settings, not on the avatar, so applying changes them for every prefab and scene avatar made from this model.",
                    PawlygonEditorUI.SubLabelStyle);

                if (readOnlyReason != null)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.HelpBox(readOnlyReason + " You can still try values with the preview.", MessageType.Warning);
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
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Eye Limits", PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();

                    bool newSplit = GUILayout.Toggle(splitLeftRight,
                        new GUIContent(" Separate left / right", "Give each eye its own limits."), GUILayout.ExpandWidth(false));
                    if (newSplit != splitLeftRight)
                    {
                        SetSplit(newSplit);
                        GUIUtility.ExitGUI();
                    }

                    GUILayout.Space(8f);
                    if (GUILayout.Button(new GUIContent("Reset to Unity Defaults",
                            $"Set the sliders to Unity's built-in limits ({DescribeValues(defaultLeftEye)}). Nothing is saved until you apply."),
                            EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    {
                        leftEye = defaultLeftEye.Clone();
                        rightEye = splitLeftRight ? defaultRightEye.Clone() : defaultLeftEye.Clone();
                        OnValuesChanged();
                    }
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "How far the eyes can turn from looking straight ahead, in degrees. In is toward the nose, Out toward the ears. " +
                    "A <b>•</b> marks a value that differs from the one saved in the model.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                bool changed = false;
                foreach (var direction in Directions)
                {
                    if (splitLeftRight)
                    {
                        EditorGUILayout.LabelField(new GUIContent(direction.Label, direction.Tooltip), EditorStyles.boldLabel);
                        changed |= DrawLimitRow(new GUIContent("   Left eye", direction.Tooltip), direction.Direction, true, false);
                        changed |= DrawLimitRow(new GUIContent("   Right eye", direction.Tooltip), direction.Direction, false, true);
                        EditorGUILayout.Space(4f);
                    }
                    else
                    {
                        changed |= DrawLimitRow(new GUIContent(direction.Label, direction.Tooltip), direction.Direction, true, true);
                        EditorGUILayout.Space(2f);
                    }
                }

                if (changed)
                {
                    OnValuesChanged();
                }
            }
        }

        /// <summary>
        /// Draws one limit slider (as a positive number of degrees) for the left eye, the right eye or both,
        /// with buttons to go back to the saved value or to Unity's default. Returns true when a value changed.
        /// </summary>
        private bool DrawLimitRow(GUIContent label, PreviewDirection direction, bool left, bool right)
        {
            EyeMuscleSettingsCore.EyeMuscleValues eye = left ? leftEye : rightEye;
            EyeMuscleSettingsCore.EyeMuscleValues saved = left ? analysisResult.LeftEye : analysisResult.RightEye;
            EyeMuscleSettingsCore.EyeMuscleValues defaults = left ? defaultLeftEye : defaultRightEye;

            float current = GetMuscleValueForDirection(eye, direction);
            float savedValue = GetMuscleValueForDirection(saved, direction);
            float defaultValue = GetMuscleValueForDirection(defaults, direction);

            bool differsFromSaved =
                (left && !Mathf.Approximately(GetMuscleValueForDirection(leftEye, direction), GetMuscleValueForDirection(analysisResult.LeftEye, direction))) ||
                (right && !Mathf.Approximately(GetMuscleValueForDirection(rightEye, direction), GetMuscleValueForDirection(analysisResult.RightEye, direction)));

            float? newValue = null;
            using (new EditorGUILayout.HorizontalScope())
            {
                var rowLabel = new GUIContent(differsFromSaved ? $"{label.text} •" : label.text,
                    differsFromSaved ? $"{label.tooltip} Changed: not applied to the model yet." : label.tooltip);
                EditorGUILayout.LabelField(rowLabel, differsFromSaved ? changedLabelStyle : EditorStyles.label, GUILayout.Width(RowLabelWidth));

                float display = Magnitude(current, direction);
                float newDisplay = EditorGUILayout.Slider(display, SliderMin, SliderMax);
                if (!Mathf.Approximately(newDisplay, display))
                {
                    newValue = FromMagnitude(newDisplay, direction);
                }

                using (new EditorGUI.DisabledScope(!differsFromSaved))
                {
                    var savedContent = new GUIContent($"Saved {Magnitude(savedValue, direction):0.#}°",
                        "Go back to the value saved in the model.");
                    if (GUILayout.Button(savedContent, EditorStyles.miniButton, GUILayout.Width(RowButtonWidth)))
                    {
                        newValue = savedValue;
                    }
                }

                using (new EditorGUI.DisabledScope(Mathf.Approximately(current, defaultValue)))
                {
                    var defaultContent = new GUIContent($"Default {Magnitude(defaultValue, direction):0.#}°",
                        "Use Unity's built-in limit for this direction.");
                    if (GUILayout.Button(defaultContent, EditorStyles.miniButton, GUILayout.Width(RowButtonWidth)))
                    {
                        newValue = defaultValue;
                    }
                }
            }

            if (!newValue.HasValue) return false;

            if (left) SetMuscleValueForDirection(leftEye, direction, newValue.Value);
            if (right) SetMuscleValueForDirection(rightEye, direction, newValue.Value);
            return true;
        }

        /// <summary>
        /// Turns split mode on or off. Turning it off makes both eyes share one set of limits, so when they
        /// differ the user chooses which eye's values to keep instead of the right eye being silently overwritten.
        /// </summary>
        private void SetSplit(bool split)
        {
            if (split || leftEye.Equals(rightEye))
            {
                splitLeftRight = split;
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex(
                "Use the Same Limits for Both Eyes",
                "The eyes have different limits:\n\n" +
                $"Left eye:  {DescribeValues(leftEye)}\n" +
                $"Right eye: {DescribeValues(rightEye)}\n\n" +
                "Which eye's limits should both eyes use?",
                "Use Left Eye's",
                "Keep Separate",
                "Use Right Eye's");

            switch (choice)
            {
                case 0:
                    rightEye = leftEye.Clone();
                    break;
                case 2:
                    leftEye = rightEye.Clone();
                    break;
                default:
                    return;
            }

            splitLeftRight = false;
            OnValuesChanged();
        }

        private void OnValuesChanged()
        {
            if (isPreviewActive)
            {
                UpdatePreview();
            }

            Repaint();
        }

        private static float Magnitude(float signedValue, PreviewDirection direction)
        {
            // In and Down are stored as negative values but shown as positive degrees.
            float display = IsNegated(direction) ? -signedValue : signedValue;
            return Mathf.Max(0f, display);
        }

        private static float FromMagnitude(float magnitude, PreviewDirection direction)
        {
            return IsNegated(direction) ? -magnitude : magnitude;
        }

        private static bool IsNegated(PreviewDirection direction)
        {
            return direction == PreviewDirection.In || direction == PreviewDirection.Down;
        }

        // =====================================================================
        // Drawing: Preview section
        // =====================================================================

        private void DrawPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Preview",
                    "Turns the eyes in the Scene view as far as face tracking will at full input, using the slider values. Nothing is saved; the eyes go back when you stop.");

                if (!CanPreview())
                {
                    EditorGUILayout.HelpBox(
                        "Preview needs the avatar in the scene with eye bones set in its Humanoid rig.",
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
                        if (GUILayout.Button("Stop", GUILayout.Height(28f), GUILayout.Width(70f)))
                        {
                            StopPreview();
                            GUIUtility.ExitGUI();
                        }
                    }

                    GUILayout.FlexibleSpace();
                }

                if (isPreviewActive)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.HelpBox(DescribePreview(), MessageType.Info);
                }
            }
        }

        /// <summary>
        /// Plain-language readout of the previewed direction: how many degrees each eye turns at full face
        /// tracking input (always positive), and how that follows from the limit.
        /// </summary>
        private string DescribePreview()
        {
            float multiplier = Mathf.Abs(GetFTAnimValueForDirection(activePreview));
            float leftLimit = Magnitude(GetMuscleValueForDirection(leftEye, activePreview), activePreview);
            float rightLimit = Magnitude(GetMuscleValueForDirection(rightEye, activePreview), activePreview);

            string where;
            switch (activePreview)
            {
                case PreviewDirection.In: where = "in, toward the nose"; break;
                case PreviewDirection.Out: where = "out, toward the ears"; break;
                case PreviewDirection.Up: where = "up"; break;
                default: where = "down"; break;
            }

            string turn = Mathf.Approximately(leftLimit, rightLimit)
                ? $"both eyes turn {leftLimit * multiplier:0.#}°"
                : $"the left eye turns {leftLimit * multiplier:0.#}° and the right eye {rightLimit * multiplier:0.#}°";

            return $"Looking {where}: {turn}.\n" +
                   $"Face tracking drives this direction to {multiplier:0.#}× the limit at full input.";
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

                // Toggling the preview shows/hides the info box below the buttons.
                GUIUtility.ExitGUI();
            }
        }

        // =====================================================================
        // Drawing: Action bar
        // =====================================================================

        private void DrawActionBar(bool unsaved)
        {
            bool readOnly = analysisResult.ReadOnlyReason != null;

            PawlygonEditorUI.BeginActionBar();

            if (CanRevert())
            {
                var revertContent = new GUIContent("Restore Previous",
                    "Write back the eye limits this model had before you first applied changes in this window, and reimport it.");
                if (PawlygonEditorUI.DrawSecondaryButton(revertContent, 28f))
                {
                    RevertSettings();

                    // Reverting reimports the model and removes this button.
                    GUIUtility.ExitGUI();
                }
            }

            using (new EditorGUI.DisabledScope(!unsaved))
            {
                var discardContent = new GUIContent("Discard Changes", "Set the sliders back to the values saved in the model.");
                if (PawlygonEditorUI.DrawSecondaryButton(discardContent, 28f))
                {
                    DiscardSliderChanges();
                    GUIUtility.ExitGUI();
                }
            }

            GUILayout.FlexibleSpace();

            if (unsaved)
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(6f);
                    PawlygonEditorUI.DrawBadge("Unsaved", PawlygonEditorUI.BadgeKind.Warning, "The sliders differ from the values saved in the model.");
                }
            }

            using (new EditorGUI.DisabledScope(!unsaved || readOnly))
            {
                if (PawlygonEditorUI.DrawPrimaryButton("Apply to Model", 28f, GUILayout.Width(140f)))
                {
                    ApplySettings();

                    // Applying reimports the model and changes what the action bar shows.
                    GUIUtility.ExitGUI();
                }
            }

            PawlygonEditorUI.EndActionBar();
        }

        // =====================================================================
        // Load / Apply
        // =====================================================================

        /// <summary>
        /// Reads the selected avatar's eye limits from its model. Runs on open, when the avatar changes and
        /// after this window writes the model; discards slider changes.
        /// </summary>
        private void LoadSettings()
        {
            StopPreview();
            analysisResult = null;
            leftEye = null;
            rightEye = null;

            if (selectedAvatar == null) return;

            analysisResult = EyeMuscleSettingsCore.Analyze(selectedAvatar);

            if (analysisResult.Success)
            {
                leftEye = analysisResult.LeftEye.Clone();
                rightEye = analysisResult.RightEye.Clone();
                defaultLeftEye = EyeMuscleSettingsCore.GetDefaultEyeMuscleValues(true);
                defaultRightEye = EyeMuscleSettingsCore.GetDefaultEyeMuscleValues(false);

                // Auto-enable split mode if loaded values differ between eyes
                splitLeftRight = !leftEye.Equals(rightEye);

                // Eye bones, their original rotations and the look blendshapes' original
                // weights are captured when a preview starts (see StartPreview), so a restore
                // always returns to the pose the user had right before previewing.
            }
        }

        /// <summary>"Reload": re-reads the model, asking first when that would discard slider changes.</summary>
        private void ReloadFromModel()
        {
            if (HasUnsavedChanges && !EditorUtility.DisplayDialog(
                    "Reload Eye Limits",
                    "Read the eye limits from the model again? Your slider changes that haven't been applied will be lost.",
                    "Reload", "Cancel"))
            {
                return;
            }

            status.Clear();
            LoadSettings();
        }

        private void DiscardSliderChanges()
        {
            if (!IsLoaded) return;

            leftEye = analysisResult.LeftEye.Clone();
            rightEye = analysisResult.RightEye.Clone();
            splitLeftRight = splitLeftRight || !leftEye.Equals(rightEye);
            OnValuesChanged();
        }

        private void ApplySettings()
        {
            if (analysisResult == null || !analysisResult.Success || analysisResult.Importer == null)
            {
                status.Error("No model loaded. Pick an avatar with a Humanoid rig first.");
                return;
            }

            if (analysisResult.ReadOnlyReason != null)
            {
                status.Error(analysisResult.ReadOnlyReason);
                return;
            }

            string modelName = System.IO.Path.GetFileName(analysisResult.ModelAssetPath);
            bool confirmed = EditorUtility.DisplayDialog(
                "Apply Eye Limits to Model",
                $"Write these eye limits to the import settings of '{modelName}' and reimport it?\n\n" +
                $"Left eye:  {DescribeValues(leftEye)}\n" +
                $"Right eye: {DescribeValues(rightEye)}\n\n" +
                "Every prefab and scene avatar made from this model gets the new limits.\n\n" +
                "Unity can't undo import settings. 'Restore Previous' in this window puts back the values " +
                "the model had before your first apply.",
                "Apply and Reimport",
                "Cancel");
            if (!confirmed) return;

            StopPreview();

            // Keep the model's values from before the first apply in this session for Revert
            if (revertSnapshot == null || revertSnapshot.ModelAssetPath != analysisResult.ModelAssetPath)
            {
                revertSnapshot = EyeMuscleSettingsCore.CaptureEyeLimits(analysisResult.Importer);
            }

            UnityEngine.Object model = AssetDatabase.LoadMainAssetAtPath(analysisResult.ModelAssetPath);
            bool success;
            isWritingModel = true;
            try
            {
                success = EyeMuscleSettingsCore.ApplyEyeMuscleValues(analysisResult.Importer, leftEye, rightEye);
            }
            finally
            {
                isWritingModel = false;
            }

            if (success)
            {
                status.Info($"Applied the eye limits to '{modelName}'.", "Ping", PawlygonStatus.Ping(model));
                ReloadAfterReimport();
            }
            else
            {
                status.Error("Couldn't apply the eye limits. Check the Console for details.");
            }
        }

        /// <summary>
        /// True when the loaded model has a snapshot from before this window's first apply.
        /// </summary>
        private bool CanRevert()
        {
            return revertSnapshot != null &&
                   analysisResult != null &&
                   analysisResult.Success &&
                   analysisResult.ReadOnlyReason == null &&
                   revertSnapshot.ModelAssetPath == analysisResult.ModelAssetPath;
        }

        /// <summary>
        /// Writes the snapshot taken before the first apply back to the model (after confirming)
        /// and reimports it. Discards unsaved slider edits.
        /// </summary>
        private void RevertSettings()
        {
            if (!CanRevert() || analysisResult.Importer == null) return;

            string modelName = System.IO.Path.GetFileName(analysisResult.ModelAssetPath);
            bool confirmed = EditorUtility.DisplayDialog(
                "Restore Previous Eye Limits",
                $"Restore the eye limits '{modelName}' had before you first applied changes in this window, " +
                "and reimport it? Every prefab made from this model gets them back." +
                (HasUnsavedChanges ? "\n\nYour slider changes that haven't been applied will be discarded." : string.Empty),
                "Restore and Reimport",
                "Cancel");
            if (!confirmed) return;

            StopPreview();

            UnityEngine.Object model = AssetDatabase.LoadMainAssetAtPath(analysisResult.ModelAssetPath);
            bool success;
            isWritingModel = true;
            try
            {
                success = EyeMuscleSettingsCore.RestoreEyeLimits(analysisResult.Importer, revertSnapshot);
            }
            finally
            {
                isWritingModel = false;
            }

            if (success)
            {
                revertSnapshot = null;
                status.Info($"Restored the previous eye limits of '{modelName}'.", "Ping", PawlygonStatus.Ping(model));
                ReloadAfterReimport();
            }
            else
            {
                status.Error("Couldn't restore the previous eye limits. Check the Console for details.");
            }
        }

        /// <summary>
        /// Re-reads the values from the reimported model so the sliders show what was written.
        /// </summary>
        private void ReloadAfterReimport()
        {
            EyeMuscleSettingsCore.AnalysisResult reloaded = EyeMuscleSettingsCore.Analyze(selectedAvatar);
            if (!reloaded.Success) return;

            analysisResult = reloaded;
            leftEye = reloaded.LeftEye.Clone();
            rightEye = reloaded.RightEye.Clone();
            splitLeftRight = splitLeftRight || !leftEye.Equals(rightEye);
        }

        private static string DescribeValues(EyeMuscleSettingsCore.EyeMuscleValues values)
        {
            // In and Down are stored negative; show magnitudes like the sliders do
            return $"In {-values.In:0.#}°, Out {values.Out:0.#}°, Up {values.Up:0.#}°, Down {-values.Down:0.#}°";
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
            if (previewAnimator == null || previewAnimator.avatar == null || !previewAnimator.avatar.isHuman) return;

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

        /// <summary>
        /// Starts (or switches) the preview to the given direction. When no preview is
        /// running yet, the eye bones' current rotations and the look blendshapes' current
        /// weights are captured first, so <see cref="StopPreview"/> restores exactly the
        /// pose the user had before previewing (including edits made since loading).
        /// </summary>
        private void StartPreview(PreviewDirection direction)
        {
            if (!CanPreview()) return;

            if (!isPreviewActive)
            {
                // Re-resolve the bones and re-capture originals (rotations + blendshape weights).
                leftEyeBone = null;
                rightEyeBone = null;
                CacheEyeBones();
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

            ApplyAvatarSpaceRotation(leftEyeBone, leftEyeOriginalRotation, leftRotation);
            ApplyAvatarSpaceRotation(rightEyeBone, rightEyeOriginalRotation, rightRotation);

            // Apply blendshapes: set active direction's shapes to 100, restore all others
            ApplyBlendshapesForDirection(activePreview);

            SceneView.RepaintAll();
        }

        /// <summary>
        /// Turns an eye bone from its pre-preview pose by <paramref name="avatarSpaceRotation"/>
        /// (pitch about the avatar's right axis, yaw about its up axis). Working in the avatar
        /// root's space instead of the bone's local axes gives the right direction whatever the
        /// bone's orientation (Blender-style rigs often point the bone's local Y forward, which
        /// would turn a local-Y "yaw" into a roll). Only the bone's rotation is written, and
        /// <see cref="StopPreview"/> restores its original local rotation exactly.
        /// </summary>
        private void ApplyAvatarSpaceRotation(Transform eyeBone, Quaternion originalLocalRotation, Quaternion avatarSpaceRotation)
        {
            if (eyeBone == null) return;

            Quaternion avatarRotation = previewAnimator != null ? previewAnimator.transform.rotation : Quaternion.identity;
            Quaternion worldOffset = avatarRotation * avatarSpaceRotation * Quaternion.Inverse(avatarRotation);

            // The pre-preview world rotation, rebuilt from the parent so it stays valid if the
            // head moved since the preview started
            Quaternion originalWorldRotation = eyeBone.parent != null
                ? eyeBone.parent.rotation * originalLocalRotation
                : originalLocalRotation;

            eyeBone.rotation = worldOffset * originalWorldRotation;
        }

        /// <summary>
        /// Ends the preview and restores the eye bone rotations and look blendshape weights
        /// captured when it started. Does nothing when no preview is running, so closing the
        /// window, loading, applying or switching avatars never overwrites changes the user
        /// made to the eyes or blendshapes outside a preview.
        /// </summary>
        private void StopPreview()
        {
            if (!isPreviewActive)
            {
                activePreview = PreviewDirection.None;
                return;
            }

            isPreviewActive = false;
            activePreview = PreviewDirection.None;

            if (leftEyeBone != null)
            {
                leftEyeBone.localRotation = leftEyeOriginalRotation;
            }

            if (rightEyeBone != null)
            {
                rightEyeBone.localRotation = rightEyeOriginalRotation;
            }

            RestoreAllBlendshapes();

            SceneView.RepaintAll();
            Repaint();
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

        private static void SetMuscleValueForDirection(EyeMuscleSettingsCore.EyeMuscleValues eye, PreviewDirection direction, float value)
        {
            switch (direction)
            {
                case PreviewDirection.In: eye.In = value; break;
                case PreviewDirection.Out: eye.Out = value; break;
                case PreviewDirection.Up: eye.Up = value; break;
                case PreviewDirection.Down: eye.Down = value; break;
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

        private void EnsureStyles()
        {
            if (previewButtonActiveStyle != null && changedLabelStyle != null) return;

            previewButtonActiveStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold
            };

            previewButtonActiveStyle.normal.textColor = new Color(0.3f, 0.85f, 0.3f);
            previewButtonActiveStyle.hover.textColor = new Color(0.3f, 0.85f, 0.3f);

            changedLabelStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
            changedLabelStyle.normal.textColor = PawlygonEditorUI.WarningColor;
        }

        private void AutoSelectFirstSceneAvatar()
        {
            if (selectedAvatar != null) return;

            selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
        }
    }
}
