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
                                GenerateDiffsAndMoveToMeshSelection(importedOnly: true, skippedImportWait: false);
                            }
                            else
                            {
                                statusMessage = "Not every modified FBX is ready yet. Finish importing all copied FBXs, then continue.";
                            }

                            // Diff generation refreshes the AssetDatabase, may show a dialog and
                            // changes the step, all of which invalidate the current layout pass.
                            GUIUtility.ExitGUI();
                        }

                        if (GUILayout.Button("Skip Waiting", GUILayout.Height(34f)))
                        {
                            if (CanLoadImportedAssets())
                            {
                                GenerateDiffsAndMoveToMeshSelection(importedOnly: false, skippedImportWait: true);
                            }
                            else
                            {
                                statusMessage = "The copied FBX and prefab assets are not loadable yet. Wait for Unity to finish importing before skipping.";
                            }

                            GUIUtility.ExitGUI();
                        }
                    }
                });
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
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            EditorApplication.delayCall += TryMoveToMeshSelectionAfterImport;
        }

        /// <summary>
        /// Marks an entry's copied FBX as replaced when its write time or file size differs from
        /// the last recorded one (or always, with <paramref name="force"/>). Any difference counts,
        /// not only a newer write time: copying a file in Explorer keeps its original write time,
        /// so an edit exported before setup can be older than the copy it replaces. Reimports that
        /// leave the file untouched (e.g. changing import settings) are ignored.
        /// </summary>
        private bool TryMarkEntryAsImported(AvatarEntry entry, bool force)
        {
            if (entry == null || string.IsNullOrEmpty(entry.copiedFbxPath))
            {
                return false;
            }

            long currentWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(entry.copiedFbxPath);
            long currentFileSize = GetAssetFileSize(entry.copiedFbxPath);
            bool fileChanged = currentWriteTimeUtcTicks != entry.watchedFbxWriteTimeUtcTicks ||
                               currentFileSize != entry.watchedFbxFileSize;
            if (!force && !fileChanged)
            {
                return false;
            }

            entry.watchedFbxWriteTimeUtcTicks = currentWriteTimeUtcTicks;
            entry.watchedFbxFileSize = currentFileSize;
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

                string sourceFbxAssetPath = entry.sourceFbx != null ? AssetDatabase.GetAssetPath(entry.sourceFbx) : null;
                if (!string.IsNullOrEmpty(sourceFbxAssetPath) && FTDiffGenerator.AreFilesIdentical(ToAbsolutePath(sourceFbxAssetPath), targetAbsolutePath))
                {
                    statusMessage += " Warning: this FBX is identical to the original, so its patch would not change the model.";
                }
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

            // A transition queued before the user continued manually (or started over) must not
            // regenerate diffs and reset the mesh selections of a later step.
            if (currentStep != WizardStep.WaitForImport)
            {
                return;
            }

            if (!avatarEntries.All(entry => entry.hasImportedModifiedFbx))
            {
                statusMessage = $"Waiting for {avatarEntries.Count(entry => !entry.hasImportedModifiedFbx)} more modified FBX import(s).";
                Repaint();
                return;
            }

            // FBXImportDetector reports imports from OnPostprocessAllAssets, after the import batch
            // has completed, so the assets are loadable now unless the import itself failed.
            if (!CanLoadImportedAssets())
            {
                Debug.LogWarning("[AvatarSetupWizard] All modified FBXs were detected, but at least one copied FBX or prefab could not be loaded.");
                statusMessage = "All modified FBXs were detected, but at least one copied FBX or prefab could not be loaded. Check the Console for import errors, then continue manually.";
                Repaint();
                return;
            }

            GenerateDiffsAndMoveToMeshSelection(importedOnly: true, skippedImportWait: false);
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

        /// <summary>
        /// Regenerates the .hdiff files for the wizard's entries and, when PatcherHub is installed,
        /// writes a patch config for every entry whose diffs were generated successfully. Each
        /// entry's <c>diffGenerationFailed</c> flag is updated; returns the number of entries whose
        /// diffs were generated and reports failures through <paramref name="failedCount"/>.
        /// </summary>
        private int GenerateDiffFilesForEntries(Func<AvatarEntry, bool> shouldGenerate, out int failedCount)
        {
            int generatedCount = 0;
            failedCount = 0;

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (!shouldGenerate(entry))
                {
                    continue;
                }

                entry.diffGenerationFailed = false;
                entry.diffGenerationError = string.Empty;

                if (string.IsNullOrEmpty(entry.diffGeneratorAssetPath))
                {
                    MarkDiffGenerationFailed(entry, "No diff generator asset was created for this avatar.");
                    failedCount++;
                    continue;
                }

                FTDiffGenerator diffGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath);
                if (diffGenerator == null)
                {
                    MarkDiffGenerationFailed(entry, $"Could not load the diff generator asset at '{entry.diffGeneratorAssetPath}'.");
                    failedCount++;
                    continue;
                }

                if (!diffGenerator.GenerateDiffFiles(out string diffError))
                {
                    // Never write a patch config that would point at missing or stale diff files.
                    MarkDiffGenerationFailed(entry, diffError);
                    failedCount++;
                    continue;
                }

                generatedCount++;

                // Generate FTPatchConfig with wizard context if PatcherHub is installed
                if (FTPatchConfigGenerator.IsPatcherHubAvailable())
                {
                    FTPatchConfigGenerator.ConfigContext configContext = BuildPatchConfigContext(entry, diffGenerator);
                    if (configContext != null)
                    {
                        FTPatchConfigGenerator.GenerateConfig(configContext);
                    }
                }
            }

            return generatedCount;
        }

        /// <summary>
        /// Builds the PatcherHub config context for an entry whose structure has been created.
        /// Used by every path that writes a config; the shared
        /// <see cref="FTPatchConfigGenerator.BuildContext"/> keeps it identical to what the diff
        /// generator inspector writes. Returns null when the diff base name or the avatar root
        /// is unknown.
        /// </summary>
        private FTPatchConfigGenerator.ConfigContext BuildPatchConfigContext(AvatarEntry entry, FTDiffGenerator diffGenerator)
        {
            if (diffGenerator == null)
            {
                return null;
            }

            GameObject copiedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath);

            return FTPatchConfigGenerator.BuildContext(
                diffGenerator.originalModelFbx,
                entry.avatarRootPath,
                diffGenerator.GetBaseName(),
                GetPatchConfigAvatarName(entry),
                copiedPrefab != null ? new[] { copiedPrefab } : null);
        }

        /// <summary>
        /// Returns the avatar name used for an entry's PatcherHub config file name and display
        /// name. Separate-folder mode uses the entry's own folder name. Shared-folder mode uses the
        /// shared folder name for a single entry, and the source FBX file name when several entries
        /// share the folder (the per-entry folder name is not editable there, so it would be the
        /// same default for every entry and their configs would overwrite each other).
        /// </summary>
        private string GetPatchConfigAvatarName(AvatarEntry entry)
        {
            string name;

            if (useSeparateFolderPerAvatar)
            {
                name = entry.avatarFolderName;
            }
            else if (avatarEntries.Count == 1)
            {
                name = sharedAvatarFolderName;
            }
            else
            {
                string sourceFbxPath = entry.sourceFbx != null ? AssetDatabase.GetAssetPath(entry.sourceFbx) : null;
                name = string.IsNullOrEmpty(sourceFbxPath) ? null : Path.GetFileNameWithoutExtension(sourceFbxPath);
            }

            name = name?.Trim();
            return string.IsNullOrEmpty(name) ? GetEntryDisplayName(entry) : name;
        }

        private string GetPatchConfigAssetPath(AvatarEntry entry, string avatarRootPath)
        {
            return FTPatchConfigGenerator.GetConfigAssetPath(avatarRootPath, GetPatchConfigAvatarName(entry));
        }

        private static void MarkDiffGenerationFailed(AvatarEntry entry, string errorMessage)
        {
            entry.diffGenerationFailed = true;
            entry.diffGenerationError = string.IsNullOrEmpty(errorMessage) ? "Unknown error." : errorMessage;
            Debug.LogError($"[AvatarSetupWizard] Diff generation failed for '{GetEntryDisplayName(entry)}': {entry.diffGenerationError}");
        }

        /// <summary>
        /// Generates the diff files for the wizard's entries, then advances to mesh selection.
        /// Diff failures do not block mesh review (it does not depend on the diffs), but they are
        /// always reported with a dialog and a persistent warning instead of a success message.
        /// </summary>
        private void GenerateDiffsAndMoveToMeshSelection(bool importedOnly, bool skippedImportWait)
        {
            Func<AvatarEntry, bool> shouldGenerate = entry => !importedOnly || entry.hasImportedModifiedFbx;

            // A copied FBX that still matches the original yields a patch that changes nothing,
            // usually because the edited FBX was never dropped in (e.g. after "Skip Waiting").
            List<string> unchangedEntryNames = GetEntriesWithUnchangedFbx(shouldGenerate)
                .Select(GetEntryDisplayName)
                .ToList();
            if (!FTDiffGenerator.ConfirmGenerationForUnchangedModels(unchangedEntryNames))
            {
                statusMessage = $"Diff generation cancelled. The modified FBX is identical to the original for: {string.Join(", ", unchangedEntryNames)}. " +
                                "Replace the copied FBX with your edited version (or use \"Choose FBX...\"), then continue.";
                Repaint();
                return;
            }

            int generatedDiffCount = GenerateDiffFilesForEntries(shouldGenerate, out int failedDiffCount);
            MoveToMeshSelection(generatedDiffCount, failedDiffCount, skippedImportWait, unchangedEntryNames.Count);

            if (failedDiffCount > 0)
            {
                EditorUtility.DisplayDialog(
                    "Diff Generation Failed",
                    $"The face tracking diff files could not be generated for:\n\n{BuildDiffFailureList()}\n\n" +
                    "No patch config was written for these avatars. Fix the problem shown in the Console, then use " +
                    "\"Retry Diff Generation\" at the top of the wizard.",
                    "OK");
            }
        }

        private string BuildDiffFailureList()
        {
            return string.Join("\n", avatarEntries
                .Where(entry => entry.diffGenerationFailed)
                .Select(entry => $"• {GetEntryDisplayName(entry)}: {FirstLine(entry.diffGenerationError)}"));
        }

        /// <summary>
        /// Draws a persistent error box listing the entries whose diff generation failed, so the
        /// problem stays visible after the status message has been replaced.
        /// </summary>
        private void DrawDiffFailureWarning()
        {
            if (currentStep == WizardStep.Setup || currentStep == WizardStep.WaitForImport)
            {
                return;
            }

            if (!avatarEntries.Any(entry => entry.diffGenerationFailed))
            {
                return;
            }

            EditorGUILayout.HelpBox(
                $"Diff generation failed, so these avatars have no up-to-date patch files:\n{BuildDiffFailureList()}\n\n" +
                "Fix the problem shown in the Console, then retry.",
                MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Retry Diff Generation", GUILayout.Width(170f), GUILayout.Height(24f)))
                {
                    RetryFailedDiffGeneration();
                    // Runs hdiffz, refreshes the AssetDatabase and may show a dialog.
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
        }

        /// <summary>
        /// Regenerates diffs (and patch configs, if PatcherHub is installed) for the entries whose
        /// previous diff generation failed, reporting the outcome in the status message.
        /// </summary>
        private void RetryFailedDiffGeneration()
        {
            int generatedCount = GenerateDiffFilesForEntries(entry => entry.diffGenerationFailed, out int failedCount);

            if (failedCount > 0)
            {
                statusMessage = $"Diff generation still fails for {failedCount} avatar entr{(failedCount == 1 ? "y" : "ies")}. See the Console for hdiffz's output.";
                EditorUtility.DisplayDialog("Diff Generation Failed",
                    $"The face tracking diff files could not be generated for:\n\n{BuildDiffFailureList()}\n\nSee the Console for details.", "OK");
            }
            else
            {
                statusMessage = $"Regenerated diff files for {generatedCount} avatar entr{(generatedCount == 1 ? "y" : "ies")}.";
            }

            Repaint();
        }

        /// <summary>
        /// Returns the entries (among those matching <paramref name="filter"/>) whose copied FBX is
        /// still byte-identical to the source FBX it was copied from.
        /// </summary>
        private List<AvatarEntry> GetEntriesWithUnchangedFbx(Func<AvatarEntry, bool> filter)
        {
            var result = new List<AvatarEntry>();

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (!filter(entry) || string.IsNullOrEmpty(entry.diffGeneratorAssetPath))
                {
                    continue;
                }

                FTDiffGenerator diffGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath);
                if (diffGenerator != null && diffGenerator.IsModifiedFbxIdenticalToOriginal())
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        private void MoveToMeshSelection(int generatedDiffCount, int failedDiffCount, bool skippedImportWait, int unchangedFbxCount)
        {
            // A queued import transition must not run after we moved on.
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            pendingImportTransition = false;

            foreach (AvatarEntry entry in avatarEntries)
            {
                LoadMeshSelections(entry);
                entry.isMeshReviewComplete = false;
                entry.reviewResultLabel = string.Empty;
            }

            selectedEntryIndex = Mathf.Clamp(FindNextIncompleteEntryIndex(0), 0, avatarEntries.Count - 1);
            currentStep = WizardStep.SelectMeshes;

            string prefix = skippedImportWait ? "Skipped the import wait." : "All modified FBXs imported.";
            string diffSummary = generatedDiffCount > 0
                ? $" Regenerated diff files for {generatedDiffCount} avatar entr{(generatedDiffCount == 1 ? "y" : "ies")}."
                : string.Empty;
            string failureSummary = failedDiffCount > 0
                ? $" Diff generation FAILED for {failedDiffCount} avatar entr{(failedDiffCount == 1 ? "y" : "ies")} — see the error above and the Console."
                : string.Empty;

            string unchangedSummary = unchangedFbxCount > 0
                ? $" Warning: the modified FBX is identical to the original for {unchangedFbxCount} avatar entr{(unchangedFbxCount == 1 ? "y" : "ies")}, so its patch does not change the model."
                : string.Empty;

            statusMessage = $"{prefix}{diffSummary}{failureSummary}{unchangedSummary} Review each avatar entry and apply the mesh and rig replacements you want.";

            Repaint();
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
                            // Opens a modal file panel and imports the chosen FBX.
                            GUIUtility.ExitGUI();
                        }
                    }

                    EditorGUILayout.LabelField("Choose an updated FBX to replace this copied file", PawlygonEditorUI.RichMiniLabelStyle);
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.Space(3f);
                }
            }
        }
    }
}
