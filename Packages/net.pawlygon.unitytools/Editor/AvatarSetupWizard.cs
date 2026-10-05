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
    public partial class AvatarSetupWizard : EditorWindow
    {
        private const string DefaultMainFolderName = "!Pawlygon";
        private const string DefaultAvatarName = "Avatar Name";
        private const float SectionSpacing = 10f;
        private const float StepBadgeHeight = 28f;
        private const int SharedSceneGridColumns = 3;
        private const float SharedSceneGridSpacingX = 1.5f;
        private const float SharedSceneGridSpacingZ = 1.5f;
        private const string VrcftPrefabGuid = "ca618adb2c3333545a1f36d72a73a3ef";
        private const string VrcftPackageListingUrl = "https://vcc.pawlygon.net/";
        private const string PatcherHubLatestReleaseApiUrl = "https://api.github.com/repos/PawlygonStudio/PatcherHub/releases/latest";
        private const int PatcherHubReleaseInfoTimeoutSeconds = 30;
        private const int PatcherHubDownloadTimeoutSeconds = 300;

        private const int SourceFbxPickerControlId = 9001;
        private const int SourcePrefabPickerControlId = 9002;

        [SerializeField] private string mainFolderName = DefaultMainFolderName;
        [SerializeField] private bool useSeparateFolderPerAvatar;
        [SerializeField] private string sharedAvatarFolderName = DefaultAvatarName;
        [SerializeField] private List<AvatarEntry> avatarEntries = new List<AvatarEntry> { new AvatarEntry() };

        [SerializeField] private WizardStep currentStep = WizardStep.Setup;
        [SerializeField] private int selectedEntryIndex;

        private Vector2 mainContentScrollPosition;
        private string statusMessage = string.Empty;
        private bool pendingImportTransition;
        private string vrcftSetupStatusMessage = string.Empty;
        private string patcherHubImportStatusMessage = string.Empty;
        private bool patcherHubImportedThisSession;
        private GUIStyle stepStyle;
        private GUIStyle currentStepStyle;
        private GUIStyle helpBoxPadding10_8;
        private GUIStyle helpBoxPadding8_6;
        private GUIStyle helpBoxPadding10_6;
        private GUIStyle helpBoxPadding5;
        private GUIStyle boldLabel14;
        private GUIStyle boldLabel13;
        private bool fxCheckAnalyzed;
        private readonly HashSet<int> fxExpandedLayers = new HashSet<int>();
        private readonly HashSet<int> fxExpandedBlinkLayers = new HashSet<int>();
        private bool fxShowAllBlinkLayers;

        private enum WizardStep
        {
            Setup,
            WaitForImport,
            SelectMeshes,
            Prefabs,
            FXCheck,
            Complete
        }

        [Serializable]
        private class AvatarEntry
        {
            public GameObject sourceFbx;
            public GameObject sourcePrefab;
            public string avatarFolderName = DefaultAvatarName;
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
            public bool isMeshReviewComplete;
            public string reviewResultLabel;
            public AnimatorReplacementState animatorReplacement = new AnimatorReplacementState();
            public List<MeshSelectionState> meshSelections = new List<MeshSelectionState>();
            public string copiedFxControllerPath;
            public string originalFxControllerPath;
            [NonSerialized] public FXGestureCheckerCore.AnalysisResult fxAnalysisResult;
            public bool fxCheckComplete;
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

            /// <summary>The FBX renderer's bones differ from the prefab renderer's (names, order or count).</summary>
            public bool bonesDiffer;

            /// <summary>The bones differ and cannot all be found on the prefab, so they cannot be remapped.</summary>
            public bool bonesUnresolved;

            /// <summary>Why the bones cannot be remapped (missing or ambiguous bone names).</summary>
            public string boneIssue;
        }

        [MenuItem("!Pawlygon/Avatar Setup Wizard")]
        public static void ShowWindow()
        {
            AvatarSetupWizard window = GetWindow<AvatarSetupWizard>();
            window.titleContent = new GUIContent("Avatar Setup Wizard");
            window.minSize = new Vector2(620f, 560f);
        }

        private void OnEnable()
        {
            FBXImportDetector.FbxReimported += HandleFbxReimported;
            EnsureAtLeastOneEntry();
        }

        private void OnDisable()
        {
            FBXImportDetector.FbxReimported -= HandleFbxReimported;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
        }

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();
            EnsureAtLeastOneEntry();
            HandleObjectPickerSelection();

            PawlygonEditorUI.DrawHeader(
                "Pawlygon Avatar Setup Wizard",
                "Tool to duplicate avatars, prepare face tracking assets, and build ready-to-edit prefabs.");
            DrawStepIndicator();
            EditorGUILayout.Space(SectionSpacing);

            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
            {
                mainContentScrollPosition = EditorGUILayout.BeginScrollView(mainContentScrollPosition, GUILayout.ExpandHeight(true));

                DrawDiffFailureWarning();

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

                if (!string.IsNullOrEmpty(statusMessage))
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
                }

                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(8f);
            PawlygonEditorUI.DrawFooter();
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

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int newLineIndex = text.IndexOf('\n');
            return newLineIndex >= 0 ? text.Substring(0, newLineIndex).TrimEnd() : text;
        }

        private AvatarEntry GetSelectedEntry()
        {
            if (avatarEntries.Count == 0)
            {
                return null;
            }

            selectedEntryIndex = Mathf.Clamp(selectedEntryIndex, 0, avatarEntries.Count - 1);
            return avatarEntries[selectedEntryIndex];
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

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, PawlygonEditorUtils.NormalizeAssetPath(assetPath)));
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

        private static void DrawReadOnlyPathField(string label, string value)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(label, value ?? string.Empty);
            }
        }

        private void EnsureStyles()
        {
            if (stepStyle != null)
            {
                return;
            }

            stepStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 6, 6)
            };

            currentStepStyle = new GUIStyle(stepStyle);
            currentStepStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.8f, 0.92f, 1f)
                : new Color(0.1f, 0.35f, 0.7f);

            helpBoxPadding10_8 = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8) };
            helpBoxPadding8_6 = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6) };
            helpBoxPadding10_6 = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 6, 6) };
            helpBoxPadding5 = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(5, 5, 5, 5) };
            boldLabel14 = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            boldLabel13 = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        }

        private void DrawStepIndicator()
        {
            using (new EditorGUILayout.HorizontalScope(helpBoxPadding5))
            {
                DrawStepBadge(WizardStep.Setup, "1. Setup");
                DrawStepArrow();
                DrawStepBadge(WizardStep.WaitForImport, "2. Import");
                DrawStepArrow();
                DrawStepBadge(WizardStep.SelectMeshes, "3. Replacements");
                DrawStepArrow();
                DrawStepBadge(WizardStep.Prefabs, "4. Prefabs");
                DrawStepArrow();
                DrawStepBadge(WizardStep.FXCheck, "5. FX Check");
                DrawStepArrow();
                DrawStepBadge(WizardStep.Complete, "6. Finish");
            }
        }

        private void DrawStepBadge(WizardStep step, string label)
        {
            bool isPast = currentStep > step;
            bool isCurrent = currentStep == step;
            GUIStyle style = isCurrent ? currentStepStyle : stepStyle;

            Color savedColor = style.normal.textColor;
            if (isPast)
            {
                style.normal.textColor = new Color(0.3f, 0.7f, 0.3f);
            }

            GUIContent content = isPast
                ? new GUIContent($" {label}", EditorGUIUtility.IconContent("TestPassed").image)
                : new GUIContent(label);

            using (new EditorGUILayout.VerticalScope(GUILayout.Height(StepBadgeHeight)))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(content, style, GUILayout.ExpandWidth(true));
                GUILayout.FlexibleSpace();
            }

            style.normal.textColor = savedColor;
        }

        private static void DrawStepArrow()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Height(StepBadgeHeight)))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(EditorGUIUtility.IconContent("IN foldout").image, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(16f));
                GUILayout.FlexibleSpace();
            }
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

        private void ResetWizard()
        {
            mainFolderName = DefaultMainFolderName;
            useSeparateFolderPerAvatar = false;
            sharedAvatarFolderName = DefaultAvatarName;
            avatarEntries = new List<AvatarEntry> { new AvatarEntry() };
            selectedEntryIndex = 0;
            statusMessage = string.Empty;
            vrcftSetupStatusMessage = string.Empty;
            patcherHubImportStatusMessage = string.Empty;
            patcherHubImportedThisSession = false;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            pendingImportTransition = false;
            fxCheckAnalyzed = false;
            fxExpandedLayers.Clear();
            fxExpandedBlinkLayers.Clear();
            fxShowAllBlinkLayers = false;
            currentStep = WizardStep.Setup;
        }
    }
}
