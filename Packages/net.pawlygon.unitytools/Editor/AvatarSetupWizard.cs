using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
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
        private const string PawlygonWebsiteUrl = "https://www.pawlygon.net";
        private const string PawlygonTwitterUrl = "https://x.com/Pawlygon_studio";
        private const string PawlygonYouTubeUrl = "https://www.youtube.com/@Pawlygon";
        private const string PawlygonDiscordUrl = "https://discord.com/invite/pZew3JGpjb";
        private const string PackageJsonPath = "Packages/net.pawlygon.unitytools/package.json";
        private const int SourceFbxPickerControlId = 9001;
        private const int SourcePrefabPickerControlId = 9002;

        [SerializeField] private string mainFolderName = DefaultMainFolderName;
        [SerializeField] private bool useSeparateFolderPerAvatar;
        [SerializeField] private string sharedAvatarFolderName = DefaultAvatarName;
        [SerializeField] private List<AvatarEntry> avatarEntries = new List<AvatarEntry> { new AvatarEntry() };

        [SerializeField] private WizardStep currentStep = WizardStep.Setup;
        [SerializeField] private int selectedEntryIndex;

        private Vector2 setupScrollPosition;
        private Vector2 meshScrollPosition;
        private string statusMessage = string.Empty;
        private bool pendingImportTransition;
        private int importLoadAttempts;
        private string vrcftSetupStatusMessage = string.Empty;
        private string patcherHubImportStatusMessage = string.Empty;
        private bool patcherHubImportedThisSession;
        private Texture2D pawlygonLogoTexture;
        private string packageVersion = "1.0.0";
        private GUIStyle sectionStyle;
        private GUIStyle titleStyle;
        private GUIStyle stepStyle;
        private GUIStyle currentStepStyle;
        private GUIStyle subLabelStyle;
        private GUIStyle richMiniLabelStyle;
        private GUIStyle headerTitleStyle;
        private GUIStyle headerSubtitleStyle;
        private GUIStyle footerStyle;
        private GUIStyle footerLinkStyle;

        private enum WizardStep
        {
            Setup,
            WaitForImport,
            SelectMeshes,
            Prefabs,
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
            public List<MeshSelectionState> meshSelections = new List<MeshSelectionState>();
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
        }

        private class RendererInfo
        {
            public SkinnedMeshRenderer Renderer;
            public string ObjectName;
            public string RelativePath;
            public string MeshName;
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

        [Serializable]
        private class PackageManifestInfo
        {
            public string version;
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
            LoadPackageVersion();
        }

        private void OnDisable()
        {
            FBXImportDetector.FbxReimported -= HandleFbxReimported;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
        }

        private void OnGUI()
        {
            EnsureStyles();
            EnsureAtLeastOneEntry();
            HandleObjectPickerSelection();

            DrawHeader();
            DrawStepIndicator();
            EditorGUILayout.Space(SectionSpacing);

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
                case WizardStep.Complete:
                    DrawCompleteStep();
                    break;
            }

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
            }

            EditorGUILayout.Space(8f);
            DrawFooter();
        }

        private void DrawSetupStep()
        {
            bool hasMultipleEntries = avatarEntries.Count > 1;

            DrawSection(
                "Setup",
                "Choose the source assets and create the working avatar structure.",
                () =>
                {
                    setupScrollPosition = EditorGUILayout.BeginScrollView(setupScrollPosition);

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

                    EditorGUILayout.EndScrollView();

                    string validationMessage = GetSetupValidationMessage();

                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                    using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(validationMessage)))
                    {
                        if (DrawPrimaryButton("Create Avatar Structure", 36f))
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
            using (new EditorGUILayout.VerticalScope(sectionStyle))
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
            DrawSection(
                "Import Modified FBX",
                "Replace each copied FBX on disk with its edited version. Continue after every model below shows Updated.",
                () =>
                {
                    int importedCount = avatarEntries.Count(entry => entry.hasImportedModifiedFbx);
                    EditorGUILayout.HelpBox($"Progress: {importedCount} / {avatarEntries.Count} modified FBXs detected.", MessageType.Info);
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    DrawImportStatusSummary();
                    EditorGUILayout.Space(SectionSpacing);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (DrawPrimaryButton("Continue After Import", 34f))
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

            DrawSection(
                "Select Meshes",
                "Review one avatar at a time. Apply the selected mesh replacements or explicitly skip that avatar.",
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

                        using (new EditorGUILayout.VerticalScope(sectionStyle))
                        {
                            meshScrollPosition = EditorGUILayout.BeginScrollView(meshScrollPosition, GUILayout.Height(260f));

                            foreach (MeshSelectionState meshSelection in selectedEntry.meshSelections)
                            {
                                DrawMeshSelectionRow(meshSelection);
                            }

                            EditorGUILayout.EndScrollView();
                        }
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Skip This Avatar", GUILayout.Height(34f)))
                        {
                            SkipEntryReview(selectedEntry);
                        }

                        using (new EditorGUI.DisabledScope(selectedEntry.meshSelections.All(selection => !selection.selected)))
                        {
                            if (DrawPrimaryButton("Replace Selected Meshes", 34f))
                            {
                                ApplySelectedMeshesToPrefab(selectedEntry);
                            }
                        }
                    }
                });
        }

        private void DrawCompleteStep()
        {
            DrawSection(
                "Finish",
                "Your avatar setup is ready. Optional prefab tools were available in the previous step.",
                () =>
                {
                    GUIContent successIcon = EditorGUIUtility.IconContent("TestPassed");
                    EditorGUILayout.LabelField(new GUIContent(" Batch avatar setup completed successfully", successIcon.image), new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                    foreach (AvatarEntry entry in avatarEntries)
                    {
                        using (new EditorGUILayout.VerticalScope(sectionStyle))
                        {
                            EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                            if (!string.IsNullOrEmpty(entry.reviewResultLabel))
                            {
                                EditorGUILayout.LabelField($"Result: {entry.reviewResultLabel}", richMiniLabelStyle);
                            }
                            DrawPathSummary(entry, includeAvatarRoot: true, includeDiffGenerator: true);
                        }
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    if (DrawPrimaryButton("Start Over", 34f))
                    {
                        ResetWizard();
                    }
                });
        }

        private void DrawPrefabsStep()
        {
            bool isVrcftAvailable = IsVrcftPackageAvailable(out string vrcftPrefabPath);

            DrawSection(
                "Prefabs",
                "Optional tools for adding prefab helpers and distributing patch assets.",
                () =>
                {
                    DrawVrcftPrefabBlock(isVrcftAvailable, vrcftPrefabPath);
                    EditorGUILayout.Space(SectionSpacing);
                    DrawPatcherHubBlock();
                    EditorGUILayout.Space(SectionSpacing);

                    if (DrawPrimaryButton("Finish", 34f))
                    {
                        currentStep = WizardStep.Complete;
                        statusMessage = "Avatar setup completed.";
                    }
                });
        }

        private void DrawVrcftPrefabBlock(bool isVrcftAvailable, string vrcftPrefabPath)
        {
            using (new EditorGUILayout.VerticalScope(sectionStyle))
            {
                EditorGUILayout.LabelField("Pawlygon VRCFT", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
                EditorGUILayout.Space(2f);

                if (isVrcftAvailable)
                {
                    EditorGUILayout.LabelField("Package detected. Add the VRCFT setup to each generated prefab.", subLabelStyle);
                    EditorGUILayout.Space(8f);

                    if (DrawPrimaryButton("Add VRCFT To Prefabs", 32f))
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
            using (new EditorGUILayout.VerticalScope(sectionStyle))
            {
                EditorGUILayout.LabelField("PatcherHub", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("Import the latest PatcherHub unitypackage so end users can patch the face-tracking changes onto the avatar FBX model.", subLabelStyle);
                EditorGUILayout.Space(8f);

                if (DrawPrimaryButton("Import Latest PatcherHub", 32f))
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

            EnsureFolderExists(CombineAssetPath("Assets", sanitizedMainFolderName));

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

            entry.avatarRootPath = CombineAssetPath("Assets", sanitizedMainFolderName, avatarFolderName);
            string fbxFolderPath = CombineAssetPath(entry.avatarRootPath, "FBX");
            string prefabFolderPath = CombineAssetPath(entry.avatarRootPath, "Prefabs");
            string internalFolderPath = CombineAssetPath(entry.avatarRootPath, "Internal");
            string scenesFolderPath = CombineAssetPath(internalFolderPath, "Scenes");

            string copiedFbxFileName = GetCopiedFbxFileName(sourceFbxPath);
            string copiedPrefabFileName = Path.GetFileName(sourcePrefabPath);
            string sceneFileName = $"{avatarFolderName} - Pawlygon VRCFT.unity";
            string diffGeneratorFileName = $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} Face Tracking DiffGenerator.asset";

            entry.copiedFbxPath = CombineAssetPath(fbxFolderPath, copiedFbxFileName);
            entry.copiedPrefabPath = CombineAssetPath(prefabFolderPath, copiedPrefabFileName);
            entry.createdScenePath = CombineAssetPath(scenesFolderPath, sceneFileName);
            entry.diffGeneratorAssetPath = CombineAssetPath(internalFolderPath, diffGeneratorFileName);

            if (AssetDatabase.IsValidFolder(entry.avatarRootPath))
            {
                EditorUtility.DisplayDialog("Avatar Already Exists", $"The folder '{entry.avatarRootPath}' already exists. Choose a new avatar name or remove the existing folder first.", "OK");
                return false;
            }

            EnsureFolderExists(entry.avatarRootPath);
            EnsureFolderExists(fbxFolderPath);
            EnsureFolderExists(prefabFolderPath);
            EnsureFolderExists(internalFolderPath);
            EnsureFolderExists(scenesFolderPath);

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
            string avatarRootPath = CombineAssetPath("Assets", sanitizedMainFolderName, effectiveSharedAvatarFolderName);
            string fbxFolderPath = CombineAssetPath(avatarRootPath, "FBX");
            string prefabFolderPath = CombineAssetPath(avatarRootPath, "Prefabs");
            string internalFolderPath = CombineAssetPath(avatarRootPath, "Internal");
            string scenesFolderPath = CombineAssetPath(internalFolderPath, "Scenes");
            string sharedScenePath = CombineAssetPath(scenesFolderPath, $"{effectiveSharedAvatarFolderName} - Pawlygon VRCFT.unity");

            if (AssetDatabase.IsValidFolder(avatarRootPath))
            {
                EditorUtility.DisplayDialog("Avatar Already Exists", $"The folder '{avatarRootPath}' already exists. Choose a new avatar name or remove the existing folder first.", "OK");
                return false;
            }

            EnsureFolderExists(avatarRootPath);
            EnsureFolderExists(fbxFolderPath);
            EnsureFolderExists(prefabFolderPath);
            EnsureFolderExists(internalFolderPath);
            EnsureFolderExists(scenesFolderPath);

            var copiedPrefabPaths = new List<string>();

            foreach (AvatarEntry entry in avatarEntries)
            {
                string sourceFbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
                string sourcePrefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);
                string copiedFbxFileName = GetCopiedFbxFileName(sourceFbxPath);
                string copiedPrefabFileName = Path.GetFileName(sourcePrefabPath);
                string diffGeneratorFileName = $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} Face Tracking DiffGenerator.asset";

                entry.avatarRootPath = avatarRootPath;
                entry.copiedFbxPath = CombineAssetPath(fbxFolderPath, copiedFbxFileName);
                entry.copiedPrefabPath = CombineAssetPath(prefabFolderPath, copiedPrefabFileName);
                entry.createdScenePath = sharedScenePath;
                entry.diffGeneratorAssetPath = CombineAssetPath(internalFolderPath, diffGeneratorFileName);

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

            string normalizedImportedAssetPath = NormalizeAssetPath(importedAssetPath);
            bool matchedAny = false;

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (string.IsNullOrEmpty(entry.copiedFbxPath))
                {
                    continue;
                }

                if (!string.Equals(NormalizeAssetPath(entry.copiedFbxPath), normalizedImportedAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long currentWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(entry.copiedFbxPath);
                if (currentWriteTimeUtcTicks <= entry.watchedFbxWriteTimeUtcTicks)
                {
                    continue;
                }

                entry.watchedFbxWriteTimeUtcTicks = currentWriteTimeUtcTicks;
                entry.hasImportedModifiedFbx = true;
                matchedAny = true;
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
                    ? $"Skipped the import wait and regenerated diff files for {generatedDiffCount} avatar entr{(generatedDiffCount == 1 ? "y" : "ies")}. Review each avatar entry and apply the mesh replacements you want."
                    : "Skipped the import wait. Review each avatar entry and apply the mesh replacements you want.";
            }
            else
            {
                statusMessage = generatedDiffCount > 0
                    ? $"All modified FBXs imported. Regenerated diff files for {generatedDiffCount} avatar entr{(generatedDiffCount == 1 ? "y" : "ies")}. Review each avatar entry and apply the mesh replacements you want."
                    : "All modified FBXs imported. Review each avatar entry and apply the mesh replacements you want.";
            }

            Repaint();
        }

        private void LoadMeshSelections(AvatarEntry entry)
        {
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

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            List<RendererInfo> fbxRenderers = GetRendererInfos(fbxRoot, fbxMeshSubAssets);
            List<RendererInfo> prefabRenderers = GetRendererInfos(prefabRoot, meshSubAssets: null);
            var usedPrefabPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            entry.meshSelections = fbxRenderers
                .Select(fbxRenderer => CreateMeshSelectionState(fbxRenderer, prefabRenderers, usedPrefabPaths))
                .OrderBy(selection => selection.fbxRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ApplySelectedMeshesToPrefab(AvatarEntry entry)
        {
            List<MeshSelectionState> selectedMappings = entry.meshSelections
                .Where(selection => selection.selected && selection.hasMatch)
                .ToList();

            if (selectedMappings.Count == 0)
            {
                EditorUtility.DisplayDialog("No Meshes Selected", "Select at least one mapped skinned mesh renderer to replace on the prefab.", "OK");
                return;
            }

            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(entry.copiedFbxPath);
            if (fbxMeshSubAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("FBX Missing Meshes", "No mesh sub-assets could be loaded from the duplicated FBX.", "OK");
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

                int replacedCount = 0;

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

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
                CompleteEntryReview(entry, "Applied");
                statusMessage = replacedCount > 0
                    ? $"Updated {replacedCount} mesh reference(s) on '{GetEntryDisplayName(entry)}'."
                    : $"No mapped skinned mesh renderers were updated on '{GetEntryDisplayName(entry)}'.";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private void SkipEntryReview(AvatarEntry entry)
        {
            if (!EditorUtility.DisplayDialog("Skip Avatar Review", $"Skip mesh replacement for '{GetEntryDisplayName(entry)}'?", "Skip", "Cancel"))
            {
                return;
            }

            CompleteEntryReview(entry, "Skipped");
            statusMessage = $"Skipped mesh replacement for '{GetEntryDisplayName(entry)}'.";
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
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8) }))
            {
                foreach (AvatarEntry entry in avatarEntries)
                {
                    using (new EditorGUILayout.HorizontalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6) }))
                    {
                        GUIContent statusIcon = entry.hasImportedModifiedFbx
                            ? EditorGUIUtility.IconContent("TestPassed")
                            : EditorGUIUtility.IconContent("console.warnicon.sml");

                        GUILayout.Label(statusIcon, GUILayout.Width(20f), GUILayout.Height(18f));

                        using (new EditorGUILayout.VerticalScope())
                        {
                            EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                            EditorGUILayout.LabelField(entry.hasImportedModifiedFbx ? "Updated" : "Waiting for updated FBX", richMiniLabelStyle);
                            if (!string.IsNullOrEmpty(entry.copiedFbxPath))
                            {
                                EditorGUILayout.LabelField(entry.copiedFbxPath, richMiniLabelStyle);
                            }
                        }
                    }

                    EditorGUILayout.Space(3f);
                }
            }
        }

        private void DrawMeshReviewSummary(AvatarEntry entry)
        {
            int matchedCount = entry.meshSelections.Count(selection => selection.hasMatch);
            int selectedCount = entry.meshSelections.Count(selection => selection.selected && selection.hasMatch);

            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8) }))
            {
                EditorGUILayout.LabelField(GetEntryDisplayName(entry), EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{matchedCount} matched renderer{(matchedCount == 1 ? string.Empty : "s")}, {selectedCount} selected", richMiniLabelStyle);
                DrawReadOnlyPathField("Modified FBX", entry.copiedFbxPath);
                DrawReadOnlyPathField("Target Prefab", entry.copiedPrefabPath);
            }
        }

        private void DrawPathSummary(AvatarEntry entry, bool includeAvatarRoot, bool includeDiffGenerator)
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8) }))
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
                GUILayout.Label($"{entry.meshSelections.Count(selection => selection.selected)} selected", EditorStyles.miniBoldLabel);
            }
        }

        private void DrawMeshSelectionRow(MeshSelectionState meshSelection)
        {
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = meshSelection.hasMatch ? originalColor : new Color(1f, 0.9f, 0.7f, 0.5f);

            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6) }))
            {
                GUI.backgroundColor = originalColor;

                using (new EditorGUI.DisabledScope(!meshSelection.hasMatch))
                {
                    string meshLabel = string.IsNullOrEmpty(meshSelection.fbxMeshName)
                        ? meshSelection.fbxObjectName
                        : $"{meshSelection.fbxObjectName} ({meshSelection.fbxMeshName})";
                    meshSelection.selected = EditorGUILayout.ToggleLeft(meshLabel, meshSelection.selected, EditorStyles.boldLabel);
                }

                EditorGUILayout.LabelField($"FBX: {meshSelection.fbxRelativePath}", richMiniLabelStyle);

                GUIContent statusIcon = meshSelection.hasMatch
                    ? EditorGUIUtility.IconContent("TestPassed")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                string matchText = meshSelection.hasMatch
                    ? $"<b>Prefab:</b> {meshSelection.prefabRelativePath} ({meshSelection.prefabMeshName}) [{meshSelection.matchReason}]"
                    : "<b>Prefab:</b> <color=#c27725>No matching skinned mesh renderer found</color>";

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(statusIcon, GUILayout.Width(18f), GUILayout.Height(16f));
                    EditorGUILayout.LabelField(matchText, richMiniLabelStyle);
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

        private static void EnsureFolderExists(string folderPath)
        {
            folderPath = NormalizeAssetPath(folderPath);
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string parentPath = Path.GetDirectoryName(folderPath)?.Replace("\\", "/");
            string folderName = Path.GetFileName(folderPath);

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(folderName))
            {
                throw new InvalidOperationException($"Cannot create folder at '{folderPath}'.");
            }

            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                EnsureFolderExists(parentPath);
            }

            AssetDatabase.CreateFolder(parentPath, folderName);
        }

        private static string CombineAssetPath(params string[] parts)
        {
            return string.Join("/", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim('/')));
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            return assetPath.Replace("\\", "/");
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, NormalizeAssetPath(assetPath)));
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
            if (sectionStyle != null)
            {
                return;
            }

            sectionStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 0, 0)
            };

            titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                fixedHeight = 24f
            };

            headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleLeft
            };

            headerSubtitleStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true
            };

            headerSubtitleStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.72f, 0.72f, 0.72f)
                : new Color(0.35f, 0.35f, 0.35f);

            footerStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };

            footerLinkStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                stretchWidth = false
            };

            footerLinkStyle.normal.textColor = new Color(0.39f, 0.67f, 1f);
            footerLinkStyle.hover.textColor = new Color(0.58f, 0.79f, 1f);

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

            subLabelStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = true
            };

            richMiniLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                richText = true,
                wordWrap = true
            };
        }

        private void DrawHeader()
        {
            if (pawlygonLogoTexture == null)
            {
                pawlygonLogoTexture = Resources.Load<Texture2D>("pawlygon_logo");
            }

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(14, 14, 12, 12) }))
            {
                Rect headerRect = EditorGUILayout.GetControlRect(false, 84f);

                float logoSize = 70f;
                float spacing = 16f;
                float textBlockWidth = Mathf.Min(520f, Mathf.Max(300f, headerRect.width - 140f));
                float totalWidth = logoSize + spacing + textBlockWidth;
                float startX = headerRect.x + Mathf.Max(0f, (headerRect.width - totalWidth) * 0.5f);
                float logoY = headerRect.y + (headerRect.height - logoSize) * 0.5f;
                float textX = startX + logoSize + spacing;
                float textWidth = textBlockWidth;

                if (pawlygonLogoTexture != null)
                {
                    GUI.DrawTexture(new Rect(startX, logoY, logoSize, logoSize), pawlygonLogoTexture, ScaleMode.ScaleToFit, true);
                }

                EditorGUI.LabelField(new Rect(textX, headerRect.y + 18f, textWidth, 22f), "Pawlygon Avatar Setup Wizard", headerTitleStyle);
                EditorGUI.LabelField(new Rect(textX, headerRect.y + 40f, textWidth, 36f), "Tool to duplicate avatars, prepare face tracking assets, and build ready-to-edit prefabs.", headerSubtitleStyle);

            }

            EditorGUILayout.Space(6f);
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(12, 12, 10, 10) }))
            {
                EditorGUILayout.LabelField($"Made with ❤ by Pawlygon Studio  •  v{packageVersion}", footerStyle);
                EditorGUILayout.Space(6f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DrawFooterLink("Website", PawlygonWebsiteUrl);
                    GUILayout.Space(28f);
                    DrawFooterLink("X (Twitter)", PawlygonTwitterUrl);
                    GUILayout.Space(28f);
                    DrawFooterLink("YouTube", PawlygonYouTubeUrl);
                    GUILayout.Space(28f);
                    DrawFooterLink("Discord", PawlygonDiscordUrl);

                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void DrawFooterLink(string label, string url)
        {
            if (GUILayout.Button(label, footerLinkStyle))
            {
                Application.OpenURL(url);
            }
        }

        private void LoadPackageVersion()
        {
            TextAsset packageJson = AssetDatabase.LoadAssetAtPath<TextAsset>(PackageJsonPath);
            if (packageJson == null || string.IsNullOrWhiteSpace(packageJson.text))
            {
                return;
            }

            PackageManifestInfo manifestInfo = JsonUtility.FromJson<PackageManifestInfo>(packageJson.text);
            if (!string.IsNullOrWhiteSpace(manifestInfo?.version))
            {
                packageVersion = manifestInfo.version;
            }
        }

        private void DrawStepIndicator()
        {
            using (new EditorGUILayout.HorizontalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(5, 5, 5, 5) }))
            {
                DrawStepBadge(WizardStep.Setup, "1. Setup");
                DrawStepArrow();
                DrawStepBadge(WizardStep.WaitForImport, "2. Import");
                DrawStepArrow();
                DrawStepBadge(WizardStep.SelectMeshes, "3. Meshes");
                DrawStepArrow();
                DrawStepBadge(WizardStep.Prefabs, "4. Prefabs");
                DrawStepArrow();
                DrawStepBadge(WizardStep.Complete, "5. Finish");
            }
        }

        private void DrawStepBadge(WizardStep step, string label)
        {
            bool isPast = currentStep > step;
            bool isCurrent = currentStep == step;
            GUIStyle style = new GUIStyle(isCurrent ? currentStepStyle : stepStyle);

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

        private void DrawSection(string title, string description, Action drawContent)
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(15, 15, 15, 15) }))
            {
                EditorGUILayout.LabelField(title, new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(description, subLabelStyle);
                EditorGUILayout.Space(12);
                drawContent?.Invoke();
            }
        }

        private static bool DrawPrimaryButton(string text, float height = 34f)
        {
            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.6f, 1f) : new Color(0.1f, 0.4f, 0.8f);
            bool clicked = GUILayout.Button(text, new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.Height(height));
            GUI.backgroundColor = oldColor;
            return clicked;
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
            importLoadAttempts = 0;
            currentStep = WizardStep.Setup;
        }
    }
}
