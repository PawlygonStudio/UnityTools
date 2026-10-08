using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step 6: the real outcome of each avatar (replacements, patch files, FX controller, VRCFT), quick
    /// actions on the created files and the tools to use next.
    /// </summary>
    public partial class AvatarSetupWizard
    {
        private static readonly (string Name, string Description, Action Open)[] NextSteps =
        {
            ("Getting Started", "Overview of every Pawlygon tool and when to use it.", GettingStarted.ShowWindow),
            ("Eye Muscle Settings", "Set how far the eyes can look in each direction, with a live preview.", EyeMuscleSettings.ShowWindow),
            ("Face Tracking Extras", "Ears, tail and pupils that react to your face.", FaceTrackingExtras.ShowWindow),
            ("Patch Config Package Rules", "Add the packages (and minimum versions) your customers need to the patch configs.", PatchConfigRulesEditor.ShowWindow),
        };

        private void DrawCompleteStep()
        {
            int attentionCount = avatarEntries.Count(EntryNeedsAttention);

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string icon = attentionCount == 0 ? "TestPassed" : "console.warnicon.sml";
                    GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20f), GUILayout.Height(20f));
                    EditorGUILayout.LabelField(
                        attentionCount == 0 ? "Setup complete" : $"Setup finished: {Plural(attentionCount, "avatar needs", "avatars need")} attention",
                        PawlygonEditorUI.SectionTitleStyle);
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    attentionCount == 0
                        ? "Test the avatar in the working scene, then continue with the next steps below."
                        : "The badges below show what still needs a look. Every step stays open from the step bar.",
                    PawlygonEditorUI.SubLabelStyle);
            }

            EditorGUILayout.Space(SectionSpacing);

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                DrawFinishEntryCard(i, avatarEntries[i]);
                EditorGUILayout.Space(4f);
            }

            if (cachedEntriesMissingPatchConfig.Count > 0)
            {
                using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                {
                    DrawMissingPatchConfigsNotice();
                }

                EditorGUILayout.Space(4f);
            }

            EditorGUILayout.Space(SectionSpacing);
            DrawNextStepsCard();
        }

        private void DrawFinishEntryCard(int index, AvatarEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(GetEntryDisplayName(entry), PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button(new GUIContent("Ping Folder", entry.avatarRootPath), EditorStyles.miniButtonLeft, GUILayout.Width(80f)))
                    {
                        PingAssetPath(entry.avatarRootPath);
                    }

                    using (new EditorGUI.DisabledScope(!AssetFileExists(entry.createdScenePath)))
                    {
                        if (GUILayout.Button(new GUIContent("Open Scene", entry.createdScenePath), EditorStyles.miniButtonRight, GUILayout.Width(80f)))
                        {
                            OpenWorkingScene(entry.createdScenePath);
                            // May show the save-scene dialog and loads another scene.
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                EditorGUILayout.Space(2f);

                var review = GetReviewBadge(entry);
                DrawBadgeRow("Replacements", review.Text, review.Kind, review.Tooltip);

                var patch = GetPatchBadge(entry);
                DrawBadgeRow("Patch files", patch.Text, patch.Kind, patch.Tooltip);

                var fx = GetFxBadge(entry);
                DrawBadgeRow("FX controller", fx.Text, fx.Kind, fx.Tooltip);

                if (cachedVrcftAvailable)
                {
                    if (IsVrcftOnEntry(index))
                    {
                        DrawBadgeRow("VRCFT prefab", "Added", PawlygonEditorUI.BadgeKind.Ok, "The prefab has the Pawlygon VRCFT setup.");
                    }
                    else
                    {
                        DrawBadgeRow("VRCFT prefab", "Not added", PawlygonEditorUI.BadgeKind.Neutral, "Add it in the Prefabs step (optional).");
                    }
                }

                EditorGUILayout.Space(2f);

                // Draw with this event's value so Layout and Repaint match; the change shows on the next event.
                bool showDetails = entry.showFinishDetails;
                bool newShowDetails = EditorGUILayout.Foldout(showDetails, "Details", true);
                if (showDetails)
                {
                    DrawPathRow("Avatar folder", entry.avatarRootPath);
                    DrawPathRow("Edited FBX", entry.copiedFbxPath);
                    DrawPathRow("Prefab", entry.copiedPrefabPath);
                    DrawPathRow("Working scene", entry.createdScenePath);
                    DrawPathRow("Diff generator", entry.diffGeneratorAssetPath);
                    DrawPathRow("FX controller", entry.copiedFxControllerPath);

                    if (entry.diffGenerationFailed)
                    {
                        EditorGUILayout.LabelField($"Patch error: {entry.diffGenerationError}", PawlygonEditorUI.RichMiniLabelStyle);
                    }
                }

                entry.showFinishDetails = newShowDetails;
            }
        }

        private void DrawNextStepsCard()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader("Next steps", "Tools that build on this setup.");

                foreach ((string name, string description, Action open) in NextSteps)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(name, GUILayout.Width(190f), GUILayout.Height(22f)))
                        {
                            open();
                            GUIUtility.ExitGUI();
                        }

                        GUILayout.Space(6f);
                        EditorGUILayout.LabelField(description, PawlygonEditorUI.SubLabelStyle);
                    }

                    EditorGUILayout.Space(2f);
                }
            }
        }

        private void DrawCompleteActions()
        {
            DrawBackButton(WizardStep.FXCheck);

            if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Start Over…", "Start a new setup. Created files stay in the project."), ActionButtonHeight))
            {
                ConfirmStartOver();
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            string[] scenes = avatarEntries
                .Select(entry => entry.createdScenePath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (scenes.Length == 1 &&
                PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Open Working Scene", scenes[0]), ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
            {
                OpenWorkingScene(scenes[0]);
                GUIUtility.ExitGUI();
            }
        }

        private bool EntryNeedsAttention(AvatarEntry entry)
        {
            return entry.diffGenerationFailed || entry.needsProcessing || !entry.isMeshReviewComplete || !IsFxComplete(entry);
        }

        private (string Text, PawlygonEditorUI.BadgeKind Kind, string Tooltip) GetPatchBadge(AvatarEntry entry)
        {
            if (entry.diffGenerationFailed)
            {
                return ("Failed", PawlygonEditorUI.BadgeKind.Error, FirstLine(entry.diffGenerationError));
            }

            if (entry.needsProcessing)
            {
                return ("Out of date", PawlygonEditorUI.BadgeKind.Warning, "The FBX changed after the patch was made. Update it from the banner.");
            }

            if (cachedEntriesMissingPatchConfig.Contains(entry))
            {
                return ("No patch config", PawlygonEditorUI.BadgeKind.Warning, "The patch files exist but PatcherHub has no config for them yet. Generate it below.");
            }

            return cachedPatcherHubAvailable
                ? ("Ready", PawlygonEditorUI.BadgeKind.Ok, "The patch files and the PatcherHub config are up to date.")
                : ("Ready", PawlygonEditorUI.BadgeKind.Ok, "The patch files are up to date. Install PatcherHub (Prefabs step) to also get a patch config.");
        }

        private void OpenWorkingScene(string scenePath)
        {
            if (!AssetFileExists(scenePath))
            {
                status.Error($"The working scene '{scenePath}' doesn't exist anymore.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        }
    }
}
