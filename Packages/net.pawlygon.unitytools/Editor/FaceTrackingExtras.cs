using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for generating ear and tail animations driven by face tracking parameters.
    /// Detects the ear and tail bone chains, saves them to a per-avatar <see cref="FTExtrasProfile"/>,
    /// and lets the user author poses on the avatar in the scene (with live ear mirroring) and preview
    /// how they blend. Delegates rig analysis to <see cref="FaceTrackingExtrasCore"/>, pose math to
    /// <see cref="FTExtrasPoses"/> and scene changes to <see cref="FTExtrasPoseSession"/>.
    /// </summary>
    public class FaceTrackingExtras : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Face Tracking Extras";
        private const float SectionSpacing = 10f;

        private const string SlotEarLeft = "EarLeft";
        private const string SlotEarRight = "EarRight";
        private const string SlotTail = "Tail";

        private static readonly (string Slot, string Label, FaceTrackingExtrasCore.ChainKind Kind)[] Slots =
        {
            (SlotEarLeft, "Left Ear", FaceTrackingExtrasCore.ChainKind.Ear),
            (SlotEarRight, "Right Ear", FaceTrackingExtrasCore.ChainKind.Ear),
            (SlotTail, "Tail", FaceTrackingExtrasCore.ChainKind.Tail),
        };

        /// <summary>What the window is currently doing to the avatar's bones.</summary>
        private enum Mode { Idle, Editing, Showing, Previewing }

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedAvatar;
        private FaceTrackingExtrasCore.RigAnalysis analysis;
        private readonly Dictionary<string, FaceTrackingExtrasCore.BoneChain> selectedChains = new Dictionary<string, FaceTrackingExtrasCore.BoneChain>();
        private string statusMessage;
        private MessageType statusMessageType;
        private string lastExportPath;

        // --- Profile & posing ---
        private FTExtrasProfile profile;
        private FTExtrasPoseSession session;
        private string sessionError;
        private Mode mode;
        private FTExtrasPoseId activePose;
        private bool liveMirror;
        private FTExtrasPoses.BlendInputs previewInputs;
        private double loopStartTime;
        private double lastLoopApplyTime;
        private bool showDebug;
        private bool showGenerationSettings;
        private SerializedObject profileObject;
        private FTExtrasGenerator.Result lastResult;

        // --- Styles ---
        private GUIStyle boneButtonStyle;
        private GUIStyle sectionTitleStyle;
        private GUIStyle poseLabelStyle;

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            FaceTrackingExtras window = GetWindow<FaceTrackingExtras>();
            window.titleContent = new GUIContent("Face Tracking Extras");
            window.minSize = new Vector2(520f, 520f);
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;

            if (selectedAvatar == null)
            {
                selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
            }
            LoadAvatar(selectedAvatar);
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            StopMode();
        }

        /// <summary>Loops redraw at most this often.</summary>
        private const double LoopFrameInterval = 1.0 / 60.0;

        private void OnEditorUpdate()
        {
            if (mode == Mode.Idle) return;

            // The session restores itself before play mode and script reloads.
            if (session == null || !session.IsActive)
            {
                mode = Mode.Idle;
                Repaint();
                return;
            }

            if (mode == Mode.Editing && liveMirror && IsEarPose(activePose))
            {
                session.UpdateLiveMirror();
            }

            if (mode == Mode.Previewing && previewInputs.PlayLoops)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - lastLoopApplyTime < LoopFrameInterval) return;

                lastLoopApplyTime = now;
                previewInputs.Time = (float)(now - loopStartTime);
                ApplyPreview();
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
                "Face Tracking Extras",
                "Ear, tail and pupil animations driven by face tracking.");
            EditorGUILayout.Space(SectionSpacing);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            DrawAvatarSelection();

            if (analysis != null && analysis.Success)
            {
                EditorGUILayout.Space(SectionSpacing);
                using (new EditorGUI.DisabledScope(mode != Mode.Idle))
                {
                    DrawRigSection();
                }

                EditorGUILayout.Space(SectionSpacing);
                DrawPoseSection();

                EditorGUILayout.Space(SectionSpacing);
                DrawPreviewSection();

                EditorGUILayout.Space(SectionSpacing);
                DrawGenerateSection();

                EditorGUILayout.Space(SectionSpacing);
                DrawDebugSection();
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

        private void EnsureStyles()
        {
            if (boneButtonStyle != null) return;

            boneButtonStyle = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                padding = new RectOffset(2, 2, 1, 1)
            };

            sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };

            poseLabelStyle = new GUIStyle(EditorStyles.label) { richText = true };
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusMessageType = type;
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

                using (new EditorGUI.DisabledScope(mode != Mode.Idle))
                {
                    EditorGUI.BeginChangeCheck();
                    GameObject newAvatar = (GameObject)EditorGUILayout.ObjectField("Selected Avatar", selectedAvatar, typeof(GameObject), true);
                    if (EditorGUI.EndChangeCheck())
                    {
                        selectedAvatar = newAvatar;
                        LoadAvatar(newAvatar);
                    }

                    EditorGUI.BeginChangeCheck();
                    FTExtrasProfile newProfile = (FTExtrasProfile)EditorGUILayout.ObjectField("Profile", profile, typeof(FTExtrasProfile), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        profile = newProfile;
                        if (profile != null && analysis != null && analysis.Success) ApplyProfileChains();
                        BindSession();
                    }
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    profile == null
                        ? "Select an avatar from the scene with a Humanoid rig. Its profile is created when you save the bone chains."
                        : "The profile stores this avatar's bone chains and poses.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                using (new EditorGUI.DisabledScope(selectedAvatar == null || mode != Mode.Idle))
                {
                    if (PawlygonEditorUI.DrawPrimaryButton(profile == null ? "Detect Ears & Tail" : "Re-detect Ears & Tail", 32f))
                    {
                        RunAnalysis();
                    }
                }
            }
        }

        private void LoadAvatar(GameObject avatar)
        {
            StopMode();
            analysis = null;
            selectedChains.Clear();
            lastExportPath = null;
            statusMessage = null;
            session = null;
            sessionError = null;
            profile = avatar != null ? FindProfile(avatar) : null;

            if (avatar == null) return;

            RunAnalysis();
            if (profile != null && analysis.Success)
            {
                ApplyProfileChains();
                BindSession();
            }
        }

        private void RunAnalysis()
        {
            analysis = FaceTrackingExtrasCore.Analyze(selectedAvatar);
            selectedChains.Clear();
            lastExportPath = null;

            SetStatus(analysis.StatusMessage, analysis.Success ? MessageType.Info : MessageType.Error);

            if (!analysis.Success) return;

            selectedChains[SlotEarLeft] = PickBest(analysis.ChainsOfSide(FaceTrackingExtrasCore.ChainKind.Ear, FaceTrackingExtrasCore.ChainSide.Left));
            selectedChains[SlotEarRight] = PickBest(analysis.ChainsOfSide(FaceTrackingExtrasCore.ChainKind.Ear, FaceTrackingExtrasCore.ChainSide.Right));
            selectedChains[SlotTail] = PickBest(analysis.TailChains);
        }

        /// <summary>
        /// Prefers visible chains over helper rigs, then chains animation can actually move
        /// (fewest constraint-driven bones), then the longest.
        /// </summary>
        private FaceTrackingExtrasCore.BoneChain PickBest(IEnumerable<FaceTrackingExtrasCore.BoneChain> chains)
        {
            return chains
                .OrderBy(c => c.IsHelper)
                .ThenBy(c => c.Bones.Count(b => analysis.ConstraintDrivers.ContainsKey(b)))
                .ThenByDescending(c => c.Bones.Count)
                .FirstOrDefault();
        }

        // =====================================================================
        // Drawing: Rig
        // =====================================================================

        private void DrawRigSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Bone Chains", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Check the detected chains. Pick another candidate or drag a different root bone if a chain is wrong. Click a bone to select it in the scene.",
                    PawlygonEditorUI.SubLabelStyle);

                foreach (var slot in Slots)
                {
                    EditorGUILayout.Space(10f);
                    PawlygonEditorUI.DrawSeparator();
                    EditorGUILayout.Space(6f);
                    DrawSlot(slot.Slot, slot.Label, slot.Kind);
                }

                EditorGUILayout.Space(10f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(8f);
                DrawSaveChains();
            }
        }

        private void DrawSlot(string slot, string label, FaceTrackingExtrasCore.ChainKind kind)
        {
            selectedChains.TryGetValue(slot, out var chain);
            var candidates = kind == FaceTrackingExtrasCore.ChainKind.Ear ? analysis.EarChains : analysis.TailChains;

            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

            // Candidate picker
            if (candidates.Count > 0)
            {
                // A manually picked root is not one of the candidates; show it as "(custom)" in slot 0.
                bool isCustom = chain != null && !candidates.Contains(chain);
                var options = new List<string> { isCustom ? "(custom)" : "(none)" };
                options.AddRange(candidates.Select(c => $"{c.DisplayName} - {c.Side}"));

                int currentIndex = chain != null ? candidates.IndexOf(chain) + 1 : 0;
                int newIndex = EditorGUILayout.Popup("Detected", currentIndex, options.ToArray());
                if (newIndex != currentIndex)
                {
                    chain = newIndex == 0 ? null : candidates[newIndex - 1];
                    selectedChains[slot] = chain;
                }
            }
            else
            {
                EditorGUILayout.LabelField("Detected", "No candidates found by name");
            }

            // Manual root override
            EditorGUI.BeginChangeCheck();
            Transform newRoot = (Transform)EditorGUILayout.ObjectField("Root Bone", chain?.Root, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (newRoot == null)
                {
                    chain = null;
                }
                else if (!newRoot.IsChildOf(selectedAvatar.transform))
                {
                    SetStatus($"'{newRoot.name}' is not part of '{selectedAvatar.name}'.", MessageType.Warning);
                    return;
                }
                else
                {
                    chain = FaceTrackingExtrasCore.BuildChainFromRoot(newRoot, kind, analysis);
                }
                selectedChains[slot] = chain;
            }

            if (chain == null) return;

            EditorGUILayout.Space(4f);
            DrawBoneList(chain);
            DrawChainWarnings(chain, slot);
        }

        private void DrawBoneList(FaceTrackingExtrasCore.BoneChain chain)
        {
            if (chain.SkippedBones.Count > 0)
            {
                EditorGUILayout.LabelField(
                    $"<color=#909090>Skipped (constraint helpers): {string.Join(" → ", chain.SkippedBones.Select(b => b.name))}</color>",
                    PawlygonEditorUI.RichMiniLabelStyle);
            }

            for (int i = 0; i < chain.Bones.Count; i++)
            {
                Transform bone = chain.Bones[i];
                var badges = new List<string>();
                if (analysis.PhysBoneDrivers.ContainsKey(bone)) badges.Add("<color=#5FA8FF>PhysBone</color>");
                if (analysis.ConstraintDrivers.ContainsKey(bone)) badges.Add("<color=#FFA040>Constraint</color>");

                string text = $"{new string(' ', i * 3)}└ {bone.name}";
                if (badges.Count > 0) text += "   " + string.Join("  ", badges);

                if (GUILayout.Button(text, boneButtonStyle))
                {
                    SelectBone(bone);
                }
            }
        }

        private void DrawChainWarnings(FaceTrackingExtrasCore.BoneChain chain, string slot)
        {
            var constrained = chain.Bones.Where(b => analysis.ConstraintDrivers.ContainsKey(b)).ToList();
            if (constrained.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{string.Join(", ", constrained.Select(b => b.name))} driven by a constraint. Animating these bones will be overridden; animate the bones the constraint follows instead.",
                    MessageType.Warning);
            }

            if (chain.IsHelper)
            {
                EditorGUILayout.HelpBox("This looks like a helper chain (dummy/constraint bones), not the visible one.", MessageType.Warning);
            }

            if (chain.Branches)
            {
                EditorGUILayout.HelpBox("This chain branches. The longest branch was followed.", MessageType.Info);
            }

            if (slot != SlotTail && chain.Side == FaceTrackingExtrasCore.ChainSide.Centre)
            {
                EditorGUILayout.HelpBox("This chain sits on the centre line, so it may not be an ear.", MessageType.Warning);
            }

            bool sideMismatch = (slot == SlotEarLeft && chain.Side == FaceTrackingExtrasCore.ChainSide.Right)
                || (slot == SlotEarRight && chain.Side == FaceTrackingExtrasCore.ChainSide.Left);
            if (sideMismatch)
            {
                EditorGUILayout.HelpBox($"This chain is on the avatar's {chain.Side.ToString().ToLowerInvariant()} side.", MessageType.Warning);
            }
        }

        private void DrawSaveChains()
        {
            if (profile != null && ChainsMatchProfile())
            {
                EditorGUILayout.LabelField("<color=#6BCB77>✓</color> Chains saved in the profile.", poseLabelStyle);
                return;
            }

            bool earsMismatch = selectedChains.TryGetValue(SlotEarLeft, out var left) && selectedChains.TryGetValue(SlotEarRight, out var right)
                && left != null && right != null && left.Bones.Count != right.Bones.Count;
            if (earsMismatch)
            {
                EditorGUILayout.HelpBox("The ears have different bone counts, so they can only be mirrored up to the shorter chain.", MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                profile == null
                    ? "Save the chains to create this avatar's Face Tracking Extras profile. The bones' current rotations are stored as the rest pose."
                    : "The chains differ from the saved profile.",
                MessageType.Info);

            if (PawlygonEditorUI.DrawPrimaryButton(profile == null ? "Create Profile" : "Update Profile", 28f))
            {
                SaveChains();
                GUIUtility.ExitGUI();
            }
        }

        // =====================================================================
        // Drawing: Poses
        // =====================================================================

        private void DrawPoseSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Poses", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Click Edit, rotate the bones in the Scene view with the Rotate tool (E), then Save. Mirrored poses are filled in automatically.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                if (session == null)
                {
                    EditorGUILayout.HelpBox(sessionError ?? "Save the bone chains first.", sessionError != null ? MessageType.Error : MessageType.Info);
                    return;
                }

                DrawPoseGroup("Ears", FTExtrasPoses.PoseGroup.Ears);
                EditorGUILayout.Space(8f);
                DrawPoseGroup("Tail", FTExtrasPoses.PoseGroup.Tail);
            }
        }

        private void DrawPoseGroup(string title, FTExtrasPoses.PoseGroup group)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            bool hasBones = group == FTExtrasPoses.PoseGroup.Tail
                ? profile.tail.Count > 0
                : profile.earLeft.Count + profile.earRight.Count > 0;
            if (!hasBones)
            {
                EditorGUILayout.LabelField($"No {title.ToLowerInvariant()} bones in the profile.", PawlygonEditorUI.SubLabelStyle);
                return;
            }

            foreach (var definition in FTExtrasPoses.All.Where(d => d.Group == group))
            {
                DrawPoseRow(definition);
            }
        }

        private void DrawPoseRow(FTExtrasPoses.PoseDefinition definition)
        {
            bool isSet = FTExtrasPoses.Resolve(profile, definition.Id) != null;
            bool isActive = activePose == definition.Id && (mode == Mode.Editing || mode == Mode.Showing);
            bool isEditing = isActive && mode == Mode.Editing;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(definition.Label, GUILayout.Width(110f));

                string status;
                if (definition.IsDerived)
                {
                    string source = FTExtrasPoses.Get(definition.DerivedFrom.Value).Label;
                    status = isSet ? $"<color=#909090>Mirrored from {source}</color>" : $"<color=#909090>Needs {source}</color>";
                }
                else
                {
                    status = isSet ? "<color=#6BCB77>✓ Set</color>" : "<color=#909090>Not set</color>";
                }
                EditorGUILayout.LabelField(status, poseLabelStyle, GUILayout.MinWidth(90f));

                if (isEditing)
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Save", 20f))
                    {
                        SaveActivePose();
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("Cancel", GUILayout.Width(70f)))
                    {
                        StopMode();
                        GUIUtility.ExitGUI();
                    }
                }
                else
                {
                    using (new EditorGUI.DisabledScope(mode == Mode.Editing))
                    {
                        if (!definition.IsDerived && GUILayout.Button("Edit", GUILayout.Width(60f)))
                        {
                            StartEditing(definition);
                            GUIUtility.ExitGUI();
                        }

                        using (new EditorGUI.DisabledScope(!isSet))
                        {
                            bool showing = isActive && mode == Mode.Showing;
                            if (GUILayout.Button(showing ? "Hide" : "Show", GUILayout.Width(60f)))
                            {
                                if (showing) StopMode();
                                else ShowPose(definition);
                                GUIUtility.ExitGUI();
                            }

                            if (!definition.IsDerived && GUILayout.Button("Clear", GUILayout.Width(60f)))
                            {
                                ClearPose(definition);
                                GUIUtility.ExitGUI();
                            }
                        }

                        if (definition.IsDerived)
                        {
                            GUILayout.Space(64f);
                        }
                    }
                }
            }

            if (isEditing)
            {
                DrawEditingPanel(definition);
            }
        }

        private void DrawEditingPanel(FTExtrasPoses.PoseDefinition definition)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(definition.Hint, PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(4f);

                bool isEar = definition.Group == FTExtrasPoses.PoseGroup.Ears;
                if (isEar)
                {
                    EditorGUI.BeginChangeCheck();
                    liveMirror = EditorGUILayout.ToggleLeft("Mirror ears while editing", liveMirror);
                    if (EditorGUI.EndChangeCheck())
                    {
                        session.ResetMirrorTracking();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (isEar)
                    {
                        using (new EditorGUI.DisabledScope(session.EarLeft.Length == 0))
                        {
                            if (GUILayout.Button("Select Left Ear")) SelectBone(session.EarLeft[0]);
                        }
                        using (new EditorGUI.DisabledScope(session.EarRight.Length == 0))
                        {
                            if (GUILayout.Button("Select Right Ear")) SelectBone(session.EarRight[0]);
                        }
                    }
                    else if (GUILayout.Button("Select Tail"))
                    {
                        SelectBone(session.Tail[0]);
                    }

                    if (GUILayout.Button("Reset to Rest"))
                    {
                        session.Apply(FTExtrasPoses.RestPose(profile));
                        session.ResetMirrorTracking();
                    }
                }
            }
        }

        // =====================================================================
        // Drawing: Preview
        // =====================================================================

        private void DrawPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Preview", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Drive the poses the way face tracking will, to check how they combine.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                if (session == null) return;

                bool previewing = mode == Mode.Previewing;
                using (new EditorGUI.DisabledScope(mode == Mode.Editing))
                {
                    bool newPreviewing = EditorGUILayout.ToggleLeft("Preview on avatar", previewing);
                    if (newPreviewing != previewing)
                    {
                        if (newPreviewing) StartPreview();
                        else StopMode();
                        GUIUtility.ExitGUI();
                    }
                }

                using (new EditorGUI.DisabledScope(!previewing))
                {
                    EditorGUI.BeginChangeCheck();
                    previewInputs.GazeX = EditorGUILayout.Slider("Eyes Left ↔ Right", previewInputs.GazeX, -1f, 1f);
                    previewInputs.GazeY = EditorGUILayout.Slider("Eyes Down ↔ Up", previewInputs.GazeY, -1f, 1f);
                    previewInputs.Mood = EditorGUILayout.Slider("Sad ↔ Happy", previewInputs.Mood, -1f, 1f);
                    previewInputs.JawX = EditorGUILayout.Slider("Jaw Left ↔ Right", previewInputs.JawX, -1f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        ApplyPreview();
                    }

                    if (GUILayout.Button("Reset Sliders"))
                    {
                        bool playLoops = previewInputs.PlayLoops;
                        previewInputs = default;
                        previewInputs.PlayLoops = playLoops;
                        ApplyPreview();
                    }

                    EditorGUILayout.Space(8f);
                    EditorGUI.BeginChangeCheck();
                    bool playLoopsToggle = EditorGUILayout.ToggleLeft("Play loops (happy ear flick and tail wag)", previewInputs.PlayLoops);
                    if (EditorGUI.EndChangeCheck())
                    {
                        previewInputs.PlayLoops = playLoopsToggle;
                        previewInputs.Time = 0f;
                        loopStartTime = EditorApplication.timeSinceStartup;
                        ApplyPreview();
                    }

                    if (previewInputs.PlayLoops && previewInputs.Mood <= 0f)
                    {
                        EditorGUILayout.HelpBox("Move Sad \u2194 Happy above 0 to see the loops.", MessageType.None);
                    }
                }

                EditorGUILayout.Space(8f);
                DrawLoopSettings();
            }
        }

        private void DrawLoopSettings()
        {
            EditorGUILayout.LabelField("Loop Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            float flickPeriod = EditorGUILayout.Slider(new GUIContent("Ear Flick Speed (s)", "Seconds for one Happy \u2192 Happy Flick \u2192 Happy cycle."), profile.earFlickPeriod, 0.2f, 2f);
            float wagPeriod = EditorGUILayout.Slider(new GUIContent("Tail Wag Speed (s)", "Seconds for one full wag, right \u2192 left \u2192 right."), profile.tailWagPeriod, 0.2f, 2f);
            float wagAmount = EditorGUILayout.Slider(new GUIContent("Tail Wag Amount", "How far the wag swings, as a fraction of the Tail Right / Tail Left poses."), profile.tailWagAmount, 0f, 1f);
            float wagDelay = EditorGUILayout.Slider(new GUIContent("Tail Wag Delay", "How much each bone down the tail lags the one before it, as a fraction of a wag. Higher values make the wag ripple more."), profile.tailWagDelay, 0f, 0.5f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(profile, "Change Face Tracking Extras Loop Settings");
                profile.earFlickPeriod = flickPeriod;
                profile.tailWagPeriod = wagPeriod;
                profile.tailWagAmount = wagAmount;
                profile.tailWagDelay = wagDelay;
                EditorUtility.SetDirty(profile);
                ApplyPreview();
            }
        }

        // =====================================================================
        // Drawing: Generate
        // =====================================================================

        private void DrawGenerateSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Generate", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Bakes the poses into animation clips and builds the FX controller, the jaw toggle menu, the fake pupil dilation and a VRCFury prefab.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                if (session == null)
                {
                    EditorGUILayout.LabelField("Save the bone chains first.", PawlygonEditorUI.SubLabelStyle);
                    return;
                }

                EditorGUILayout.LabelField($"<b>Output:</b> {FTExtrasGenerator.GetOutputFolder(selectedAvatar)}", PawlygonEditorUI.RichMiniLabelStyle);
                EditorGUILayout.Space(4f);

                showGenerationSettings = EditorGUILayout.Foldout(showGenerationSettings, "Settings", true);
                if (showGenerationSettings)
                {
                    if (profileObject == null || profileObject.targetObject != profile) profileObject = new SerializedObject(profile);
                    profileObject.Update();
                    using (new EditorGUI.IndentLevelScope())
                    {
                        SerializedProperty generation = profileObject.FindProperty(nameof(FTExtrasProfile.generation));
                        SerializedProperty end = generation.GetEndProperty();
                        bool enterChildren = true;
                        while (generation.NextVisible(enterChildren) && !SerializedProperty.EqualContents(generation, end))
                        {
                            EditorGUILayout.PropertyField(generation, true);
                            enterChildren = false;
                        }
                    }
                    profileObject.ApplyModifiedProperties();
                    EditorGUILayout.Space(4f);
                }

                using (new EditorGUI.DisabledScope(mode == Mode.Editing))
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Generate Animations", 32f))
                    {
                        GenerateAnimations();
                        GUIUtility.ExitGUI();
                    }
                }

                bool hasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FTExtrasGenerator.GetPrefabPath(selectedAvatar)) != null;
                bool onAvatar = hasPrefab && FTExtrasGenerator.IsPrefabOnAvatar(selectedAvatar);
                using (new EditorGUI.DisabledScope(!hasPrefab || onAvatar))
                {
                    string label = onAvatar ? "\u2713 Prefab is on the avatar" : "Add Prefab to Avatar";
                    if (GUILayout.Button(label, GUILayout.Height(24f)))
                    {
                        FTExtrasGenerator.AddPrefabToAvatar(selectedAvatar);
                        SetStatus($"Added {FTExtrasGenerator.PrefabName} to {selectedAvatar.name}.", MessageType.Info);
                    }
                }

                if (lastResult != null)
                {
                    foreach (string warning in lastResult.Warnings)
                    {
                        EditorGUILayout.HelpBox(warning, MessageType.Warning);
                    }
                }
            }
        }

        private void GenerateAnimations()
        {
            StopMode();
            try
            {
                lastResult = FTExtrasGenerator.Generate(profile, selectedAvatar);
                SetStatus(lastResult.Message, lastResult.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info);
                Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} {lastResult.Message}");
                foreach (string warning in lastResult.Warnings)
                {
                    Debug.LogWarning($"{FaceTrackingExtrasCore.LogPrefix} {warning}");
                }
            }
            catch (System.Exception ex)
            {
                lastResult = null;
                SetStatus($"Generation failed: {ex.Message}", MessageType.Error);
                Debug.LogException(ex);
            }
        }

        // =====================================================================
        // Drawing: Debug
        // =====================================================================

        private void DrawDebugSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                showDebug = EditorGUILayout.Foldout(showDebug, "Rig Data (debug)", true);
                if (!showDebug) return;

                EditorGUILayout.LabelField(
                    $"Exports bone orientations, rest poses, PhysBones and constraints to <b>{FaceTrackingExtrasCore.DebugFolderName}/</b> in the project folder.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Export Rig Data", 28f))
                    {
                        ExportRigData();
                    }

                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(lastExportPath)))
                    {
                        if (GUILayout.Button("Show File", GUILayout.Height(28f), GUILayout.Width(100f)))
                        {
                            EditorUtility.RevealInFinder(lastExportPath);
                        }
                    }
                }
            }
        }

        private void ExportRigData()
        {
            try
            {
                lastExportPath = FaceTrackingExtrasCore.ExportJson(analysis, selectedChains);
                SetStatus($"Rig data exported to {lastExportPath}", MessageType.Info);
                Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} Rig data exported to {lastExportPath}");
            }
            catch (System.Exception ex)
            {
                SetStatus($"Export failed: {ex.Message}", MessageType.Error);
                Debug.LogException(ex);
            }
        }

        // =====================================================================
        // Profile
        // =====================================================================

        private static FTExtrasProfile FindProfile(GameObject avatar)
        {
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(FTExtrasProfile)}"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<FTExtrasProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null && candidate.avatarName == avatar.name) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Creates the profile in the avatar's output folder (Prefabs/FaceTrackingExtras).
        /// </summary>
        private FTExtrasProfile CreateProfile()
        {
            string folder = FTExtrasGenerator.GetOutputFolder(selectedAvatar);
            PawlygonEditorUtils.EnsureFolderExists(folder);

            string path = AssetDatabase.GenerateUniqueAssetPath(
                PawlygonEditorUtils.CombineAssetPath(folder, $"{FTExtrasGenerator.SafeName(selectedAvatar.name)} FTExtras.asset"));

            var created = CreateInstance<FTExtrasProfile>();
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        private void SaveChains()
        {
            StopMode();

            Transform root = selectedAvatar.transform;
            List<FTExtrasProfileBone> Capture(string slot) =>
                selectedChains.TryGetValue(slot, out var chain) && chain != null
                    ? chain.Bones.Select(b => FTExtrasPoseSession.CaptureRest(b, root)).ToList()
                    : new List<FTExtrasProfileBone>();

            var newLeft = Capture(SlotEarLeft);
            var newRight = Capture(SlotEarRight);
            var newTail = Capture(SlotTail);

            if (profile == null)
            {
                profile = CreateProfile();
            }
            else
            {
                bool earsChanged = !SamePaths(profile.earLeft, newLeft) || !SamePaths(profile.earRight, newRight);
                bool tailChanged = !SamePaths(profile.tail, newTail);
                var lost = profile.poses
                    .Where(p => FTExtrasPoses.Get(p.id).Group == FTExtrasPoses.PoseGroup.Ears ? earsChanged : tailChanged)
                    .ToList();

                if (lost.Count > 0 && !EditorUtility.DisplayDialog(
                        "Update Bone Chains",
                        $"Changing the chains clears these poses: {string.Join(", ", lost.Select(p => FTExtrasPoses.Get(p.id).Label))}.",
                        "Update", "Cancel"))
                {
                    return;
                }

                Undo.RecordObject(profile, "Update Face Tracking Extras Chains");
                foreach (var pose in lost) profile.ClearStoredPose(pose.id);
            }

            profile.avatarName = selectedAvatar.name;
            profile.earLeft = newLeft;
            profile.earRight = newRight;
            profile.tail = newTail;
            SaveProfile();

            BindSession();
            SetStatus($"Saved bone chains to {AssetDatabase.GetAssetPath(profile)}", MessageType.Info);
        }

        private void SaveProfile()
        {
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
        }

        /// <summary>
        /// Replaces the detected chains with the ones saved in the profile, when they still resolve.
        /// </summary>
        private void ApplyProfileChains()
        {
            ApplyProfileChain(SlotEarLeft, profile.earLeft, FaceTrackingExtrasCore.ChainKind.Ear, FaceTrackingExtrasCore.ChainSide.Left);
            ApplyProfileChain(SlotEarRight, profile.earRight, FaceTrackingExtrasCore.ChainKind.Ear, FaceTrackingExtrasCore.ChainSide.Right);
            ApplyProfileChain(SlotTail, profile.tail, FaceTrackingExtrasCore.ChainKind.Tail, FaceTrackingExtrasCore.ChainSide.Centre);
        }

        private void ApplyProfileChain(string slot, List<FTExtrasProfileBone> bones, FaceTrackingExtrasCore.ChainKind kind, FaceTrackingExtrasCore.ChainSide side)
        {
            if (bones.Count == 0) return;

            var chain = new FaceTrackingExtrasCore.BoneChain { Kind = kind, Side = side };
            foreach (var bone in bones)
            {
                Transform t = selectedAvatar.transform.Find(bone.path);
                if (t == null) return;
                chain.Bones.Add(t);
            }

            // Reuse the detected chain object when it is the same, so the candidate picker shows it.
            var candidates = kind == FaceTrackingExtrasCore.ChainKind.Ear ? analysis.EarChains : analysis.TailChains;
            selectedChains[slot] = candidates.FirstOrDefault(c => c.Bones.SequenceEqual(chain.Bones)) ?? chain;
        }

        private void BindSession()
        {
            StopMode();
            session = null;
            sessionError = null;

            if (profile == null || selectedAvatar == null) return;
            session = FTExtrasPoseSession.Bind(profile, selectedAvatar.transform, out sessionError);
        }

        private bool ChainsMatchProfile()
        {
            return SamePaths(profile.earLeft, ChainPaths(SlotEarLeft))
                && SamePaths(profile.earRight, ChainPaths(SlotEarRight))
                && SamePaths(profile.tail, ChainPaths(SlotTail));
        }

        private List<string> ChainPaths(string slot)
        {
            return selectedChains.TryGetValue(slot, out var chain) && chain != null
                ? chain.Bones.Select(b => FaceTrackingExtrasCore.GetRelativePath(b, selectedAvatar.transform)).ToList()
                : new List<string>();
        }

        private static bool SamePaths(List<FTExtrasProfileBone> bones, List<FTExtrasProfileBone> other)
        {
            return SamePaths(bones, other.Select(b => b.path).ToList());
        }

        private static bool SamePaths(List<FTExtrasProfileBone> bones, List<string> paths)
        {
            return bones.Select(b => b.path).SequenceEqual(paths);
        }

        // =====================================================================
        // Modes: editing, showing, previewing
        // =====================================================================

        private static bool IsEarPose(FTExtrasPoseId id) => FTExtrasPoses.Get(id).Group == FTExtrasPoses.PoseGroup.Ears;

        private void StartEditing(FTExtrasPoses.PoseDefinition definition)
        {
            StopMode();
            session.Begin();
            session.Apply(FTExtrasPoses.RestPose(profile));
            session.Apply(profile.GetStoredPose(definition.Id));

            mode = Mode.Editing;
            activePose = definition.Id;
            liveMirror = definition.Symmetry == FTExtrasPoses.PoseSymmetry.MirroredEars;
            session.ResetMirrorTracking();

            Tools.current = Tool.Rotate;
            Transform first = definition.Group == FTExtrasPoses.PoseGroup.Tail
                ? session.Tail.FirstOrDefault()
                : session.EarLeft.FirstOrDefault() ?? session.EarRight.FirstOrDefault();
            if (first != null) SelectBone(first);

            SetStatus($"Editing {definition.Label}. Rotate the bones, then click Save.", MessageType.Info);
        }

        private void SaveActivePose()
        {
            FTExtrasPoseData pose = session.Capture(activePose);
            Undo.RecordObject(profile, $"Save {FTExtrasPoses.Get(activePose).Label} Pose");
            profile.SetStoredPose(pose);
            SaveProfile();

            string label = FTExtrasPoses.Get(activePose).Label;
            StopMode();
            SetStatus($"Saved {label}.", MessageType.Info);
        }

        private void ClearPose(FTExtrasPoses.PoseDefinition definition)
        {
            if (!EditorUtility.DisplayDialog("Clear Pose", $"Clear the {definition.Label} pose?", "Clear", "Cancel")) return;

            StopMode();
            Undo.RecordObject(profile, $"Clear {definition.Label} Pose");
            profile.ClearStoredPose(definition.Id);
            SaveProfile();
        }

        private void ShowPose(FTExtrasPoses.PoseDefinition definition)
        {
            StopMode();
            session.Begin();
            session.Apply(FTExtrasPoses.RestPose(profile));
            session.Apply(FTExtrasPoses.Resolve(profile, definition.Id));

            mode = Mode.Showing;
            activePose = definition.Id;
        }

        private void StartPreview()
        {
            StopMode();
            session.Begin();
            mode = Mode.Previewing;
            ApplyPreview();
        }

        private void ApplyPreview()
        {
            if (mode != Mode.Previewing) return;
            session.Apply(FTExtrasPoses.Blend(profile, previewInputs));
        }

        /// <summary>
        /// Puts the bones back to how they were before the current mode started.
        /// </summary>
        private void StopMode()
        {
            // Loop settings are only marked dirty while dragging; write them out now.
            if (profile != null) AssetDatabase.SaveAssetIfDirty(profile);

            if (session != null && session.IsActive)
            {
                bool wasEditing = mode == Mode.Editing;
                session.Restore();

                // Drop the gizmo rotations from the undo history so Ctrl+Z cannot re-pose the bones.
                if (wasEditing)
                {
                    foreach (Transform bone in session.EarLeft.Concat(session.EarRight).Concat(session.Tail))
                    {
                        Undo.ClearUndo(bone);
                    }
                }
            }

            mode = Mode.Idle;
        }

        private static void SelectBone(Transform bone)
        {
            if (bone == null) return;
            Selection.activeTransform = bone;
            EditorGUIUtility.PingObject(bone);
        }
    }
}
