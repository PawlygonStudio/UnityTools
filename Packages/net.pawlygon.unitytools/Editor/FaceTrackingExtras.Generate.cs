using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Generate tab: a summary of what will be generated and the parameter settings. Generating the animator
    /// and adding the prefab to the avatar are in the action bar.
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
                if (IsGenerationStale())
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.HelpBox("Poses or settings changed since the last generation. Generate again to update the animations.", MessageType.Warning);
                }
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField($"<b>Output:</b> {FTExtrasGenerator.GetOutputFolder(selectedAvatar)}", PawlygonEditorUI.RichMiniLabelStyle);

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
        /// Generate's action bar: adding the generated prefab to the avatar, and generating (the main action).
        /// </summary>
        private void DrawGenerateActions()
        {
            if (session == null || profile == null) return;

            bool hasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FTExtrasGenerator.GetPrefabPath(selectedAvatar)) != null;
            bool onAvatar = hasPrefab && IsPrefabOnAvatar();

            PawlygonEditorUI.BeginActionBar();

            using (new EditorGUI.DisabledScope(!hasPrefab || onAvatar))
            {
                var addContent = new GUIContent(
                    onAvatar ? "✓ Prefab Is on the Avatar" : "Add Prefab to Avatar",
                    !hasPrefab ? "Generate first: this adds the generated prefab to the avatar." : $"Adds {FTExtrasGenerator.PrefabName} to {selectedAvatar.name}.");
                if (PawlygonEditorUI.DrawSecondaryButton(addContent, 28f))
                {
                    // The button turns into "Prefab Is on the Avatar", so no status message is needed.
                    FTExtrasGenerator.AddPrefabToAvatar(selectedAvatar);
                    prefabOnAvatarCache = null;
                    GUIUtility.ExitGUI();
                }
            }

            GUILayout.FlexibleSpace();

            if (PawlygonEditorUI.DrawPrimaryButton("Generate Animations", 28f, GUILayout.Width(170f)))
            {
                GenerateAnimations();
                GUIUtility.ExitGUI();
            }

            PawlygonEditorUI.EndActionBar();
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
                prefabOnAvatarCache = null;
                MessageType type = !lastResult.Success ? MessageType.Error
                    : lastResult.Warnings.Count > 0 ? MessageType.Warning
                    : MessageType.Info;
                GameObject prefab = lastResult.Success && !string.IsNullOrEmpty(lastResult.PrefabPath)
                    ? AssetDatabase.LoadAssetAtPath<GameObject>(lastResult.PrefabPath)
                    : null;
                if (prefab != null) SetStatus(lastResult.Message, type, "Ping", PawlygonStatus.Ping(prefab));
                else SetStatus(lastResult.Message, type);
                if (lastResult.Success) Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} {lastResult.Message}");
                else Debug.LogError($"{FaceTrackingExtrasCore.LogPrefix} {lastResult.Message}");
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
