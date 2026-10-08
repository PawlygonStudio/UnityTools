using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Baselines: save a finished profile as the starting point for other avatars, and copy one into a new or
    /// existing profile (settings, custom animations and poses).
    /// </summary>
    public partial class FaceTrackingExtras
    {
        private FTExtrasBaseline baseline;
        private bool baselineLookedUp;
        private FTExtrasBaselineParts baselineParts = FTExtrasBaselineParts.All;

        private FTExtrasBaseline CurrentBaseline
        {
            get
            {
                if (!baselineLookedUp)
                {
                    baseline = FTExtrasBaseline.FindDefault();
                    baselineLookedUp = true;
                }
                return baseline;
            }
        }

        /// <summary>
        /// Shown with Create Profile: the baseline the new profile starts from.
        /// </summary>
        private void DrawNewProfileBaseline()
        {
            DrawBaselineField(
                new GUIContent("Start From Baseline", "Copies its settings, custom animations and poses into the new profile. Leave empty to start blank."));

            if (CurrentBaseline != null)
            {
                EditorGUILayout.LabelField(" ", CurrentBaseline.Summary, EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>
        /// Setup's Baseline section, once the profile exists: apply a baseline to it or save it as one.
        /// </summary>
        private void DrawBaselineSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Baseline", sectionTitleStyle);
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Reuse a finished avatar as the starting point for others. New profiles start from the baseline picked here; " +
                    "applying it again replaces only the parts you tick.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                DrawBaselineField(new GUIContent("Baseline"));
                if (CurrentBaseline != null)
                {
                    EditorGUILayout.LabelField(" ", CurrentBaseline.Summary, EditorStyles.wordWrappedMiniLabel);
                }

                EditorGUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Apply");
                    baselineParts = PartToggle(baselineParts, FTExtrasBaselineParts.Settings, "Settings", 76f);
                    baselineParts = PartToggle(baselineParts, FTExtrasBaselineParts.CustomAnimations, "Custom animations", 128f);
                    baselineParts = PartToggle(baselineParts, FTExtrasBaselineParts.Poses, "Poses", 60f);
                }

                EditorGUILayout.Space(6f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (PawlygonEditorUI.DrawSecondaryButton("Save as Baseline…", 24f, GUILayout.Width(150f)))
                    {
                        SaveAsBaseline();
                        GUIUtility.ExitGUI();
                    }

                    using (new EditorGUI.DisabledScope(CurrentBaseline == null || baselineParts == FTExtrasBaselineParts.None))
                    {
                        if (PawlygonEditorUI.DrawSecondaryButton("Apply to This Avatar", 24f, GUILayout.Width(150f)))
                        {
                            ApplyBaselineToProfile();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        private void DrawBaselineField(GUIContent label)
        {
            EditorGUI.BeginChangeCheck();
            var picked = (FTExtrasBaseline)EditorGUILayout.ObjectField(label, CurrentBaseline, typeof(FTExtrasBaseline), false);
            if (EditorGUI.EndChangeCheck())
            {
                baseline = picked;
                if (picked != null) FTExtrasBaseline.Remember(picked);
                else EditorPrefs.DeleteKey(FTExtrasBaseline.LastBaselinePrefKey);
            }
        }

        private static FTExtrasBaselineParts PartToggle(FTExtrasBaselineParts parts, FTExtrasBaselineParts part, string label, float width)
        {
            bool on = EditorGUILayout.ToggleLeft(label, (parts & part) != 0, GUILayout.Width(width));
            return on ? parts | part : parts & ~part;
        }

        /// <summary>
        /// Called when Create Profile has just saved a new profile's chains. Returns the status message, or
        /// null when no baseline is picked.
        /// </summary>
        private string ApplyBaselineToNewProfile()
        {
            if (CurrentBaseline == null) return null;

            FTExtrasBaseline.ApplyResult result = CurrentBaseline.ApplyTo(profile, FTExtrasBaselineParts.All);
            SaveProfile();
            FTExtrasBaseline.Remember(CurrentBaseline);
            return $"Created '{profile.name}'. {result.Describe(CurrentBaseline.name)}{MissingBindingsNote()}";
        }

        private void ApplyBaselineToProfile()
        {
            FTExtrasBaseline source = CurrentBaseline;
            if (source == null || profile == null) return;

            var replaced = new[]
            {
                (baselineParts & FTExtrasBaselineParts.Settings) != 0 ? "settings and timings" : null,
                (baselineParts & FTExtrasBaselineParts.CustomAnimations) != 0 ? $"all {profile.customAnimations.Count} custom animations" : null,
                (baselineParts & FTExtrasBaselineParts.Poses) != 0 ? "poses the baseline has" : null,
            }.Where(p => p != null);

            if (!EditorUtility.DisplayDialog(
                    "Apply Baseline",
                    $"Replace the {string.Join(", ", replaced)} in '{profile.name}' with the ones from '{source.name}'?\n\nCtrl+Z undoes it.",
                    "Apply", "Cancel"))
            {
                return;
            }

            StopMode();
            StopPupilPreview();
            StopCustomPreview();
            selectedCustomIndex = -1;

            Undo.RecordObject(profile, "Apply Face Tracking Extras Baseline");
            FTExtrasBaseline.ApplyResult result = source.ApplyTo(profile, baselineParts);
            SaveProfile();
            FTExtrasBaseline.Remember(source);
            profileObject?.Update();
            BindSession();

            SetStatus(result.Describe(source.name) + MissingBindingsNote(),
                result.SkippedPoses.Count > 0 ? MessageType.Warning : MessageType.Info);
        }

        private void SaveAsBaseline()
        {
            if (profile == null) return;

            string folder = CurrentBaseline != null
                ? PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(AssetDatabase.GetAssetPath(CurrentBaseline)))
                : "Assets";
            string path = EditorUtility.SaveFilePanelInProject(
                "Save Face Tracking Extras Baseline",
                $"{FTExtrasGenerator.SafeName(profile.avatarName ?? profile.name)} Baseline",
                "asset",
                "Save this avatar's settings, custom animations and poses as a starting point for other avatars.",
                folder);
            if (string.IsNullOrEmpty(path)) return;

            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            FTExtrasBaseline target;
            if (existing is FTExtrasBaseline existingBaseline)
            {
                // Overwrite in place so the asset keeps its GUID.
                Undo.RecordObject(existingBaseline, "Save Face Tracking Extras Baseline");
                existingBaseline.CaptureFrom(profile);
                EditorUtility.SetDirty(existingBaseline);
                AssetDatabase.SaveAssetIfDirty(existingBaseline);
                target = existingBaseline;
            }
            else if (existing != null)
            {
                SetStatus($"'{path}' is not a Face Tracking Extras baseline; pick another name.", MessageType.Error);
                return;
            }
            else
            {
                target = CreateInstance<FTExtrasBaseline>();
                target.CaptureFrom(profile);
                AssetDatabase.CreateAsset(target, path);
            }

            baseline = target;
            baselineLookedUp = true;
            FTExtrasBaseline.Remember(target);
            SetStatus($"Saved '{profile.name}' as the baseline '{target.name}': {target.Summary}.", MessageType.Info, "Ping", PawlygonStatus.Ping(target));
        }

        /// <summary>
        /// Custom animation clips made for another avatar can animate objects this one doesn't have.
        /// </summary>
        private string MissingBindingsNote()
        {
            int affected = profile.customAnimations.Count(a => a != null
                && (FTExtrasBaseline.CountMissingBindings(a.clip, selectedAvatar) > 0
                    || FTExtrasBaseline.CountMissingBindings(a.negativeClip, selectedAvatar) > 0));
            return affected == 0 ? string.Empty
                : $" {affected} custom animation{(affected == 1 ? " animates" : "s animate")} objects this avatar doesn't have; check the Custom tab.";
        }
    }
}
