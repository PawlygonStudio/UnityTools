using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step-by-step wizard that copies a vendor avatar (FBX and prefab), waits for the edited face tracking
    /// FBX, generates the patch files, swaps the edited meshes and rig into the prefab, adds optional helpers,
    /// guards the FX controller and reports the outcome. Every step already reached can be reopened from the
    /// step bar without losing its state; the main action of each step sits in the pinned action bar.
    /// The steps live in the partial files AvatarSetupWizard.&lt;Step&gt;.cs.
    /// </summary>
    public partial class AvatarSetupWizard : EditorWindow
    {
        private const string WindowTitle = "Avatar Setup Wizard";
        private const string MenuPath = "!Pawlygon/Avatar Setup Wizard";
        private const string DefaultMainFolderName = "!Pawlygon";

        /// <summary>Placeholder folder name used by older versions; never accepted as a real name.</summary>
        private const string PlaceholderAvatarName = "Avatar Name";

        private const float SectionSpacing = 8f;
        private const float ActionButtonHeight = 28f;
        private const float PrimaryButtonMinWidth = 190f;
        private const string ProgressTitle = "Avatar Setup Wizard";
        private const int SharedSceneGridColumns = 3;
        private const float SharedSceneGridSpacingX = 1.5f;
        private const float SharedSceneGridSpacingZ = 1.5f;
        private const string VrcftPrefabGuid = "ca618adb2c3333545a1f36d72a73a3ef";
        private const string VrcftContainerName = "!Pawlygon - VRCFT";
        private const string VrcftPackageListingUrl = "https://vcc.pawlygon.net/";
        private const string PatcherHubLatestReleaseApiUrl = "https://api.github.com/repos/PawlygonStudio/PatcherHub/releases/latest";
        private const int PatcherHubReleaseInfoTimeoutSeconds = 30;
        private const int PatcherHubDownloadTimeoutSeconds = 300;

        private const int SourceFbxPickerControlId = 9001;
        private const int SourcePrefabPickerControlId = 9002;

        [SerializeField] private string mainFolderName = DefaultMainFolderName;
        [SerializeField] private bool useSeparateFolderPerAvatar;
        [SerializeField] private string sharedAvatarFolderName = string.Empty;
        [SerializeField] private List<AvatarEntry> avatarEntries = new List<AvatarEntry> { new AvatarEntry() };

        [SerializeField] private WizardStep currentStep = WizardStep.Setup;

        /// <summary>The furthest step reached; every step up to it can be reopened from the step bar.</summary>
        [SerializeField] private WizardStep furthestStep = WizardStep.Setup;

        [SerializeField] private int selectedEntryIndex;

        private Vector2 scrollPosition;
        private readonly PawlygonStatus status = new PawlygonStatus();
        private bool pendingImportTransition;

        /// <summary>Set while the structure is created or resumed, so the copies' imports aren't taken for edited FBXs.</summary>
        private bool isBuildingStructure;

        // --- Styles ---
        private GUIStyle cardStyle;
        private GUIStyle rowLabelStyle;
        private GUIStyle rowSelectedLabelStyle;
        private GUIStyle rowBadgeStyle;
        private GUIStyle fieldErrorStyle;
        private GUIStyle mutedMiniStyle;
        private GUIStyle pathLabelStyle;
        private bool stylesBuiltForProSkin;

        private enum WizardStep
        {
            Setup,
            WaitForImport,
            SelectMeshes,
            Prefabs,
            FXCheck,
            Complete
        }

        private static readonly string[] StepLabels = { "Setup", "Import", "Replacements", "Prefabs", "FX Check", "Finish" };

        private static readonly string[] StepTooltips =
        {
            "Choose the avatars to set up and where to save them.",
            "Swap in your edited (face tracking) FBX files.",
            "Choose which meshes and rig the prefab takes from the edited FBX.",
            "Optional helpers: the VRCFT prefab and PatcherHub.",
            "Turn gesture expressions and blinking off while face tracking is active.",
            "What was done for each avatar, and what to do next."
        };

        private static readonly string[] LockedStepTooltips =
        {
            null,
            "Create the avatar structure in Setup first.",
            "Import the edited FBX files first.",
            "Review the replacements of every avatar first.",
            "Continue from Prefabs first.",
            "Finish the FX Check first."
        };

        [Serializable]
        private class AvatarEntry
        {
            public GameObject sourceFbx;
            public GameObject sourcePrefab;
            public string avatarFolderName = string.Empty;
            public string copiedFbxPath;
            public string copiedPrefabPath;
            public string createdScenePath;
            public string avatarRootPath;
            public string diffGeneratorAssetPath;
            public bool diffGenerationFailed;
            public string diffGenerationError;
            public long watchedFbxWriteTimeUtcTicks;
            public long watchedFbxFileSize;
            public bool hasImportedModifiedFbx;

            /// <summary>
            /// The copied FBX changed since its patch files were generated and its replacements loaded (or
            /// they never were): it needs a diff run and a fresh replacement review.
            /// </summary>
            public bool needsProcessing = true;

            public bool isMeshReviewComplete;
            public string reviewResultLabel;
            public AnimatorReplacementState animatorReplacement = new AnimatorReplacementState();
            public List<MeshSelectionState> meshSelections = new List<MeshSelectionState>();
            /// <summary>The guarded FX controller copy this wizard made and gave to the prefab.</summary>
            public string copiedFxControllerPath;

            /// <summary>The FX controller (or override) the prefab used before the wizard changed it; avatars sharing it share the copy.</summary>
            public string originalFxControllerPath;

            /// <summary>The user skipped the FX Check for this avatar.</summary>
            public bool fxSkipped;

            /// <summary>How many guards the wizard wrote for this avatar.</summary>
            public int fxGuardsWritten;

            // FX Check analysis: rebuilt when needed (after a domain reload, an apply or an undo).
            [NonSerialized] public bool fxAnalyzed;
            [NonSerialized] public FXGestureCheckerCore.AnalysisResult fxAnalysisResult;
            [NonSerialized] public FXGestureCheckerCore.RecommendedPlan fxPlan;
            [NonSerialized] public AnimatorOverrideController fxOverrideController;
            [NonSerialized] public FXGestureCheckerUI.ViewState fxView;
            [NonSerialized] public string fxProblem;
            [NonSerialized] public MessageType fxProblemType;

            /// <summary>Finish step: the Details foldout is open.</summary>
            [NonSerialized] public bool showFinishDetails;
        }

        [Serializable]
        private class AnimatorReplacementState
        {
            public string prefabAnimatorObjectName;
            public string prefabAnimatorRelativePath;
            public string fbxAnimatorObjectName;
            public string fbxAnimatorRelativePath;
            public string fbxAvatarName;
            public string matchReason;
            public bool hasPrefabAnimator;
            public bool hasHumanoidAvatar;
            public bool selected;
        }

        [Serializable]
        private class MeshSelectionState
        {
            public string fbxObjectName;
            public string fbxRelativePath;
            public string fbxMeshName;
            public string prefabObjectName;
            public string prefabRelativePath;
            public string prefabMeshName;
            public string matchReason;
            public bool hasMatch;
            public bool selected;
            public bool isBodyMeshCandidate;
            public string[] missingRequiredUnifiedBlendshapesOnFbx = Array.Empty<string>();
            public bool showUnifiedBlendshapeWarningDetails;

            /// <summary>The row shows its details (paths, bones, blendshapes). Opened automatically when it needs attention.</summary>
            public bool showDetails;

            /// <summary>The FBX renderer's bones differ from the prefab renderer's (names, order or count).</summary>
            public bool bonesDiffer;

            /// <summary>The bones differ and cannot all be found on the prefab, so they cannot be remapped.</summary>
            public bool bonesUnresolved;

            /// <summary>Why the bones cannot be remapped (missing or ambiguous bone names).</summary>
            public string boneIssue;
        }

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            AvatarSetupWizard window = GetWindow<AvatarSetupWizard>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(560f, 480f);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            FBXImportDetector.FbxReimported += HandleFbxReimported;
            Undo.undoRedoPerformed += OnUndoRedo;
            EnsureAtLeastOneEntry();

            // Windows saved by older versions only knew the current step.
            if (furthestStep < currentStep)
            {
                furthestStep = currentStep;
            }
        }

        private void OnDisable()
        {
            FBXImportDetector.FbxReimported -= HandleFbxReimported;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.delayCall -= TryContinueAfterImport;
        }

        private void OnProjectChange()
        {
            InvalidateProjectCaches();
            Repaint();
        }

        /// <summary>Undo can remove the guards the FX Check added.</summary>
        private void OnUndoRedo()
        {
            RequestFxRefresh();
            Repaint();
        }

        /// <summary>The FX controllers may have been edited in the Animator window meanwhile.</summary>
        private void OnFocus()
        {
            if (currentStep == WizardStep.FXCheck)
            {
                RequestFxRefresh();
            }
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();
            EnsureAtLeastOneEntry();
            HandleObjectPickerSelection();

            // Everything that decides which controls are drawn is refreshed before the layout pass, so the
            // Layout and Repaint passes draw the same controls.
            if (Event.current.type == EventType.Layout)
            {
                RefreshLayoutState();
            }

            PawlygonEditorUI.DrawHeader(
                WindowTitle,
                "Copies your avatar, builds the face tracking patch and prepares a ready-to-edit prefab.",
                PawlygonEditorUI.DocumentationUrl);

            DrawStepBar();
            EditorGUILayout.Space(4f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
            DrawStepBanners();

            switch (currentStep)
            {
                case WizardStep.Setup:
                    DrawSetupStep();
                    break;
                case WizardStep.WaitForImport:
                    DrawWaitForImportStep();
                    break;
                case WizardStep.SelectMeshes:
                    DrawMeshSelectionStep();
                    break;
                case WizardStep.Prefabs:
                    DrawPrefabsStep();
                    break;
                case WizardStep.FXCheck:
                    DrawFXCheckStep();
                    break;
                case WizardStep.Complete:
                    DrawCompleteStep();
                    break;
            }

            EditorGUILayout.Space(SectionSpacing);
            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);
            DrawActionBar();
            PawlygonEditorUI.DrawFooter();
        }

        private void RefreshLayoutState()
        {
            // The step bar, FX Check and Finish show each avatar's FX outcome.
            if (furthestStep >= WizardStep.FXCheck)
            {
                EnsureFxAnalyzed(showProgress: false);
            }

            switch (currentStep)
            {
                case WizardStep.Setup:
                    RefreshSetupSnapshot();
                    break;
                case WizardStep.Prefabs:
                case WizardStep.Complete:
                    EnsureProjectCaches();
                    break;
            }
        }

        private void DrawActionBar()
        {
            PawlygonEditorUI.BeginActionBar();

            switch (currentStep)
            {
                case WizardStep.Setup:
                    DrawSetupActions();
                    break;
                case WizardStep.WaitForImport:
                    DrawImportActions();
                    break;
                case WizardStep.SelectMeshes:
                    DrawReplacementActions();
                    break;
                case WizardStep.Prefabs:
                    DrawPrefabsActions();
                    break;
                case WizardStep.FXCheck:
                    DrawFXCheckActions();
                    break;
                case WizardStep.Complete:
                    DrawCompleteActions();
                    break;
            }

            PawlygonEditorUI.EndActionBar();
        }

        // =====================================================================
        // Steps and navigation
        // =====================================================================

        private void DrawStepBar()
        {
            var tabs = new PawlygonEditorUI.TabSpec[StepLabels.Length];
            for (int i = 0; i < tabs.Length; i++)
            {
                var step = (WizardStep)i;
                bool locked = step > furthestStep;
                tabs[i] = new PawlygonEditorUI.TabSpec(StepLabels[i], IsStepDone(step), locked,
                    locked ? LockedStepTooltips[i] : StepTooltips[i]);
            }

            int clicked = PawlygonEditorUI.DrawTabBar(tabs, (int)currentStep);
            if (clicked >= 0)
            {
                GoToStep((WizardStep)clicked);
                GUIUtility.ExitGUI();
            }
        }

        private bool IsStepDone(WizardStep step)
        {
            switch (step)
            {
                case WizardStep.Setup:
                    return IsStructureCreated;
                case WizardStep.WaitForImport:
                    return furthestStep > WizardStep.WaitForImport && avatarEntries.All(entry => !entry.needsProcessing);
                case WizardStep.SelectMeshes:
                    return furthestStep >= WizardStep.SelectMeshes && avatarEntries.All(entry => entry.isMeshReviewComplete);
                case WizardStep.Prefabs:
                    return furthestStep > WizardStep.Prefabs;
                case WizardStep.FXCheck:
                    return furthestStep >= WizardStep.FXCheck && avatarEntries.All(IsFxComplete);
                default:
                    return false;
            }
        }

        /// <summary>True once the avatar structure exists; the Setup inputs are locked from then on.</summary>
        private bool IsStructureCreated => furthestStep > WizardStep.Setup;

        /// <summary>
        /// Opens <paramref name="step"/> (also unlocking it when it is further than any step so far), clears
        /// the status and picks the avatar to show: the first one still to review in Replacements and FX Check.
        /// Never resets the work done in a step; only a newly imported FBX reloads its replacements.
        /// </summary>
        private void GoToStep(WizardStep step)
        {
            currentStep = step;
            if (step > furthestStep)
            {
                furthestStep = step;
            }

            status.Clear();
            scrollPosition = Vector2.zero;
            InvalidateProjectCaches();

            switch (step)
            {
                case WizardStep.SelectMeshes:
                    SelectFirstEntry(entry => !entry.isMeshReviewComplete);
                    break;
                case WizardStep.FXCheck:
                    OnEnterFXCheck();
                    break;
            }

            Repaint();
        }

        /// <summary>Selects the first avatar matching <paramref name="predicate"/>; keeps the selection when none does.</summary>
        private void SelectFirstEntry(Func<AvatarEntry, bool> predicate)
        {
            int index = avatarEntries.FindIndex(entry => predicate(entry));
            selectedEntryIndex = index >= 0 ? index : Mathf.Clamp(selectedEntryIndex, 0, avatarEntries.Count - 1);
        }

        /// <summary>Draws the "Back" button of a step's action bar.</summary>
        private void DrawBackButton(WizardStep previousStep)
        {
            if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Back", $"Go back to {StepLabels[(int)previousStep]}. Nothing is lost."),
                    ActionButtonHeight, GUILayout.Width(70f)))
            {
                GoToStep(previousStep);
                GUIUtility.ExitGUI();
            }
        }

        // =====================================================================
        // Banners shown above every step after Import
        // =====================================================================

        private void DrawStepBanners()
        {
            if (currentStep <= WizardStep.WaitForImport)
            {
                return;
            }

            DrawOutdatedFbxBanner();
            DrawDiffFailureWarning();
        }

        /// <summary>
        /// A copied FBX was replaced again after its patch was generated: offer to regenerate the patch and
        /// review that avatar's replacements again (the other avatars keep their work).
        /// </summary>
        private void DrawOutdatedFbxBanner()
        {
            List<AvatarEntry> outdated = avatarEntries.Where(entry => entry.needsProcessing).ToList();
            if (outdated.Count == 0)
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(18f));
                    string names = string.Join(", ", outdated.Select(entry => $"'{GetEntryDisplayName(entry)}'"));
                    EditorGUILayout.LabelField(
                        $"The FBX of {names} changed after its patch files were made, so its patch and replacements are out of date.",
                        PawlygonEditorUI.RichLabelStyle);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Update Patch & Review", "Generate the patch files again and review the replacements of these avatars."),
                            GUILayout.Width(170f), GUILayout.Height(22f)))
                    {
                        ProcessEntriesAndReview(outdated, skippedImportWait: false);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUILayout.Space(SectionSpacing);
        }

        // =====================================================================
        // Avatar list (Replacements, FX Check)
        // =====================================================================

        /// <summary>
        /// Draws the avatars as a vertical list with one status badge each, and selects the clicked one.
        /// Returns true when the selection changed (the caller must call <c>GUIUtility.ExitGUI()</c>).
        /// Draws nothing for a single avatar.
        /// </summary>
        private bool DrawAvatarList(string title, string progress, Func<AvatarEntry, (string Text, PawlygonEditorUI.BadgeKind Kind, string Tooltip)> getBadge)
        {
            if (avatarEntries.Count <= 1)
            {
                return false;
            }

            int clicked = -1;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(title, PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(progress, mutedMiniStyle);
                }

                EditorGUILayout.Space(2f);

                for (int i = 0; i < avatarEntries.Count; i++)
                {
                    if (DrawAvatarListRow(i, getBadge(avatarEntries[i])))
                    {
                        clicked = i;
                    }
                }
            }

            EditorGUILayout.Space(SectionSpacing);

            if (clicked < 0 || clicked == selectedEntryIndex)
            {
                return false;
            }

            selectedEntryIndex = clicked;
            scrollPosition = Vector2.zero;
            return true;
        }

        private bool DrawAvatarListRow(int index, (string Text, PawlygonEditorUI.BadgeKind Kind, string Tooltip) badge)
        {
            AvatarEntry entry = avatarEntries[index];
            bool selected = index == selectedEntryIndex;
            Rect row = EditorGUILayout.GetControlRect(false, 22f);

            if (Event.current.type == EventType.Repaint && selected)
            {
                Color accent = PawlygonEditorUI.InfoColor;
                EditorGUI.DrawRect(row, new Color(accent.r, accent.g, accent.b, EditorGUIUtility.isProSkin ? 0.16f : 0.12f));
                EditorGUI.DrawRect(new Rect(row.x, row.y, 3f, row.height), accent);
            }

            var badgeContent = new GUIContent(PawlygonEditorUI.BadgeText(badge.Text, badge.Kind));
            float badgeWidth = rowBadgeStyle.CalcSize(badgeContent).x;
            var nameRect = new Rect(row.x + 8f, row.y, Mathf.Max(0f, row.width - badgeWidth - 16f), row.height);
            var badgeRect = new Rect(row.xMax - badgeWidth - 4f, row.y, badgeWidth, row.height);

            GUI.Label(nameRect, $"{index + 1}.  {GetEntryDisplayName(entry)}", selected ? rowSelectedLabelStyle : rowLabelStyle);
            GUI.Label(badgeRect, badgeContent, rowBadgeStyle);

            EditorGUIUtility.AddCursorRect(row, MouseCursor.Link);
            return GUI.Button(row, new GUIContent(string.Empty, badge.Tooltip), GUIStyle.none);
        }

        // =====================================================================
        // Project caches (Prefabs and Finish)
        // =====================================================================

        private bool projectCachesValid;
        private bool cachedVrcftAvailable;
        private string cachedVrcftPrefabPath;
        private bool[] cachedVrcftPresent = Array.Empty<bool>();
        private bool cachedPatcherHubAvailable;
        private List<AvatarEntry> cachedEntriesMissingPatchConfig = new List<AvatarEntry>();

        private void InvalidateProjectCaches()
        {
            projectCachesValid = false;
        }

        /// <summary>
        /// Scans the prefabs for the VRCFT setup and the patch folders for missing PatcherHub configs. Called
        /// on Layout events only and cached until the project changes, so the steps never scan per repaint.
        /// </summary>
        private void EnsureProjectCaches()
        {
            if (projectCachesValid)
            {
                return;
            }

            projectCachesValid = true;
            cachedVrcftAvailable = IsVrcftPackageAvailable(out cachedVrcftPrefabPath);
            GameObject vrcftPrefab = cachedVrcftAvailable ? AssetDatabase.LoadAssetAtPath<GameObject>(cachedVrcftPrefabPath) : null;
            cachedVrcftPresent = avatarEntries
                .Select(entry => vrcftPrefab != null && HasVrcftSetup(AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath), vrcftPrefab))
                .ToArray();
            cachedPatcherHubAvailable = FTPatchConfigGenerator.IsPatcherHubAvailable();
            cachedEntriesMissingPatchConfig = cachedPatcherHubAvailable ? GetEntriesMissingPatchConfig() : new List<AvatarEntry>();
        }

        private bool IsVrcftOnEntry(int index)
        {
            return index >= 0 && index < cachedVrcftPresent.Length && cachedVrcftPresent[index];
        }

        // =====================================================================
        // Start over
        // =====================================================================

        /// <summary>
        /// Asks before forgetting the progress, and offers to keep the avatars and folder names so a similar
        /// setup can be started straight away. Files already created are never touched.
        /// </summary>
        private void ConfirmStartOver()
        {
            int choice = EditorUtility.DisplayDialogComplex(
                "Start Over",
                "Start a new setup? The wizard forgets the progress of this one; the files it created stay in your project.\n\n" +
                "Keep Inputs keeps the source FBXs, prefabs and folder names so you can adjust them. " +
                "Clear Everything empties the form.",
                "Keep Inputs",
                "Cancel",
                "Clear Everything");

            if (choice == 1)
            {
                return;
            }

            ResetWizard(keepInputs: choice == 0);
        }

        private void ResetWizard(bool keepInputs)
        {
            if (keepInputs)
            {
                avatarEntries = avatarEntries
                    .Select(entry => new AvatarEntry
                    {
                        sourceFbx = entry.sourceFbx,
                        sourcePrefab = entry.sourcePrefab,
                        avatarFolderName = entry.avatarFolderName
                    })
                    .ToList();
            }
            else
            {
                mainFolderName = DefaultMainFolderName;
                useSeparateFolderPerAvatar = false;
                sharedAvatarFolderName = string.Empty;
                avatarEntries = new List<AvatarEntry> { new AvatarEntry() };
            }

            selectedEntryIndex = 0;
            EditorApplication.delayCall -= TryContinueAfterImport;
            pendingImportTransition = false;
            ResetSetupValidation();
            ResetFXCheckState();
            InvalidateProjectCaches();
            currentStep = WizardStep.Setup;
            furthestStep = WizardStep.Setup;
            scrollPosition = Vector2.zero;
            status.Clear();
            Repaint();
        }

        // =====================================================================
        // Shared helpers
        // =====================================================================

        private AvatarEntry GetSelectedEntry()
        {
            if (avatarEntries.Count == 0)
            {
                return null;
            }

            selectedEntryIndex = Mathf.Clamp(selectedEntryIndex, 0, avatarEntries.Count - 1);
            return avatarEntries[selectedEntryIndex];
        }

        private void EnsureAtLeastOneEntry()
        {
            if (avatarEntries == null)
            {
                avatarEntries = new List<AvatarEntry>();
            }

            if (avatarEntries.Count == 0)
            {
                avatarEntries.Add(new AvatarEntry());
            }
        }

        private static string GetEntryDisplayName(AvatarEntry entry)
        {
            if (entry.sourceFbx != null)
            {
                return entry.sourceFbx.name;
            }

            if (!string.IsNullOrWhiteSpace(entry.avatarFolderName))
            {
                return entry.avatarFolderName;
            }

            return "Avatar";
        }

        private static string Plural(int count, string singular, string plural = null)
        {
            return $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int newLineIndex = text.IndexOf('\n');
            return newLineIndex >= 0 ? text.Substring(0, newLineIndex).TrimEnd() : text;
        }

        private static bool IsAssetPathInsideFolder(string assetPath, string folderPath)
        {
            if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(folderPath))
            {
                return false;
            }

            string normalizedFolder = PawlygonEditorUtils.NormalizeAssetPath(folderPath).TrimEnd('/') + "/";
            return PawlygonEditorUtils.NormalizeAssetPath(assetPath).StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, PawlygonEditorUtils.NormalizeAssetPath(assetPath)));
        }

        private static bool AssetFileExists(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && File.Exists(ToAbsolutePath(assetPath));
        }

        private static long GetAssetWriteTimeUtcTicks(string assetPath)
        {
            string absolutePath = ToAbsolutePath(assetPath);
            return File.Exists(absolutePath) ? File.GetLastWriteTimeUtc(absolutePath).Ticks : 0L;
        }

        private static long GetAssetFileSize(string assetPath)
        {
            string absolutePath = ToAbsolutePath(assetPath);
            return File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : 0L;
        }

        /// <summary>Selects and pings the asset at <paramref name="assetPath"/> (e.g. an avatar folder).</summary>
        private static void PingAssetPath(string assetPath)
        {
            UnityEngine.Object asset = string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
            {
                return;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>A read-only "Label   path" row whose path can be selected and copied.</summary>
        private void DrawPathRow(string label, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, mutedMiniStyle, GUILayout.Width(100f));
                EditorGUILayout.SelectableLabel(path, pathLabelStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        /// <summary>Draws a validation or problem message with a warning icon below a field.</summary>
        private void DrawFieldError(string message)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(18f));
                GUILayout.Label(message, fieldErrorStyle);
            }
        }

        /// <summary>Draws a "Title   [Badge]" row for status summaries.</summary>
        private static void DrawBadgeRow(string title, string badge, PawlygonEditorUI.BadgeKind kind, string tooltip, float titleWidth = 110f)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(title, GUILayout.Width(titleWidth));
                PawlygonEditorUI.DrawBadge(badge, kind, tooltip);
                GUILayout.FlexibleSpace();
            }
        }

        private void EnsureStyles()
        {
            if (cardStyle != null && stylesBuiltForProSkin == EditorGUIUtility.isProSkin)
            {
                return;
            }

            stylesBuiltForProSkin = EditorGUIUtility.isProSkin;

            cardStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8) };

            rowLabelStyle = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            rowSelectedLabelStyle = new GUIStyle(rowLabelStyle) { fontStyle = FontStyle.Bold };
            rowBadgeStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true, alignment = TextAnchor.MiddleRight };

            fieldErrorStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true };
            fieldErrorStyle.normal.textColor = PawlygonEditorUI.WarningColor;

            mutedMiniStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };
            mutedMiniStyle.normal.textColor = PawlygonEditorUI.MutedColor;

            pathLabelStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };
        }
    }
}
