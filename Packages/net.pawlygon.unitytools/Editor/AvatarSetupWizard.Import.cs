using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step 2: waiting for the edited FBX files (detected through <see cref="FBXImportDetector"/> or chosen
    /// with "Choose FBX…"), then generating the patch files and loading the replacements. A copied FBX
    /// replaced again later marks only that avatar as out of date.
    /// </summary>
    public partial class AvatarSetupWizard
    {
        // =====================================================================
        // Drawing
        // =====================================================================

        private void DrawWaitForImportStep()
        {
            int importedCount = avatarEntries.Count(entry => entry.hasImportedModifiedFbx);
            bool allImported = importedCount == avatarEntries.Count;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Swap in your edited FBX", PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{importedCount} of {avatarEntries.Count} updated", mutedMiniStyle);
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    allImported
                        ? "Every edited FBX has been detected."
                        : "Replace each copied FBX below with your edited (face tracking) version: overwrite the file in your " +
                          "file browser, export over it from Blender, or use Choose FBX…. The wizard continues by itself as soon " +
                          "as every avatar shows Updated.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                foreach (AvatarEntry entry in avatarEntries)
                {
                    DrawImportRow(entry);
                    EditorGUILayout.Space(2f);
                }
            }
        }

        private void DrawImportRow(AvatarEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(cardStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(GetEntryDisplayName(entry), EditorStyles.boldLabel, GUILayout.ExpandWidth(false));

                    if (entry.hasImportedModifiedFbx)
                    {
                        PawlygonEditorUI.DrawBadge("Updated", PawlygonEditorUI.BadgeKind.Ok, "The edited FBX has been detected.");
                    }
                    else if (!entry.needsProcessing)
                    {
                        PawlygonEditorUI.DrawBadge("Not replaced", PawlygonEditorUI.BadgeKind.Warning,
                            "You continued without an edited FBX, so this avatar's patch doesn't change the model.");
                    }
                    else
                    {
                        PawlygonEditorUI.DrawBadge("Waiting", PawlygonEditorUI.BadgeKind.Info, "Waiting for the edited FBX.");
                    }

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button(new GUIContent("Choose FBX…", "Pick the edited FBX file; it is copied over the file below."),
                            GUILayout.Width(100f), GUILayout.Height(20f)))
                    {
                        PromptForModifiedFbx(entry);
                        // Opens a modal file panel and imports the chosen FBX.
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUILayout.SelectableLabel(entry.copiedFbxPath ?? string.Empty, pathLabelStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        private void DrawImportActions()
        {
            DrawBackButton(WizardStep.Setup);

            int waitingCount = avatarEntries.Count(entry => !entry.hasImportedModifiedFbx);
            bool anyNeedsProcessing = avatarEntries.Any(entry => entry.needsProcessing);

            if (waitingCount > 0 && anyNeedsProcessing &&
                PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Continue Without Waiting",
                    "Continue with the copied FBX files as they are now. An avatar whose FBX wasn't replaced gets a patch that changes nothing."),
                    ActionButtonHeight))
            {
                ContinueFromImport(skippedImportWait: true);
                // Generates the patch files, may show dialogs and changes the step.
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            bool canContinue = waitingCount == 0 || !anyNeedsProcessing;
            var content = canContinue
                ? new GUIContent("Continue", anyNeedsProcessing ? "Generate the patch files and review the replacements." : "Review the replacements.")
                : new GUIContent("Continue", $"Waiting for {Plural(waitingCount, "edited FBX file")}. The wizard continues by itself once all are detected.");

            using (new EditorGUI.DisabledScope(!canContinue))
            {
                if (PawlygonEditorUI.DrawPrimaryButton(content, ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
                {
                    ContinueFromImport(skippedImportWait: false);
                    GUIUtility.ExitGUI();
                }
            }
        }

        // =====================================================================
        // Continuing to Replacements
        // =====================================================================

        /// <summary>
        /// Generates the patch files and loads the replacements of every avatar that needs it, then opens
        /// Replacements. Avatars already processed keep their review.
        /// </summary>
        private void ContinueFromImport(bool skippedImportWait)
        {
            if (!CanLoadImportedAssets())
            {
                status.Error("A copied FBX or prefab can't be loaded yet. Wait for Unity to finish importing (check the Console for import errors), then continue.");
                return;
            }

            List<AvatarEntry> pending = avatarEntries.Where(entry => entry.needsProcessing).ToList();
            if (pending.Count == 0)
            {
                GoToStep(WizardStep.SelectMeshes);
                return;
            }

            ProcessEntriesAndReview(pending, skippedImportWait);
        }

        /// <summary>
        /// Generates the patch files of <paramref name="entries"/> (with a cancelable progress bar), reloads
        /// their replacements and opens Replacements on the first avatar to review. Diff failures don't block
        /// the review; they are reported with a dialog and a persistent banner.
        /// </summary>
        private void ProcessEntriesAndReview(List<AvatarEntry> entries, bool skippedImportWait)
        {
            // A copied FBX that still matches the original yields a patch that changes nothing,
            // usually because the edited FBX was never dropped in.
            List<string> unchangedEntryNames = GetEntriesWithUnchangedFbx(entries).Select(GetEntryDisplayName).ToList();
            if (!FTDiffGenerator.ConfirmGenerationForUnchangedModels(unchangedEntryNames))
            {
                status.Warning($"Nothing was generated: the FBX is still the original for {string.Join(", ", unchangedEntryNames)}. " +
                               "Replace the copied FBX with your edited version (or use Choose FBX…), then continue.");
                Repaint();
                return;
            }

            // A queued import transition must not run again after this.
            EditorApplication.delayCall -= TryContinueAfterImport;
            pendingImportTransition = false;

            int generatedCount = GenerateDiffFilesForEntries(entries, out int failedCount, out List<AvatarEntry> processed, out bool cancelled);

            foreach (AvatarEntry entry in processed)
            {
                LoadMeshSelections(entry);
                entry.isMeshReviewComplete = false;
                entry.reviewResultLabel = string.Empty;
                entry.needsProcessing = false;
            }

            if (cancelled)
            {
                status.Warning($"Stopped after {Plural(processed.Count, "avatar")} of {entries.Count}. Continue again to generate the rest.");
                Repaint();
                return;
            }

            GoToStep(WizardStep.SelectMeshes);
            if (processed.Count > 0)
            {
                selectedEntryIndex = avatarEntries.IndexOf(processed[0]);
            }

            var message = new List<string>
            {
                skippedImportWait ? "Continued without waiting." : "Edited FBX detected.",
                $"Generated the patch files for {Plural(generatedCount, "avatar")}."
            };

            if (failedCount > 0)
            {
                message.Add($"Patch generation FAILED for {Plural(failedCount, "avatar")} (see the banner above and the Console).");
            }

            if (unchangedEntryNames.Count > 0)
            {
                message.Add($"The FBX is still the original for {string.Join(", ", unchangedEntryNames)}, so its patch doesn't change the model.");
            }

            message.Add("Review the replacements of each avatar.");
            string text = string.Join(" ", message);

            if (failedCount > 0) status.Error(text);
            else if (unchangedEntryNames.Count > 0) status.Warning(text);
            else status.Info(text);

            if (failedCount > 0)
            {
                EditorUtility.DisplayDialog(
                    "Patch Generation Failed",
                    $"The face tracking diff files could not be generated for:\n\n{BuildDiffFailureList()}\n\n" +
                    "No patch config was written for these avatars. Fix the problem shown in the Console, then use " +
                    "\"Retry\" in the banner at the top of the wizard.",
                    "OK");
            }
        }

        // =====================================================================
        // Import detection
        // =====================================================================

        private void HandleFbxReimported(string importedAssetPath)
        {
            // The copies made while building the structure are not edited FBXs.
            if (isBuildingStructure || !IsStructureCreated)
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

            if (!matchedAny)
            {
                return;
            }

            if (currentStep == WizardStep.WaitForImport)
            {
                QueueContinueAfterImport();
            }
            else
            {
                // Later steps show the "out of date" banner.
                Repaint();
            }
        }

        private void QueueContinueAfterImport()
        {
            if (pendingImportTransition)
            {
                return;
            }

            pendingImportTransition = true;
            EditorApplication.delayCall -= TryContinueAfterImport;
            EditorApplication.delayCall += TryContinueAfterImport;
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
            entry.needsProcessing = true;
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

            string selectedPath = EditorUtility.OpenFilePanel("Choose the Edited FBX", initialDirectory, "fbx");
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

            string resolvedSourcePath = ResolveAbsoluteFilePath(sourcePath);
            if (string.IsNullOrEmpty(resolvedSourcePath) || !File.Exists(resolvedSourcePath))
            {
                status.Error("The selected FBX file could not be found.");
                return;
            }

            if (!string.Equals(Path.GetExtension(resolvedSourcePath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                status.Error("Only .fbx files can be used here.");
                return;
            }

            if (string.IsNullOrEmpty(entry.copiedFbxPath))
            {
                status.Error($"'{GetEntryDisplayName(entry)}' has no copied FBX yet. Create the avatar structure first.");
                return;
            }

            string targetAbsolutePath = ToAbsolutePath(entry.copiedFbxPath);
            string fileName = Path.GetFileName(resolvedSourcePath);

            try
            {
                if (!string.Equals(Path.GetFullPath(resolvedSourcePath), Path.GetFullPath(targetAbsolutePath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(resolvedSourcePath, targetAbsolutePath, true);
                }

                AssetDatabase.ImportAsset(entry.copiedFbxPath, ImportAssetOptions.ForceSynchronousImport);

                if (AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath) == null)
                {
                    status.Error($"Unity couldn't import '{fileName}' as an FBX. Check the Console for import errors.");
                    return;
                }

                TryMarkEntryAsImported(entry, force: true);

                string sourceFbxAssetPath = entry.sourceFbx != null ? AssetDatabase.GetAssetPath(entry.sourceFbx) : null;
                if (!string.IsNullOrEmpty(sourceFbxAssetPath) && FTDiffGenerator.AreFilesIdentical(ToAbsolutePath(sourceFbxAssetPath), targetAbsolutePath))
                {
                    status.Warning($"Copied '{fileName}' into '{Path.GetFileName(entry.copiedFbxPath)}', but it is identical to the original FBX, so its patch would not change the model.");
                }
                else
                {
                    status.Info($"Copied '{fileName}' into '{Path.GetFileName(entry.copiedFbxPath)}'.");
                }

                if (currentStep == WizardStep.WaitForImport)
                {
                    QueueContinueAfterImport();
                }
            }
            catch (Exception exception)
            {
                status.Error($"Couldn't copy '{fileName}': {exception.Message}");
            }

            Repaint();
        }

        private static string ResolveAbsoluteFilePath(string path)
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

        private void TryContinueAfterImport()
        {
            pendingImportTransition = false;

            // A transition queued before the user continued manually, went to another step or started
            // over must not run.
            if (currentStep != WizardStep.WaitForImport || !IsStructureCreated)
            {
                return;
            }

            int waitingCount = avatarEntries.Count(entry => !entry.hasImportedModifiedFbx);
            if (waitingCount > 0)
            {
                status.Info($"Edited FBX detected. Waiting for {waitingCount} more.");
                Repaint();
                return;
            }

            if (!avatarEntries.Any(entry => entry.needsProcessing))
            {
                return;
            }

            // FBXImportDetector reports imports from OnPostprocessAllAssets, after the import batch
            // has completed, so the assets are loadable now unless the import itself failed.
            if (!CanLoadImportedAssets())
            {
                Debug.LogWarning("[AvatarSetupWizard] All edited FBXs were detected, but at least one copied FBX or prefab could not be loaded.");
                status.Error("Every edited FBX was detected, but a copied FBX or prefab can't be loaded. Check the Console for import errors, then click Continue.");
                Repaint();
                return;
            }

            ProcessEntriesAndReview(avatarEntries.Where(entry => entry.needsProcessing).ToList(), skippedImportWait: false);
        }

        private bool CanLoadImportedAssets()
        {
            return avatarEntries.All(entry =>
                AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath) != null &&
                AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath) != null);
        }

        // =====================================================================
        // Patch files
        // =====================================================================

        /// <summary>
        /// Regenerates the .hdiff files of <paramref name="entries"/> with a cancelable progress bar (checked
        /// between avatars) and, when PatcherHub is installed, writes a patch config for every entry whose
        /// diffs were generated. Updates each entry's <c>diffGenerationFailed</c> flag. Returns the number
        /// generated; <paramref name="processed"/> lists the entries that were attempted (all of them unless
        /// <paramref name="cancelled"/>).
        /// </summary>
        private int GenerateDiffFilesForEntries(IReadOnlyList<AvatarEntry> entries, out int failedCount,
            out List<AvatarEntry> processed, out bool cancelled)
        {
            int generatedCount = 0;
            failedCount = 0;
            processed = new List<AvatarEntry>();
            cancelled = false;

            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    AvatarEntry entry = entries[i];
                    string info = $"Generating the patch files for '{GetEntryDisplayName(entry)}' ({i + 1}/{entries.Count})…";
                    if (EditorUtility.DisplayCancelableProgressBar(ProgressTitle, info, (float)i / entries.Count))
                    {
                        cancelled = true;
                        break;
                    }

                    processed.Add(entry);
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
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            InvalidateProjectCaches();
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

        private string BuildDiffFailureList()
        {
            return string.Join("\n", avatarEntries
                .Where(entry => entry.diffGenerationFailed)
                .Select(entry => $"• {GetEntryDisplayName(entry)}: {FirstLine(entry.diffGenerationError)}"));
        }

        /// <summary>
        /// Draws a persistent error banner listing the entries whose diff generation failed, so the
        /// problem stays visible after the status message has been replaced.
        /// </summary>
        private void DrawDiffFailureWarning()
        {
            if (!avatarEntries.Any(entry => entry.diffGenerationFailed))
            {
                return;
            }

            EditorGUILayout.HelpBox(
                $"The patch files couldn't be generated for:\n{BuildDiffFailureList()}\n\n" +
                "Customers can't patch these avatars yet. Fix the problem shown in the Console, then retry.",
                MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Retry", "Generate the patch files of these avatars again."), GUILayout.Width(90f), GUILayout.Height(22f)))
                {
                    RetryFailedDiffGeneration();
                    // Runs hdiffz, refreshes the AssetDatabase and may show a dialog.
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.Space(SectionSpacing);
        }

        /// <summary>
        /// Regenerates diffs (and patch configs, if PatcherHub is installed) for the entries whose
        /// previous diff generation failed, reporting the outcome in the status bar.
        /// </summary>
        private void RetryFailedDiffGeneration()
        {
            List<AvatarEntry> failed = avatarEntries.Where(entry => entry.diffGenerationFailed).ToList();
            int generatedCount = GenerateDiffFilesForEntries(failed, out int failedCount, out _, out bool cancelled);

            if (cancelled)
            {
                status.Warning("Retry stopped before every avatar was processed.");
            }
            else if (failedCount > 0)
            {
                status.Error($"Patch generation still fails for {Plural(failedCount, "avatar")}. See the Console for hdiffz's output.");
                EditorUtility.DisplayDialog("Patch Generation Failed",
                    $"The face tracking diff files could not be generated for:\n\n{BuildDiffFailureList()}\n\nSee the Console for details.", "OK");
            }
            else
            {
                status.Info($"Generated the patch files for {Plural(generatedCount, "avatar")}.");
            }

            Repaint();
        }

        /// <summary>
        /// Returns the entries of <paramref name="entries"/> whose copied FBX is still byte-identical to the
        /// source FBX it was copied from.
        /// </summary>
        private static List<AvatarEntry> GetEntriesWithUnchangedFbx(IEnumerable<AvatarEntry> entries)
        {
            var result = new List<AvatarEntry>();

            foreach (AvatarEntry entry in entries)
            {
                if (string.IsNullOrEmpty(entry.diffGeneratorAssetPath))
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
    }
}
