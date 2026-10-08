using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Previews user animation clips on the avatar through Unity's Animation Mode, which records every property
    /// it touches and restores them all when the preview stops. Clips can be shown partly faded in (a weight
    /// between 0 and 1) and two clips can be mixed, as Follow/Trigger animations do in the generated animator.
    ///
    /// Partial weights are applied by sampling a temporary clip whose float curves hold
    /// <c>lerp(value without the clip, value at time t, weight)</c>; object-reference curves (material swaps)
    /// switch at 50%.
    /// </summary>
    internal class FTExtrasClipPreview
    {
        internal static FTExtrasClipPreview Active { get; private set; }

        private readonly GameObject avatar;
        private readonly Dictionary<EditorCurveBinding, float> restValues = new Dictionary<EditorCurveBinding, float>();
        private AnimationClip mixClip;

        public bool IsActive => Active == this && AnimationMode.InAnimationMode();

        public FTExtrasClipPreview(GameObject avatar)
        {
            this.avatar = avatar;
        }

        public void Begin()
        {
            if (IsActive) return;
            Active?.Stop();

            restValues.Clear();
            if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
            Active = this;
        }

        /// <summary>
        /// Shows <paramref name="clip"/> at <paramref name="time"/> with <paramref name="weight"/>, plus an optional
        /// second clip (e.g. the other side of a two-sided animation) with its own time and weight.
        /// </summary>
        public void Sample(AnimationClip clip, float time, float weight, AnimationClip secondClip = null, float secondTime = 0f, float secondWeight = 0f)
        {
            if (!IsActive || avatar == null) return;

            CaptureRestValues(clip);
            CaptureRestValues(secondClip);

            if (mixClip == null) mixClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            mixClip.ClearCurves();
            var setBindings = new HashSet<EditorCurveBinding>();

            AnimationMode.BeginSampling();
            try
            {
                // Object-reference curves can't be blended: show them when the clip is at least half visible.
                if (clip != null && weight >= 0.5f) SampleObjectCurves(clip, time);
                if (secondClip != null && secondWeight >= 0.5f) SampleObjectCurves(secondClip, secondTime);

                AddWeightedFloats(clip, time, weight, setBindings);
                AddWeightedFloats(secondClip, secondTime, secondWeight, setBindings);

                // Properties of the clips that are fully off still need their rest value written.
                foreach (var pair in restValues)
                {
                    if (setBindings.Contains(pair.Key)) continue;
                    AnimationUtility.SetEditorCurve(mixClip, pair.Key, AnimationCurve.Constant(0f, 0f, pair.Value));
                }

                AnimationMode.SampleAnimationClip(avatar, mixClip, 0f);
            }
            finally
            {
                AnimationMode.EndSampling();
            }

            SceneView.RepaintAll();
        }

        public void Stop()
        {
            if (Active == this)
            {
                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                Active = null;
            }

            restValues.Clear();
            if (mixClip != null)
            {
                Object.DestroyImmediate(mixClip);
                mixClip = null;
            }
            SceneView.RepaintAll();
        }

        private void CaptureRestValues(AnimationClip clip)
        {
            if (clip == null) return;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (restValues.ContainsKey(binding)) continue;

                // Read before this binding is first sampled, so the value is the one without the clip.
                if (AnimationUtility.GetFloatValue(avatar, binding, out float value)) restValues[binding] = value;
            }
        }

        private void AddWeightedFloats(AnimationClip clip, float time, float weight, HashSet<EditorCurveBinding> setBindings)
        {
            if (clip == null || weight <= 0f) return;

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || !restValues.TryGetValue(binding, out float rest)) continue;

                float value = Mathf.Lerp(rest, curve.Evaluate(time), Mathf.Clamp01(weight));
                AnimationUtility.SetEditorCurve(mixClip, binding, AnimationCurve.Constant(0f, 0f, value));
                setBindings.Add(binding);
            }
        }

        private void SampleObjectCurves(AnimationClip clip, float time)
        {
            if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Length == 0) return;
            AnimationMode.SampleAnimationClip(avatar, clip, time);
        }
    }

    /// <summary>
    /// Stops the custom animation preview before play mode, script reloads and scene saves, so previewed values
    /// never end up saved or stuck.
    /// </summary>
    [InitializeOnLoad]
    internal static class FTExtrasClipPreviewSafety
    {
        static FTExtrasClipPreviewSafety()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => FTExtrasClipPreview.Active?.Stop();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) FTExtrasClipPreview.Active?.Stop();
            };
            EditorSceneManager.sceneSaving += (scene, path) => FTExtrasClipPreview.Active?.Stop();
        }
    }
}
