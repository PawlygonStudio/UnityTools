using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Generate tab: a summary of what will be generated, parameter settings, generating the animator and
    /// adding the prefab to the avatar.
    /// </summary>
    public partial class FaceTrackingExtras
    {
        private static readonly string[] ParameterFields =
        {
            nameof(FTExtrasGenerationSettings.eyeLeftX),
            nameof(FTExtrasGenerationSettings.eyeRightX),
            nameof(FTExtrasGenerationSettings.eyeY),
            nameof(FTExtrasGenerationSettings.smileFrownLeft),
            nameof(FTExtrasGenerationSettings.smileFrownRight),
            nameof(FTExtrasGenerationSettings.jawX),
            nameof(FTExtrasGenerationSettings.eyeLidLeft),
            nameof(FTExtrasGenerationSettings.eyeLidRight),
            nameof(FTExtrasGenerationSettings.eyeTrackingActive),
            nameof(FTExtrasGenerationSettings.lipTrackingActive),
            nameof(FTExtrasGenerationSettings.eyeDilationEnable),
            nameof(FTExtrasGenerationSettings.gazeFullAt),
            nameof(FTExtrasGenerationSettings.moodFullAt),
            nameof(FTExtrasGenerationSettings.jawFullAt),
            nameof(FTExtrasGenerationSettings.tailFollowsJawParameter),
            nameof(FTExtrasGenerationSettings.menuName),
        };

        // =====================================================================
        // Generate tab
        // =====================================================================

        private void DrawGenerateTab()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Generate", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Bakes the poses into animation clips and builds the FX controller, the Tail follows Jaw menu toggle, the fake pupil dilation and a VRCFury prefab.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                DrawGenerateSummary();
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField($"<b>Output:</b> {FTExtrasGenerator.GetOutputFolder(selectedAvatar)}", PawlygonEditorUI.RichMiniLabelStyle);
                EditorGUILayout.Space(8f);

                if (PawlygonEditorUI.DrawPrimaryButton("Generate Animations", 32f))
                {
                    GenerateAnimations();
                    GUIUtility.ExitGUI();
                }

                bool hasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FTExtrasGenerator.GetPrefabPath(selectedAvatar)) != null;
                bool onAvatar = hasPrefab && FTExtrasGenerator.IsPrefabOnAvatar(selectedAvatar);
                using (new EditorGUI.DisabledScope(!hasPrefab || onAvatar))
                {
                    string label = onAvatar ? "✓ Prefab is on the avatar" : "Add Prefab to Avatar";
                    if (GUILayout.Button(label, GUILayout.Height(24f)))
                    {
                        FTExtrasGenerator.AddPrefabToAvatar(selectedAvatar);
                        SetStatus($"Added {FTExtrasGenerator.PrefabName} to {selectedAvatar.name}.", MessageType.Info);
                    }
                }

                if (lastResult != null)
                {
                    foreach (string warning in lastResult.Warnings)
                    {
                        EditorGUILayout.HelpBox(warning, MessageType.Warning);
                    }
                }
            }

            EditorGUILayout.Space(SectionSpacing);
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                showParameterSettings = EditorGUILayout.Foldout(showParameterSettings, "Parameters & Ranges", true);
                if (!showParameterSettings) return;

                EditorGUILayout.LabelField(
                    "Face tracking parameter names default to the OSCmooth proxies used by Pawlygon VRCFT. The ranges set how far an input must go for a pose to be fully applied.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(4f);
                DrawGenerationFields(ParameterFields);
            }
        }

        /// <summary>
        /// One line per part of the output, so it is clear what Generate will produce.
        /// </summary>
        private void DrawGenerateSummary()
        {
            int earSet = 0, earTotal = 0, tailSet = 0, tailTotal = 0;
            foreach (var definition in FTExtrasPoses.All)
            {
                if (definition.IsDerived || !HasBonesFor(definition.Group)) continue;
                bool set = profile.GetStoredPose(definition.Id) != null;
                if (definition.Group == FTExtrasPoses.PoseGroup.Ears) { earTotal++; if (set) earSet++; }
                else { tailTotal++; if (set) tailSet++; }
            }

            DrawSummaryLine("Ears", earTotal == 0 ? "no ear bones" : $"{earSet}/{earTotal} poses set", earSet > 0);
            DrawSummaryLine("Tail", tailTotal == 0 ? "no tail bones" : $"{tailSet}/{tailTotal} poses set", tailSet > 0);

            string pupils;
            bool pupilsOk;
            if (!profile.generation.fakeDilation) { pupils = "off"; pupilsOk = false; }
            else if (pupilMeshCount == 0) { pupils = $"on, but no mesh has '{FTExtrasPupils.DilationShape}'"; pupilsOk = false; }
            else { pupils = $"fake dilation on {pupilMeshCount} mesh{(pupilMeshCount == 1 ? "" : "es")}"; pupilsOk = true; }
            DrawSummaryLine("Pupils", pupils, pupilsOk);
        }

        private void DrawSummaryLine(string label, string value, bool ok)
        {
            string colour = ok ? "#6BCB77" : "#909090";
            EditorGUILayout.LabelField($"<b>{label}:</b> <color={colour}>{value}</color>", poseLabelStyle);
        }

        /// <summary>
        /// Draws fields of the profile's generation settings with undo support.
        /// </summary>
        private void DrawGenerationFields(params string[] fields)
        {
            if (profileObject == null || profileObject.targetObject != profile) profileObject = new SerializedObject(profile);
            profileObject.Update();

            SerializedProperty generation = profileObject.FindProperty(nameof(FTExtrasProfile.generation));
            foreach (string field in fields)
            {
                EditorGUILayout.PropertyField(generation.FindPropertyRelative(field), true);
            }

            profileObject.ApplyModifiedProperties();
        }

        private void GenerateAnimations()
        {
            StopMode();
            StopPupilPreview();
            try
            {
                lastResult = FTExtrasGenerator.Generate(profile, selectedAvatar);
                SetStatus(lastResult.Message, lastResult.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info);
                Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} {lastResult.Message}");
                foreach (string warning in lastResult.Warnings)
                {
                    Debug.LogWarning($"{FaceTrackingExtrasCore.LogPrefix} {warning}");
                }
            }
            catch (System.Exception ex)
            {
                lastResult = null;
                SetStatus($"Generation failed: {ex.Message}", MessageType.Error);
                Debug.LogException(ex);
            }
        }
    }
}
