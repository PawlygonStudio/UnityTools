using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for Face Tracking Extras: ear, tail and pupil animations driven by face tracking.
    /// Organised in tabs that follow the setup order: Setup (bone chains and profile), Ears &amp; Tail
    /// (poses and preview), Pupils (fake dilation settings and preview) and Generate. Each tab lives in its
    /// own partial file. Delegates rig analysis to <see cref="FaceTrackingExtrasCore"/>, pose math to
    /// <see cref="FTExtrasPoses"/>, bone changes to <see cref="FTExtrasPoseSession"/> and pupil curves to
    /// <see cref="FTExtrasPupils"/>.
    /// </summary>
    public partial class FaceTrackingExtras : EditorWindow
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

        private enum Tab { Setup, EarsAndTail, Pupils, Generate }

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private Tab currentTab;
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
        private bool showParameterSettings;
        private SerializedObject profileObject;
        private FTExtrasGenerator.Result lastResult;

        // --- Pupil preview ---
        private FTExtrasPupilPreview pupilPreview;
        private int pupilMeshCount;
        private bool pupilIdlePlaying;
        private double pupilIdleStartTime;
        private double pupilReflexStartTime = -1;
        private float pupilDilationValue;
        private float pupilConstrictValue;
        private bool showPupilAdvanced;

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
            StopPupilPreview();
        }

        /// <summary>Loops redraw at most this often.</summary>
        private const double LoopFrameInterval = 1.0 / 60.0;

        private void OnEditorUpdate()
        {
            UpdateBoneModes();
            UpdatePupilPreview();
        }

        private void UpdateBoneModes()
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

            DrawAvatarBar();
            EditorGUILayout.Space(6f);

            if (!IsTabAvailable(currentTab)) currentTab = Tab.Setup;
            DrawTabBar();
            EditorGUILayout.Space(6f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            switch (currentTab)
            {
                case Tab.Setup: DrawSetupTab(); break;
                case Tab.EarsAndTail: DrawEarsAndTailTab(); break;
                case Tab.Pupils: DrawPupilsTab(); break;
                case Tab.Generate: DrawGenerateTab(); break;
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

        // =====================================================================
        // Tabs
        // =====================================================================

        /// <summary>Every tab but Setup needs a saved profile bound to the avatar.</summary>
        private bool IsTabAvailable(Tab tab) => tab == Tab.Setup || session != null;

        private void DrawTabBar()
        {
            var tabs = new[]
            {
                (Tab.Setup, "Setup", IsSetupDone()),
                (Tab.EarsAndTail, EarsAndTailTabLabel(), session != null && CountSetPoses() == CountStoredPoseSlots()),
                (Tab.Pupils, profile != null && !profile.generation.fakeDilation ? "Pupils (off)" : "Pupils", IsPupilsReady()),
                (Tab.Generate, "Generate", IsGenerated()),
            };

            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < tabs.Length; i++)
                {
                    var (tab, label, done) = tabs[i];
                    string styleName = i == 0 ? "LargeButtonLeft" : i == tabs.Length - 1 ? "LargeButtonRight" : "LargeButtonMid";
                    GUIStyle style = GUI.skin.FindStyle(styleName) ?? EditorStyles.miniButton;

                    bool available = IsTabAvailable(tab);
                    bool showTick = done && available;
                    var content = new GUIContent(
                        showTick ? $" {label}" : label,
                        showTick ? EditorGUIUtility.IconContent("TestPassed").image : null,
                        available ? null : "Save the bone chains in Setup first.");

                    using (new EditorGUI.DisabledScope(!available))
                    {
                        bool selected = GUILayout.Toggle(currentTab == tab, content, style, GUILayout.Height(26f));
                        if (selected && currentTab != tab)
                        {
                            SwitchTab(tab);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        private void SwitchTab(Tab tab)
        {
            StopMode();
            StopPupilPreview();
            currentTab = tab;
            scrollPosition = Vector2.zero;
        }

        private bool IsSetupDone() => profile != null && session != null && ChainsMatchProfile();

        private int CountStoredPoseSlots()
        {
            if (profile == null) return 0;
            return FTExtrasPoses.All.Count(d => !d.IsDerived && HasBonesFor(d.Group));
        }

        private int CountSetPoses()
        {
            if (profile == null) return 0;
            return FTExtrasPoses.All.Count(d => !d.IsDerived && HasBonesFor(d.Group) && profile.GetStoredPose(d.Id) != null);
        }

        private bool HasBonesFor(FTExtrasPoses.PoseGroup group)
        {
            return group == FTExtrasPoses.PoseGroup.Tail
                ? profile.tail.Count > 0
                : profile.earLeft.Count + profile.earRight.Count > 0;
        }

        private string EarsAndTailTabLabel()
        {
            return session == null ? "Ears & Tail" : $"Ears & Tail ({CountSetPoses()}/{CountStoredPoseSlots()})";
        }

        private bool IsPupilsReady()
        {
            return profile != null && profile.generation.fakeDilation && pupilMeshCount > 0;
        }

        private bool IsGenerated()
        {
            return selectedAvatar != null && session != null && FTExtrasGenerator.IsPrefabOnAvatar(selectedAvatar);
        }

        private void SetStatus(string message, MessageType type)
        {
            statusMessage = message;
            statusMessageType = type;
        }

        // =====================================================================
        // Avatar bar
        // =====================================================================

        private void DrawAvatarBar()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
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

                if (profile == null)
                {
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField(
                        "Select an avatar from the scene with a Humanoid rig. Its profile is created when you save the bone chains in Setup.",
                        PawlygonEditorUI.SubLabelStyle);
                }
            }
        }

        private void LoadAvatar(GameObject avatar)
        {
            StopMode();
            StopPupilPreview();
            pupilPreview = null;
            pupilMeshCount = FTExtrasPupils.FindMeshes(avatar).Dilation.Count;
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
