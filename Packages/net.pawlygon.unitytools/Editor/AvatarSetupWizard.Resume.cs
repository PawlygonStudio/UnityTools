using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Resuming a setup whose folders already exist: the wizard's state is rebuilt from the files on disk
    /// (copied FBX and prefab, working scene, diff generator, patch files, replaced meshes, FX controller
    /// copy), anything missing is created, and the wizard opens the furthest step those files allow.
    /// </summary>
    public partial class AvatarSetupWizard
    {
        /// <summary>
        /// Rebuilds the wizard's progress from the existing avatar folders instead of deleting them. Missing
        /// files are created as in a fresh setup; nothing that exists is overwritten.
        /// </summary>
        private void ResumeExistingSetup()
        {
            status.Clear();

            foreach (AvatarEntry entry in avatarEntries)
            {
                AssignPlannedPaths(entry);
            }

            List<string> missingScenes = avatarEntries
                .Select(entry => entry.createdScenePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !AssetFileExists(path))
                .ToList();

            if (missingScenes.Count > 0 && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                status.Warning("Cancelled: the open scene has unsaved changes. Save or discard them so the missing working scene can be created.");
                return;
            }

            bool resumed = false;
            isBuildingStructure = true;

            try
            {
                resumed = CreateMissingStructure(missingScenes);

                if (resumed)
                {
                    for (int i = 0; i < avatarEntries.Count; i++)
                    {
                        ShowCreationProgress($"Reading the progress of '{GetEntryDisplayName(avatarEntries[i])}'…", i, avatarEntries.Count);
                        RestoreEntryProgress(avatarEntries[i]);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                isBuildingStructure = false;
            }

            if (!resumed)
            {
                if (!status.HasMessage || status.Type != MessageType.Error)
                {
                    status.Error("Resuming failed. See the Console for details.");
                }

                Repaint();
                return;
            }

            int importedCount = avatarEntries.Count(entry => entry.hasImportedModifiedFbx);
            int appliedCount = avatarEntries.Count(entry => entry.isMeshReviewComplete);

            WizardStep step = avatarEntries.Any(entry => entry.needsProcessing) ? WizardStep.WaitForImport
                : appliedCount < avatarEntries.Count ? WizardStep.SelectMeshes
                : WizardStep.Prefabs;

            selectedEntryIndex = 0;
            GoToStep(step);

            string rootPath = avatarEntries[0].avatarRootPath;
            status.Info(
                $"Resumed the earlier setup: {importedCount}/{avatarEntries.Count} edited FBX found, {appliedCount}/{avatarEntries.Count} with replacements applied. " +
                "Skipped reviews and FX choices aren't stored in the files, so check those steps again.",
                "Ping Folder", () => PingAssetPath(rootPath));
        }

        /// <summary>Creates the folders, copies, working scenes and diff generators that don't exist yet.</summary>
        private bool CreateMissingStructure(List<string> missingScenes)
        {
            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath("Assets", mainFolderName.Trim()));

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                AvatarEntry entry = avatarEntries[i];
                ShowCreationProgress($"Checking the files of '{GetEntryDisplayName(entry)}' ({i + 1}/{avatarEntries.Count})…", i, avatarEntries.Count);
                EnsureAvatarFolders(entry.avatarRootPath);

                if (!CopyAvatarAssets(entry))
                {
                    return false;
                }
            }

            foreach (string scenePath in missingScenes)
            {
                ShowCreationProgress("Creating the missing working scene…", avatarEntries.Count, avatarEntries.Count);
                List<string> prefabs = avatarEntries
                    .Where(entry => string.Equals(entry.createdScenePath, scenePath, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.copiedPrefabPath)
                    .ToList();

                if (!CreateSceneAsset(scenePath, prefabs))
                {
                    status.Error($"Couldn't create the working scene at '{scenePath}'.");
                    return false;
                }
            }

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath) == null &&
                    !CreateDiffGeneratorAsset(entry, entry.diffGeneratorAssetPath))
                {
                    return false;
                }
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>
        /// Sets an entry's progress from its files: the edited FBX counts as imported when it differs from the
        /// source FBX; when the patch files are newer than it, the replacements are loaded and count as
        /// applied when the prefab already uses a mesh of the edited FBX. FX outcomes come from analyzing the
        /// prefab later (a controller inside the avatar's VRChat folder is the wizard's copy).
        /// </summary>
        private void RestoreEntryProgress(AvatarEntry entry)
        {
            ResetEntryProgress(entry);

            string sourceFbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
            entry.hasImportedModifiedFbx = !FTDiffGenerator.AreFilesIdentical(ToAbsolutePath(sourceFbxPath), ToAbsolutePath(entry.copiedFbxPath));

            if (!entry.hasImportedModifiedFbx || !AreDiffFilesUpToDate(entry))
            {
                return;
            }

            entry.needsProcessing = false;
            LoadMeshSelections(entry);

            if (PrefabUsesEditedMeshes(entry))
            {
                entry.isMeshReviewComplete = true;
                entry.reviewResultLabel = "Applied";
            }
        }

        /// <summary>True when both .hdiff files exist and are at least as new as the copied FBX.</summary>
        private static bool AreDiffFilesUpToDate(AvatarEntry entry)
        {
            FTDiffGenerator diffGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath);
            string baseName = diffGenerator != null ? diffGenerator.GetBaseName() : null;
            if (string.IsNullOrEmpty(baseName))
            {
                return false;
            }

            string fbxDiffPath = ToAbsolutePath(FTDiffGenerator.GetFbxDiffAssetPath(entry.avatarRootPath, baseName));
            string metaDiffPath = ToAbsolutePath(FTDiffGenerator.GetMetaDiffAssetPath(entry.avatarRootPath, baseName));
            if (!File.Exists(fbxDiffPath) || !File.Exists(metaDiffPath))
            {
                return false;
            }

            DateTime fbxWriteTime = File.GetLastWriteTimeUtc(ToAbsolutePath(entry.copiedFbxPath));
            return File.GetLastWriteTimeUtc(fbxDiffPath) >= fbxWriteTime && File.GetLastWriteTimeUtc(metaDiffPath) >= fbxWriteTime;
        }

        /// <summary>
        /// True when a matched renderer of the copied prefab already uses a mesh of the copied (edited) FBX.
        /// The copied prefab starts out with the source FBX's meshes, so only an applied replacement does that.
        /// </summary>
        private static bool PrefabUsesEditedMeshes(AvatarEntry entry)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);
            if (prefab == null)
            {
                return false;
            }

            var matchedPaths = new HashSet<string>(
                entry.meshSelections.Where(selection => selection.hasMatch).Select(selection => selection.prefabRelativePath),
                StringComparer.OrdinalIgnoreCase);

            foreach (SkinnedMeshRenderer renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null &&
                    matchedPaths.Contains(GetRelativeTransformPath(renderer.transform)) &&
                    string.Equals(AssetDatabase.GetAssetPath(renderer.sharedMesh), entry.copiedFbxPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
