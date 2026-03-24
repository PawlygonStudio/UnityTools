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
    public class AvatarSetupWizard : EditorWindow
    {
        private const string DefaultMainFolderName = "!Pawlygon";
        private const string DefaultAvatarName = "Avatar Name";
        private const int MaxImportLoadAttempts = 10;
        private const float SectionSpacing = 10f;
        private const float StepBadgeHeight = 28f;
        private const int SharedSceneGridColumns = 3;
        private const float SharedSceneGridSpacingX = 1.5f;
        private const float SharedSceneGridSpacingZ = 1.5f;
        private const string VrcftPrefabGuid = "ca618adb2c3333545a1f36d72a73a3ef";
        private const string VrcftPackageListingUrl = "https://vcc.pawlygon.net/";
        private const string PatcherHubLatestReleaseApiUrl = "https://api.github.com/repos/PawlygonStudio/PatcherHub/releases/latest";

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
        private int importLoadAttempts;
        private string vrcftSetupStatusMessage = string.Empty;
        private string patcherHubImportStatusMessage = string.Empty;
        private bool patcherHubImportedThisSession;
        private GUIStyle stepStyle;
        private GUIStyle currentStepStyle;
        private GUIStyle fxLayerHeaderStyle;
        private GUIStyle fxGuardedLabelStyle;
        private GUIStyle helpBoxPadding10_8;
        private GUIStyle helpBoxPadding8_6;
        private GUIStyle helpBoxPadding10_6;
        private GUIStyle helpBoxPadding5;
        private GUIStyle boldLabel14;
        private GUIStyle boldLabel13;
        private bool fxCheckAnalyzed;
        private readonly HashSet<int> fxExpandedLayers = new HashSet<int>();

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
            public long watchedFbxWriteTimeUtcTicks;
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
        }

        private class RendererInfo
        {
            public SkinnedMeshRenderer Renderer;
            public string ObjectName;
            public string RelativePath;
            public string MeshName;
        }

        private class AnimatorInfo
        {
            public Animator Animator;
            public string ObjectName;
            public string RelativePath;
            public Avatar Avatar;
            public int PriorityScore;
        }

        [Serializable]
        private class GitHubReleaseInfo
        {
            public string tag_name;
            public string name;
            public GitHubReleaseAsset[] assets;
        }

        [Serializable]
        private class GitHubReleaseAsset
        {
            public string name;
            public string browser_download_url;
        }

        [MenuItem("!Pawlygon/Avatar Setup Wizard")]
        public static void ShowWindow()
        {
            AvatarSetupWizard window = GetWindow<AvatarSetupWizard>();
            window.titleContent = new GUIContent("Avatar Setup Wizard");
            window.minSize = new Vector2(620f, 560f);
        }

        private const string ImportPatcherHubMenuPath = "!Pawlygon/Import Latest PatcherHub";

        [MenuItem(ImportPatcherHubMenuPath, validate = true)]
        private static bool ValidateImportPatcherHub()
        {
            Menu.SetChecked(ImportPatcherHubMenuPath, FTPatchConfigGenerator.IsPatcherHubAvailable());
            return true;
        }

        [MenuItem(ImportPatcherHubMenuPath, priority = 200)]
        private static void ImportPatcherHubMenuItem()
        {
            if (FTPatchConfigGenerator.IsPatcherHubAvailable())
            {
                bool reimport = EditorUtility.DisplayDialog(
                    "PatcherHub Already Installed",
                    "PatcherHub is already installed in this project. Do you want to re-import the latest version?",
                    "Re-import", "Cancel");
                if (!reimport) return;
            }

            try
            {
                GitHubReleaseInfo releaseInfo = FetchLatestPatcherHubReleaseInfo();
                GitHubReleaseAsset unityPackageAsset = releaseInfo?.assets?.FirstOrDefault(asset =>
                    !string.IsNullOrEmpty(asset.name) &&
                    asset.name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(asset.browser_download_url));

                if (unityPackageAsset == null)
                {
                    EditorUtility.DisplayDialog("Import Failed",
                        "Could not find a .unitypackage asset in the latest PatcherHub release.", "OK");
                    return;
                }

                string downloadPath = Path.Combine(Path.GetTempPath(), unityPackageAsset.name);
                DownloadFile(unityPackageAsset.browser_download_url, downloadPath);
                AssetDatabase.ImportPackage(downloadPath, false);
                Debug.Log($"[AvatarSetupWizard] Imported {unityPackageAsset.name} from {releaseInfo.tag_name}.");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Import Failed",
                    $"Failed to import PatcherHub: {ex.Message}", "OK");
            }
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

        private void DrawSetupStep()
        {
            bool hasMultipleEntries = avatarEntries.Count > 1;

            PawlygonEditorUI.DrawSection(
                "Setup",
                "Choose the source assets and create the working avatar structure.",
                () =>
                {
                    mainFolderName = EditorGUILayout.TextField("Main Folder Name", mainFolderName);

                    if (hasMultipleEntries)
                    {
                        useSeparateFolderPerAvatar = EditorGUILayout.ToggleLeft("Use Separate Folder Per Avatar", useSeparateFolderPerAvatar);
                    }
                    else
                    {
                        useSeparateFolderPerAvatar = false;
                    }

                    if (!useSeparateFolderPerAvatar)
                    {
                        sharedAvatarFolderName = EditorGUILayout.TextField(hasMultipleEntries ? "Shared Avatar Folder" : "Avatar Folder Name", sharedAvatarFolderName);
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    for (int i = 0; i < avatarEntries.Count; i++)
                    {
                        DrawAvatarEntryEditor(i, avatarEntries[i]);
                        EditorGUILayout.Space(6f);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Add Avatar", GUILayout.Height(28f)))
                        {
                            avatarEntries.Add(new AvatarEntry());
                        }

                        GUILayout.FlexibleSpace();
                        EditorGUILayout.LabelField($"{avatarEntries.Count} avatar entr{(avatarEntries.Count == 1 ? "y" : "ies")}", EditorStyles.miniBoldLabel, GUILayout.Width(110f));
                    }

                    string validationMessage = GetSetupValidationMessage();

                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                    using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(validationMessage)))
                    {
                        if (PawlygonEditorUI.DrawPrimaryButton("Create Avatar Structure", 36f))
                        {
                            CreateAvatarStructures();
                        }
                    }

                    if (!string.IsNullOrEmpty(validationMessage))
                    {
                        EditorGUILayout.HelpBox(validationMessage, MessageType.Warning);
                    }
                });
        }

        private void DrawAvatarEntryEditor(int index, AvatarEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                if (avatarEntries.Count > 1)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Avatar {index + 1}", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();

                        using (new EditorGUI.DisabledScope(avatarEntries.Count <= 1))
                        {
                            if (GUILayout.Button("Remove", GUILayout.Width(72f)))
                            {
                                avatarEntries.RemoveAt(index);
                                selectedEntryIndex = Mathf.Clamp(selectedEntryIndex, 0, avatarEntries.Count - 1);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }

                entry.sourceFbx = DrawFilteredAssetField("Source FBX", entry.sourceFbx, "fbx", SourceFbxPickerControlId + index * 2);
                entry.sourcePrefab = DrawFilteredAssetField("Source Prefab", entry.sourcePrefab, "prefab", SourcePrefabPickerControlId + index * 2);

                if (useSeparateFolderPerAvatar)
                {
                    entry.avatarFolderName = EditorGUILayout.TextField("Avatar Folder Name", entry.avatarFolderName);
                }

                string rowError = GetEntryValidationMessage(index, entry);
                if (!string.IsNullOrEmpty(rowError))
                {
                    EditorGUILayout.HelpBox(rowError, MessageType.None);
                }
            }
        }

        private void DrawWaitForImportStep()
        {
            PawlygonEditorUI.DrawSection(
                "Import Modified FBX",
                "Replace each copied FBX on disk with its edited version, or choose an edited FBX below. Continue after every model below shows Updated.",
                () =>
                {
                    int importedCount = avatarEntries.Count(entry => entry.hasImportedModifiedFbx);
                    EditorGUILayout.HelpBox($"Progress: {importedCount} / {avatarEntries.Count} modified FBXs detected.", MessageType.Info);
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    DrawImportStatusSummary();
                    EditorGUILayout.Space(SectionSpacing);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (PawlygonEditorUI.DrawPrimaryButton("Continue After Import", 34f))
                        {
                            if (AreAllEntriesImportedAndLoadable())
                            {
                                int generatedDiffCount = GenerateDiffFilesForEntries(importedOnly: true);
                                MoveToMeshSelection(generatedDiffCount, skippedImportWait: false);
                            }
                            else
                            {
                                statusMessage = "Not every modified FBX is ready yet. Finish importing all copied FBXs, then continue.";
                            }
                        }

                        if (GUILayout.Button("Skip Waiting", GUILayout.Height(34f)))
                        {
                            if (CanLoadImportedAssets())
                            {
                                int generatedDiffCount = GenerateDiffFilesForEntries(importedOnly: false);
                                MoveToMeshSelection(generatedDiffCount, skippedImportWait: true);
                            }
                            else
                            {
                                statusMessage = "The copied FBX and prefab assets are not loadable yet. Wait for Unity to finish importing before skipping.";
                            }
                        }
                    }
                });
        }

        private void DrawMeshSelectionStep()
        {
            AvatarEntry selectedEntry = GetSelectedEntry();
            if (selectedEntry == null)
            {
                statusMessage = "No avatar entries are available for mesh review.";
                currentStep = WizardStep.Setup;
                return;
            }

            PawlygonEditorUI.DrawSection(
                "Select Replacements",
                "Review one avatar at a time. Apply the selected mesh and humanoid rig replacements or explicitly skip that avatar.",
                () =>
                {
                    DrawEntrySelectionToolbar();
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    DrawMeshReviewSummary(selectedEntry);
                    EditorGUILayout.Space(SectionSpacing);

                    if (selectedEntry.meshSelections.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No skinned mesh renderer mappings were found between the duplicated FBX and prefab.", MessageType.Warning);
                    }
                    else
                    {
                        DrawMeshSelectionToolbar(selectedEntry);
                        EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                        using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                        {
                            DrawAnimatorReplacementRow(selectedEntry.animatorReplacement);

                            foreach (MeshSelectionState meshSelection in selectedEntry.meshSelections)
                            {
                                DrawMeshSelectionRow(meshSelection);
                            }
                        }
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Skip This Avatar", GUILayout.Height(34f)))
                        {
                            SkipEntryReview(selectedEntry);
                        }

                        using (new EditorGUI.DisabledScope(!HasAnySelectedReplacement(selectedEntry)))
                        {
                            if (PawlygonEditorUI.DrawPrimaryButton("Apply Selected Replacements", 34f))
                            {
                                ApplySelectedReplacementsToPrefab(selectedEntry);
                            }
                        }
                    }
                });
        }

        private void DrawCompleteStep()
        {
            PawlygonEditorUI.DrawSection(
                "Finish",
                "Your avatar setup is ready. Optional prefab tools were available in the previous step.",
                () =>
                {
                    GUIContent successIcon = EditorGUIUtility.IconContent("TestPassed");
                    EditorGUILayout.LabelField(new GUIContent(" Batch avatar setup completed successfully", successIcon.image), boldLabel14);
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                    foreach (AvatarEntry entry in avatarEntries)
                    {
                        using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                        {
                            EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                            if (!string.IsNullOrEmpty(entry.reviewResultLabel))
                            {
                                EditorGUILayout.LabelField($"Result: {entry.reviewResultLabel}", PawlygonEditorUI.RichMiniLabelStyle);
                            }
                            DrawPathSummary(entry, includeAvatarRoot: true, includeDiffGenerator: true);
                        }
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    if (PawlygonEditorUI.DrawPrimaryButton("Start Over", 34f))
                    {
                        ResetWizard();
                    }
                });
        }

        private void DrawPrefabsStep()
        {
            bool isVrcftAvailable = IsVrcftPackageAvailable(out string vrcftPrefabPath);

            PawlygonEditorUI.DrawSection(
                "Prefabs",
                "Optional tools for adding prefab helpers and distributing patch assets.",
                () =>
                {
                    DrawVrcftPrefabBlock(isVrcftAvailable, vrcftPrefabPath);
                    EditorGUILayout.Space(SectionSpacing);
                    DrawPatcherHubBlock();
                    EditorGUILayout.Space(SectionSpacing);

                    if (PawlygonEditorUI.DrawPrimaryButton("Continue", 34f))
                    {
                        currentStep = WizardStep.FXCheck;
                        statusMessage = string.Empty;
                    }
                });
        }

        private void DrawVrcftPrefabBlock(bool isVrcftAvailable, string vrcftPrefabPath)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Pawlygon VRCFT", boldLabel13);
                EditorGUILayout.Space(2f);

                if (isVrcftAvailable)
                {
                    EditorGUILayout.LabelField("Package detected. Add the VRCFT setup to each generated prefab.", PawlygonEditorUI.SubLabelStyle);
                    EditorGUILayout.Space(8f);

                    if (PawlygonEditorUI.DrawPrimaryButton("Add VRCFT To Prefabs", 32f))
                    {
                        AddVrcftSetupToPrefabs(vrcftPrefabPath);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox($"Install Pawlygon - VRC Facetracking from VCC at {VrcftPackageListingUrl}. Once installed, this wizard can auto-add the VRCFT setup for you.", MessageType.Info);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Refresh", GUILayout.Height(28f)))
                        {
                            bool refreshedAvailability = IsVrcftPackageAvailable(out _);
                            vrcftSetupStatusMessage = refreshedAvailability
                                ? "Pawlygon VRCFT package detected. You can now add the setup to the generated prefabs."
                                : "Pawlygon VRCFT package is still not available in this project.";
                        }
                    }
                }

                if (!string.IsNullOrEmpty(vrcftSetupStatusMessage))
                {
                    EditorGUILayout.Space(6f);
                    EditorGUILayout.HelpBox(vrcftSetupStatusMessage, MessageType.None);
                }
            }
        }

        private void DrawPatcherHubBlock()
        {
            bool isPatcherHubInstalled = FTPatchConfigGenerator.IsPatcherHubAvailable();

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("PatcherHub", boldLabel13);
                EditorGUILayout.Space(2f);

                if (isPatcherHubInstalled)
                {
                    EditorGUILayout.LabelField("PatcherHub detected. You can re-import to update to the latest version.", PawlygonEditorUI.SubLabelStyle);
                }
                else
                {
                    EditorGUILayout.LabelField("Import the latest PatcherHub unitypackage so end users can patch the face-tracking changes onto the avatar FBX model.", PawlygonEditorUI.SubLabelStyle);
                }

                EditorGUILayout.Space(8f);

                if (PawlygonEditorUI.DrawPrimaryButton(isPatcherHubInstalled ? "Re-import Latest PatcherHub" : "Import Latest PatcherHub", 32f))
                {
                    ImportLatestPatcherHub();
                }

                if (patcherHubImportedThisSession || !string.IsNullOrEmpty(patcherHubImportStatusMessage))
                {
                    EditorGUILayout.Space(6f);
                    EditorGUILayout.HelpBox(patcherHubImportStatusMessage, MessageType.None);
                }
            }
        }

        private void CreateAvatarStructures()
        {
            statusMessage = string.Empty;

            if (!ValidateSetupInputs(out string validationMessage))
            {
                statusMessage = validationMessage;
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                statusMessage = "Scene creation was cancelled before saving current work.";
                return;
            }

            string sanitizedMainFolderName = mainFolderName.Trim();
            string effectiveSharedAvatarFolderName = useSeparateFolderPerAvatar ? string.Empty : sharedAvatarFolderName.Trim();

            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath("Assets", sanitizedMainFolderName));

            if (useSeparateFolderPerAvatar)
            {
                for (int i = 0; i < avatarEntries.Count; i++)
                {
                    if (!CreateSeparateAvatarStructure(avatarEntries[i], sanitizedMainFolderName, i == avatarEntries.Count - 1))
                    {
                        return;
                    }
                }
            }
            else
            {
                if (!CreateSharedAvatarStructure(sanitizedMainFolderName, effectiveSharedAvatarFolderName))
                {
                    return;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            foreach (AvatarEntry entry in avatarEntries)
            {
                entry.watchedFbxWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(entry.copiedFbxPath);
                entry.hasImportedModifiedFbx = false;
                entry.isMeshReviewComplete = false;
                entry.reviewResultLabel = string.Empty;
                entry.animatorReplacement = new AnimatorReplacementState();
                entry.meshSelections = new List<MeshSelectionState>();
            }

            vrcftSetupStatusMessage = string.Empty;
            patcherHubImportStatusMessage = string.Empty;
            patcherHubImportedThisSession = false;

            selectedEntryIndex = 0;
            currentStep = WizardStep.WaitForImport;
            statusMessage = $"Created {avatarEntries.Count} avatar entr{(avatarEntries.Count == 1 ? "y" : "ies")}. Replace each copied FBX with its modified version and let Unity import them all.";
            Repaint();
        }

        private bool CreateSeparateAvatarStructure(AvatarEntry entry, string sanitizedMainFolderName, bool openAfterCreate)
        {
            string avatarFolderName = entry.avatarFolderName.Trim();
            string sourceFbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
            string sourcePrefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);

            entry.avatarRootPath = PawlygonEditorUtils.CombineAssetPath("Assets", sanitizedMainFolderName, avatarFolderName);
            string fbxFolderPath = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "FBX");
            string prefabFolderPath = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "Prefabs");
            string internalFolderPath = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "Internal");
            string scenesFolderPath = PawlygonEditorUtils.CombineAssetPath(internalFolderPath, "Scenes");

            string copiedFbxFileName = GetCopiedFbxFileName(sourceFbxPath);
            string copiedPrefabFileName = Path.GetFileName(sourcePrefabPath);
            string sceneFileName = $"{avatarFolderName} - Pawlygon VRCFT.unity";
            string diffGeneratorFileName = $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} Face Tracking DiffGenerator.asset";

            entry.copiedFbxPath = PawlygonEditorUtils.CombineAssetPath(fbxFolderPath, copiedFbxFileName);
            entry.copiedPrefabPath = PawlygonEditorUtils.CombineAssetPath(prefabFolderPath, copiedPrefabFileName);
            entry.createdScenePath = PawlygonEditorUtils.CombineAssetPath(scenesFolderPath, sceneFileName);
            entry.diffGeneratorAssetPath = PawlygonEditorUtils.CombineAssetPath(internalFolderPath, diffGeneratorFileName);

            if (AssetDatabase.IsValidFolder(entry.avatarRootPath))
            {
                EditorUtility.DisplayDialog("Avatar Already Exists", $"The folder '{entry.avatarRootPath}' already exists. Choose a new avatar name or remove the existing folder first.", "OK");
                return false;
            }

            PawlygonEditorUtils.EnsureFolderExists(entry.avatarRootPath);
            PawlygonEditorUtils.EnsureFolderExists(fbxFolderPath);
            PawlygonEditorUtils.EnsureFolderExists(prefabFolderPath);
            PawlygonEditorUtils.EnsureFolderExists(internalFolderPath);
            PawlygonEditorUtils.EnsureFolderExists(scenesFolderPath);

            if (!CopyAvatarAssets(entry, sourceFbxPath, sourcePrefabPath))
            {
                return false;
            }

            if (!CreateSceneAsset(entry.createdScenePath, new[] { entry.copiedPrefabPath }))
            {
                EditorUtility.DisplayDialog("Scene Creation Failed", $"The working scene could not be created for '{avatarFolderName}'.", "OK");
                return false;
            }

            if (!CreateDiffGeneratorAsset(entry, entry.diffGeneratorAssetPath))
            {
                return false;
            }

            if (openAfterCreate)
            {
                EditorSceneManager.OpenScene(entry.createdScenePath, OpenSceneMode.Single);
            }

            return true;
        }

        private bool CreateSharedAvatarStructure(string sanitizedMainFolderName, string effectiveSharedAvatarFolderName)
        {
            string avatarRootPath = PawlygonEditorUtils.CombineAssetPath("Assets", sanitizedMainFolderName, effectiveSharedAvatarFolderName);
            string fbxFolderPath = PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "FBX");
            string prefabFolderPath = PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "Prefabs");
            string internalFolderPath = PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "Internal");
            string scenesFolderPath = PawlygonEditorUtils.CombineAssetPath(internalFolderPath, "Scenes");
            string sharedScenePath = PawlygonEditorUtils.CombineAssetPath(scenesFolderPath, $"{effectiveSharedAvatarFolderName} - Pawlygon VRCFT.unity");

            if (AssetDatabase.IsValidFolder(avatarRootPath))
            {
                EditorUtility.DisplayDialog("Avatar Already Exists", $"The folder '{avatarRootPath}' already exists. Choose a new avatar name or remove the existing folder first.", "OK");
                return false;
            }

            PawlygonEditorUtils.EnsureFolderExists(avatarRootPath);
            PawlygonEditorUtils.EnsureFolderExists(fbxFolderPath);
            PawlygonEditorUtils.EnsureFolderExists(prefabFolderPath);
            PawlygonEditorUtils.EnsureFolderExists(internalFolderPath);
            PawlygonEditorUtils.EnsureFolderExists(scenesFolderPath);

            var copiedPrefabPaths = new List<string>();

            foreach (AvatarEntry entry in avatarEntries)
            {
                string sourceFbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
                string sourcePrefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);
                string copiedFbxFileName = GetCopiedFbxFileName(sourceFbxPath);
                string copiedPrefabFileName = Path.GetFileName(sourcePrefabPath);
                string diffGeneratorFileName = $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} Face Tracking DiffGenerator.asset";

                entry.avatarRootPath = avatarRootPath;
                entry.copiedFbxPath = PawlygonEditorUtils.CombineAssetPath(fbxFolderPath, copiedFbxFileName);
                entry.copiedPrefabPath = PawlygonEditorUtils.CombineAssetPath(prefabFolderPath, copiedPrefabFileName);
                entry.createdScenePath = sharedScenePath;
                entry.diffGeneratorAssetPath = PawlygonEditorUtils.CombineAssetPath(internalFolderPath, diffGeneratorFileName);

                if (!CopyAvatarAssets(entry, sourceFbxPath, sourcePrefabPath))
                {
                    return false;
                }

                copiedPrefabPaths.Add(entry.copiedPrefabPath);
            }

            if (!CreateSceneAsset(sharedScenePath, copiedPrefabPaths))
            {
                EditorUtility.DisplayDialog("Scene Creation Failed", "The shared working scene could not be created.", "OK");
                return false;
            }

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (!CreateDiffGeneratorAsset(entry, entry.diffGeneratorAssetPath))
                {
                    return false;
                }
            }

            EditorSceneManager.OpenScene(sharedScenePath, OpenSceneMode.Single);
            return true;
        }

        private static bool CopyAvatarAssets(AvatarEntry entry, string sourceFbxPath, string sourcePrefabPath)
        {
            if (!AssetDatabase.CopyAsset(sourceFbxPath, entry.copiedFbxPath))
            {
                EditorUtility.DisplayDialog("Copy Failed", $"The source FBX could not be copied for '{Path.GetFileNameWithoutExtension(sourceFbxPath)}'.", "OK");
                return false;
            }

            if (!AssetDatabase.CopyAsset(sourcePrefabPath, entry.copiedPrefabPath))
            {
                EditorUtility.DisplayDialog("Copy Failed", $"The source prefab could not be copied for '{Path.GetFileName(sourcePrefabPath)}'.", "OK");
                return false;
            }

            return true;
        }

        private static bool CreateSceneAsset(string sceneAssetPath, IReadOnlyList<string> prefabAssetPaths)
        {
            Scene newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);

            try
            {
                for (int i = 0; i < prefabAssetPaths.Count; i++)
                {
                    GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPaths[i]);
                    if (prefabAsset == null)
                    {
                        continue;
                    }

                    GameObject instance = PrefabUtility.InstantiatePrefab(prefabAsset, newScene) as GameObject;
                    if (instance != null)
                    {
                        instance.transform.position = GetGridPosition(i);
                    }
                }

                EditorSceneManager.MarkSceneDirty(newScene);
                return EditorSceneManager.SaveScene(newScene, sceneAssetPath);
            }
            finally
            {
                if (newScene.IsValid())
                {
                    EditorSceneManager.CloseScene(newScene, true);
                }
            }
        }

        private bool CreateDiffGeneratorAsset(AvatarEntry entry, string assetPath)
        {
            AssetDatabase.ImportAsset(entry.copiedFbxPath, ImportAssetOptions.ForceSynchronousImport);

            GameObject copiedFbx = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath);
            DefaultAsset outputDirectory = AssetDatabase.LoadAssetAtPath<DefaultAsset>(entry.avatarRootPath);
            if (copiedFbx == null || outputDirectory == null)
            {
                EditorUtility.DisplayDialog("Diff Generator Failed", $"Could not initialize the diff generator asset for '{GetEntryDisplayName(entry)}'.", "OK");
                return false;
            }

            var diffGenerator = ScriptableObject.CreateInstance<FTDiffGenerator>();
            diffGenerator.originalModelFbx = entry.sourceFbx;
            diffGenerator.modifiedModelFbx = copiedFbx;
            diffGenerator.outputDirectory = outputDirectory;
            AssetDatabase.CreateAsset(diffGenerator, assetPath);
            return true;
        }

        private bool IsVrcftPackageAvailable(out string prefabAssetPath)
        {
            prefabAssetPath = AssetDatabase.GUIDToAssetPath(VrcftPrefabGuid);
            return !string.IsNullOrEmpty(prefabAssetPath) && AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) != null;
        }

        private void AddVrcftSetupToPrefabs(string vrcftPrefabPath)
        {
            GameObject vrcftPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(vrcftPrefabPath);
            if (vrcftPrefabAsset == null)
            {
                vrcftSetupStatusMessage = "The VRCFT prefab could not be loaded from the installed package.";
                return;
            }

            int addedCount = 0;
            int alreadyConfiguredCount = 0;

            foreach (AvatarEntry entry in avatarEntries)
            {
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);

                try
                {
                    Transform container = prefabRoot.transform.Find("!Pawlygon - VRCFT");
                    if (container == null)
                    {
                        var containerObject = new GameObject("!Pawlygon - VRCFT");
                        containerObject.transform.SetParent(prefabRoot.transform, false);
                        container = containerObject.transform;
                    }

                    bool hasExistingSetup = false;
                    foreach (Transform child in container)
                    {
                        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
                        if (source == vrcftPrefabAsset)
                        {
                            hasExistingSetup = true;
                            break;
                        }
                    }

                    if (hasExistingSetup)
                    {
                        alreadyConfiguredCount++;
                        continue;
                    }

                    GameObject instance = PrefabUtility.InstantiatePrefab(vrcftPrefabAsset) as GameObject;
                    if (instance != null)
                    {
                        instance.transform.SetParent(container, false);
                        addedCount++;
                    }

                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            vrcftSetupStatusMessage = $"Added VRCFT setup to {addedCount} prefab{(addedCount == 1 ? string.Empty : "s")}";
            if (alreadyConfiguredCount > 0)
            {
                vrcftSetupStatusMessage += $", already present on {alreadyConfiguredCount}.";
            }
            else
            {
                vrcftSetupStatusMessage += ".";
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private void ImportLatestPatcherHub()
        {
            patcherHubImportStatusMessage = "Downloading latest PatcherHub release...";

            try
            {
                GitHubReleaseInfo releaseInfo = FetchLatestPatcherHubReleaseInfo();
                GitHubReleaseAsset unityPackageAsset = releaseInfo?.assets?.FirstOrDefault(asset =>
                    !string.IsNullOrEmpty(asset.name) &&
                    asset.name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(asset.browser_download_url));

                if (unityPackageAsset == null)
                {
                    patcherHubImportStatusMessage = "Could not find a .unitypackage asset in the latest PatcherHub release.";
                    return;
                }

                string downloadPath = Path.Combine(Path.GetTempPath(), unityPackageAsset.name);
                DownloadFile(unityPackageAsset.browser_download_url, downloadPath);
                AssetDatabase.ImportPackage(downloadPath, false);
                patcherHubImportedThisSession = true;
                patcherHubImportStatusMessage = $"Imported {unityPackageAsset.name} from {releaseInfo.tag_name}.";
            }
            catch (Exception ex)
            {
                patcherHubImportStatusMessage = $"Failed to import PatcherHub: {ex.Message}";
            }
        }

        private static GitHubReleaseInfo FetchLatestPatcherHubReleaseInfo()
        {
            using var request = UnityWebRequest.Get(PatcherHubLatestReleaseApiUrl);
            request.SetRequestHeader("User-Agent", "PawlygonUnityTools");
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                System.Threading.Thread.Sleep(10);
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException(request.error);
            }

            GitHubReleaseInfo releaseInfo = JsonUtility.FromJson<GitHubReleaseInfo>(request.downloadHandler.text);
            if (releaseInfo == null)
            {
                throw new InvalidOperationException("GitHub returned an invalid release payload.");
            }

            return releaseInfo;
        }

        private static void DownloadFile(string url, string destinationPath)
        {
            using var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("User-Agent", "PawlygonUnityTools");
            request.downloadHandler = new DownloadHandlerFile(destinationPath);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                System.Threading.Thread.Sleep(10);
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException(request.error);
            }
        }

        private bool ValidateSetupInputs(out string validationMessage)
        {
            validationMessage = GetSetupValidationMessage();
            return string.IsNullOrEmpty(validationMessage);
        }

        private string GetSetupValidationMessage()
        {
            if (avatarEntries.Count == 0)
            {
                return "Add at least one avatar entry.";
            }

            if (string.IsNullOrWhiteSpace(mainFolderName))
            {
                return "Enter a main folder name.";
            }

            if (mainFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "The main folder name contains invalid characters.";
            }

            if (useSeparateFolderPerAvatar)
            {
                if (avatarEntries.Any(entry => string.IsNullOrWhiteSpace(entry.avatarFolderName)))
                {
                    return "Every avatar entry needs an avatar folder name.";
                }

                if (avatarEntries.Any(entry => entry.avatarFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    return "One or more avatar folder names contain invalid characters.";
                }

                if (avatarEntries.GroupBy(entry => entry.avatarFolderName.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                {
                    return "Avatar folder names must be unique when using separate folders.";
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(sharedAvatarFolderName))
                {
                    return "Enter a shared avatar folder name.";
                }

                if (sharedAvatarFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return "The shared avatar folder name contains invalid characters.";
                }

                if (HasDuplicateSharedTargetNames(GetCopiedFbxFileName, avatarEntries.Select(entry => entry.sourceFbx)))
                {
                    return "Shared folder mode would create duplicate copied FBX names. Rename the source FBXs or use separate folders.";
                }

                if (HasDuplicateSharedTargetNames(path => Path.GetFileName(path), avatarEntries.Select(entry => entry.sourcePrefab)))
                {
                    return "Shared folder mode would create duplicate prefab names. Rename the source prefabs or use separate folders.";
                }

                if (HasDuplicateSharedTargetNames(path => $"{Path.GetFileNameWithoutExtension(path)} Face Tracking DiffGenerator.asset", avatarEntries.Select(entry => entry.sourceFbx)))
                {
                    return "Shared folder mode would create duplicate Face Tracking DiffGenerator asset names. Rename the source FBXs or use separate folders.";
                }
            }

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                string entryError = GetEntryValidationMessage(i, avatarEntries[i]);
                if (!string.IsNullOrEmpty(entryError))
                {
                    return entryError;
                }
            }

            return string.Empty;
        }

        private string GetEntryValidationMessage(int index, AvatarEntry entry)
        {
            if (entry.sourceFbx == null)
            {
                return $"Avatar {index + 1}: select an FBX asset to duplicate.";
            }

            if (entry.sourcePrefab == null)
            {
                return $"Avatar {index + 1}: select a prefab asset to duplicate.";
            }

            string fbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
            if (!string.Equals(Path.GetExtension(fbxPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return $"Avatar {index + 1}: the source FBX must point to an .fbx asset.";
            }

            string prefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);
            if (!string.Equals(Path.GetExtension(prefabPath), ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return $"Avatar {index + 1}: the source prefab must point to a .prefab asset.";
            }

            if (useSeparateFolderPerAvatar && string.IsNullOrWhiteSpace(entry.avatarFolderName))
            {
                return $"Avatar {index + 1}: enter an avatar folder name.";
            }

            if (useSeparateFolderPerAvatar && entry.avatarFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return $"Avatar {index + 1}: the avatar folder name contains invalid characters.";
            }

            return string.Empty;
        }

        private void HandleFbxReimported(string importedAssetPath)
        {
            if (currentStep != WizardStep.WaitForImport)
            {
                return;
            }

            string normalizedImportedAssetPath = PawlygonEditorUtils.NormalizeAssetPath(importedAssetPath);
            bool matchedAny = false;

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (string.IsNullOrEmpty(entry.copiedFbxPath))
                {
                    continue;
                }

                if (!string.Equals(PawlygonEditorUtils.NormalizeAssetPath(entry.copiedFbxPath), normalizedImportedAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchedAny |= TryMarkEntryAsImported(entry, force: false);
            }

            if (matchedAny)
            {
                QueueMoveToMeshSelection();
            }
        }

        private void QueueMoveToMeshSelection()
        {
            if (pendingImportTransition)
            {
                return;
            }

            pendingImportTransition = true;
            importLoadAttempts = 0;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            EditorApplication.delayCall += TryMoveToMeshSelectionAfterImport;
        }

        private bool TryMarkEntryAsImported(AvatarEntry entry, bool force)
        {
            if (entry == null || string.IsNullOrEmpty(entry.copiedFbxPath))
            {
                return false;
            }

            long currentWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(entry.copiedFbxPath);
            if (!force && currentWriteTimeUtcTicks <= entry.watchedFbxWriteTimeUtcTicks)
            {
                return false;
            }

            entry.watchedFbxWriteTimeUtcTicks = currentWriteTimeUtcTicks;
            entry.hasImportedModifiedFbx = true;
            return true;
        }

        private void PromptForModifiedFbx(AvatarEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            string initialDirectory = string.Empty;

            if (!string.IsNullOrEmpty(entry.copiedFbxPath))
            {
                string copiedFbxAbsolutePath = ToAbsolutePath(entry.copiedFbxPath);
                if (File.Exists(copiedFbxAbsolutePath))
                {
                    initialDirectory = Path.GetDirectoryName(copiedFbxAbsolutePath) ?? string.Empty;
                }
            }

            string selectedPath = EditorUtility.OpenFilePanel("Choose Modified FBX", initialDirectory, "fbx");
            if (string.IsNullOrEmpty(selectedPath))
            {
                return;
            }

            ImportModifiedFbxForEntry(entry, selectedPath);
        }

        private void ImportModifiedFbxForEntry(AvatarEntry entry, string sourcePath)
        {
            if (entry == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                statusMessage = "Select a single .fbx file to import.";
                return;
            }

            string resolvedSourcePath = ResolveAbsoluteFilePath(sourcePath);
            if (string.IsNullOrEmpty(resolvedSourcePath) || !File.Exists(resolvedSourcePath))
            {
                statusMessage = "The selected FBX file could not be found.";
                return;
            }

            if (!string.Equals(Path.GetExtension(resolvedSourcePath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                statusMessage = "Only .fbx files can be imported here.";
                return;
            }

            if (string.IsNullOrEmpty(entry.copiedFbxPath))
            {
                statusMessage = $"No copied FBX path is available for '{GetEntryDisplayName(entry)}'.";
                return;
            }

            string targetAbsolutePath = ToAbsolutePath(entry.copiedFbxPath);

            try
            {
                if (!string.Equals(Path.GetFullPath(resolvedSourcePath), Path.GetFullPath(targetAbsolutePath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(resolvedSourcePath, targetAbsolutePath, true);
                }

                AssetDatabase.ImportAsset(entry.copiedFbxPath, ImportAssetOptions.ForceSynchronousImport);

                if (AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath) == null)
                {
                    statusMessage = $"Unity could not import '{Path.GetFileName(resolvedSourcePath)}' as an FBX.";
                    return;
                }

                TryMarkEntryAsImported(entry, force: true);
                statusMessage = $"Imported '{Path.GetFileName(resolvedSourcePath)}' into '{Path.GetFileName(entry.copiedFbxPath)}'.";
                QueueMoveToMeshSelection();
            }
            catch (Exception exception)
            {
                statusMessage = $"Failed to import '{Path.GetFileName(resolvedSourcePath)}': {exception.Message}";
            }

            Repaint();
        }

        private string ResolveAbsoluteFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalizedPath = PawlygonEditorUtils.NormalizeAssetPath(path);
            return Path.IsPathRooted(normalizedPath)
                ? Path.GetFullPath(normalizedPath)
                : ToAbsolutePath(normalizedPath);
        }

        private void TryMoveToMeshSelectionAfterImport()
        {
            pendingImportTransition = false;

            if (!avatarEntries.All(entry => entry.hasImportedModifiedFbx))
            {
                statusMessage = $"Waiting for {avatarEntries.Count(entry => !entry.hasImportedModifiedFbx)} more modified FBX import(s).";
                Repaint();
                return;
            }

            importLoadAttempts++;

            if (CanLoadImportedAssets())
            {
                int generatedDiffCount = GenerateDiffFilesForEntries(importedOnly: true);
                MoveToMeshSelection(generatedDiffCount, skippedImportWait: false);
                return;
            }

            if (importLoadAttempts >= MaxImportLoadAttempts)
            {
                Debug.LogWarning("[AvatarSetupWizard] Imported FBXs are still unavailable after delayed load attempts.");
                statusMessage = "All modified FBXs were detected, but Unity has not finished making them loadable yet. Wait a moment, then continue manually if needed.";
                Repaint();
                return;
            }

            pendingImportTransition = true;
            Debug.Log($"[AvatarSetupWizard] FBX assets not ready yet, retrying delayed load attempt {importLoadAttempts + 1}/{MaxImportLoadAttempts}.");
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            EditorApplication.delayCall += TryMoveToMeshSelectionAfterImport;
        }

        private bool CanLoadImportedAssets()
        {
            return avatarEntries.All(entry =>
                AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath) != null &&
                AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath) != null);
        }

        private bool AreAllEntriesImportedAndLoadable()
        {
            return avatarEntries.All(entry => entry.hasImportedModifiedFbx) && CanLoadImportedAssets();
        }

        private int GenerateDiffFilesForEntries(bool importedOnly)
        {
            int generatedCount = 0;

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (importedOnly && !entry.hasImportedModifiedFbx)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.diffGeneratorAssetPath))
                {
                    continue;
                }

                FTDiffGenerator diffGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath);
                if (diffGenerator == null)
                {
                    Debug.LogWarning($"[AvatarSetupWizard] Could not load diff generator asset at '{entry.diffGeneratorAssetPath}'.");
                    continue;
                }

                diffGenerator.GenerateDiffFiles();
                generatedCount++;

                // Generate FTPatchConfig with wizard context if PatcherHub is installed
                if (FTPatchConfigGenerator.IsPatcherHubAvailable())
                {
                    string baseName = diffGenerator.GetBaseName();
                    if (!string.IsNullOrEmpty(baseName))
                    {
                        string patcherFolder = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "patcher");
                        string diffFilesFolder = PawlygonEditorUtils.CombineAssetPath(patcherFolder, "data", "DiffFiles");
                        string fbxFolder = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "FBX");

                        GameObject copiedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);

                        var configContext = new FTPatchConfigGenerator.ConfigContext
                        {
                            OriginalFbx = diffGenerator.originalModelFbx,
                            AvatarDisplayName = entry.avatarFolderName?.Trim(),
                            FbxDiffAssetPath = PawlygonEditorUtils.CombineAssetPath(diffFilesFolder, baseName + ".hdiff"),
                            MetaDiffAssetPath = PawlygonEditorUtils.CombineAssetPath(diffFilesFolder, baseName + "Meta.hdiff"),
                            ConfigOutputFolder = patcherFolder,
                            FbxOutputPath = fbxFolder,
                            PatchedPrefabs = copiedPrefab != null ? new List<GameObject> { copiedPrefab } : null,
                            ConfigAssetName = (entry.avatarFolderName?.Trim() ?? baseName) + " FTPatchConfig"
                        };

                        FTPatchConfigGenerator.GenerateConfig(configContext);
                    }
                }
            }

            return generatedCount;
        }

        private void MoveToMeshSelection(int generatedDiffCount, bool skippedImportWait)
        {
            foreach (AvatarEntry entry in avatarEntries)
            {
                LoadMeshSelections(entry);
                entry.isMeshReviewComplete = false;
                entry.reviewResultLabel = string.Empty;
            }

            selectedEntryIndex = Mathf.Clamp(FindNextIncompleteEntryIndex(0), 0, avatarEntries.Count - 1);
            currentStep = WizardStep.SelectMeshes;
            if (skippedImportWait)
            {
                statusMessage = generatedDiffCount > 0
                    ? $"Skipped the import wait and regenerated diff files for {generatedDiffCount} avatar entr{(generatedDiffCount == 1 ? "y" : "ies")}. Review each avatar entry and apply the mesh and rig replacements you want."
                    : "Skipped the import wait. Review each avatar entry and apply the mesh and rig replacements you want.";
            }
            else
            {
                statusMessage = generatedDiffCount > 0
                    ? $"All modified FBXs imported. Regenerated diff files for {generatedDiffCount} avatar entr{(generatedDiffCount == 1 ? "y" : "ies")}. Review each avatar entry and apply the mesh and rig replacements you want."
                    : "All modified FBXs imported. Review each avatar entry and apply the mesh and rig replacements you want.";
            }

            Repaint();
        }

        private void LoadMeshSelections(AvatarEntry entry)
        {
            entry.animatorReplacement = new AnimatorReplacementState();
            entry.meshSelections = new List<MeshSelectionState>();

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath);
            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);

            if (fbxRoot == null)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not load FBX asset at '{entry.copiedFbxPath}'.");
                return;
            }

            if (prefabRoot == null)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not load prefab asset at '{entry.copiedPrefabPath}'.");
                return;
            }

            entry.animatorReplacement = CreateAnimatorReplacementState(fbxRoot, prefabRoot, entry.copiedFbxPath);

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            List<RendererInfo> fbxRenderers = GetRendererInfos(fbxRoot, fbxMeshSubAssets);
            List<RendererInfo> prefabRenderers = GetRendererInfos(prefabRoot, meshSubAssets: null);
            var usedPrefabPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            entry.meshSelections = fbxRenderers
                .Select(fbxRenderer => CreateMeshSelectionState(fbxRenderer, prefabRenderers, usedPrefabPaths))
                .OrderBy(selection => selection.fbxRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            PopulateUnifiedBlendshapeWarnings(entry.meshSelections, fbxMeshSubAssets, PawlygonEditorUtils.RequiredUnifiedExpressionBlendshapes);
        }

        private void ApplySelectedReplacementsToPrefab(AvatarEntry entry)
        {
            List<MeshSelectionState> selectedMappings = entry.meshSelections
                .Where(selection => selection.selected && selection.hasMatch)
                .ToList();

            bool shouldReplaceAnimator = entry.animatorReplacement != null &&
                entry.animatorReplacement.selected &&
                entry.animatorReplacement.hasPrefabAnimator &&
                entry.animatorReplacement.hasHumanoidAvatar;

            if (selectedMappings.Count == 0 && !shouldReplaceAnimator)
            {
                EditorUtility.DisplayDialog("No Replacements Selected", "Select at least one mapped skinned mesh renderer or the humanoid rig replacement to update on the prefab.", "OK");
                return;
            }

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            if (selectedMappings.Count > 0 && fbxMeshSubAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("FBX Missing Meshes", "No mesh sub-assets could be loaded from the duplicated FBX.", "OK");
                return;
            }

            Avatar replacementAvatar = shouldReplaceAnimator
                ? LoadReplacementAvatar(entry.animatorReplacement, entry.copiedFbxPath)
                : null;

            if (shouldReplaceAnimator && !IsValidHumanoidAvatar(replacementAvatar))
            {
                EditorUtility.DisplayDialog("FBX Missing Humanoid Avatar", "No valid humanoid avatar could be loaded from the duplicated FBX.", "OK");
                return;
            }

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);

            try
            {
                Dictionary<string, SkinnedMeshRenderer> prefabRendererLookup = prefabRoot
                    .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .ToDictionary(
                        renderer => GetRelativeTransformPath(renderer.transform),
                        renderer => renderer,
                        StringComparer.OrdinalIgnoreCase);

                Dictionary<string, Animator> prefabAnimatorLookup = prefabRoot
                    .GetComponentsInChildren<Animator>(true)
                    .ToDictionary(
                        animator => GetRelativeTransformPath(animator.transform),
                        animator => animator,
                        StringComparer.OrdinalIgnoreCase);

                int replacedCount = 0;
                bool replacedAnimator = false;

                foreach (MeshSelectionState mapping in selectedMappings)
                {
                    if (string.IsNullOrEmpty(mapping.fbxMeshName))
                    {
                        continue;
                    }

                    if (!fbxMeshSubAssets.TryGetValue(mapping.fbxMeshName, out Mesh fbxMesh))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] Mesh sub-asset '{mapping.fbxMeshName}' not found in FBX '{entry.copiedFbxPath}'.");
                        continue;
                    }

                    if (!prefabRendererLookup.TryGetValue(mapping.prefabRelativePath, out SkinnedMeshRenderer prefabRenderer))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] No SkinnedMeshRenderer at relative path '{mapping.prefabRelativePath}' in prefab.");
                        continue;
                    }

                    prefabRenderer.sharedMesh = fbxMesh;
                    replacedCount++;
                }

                if (shouldReplaceAnimator)
                {
                    if (!prefabAnimatorLookup.TryGetValue(entry.animatorReplacement.prefabAnimatorRelativePath, out Animator prefabAnimator))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] No Animator at relative path '{entry.animatorReplacement.prefabAnimatorRelativePath}' in prefab.");
                    }
                    else
                    {
                        prefabAnimator.avatar = replacementAvatar;
                        replacedAnimator = true;
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
                CompleteEntryReview(entry, "Applied");
                statusMessage = BuildReplacementStatusMessage(entry, replacedCount, replacedAnimator);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private void SkipEntryReview(AvatarEntry entry)
        {
            if (!EditorUtility.DisplayDialog("Skip Avatar Review", $"Skip mesh and rig replacement for '{GetEntryDisplayName(entry)}'?", "Skip", "Cancel"))
            {
                return;
            }

            CompleteEntryReview(entry, "Skipped");
            statusMessage = $"Skipped mesh and rig replacement for '{GetEntryDisplayName(entry)}'.";
        }

        private void CompleteEntryReview(AvatarEntry entry, string reviewResultLabel)
        {
            entry.isMeshReviewComplete = true;
            entry.reviewResultLabel = reviewResultLabel;

            if (avatarEntries.All(item => item.isMeshReviewComplete))
            {
                currentStep = WizardStep.Prefabs;
                statusMessage = string.Empty;
                return;
            }

            int nextIndex = FindNextIncompleteEntryIndex(selectedEntryIndex + 1);
            if (nextIndex < 0)
            {
                nextIndex = FindNextIncompleteEntryIndex(0);
            }

            if (nextIndex >= 0)
            {
                selectedEntryIndex = nextIndex;
            }
        }

        private int FindNextIncompleteEntryIndex(int startIndex)
        {
            for (int i = startIndex; i < avatarEntries.Count; i++)
            {
                if (!avatarEntries[i].isMeshReviewComplete)
                {
                    return i;
                }
            }

            return -1;
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

        private void DrawEntrySelectionToolbar()
        {
            string[] labels = avatarEntries
                .Select(entry =>
                {
                    string prefix = entry.isMeshReviewComplete ? $"[{entry.reviewResultLabel}] " : string.Empty;
                    return prefix + GetEntryDisplayName(entry);
                })
                .ToArray();

            selectedEntryIndex = GUILayout.Toolbar(Mathf.Clamp(selectedEntryIndex, 0, labels.Length - 1), labels);
        }

        private void DrawImportStatusSummary()
        {
            using (new EditorGUILayout.VerticalScope(helpBoxPadding10_8))
            {
                foreach (AvatarEntry entry in avatarEntries)
                {
                    EditorGUILayout.BeginVertical(helpBoxPadding8_6);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUIContent statusIcon = entry.hasImportedModifiedFbx
                            ? EditorGUIUtility.IconContent("TestPassed")
                            : EditorGUIUtility.IconContent("console.warnicon.sml");

                        GUILayout.Label(statusIcon, GUILayout.Width(20f), GUILayout.Height(18f));

                        using (new EditorGUILayout.VerticalScope())
                        {
                            EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                            EditorGUILayout.LabelField(entry.hasImportedModifiedFbx ? "Updated" : "Waiting for updated FBX", PawlygonEditorUI.RichMiniLabelStyle);
                            if (!string.IsNullOrEmpty(entry.copiedFbxPath))
                            {
                                EditorGUILayout.LabelField(entry.copiedFbxPath, PawlygonEditorUI.RichMiniLabelStyle);
                            }
                        }

                        GUILayout.FlexibleSpace();

                        if (GUILayout.Button("Choose FBX...", GUILayout.Width(100f), GUILayout.Height(24f)))
                        {
                            PromptForModifiedFbx(entry);
                        }
                    }

                    EditorGUILayout.LabelField("Choose an updated FBX to replace this copied file", PawlygonEditorUI.RichMiniLabelStyle);
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.Space(3f);
                }
            }
        }

        private void DrawMeshReviewSummary(AvatarEntry entry)
        {
            int matchedCount = entry.meshSelections.Count(selection => selection.hasMatch);
            int selectedCount = entry.meshSelections.Count(selection => selection.selected && selection.hasMatch);
            string rigStatus = GetAnimatorSelectionSummary(entry.animatorReplacement);
            bool hasMissingUnifiedBlendshapes = entry.meshSelections.Any(HasMissingUnifiedBlendshapesWarning);
            bool hasCompleteUnifiedBlendshapes = entry.meshSelections.Any(HasCompleteUnifiedBlendshapesInfo);

            using (new EditorGUILayout.VerticalScope(helpBoxPadding10_8))
            {
                EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{matchedCount} matched renderer{(matchedCount == 1 ? string.Empty : "s")}, {selectedCount} selected", PawlygonEditorUI.RichMiniLabelStyle);
                EditorGUILayout.LabelField($"Humanoid rig: {rigStatus}", PawlygonEditorUI.RichMiniLabelStyle);
                if (hasMissingUnifiedBlendshapes)
                {
                    EditorGUILayout.LabelField("Warning: Missing Unified Expression Blendshapes", PawlygonEditorUI.RichMiniLabelStyle);
                }
                else if (hasCompleteUnifiedBlendshapes)
                {
                    EditorGUILayout.LabelField("Unified Expression Blendshapes: Complete", PawlygonEditorUI.RichMiniLabelStyle);
                }

                DrawReadOnlyPathField("Modified FBX", entry.copiedFbxPath);
                DrawReadOnlyPathField("Target Prefab", entry.copiedPrefabPath);
            }
        }

        private void DrawPathSummary(AvatarEntry entry, bool includeAvatarRoot, bool includeDiffGenerator)
        {
            using (new EditorGUILayout.VerticalScope(helpBoxPadding10_8))
            {
                if (includeAvatarRoot)
                {
                    DrawReadOnlyPathField("Avatar Root", entry.avatarRootPath);
                }

                if (!string.IsNullOrEmpty(entry.copiedFbxPath))
                {
                    DrawReadOnlyPathField("Modified FBX", entry.copiedFbxPath);
                }

                if (!string.IsNullOrEmpty(entry.copiedPrefabPath))
                {
                    DrawReadOnlyPathField("Target Prefab", entry.copiedPrefabPath);
                }

                if (!string.IsNullOrEmpty(entry.createdScenePath))
                {
                    DrawReadOnlyPathField("Working Scene", entry.createdScenePath);
                }

                if (includeDiffGenerator && !string.IsNullOrEmpty(entry.diffGeneratorAssetPath))
                {
                    DrawReadOnlyPathField("Diff Generator", entry.diffGeneratorAssetPath);
                }

                if (!string.IsNullOrEmpty(entry.copiedFxControllerPath))
                {
                    DrawReadOnlyPathField("FX Controller", entry.copiedFxControllerPath);
                }
            }
        }

        private void DrawMeshSelectionToolbar(AvatarEntry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(90f)))
                {
                    SetMeshSelectionState(entry, true);
                }

                if (GUILayout.Button("Deselect All", GUILayout.Width(90f)))
                {
                    SetMeshSelectionState(entry, false);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{GetSelectedReplacementCount(entry)} selected", EditorStyles.miniBoldLabel);
            }
        }

        private void DrawAnimatorReplacementRow(AnimatorReplacementState animatorReplacement)
        {
            animatorReplacement ??= new AnimatorReplacementState();

            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = animatorReplacement.hasPrefabAnimator && animatorReplacement.hasHumanoidAvatar
                ? originalColor
                : new Color(1f, 0.9f, 0.7f, 0.5f);

            using (new EditorGUILayout.VerticalScope(helpBoxPadding8_6))
            {
                GUI.backgroundColor = originalColor;

                using (new EditorGUI.DisabledScope(!animatorReplacement.hasPrefabAnimator || !animatorReplacement.hasHumanoidAvatar))
                {
                    string label = string.IsNullOrEmpty(animatorReplacement.fbxAvatarName)
                        ? "Primary Animator Rig"
                        : $"Primary Animator Rig ({animatorReplacement.fbxAvatarName})";
                    animatorReplacement.selected = EditorGUILayout.ToggleLeft(label, animatorReplacement.selected, EditorStyles.boldLabel);
                }

                GUIContent statusIcon = animatorReplacement.hasPrefabAnimator && animatorReplacement.hasHumanoidAvatar
                    ? EditorGUIUtility.IconContent("TestPassed")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                string matchText;
                if (!animatorReplacement.hasPrefabAnimator)
                {
                    matchText = "No primary Animator found on the prefab";
                }
                else if (!animatorReplacement.hasHumanoidAvatar)
                {
                    matchText = "No humanoid FBX avatar found";
                }
                else
                {
                    matchText = "Ready to replace the primary humanoid rig";
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(statusIcon, GUILayout.Width(18f), GUILayout.Height(16f));
                    EditorGUILayout.LabelField(matchText, PawlygonEditorUI.RichMiniLabelStyle);
                }

            }

            EditorGUILayout.Space(2f);
        }

        private void DrawMeshSelectionRow(MeshSelectionState meshSelection)
        {
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = meshSelection.hasMatch ? originalColor : new Color(1f, 0.9f, 0.7f, 0.5f);

            using (new EditorGUILayout.VerticalScope(helpBoxPadding8_6))
            {
                GUI.backgroundColor = originalColor;

                using (new EditorGUI.DisabledScope(!meshSelection.hasMatch))
                {
                    string meshLabel = string.IsNullOrEmpty(meshSelection.fbxMeshName)
                        ? meshSelection.fbxObjectName
                        : $"{meshSelection.fbxObjectName} ({meshSelection.fbxMeshName})";
                    meshSelection.selected = EditorGUILayout.ToggleLeft(meshLabel, meshSelection.selected, EditorStyles.boldLabel);
                }

                EditorGUILayout.LabelField($"FBX: {meshSelection.fbxRelativePath}", PawlygonEditorUI.RichMiniLabelStyle);

                GUIContent statusIcon = meshSelection.hasMatch
                    ? EditorGUIUtility.IconContent("TestPassed")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                string matchText = meshSelection.hasMatch
                    ? $"<b>Prefab:</b> {meshSelection.prefabRelativePath} ({meshSelection.prefabMeshName}) [{meshSelection.matchReason}]"
                    : "<b>Prefab:</b> <color=#c27725>No matching skinned mesh renderer found</color>";

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(statusIcon, GUILayout.Width(18f), GUILayout.Height(16f));
                    EditorGUILayout.LabelField(matchText, PawlygonEditorUI.RichMiniLabelStyle);
                }

                if (HasMissingUnifiedBlendshapesWarning(meshSelection))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(16f));
                        EditorGUILayout.LabelField("Missing Unified Expression Blendshapes", PawlygonEditorUI.RichMiniLabelStyle);
                    }

                    meshSelection.showUnifiedBlendshapeWarningDetails = EditorGUILayout.Foldout(
                        meshSelection.showUnifiedBlendshapeWarningDetails,
                        $"Show missing blendshapes ({meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length})",
                        true);

                    if (meshSelection.showUnifiedBlendshapeWarningDetails)
                    {
                        using (new EditorGUILayout.VerticalScope(helpBoxPadding10_6))
                        {
                            foreach (string blendshapeName in meshSelection.missingRequiredUnifiedBlendshapesOnFbx)
                            {
                                EditorGUILayout.LabelField($"- {blendshapeName}", PawlygonEditorUI.RichMiniLabelStyle);
                            }
                        }
                    }
                }
                else if (HasCompleteUnifiedBlendshapesInfo(meshSelection))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("TestPassed"), GUILayout.Width(18f), GUILayout.Height(16f));
                        EditorGUILayout.LabelField("All Unified Expression Blendshapes found", PawlygonEditorUI.RichMiniLabelStyle);
                    }
                }
            }

            EditorGUILayout.Space(2f);
        }

        private static void SetMeshSelectionState(AvatarEntry entry, bool selected)
        {
            foreach (MeshSelectionState meshSelection in entry.meshSelections)
            {
                if (meshSelection.hasMatch)
                {
                    meshSelection.selected = selected;
                }
            }
        }

        private static bool HasAnySelectedReplacement(AvatarEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

            return entry.meshSelections.Any(selection => selection.selected && selection.hasMatch) ||
                   (entry.animatorReplacement != null &&
                    entry.animatorReplacement.selected &&
                    entry.animatorReplacement.hasPrefabAnimator &&
                    entry.animatorReplacement.hasHumanoidAvatar);
        }

        private static int GetSelectedReplacementCount(AvatarEntry entry)
        {
            if (entry == null)
            {
                return 0;
            }

            int selectedMeshCount = entry.meshSelections.Count(selection => selection.selected && selection.hasMatch);
            int selectedRigCount = entry.animatorReplacement != null &&
                entry.animatorReplacement.selected &&
                entry.animatorReplacement.hasPrefabAnimator &&
                entry.animatorReplacement.hasHumanoidAvatar
                ? 1
                : 0;

            return selectedMeshCount + selectedRigCount;
        }

        private static string GetAnimatorSelectionSummary(AnimatorReplacementState animatorReplacement)
        {
            if (animatorReplacement == null || !animatorReplacement.hasPrefabAnimator)
            {
                return "unavailable";
            }

            if (!animatorReplacement.hasHumanoidAvatar)
            {
                return "no humanoid FBX avatar found";
            }

            return animatorReplacement.selected
                ? $"selected ({animatorReplacement.fbxAvatarName})"
                : $"available ({animatorReplacement.fbxAvatarName})";
        }

        private static string BuildReplacementStatusMessage(AvatarEntry entry, int replacedMeshCount, bool replacedAnimator)
        {
            string displayName = GetEntryDisplayName(entry);

            if (replacedMeshCount > 0 && replacedAnimator)
            {
                return $"Updated {replacedMeshCount} mesh reference(s) and the primary Animator rig on '{displayName}'.";
            }

            if (replacedMeshCount > 0)
            {
                return $"Updated {replacedMeshCount} mesh reference(s) on '{displayName}'.";
            }

            if (replacedAnimator)
            {
                return $"Updated the primary Animator rig on '{displayName}'.";
            }

            return $"No mapped skinned mesh renderers or humanoid rig were updated on '{displayName}'.";
        }

        private static bool HasMissingUnifiedBlendshapesWarning(MeshSelectionState meshSelection)
        {
            return meshSelection != null &&
                meshSelection.isBodyMeshCandidate &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx != null &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length > 0;
        }

        private static bool HasCompleteUnifiedBlendshapesInfo(MeshSelectionState meshSelection)
        {
            return meshSelection != null &&
                meshSelection.isBodyMeshCandidate &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx != null &&
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx.Length == 0;
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

        private static string GetCopiedFbxFileName(string sourceFbxPath)
        {
            return $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} FT{Path.GetExtension(sourceFbxPath)}";
        }

        private static bool HasDuplicateSharedTargetNames(Func<string, string> pathSelector, IEnumerable<GameObject> assets)
        {
            return assets
                .Where(asset => asset != null)
                .Select(asset => pathSelector(AssetDatabase.GetAssetPath(asset)))
                .Where(name => !string.IsNullOrEmpty(name))
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1);
        }

        private static Vector3 GetGridPosition(int index)
        {
            int column = index % SharedSceneGridColumns;
            int row = index / SharedSceneGridColumns;
            return new Vector3(column * SharedSceneGridSpacingX, 0f, row * SharedSceneGridSpacingZ);
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

        private static Dictionary<string, Mesh> LoadMeshSubAssets(string fbxAssetPath)
        {
            var result = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);

            if (allSubAssets == null || allSubAssets.Length == 0)
            {
                return result;
            }

            foreach (UnityEngine.Object subAsset in allSubAssets)
            {
                if (subAsset is Mesh mesh && !string.IsNullOrEmpty(mesh.name))
                {
                    result[mesh.name] = mesh;
                }
            }

            return result;
        }

        private static void PopulateUnifiedBlendshapeWarnings(List<MeshSelectionState> meshSelections, Dictionary<string, Mesh> fbxMeshSubAssets, string[] requiredBlendshapes)
        {
            if (meshSelections == null)
            {
                return;
            }

            foreach (MeshSelectionState meshSelection in meshSelections)
            {
                meshSelection.isBodyMeshCandidate = IsBodyMeshSelection(meshSelection);
                meshSelection.showUnifiedBlendshapeWarningDetails = false;
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx = Array.Empty<string>();

                if (!meshSelection.isBodyMeshCandidate)
                {
                    continue;
                }

                Mesh fbxMesh = ResolveFbxMeshForSelection(meshSelection, fbxMeshSubAssets);
                meshSelection.missingRequiredUnifiedBlendshapesOnFbx = PawlygonEditorUtils.GetMissingRequiredUnifiedBlendshapes(fbxMesh, requiredBlendshapes);
            }
        }

        private static Mesh ResolveFbxMeshForSelection(MeshSelectionState meshSelection, Dictionary<string, Mesh> fbxMeshSubAssets)
        {
            if (meshSelection == null || fbxMeshSubAssets == null || fbxMeshSubAssets.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(meshSelection.fbxMeshName) && fbxMeshSubAssets.TryGetValue(meshSelection.fbxMeshName, out Mesh meshByName))
            {
                return meshByName;
            }

            if (!string.IsNullOrEmpty(meshSelection.fbxObjectName) && fbxMeshSubAssets.TryGetValue(meshSelection.fbxObjectName, out Mesh meshByObjectName))
            {
                return meshByObjectName;
            }

            return null;
        }

        private static List<Avatar> LoadHumanoidAvatarSubAssets(string fbxAssetPath)
        {
            var result = new List<Avatar>();
            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);

            if (allSubAssets == null || allSubAssets.Length == 0)
            {
                return result;
            }

            foreach (UnityEngine.Object subAsset in allSubAssets)
            {
                if (subAsset is Avatar avatar && IsValidHumanoidAvatar(avatar))
                {
                    result.Add(avatar);
                }
            }

            return result;
        }

        private static AnimatorReplacementState CreateAnimatorReplacementState(GameObject fbxRoot, GameObject prefabRoot, string fbxAssetPath)
        {
            AnimatorInfo prefabAnimator = GetPrimaryAnimatorInfo(prefabRoot);
            AnimatorInfo fbxAnimator = GetPrimaryAnimatorInfo(fbxRoot);
            Avatar replacementAvatar = null;
            string matchReason;

            if (IsValidHumanoidAvatar(fbxAnimator?.Avatar))
            {
                replacementAvatar = fbxAnimator.Avatar;
                matchReason = "Using the duplicated FBX's primary Animator avatar.";
            }
            else
            {
                List<Avatar> avatars = LoadHumanoidAvatarSubAssets(fbxAssetPath);
                replacementAvatar = avatars.FirstOrDefault();
                matchReason = replacementAvatar != null
                    ? "Using the duplicated FBX's humanoid Avatar sub-asset."
                    : "No valid humanoid Avatar was found on the duplicated FBX.";
            }

            return new AnimatorReplacementState
            {
                prefabAnimatorObjectName = prefabAnimator?.ObjectName ?? string.Empty,
                prefabAnimatorRelativePath = prefabAnimator?.RelativePath ?? string.Empty,
                fbxAnimatorObjectName = fbxAnimator?.ObjectName ?? string.Empty,
                fbxAnimatorRelativePath = fbxAnimator?.RelativePath ?? string.Empty,
                fbxAvatarName = replacementAvatar != null ? replacementAvatar.name : string.Empty,
                matchReason = matchReason,
                hasPrefabAnimator = prefabAnimator != null,
                hasHumanoidAvatar = replacementAvatar != null,
                selected = prefabAnimator != null && replacementAvatar != null
            };
        }

        private static List<RendererInfo> GetRendererInfos(GameObject root, Dictionary<string, Mesh> meshSubAssets)
        {
            var results = new List<RendererInfo>();
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                string objectName = renderer.gameObject.name;
                string relativePath = GetRelativeTransformPath(renderer.transform);
                string meshName = string.Empty;

                if (renderer.sharedMesh != null)
                {
                    meshName = renderer.sharedMesh.name;
                }
                else if (meshSubAssets != null)
                {
                    if (meshSubAssets.TryGetValue(objectName, out Mesh _))
                    {
                        meshName = objectName;
                    }
                    else
                    {
                        string found = meshSubAssets.Keys.FirstOrDefault(
                            key => key.IndexOf(objectName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   objectName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (found != null)
                        {
                            meshName = found;
                        }
                    }
                }

                results.Add(new RendererInfo
                {
                    Renderer = renderer,
                    ObjectName = objectName,
                    RelativePath = relativePath,
                    MeshName = meshName
                });
            }

            return results;
        }

        private static AnimatorInfo GetPrimaryAnimatorInfo(GameObject root)
        {
            if (root == null)
            {
                return null;
            }

            return root
                .GetComponentsInChildren<Animator>(true)
                .Select(animator => new AnimatorInfo
                {
                    Animator = animator,
                    ObjectName = animator.gameObject.name,
                    RelativePath = GetRelativeTransformPath(animator.transform),
                    Avatar = animator.avatar,
                    PriorityScore = GetAnimatorPriorityScore(root.transform, animator)
                })
                .OrderByDescending(info => info.PriorityScore)
                .ThenBy(info => info.RelativePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int GetAnimatorPriorityScore(Transform rootTransform, Animator animator)
        {
            if (animator == null)
            {
                return int.MinValue;
            }

            int score = 0;

            if (IsValidHumanoidAvatar(animator.avatar))
            {
                score += 1000;
            }

            if (animator.transform == rootTransform)
            {
                score += 500;
            }
            else if (animator.transform.parent == rootTransform)
            {
                score += 250;
            }

            score += animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length * 10;
            score -= GetTransformDepth(animator.transform, rootTransform);
            return score;
        }

        private static int GetTransformDepth(Transform transform, Transform rootTransform)
        {
            int depth = 0;
            Transform current = transform;

            while (current != null && current != rootTransform)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static MeshSelectionState CreateMeshSelectionState(RendererInfo fbxRenderer, List<RendererInfo> prefabRenderers, ISet<string> usedPrefabPaths)
        {
            RendererInfo matchedRenderer = FindBestRendererMatch(fbxRenderer, prefabRenderers, usedPrefabPaths, out string matchReason);

            if (matchedRenderer != null)
            {
                usedPrefabPaths.Add(matchedRenderer.RelativePath);
            }

            return new MeshSelectionState
            {
                fbxObjectName = fbxRenderer.ObjectName,
                fbxRelativePath = fbxRenderer.RelativePath,
                fbxMeshName = fbxRenderer.MeshName,
                prefabObjectName = matchedRenderer?.ObjectName ?? string.Empty,
                prefabRelativePath = matchedRenderer?.RelativePath ?? string.Empty,
                prefabMeshName = matchedRenderer?.MeshName ?? string.Empty,
                matchReason = matchReason,
                hasMatch = matchedRenderer != null,
                selected = matchedRenderer != null
            };
        }

        private static bool IsBodyMeshSelection(MeshSelectionState meshSelection)
        {
            if (meshSelection == null)
            {
                return false;
            }

            return string.Equals(meshSelection.fbxObjectName, "Body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(meshSelection.fbxMeshName, "Body", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetLastPathSegment(meshSelection.fbxRelativePath), "Body", StringComparison.OrdinalIgnoreCase);
        }

        private static RendererInfo FindBestRendererMatch(RendererInfo fbxRenderer, List<RendererInfo> prefabRenderers, ISet<string> usedPrefabPaths, out string matchReason)
        {
            RendererInfo relativePathMatch = prefabRenderers.FirstOrDefault(renderer =>
                !usedPrefabPaths.Contains(renderer.RelativePath) &&
                string.Equals(renderer.RelativePath, fbxRenderer.RelativePath, StringComparison.OrdinalIgnoreCase));

            if (relativePathMatch != null)
            {
                matchReason = "Relative path";
                return relativePathMatch;
            }

            RendererInfo objectNameMatch = prefabRenderers.FirstOrDefault(renderer =>
                !usedPrefabPaths.Contains(renderer.RelativePath) &&
                string.Equals(renderer.ObjectName, fbxRenderer.ObjectName, StringComparison.OrdinalIgnoreCase));

            if (objectNameMatch != null)
            {
                matchReason = "GameObject name";
                return objectNameMatch;
            }

            if (!string.IsNullOrEmpty(fbxRenderer.MeshName))
            {
                RendererInfo meshNameMatch = prefabRenderers.FirstOrDefault(renderer =>
                    !usedPrefabPaths.Contains(renderer.RelativePath) &&
                    !string.IsNullOrEmpty(renderer.MeshName) &&
                    string.Equals(renderer.MeshName, fbxRenderer.MeshName, StringComparison.OrdinalIgnoreCase));

                if (meshNameMatch != null)
                {
                    matchReason = "Mesh name";
                    return meshNameMatch;
                }
            }

            matchReason = string.Empty;
            return null;
        }

        private static string GetRelativeTransformPath(Transform transform)
        {
            var names = new List<string>();
            Transform current = transform;

            while (current != null && current.parent != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            if (names.Count == 0)
            {
                return transform.name;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static string GetLastPathSegment(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            int separatorIndex = path.LastIndexOf('/');
            return separatorIndex >= 0 ? path.Substring(separatorIndex + 1) : path;
        }

        private static Transform FindTransformByRelativePath(Transform root, string relativePath)
        {
            if (root == null || string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            if (string.Equals(relativePath, root.name, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            string rootPrefix = root.name + "/";
            string localPath = relativePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? relativePath.Substring(rootPrefix.Length)
                : relativePath;

            return root.Find(localPath);
        }

        private static Avatar LoadReplacementAvatar(AnimatorReplacementState animatorReplacement, string fbxAssetPath)
        {
            if (animatorReplacement == null)
            {
                return null;
            }

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (fbxRoot != null && !string.IsNullOrEmpty(animatorReplacement.fbxAnimatorRelativePath))
            {
                Transform animatorTransform = FindTransformByRelativePath(fbxRoot.transform, animatorReplacement.fbxAnimatorRelativePath);
                Animator fbxAnimator = animatorTransform != null ? animatorTransform.GetComponent<Animator>() : null;
                if (IsValidHumanoidAvatar(fbxAnimator?.avatar))
                {
                    return fbxAnimator.avatar;
                }
            }

            List<Avatar> avatars = LoadHumanoidAvatarSubAssets(fbxAssetPath);
            return avatars.FirstOrDefault(avatar => string.Equals(avatar.name, animatorReplacement.fbxAvatarName, StringComparison.OrdinalIgnoreCase))
                ?? avatars.FirstOrDefault();
        }

        private static bool IsValidHumanoidAvatar(Avatar avatar)
        {
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        private static void DrawReadOnlyPathField(string label, string value)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(label, value ?? string.Empty);
            }
        }

        private GameObject DrawFilteredAssetField(string label, GameObject currentValue, string extension, int controlId)
        {
            Rect totalRect = EditorGUILayout.GetControlRect();
            Rect fieldRect = EditorGUI.PrefixLabel(totalRect, new GUIContent(label));

            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && fieldRect.Contains(currentEvent.mousePosition))
            {
                bool clickedPickerButton = currentEvent.mousePosition.x >= fieldRect.xMax - 19f;
                if (clickedPickerButton)
                {
                    EditorGUIUtility.ShowObjectPicker<GameObject>(currentValue, false, $"glob:\"*.{extension}\"", controlId);
                    currentEvent.Use();
                }
            }

            return (GameObject)EditorGUI.ObjectField(fieldRect, GUIContent.none, currentValue, typeof(GameObject), false);
        }

        private void HandleObjectPickerSelection()
        {
            Event currentEvent = Event.current;
            if (currentEvent.type != EventType.ExecuteCommand && currentEvent.type != EventType.ValidateCommand)
            {
                return;
            }

            if (currentEvent.commandName != "ObjectSelectorUpdated" && currentEvent.commandName != "ObjectSelectorClosed")
            {
                return;
            }

            int pickerControlId = EditorGUIUtility.GetObjectPickerControlID();
            UnityEngine.Object pickedObject = EditorGUIUtility.GetObjectPickerObject();
            if (pickedObject is not GameObject pickedGameObject)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(pickedGameObject);
            string extension = Path.GetExtension(assetPath);

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                if (pickerControlId == SourceFbxPickerControlId + i * 2 && string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    avatarEntries[i].sourceFbx = pickedGameObject;
                    Repaint();
                    return;
                }

                if (pickerControlId == SourcePrefabPickerControlId + i * 2 && string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    avatarEntries[i].sourcePrefab = pickedGameObject;
                    Repaint();
                    return;
                }
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

            fxLayerHeaderStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            fxGuardedLabelStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Italic };
            fxGuardedLabelStyle.normal.textColor = new Color(0.3f, 0.75f, 0.3f);

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

        // =====================================================================
        // FX Gesture Check step
        // =====================================================================

        private void DrawFXCheckStep()
        {
            PawlygonEditorUI.DrawSection(
                "FX Gesture Check",
                "Analyze and guard gesture-based facial expression transitions in the FX controller.",
                () =>
                {
                    // Check VRChat SDK availability
                    if (PawlygonEditorUtils.FindVRCAvatarDescriptorType() == null)
                    {
                        EditorGUILayout.HelpBox(
                            "VRChat SDK not detected. Skipping FX gesture check.",
                            MessageType.Info);
                        EditorGUILayout.Space(SectionSpacing);
                        if (PawlygonEditorUI.DrawPrimaryButton("Continue to Finish", 34f))
                        {
                            currentStep = WizardStep.Complete;
                            statusMessage = "Avatar setup completed.";
                        }
                        return;
                    }

                    // Auto-analyze on first visit
                    if (!fxCheckAnalyzed)
                    {
                        AnalyzeAllEntries();
                        fxCheckAnalyzed = true;
                    }

                    // Entry selection tabs (FX-specific labels)
                    DrawFXEntrySelectionToolbar();
                    EditorGUILayout.Space(SectionSpacing);

                    // Current entry results
                    AvatarEntry entry = avatarEntries[selectedEntryIndex];
                    if (entry.fxAnalysisResult == null)
                    {
                        EditorGUILayout.HelpBox("No analysis result available.", MessageType.Warning);
                    }
                    else if (!entry.fxAnalysisResult.Success)
                    {
                        EditorGUILayout.HelpBox(entry.fxAnalysisResult.StatusMessage,
                            entry.fxAnalysisResult.StatusMessageType);
                    }
                    else if (entry.fxAnalysisResult.Layers == null ||
                             entry.fxAnalysisResult.Layers.Count == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "No gesture-based facial expression transitions found.",
                            MessageType.Info);
                    }
                    else
                    {
                        DrawFXResultsForEntry(entry);
                        EditorGUILayout.Space(SectionSpacing);
                        DrawFXApplySectionForEntry(entry);
                    }

                    // Navigation buttons
                    EditorGUILayout.Space(SectionSpacing);
                    bool allProcessed = avatarEntries.All(e => e.fxCheckComplete);

                    if (!allProcessed)
                    {
                        if (GUILayout.Button("Skip FX Check", GUILayout.Height(28f)))
                        {
                            foreach (AvatarEntry e in avatarEntries) e.fxCheckComplete = true;
                            currentStep = WizardStep.Complete;
                            statusMessage = "Avatar setup completed.";
                        }
                    }

                    if (allProcessed)
                    {
                        if (PawlygonEditorUI.DrawPrimaryButton("Continue to Finish", 34f))
                        {
                            currentStep = WizardStep.Complete;
                            statusMessage = "Avatar setup completed.";
                        }
                    }
                });
        }

        private void AnalyzeAllEntries()
        {
            foreach (AvatarEntry entry in avatarEntries)
            {
                // Validate prefab exists
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);
                if (prefabAsset == null)
                {
                    entry.fxAnalysisResult = new FXGestureCheckerCore.AnalysisResult
                    {
                        StatusMessage = $"Could not load prefab at '{entry.copiedPrefabPath}'.",
                        StatusMessageType = MessageType.Error,
                        Success = false
                    };
                    entry.fxCheckComplete = true;
                    continue;
                }

                // Open prefab contents to get a live GameObject for analysis
                GameObject prefabRoot = null;
                try
                {
                    prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                    entry.fxAnalysisResult = FXGestureCheckerCore.Analyze(prefabRoot);
                }
                finally
                {
                    if (prefabRoot != null)
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                // Store original FX controller path for shared controller detection
                if (entry.fxAnalysisResult.FXController != null)
                    entry.originalFxControllerPath = AssetDatabase.GetAssetPath(entry.fxAnalysisResult.FXController);

                // Auto-skip entries with no actionable results
                if (!entry.fxAnalysisResult.Success ||
                    entry.fxAnalysisResult.Layers == null ||
                    entry.fxAnalysisResult.Layers.Count == 0)
                {
                    entry.fxCheckComplete = true;
                }
            }
        }

        private void DrawFXEntrySelectionToolbar()
        {
            if (avatarEntries.Count <= 1) return;

            string[] labels = new string[avatarEntries.Count];
            for (int i = 0; i < avatarEntries.Count; i++)
            {
                AvatarEntry entry = avatarEntries[i];
                string name = GetEntryDisplayName(entry);
                if (entry.fxCheckComplete)
                {
                    bool hadFixes = !string.IsNullOrEmpty(entry.copiedFxControllerPath);
                    name += hadFixes ? " [Fixed]" : " [Skipped]";
                }
                labels[i] = name;
            }

            selectedEntryIndex = GUILayout.Toolbar(
                Mathf.Clamp(selectedEntryIndex, 0, labels.Length - 1), labels);
        }

        private void DrawFXResultsForEntry(AvatarEntry entry)
        {
            FXGestureCheckerCore.AnalysisResult result = entry.fxAnalysisResult;

            // Summary
            int totalTransitions = result.Layers.Sum(l => l.GestureTransitions.Count);
            int guardedTransitions = result.Layers.Sum(l =>
                l.GestureTransitions.Count(t => t.HasDisabledGuard));
            EditorGUILayout.HelpBox(
                $"Found {totalTransitions} gesture transition(s) across {result.Layers.Count} layer(s). " +
                $"{guardedTransitions} already guarded.",
                MessageType.Info);
            EditorGUILayout.Space(4f);

            // Per-layer foldouts
            for (int i = 0; i < result.Layers.Count; i++)
            {
                DrawFXLayerAnalysis(result.Layers[i]);
                EditorGUILayout.Space(4f);
            }
        }

        private void DrawFXLayerAnalysis(FXGestureCheckerCore.LayerAnalysis layer)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                bool isExpanded = fxExpandedLayers.Contains(layer.LayerIndex);
                string gestureCount = $"{layer.GestureTransitions.Count} gesture transition{(layer.GestureTransitions.Count != 1 ? "s" : "")}";

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newExpanded = EditorGUILayout.Foldout(isExpanded, "", true);
                    EditorGUILayout.LabelField($"Layer: {layer.LayerName}", fxLayerHeaderStyle);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"({gestureCount})", EditorStyles.miniLabel, GUILayout.Width(150f));

                    if (newExpanded != isExpanded)
                    {
                        if (newExpanded) fxExpandedLayers.Add(layer.LayerIndex);
                        else fxExpandedLayers.Remove(layer.LayerIndex);
                    }
                }

                if (!isExpanded) return;

                EditorGUI.indentLevel++;

                EditorGUILayout.Space(4f);
                if (layer.AlreadyHasLayerGuard)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ToggleLeft("Disable entire layer when FacialExpressionsDisabled", true);
                        }
                        EditorGUILayout.LabelField("[Applied]", fxGuardedLabelStyle, GUILayout.Width(60f));
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

                foreach (FXGestureCheckerCore.TransitionAnalysis transition in layer.GestureTransitions)
                {
                    DrawFXTransitionRow(transition);
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawFXTransitionRow(FXGestureCheckerCore.TransitionAnalysis transition)
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
                    EditorGUILayout.LabelField("[Applied]", fxGuardedLabelStyle, GUILayout.Width(60f));
                }
                else
                {
                    transition.SelectedForFix = EditorGUILayout.ToggleLeft(label, transition.SelectedForFix);
                }
            }
        }

        private void DrawFXApplySectionForEntry(AvatarEntry entry)
        {
            FXGestureCheckerCore.AnalysisResult result = entry.fxAnalysisResult;
            List<FXGestureCheckerCore.LayerAnalysis> layers = result.Layers;

            bool anySelected = layers.Any(l =>
                l.SelectedForLayerDisable ||
                l.GestureTransitions.Any(t => t.SelectedForFix));

            bool allGuarded = layers.All(l =>
                l.AlreadyHasLayerGuard &&
                l.GestureTransitions.All(t => t.HasDisabledGuard));

            if (allGuarded)
            {
                EditorGUILayout.HelpBox(
                    "All gesture transitions and layers are already guarded.",
                    MessageType.Info);
                entry.fxCheckComplete = true;
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Select All Unguarded", GUILayout.Height(26f), GUILayout.Width(160f)))
                    FXGestureCheckerCore.SelectAllUnguarded(layers);
                if (GUILayout.Button("Deselect All", GUILayout.Height(26f), GUILayout.Width(120f)))
                    FXGestureCheckerCore.DeselectAll(layers);
            }

            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(!anySelected))
            {
                if (PawlygonEditorUI.DrawPrimaryButton("Apply Fixes"))
                {
                    ApplyFXFixesForEntry(entry);
                }
            }

            if (!anySelected)
            {
                EditorGUILayout.HelpBox(
                    "Select transitions or layers to apply the FacialExpressionsDisabled guard.",
                    MessageType.Info);
            }
        }

        private void ApplyFXFixesForEntry(AvatarEntry entry)
        {
            FXGestureCheckerCore.AnalysisResult result = entry.fxAnalysisResult;
            if (result.FXController == null) return;

            // Capture user selections before re-analysis resets them
            var selectedTransitionKeys = new HashSet<string>();
            var selectedLayerIndices = new HashSet<int>();
            foreach (FXGestureCheckerCore.LayerAnalysis layer in result.Layers)
            {
                if (layer.SelectedForLayerDisable)
                    selectedLayerIndices.Add(layer.LayerIndex);
                foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                {
                    if (t.SelectedForFix)
                        selectedTransitionKeys.Add(
                            $"{layer.LayerIndex}:{t.SourceName}->{t.DestinationName}:{t.GestureParameter}");
                }
            }

            // 1. Copy FX controller to VRChat subfolder
            string vrchatFolder = PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, "VRChat");
            AnimatorController copy = FXGestureCheckerCore.CopyFXController(
                result.FXController, vrchatFolder, out string copyError);
            if (copy == null)
            {
                statusMessage = copyError ?? "Failed to copy FX controller.";
                return;
            }
            entry.copiedFxControllerPath = AssetDatabase.GetAssetPath(copy);

            // 2. Re-analyze on the copy so TransitionRef references point to the new asset
            FXGestureCheckerCore.AnalysisResult copyResult = FXGestureCheckerCore.Analyze(copy);
            if (!copyResult.Success || copyResult.Layers == null)
            {
                statusMessage = "Failed to analyze copied FX controller.";
                return;
            }

            // Restore user selections on re-analyzed layers
            foreach (FXGestureCheckerCore.LayerAnalysis layer in copyResult.Layers)
            {
                if (selectedLayerIndices.Contains(layer.LayerIndex))
                    layer.SelectedForLayerDisable = true;
                foreach (FXGestureCheckerCore.TransitionAnalysis t in layer.GestureTransitions)
                {
                    string key = $"{layer.LayerIndex}:{t.SourceName}->{t.DestinationName}:{t.GestureParameter}";
                    if (selectedTransitionKeys.Contains(key))
                        t.SelectedForFix = true;
                }
            }

            // 3. Apply fixes on the copy
            var (tFixes, lFixes) = FXGestureCheckerCore.ApplySelectedFixes(copy, copyResult.Layers);

            // 4. Assign copy to descriptor — must reopen prefab to get fresh descriptor
            GameObject prefabRoot = null;
            try
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                Type descriptorType = result.DescriptorType;
                if (descriptorType != null)
                {
                    Component descriptor = prefabRoot.GetComponent(descriptorType);
                    if (descriptor != null)
                    {
                        FXGestureCheckerCore.AssignFXControllerToDescriptor(
                            descriptor, descriptorType, copy);
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
            }
            finally
            {
                if (prefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            // 5. Re-analyze to refresh UI (shows [Applied] labels)
            GameObject finalPrefabRoot = null;
            try
            {
                finalPrefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                entry.fxAnalysisResult = FXGestureCheckerCore.Analyze(finalPrefabRoot);
            }
            finally
            {
                if (finalPrefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(finalPrefabRoot);
            }

            entry.fxCheckComplete = true;
            statusMessage = $"Applied {tFixes} transition guard(s) and {lFixes} layer guard(s).";

            // Propagate to other entries sharing the same original FX controller
            PropagateSharedFXFixes(entry, copy);
        }

        private void PropagateSharedFXFixes(AvatarEntry sourceEntry, AnimatorController fixedCopy)
        {
            if (string.IsNullOrEmpty(sourceEntry.originalFxControllerPath)) return;

            foreach (AvatarEntry otherEntry in avatarEntries)
            {
                if (otherEntry == sourceEntry) continue;
                if (otherEntry.fxCheckComplete) continue;
                if (string.IsNullOrEmpty(otherEntry.originalFxControllerPath)) continue;

                // Check if they share the same original FX controller
                if (otherEntry.originalFxControllerPath != sourceEntry.originalFxControllerPath) continue;

                // Assign the same fixed copy to this entry's prefab
                otherEntry.copiedFxControllerPath = AssetDatabase.GetAssetPath(fixedCopy);

                GameObject prefabRoot = null;
                try
                {
                    prefabRoot = PrefabUtility.LoadPrefabContents(otherEntry.copiedPrefabPath);
                    Type descriptorType = otherEntry.fxAnalysisResult?.DescriptorType;
                    if (descriptorType != null)
                    {
                        Component descriptor = prefabRoot.GetComponent(descriptorType);
                        if (descriptor != null)
                        {
                            FXGestureCheckerCore.AssignFXControllerToDescriptor(
                                descriptor, descriptorType, fixedCopy);
                        }
                    }
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, otherEntry.copiedPrefabPath);
                }
                finally
                {
                    if (prefabRoot != null)
                        PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                // Re-analyze
                GameObject finalRoot = null;
                try
                {
                    finalRoot = PrefabUtility.LoadPrefabContents(otherEntry.copiedPrefabPath);
                    otherEntry.fxAnalysisResult = FXGestureCheckerCore.Analyze(finalRoot);
                }
                finally
                {
                    if (finalRoot != null)
                        PrefabUtility.UnloadPrefabContents(finalRoot);
                }

                otherEntry.fxCheckComplete = true;
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
            importLoadAttempts = 0;
            fxCheckAnalyzed = false;
            fxExpandedLayers.Clear();
            currentStep = WizardStep.Setup;
        }
    }
}
