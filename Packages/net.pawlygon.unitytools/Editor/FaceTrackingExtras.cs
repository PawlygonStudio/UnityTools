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
        private const string WindowTitle = "Face Tracking Extras";
        private const string LockedTabTooltip = "Create the profile in Setup first.";
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

        private enum Tab { Setup, EarsAndTail, Pupils, Custom, Generate }

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private Tab currentTab;
        private GameObject selectedAvatar;
        private FaceTrackingExtrasCore.RigAnalysis analysis;
        private readonly Dictionary<string, FaceTrackingExtrasCore.BoneChain> selectedChains = new Dictionary<string, FaceTrackingExtrasCore.BoneChain>();
        private readonly PawlygonStatus status = new PawlygonStatus();
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
        private List<SkinnedMeshRenderer> pupilDilationMeshes = new List<SkinnedMeshRenderer>();
        private List<SkinnedMeshRenderer> pupilConstrictMeshes = new List<SkinnedMeshRenderer>();

        /// <summary>Cached result of the prefab-on-avatar scan, refreshed when the hierarchy changes.</summary>
        private bool? prefabOnAvatarCache;
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

        [MenuItem(MenuPath, priority = 41)] // Tools: Tune
        public static void ShowWindow()
        {
            FaceTrackingExtras window = GetWindow<FaceTrackingExtras>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(520f, 520f);
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;

            if (selectedAvatar == null)
            {
                selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
            }
            LoadAvatar(selectedAvatar);
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            StopMode();
            StopPupilPreview();
            StopCustomPreview();
        }

        /// <summary>
        /// The avatar was deleted or its scene closed: reload from whatever avatar is in the scene now, instead
        /// of keeping a session bound to destroyed bones. Also refreshes the cached hierarchy scans.
        /// </summary>
        private void OnHierarchyChange()
        {
            FTExtrasParameterCatalog.Invalidate();
            if (selectedAvatar == null && (analysis != null || session != null || profile != null))
            {
                selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
                LoadAvatar(selectedAvatar);
            }
            else
            {
                // A bone in the chains was deleted or renamed: rebind, which reports the missing bones.
                if (session != null && !session.MatchesProfile())
                {
                    StopMode();
                    BindSession();
                }
                RefreshHierarchyCaches();
            }
            Repaint();
        }

        private void RefreshHierarchyCaches()
        {
            prefabOnAvatarCache = null;
            var (dilation, constrict) = FTExtrasPupils.FindMeshes(selectedAvatar);
            pupilDilationMeshes = dilation;
            pupilConstrictMeshes = constrict;
            pupilMeshCount = dilation.Count;
        }

        /// <summary>
        /// Undo/redo can roll the profile back to different bone chains. Rebind so the session never indexes
        /// bones the profile no longer has. (Undoing a gizmo rotation while editing leaves the chains alone.)
        /// </summary>
        private void OnUndoRedo()
        {
            if (profile != null && session != null && !session.MatchesProfile())
            {
                StopMode();
                StopPupilPreview();
                if (analysis != null && analysis.Success) ApplyProfileChains();
                BindSession();
            }

            profileObject?.Update();
            Repaint();
        }

        /// <summary>Loops redraw at most this often.</summary>
        private const double LoopFrameInterval = 1.0 / 60.0;

        private void OnEditorUpdate()
        {
            UpdateBoneModes();
            UpdatePupilPreview();
            UpdateCustomPreview();
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
                WindowTitle,
                "Ears, tail, pupils and your own animations, driven by face tracking.",
                PawlygonEditorUI.DocumentationUrl);

            DrawAvatarBar();
            EditorGUILayout.Space(6f);

            // The profile went away (another avatar, a missing bone): fall back to Setup, keeping the status that explains why.
            if (!IsTabAvailable(currentTab)) SwitchTab(Tab.Setup, clearStatus: false);
            DrawTabBar();
            EditorGUILayout.Space(6f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            switch (currentTab)
            {
                case Tab.Setup: DrawSetupTab(); break;
                case Tab.EarsAndTail: DrawEarsAndTailTab(); break;
                case Tab.Pupils: DrawPupilsTab(); break;
                case Tab.Custom: DrawCustomTab(); break;
                case Tab.Generate: DrawGenerateTab(); break;
            }

            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);
            DrawActionBar();
            PawlygonEditorUI.DrawFooter();
        }

        /// <summary>
        /// The current tab's main action, pinned above the footer: creating or updating the profile in Setup,
        /// saving the pose being edited in Ears &amp; Tail, and generating in Generate.
        /// </summary>
        private void DrawActionBar()
        {
            if (selectedAvatar == null) return;

            switch (currentTab)
            {
                case Tab.Setup: DrawSetupActions(); break;
                case Tab.EarsAndTail: DrawPoseActions(); break;
                case Tab.Custom: DrawCustomActions(); break;
                case Tab.Generate: DrawGenerateActions(); break;
            }
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
            PawlygonEditorUI.TabSpec Spec(Tab tab, string label, bool done)
            {
                bool available = IsTabAvailable(tab);
                return new PawlygonEditorUI.TabSpec(label, done, !available, available ? null : LockedTabTooltip);
            }

            var tabs = new[]
            {
                Spec(Tab.Setup, "Setup", IsSetupDone()),
                Spec(Tab.EarsAndTail, EarsAndTailTabLabel(), session != null && AreRequiredPosesSet()),
                Spec(Tab.Pupils, profile != null && !profile.generation.fakeDilation ? "Pupils (off)" : "Pupils", IsPupilsReady()),
                Spec(Tab.Custom, CustomTabLabel(), AreCustomAnimationsValid()),
                Spec(Tab.Generate, "Generate", IsGenerated()),
            };

            int clicked = PawlygonEditorUI.DrawTabBar(tabs, (int)currentTab);
            if (clicked >= 0)
            {
                SwitchTab((Tab)clicked);
                GUIUtility.ExitGUI();
            }
        }

        private void SwitchTab(Tab tab, bool clearStatus = true)
        {
            StopMode();
            StopPupilPreview();
            StopCustomPreview();
            currentTab = tab;
            scrollPosition = Vector2.zero;
            if (clearStatus) status.Clear();
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

        private bool AreRequiredPosesSet()
        {
            return profile != null && FTExtrasPoses.All
                .Where(d => !d.IsDerived && !d.Optional && HasBonesFor(d.Group))
                .All(d => profile.GetStoredPose(d.Id) != null);
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
            return selectedAvatar != null && session != null && IsPrefabOnAvatar() && !IsGenerationStale();
        }

        /// <summary>
        /// Poses or settings changed since the last generation. Hashing the profile is cheap but not free, and
        /// OnGUI runs several times per frame, so the result is reused for a short moment.
        /// </summary>
        private bool IsGenerationStale()
        {
            if (profile == null) return false;
            double now = EditorApplication.timeSinceStartup;
            if (now - staleCheckTime > 0.25 || staleCheckProfile != profile)
            {
                staleCheckTime = now;
                staleCheckProfile = profile;
                staleCache = profile.IsGenerationStale;
            }
            return staleCache;
        }

        private double staleCheckTime = -1;
        private FTExtrasProfile staleCheckProfile;
        private bool staleCache;

        private bool IsPrefabOnAvatar()
        {
            if (selectedAvatar == null) return false;
            if (prefabOnAvatarCache == null) prefabOnAvatarCache = FTExtrasGenerator.IsPrefabOnAvatar(selectedAvatar);
            return prefabOnAvatarCache.Value;
        }

        private void SetStatus(string message, MessageType type, string actionLabel = null, System.Action action = null)
        {
            status.Set(message, type, actionLabel, action);
        }

        // =====================================================================
        // Avatar bar
        // =====================================================================

        private void DrawAvatarBar()
        {
            using (new EditorGUI.DisabledScope(mode != Mode.Idle))
            {
                if (PawlygonEditorUI.DrawAvatarBar(this, ref selectedAvatar, "Avatar"))
                {
                    LoadAvatar(selectedAvatar);
                    GUIUtility.ExitGUI();
                }

                EditorGUI.BeginChangeCheck();
                FTExtrasProfile newProfile = (FTExtrasProfile)EditorGUILayout.ObjectField(
                    new GUIContent("Profile", "Where this avatar's bone chains, poses and settings are saved."), profile, typeof(FTExtrasProfile), false);
                if (EditorGUI.EndChangeCheck())
                {
                    StopPupilPreview();
                    StopCustomPreview();
                    selectedCustomIndex = -1;
                    status.Clear();
                    profile = newProfile;
                    if (profile != null && analysis != null && analysis.Success) ApplyProfileChains();
                    BindSession();
                    GUIUtility.ExitGUI();
                }
            }

            if (profile == null)
            {
                EditorGUILayout.LabelField(
                    "Pick an avatar with a Humanoid rig. Its profile is created when you click Create Profile in Setup.",
                    PawlygonEditorUI.SubLabelStyle);
            }
        }

        private void LoadAvatar(GameObject avatar)
        {
            StopMode();
            StopPupilPreview();
            StopCustomPreview();
            selectedCustomIndex = -1;
            pupilPreview = null;
            selectedAvatar = avatar;
            RefreshHierarchyCaches();
            analysis = null;
            selectedChains.Clear();
            lastExportPath = null;
            status.Clear();
            session = null;
            sessionError = null;
            profile = avatar != null ? FindProfile(avatar) : null;

            if (avatar == null) return;

            RunAnalysis();
            if (profile != null && analysis.Success)
            {
                ApplyProfileChains();
                BindSession();
                AdoptLegacyProfile();
            }
        }

        private void RunAnalysis()
        {
            analysis = FaceTrackingExtrasCore.Analyze(selectedAvatar);
            selectedChains.Clear();
            lastExportPath = null;

            // A successful scan shows its result in the Setup tab; only a failure needs the status bar.
            if (!analysis.Success)
            {
                SetStatus(analysis.StatusMessage, MessageType.Error);
                return;
            }

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

        /// <summary>
        /// The profile that belongs to <paramref name="avatar"/>. Profiles with an identity (prefab or scene
        /// object) win over 1.6.0 profiles that only recorded the avatar's name.
        /// </summary>
        private static FTExtrasProfile FindProfile(GameObject avatar)
        {
            FTExtrasProfile nameOnlyMatch = null;
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(FTExtrasProfile)}"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<FTExtrasProfile>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate == null || !candidate.BelongsTo(avatar)) continue;

                if (candidate.HasIdentity) return candidate;
                if (nameOnlyMatch == null) nameOnlyMatch = candidate;
            }
            return nameOnlyMatch;
        }

        /// <summary>
        /// A 1.6.0 profile matched by name adopts this avatar's identity once its bones resolve on it, so it
        /// can no longer be picked up by a different avatar that happens to share the name.
        /// </summary>
        private void AdoptLegacyProfile()
        {
            if (profile == null || profile.HasIdentity || session == null || selectedAvatar == null) return;

            profile.SetIdentity(selectedAvatar);
            SaveProfile();
        }

        /// <summary>
        /// The profile was matched (by name or picked by hand) but its bones are not on this avatar, so it
        /// most likely belongs to a different avatar.
        /// </summary>
        private bool ProfileBelongsToAnotherAvatar => profile != null && session == null && sessionError != null;

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

            bool created = profile == null;
            if (created)
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

            profile.SetIdentity(selectedAvatar);
            profile.earLeft = newLeft;
            profile.earRight = newRight;
            profile.tail = newTail;
            SaveProfile();

            string baselineNote = created ? ApplyBaselineToNewProfile() : null;
            BindSession();
            SetStatus(baselineNote ?? $"Saved the bone chains to '{profile.name}'.", MessageType.Info, "Ping", PawlygonStatus.Ping(profile));
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

            // The gizmo edits go into the undo history; the session clears them when it restores the bones,
            // however that happens (Save, Cancel, play mode, script reload), so Ctrl+Z can't re-pose the bones.
            session.ClearUndoOnRestore = true;

            Tools.current = Tool.Rotate;
            Transform first = definition.Group == FTExtrasPoses.PoseGroup.Tail
                ? session.Tail.FirstOrDefault()
                : session.EarLeft.FirstOrDefault() ?? session.EarRight.FirstOrDefault();
            if (first != null) SelectBone(first);
        }

        private void SaveActivePose()
        {
            FTExtrasPoseData pose = session.Capture(activePose);
            Undo.RecordObject(profile, $"Save {FTExtrasPoses.Get(activePose).Label} Pose");
            profile.SetStoredPose(pose);
            SaveProfile();

            // The pose row now shows it as set, so no status message is needed.
            StopMode();
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
                session.Restore();
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
