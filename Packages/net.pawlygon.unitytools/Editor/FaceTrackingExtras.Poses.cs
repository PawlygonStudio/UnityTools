using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Ears &amp; Tail tab: authoring poses on the avatar and previewing how they blend, including the loops.
    /// </summary>
    public partial class FaceTrackingExtras
    {
        // =====================================================================
        // Ears & Tail tab
        // =====================================================================

        private void DrawEarsAndTailTab()
        {
            DrawPoseSection();
            EditorGUILayout.Space(SectionSpacing);
            DrawPreviewSection();
        }

        // =====================================================================
        // Drawing: Poses
        // =====================================================================

        private void DrawPoseSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Poses", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Click Edit, rotate the bones in the Scene view with the Rotate tool (E), then Save. Mirrored poses are filled in automatically.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                if (session == null)
                {
                    EditorGUILayout.HelpBox(sessionError ?? "Save the bone chains first.", sessionError != null ? MessageType.Error : MessageType.Info);
                    return;
                }

                DrawPoseGroup("Ears", FTExtrasPoses.PoseGroup.Ears);
                EditorGUILayout.Space(8f);
                DrawPoseGroup("Tail", FTExtrasPoses.PoseGroup.Tail);
            }
        }

        private void DrawPoseGroup(string title, FTExtrasPoses.PoseGroup group)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            bool hasBones = group == FTExtrasPoses.PoseGroup.Tail
                ? profile.tail.Count > 0
                : profile.earLeft.Count + profile.earRight.Count > 0;
            if (!hasBones)
            {
                EditorGUILayout.LabelField($"No {title.ToLowerInvariant()} bones in the profile.", PawlygonEditorUI.SubLabelStyle);
                return;
            }

            foreach (var definition in FTExtrasPoses.All.Where(d => d.Group == group))
            {
                DrawPoseRow(definition);
            }
        }

        private void DrawPoseRow(FTExtrasPoses.PoseDefinition definition)
        {
            bool isSet = FTExtrasPoses.Resolve(profile, definition.Id) != null;
            bool isActive = activePose == definition.Id && (mode == Mode.Editing || mode == Mode.Showing);
            bool isEditing = isActive && mode == Mode.Editing;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(definition.Label, GUILayout.Width(110f));

                string status;
                if (definition.IsDerived)
                {
                    string source = FTExtrasPoses.Get(definition.DerivedFrom.Value).Label;
                    status = isSet ? $"<color=#909090>Mirrored from {source}</color>" : $"<color=#909090>Needs {source}</color>";
                }
                else
                {
                    status = isSet ? "<color=#6BCB77>✓ Set</color>" : "<color=#909090>Not set</color>";
                }
                EditorGUILayout.LabelField(status, poseLabelStyle, GUILayout.MinWidth(90f));

                if (isEditing)
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Save", 20f))
                    {
                        SaveActivePose();
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button("Cancel", GUILayout.Width(70f)))
                    {
                        StopMode();
                        GUIUtility.ExitGUI();
                    }
                }
                else
                {
                    using (new EditorGUI.DisabledScope(mode == Mode.Editing))
                    {
                        if (!definition.IsDerived && GUILayout.Button("Edit", GUILayout.Width(60f)))
                        {
                            StartEditing(definition);
                            GUIUtility.ExitGUI();
                        }

                        using (new EditorGUI.DisabledScope(!isSet))
                        {
                            bool showing = isActive && mode == Mode.Showing;
                            if (GUILayout.Button(showing ? "Hide" : "Show", GUILayout.Width(60f)))
                            {
                                if (showing) StopMode();
                                else ShowPose(definition);
                                GUIUtility.ExitGUI();
                            }

                            if (!definition.IsDerived && GUILayout.Button("Clear", GUILayout.Width(60f)))
                            {
                                ClearPose(definition);
                                GUIUtility.ExitGUI();
                            }
                        }

                        if (definition.IsDerived)
                        {
                            GUILayout.Space(64f);
                        }
                    }
                }
            }

            if (isEditing)
            {
                DrawEditingPanel(definition);
            }
        }

        private void DrawEditingPanel(FTExtrasPoses.PoseDefinition definition)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(definition.Hint, PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(4f);

                bool isEar = definition.Group == FTExtrasPoses.PoseGroup.Ears;
                if (isEar)
                {
                    EditorGUI.BeginChangeCheck();
                    liveMirror = EditorGUILayout.ToggleLeft("Mirror ears while editing", liveMirror);
                    if (EditorGUI.EndChangeCheck())
                    {
                        session.ResetMirrorTracking();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (isEar)
                    {
                        using (new EditorGUI.DisabledScope(session.EarLeft.Length == 0))
                        {
                            if (GUILayout.Button("Select Left Ear")) SelectBone(session.EarLeft[0]);
                        }
                        using (new EditorGUI.DisabledScope(session.EarRight.Length == 0))
                        {
                            if (GUILayout.Button("Select Right Ear")) SelectBone(session.EarRight[0]);
                        }
                    }
                    else if (GUILayout.Button("Select Tail"))
                    {
                        SelectBone(session.Tail[0]);
                    }

                    if (GUILayout.Button("Reset to Rest"))
                    {
                        session.Apply(FTExtrasPoses.RestPose(profile));
                        session.ResetMirrorTracking();
                    }
                }
            }
        }

        // =====================================================================
        // Drawing: Preview
        // =====================================================================

        private void DrawPreviewSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Preview", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Drive the poses the way face tracking will, to check how they combine.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                if (session == null) return;

                bool previewing = mode == Mode.Previewing;
                using (new EditorGUI.DisabledScope(mode == Mode.Editing))
                {
                    bool newPreviewing = EditorGUILayout.ToggleLeft("Preview on avatar", previewing);
                    if (newPreviewing != previewing)
                    {
                        if (newPreviewing) StartPreview();
                        else StopMode();
                        GUIUtility.ExitGUI();
                    }
                }

                using (new EditorGUI.DisabledScope(!previewing))
                {
                    EditorGUI.BeginChangeCheck();
                    previewInputs.GazeX = EditorGUILayout.Slider("Eyes Left ↔ Right", previewInputs.GazeX, -1f, 1f);
                    previewInputs.GazeY = EditorGUILayout.Slider("Eyes Down ↔ Up", previewInputs.GazeY, -1f, 1f);
                    previewInputs.Mood = EditorGUILayout.Slider("Sad ↔ Happy", previewInputs.Mood, -1f, 1f);
                    previewInputs.JawX = EditorGUILayout.Slider("Jaw Left ↔ Right", previewInputs.JawX, -1f, 1f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        ApplyPreview();
                    }

                    if (GUILayout.Button("Reset Sliders"))
                    {
                        bool playLoops = previewInputs.PlayLoops;
                        previewInputs = default;
                        previewInputs.PlayLoops = playLoops;
                        ApplyPreview();
                    }

                    EditorGUILayout.Space(8f);
                    EditorGUI.BeginChangeCheck();
                    bool playLoopsToggle = EditorGUILayout.ToggleLeft("Play loops (happy ear flick and tail wag)", previewInputs.PlayLoops);
                    if (EditorGUI.EndChangeCheck())
                    {
                        previewInputs.PlayLoops = playLoopsToggle;
                        previewInputs.Time = 0f;
                        loopStartTime = EditorApplication.timeSinceStartup;
                        ApplyPreview();
                    }

                    if (previewInputs.PlayLoops && previewInputs.Mood <= 0f)
                    {
                        EditorGUILayout.HelpBox("Move Sad \u2194 Happy above 0 to see the loops.", MessageType.None);
                    }
                }

                EditorGUILayout.Space(8f);
                DrawLoopSettings();
            }
        }

        private void DrawLoopSettings()
        {
            EditorGUILayout.LabelField("Loop Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            float flickPeriod = EditorGUILayout.Slider(new GUIContent("Ear Flick Speed (s)", "Seconds for one Happy \u2192 Happy Flick \u2192 Happy cycle."), profile.earFlickPeriod, 0.2f, 2f);
            float wagPeriod = EditorGUILayout.Slider(new GUIContent("Tail Wag Speed (s)", "Seconds for one full wag, right \u2192 left \u2192 right."), profile.tailWagPeriod, 0.2f, 2f);
            float wagAmount = EditorGUILayout.Slider(new GUIContent("Tail Wag Amount", "How far the wag swings, as a fraction of the Tail Right / Tail Left poses."), profile.tailWagAmount, 0f, 1f);
            float wagDelay = EditorGUILayout.Slider(new GUIContent("Tail Wag Delay", "How much each bone down the tail lags the one before it, as a fraction of a wag. Higher values make the wag ripple more."), profile.tailWagDelay, 0f, 0.5f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(profile, "Change Face Tracking Extras Loop Settings");
                profile.earFlickPeriod = flickPeriod;
                profile.tailWagPeriod = wagPeriod;
                profile.tailWagAmount = wagAmount;
                profile.tailWagDelay = wagDelay;
                EditorUtility.SetDirty(profile);
                ApplyPreview();
            }
        }
    }
}
