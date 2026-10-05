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

                    DrawMissingPatchConfigsNotice();

                    EditorGUILayout.Space(SectionSpacing);

                    if (PawlygonEditorUI.DrawPrimaryButton("Start Over", 34f))
                    {
                        ResetWizard();
                        GUIUtility.ExitGUI();
                    }
                });
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
    }
}
