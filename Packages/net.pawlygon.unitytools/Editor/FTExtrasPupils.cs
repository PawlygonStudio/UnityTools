using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Fake pupil dilation shared by the generator and the window's preview: which meshes have the
    /// Unified Expressions pupil blendshapes, the idle drift, and the blink reflex curves. The generator
    /// bakes exactly these curves into clips, so the preview matches what the animator plays.
    /// </summary>
    internal static class FTExtrasPupils
    {
        internal const string DilationShape = "EyeDilation";
        internal const string ConstrictShape = "EyeConstrict";

        /// <summary>The idle drift repeats every 12 s (waves of 3, 4 and 6 s line up only then).</summary>
        internal const float IdleLength = 12f;

        /// <summary>
        /// Skinned meshes on the avatar that have the EyeDilation / EyeConstrict blendshapes.
        /// </summary>
        internal static (List<SkinnedMeshRenderer> Dilation, List<SkinnedMeshRenderer> Constrict) FindMeshes(GameObject avatar)
        {
            var dilation = new List<SkinnedMeshRenderer>();
            var constrict = new List<SkinnedMeshRenderer>();
            if (avatar == null) return (dilation, constrict);

            foreach (SkinnedMeshRenderer renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                if (mesh.GetBlendShapeIndex(DilationShape) >= 0) dilation.Add(renderer);
                if (mesh.GetBlendShapeIndex(ConstrictShape) >= 0) constrict.Add(renderer);
            }
            return (dilation, constrict);
        }

        /// <summary>
        /// EyeDilation during the idle drift: three waves whose periods only line up every 12 s, so it never
        /// reads as a regular pulse.
        /// </summary>
        internal static float IdleDilation(FTExtrasGenerationSettings settings, float time)
        {
            float wave = 0.5f * Mathf.Sin(2f * Mathf.PI * time / 3f)
                + 0.3f * Mathf.Sin(2f * Mathf.PI * time / 4f + 1.3f)
                + 0.2f * Mathf.Sin(2f * Mathf.PI * time / 6f + 2.1f);
            float max = Mathf.Max(settings.idleDilationMin, settings.idleDilationMax);
            return Mathf.Lerp(settings.idleDilationMin, max, Mathf.Clamp01(0.5f + 0.5f * wave));
        }

        internal static float ReflexDuration(FTExtrasGenerationSettings settings) => Mathf.Max(0.3f, settings.reflexDuration);

        /// <summary>
        /// The blink reflex: eyes reopen wide (they were in the dark), tighten quickly, then relax and end on
        /// the idle drift's first value so the hand-off is seamless.
        /// </summary>
        internal static (AnimationCurve Dilation, AnimationCurve Constrict) ReflexCurves(FTExtrasGenerationSettings settings)
        {
            float d = ReflexDuration(settings);
            float max = Mathf.Max(settings.idleDilationMin, settings.idleDilationMax);
            float relaxed = Mathf.Lerp(settings.idleDilationMin, max, 0.8f);
            float[] times = { 0f, 0.18f * d, 0.4f * d, 0.7f * d, d };

            AnimationCurve dilation = SmoothCurve(times, new[] { settings.reflexPeakDilation, 0f, 0f, relaxed, IdleDilation(settings, 0f) });
            AnimationCurve constrict = SmoothCurve(times, new[] { 0f, settings.reflexConstrict, settings.reflexConstrict * 0.35f, 0f, 0f });
            return (dilation, constrict);
        }

        internal static AnimationCurve SmoothCurve(float[] times, float[] values)
        {
            var curve = new AnimationCurve(times.Select((t, i) => new Keyframe(t, values[i])).ToArray());
            for (int k = 0; k < curve.length; k++) curve.SmoothTangents(k, 0f);
            return curve;
        }
    }

    /// <summary>
    /// Applies pupil blendshape weights to the avatar for previewing and puts the original weights back:
    /// on <see cref="Restore"/>, before play mode, before a script reload, and around scene saves.
    /// </summary>
    internal class FTExtrasPupilPreview
    {
        private readonly List<(SkinnedMeshRenderer Renderer, int Index)> dilation = new List<(SkinnedMeshRenderer, int)>();
        private readonly List<(SkinnedMeshRenderer Renderer, int Index)> constrict = new List<(SkinnedMeshRenderer, int)>();
        private Dictionary<(SkinnedMeshRenderer, int), float> snapshot;
        private Dictionary<(SkinnedMeshRenderer, int), float> suspended;

        internal static FTExtrasPupilPreview Active { get; private set; }

        public bool IsActive => snapshot != null;
        public bool HasMeshes => dilation.Count > 0;

        public FTExtrasPupilPreview(GameObject avatar)
        {
            var (dilationMeshes, constrictMeshes) = FTExtrasPupils.FindMeshes(avatar);
            dilation.AddRange(dilationMeshes.Select(r => (r, r.sharedMesh.GetBlendShapeIndex(FTExtrasPupils.DilationShape))));
            constrict.AddRange(constrictMeshes.Select(r => (r, r.sharedMesh.GetBlendShapeIndex(FTExtrasPupils.ConstrictShape))));
        }

        private IEnumerable<(SkinnedMeshRenderer Renderer, int Index)> All => dilation.Concat(constrict);

        public void Begin()
        {
            if (IsActive) return;
            if (Active != null && Active != this) Active.Restore();

            snapshot = All.Where(e => e.Renderer != null).Distinct().ToDictionary(e => e, e => e.Renderer.GetBlendShapeWeight(e.Index));
            Active = this;
        }

        public void Apply(float dilationWeight, float constrictWeight)
        {
            if (!IsActive) return;
            foreach (var (renderer, index) in dilation) if (renderer != null) renderer.SetBlendShapeWeight(index, dilationWeight);
            foreach (var (renderer, index) in constrict) if (renderer != null) renderer.SetBlendShapeWeight(index, constrictWeight);
            SceneView.RepaintAll();
        }

        public void Restore()
        {
            if (!IsActive) return;
            Write(snapshot);
            snapshot = null;
            suspended = null;
            if (Active == this) Active = null;
            SceneView.RepaintAll();
        }

        internal void SuspendForSave()
        {
            if (!IsActive || suspended != null) return;
            suspended = snapshot.Keys.Where(e => e.Item1 != null).ToDictionary(e => e, e => e.Item1.GetBlendShapeWeight(e.Item2));
            Write(snapshot);
        }

        internal void ResumeAfterSave()
        {
            if (suspended == null) return;
            Write(suspended);
            suspended = null;
        }

        private static void Write(Dictionary<(SkinnedMeshRenderer, int), float> weights)
        {
            foreach (var pair in weights)
            {
                if (pair.Key.Item1 != null) pair.Key.Item1.SetBlendShapeWeight(pair.Key.Item2, pair.Value);
            }
        }
    }

    /// <summary>
    /// Restores previewed pupil weights before play mode and script reloads, and keeps them out of saved scenes.
    /// </summary>
    [InitializeOnLoad]
    internal static class FTExtrasPupilPreviewSafety
    {
        static FTExtrasPupilPreviewSafety()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => FTExtrasPupilPreview.Active?.Restore();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) FTExtrasPupilPreview.Active?.Restore();
            };
            EditorSceneManager.sceneSaving += (scene, path) => FTExtrasPupilPreview.Active?.SuspendForSave();
            EditorSceneManager.sceneSaved += scene => FTExtrasPupilPreview.Active?.ResumeAfterSave();
        }
    }
}
