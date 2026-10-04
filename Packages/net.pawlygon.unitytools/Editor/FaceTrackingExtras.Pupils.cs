using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Pupils tab: turn the fake pupil dilation on or off, tune it, and preview the idle drift and the blink
    /// reflex on the avatar. The preview evaluates the same curves the generator bakes
    /// (<see cref="FTExtrasPupils"/>), and settings changes apply to it immediately.
    /// </summary>
    public partial class FaceTrackingExtras
    {
        private const double PupilFrameInterval = 1.0 / 60.0;
        private double lastPupilApplyTime;

        // =====================================================================
        // Pupils tab
        // =====================================================================

        private void DrawPupilsTab()
        {
            if (profileObject == null || profileObject.targetObject != profile) profileObject = new SerializedObject(profile);
            profileObject.Update();
            SerializedProperty generation = profileObject.FindProperty(nameof(FTExtrasProfile.generation));
            SerializedProperty enabled = generation.FindPropertyRelative(nameof(FTExtrasGenerationSettings.fakeDilation));

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Fake Pupil Dilation", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "For headsets without pupil tracking. While eye tracking is active, the pupils drift gently and react to every blink: " +
                    "they reopen wide, tighten, then settle. The layer switches itself off when eye tracking stops or VRCFT's real pupil dilation " +
                    $"(<b>{profile.generation.eyeDilationEnable}</b>) is on, so it never overrides real tracking.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                EditorGUI.BeginChangeCheck();
                bool isEnabled = EditorGUILayout.ToggleLeft("Add fake pupil dilation", enabled.boolValue, EditorStyles.boldLabel);
                if (EditorGUI.EndChangeCheck())
                {
                    enabled.boolValue = isEnabled;
                    if (!isEnabled) StopPupilPreview();
                }

                EditorGUILayout.Space(4f);
                DrawPupilMeshes();
            }

            profileObject.ApplyModifiedProperties();
            if (!enabled.boolValue) return;

            EditorGUILayout.Space(SectionSpacing);
            DrawPupilPreviewSection();

            EditorGUILayout.Space(SectionSpacing);
            DrawPupilAdvancedSection();
        }

        private void DrawPupilMeshes()
        {
            var (dilation, constrict) = FTExtrasPupils.FindMeshes(selectedAvatar);
            if (dilation.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No mesh on this avatar has an '{FTExtrasPupils.DilationShape}' blendshape (Unified Expressions naming), so the fake dilation will be skipped.",
                    MessageType.Warning);
                return;
            }

            foreach (SkinnedMeshRenderer renderer in dilation.Union(constrict))
            {
                var shapes = new[]
                {
                    dilation.Contains(renderer) ? FTExtrasPupils.DilationShape : null,
                    constrict.Contains(renderer) ? FTExtrasPupils.ConstrictShape : null,
                }.Where(s => s != null);
                EditorGUILayout.LabelField($"<b>{renderer.name}</b>  <color=#909090>{string.Join(", ", shapes)}</color>", PawlygonEditorUI.RichMiniLabelStyle);
            }

            if (constrict.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"No mesh has '{FTExtrasPupils.ConstrictShape}', so the post-blink tightening will only use '{FTExtrasPupils.DilationShape}'.",
                    MessageType.Info);
            }
        }

        // =====================================================================
        // Preview
        // =====================================================================

        private void DrawPupilPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Preview", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Plays the pupil animation on the avatar. Changes to the settings below apply while it plays.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                if (pupilMeshCount == 0)
                {
                    EditorGUILayout.LabelField("Nothing to preview without a pupil blendshape.", PawlygonEditorUI.SubLabelStyle);
                    return;
                }

                bool active = pupilPreview != null && pupilPreview.IsActive;
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(pupilIdlePlaying ? "Stop Idle" : "Play Idle", GUILayout.Height(26f)))
                    {
                        if (pupilIdlePlaying) StopPupilPreview();
                        else StartPupilIdle();
                        GUIUtility.ExitGUI();
                    }

                    if (GUILayout.Button("Blink", GUILayout.Height(26f)))
                    {
                        TriggerPupilBlink();
                        GUIUtility.ExitGUI();
                    }

                    using (new EditorGUI.DisabledScope(!active))
                    {
                        if (GUILayout.Button("Stop", GUILayout.Height(26f), GUILayout.Width(70f)))
                        {
                            StopPupilPreview();
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                EditorGUILayout.Space(6f);
                string phase = !active ? "Stopped" : pupilReflexStartTime >= 0 ? "Blink reflex" : "Idle drift";
                EditorGUILayout.LabelField($"<color=#909090>{phase}</color>", PawlygonEditorUI.RichMiniLabelStyle);
                DrawWeightBar(FTExtrasPupils.DilationShape, active ? pupilDilationValue : 0f);
                DrawWeightBar(FTExtrasPupils.ConstrictShape, active ? pupilConstrictValue : 0f);
            }
        }

        private static void DrawWeightBar(string label, float value)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.ProgressBar(rect, Mathf.Clamp01(value / 100f), $"{label}  {value:0}");
        }

        private void StartPupilIdle()
        {
            StopMode();
            EnsurePupilPreview();
            pupilPreview.Begin();
            pupilIdlePlaying = true;
            pupilIdleStartTime = EditorApplication.timeSinceStartup;
            pupilReflexStartTime = -1;
        }

        /// <summary>
        /// Plays the reflex once. If the idle drift is playing, it resumes afterwards, like the animator's
        /// Blink Reflex → Idle transition; otherwise the preview stops.
        /// </summary>
        private void TriggerPupilBlink()
        {
            StopMode();
            EnsurePupilPreview();
            pupilPreview.Begin();
            pupilReflexStartTime = EditorApplication.timeSinceStartup;
        }

        private void EnsurePupilPreview()
        {
            if (pupilPreview == null) pupilPreview = new FTExtrasPupilPreview(selectedAvatar);
        }

        private void StopPupilPreview()
        {
            pupilIdlePlaying = false;
            pupilReflexStartTime = -1;
            pupilPreview?.Restore();
        }

        private void UpdatePupilPreview()
        {
            if (pupilPreview == null || !pupilPreview.IsActive)
            {
                // Restored from outside (play mode, script reload): drop the playing state.
                if (pupilIdlePlaying || pupilReflexStartTime >= 0)
                {
                    pupilIdlePlaying = false;
                    pupilReflexStartTime = -1;
                    Repaint();
                }
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - lastPupilApplyTime < PupilFrameInterval) return;
            lastPupilApplyTime = now;

            FTExtrasGenerationSettings settings = profile.generation;
            if (pupilReflexStartTime >= 0)
            {
                float reflexTime = (float)(now - pupilReflexStartTime);
                if (reflexTime < FTExtrasPupils.ReflexDuration(settings))
                {
                    var (dilation, constrict) = FTExtrasPupils.ReflexCurves(settings);
                    ApplyPupilWeights(dilation.Evaluate(reflexTime), constrict.Evaluate(reflexTime));
                    return;
                }

                pupilReflexStartTime = -1;
                if (!pupilIdlePlaying)
                {
                    StopPupilPreview();
                    Repaint();
                    return;
                }

                // The animator restarts the idle clip when the reflex hands back to it.
                pupilIdleStartTime = now;
            }

            float idleTime = (float)(now - pupilIdleStartTime) % FTExtrasPupils.IdleLength;
            ApplyPupilWeights(FTExtrasPupils.IdleDilation(settings, idleTime), 0f);
        }

        private void ApplyPupilWeights(float dilation, float constrict)
        {
            pupilDilationValue = dilation;
            pupilConstrictValue = constrict;
            pupilPreview.Apply(dilation, constrict);
            Repaint();
        }

        // =====================================================================
        // Advanced settings
        // =====================================================================

        private void DrawPupilAdvancedSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                showPupilAdvanced = EditorGUILayout.Foldout(showPupilAdvanced, "Advanced Settings", true);
                if (!showPupilAdvanced) return;

                profileObject.Update();
                SerializedProperty generation = profileObject.FindProperty(nameof(FTExtrasProfile.generation));
                SerializedProperty Field(string name) => generation.FindPropertyRelative(name);

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Idle Drift", EditorStyles.boldLabel);
                SerializedProperty idleMin = Field(nameof(FTExtrasGenerationSettings.idleDilationMin));
                SerializedProperty idleMax = Field(nameof(FTExtrasGenerationSettings.idleDilationMax));
                float min = idleMin.floatValue, max = idleMax.floatValue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.MinMaxSlider(new GUIContent("Dilation Range", "EyeDilation drifts between these values while idle."), ref min, ref max, 0f, 100f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        idleMin.floatValue = Mathf.Round(min);
                        idleMax.floatValue = Mathf.Round(max);
                    }
                    EditorGUILayout.LabelField($"{idleMin.floatValue:0} – {idleMax.floatValue:0}", GUILayout.Width(60f));
                }

                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Blink Reflex", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(Field(nameof(FTExtrasGenerationSettings.reflexPeakDilation)), new GUIContent("Peak Dilation", "EyeDilation right after the eyes reopen."));
                EditorGUILayout.PropertyField(Field(nameof(FTExtrasGenerationSettings.reflexConstrict)), new GUIContent("Tightening", "EyeConstrict at the tightest point of the reflex."));
                SerializedProperty duration = Field(nameof(FTExtrasGenerationSettings.reflexDuration));
                duration.floatValue = EditorGUILayout.Slider(new GUIContent("Duration (s)", "Seconds from reopening until the pupils settle back into the idle drift."), duration.floatValue, 0.3f, 4f);

                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Blink Detection", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "VRCFT eyelids are about 0 when closed and 0.75 when normally open. The gap between the two thresholds stops noisy tracking from triggering false reflexes.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.PropertyField(Field(nameof(FTExtrasGenerationSettings.lidClosedBelow)), new GUIContent("Closed Below", "Both eyelids below this count as a blink."));
                EditorGUILayout.PropertyField(Field(nameof(FTExtrasGenerationSettings.lidOpenAbove)), new GUIContent("Reopened Above", "Either eyelid above this, after a blink, starts the reflex."));

                SerializedProperty closed = Field(nameof(FTExtrasGenerationSettings.lidClosedBelow));
                SerializedProperty open = Field(nameof(FTExtrasGenerationSettings.lidOpenAbove));
                if (open.floatValue <= closed.floatValue)
                {
                    EditorGUILayout.HelpBox("Reopened Above should be higher than Closed Below.", MessageType.Warning);
                }

                profileObject.ApplyModifiedProperties();

                EditorGUILayout.Space(6f);
                if (GUILayout.Button("Reset Pupil Settings to Defaults"))
                {
                    ResetPupilSettings();
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void ResetPupilSettings()
        {
            var defaults = new FTExtrasGenerationSettings();
            FTExtrasGenerationSettings settings = profile.generation;

            Undo.RecordObject(profile, "Reset Pupil Settings");
            settings.idleDilationMin = defaults.idleDilationMin;
            settings.idleDilationMax = defaults.idleDilationMax;
            settings.reflexPeakDilation = defaults.reflexPeakDilation;
            settings.reflexConstrict = defaults.reflexConstrict;
            settings.reflexDuration = defaults.reflexDuration;
            settings.lidClosedBelow = defaults.lidClosedBelow;
            settings.lidOpenAbove = defaults.lidOpenAbove;
            EditorUtility.SetDirty(profile);
        }
    }
}
