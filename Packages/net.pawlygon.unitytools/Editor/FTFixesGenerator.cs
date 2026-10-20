using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Generates the Blendshape Fixes animator for Pawlygon VRCFT 1.x: one layer whose Direct blend tree drives every
    /// fix shape from the VRCFT parameters that drive the shapes it fixes, and a prefab with a VRCFury Full Controller.
    ///
    /// Each fix is a tree of 1D blend trees, one level per parameter its shapes read, sampled at the points where
    /// those shapes change (and in between when the combination isn't linear), so the fix follows the same values as
    /// the Pawlygon VRCFT 1.x animator gives the shapes. Fixes of eye shapes only run while eye tracking is on, fixes
    /// of mouth shapes while lip tracking is on.
    /// </summary>
    internal static class FTFixesGenerator
    {
        internal const string OutputFolderName = "BlendshapeFixes";
        internal const string PrefabName = "!Pawlygon - Blendshape Fixes";
        private const string ControllerName = "FX - Blendshape Fixes";
        private const string BuildControllerName = "_Building - FX - Blendshape Fixes";
        private const string AnimationsFolderName = "Animations";
        private const string LayerName = "Blendshape Fixes";
        private const string ParamOne = "Pawlygon/FTFixes/One";

        /// <summary>Extra samples per segment when the combined weight isn't linear between the points.</summary>
        private const int Subdivisions = 4;

        /// <summary>Clips made during the current generation, by name, so each is saved once.</summary>
        private static readonly Dictionary<string, AnimationClip> clipCache = new Dictionary<string, AnimationClip>();

        internal sealed class Result
        {
            public bool Success;
            public string Message;
            public string PrefabPath;
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>Prefabs/BlendshapeFixes next to the avatar's prefab, like Face Tracking Extras.</summary>
        internal static string GetOutputFolder(GameObject avatar)
        {
            string extras = FTExtrasGenerator.GetOutputFolder(avatar);
            return PawlygonEditorUtils.CombineAssetPath(PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(extras)), OutputFolderName);
        }

        internal static string GetPrefabPath(GameObject avatar) => PawlygonEditorUtils.CombineAssetPath(GetOutputFolder(avatar), PrefabName + ".prefab");

        internal static string GetProfilePath(GameObject avatar) =>
            PawlygonEditorUtils.CombineAssetPath(GetOutputFolder(avatar), FTExtrasGenerator.SafeName(avatar.name) + " Blendshape Fixes.asset");

        /// <summary>The avatar's saved profile, or null.</summary>
        internal static FTFixesProfile FindProfile(GameObject avatar)
        {
            if (avatar == null) return null;
            string folder = GetOutputFolder(avatar);
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            return AssetDatabase.FindAssets("t:" + nameof(FTFixesProfile), new[] { folder })
                .Select(guid => AssetDatabase.LoadAssetAtPath<FTFixesProfile>(AssetDatabase.GUIDToAssetPath(guid)))
                .FirstOrDefault(p => p != null);
        }

        internal static Result Generate(FTFixesProfile profile, GameObject avatar)
        {
            var result = new Result();
            var plan = FTFixesPlan.Resolve(profile, avatar);
            var fixes = plan.Generated.ToList();
            if (fixes.Count == 0)
            {
                result.Message = "Nothing was generated: the avatar has no fix shapes to drive.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(profile.eyeTrackingActive) || string.IsNullOrWhiteSpace(profile.lipTrackingActive))
            {
                result.Message = "Nothing was generated: the eye and lip tracking parameter names can't be empty.";
                return result;
            }

            string folder = GetOutputFolder(avatar);
            string clipFolder = PawlygonEditorUtils.CombineAssetPath(folder, AnimationsFolderName);
            PawlygonEditorUtils.EnsureFolderExists(clipFolder);

            // A new profile is saved with the animator, so the next generation starts from the same settings.
            if (!AssetDatabase.Contains(profile))
            {
                AssetDatabase.CreateAsset(profile, GetProfilePath(avatar));
            }

            var clips = new FTExtrasGenerator.ClipStore(clipFolder);
            clipCache.Clear();

            // Built into a temporary controller first, then moved into the real one, which keeps its GUID.
            string controllerPath = PawlygonEditorUtils.CombineAssetPath(folder, ControllerName + ".controller");
            string buildPath = PawlygonEditorUtils.CombineAssetPath(folder, BuildControllerName + ".controller");
            if (AssetDatabase.LoadMainAssetAtPath(buildPath) != null) AssetDatabase.DeleteAsset(buildPath);

            AnimatorController building = AnimatorController.CreateAnimatorControllerAtPath(buildPath);
            try
            {
                var builder = new FTExtrasGenerator.ControllerBuilder(building);
                builder.AddFloat(ParamOne, 1f);
                builder.AddFloat(profile.eyeTrackingActive, 0f);
                builder.AddFloat(profile.lipTrackingActive, 0f);
                foreach (var parameter in fixes.SelectMany(f => f.Sources).Select(s => profile.parameterPrefix + s.Parameter).Distinct())
                {
                    builder.AddFloat(parameter, 0f);
                }

                BlendTree root = builder.NewTree(LayerName, BlendTreeType.Direct, null);
                root.children = fixes.Select(fix => FixChild(builder, clips, profile, fix)).ToArray();
                builder.SingleStateLayer(LayerName, root, isFirstLayer: true);
            }
            catch
            {
                AssetDatabase.DeleteAsset(buildPath);
                throw;
            }

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(controllerPath) != null) AssetDatabase.DeleteAsset(controllerPath);
                string moveError = AssetDatabase.MoveAsset(buildPath, controllerPath);
                if (!string.IsNullOrEmpty(moveError)) throw new InvalidOperationException($"Could not save the controller: {moveError}");
                controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            }
            else
            {
                FTExtrasGenerator.ReplaceControllerContent(building, controller);
                AssetDatabase.DeleteAsset(buildPath);
            }
            EditorUtility.SetDirty(controller);
            clips.DeleteUnused();
            AssetDatabase.SaveAssets();

            string prefabPath = GetPrefabPath(avatar);
            string prefabError = CreatePrefab(prefabPath, controller);
            if (prefabError != null) result.Warnings.Add(prefabError);
            else result.PrefabPath = prefabPath;

            Undo.RecordObject(profile, "Generate Blendshape Fixes");
            profile.lastGeneratedSignature = FTFixesPlan.Signature(profile, plan);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            result.Success = true;
            result.Message = $"Generated {fixes.Count} {(fixes.Count == 1 ? "fix" : "fixes")} in {folder}.";
            return result;
        }

        /// <summary>
        /// The fix's tree, gated by the tracking its shapes need: Direct trees multiply their children's weights, so
        /// a fix of eye and mouth shapes is nested under both.
        /// </summary>
        private static ChildMotion FixChild(FTExtrasGenerator.ControllerBuilder builder, FTExtrasGenerator.ClipStore clips, FTFixesProfile profile, FTFixesPlan.Entry fix)
        {
            var gates = fix.Sources.Select(s => s.Tracking).Distinct().OrderBy(t => t)
                .Select(t => t == FTFixesSources.Tracking.Eye ? profile.eyeTrackingActive : profile.lipTrackingActive)
                .ToList();

            Motion motion = ValueTree(builder, clips, profile, fix);
            for (int i = gates.Count - 1; i >= 1; i--)
            {
                BlendTree gate = builder.NewTree($"{fix.Blendshape} ({gates[i]})", BlendTreeType.Direct, null);
                gate.children = new[] { FTExtrasGenerator.Direct(motion, gates[i]) };
                motion = gate;
            }
            return FTExtrasGenerator.Direct(motion, gates[0]);
        }

        /// <summary>
        /// One 1D blend tree level per parameter the fix's shapes read; the leaves hold the fix shape at the combined
        /// weight for those parameter values.
        /// </summary>
        private static Motion ValueTree(FTExtrasGenerator.ControllerBuilder builder, FTExtrasGenerator.ClipStore clips, FTFixesProfile profile, FTFixesPlan.Entry fix)
        {
            var parameters = fix.Sources.Select(s => s.Parameter).Distinct().ToList();
            bool linear = profile.combine == FTFixesCombine.Average
                          || (profile.combine == FTFixesCombine.Multiply && parameters.Count == fix.Sources.Count);
            var samples = parameters.ToDictionary(p => p, p => Samples(fix.Sources.Where(s => s.Parameter == p), linear));

            Motion Build(int level, Dictionary<string, float> values)
            {
                if (level == parameters.Count)
                {
                    float weight = Combine(profile.combine, fix.Sources.Select(s => s.Evaluate(values[s.Parameter])));
                    return FixClip(clips, fix, weight);
                }

                string parameter = parameters[level];
                var children = samples[parameter].Select(sample =>
                {
                    var next = new Dictionary<string, float>(values) { [parameter] = sample };
                    return (sample, motion: Build(level + 1, next));
                }).ToList();

                // A level whose children are all the same clip needs no tree
                if (children.All(c => c.motion == children[0].motion)) return children[0].motion;

                BlendTree tree = builder.NewTree($"{fix.Blendshape} ({parameter})", BlendTreeType.Simple1D, profile.parameterPrefix + parameter);
                foreach (var (sample, motion) in children) tree.AddChild(motion, sample);
                return tree;
            }

            return Build(0, new Dictionary<string, float>());
        }

        /// <summary>
        /// The parameter values where the shapes reading it change, plus points in between when the combined weight
        /// isn't linear between them.
        /// </summary>
        private static List<float> Samples(IEnumerable<FTFixesSources.Source> sources, bool linear)
        {
            var points = sources.SelectMany(s => s.Points.Select(p => p.Value)).Distinct().OrderBy(v => v).ToList();
            if (linear || points.Count < 2) return points;

            var samples = new List<float>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                for (int k = 0; k < Subdivisions; k++) samples.Add(Mathf.Lerp(points[i], points[i + 1], (float)k / Subdivisions));
            }
            samples.Add(points[points.Count - 1]);
            return samples;
        }

        private static float Combine(FTFixesCombine combine, IEnumerable<float> weights)
        {
            var values = weights.ToList();
            switch (combine)
            {
                case FTFixesCombine.Minimum: return values.Min();
                case FTFixesCombine.Maximum: return values.Max();
                case FTFixesCombine.Multiply: return values.Aggregate(1f, (a, b) => a * b);
                default: return values.Average();
            }
        }

        /// <summary>The fix shape held at one weight, on every mesh that has it. Rounded to whole percents and shared.</summary>
        private static AnimationClip FixClip(FTExtrasGenerator.ClipStore clips, FTFixesPlan.Entry fix, float weight)
        {
            int percent = Mathf.Clamp(Mathf.RoundToInt(weight * 100f), 0, 100);
            string name = $"{fix.Blendshape} {percent}";
            if (!clipCache.TryGetValue(name, out var clip))
            {
                clip = clips.BlendShapes(name, false,
                    (fix.MeshPaths, fix.Blendshape, AnimationCurve.Constant(0f, FTExtrasGenerator.StaticClipLength, percent)));
                clipCache[name] = clip;
            }
            return clip;
        }

        // =====================================================================
        // Prefab (VRCFury public API, via reflection)
        // =====================================================================

        /// <summary>Saves a prefab holding a VRCFury Full Controller for the animator. Returns an error or null.</summary>
        private static string CreatePrefab(string prefabPath, AnimatorController controller)
        {
            Type furyComponents = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("com.vrcfury.api.FuryComponents"))
                .FirstOrDefault(t => t != null);
            if (furyComponents == null)
            {
                return "VRCFury is not installed, so the prefab was not created. The controller is ready to merge manually.";
            }

            var go = new GameObject(PrefabName);
            try
            {
                object fullController = FTExtrasGenerator.RequireMethod(furyComponents, "CreateFullController").Invoke(null, new object[] { go });
                Type fcType = fullController.GetType();
                MethodInfo addController = FTExtrasGenerator.RequireMethod(fcType, "AddController");
                Type layerType = addController.GetParameters()[1].ParameterType;
                addController.Invoke(fullController, new object[] { controller, Enum.Parse(layerType, "FX") });

                // Keep every parameter name as-is so the fixes read the same parameters as VRCFT.
                FTExtrasGenerator.RequireMethod(fcType, "AddGlobalParam").Invoke(fullController, new object[] { "*" });

                PrefabUtility.SaveAsPrefabAsset(go, prefabPath, out bool saved);
                return saved ? null : $"Could not save the prefab at {prefabPath}.";
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                return $"Could not create the VRCFury prefab: {inner.Message}";
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        /// <summary>Adds the generated prefab under the avatar root, unless it's already there. Returns true if added.</summary>
        internal static bool AddPrefabToAvatar(GameObject avatar)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GetPrefabPath(avatar));
            if (prefab == null || IsPrefabOnAvatar(avatar)) return false;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Add Blendshape Fixes");
            Selection.activeGameObject = instance;
            return true;
        }

        internal static bool IsPrefabOnAvatar(GameObject avatar)
        {
            string prefabPath = GetPrefabPath(avatar);
            return avatar.GetComponentsInChildren<Transform>(true).Any(t =>
                PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)
                && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) == prefabPath);
        }
    }
}
