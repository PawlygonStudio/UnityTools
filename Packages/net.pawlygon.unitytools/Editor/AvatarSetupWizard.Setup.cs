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
    public partial class AvatarSetupWizard
    {
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
                            // The new entry adds controls mid-event; restart the layout pass.
                            GUIUtility.ExitGUI();
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
                            // CreateAvatarStructures performs scene/asset operations and may change
                            // the wizard step, which invalidates the current IMGUI layout. Abort the
                            // current OnGUI pass cleanly so the remaining EndLayoutGroup calls do not
                            // mismatch the now-reset layout state.
                            GUIUtility.ExitGUI();
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

            if (!ConfirmAndClearExistingTargets())
            {
                return;
            }

            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath("Assets", sanitizedMainFolderName));

            if (useSeparateFolderPerAvatar)
            {
                for (int i = 0; i < avatarEntries.Count; i++)
                {
                    if (!CreateSeparateAvatarStructure(avatarEntries[i], sanitizedMainFolderName, i == avatarEntries.Count - 1))
                    {
                        if (string.IsNullOrEmpty(statusMessage))
                        {
                            statusMessage = $"Setup failed while creating the structure for '{GetEntryDisplayName(avatarEntries[i])}'.";
                        }
                        Repaint();
                        return;
                    }
                }
            }
            else
            {
                if (!CreateSharedAvatarStructure(sanitizedMainFolderName, effectiveSharedAvatarFolderName))
                {
                    if (string.IsNullOrEmpty(statusMessage))
                    {
                        statusMessage = "Setup failed while creating the shared avatar structure.";
                    }
                    Repaint();
                    return;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            foreach (AvatarEntry entry in avatarEntries)
            {
                entry.watchedFbxWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(entry.copiedFbxPath);
                entry.watchedFbxFileSize = GetAssetFileSize(entry.copiedFbxPath);
                entry.hasImportedModifiedFbx = false;
                entry.diffGenerationFailed = false;
                entry.diffGenerationError = string.Empty;
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

        /// <summary>
        /// Detects target avatar folders that already exist from a previous run and, when found,
        /// asks the user to confirm overwriting them. On confirmation the existing folders are
        /// deleted so the structure can be recreated from scratch. Returns false (after setting an
        /// inline status message) when the user cancels or a folder could not be removed, so the
        /// caller never aborts silently.
        /// </summary>
        private bool ConfirmAndClearExistingTargets()
        {
            // Use the same root list as validation, which rejects any source asset inside these
            // folders, so the deletion below can never remove the assets being copied.
            List<string> existingRoots = GetPlannedAvatarRootPaths()
                .Where(AssetDatabase.IsValidFolder)
                .ToList();

            if (existingRoots.Count == 0)
            {
                return true;
            }

            string folderList = string.Join("\n", existingRoots.Select(path => "• " + path));
            bool overwrite = EditorUtility.DisplayDialog(
                "Folder Already Exists",
                $"These folders from a previous run already exist:\n\n{folderList}\n\nOverwriting deletes each folder and everything inside it, then recreates the avatar structure. This cannot be undone.",
                "Overwrite",
                "Cancel");

            if (!overwrite)
            {
                statusMessage = "Setup cancelled — the existing folder(s) were left untouched.";
                Repaint();
                return false;
            }

            // Release any scene that lives inside a folder we are about to delete so the asset
            // deletion is not blocked by an open scene. Modified scenes were already offered for
            // saving earlier in CreateAvatarStructures.
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            foreach (string root in existingRoots)
            {
                if (!AssetDatabase.DeleteAsset(root))
                {
                    statusMessage = $"Could not delete the existing folder '{root}'. Close anything open from it and try again.";
                    Repaint();
                    return false;
                }
            }

            AssetDatabase.Refresh();
            return true;
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
            // Unity refuses to create a scene additively while any loaded scene is an unsaved
            // untitled scene. That situation is common (a fresh untitled scene, or a working
            // scene whose asset was just deleted during an overwrite), so fall back to creating
            // the working scene in Single mode and leave the saved result open instead of closing it.
            bool hasUntitledLoadedScene = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                {
                    hasUntitledLoadedScene = true;
                    break;
                }
            }

            NewSceneMode sceneMode = hasUntitledLoadedScene ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, sceneMode);

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
                // Only close the temporary scene when it was created additively alongside the
                // user's existing scene. In Single mode it is now the only (saved) scene, so
                // closing it would leave the editor with no valid scene loaded.
                if (sceneMode == NewSceneMode.Additive && newScene.IsValid())
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

            string patchTargetConflict = GetPatchTargetConflictMessage();
            if (!string.IsNullOrEmpty(patchTargetConflict))
            {
                return patchTargetConflict;
            }

            return string.Empty;
        }

        /// <summary>
        /// Checks that no two entries would write the same PatcherHub config asset or the same
        /// .hdiff files. Diff file names replace spaces with underscores, so e.g. "My Avatar.fbx"
        /// and "My_Avatar.fbx" collide even though their copied FBX names differ. Expects every
        /// entry to have passed <see cref="GetEntryValidationMessage"/>.
        /// </summary>
        private string GetPatchTargetConflictMessage()
        {
            var configOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var diffOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                AvatarEntry entry = avatarEntries[i];
                string avatarRootPath = GetPlannedAvatarRootPath(entry);
                if (string.IsNullOrEmpty(avatarRootPath))
                {
                    continue;
                }

                string configPath = GetPatchConfigAssetPath(entry, avatarRootPath);
                if (configOwners.TryGetValue(configPath, out int configOwner))
                {
                    return $"Avatars {configOwner + 1} and {i + 1} would both write the PatcherHub config '{configPath}'. Rename one of the source FBXs or use separate folders.";
                }

                configOwners[configPath] = i;

                string diffBaseName = FTDiffGenerator.GetDiffBaseName(AssetDatabase.GetAssetPath(entry.sourceFbx));
                if (string.IsNullOrEmpty(diffBaseName))
                {
                    continue;
                }

                string diffPath = FTDiffGenerator.GetFbxDiffAssetPath(avatarRootPath, diffBaseName);
                if (diffOwners.TryGetValue(diffPath, out int diffOwner))
                {
                    return $"Avatars {diffOwner + 1} and {i + 1} would both write the diff file '{diffPath}' (spaces in FBX names become underscores in diff file names). Rename one of the source FBXs or use separate folders.";
                }

                diffOwners[diffPath] = i;
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns the avatar root folder the setup will create for an entry with the current
        /// settings. This is also the folder that "Overwrite" deletes when it already exists.
        /// Returns null while the main or avatar folder name is still blank.
        /// </summary>
        private string GetPlannedAvatarRootPath(AvatarEntry entry)
        {
            string avatarFolderName = useSeparateFolderPerAvatar ? entry.avatarFolderName : sharedAvatarFolderName;
            if (string.IsNullOrWhiteSpace(mainFolderName) || string.IsNullOrWhiteSpace(avatarFolderName))
            {
                return null;
            }

            return PawlygonEditorUtils.CombineAssetPath("Assets", mainFolderName.Trim(), avatarFolderName.Trim());
        }

        /// <summary>
        /// Returns the distinct avatar root folders the setup will create (and delete on overwrite).
        /// </summary>
        private List<string> GetPlannedAvatarRootPaths()
        {
            return avatarEntries
                .Select(GetPlannedAvatarRootPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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

            // "Overwrite" deletes existing target folders, so a source living inside one of them
            // would be destroyed before it could be copied.
            foreach (string targetRoot in GetPlannedAvatarRootPaths())
            {
                if (IsAssetPathInsideFolder(fbxPath, targetRoot))
                {
                    return $"Avatar {index + 1}: the source FBX is inside '{targetRoot}', which the setup deletes and recreates. Move the source assets out of that folder or choose a different folder name.";
                }

                if (IsAssetPathInsideFolder(prefabPath, targetRoot))
                {
                    return $"Avatar {index + 1}: the source prefab is inside '{targetRoot}', which the setup deletes and recreates. Move the source assets out of that folder or choose a different folder name.";
                }
            }

            return string.Empty;
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
    }
}
