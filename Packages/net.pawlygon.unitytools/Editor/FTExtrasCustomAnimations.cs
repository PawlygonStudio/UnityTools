using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    // =========================================================================
    // Data model (stored in FTExtrasProfile.customAnimations)
    // =========================================================================

    /// <summary>How a custom animation is driven.</summary>
    public enum FTExtrasCustomMode
    {
        /// <summary>Follows a parameter's value continuously.</summary>
        Follow,
        /// <summary>Plays when conditions on parameters are met (optionally held for a time).</summary>
        Trigger,
    }

    /// <summary>How a Follow animation maps the parameter value to the clip.</summary>
    public enum FTExtrasFollowStyle
    {
        /// <summary>Off at <c>fromValue</c>, fully on at <c>toValue</c>.</summary>
        FadeIn,
        /// <summary>The value picks the playback position (from = start, to = end).</summary>
        Scrub,
        /// <summary>Negative clip at <c>fromValue</c>, nothing in the middle, clip at <c>toValue</c>.</summary>
        TwoSided,
    }

    /// <summary>What a triggered animation does once its conditions are met.</summary>
    public enum FTExtrasTriggerPlay
    {
        /// <summary>Plays to the end once; the conditions must go false before it can play again.</summary>
        PlayOnce,
        /// <summary>Loops while the conditions stay true.</summary>
        LoopWhileActive,
        /// <summary>Plays to the end and holds the last frame while the conditions stay true.</summary>
        HoldWhileActive,
    }

    public enum FTExtrasConditionCombine { All, Any }

    public enum FTExtrasComparison { Above, Below }

    /// <summary>Which face tracking must be active for the animation to run.</summary>
    public enum FTExtrasTrackingGate
    {
        /// <summary>Eye tracking for eye parameters, lip tracking for the rest (the same split VRCFT uses).</summary>
        Auto,
        EyeTracking,
        LipTracking,
        /// <summary>Always runs, even with face tracking off.</summary>
        Always,
    }

    /// <summary>One "parameter above/below value" check.</summary>
    [Serializable]
    public class FTExtrasCondition
    {
        public string parameter;
        public FTExtrasComparison comparison = FTExtrasComparison.Above;
        public float threshold = 0.75f;

        /// <summary>Whether the condition holds for <paramref name="value"/>.</summary>
        public bool IsMet(float value) => comparison == FTExtrasComparison.Above ? value > threshold : value < threshold;

        /// <summary>
        /// Whether the condition is clearly over, using <paramref name="margin"/> so noisy tracking hovering
        /// around the threshold doesn't flicker the animation (e.g. triggers above 0.75, releases below 0.65).
        /// </summary>
        public bool IsReleased(float value, float margin) =>
            comparison == FTExtrasComparison.Above ? value < threshold - margin : value > threshold + margin;
    }

    /// <summary>
    /// An existing animation clip driven by face tracking parameters: either following a parameter's value,
    /// or played when conditions are met.
    /// </summary>
    [Serializable]
    public class FTExtrasCustomAnimation
    {
        public string name = "Custom Animation";
        public bool enabled = true;
        public FTExtrasCustomMode mode = FTExtrasCustomMode.Trigger;
        public AnimationClip clip;

        // --- Follow ---
        public string followParameter;
        public FTExtrasFollowStyle followStyle = FTExtrasFollowStyle.FadeIn;
        public float fromValue;
        public float toValue = 1f;
        [Tooltip("Two-sided only: the clip for the 'from' side.")]
        public AnimationClip negativeClip;

        // --- Trigger ---
        public FTExtrasConditionCombine combine = FTExtrasConditionCombine.All;
        public List<FTExtrasCondition> conditions = new List<FTExtrasCondition>();
        [Tooltip("Seconds the conditions must stay true before the animation plays.")]
        public float holdSeconds;
        [Tooltip("How far past the threshold a value must go back before the animation releases.")]
        public float releaseMargin = 0.1f;
        public FTExtrasTriggerPlay play = FTExtrasTriggerPlay.PlayOnce;
        public float fadeIn = 0.1f;
        public float fadeOut = 0.25f;
        [Tooltip("Seconds after it ends before it can play again.")]
        public float cooldown;

        // --- Options ---
        public FTExtrasTrackingGate gate = FTExtrasTrackingGate.Auto;
        [Tooltip("Adds a synced on/off toggle for this animation to the menu.")]
        public bool menuToggle;
        public bool toggleDefaultOn = true;

        /// <summary>Every parameter this animation reads.</summary>
        public IEnumerable<string> UsedParameters =>
            mode == FTExtrasCustomMode.Follow
                ? new[] { followParameter }.Where(p => !string.IsNullOrEmpty(p))
                : conditions.Select(c => c.parameter).Where(p => !string.IsNullOrEmpty(p)).Distinct();

        /// <summary>
        /// Whether the conditions start the animation (All: every one met; Any: at least one).
        /// </summary>
        public bool AreConditionsMet(Func<string, float> valueOf)
        {
            if (conditions.Count == 0) return false;
            return combine == FTExtrasConditionCombine.All
                ? conditions.All(c => c.IsMet(valueOf(c.parameter)))
                : conditions.Any(c => c.IsMet(valueOf(c.parameter)));
        }

        /// <summary>
        /// Whether the conditions are clearly over (All: any one released; Any: every one released).
        /// </summary>
        public bool AreConditionsReleased(Func<string, float> valueOf)
        {
            if (conditions.Count == 0) return true;
            float margin = Mathf.Max(0f, releaseMargin);
            return combine == FTExtrasConditionCombine.All
                ? conditions.Any(c => c.IsReleased(valueOf(c.parameter), margin))
                : conditions.All(c => c.IsReleased(valueOf(c.parameter), margin));
        }
    }

    // =========================================================================
    // Parameter catalog
    // =========================================================================

    /// <summary>
    /// The face tracking parameters an avatar's VRCFT setup provides, with friendly names, value ranges and the
    /// tracking flag that gates them. Read from the animator controllers referenced on the avatar (descriptor,
    /// Modular Avatar and VRCFury components), so only parameters every player receives are offered.
    /// </summary>
    internal static class FTExtrasParameterCatalog
    {
        internal class Entry
        {
            /// <summary>Full animator parameter name, e.g. OSCm/Proxy/FT/v2/BrowExpressionLeft.</summary>
            public string Name;
            /// <summary>Unified Expressions v2 name, e.g. BrowExpressionLeft.</summary>
            public string ShortName;
            public string Label;
            public string RangeHint;
            public float Min;
            public float Max;
            public bool IsEye;
        }

        private static readonly Regex FaceTrackingParam = new Regex(@"^(OSCm/Proxy/)?FT/v2/([A-Za-z]+)$");

        // Friendly labels and ranges for the Unified Expressions v2 parameters (min, max, hint).
        private static readonly Dictionary<string, (string Label, float Min, float Max, string Hint)> Known =
            new Dictionary<string, (string, float, float, string)>
            {
                { "BrowExpressionLeft", ("Brow Up/Down Left", -1f, 1f, "-1 down, +1 up") },
                { "BrowExpressionRight", ("Brow Up/Down Right", -1f, 1f, "-1 down, +1 up") },
                { "CheekPuffSuckLeft", ("Cheek Puff/Suck Left", -1f, 1f, "-1 suck, +1 puff") },
                { "CheekPuffSuckRight", ("Cheek Puff/Suck Right", -1f, 1f, "-1 suck, +1 puff") },
                { "CheekSquintLeft", ("Cheek Squint Left", 0f, 1f, null) },
                { "CheekSquintRight", ("Cheek Squint Right", 0f, 1f, null) },
                { "EyeLeftX", ("Left Eye Look Left/Right", -1f, 1f, "-1 left, +1 right") },
                { "EyeRightX", ("Right Eye Look Left/Right", -1f, 1f, "-1 left, +1 right") },
                { "EyeY", ("Eyes Look Down/Up", -1f, 1f, "-1 down, +1 up") },
                { "EyeLidLeft", ("Eyelid Left", 0f, 1f, "0 closed, 0.75 open, 1 wide") },
                { "EyeLidRight", ("Eyelid Right", 0f, 1f, "0 closed, 0.75 open, 1 wide") },
                { "EyeSquintLeft", ("Eye Squint Left", 0f, 1f, null) },
                { "EyeSquintRight", ("Eye Squint Right", 0f, 1f, null) },
                { "JawForward", ("Jaw Forward", 0f, 1f, null) },
                { "JawOpen", ("Jaw Open", 0f, 1f, null) },
                { "JawX", ("Jaw Left/Right", -1f, 1f, "-1 left, +1 right") },
                { "MouthX", ("Mouth Left/Right", -1f, 1f, "-1 left, +1 right") },
                { "MouthTightenerStretchLeft", ("Mouth Tighten/Stretch Left", -1f, 1f, "-1 tighten, +1 stretch") },
                { "MouthTightenerStretchRight", ("Mouth Tighten/Stretch Right", -1f, 1f, "-1 tighten, +1 stretch") },
                { "SmileFrownLeft", ("Smile/Frown Left", -1f, 1f, "-1 frown, +1 smile") },
                { "SmileFrownRight", ("Smile/Frown Right", -1f, 1f, "-1 frown, +1 smile") },
                { "TongueX", ("Tongue Left/Right", -1f, 1f, "-1 left, +1 right") },
                { "TongueY", ("Tongue Down/Up", -1f, 1f, "-1 down, +1 up") },
                { "PupilDilation", ("Pupil Dilation", 0f, 1f, "0 constricted, 1 dilated") },
            };

        private static readonly Dictionary<int, List<Entry>> cache = new Dictionary<int, List<Entry>>();

        internal static void Invalidate() => cache.Clear();

        /// <summary>
        /// The face tracking parameters found on <paramref name="avatar"/>, preferring the smoothed
        /// <c>OSCm/Proxy</c> version of each. Cached until <see cref="Invalidate"/>.
        /// </summary>
        internal static List<Entry> Discover(GameObject avatar)
        {
            if (avatar == null) return new List<Entry>();
            if (cache.TryGetValue(avatar.GetInstanceID(), out List<Entry> cached)) return cached;

            var names = new HashSet<string>();
            foreach (AnimatorController controller in FindReferencedControllers(avatar))
            {
                foreach (AnimatorControllerParameter parameter in controller.parameters)
                {
                    if (FaceTrackingParam.IsMatch(parameter.name)) names.Add(parameter.name);
                }
            }

            var entries = names
                .GroupBy(n => FaceTrackingParam.Match(n).Groups[2].Value)
                .Select(g => Describe(g.FirstOrDefault(n => n.StartsWith("OSCm/Proxy/")) ?? g.First()))
                .OrderBy(e => e.Label)
                .ToList();

            cache[avatar.GetInstanceID()] = entries;
            return entries;
        }

        /// <summary>
        /// Describes any parameter name; unknown names get a generic label and a 0–1 range.
        /// </summary>
        internal static Entry Describe(string name)
        {
            Match match = FaceTrackingParam.Match(name ?? string.Empty);
            string shortName = match.Success ? match.Groups[2].Value : name ?? string.Empty;

            var entry = new Entry { Name = name, ShortName = shortName, Min = 0f, Max = 1f, IsEye = IsEyeParameter(shortName) };
            if (Known.TryGetValue(shortName, out var known))
            {
                entry.Label = known.Label;
                entry.Min = known.Min;
                entry.Max = known.Max;
                entry.RangeHint = known.Hint;
            }
            else
            {
                entry.Label = Regex.Replace(shortName, "(?<=[a-z])(?=[A-Z])", " ");
            }
            return entry;
        }

        /// <summary>
        /// VRCFT gates eye parameters (and pupil dilation) with EyeTrackingActive and everything else with
        /// LipTrackingActive.
        /// </summary>
        internal static bool IsEyeParameter(string shortOrFullName)
        {
            string shortName = FaceTrackingParam.Match(shortOrFullName ?? string.Empty) is var m && m.Success
                ? m.Groups[2].Value
                : shortOrFullName ?? string.Empty;
            return shortName.StartsWith("Eye", StringComparison.Ordinal) || shortName == "PupilDilation";
        }

        /// <summary>
        /// Animator controllers referenced by the avatar's descriptor and by Modular Avatar / VRCFury components,
        /// excluding Face Tracking Extras' own generated controller (it declares the same parameter names).
        /// </summary>
        private static IEnumerable<AnimatorController> FindReferencedControllers(GameObject avatar)
        {
            var found = new HashSet<AnimatorController>();
            foreach (MonoBehaviour component in avatar.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;
                if (!(typeName.Contains("Descriptor") || typeName.Contains("MergeAnimator") || typeName.Contains("VRCFury"))) continue;

                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (property.objectReferenceValue is AnimatorController controller) found.Add(controller);
                    else if (property.objectReferenceValue is AnimatorOverrideController overrideController
                             && overrideController.runtimeAnimatorController is AnimatorController baseController)
                    {
                        found.Add(baseController);
                    }
                }
            }

            return found.Where(c => c.name != FTExtrasGenerator.ControllerAssetName);
        }
    }

    // =========================================================================
    // Trigger simulation (preview)
    // =========================================================================

    /// <summary>
    /// Runs a triggered custom animation the way the generated state machine does (Waiting → Armed → Playing →
    /// Done / Cooldown), so the preview shows the same timing: hold time, release margin, play mode, fades and
    /// cooldown.
    /// </summary>
    internal class FTExtrasTriggerSimulator
    {
        internal enum State { Waiting, Armed, Playing, Done, Cooldown }

        public State Current { get; private set; } = State.Waiting;
        public float StateTime { get; private set; }

        /// <summary>Playback position in the clip, in seconds.</summary>
        public float ClipTime { get; private set; }

        /// <summary>How much of the clip is visible (fades in and out).</summary>
        public float Weight { get; private set; }

        public void Reset()
        {
            Current = State.Waiting;
            StateTime = 0f;
            ClipTime = 0f;
            Weight = 0f;
        }

        public void Update(FTExtrasCustomAnimation animation, Func<string, float> valueOf, float deltaTime)
        {
            float length = animation.clip != null ? Mathf.Max(0.0001f, animation.clip.length) : 1f;
            StateTime += deltaTime;

            switch (Current)
            {
                case State.Waiting:
                    if (animation.AreConditionsMet(valueOf))
                    {
                        Enter(animation.holdSeconds > 0f ? State.Armed : State.Playing);
                    }
                    break;

                case State.Armed:
                    if (!animation.AreConditionsMet(valueOf)) Enter(State.Waiting);
                    else if (StateTime >= animation.holdSeconds) Enter(State.Playing);
                    break;

                case State.Playing:
                    ClipTime += deltaTime;
                    switch (animation.play)
                    {
                        case FTExtrasTriggerPlay.PlayOnce:
                            if (ClipTime >= length) { ClipTime = length; Enter(State.Done); }
                            break;
                        case FTExtrasTriggerPlay.LoopWhileActive:
                            ClipTime %= length;
                            if (animation.AreConditionsReleased(valueOf)) End(animation);
                            break;
                        case FTExtrasTriggerPlay.HoldWhileActive:
                            ClipTime = Mathf.Min(ClipTime, length);
                            if (animation.AreConditionsReleased(valueOf)) End(animation);
                            break;
                    }
                    break;

                case State.Done:
                    if (animation.AreConditionsReleased(valueOf)) End(animation);
                    break;

                case State.Cooldown:
                    if (StateTime >= animation.cooldown) Enter(State.Waiting);
                    break;
            }

            // Fades: towards 1 while playing, towards 0 otherwise.
            bool visible = Current == State.Playing;
            float fade = visible ? animation.fadeIn : animation.fadeOut;
            Weight = fade <= 0f
                ? (visible ? 1f : 0f)
                : Mathf.MoveTowards(Weight, visible ? 1f : 0f, deltaTime / fade);
        }

        private void End(FTExtrasCustomAnimation animation)
        {
            Enter(animation.cooldown > 0f ? State.Cooldown : State.Waiting);
        }

        private void Enter(State state)
        {
            if (state == State.Playing) ClipTime = 0f;
            Current = state;
            StateTime = 0f;
        }
    }
}
