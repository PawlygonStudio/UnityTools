using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Custom tab: the user's own animation clips driven by face tracking parameters, either following a
    /// parameter's value or played when conditions are met, with a live preview on the avatar.
    /// </summary>
    public partial class FaceTrackingExtras
    {
        private const double CustomPreviewFrameInterval = 1.0 / 60.0;
        private const string OtherParameterLabel = "Other (type a name)…";

        private static readonly string[] FollowStyleLabels = { "Fade in", "Scrub (value picks the position)", "Two-sided" };
        private static readonly string[] PlayModeLabels = { "Play once", "Loop while active", "Hold while active" };
        private static readonly string[] CombineLabels = { "All of these are true", "Any of these is true" };
        private static readonly string[] ComparisonLabels = { "above", "below" };
        private static readonly string[] GateLabels = { "Face tracking is on (automatic)", "Eye tracking is on", "Lip tracking is on", "Always" };
        private static readonly string[] ModeLabels = { "Follow a parameter", "Play when…" };

        private int selectedCustomIndex = -1;

        // --- Preview ---
        private FTExtrasClipPreview clipPreview;
        private bool customPreviewPlaying;
        private int customPreviewIndex = -1;
        private double customPreviewLastTime;
        private double customPreviewStartTime;
        private readonly Dictionary<string, float> customPreviewValues = new Dictionary<string, float>();
        private readonly FTExtrasTriggerSimulator customSimulator = new FTExtrasTriggerSimulator();

        // =====================================================================
        // Custom tab
        // =====================================================================

        private string CustomTabLabel()
        {
            int count = profile?.customAnimations?.Count(a => a != null && a.enabled) ?? 0;
            return count > 0 ? $"Custom ({count})" : "Custom";
        }

        private bool AreCustomAnimationsValid()
        {
            return profile?.customAnimations != null
                && profile.customAnimations.Any(a => a != null && a.enabled)
                && profile.customAnimations.Where(a => a != null && a.enabled).All(a => DescribeCustomProblem(a) == null);
        }

        private void DrawCustomTab()
        {
            if (profile.customAnimations == null) profile.customAnimations = new List<FTExtrasCustomAnimation>();
            List<FTExtrasParameterCatalog.Entry> catalog = FTExtrasParameterCatalog.Discover(selectedAvatar);

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Custom Animations",
                    "Use your own animation clips with face tracking: follow a parameter's value (e.g. blush with cheek puff), " +
                    "or play when conditions are met (e.g. eye shine when both brows stay up for a second).");

                if (catalog.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No VRCFT face tracking parameters were found on this avatar. Add the VRCFT setup first (Avatar Setup Wizard, Prefabs step); " +
                        "you can still type parameter names by hand.",
                        MessageType.Warning);
                }

                if (profile.customAnimations.Count == 0)
                {
                    EditorGUILayout.LabelField("No custom animations yet. Add one below.", PawlygonEditorUI.SubLabelStyle);
                }
            }

            for (int i = 0; i < profile.customAnimations.Count; i++)
            {
                EditorGUILayout.Space(6f);
                DrawCustomAnimationCard(i, catalog);
            }
        }

        private void DrawCustomActions()
        {
            PawlygonEditorUI.BeginActionBar();
            if (PawlygonEditorUI.DrawSecondaryButton("+ Follow a Parameter"))
            {
                AddCustomAnimation(FTExtrasCustomMode.Follow);
                GUIUtility.ExitGUI();
            }
            if (PawlygonEditorUI.DrawSecondaryButton("+ Play When…"))
            {
                AddCustomAnimation(FTExtrasCustomMode.Trigger);
                GUIUtility.ExitGUI();
            }
            GUILayout.FlexibleSpace();
            PawlygonEditorUI.EndActionBar();
        }

        private void AddCustomAnimation(FTExtrasCustomMode mode)
        {
            StopCustomPreview();
            Undo.RecordObject(profile, "Add Custom Animation");

            var animation = new FTExtrasCustomAnimation
            {
                name = UniqueCustomName(mode == FTExtrasCustomMode.Follow ? "Follow Animation" : "Triggered Animation"),
                mode = mode,
            };

            // Start from a sensible parameter so the new animation is close to usable.
            List<FTExtrasParameterCatalog.Entry> catalog = FTExtrasParameterCatalog.Discover(selectedAvatar);
            FTExtrasParameterCatalog.Entry first = catalog.FirstOrDefault(e => e.ShortName == "BrowExpressionLeft") ?? catalog.FirstOrDefault();
            if (mode == FTExtrasCustomMode.Follow)
            {
                if (first != null)
                {
                    animation.followParameter = first.Name;
                    animation.fromValue = Mathf.Max(0f, first.Min);
                    animation.toValue = first.Max;
                }
            }
            else
            {
                animation.conditions.Add(new FTExtrasCondition { parameter = first?.Name, threshold = 0.75f });
            }

            profile.customAnimations.Add(animation);
            selectedCustomIndex = profile.customAnimations.Count - 1;
            EditorUtility.SetDirty(profile);
        }

        private string UniqueCustomName(string baseName)
        {
            string name = baseName;
            for (int i = 2; profile.customAnimations.Any(a => a != null && a.name == name); i++) name = $"{baseName} {i}";
            return name;
        }

        // =====================================================================
        // Card
        // =====================================================================

        private void DrawCustomAnimationCard(int index, List<FTExtrasParameterCatalog.Entry> catalog)
        {
            FTExtrasCustomAnimation animation = profile.customAnimations[index];
            if (animation == null) return;
            bool selected = selectedCustomIndex == index;
            string problem = DescribeCustomProblem(animation);

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    bool enabled = EditorGUILayout.Toggle(animation.enabled, GUILayout.Width(18f));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(profile, "Toggle Custom Animation");
                        animation.enabled = enabled;
                        EditorUtility.SetDirty(profile);
                    }

                    GUILayout.Label(animation.name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                    if (problem != null) PawlygonEditorUI.DrawBadge("Needs attention", PawlygonEditorUI.BadgeKind.Warning, problem);
                    else if (!animation.enabled) PawlygonEditorUI.DrawBadge("Off", PawlygonEditorUI.BadgeKind.Neutral);
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button(selected ? "Close" : "Edit", GUILayout.Width(56f)))
                    {
                        if (selected) StopCustomPreview();
                        selectedCustomIndex = selected ? -1 : index;
                        GUIUtility.ExitGUI();
                    }

                    if (GUILayout.Button(new GUIContent("✕", "Remove this custom animation"), GUILayout.Width(24f))
                        && EditorUtility.DisplayDialog("Remove Custom Animation", $"Remove '{animation.name}'? Your animation clip is not deleted.", "Remove", "Cancel"))
                    {
                        StopCustomPreview();
                        Undo.RecordObject(profile, "Remove Custom Animation");
                        profile.customAnimations.RemoveAt(index);
                        if (selectedCustomIndex >= index) selectedCustomIndex--;
                        EditorUtility.SetDirty(profile);
                        status.Info($"Removed '{animation.name}'. Ctrl+Z to undo.");
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUILayout.LabelField(DescribeCustomAnimation(animation), PawlygonEditorUI.SubLabelStyle);

                if (selected)
                {
                    EditorGUILayout.Space(6f);
                    PawlygonEditorUI.DrawSeparator();
                    EditorGUILayout.Space(6f);
                    DrawCustomAnimationEditor(index, animation, catalog);
                }
            }
        }

        // =====================================================================
        // Editor
        // =====================================================================

        private void DrawCustomAnimationEditor(int index, FTExtrasCustomAnimation animation, List<FTExtrasParameterCatalog.Entry> catalog)
        {
            // Record before any event that can change values, so every edit is one undo step.
            EventType type = Event.current.type;
            if (type != EventType.Layout && type != EventType.Repaint) Undo.RecordObject(profile, "Edit Custom Animation");

            EditorGUI.BeginChangeCheck();

            animation.name = EditorGUILayout.TextField("Name", animation.name);
            animation.clip = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent("Animation", "Your animation clip. It is used as-is; when a play mode needs a different loop setting, a copy is made."),
                animation.clip, typeof(AnimationClip), false);
            if (animation.clip != null)
            {
                EditorGUILayout.LabelField(" ", $"{animation.clip.length:0.##} s{(animation.clip.isLooping ? ", loops" : string.Empty)}", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4f);
            int modeIndex = GUILayout.Toolbar((int)animation.mode, ModeLabels);
            if (modeIndex != (int)animation.mode)
            {
                StopCustomPreview();
                animation.mode = (FTExtrasCustomMode)modeIndex;
                if (animation.mode == FTExtrasCustomMode.Trigger && animation.conditions.Count == 0)
                {
                    animation.conditions.Add(new FTExtrasCondition { parameter = animation.followParameter, threshold = 0.75f });
                }
            }
            EditorGUILayout.Space(4f);

            if (animation.mode == FTExtrasCustomMode.Follow) DrawFollowSettings(animation, catalog);
            else DrawTriggerSettings(animation, catalog);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            animation.gate = (FTExtrasTrackingGate)EditorGUILayout.Popup(
                new GUIContent("Active when", "Automatic follows VRCFT: eye parameters need eye tracking, the others lip tracking."),
                (int)animation.gate, GateLabels);
            using (new EditorGUILayout.HorizontalScope())
            {
                animation.menuToggle = EditorGUILayout.ToggleLeft(
                    new GUIContent("Menu toggle", "Adds a synced on/off toggle (1 bit) in the Custom Animations submenu."),
                    animation.menuToggle, GUILayout.Width(110f));
                using (new EditorGUI.DisabledScope(!animation.menuToggle))
                {
                    animation.toggleDefaultOn = EditorGUILayout.ToggleLeft("On by default", animation.toggleDefaultOn);
                }
            }

            if (EditorGUI.EndChangeCheck()) EditorUtility.SetDirty(profile);

            string problem = DescribeCustomProblem(animation);
            if (problem != null)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox($"This animation is skipped when generating: {problem}", MessageType.Warning);
            }

            EditorGUILayout.Space(8f);
            DrawCustomPreview(index, animation);
        }

        private void DrawFollowSettings(FTExtrasCustomAnimation animation, List<FTExtrasParameterCatalog.Entry> catalog)
        {
            animation.followParameter = DrawParameterField("Parameter", animation.followParameter, catalog);
            FTExtrasParameterCatalog.Entry info = FTExtrasParameterCatalog.Describe(animation.followParameter);

            animation.followStyle = (FTExtrasFollowStyle)EditorGUILayout.Popup("Style", (int)animation.followStyle, FollowStyleLabels);
            string styleHint = animation.followStyle == FTExtrasFollowStyle.FadeIn ? "Off at 'From', fully on at 'To'."
                : animation.followStyle == FTExtrasFollowStyle.Scrub ? "'From' shows the start of the clip and 'To' the end."
                : "'From' plays the From-side clip, the middle shows nothing, 'To' plays the animation.";
            EditorGUILayout.LabelField(" ", styleHint, EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                animation.fromValue = EditorGUILayout.FloatField("From", animation.fromValue);
                if (GUILayout.Button(new GUIContent("Full Range", $"Use {info.Min} to {info.Max}"), EditorStyles.miniButton, GUILayout.Width(72f)))
                {
                    animation.fromValue = animation.followStyle == FTExtrasFollowStyle.TwoSided ? info.Min : Mathf.Max(0f, info.Min);
                    animation.toValue = info.Max;
                    GUI.changed = true;
                }
            }
            animation.toValue = EditorGUILayout.FloatField("To", animation.toValue);
            if (!string.IsNullOrEmpty(info.RangeHint))
            {
                EditorGUILayout.LabelField(" ", $"{info.Label}: {info.RangeHint}", EditorStyles.miniLabel);
            }

            if (animation.followStyle == FTExtrasFollowStyle.TwoSided)
            {
                animation.negativeClip = (AnimationClip)EditorGUILayout.ObjectField(
                    new GUIContent("From-side Animation", "Plays towards 'From'. Leave empty to only animate the 'To' side."),
                    animation.negativeClip, typeof(AnimationClip), false);
            }
        }

        private void DrawTriggerSettings(FTExtrasCustomAnimation animation, List<FTExtrasParameterCatalog.Entry> catalog)
        {
            animation.combine = (FTExtrasConditionCombine)EditorGUILayout.Popup("When", (int)animation.combine, CombineLabels);

            for (int i = 0; i < animation.conditions.Count; i++)
            {
                FTExtrasCondition condition = animation.conditions[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUIUtility.labelWidth * 0.25f);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        condition.parameter = DrawParameterField(null, condition.parameter, catalog);
                        FTExtrasParameterCatalog.Entry info = FTExtrasParameterCatalog.Describe(condition.parameter);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            condition.comparison = (FTExtrasComparison)EditorGUILayout.Popup((int)condition.comparison, ComparisonLabels, GUILayout.Width(64f));
                            condition.threshold = EditorGUILayout.Slider(condition.threshold, info.Min, info.Max);
                        }
                    }

                    using (new EditorGUI.DisabledScope(animation.conditions.Count <= 1))
                    {
                        if (GUILayout.Button(new GUIContent("✕", "Remove this condition"), GUILayout.Width(24f)))
                        {
                            animation.conditions.RemoveAt(i);
                            GUI.changed = true;
                            break;
                        }
                    }
                }
                EditorGUILayout.Space(2f);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth * 0.25f);
                if (GUILayout.Button("+ Add Condition", EditorStyles.miniButton, GUILayout.Width(110f)))
                {
                    FTExtrasCondition last = animation.conditions.LastOrDefault();
                    animation.conditions.Add(new FTExtrasCondition
                    {
                        parameter = last != null ? MatchingSide(last.parameter, catalog) : null,
                        comparison = last?.comparison ?? FTExtrasComparison.Above,
                        threshold = last?.threshold ?? 0.75f,
                    });
                    GUI.changed = true;
                }
            }

            EditorGUILayout.Space(4f);
            animation.holdSeconds = Mathf.Max(0f, EditorGUILayout.FloatField(
                new GUIContent("Held for (s)", "How long the conditions must stay true before it plays. 0 plays immediately."), animation.holdSeconds));

            animation.play = (FTExtrasTriggerPlay)EditorGUILayout.Popup("Then", (int)animation.play, PlayModeLabels);
            string playHint = animation.play == FTExtrasTriggerPlay.PlayOnce ? "Plays to the end once; the conditions must clear before it can play again."
                : animation.play == FTExtrasTriggerPlay.LoopWhileActive ? "Loops while the conditions stay true, then fades out."
                : "Plays to the end and holds the last frame while the conditions stay true.";
            EditorGUILayout.LabelField(" ", playHint, EditorStyles.wordWrappedMiniLabel);

            animation.releaseMargin = EditorGUILayout.Slider(
                new GUIContent("Release margin", "How far back past the threshold a value must go to release, so noisy tracking doesn't flicker it."),
                animation.releaseMargin, 0f, 0.5f);

            using (new EditorGUILayout.HorizontalScope())
            {
                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 80f;
                animation.fadeIn = Mathf.Max(0f, EditorGUILayout.FloatField("Fade in (s)", animation.fadeIn));
                animation.fadeOut = Mathf.Max(0f, EditorGUILayout.FloatField("Fade out (s)", animation.fadeOut));
                EditorGUIUtility.labelWidth = labelWidth;
            }
            animation.cooldown = Mathf.Max(0f, EditorGUILayout.FloatField(
                new GUIContent("Cooldown (s)", "Seconds after it ends before it can play again."), animation.cooldown));
        }

        /// <summary>
        /// A dropdown of the avatar's face tracking parameters (friendly names), with "Other" for typed names.
        /// Returns the chosen full parameter name.
        /// </summary>
        private string DrawParameterField(string label, string current, List<FTExtrasParameterCatalog.Entry> catalog)
        {
            var options = catalog.Select(e => e.Label).ToList();
            options.Add(OtherParameterLabel);

            int index = catalog.FindIndex(e => e.Name == current);
            bool isOther = index < 0 && !string.IsNullOrEmpty(current);
            int shown = index >= 0 ? index : isOther || catalog.Count == 0 ? options.Count - 1 : -1;

            int picked = label != null
                ? EditorGUILayout.Popup(label, shown, options.ToArray())
                : EditorGUILayout.Popup(shown, options.ToArray());

            string result = current;
            if (picked != shown)
            {
                // "Other" starts from the VRCFT prefix so the typed name stays in Other mode until edited.
                result = picked >= 0 && picked < catalog.Count ? catalog[picked].Name
                    : isOther ? current : "FT/v2/";
            }

            bool showText = picked == options.Count - 1 || (picked < 0 && isOther);
            if (showText)
            {
                result = EditorGUILayout.TextField(label != null ? " " : string.Empty, result ?? string.Empty);
                if (!string.IsNullOrEmpty(result) && catalog.All(e => e.Name != result))
                {
                    EditorGUILayout.HelpBox("This parameter isn't part of the avatar's face tracking setup, so other players may not see the animation.", MessageType.Info);
                }
            }

            return result;
        }

        /// <summary>
        /// For "+ Add Condition": the other side of a left/right parameter (Brow Left → Brow Right), since the
        /// most common second condition is the same on the other side.
        /// </summary>
        private static string MatchingSide(string parameter, List<FTExtrasParameterCatalog.Entry> catalog)
        {
            if (string.IsNullOrEmpty(parameter)) return parameter;
            string other = parameter.EndsWith("Left") ? parameter.Substring(0, parameter.Length - 4) + "Right"
                : parameter.EndsWith("Right") ? parameter.Substring(0, parameter.Length - 5) + "Left"
                : null;
            return other != null && catalog.Any(e => e.Name == other) ? other : parameter;
        }

        // =====================================================================
        // Summaries and validation
        // =====================================================================

        private static string ParameterLabel(string parameter) =>
            string.IsNullOrEmpty(parameter) ? "(no parameter)" : FTExtrasParameterCatalog.Describe(parameter).Label;

        private static string DescribeCustomAnimation(FTExtrasCustomAnimation animation)
        {
            string clip = animation.clip != null ? animation.clip.name : "(no animation)";
            if (animation.mode == FTExtrasCustomMode.Follow)
            {
                string style = FollowStyleLabels[(int)animation.followStyle].Split(' ')[0].ToLowerInvariant();
                return $"{clip} follows {ParameterLabel(animation.followParameter)} ({style}, {animation.fromValue:0.##} → {animation.toValue:0.##})";
            }

            string joiner = animation.combine == FTExtrasConditionCombine.All ? " and " : " or ";
            string conditions = animation.conditions.Count == 0
                ? "(no conditions)"
                : string.Join(joiner, animation.conditions.Select(c =>
                    $"{ParameterLabel(c.parameter)} {(c.comparison == FTExtrasComparison.Above ? ">" : "<")} {c.threshold:0.##}"));
            string hold = animation.holdSeconds > 0f ? $" for {animation.holdSeconds:0.##} s" : string.Empty;
            return $"When {conditions}{hold} → {PlayModeLabels[(int)animation.play].ToLowerInvariant()} {clip}";
        }

        private static string DescribeCustomProblem(FTExtrasCustomAnimation animation)
        {
            if (animation.clip == null) return "no animation clip is set.";
            if (animation.mode == FTExtrasCustomMode.Follow)
            {
                if (string.IsNullOrWhiteSpace(animation.followParameter)) return "no parameter is set.";
                if (Mathf.Approximately(animation.fromValue, animation.toValue)) return "'From' and 'To' are the same value.";
            }
            else
            {
                if (animation.conditions.Count == 0) return "it has no conditions.";
                if (animation.conditions.Any(c => string.IsNullOrWhiteSpace(c.parameter))) return "a condition has no parameter.";
            }
            return null;
        }

        // =====================================================================
        // Preview
        // =====================================================================

        private void DrawCustomPreview(int index, FTExtrasCustomAnimation animation)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            if (animation.clip == null)
            {
                EditorGUILayout.LabelField("Set an animation to preview it.", PawlygonEditorUI.SubLabelStyle);
                return;
            }

            bool playing = customPreviewPlaying && customPreviewIndex == index;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (PawlygonEditorUI.DrawSecondaryButton(playing ? "Stop Preview" : "Preview on Avatar", 24f, GUILayout.Width(150f)))
                {
                    if (playing) StopCustomPreview();
                    else StartCustomPreview(index);
                    GUIUtility.ExitGUI();
                }

                if (playing && animation.mode == FTExtrasCustomMode.Trigger)
                {
                    GUILayout.Space(8f);
                    DrawSimulatorState(animation);
                }
                GUILayout.FlexibleSpace();
            }

            using (new EditorGUI.DisabledScope(!playing))
            {
                foreach (string parameter in animation.UsedParameters)
                {
                    FTExtrasParameterCatalog.Entry info = FTExtrasParameterCatalog.Describe(parameter);
                    customPreviewValues.TryGetValue(parameter, out float value);
                    customPreviewValues[parameter] = EditorGUILayout.Slider(info.Label, value, info.Min, info.Max);
                }
            }

            if (!playing)
            {
                EditorGUILayout.LabelField("Move the sliders while previewing to see how it reacts.", EditorStyles.miniLabel);
            }
        }

        private void DrawSimulatorState(FTExtrasCustomAnimation animation)
        {
            switch (customSimulator.Current)
            {
                case FTExtrasTriggerSimulator.State.Waiting:
                    PawlygonEditorUI.DrawBadge("Waiting", PawlygonEditorUI.BadgeKind.Neutral, "The conditions aren't met yet.");
                    break;
                case FTExtrasTriggerSimulator.State.Armed:
                    PawlygonEditorUI.DrawBadge($"Held {customSimulator.StateTime:0.0}/{animation.holdSeconds:0.0} s", PawlygonEditorUI.BadgeKind.Info, "The conditions are met; waiting for the hold time.");
                    break;
                case FTExtrasTriggerSimulator.State.Playing:
                    PawlygonEditorUI.DrawBadge("Playing", PawlygonEditorUI.BadgeKind.Ok);
                    break;
                case FTExtrasTriggerSimulator.State.Done:
                    PawlygonEditorUI.DrawBadge("Played, waiting for release", PawlygonEditorUI.BadgeKind.Neutral, "Move the values back past the release margin to play again.");
                    break;
                case FTExtrasTriggerSimulator.State.Cooldown:
                    PawlygonEditorUI.DrawBadge($"Cooldown {customSimulator.StateTime:0.0}/{animation.cooldown:0.0} s", PawlygonEditorUI.BadgeKind.Neutral);
                    break;
            }
        }

        private void StartCustomPreview(int index)
        {
            StopMode();
            StopPupilPreview();
            StopCustomPreview();

            FTExtrasCustomAnimation animation = profile.customAnimations[index];
            foreach (string parameter in animation.UsedParameters)
            {
                if (!customPreviewValues.ContainsKey(parameter)) customPreviewValues[parameter] = 0f;
            }

            clipPreview = new FTExtrasClipPreview(selectedAvatar);
            clipPreview.Begin();
            customSimulator.Reset();
            customPreviewIndex = index;
            customPreviewPlaying = true;
            customPreviewStartTime = customPreviewLastTime = EditorApplication.timeSinceStartup;
        }

        private void StopCustomPreview()
        {
            customPreviewPlaying = false;
            customPreviewIndex = -1;
            customSimulator.Reset();
            clipPreview?.Stop();
            clipPreview = null;
        }

        private void UpdateCustomPreview()
        {
            if (!customPreviewPlaying) return;

            if (clipPreview == null || !clipPreview.IsActive || profile == null
                || customPreviewIndex < 0 || customPreviewIndex >= profile.customAnimations.Count)
            {
                // Stopped from outside (play mode, reload, scene save) or the animation was removed.
                StopCustomPreview();
                Repaint();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - customPreviewLastTime < CustomPreviewFrameInterval) return;
            float deltaTime = (float)(now - customPreviewLastTime);
            customPreviewLastTime = now;

            FTExtrasCustomAnimation animation = profile.customAnimations[customPreviewIndex];
            if (animation.clip == null) return;

            float length = Mathf.Max(0.0001f, animation.clip.length);
            float elapsed = (float)(now - customPreviewStartTime);
            float ClipTimeAt(AnimationClip clip) => clip == null ? 0f
                : clip.isLooping ? elapsed % Mathf.Max(0.0001f, clip.length) : Mathf.Min(elapsed, clip.length);
            float ValueOf(string parameter) => parameter != null && customPreviewValues.TryGetValue(parameter, out float v) ? v : 0f;

            if (animation.mode == FTExtrasCustomMode.Follow)
            {
                float value = ValueOf(animation.followParameter);
                switch (animation.followStyle)
                {
                    case FTExtrasFollowStyle.FadeIn:
                        clipPreview.Sample(animation.clip, ClipTimeAt(animation.clip), Mathf.InverseLerp(animation.fromValue, animation.toValue, value));
                        break;
                    case FTExtrasFollowStyle.Scrub:
                        clipPreview.Sample(animation.clip, Mathf.InverseLerp(animation.fromValue, animation.toValue, value) * length, 1f);
                        break;
                    case FTExtrasFollowStyle.TwoSided:
                        float middle = (animation.fromValue + animation.toValue) * 0.5f;
                        float toWeight = Mathf.InverseLerp(middle, animation.toValue, value);
                        float fromWeight = Mathf.InverseLerp(middle, animation.fromValue, value);
                        clipPreview.Sample(animation.clip, ClipTimeAt(animation.clip), toWeight,
                            animation.negativeClip, ClipTimeAt(animation.negativeClip), fromWeight);
                        break;
                }
            }
            else
            {
                customSimulator.Update(animation, ValueOf, deltaTime);
                float time = animation.play == FTExtrasTriggerPlay.LoopWhileActive ? customSimulator.ClipTime % length : Mathf.Min(customSimulator.ClipTime, length);
                clipPreview.Sample(animation.clip, time, customSimulator.Weight);
            }

            Repaint();
        }
    }
}
