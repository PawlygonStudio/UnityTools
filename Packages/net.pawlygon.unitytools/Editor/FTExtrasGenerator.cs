using System;
using System.Collections;
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
    /// Generates the custom face tracking animator from an <see cref="FTExtrasProfile"/>: baked ear and tail
    /// clips, an FX controller, an expressions menu/parameters for the jaw toggle, fake pupil dilation, and
    /// a prefab with a VRCFury Full Controller.
    ///
    /// Layers in the generated controller:
    /// <list type="number">
    /// <item>Inputs: Direct blend trees that turn the face tracking parameters into gated, normalised
    /// animator parameters (gaze per eye, average mood, jaw) by animating them. Two states switch the jaw
    /// input on or off with the "Tail follows Jaw" toggle.</item>
    /// <item>Ears: both ears in one Direct blend tree; each ear is mood (sad, neutral, happy) → its own eye's
    /// gaze (centre, left, right, up, down). Every combination is baked from <see cref="FTExtrasPoses.Blend"/>,
    /// so the result matches the preview.</item>
    /// <item>Tail: mood (neutral, happy wag) → jaw (left, centre, right). Kept apart from the ears because a
    /// blend tree plays all its clips on one clock, so loops of different lengths cannot share it.</item>
    /// <item>Fake Pupil Dilation: idle drift plus a reflex after each blink; the layer's weight drops to 0
    /// when eye tracking is off or real pupil dilation is enabled, so it never overrides VRCFT.</item>
    /// </list>
    /// </summary>
    internal static class FTExtrasGenerator
    {
        internal const string OutputFolderName = "FaceTrackingExtras";
        internal const string PrefabName = "!Pawlygon - Face Tracking Extras";
        private const string ControllerName = "FX - Face Tracking Extras";
        private const string AnimationsFolderName = "Animations";

        private const string ParamPrefix = "Pawlygon/FTExtras/";
        private const string ParamOne = ParamPrefix + "One";
        private const string ParamGazeLeftX = ParamPrefix + "GazeLeftX";
        private const string ParamGazeRightX = ParamPrefix + "GazeRightX";
        private const string ParamGazeY = ParamPrefix + "GazeY";
        private const string ParamMood = ParamPrefix + "Mood";
        private const string ParamJaw = ParamPrefix + "Jaw";

        private const float SampleRate = 30f;
        private const float StaticClipLength = 1f / 60f;
        private const float MovedThresholdDegrees = 0.01f;

        internal class Result
        {
            public bool Success;
            public string Message;
            public string PrefabPath;
            public List<string> Warnings = new List<string>();
        }

        // =====================================================================
        // Paths
        // =====================================================================

        /// <summary>
        /// Prefabs/FaceTrackingExtras next to the avatar's prefab.
        /// </summary>
        internal static string GetOutputFolder(GameObject avatar)
        {
            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar);
            if (string.IsNullOrEmpty(prefabPath))
            {
                return PawlygonEditorUtils.CombineAssetPath("Assets/!Pawlygon", SafeName(avatar.name), "Prefabs", OutputFolderName);
            }

            string prefabFolder = PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(prefabPath));
            string prefabsFolder = Path.GetFileName(prefabFolder) == "Prefabs"
                ? prefabFolder
                : PawlygonEditorUtils.CombineAssetPath(prefabFolder, "Prefabs");
            return PawlygonEditorUtils.CombineAssetPath(prefabsFolder, OutputFolderName);
        }

        internal static string GetPrefabPath(GameObject avatar)
        {
            return PawlygonEditorUtils.CombineAssetPath(GetOutputFolder(avatar), PrefabName + ".prefab");
        }

        internal static string SafeName(string name)
        {
            return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        }

        /// <summary>
        /// Moves the profile into the output folder if it lives elsewhere. Returns an error message or null.
        /// </summary>
        internal static string MoveProfileToOutputFolder(FTExtrasProfile profile, GameObject avatar)
        {
            string current = AssetDatabase.GetAssetPath(profile);
            string folder = GetOutputFolder(avatar);
            if (PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(current)) == folder) return null;

            PawlygonEditorUtils.EnsureFolderExists(folder);
            string target = AssetDatabase.GenerateUniqueAssetPath(PawlygonEditorUtils.CombineAssetPath(folder, Path.GetFileName(current)));
            string error = AssetDatabase.MoveAsset(current, target);
            if (!string.IsNullOrEmpty(error)) return error;

            // Remove the old folder if the profile was the only thing in it.
            string oldFolder = PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(current));
            if (AssetDatabase.IsValidFolder(oldFolder) && AssetDatabase.FindAssets("", new[] { oldFolder }).Length == 0)
            {
                AssetDatabase.DeleteAsset(oldFolder);
            }
            return null;
        }

        // =====================================================================
        // Generate
        // =====================================================================

        internal static Result Generate(FTExtrasProfile profile, GameObject avatar)
        {
            var result = new Result();
            FTExtrasGenerationSettings settings = profile.generation;

            // Everything that can fail is checked before any asset is touched.
            List<string> errors = Validate(settings);
            if (errors.Count > 0)
            {
                result.Message = "Nothing was generated: " + string.Join(" ", errors);
                return result;
            }

            Type layerControlType = null;
            if (settings.fakeDilation)
            {
                layerControlType = FindLayerControlType(out string layerControlProblem);
                if (layerControlType == null) result.Warnings.Add($"{layerControlProblem} The fake pupil dilation was skipped.");
            }

            string moveError = MoveProfileToOutputFolder(profile, avatar);
            if (moveError != null) result.Warnings.Add($"Could not move the profile: {moveError}");

            string folder = GetOutputFolder(avatar);
            string clipFolder = PawlygonEditorUtils.CombineAssetPath(folder, AnimationsFolderName);
            PawlygonEditorUtils.EnsureFolderExists(clipFolder);

            var clips = new ClipStore(clipFolder);

            // --- Controller ---
            // An existing controller is emptied and rebuilt in place, so its GUID (and every reference to it,
            // such as the prefab's VRCFury component or a hand-made merge) stays valid.
            string controllerPath = PawlygonEditorUtils.CombineAssetPath(folder, ControllerName + ".controller");
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller != null)
            {
                ClearController(controller);
            }
            else
            {
                if (AssetDatabase.LoadMainAssetAtPath(controllerPath) != null) AssetDatabase.DeleteAsset(controllerPath);
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            }
            var builder = new ControllerBuilder(controller);

            AddParameters(builder, settings);

            BuildInputsLayer(builder, clips, settings);
            bool earLayer = BuildEarsLayer(builder, clips, profile);
            bool tailLayer = BuildTailLayer(builder, clips, profile);

            if (!earLayer) result.Warnings.Add("No ear poses move any bones, so no ear layer was generated.");
            if (!tailLayer) result.Warnings.Add("No tail poses move any bones, so no tail layer was generated.");

            if (settings.fakeDilation && layerControlType != null)
            {
                string dilationWarning = BuildFakeDilationLayer(builder, clips, avatar, settings, layerControlType);
                if (dilationWarning != null) result.Warnings.Add(dilationWarning);
            }

            EditorUtility.SetDirty(controller);

            int removed = clips.DeleteUnused();
            if (removed > 0) Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} Removed {removed} clip(s) no longer used by the controller.");

            // --- Menu, parameters, prefab ---
            ScriptableObject menu = null;
            ScriptableObject parameters = null;
            try
            {
                parameters = SaveOrReplace(CreateExpressionParameters(settings), PawlygonEditorUtils.CombineAssetPath(folder, "Parameters - Face Tracking Extras.asset"));
                menu = SaveOrReplace(CreateExpressionsMenu(settings), PawlygonEditorUtils.CombineAssetPath(folder, "Menu - Face Tracking Extras.asset"));
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not create the expressions menu/parameters, so the Tail follows Jaw toggle has no menu entry: {ex.Message}");
            }

            AssetDatabase.SaveAssets();

            string prefabPath = GetPrefabPath(avatar);
            string prefabError = CreatePrefab(prefabPath, controller, menu, parameters, settings);
            if (prefabError != null)
            {
                result.Warnings.Add(prefabError);
            }
            else
            {
                result.PrefabPath = prefabPath;
            }

            AssetDatabase.SaveAssets();

            result.Success = true;
            result.Message = $"Generated {clips.Count} clips and {controller.layers.Length} layers in {folder}.";
            return result;
        }

        // =====================================================================
        // Validation and controller reuse
        // =====================================================================

        /// <summary>
        /// Settings problems that would produce a broken controller. Empty when everything is usable.
        /// </summary>
        internal static List<string> Validate(FTExtrasGenerationSettings settings)
        {
            var errors = new List<string>();
            var inputs = new (string Label, string Value)[]
            {
                ("Eye Left X", settings.eyeLeftX), ("Eye Right X", settings.eyeRightX), ("Eye Y", settings.eyeY),
                ("Smile Frown Left", settings.smileFrownLeft), ("Smile Frown Right", settings.smileFrownRight), ("Jaw X", settings.jawX),
                ("Eye Lid Left", settings.eyeLidLeft), ("Eye Lid Right", settings.eyeLidRight),
                ("Eye Tracking Active", settings.eyeTrackingActive), ("Lip Tracking Active", settings.lipTrackingActive),
                ("Eye Dilation Enable", settings.eyeDilationEnable),
            };

            foreach (var (label, value) in inputs)
            {
                if (string.IsNullOrWhiteSpace(value)) errors.Add($"The '{label}' parameter name is empty.");
            }

            string toggle = settings.tailFollowsJawParameter;
            if (string.IsNullOrWhiteSpace(toggle))
            {
                errors.Add("The Tail follows Jaw parameter name is empty.");
            }
            else if (inputs.Any(i => i.Value == toggle))
            {
                errors.Add($"The Tail follows Jaw parameter '{toggle}' is also used as a face tracking input. It must be its own bool parameter.");
            }
            else if (toggle.StartsWith(ParamPrefix))
            {
                errors.Add($"The Tail follows Jaw parameter cannot start with '{ParamPrefix}', which is reserved for generated parameters.");
            }

            if (settings.gazeFullAt <= 0f || settings.moodFullAt <= 0f || settings.jawFullAt <= 0f)
            {
                errors.Add("The input ranges (gaze, mood, jaw full at) must be above 0.");
            }

            return errors;
        }

        /// <summary>
        /// VRChat's Animator Layer Control behaviour, checked for the fields the generator sets.
        /// Null (with a reason) if the SDK is missing or the fields were renamed.
        /// </summary>
        private static Type FindLayerControlType(out string problem)
        {
            Type type = FindType("VRCAnimatorLayerControl", typeof(StateMachineBehaviour));
            if (type == null)
            {
                problem = "VRCAnimatorLayerControl was not found (is the VRChat Avatars SDK installed?).";
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            string missing = new[] { "playable", "layer", "goalWeight", "blendDuration" }.FirstOrDefault(f => type.GetField(f, flags) == null);
            if (missing != null)
            {
                problem = $"VRCAnimatorLayerControl has no '{missing}' field in this VRChat SDK version.";
                return null;
            }

            problem = null;
            return type;
        }

        /// <summary>
        /// Removes every layer, parameter and sub-asset (state machines, states, transitions, blend trees,
        /// behaviours) so the controller can be rebuilt in place.
        /// </summary>
        private static void ClearController(AnimatorController controller)
        {
            controller.layers = Array.Empty<AnimatorControllerLayer>();
            controller.parameters = Array.Empty<AnimatorControllerParameter>();

            string path = AssetDatabase.GetAssetPath(controller);
            foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (subAsset == null || subAsset == controller) continue;
                AssetDatabase.RemoveObjectFromAsset(subAsset);
                UnityEngine.Object.DestroyImmediate(subAsset, true);
            }
        }

        // =====================================================================
        // Parameters
        // =====================================================================

        private static void AddParameters(ControllerBuilder builder, FTExtrasGenerationSettings settings)
        {
            foreach (string input in new[]
                     {
                         settings.eyeLeftX, settings.eyeRightX, settings.eyeY,
                         settings.smileFrownLeft, settings.smileFrownRight, settings.jawX,
                         settings.eyeLidLeft, settings.eyeLidRight,
                         settings.eyeTrackingActive, settings.lipTrackingActive, settings.eyeDilationEnable,
                     })
            {
                builder.AddFloat(input, 0f);
            }

            builder.AddBool(settings.tailFollowsJawParameter, true);

            builder.AddFloat(ParamOne, 1f);
            builder.AddFloat(ParamGazeLeftX, 0f);
            builder.AddFloat(ParamGazeRightX, 0f);
            builder.AddFloat(ParamGazeY, 0f);
            builder.AddFloat(ParamMood, 0f);
            builder.AddFloat(ParamJaw, 0f);
        }

        // =====================================================================
        // Inputs layer
        // =====================================================================

        /// <summary>
        /// Writes gated, normalised values into animator parameters:
        ///   GazeLeftX/GazeRightX/GazeY = eye input / gazeFullAt (clamped to ±1), only while eye tracking is active.
        ///   Mood = average of SmileFrownLeft/Right / moodFullAt, only while lip tracking is active.
        ///   Jaw = JawX / jawFullAt, only while lip tracking is active and the "Tail follows Jaw" toggle is on.
        /// The toggle is a bool, which cannot weight a blend tree, so it switches between two states: one whose
        /// tree includes the jaw input and one without it.
        /// </summary>
        private static void BuildInputsLayer(ControllerBuilder builder, ClipStore clips, FTExtrasGenerationSettings settings)
        {
            AnimatorStateMachine sm = builder.AddLayer("Inputs", 1f, isFirstLayer: true);

            AnimatorState jawOn = sm.AddState("Inputs (Jaw On)", new Vector3(300f, 100f));
            jawOn.motion = InputsTree(builder, clips, settings, includeJaw: true);
            jawOn.writeDefaultValues = true;

            AnimatorState jawOff = sm.AddState("Inputs (Jaw Off)", new Vector3(300f, 200f));
            jawOff.motion = InputsTree(builder, clips, settings, includeJaw: false);
            jawOff.writeDefaultValues = true;

            sm.defaultState = jawOn;
            Transition(jawOn, jawOff, 0f).AddCondition(AnimatorConditionMode.IfNot, 0f, settings.tailFollowsJawParameter);
            Transition(jawOff, jawOn, 0f).AddCondition(AnimatorConditionMode.If, 0f, settings.tailFollowsJawParameter);
        }

        /// <summary>
        /// A Direct blend tree adds its children's values, so a zero clip at weight 1 keeps every output written
        /// and each gated input adds on top of it.
        /// </summary>
        private static BlendTree InputsTree(ControllerBuilder builder, ClipStore clips, FTExtrasGenerationSettings settings, bool includeJaw)
        {
            var outputs = new[] { ParamGazeLeftX, ParamGazeRightX, ParamGazeY, ParamMood, ParamJaw };
            AnimationClip zero = clips.Parameters("Inputs - Zero", outputs.Select(p => (p, 0f)).ToArray());

            BlendTree root = builder.NewTree(includeJaw ? "Inputs (Jaw On)" : "Inputs (Jaw Off)", BlendTreeType.Direct, null);
            var children = new List<ChildMotion>
            {
                Direct(zero, ParamOne),
                Direct(RangeTree(builder, clips, settings.eyeLeftX, settings.gazeFullAt, ParamGazeLeftX, 1f), settings.eyeTrackingActive),
                Direct(RangeTree(builder, clips, settings.eyeRightX, settings.gazeFullAt, ParamGazeRightX, 1f), settings.eyeTrackingActive),
                Direct(RangeTree(builder, clips, settings.eyeY, settings.gazeFullAt, ParamGazeY, 1f), settings.eyeTrackingActive),
                Direct(RangeTree(builder, clips, settings.smileFrownLeft, settings.moodFullAt, ParamMood, 0.5f), settings.lipTrackingActive),
                Direct(RangeTree(builder, clips, settings.smileFrownRight, settings.moodFullAt, ParamMood, 0.5f), settings.lipTrackingActive),
            };

            if (includeJaw)
            {
                children.Add(Direct(RangeTree(builder, clips, settings.jawX, settings.jawFullAt, ParamJaw, 1f), settings.lipTrackingActive));
            }

            root.children = children.ToArray();
            return root;
        }

        /// <summary>
        /// 1D tree mapping input ∈ [-fullAt, +fullAt] linearly to output ∈ [-scale, +scale] (clamped outside).
        /// </summary>
        private static BlendTree RangeTree(ControllerBuilder builder, ClipStore clips, string input, float fullAt, string output, float scale)
        {
            fullAt = Mathf.Max(0.01f, fullAt);
            string label = output.Substring(ParamPrefix.Length);
            AnimationClip low = clips.Parameters($"Inputs - {label} -{scale:0.#}", (output, -scale));
            AnimationClip high = clips.Parameters($"Inputs - {label} +{scale:0.#}", (output, scale));

            BlendTree tree = builder.NewTree($"{label} from {input}", BlendTreeType.Simple1D, input);
            tree.AddChild(low, -fullAt);
            tree.AddChild(high, fullAt);
            return tree;
        }

        // =====================================================================
        // Ear and tail layers
        // =====================================================================

        private static readonly (string Name, float Value)[] Moods = { ("Sad", -1f), ("Neutral", 0f), ("Happy", 1f) };

        /// <summary>
        /// Gaze samples for the 2D tree. The diagonals are baked too: without them Unity blends looking
        /// up-right as half Right plus half Up, so the ears would move about half as far as the preview shows.
        /// </summary>
        private static readonly (string Name, Vector2 Position)[] Gazes =
        {
            ("Centre", Vector2.zero), ("Right", Vector2.right), ("Left", Vector2.left), ("Up", Vector2.up), ("Down", Vector2.down),
            ("Up Right", new Vector2(1f, 1f)), ("Up Left", new Vector2(-1f, 1f)), ("Down Right", new Vector2(1f, -1f)), ("Down Left", new Vector2(-1f, -1f)),
        };

        /// <summary>
        /// Both ears in one layer: a Direct blend tree plays each ear's tree at full weight. The ears animate
        /// different bones and every ear clip has the flick length, so they share a clock safely.
        /// </summary>
        private static bool BuildEarsLayer(ControllerBuilder builder, ClipStore clips, FTExtrasProfile profile)
        {
            var trees = new[] { EarTree(builder, clips, profile, isLeft: true), EarTree(builder, clips, profile, isLeft: false) }
                .Where(t => t != null)
                .ToArray();
            if (trees.Length == 0) return false;

            BlendTree root = builder.NewTree("Ears", BlendTreeType.Direct, null);
            root.children = trees.Select(t => Direct(t, ParamOne)).ToArray();
            builder.SingleStateLayer("Ears", root);
            return true;
        }

        /// <summary>
        /// Mood (sad, neutral, happy) → gaze for one ear, driven by that ear's eye X and the shared eye Y.
        /// Null when no pose moves this ear.
        /// </summary>
        private static BlendTree EarTree(ControllerBuilder builder, ClipStore clips, FTExtrasProfile profile, bool isLeft)
        {
            List<FTExtrasProfileBone> bones = isLeft ? profile.earLeft : profile.earRight;
            Func<FTExtrasPoseData, List<Quaternion>> select = p => isLeft ? p.earLeft : p.earRight;
            List<int> moved = MovedBones(profile, bones, select);
            if (moved.Count == 0) return null;

            string side = isLeft ? "Left" : "Right";
            string gazeX = isLeft ? ParamGazeLeftX : ParamGazeRightX;
            float length = Mathf.Max(0.1f, profile.earFlickPeriod);
            bool flick = FTExtrasPoses.Resolve(profile, FTExtrasPoseId.EarHappyFlick) != null;

            BlendTree moodTree = builder.NewTree($"Ear {side} - Mood", BlendTreeType.Simple1D, ParamMood);
            foreach (var mood in Moods)
            {
                BlendTree gazeTree = builder.NewTree($"Ear {side} - {mood.Name}", BlendTreeType.FreeformDirectional2D, gazeX, ParamGazeY);
                bool animated = mood.Value > 0f && flick;

                foreach (var gaze in Gazes)
                {
                    var inputs = new FTExtrasPoses.BlendInputs { GazeX = gaze.Position.x, GazeY = gaze.Position.y, Mood = mood.Value, PlayLoops = animated };
                    AnimationClip clip = clips.Rotations(
                        $"Ear {side} - {mood.Name} - Look {gaze.Name}", bones, moved, length, animated,
                        t => { inputs.Time = t; return select(FTExtrasPoses.Blend(profile, inputs)); });
                    gazeTree.AddChild(clip, gaze.Position);
                }

                moodTree.AddChild(gazeTree, mood.Value);
            }

            return moodTree;
        }

        private static bool BuildTailLayer(ControllerBuilder builder, ClipStore clips, FTExtrasProfile profile)
        {
            List<int> moved = MovedBones(profile, profile.tail, p => p.tail);
            if (moved.Count == 0) return false;

            float length = Mathf.Max(0.1f, profile.tailWagPeriod);
            bool wag = FTExtrasPoses.Resolve(profile, FTExtrasPoseId.TailRight) != null;
            var jaws = new[] { ("Left", -1f), ("Centre", 0f), ("Right", 1f) };

            BlendTree moodTree = builder.NewTree("Tail - Mood", BlendTreeType.Simple1D, ParamMood);
            foreach (var mood in new[] { ("Neutral", 0f), ("Happy", 1f) })
            {
                BlendTree jawTree = builder.NewTree($"Tail - {mood.Item1}", BlendTreeType.Simple1D, ParamJaw);
                bool animated = mood.Item2 > 0f && wag;

                foreach (var jaw in jaws)
                {
                    var inputs = new FTExtrasPoses.BlendInputs { Mood = mood.Item2, JawX = jaw.Item2, PlayLoops = animated };
                    AnimationClip clip = clips.Rotations(
                        $"Tail - {mood.Item1} - Jaw {jaw.Item1}", profile.tail, moved, length, animated,
                        t => { inputs.Time = t; return FTExtrasPoses.Blend(profile, inputs).tail; });
                    jawTree.AddChild(clip, jaw.Item2);
                }

                moodTree.AddChild(jawTree, mood.Item2);
            }

            builder.SingleStateLayer("Tail", moodTree);
            return true;
        }

        /// <summary>
        /// Indices of bones that at least one pose rotates away from rest. Bones no pose moves are left out of
        /// the clips, so PhysBones and other animations keep full control of them.
        /// </summary>
        private static List<int> MovedBones(FTExtrasProfile profile, List<FTExtrasProfileBone> bones, Func<FTExtrasPoseData, List<Quaternion>> select)
        {
            var moved = new List<int>();
            var poses = FTExtrasPoses.All.Select(d => FTExtrasPoses.Resolve(profile, d.Id)).Where(p => p != null).ToList();

            for (int i = 0; i < bones.Count; i++)
            {
                bool isMoved = poses.Any(p =>
                {
                    List<Quaternion> locals = select(p);
                    return locals != null && locals.Count == bones.Count
                        && Quaternion.Angle(locals[i], bones[i].restLocal) > MovedThresholdDegrees;
                });
                if (isMoved) moved.Add(i);
            }
            return moved;
        }

        // =====================================================================
        // Fake pupil dilation
        // =====================================================================

        /// <summary>
        /// Off → Idle → Closed (blink) → Reflex → Idle. Off sets the layer weight to 0 so nothing is written
        /// while eye tracking is off or real dilation is on. The curves come from <see cref="FTExtrasPupils"/>,
        /// which the window's preview also uses. Returns a warning, or null.
        /// </summary>
        private static string BuildFakeDilationLayer(ControllerBuilder builder, ClipStore clips, GameObject avatar, FTExtrasGenerationSettings settings, Type layerControlType)
        {
            var (dilationMeshes, constrictMeshes) = FTExtrasPupils.FindMeshes(avatar);
            List<string> dilationPaths = dilationMeshes.Select(r => FaceTrackingExtrasCore.GetRelativePath(r.transform, avatar.transform)).ToList();
            List<string> constrictPaths = constrictMeshes.Select(r => FaceTrackingExtrasCore.GetRelativePath(r.transform, avatar.transform)).ToList();

            if (dilationPaths.Count == 0)
            {
                return $"No mesh has an '{FTExtrasPupils.DilationShape}' blendshape, so the fake pupil dilation was skipped.";
            }

            float length = FTExtrasPupils.IdleLength;
            int idleSamples = Mathf.RoundToInt(length * 10f);
            float[] idleTimes = Enumerable.Range(0, idleSamples + 1).Select(i => i * length / idleSamples).ToArray();
            AnimationCurve idleDilation = FTExtrasPupils.SmoothCurve(idleTimes, idleTimes.Select(t => FTExtrasPupils.IdleDilation(settings, t)).ToArray());
            AnimationCurve idleConstrict = FTExtrasPupils.SmoothCurve(new[] { 0f, length }, new[] { 0f, 0f });
            AnimationClip idle = clips.BlendShapes("Pupil - Idle", loop: true,
                (dilationPaths, FTExtrasPupils.DilationShape, idleDilation),
                (constrictPaths, FTExtrasPupils.ConstrictShape, idleConstrict));

            var (reflexDilation, reflexConstrict) = FTExtrasPupils.ReflexCurves(settings);
            AnimationClip reflex = clips.BlendShapes("Pupil - Blink Reflex", loop: false,
                (dilationPaths, FTExtrasPupils.DilationShape, reflexDilation),
                (constrictPaths, FTExtrasPupils.ConstrictShape, reflexConstrict));

            AnimationClip empty = clips.Empty("Pupil - Off");

            int layerIndex = builder.Controller.layers.Length;
            AnimatorStateMachine sm = builder.AddLayer("Fake Pupil Dilation", 0f);

            AnimatorState off = sm.AddState("Off", new Vector3(300f, 0f));
            off.motion = empty;
            AnimatorState open = sm.AddState("Idle", new Vector3(300f, 120f));
            open.motion = idle;
            AnimatorState closed = sm.AddState("Eyes Closed", new Vector3(560f, 120f));
            closed.motion = idle;
            AnimatorState reflexState = sm.AddState("Blink Reflex", new Vector3(430f, 240f));
            reflexState.motion = reflex;
            foreach (AnimatorState state in new[] { off, open, closed, reflexState }) state.writeDefaultValues = true;
            sm.defaultState = off;

            AddLayerControl(off, layerControlType, layerIndex, 0f, 0.25f);
            AddLayerControl(open, layerControlType, layerIndex, 1f, 0.25f);

            // Any State → Off, without re-entering Off every frame.
            AnyStateTransition(sm, off).AddCondition(AnimatorConditionMode.Less, 0.5f, settings.eyeTrackingActive);
            AnyStateTransition(sm, off).AddCondition(AnimatorConditionMode.Greater, 0.5f, settings.eyeDilationEnable);

            AnimatorStateTransition toIdle = Transition(off, open, 0f);
            toIdle.AddCondition(AnimatorConditionMode.Greater, 0.5f, settings.eyeTrackingActive);
            toIdle.AddCondition(AnimatorConditionMode.Less, 0.5f, settings.eyeDilationEnable);

            foreach (AnimatorState from in new[] { open, reflexState })
            {
                AnimatorStateTransition blink = Transition(from, closed, 0.05f);
                blink.AddCondition(AnimatorConditionMode.Less, settings.lidClosedBelow, settings.eyeLidLeft);
                blink.AddCondition(AnimatorConditionMode.Less, settings.lidClosedBelow, settings.eyeLidRight);
            }

            Transition(closed, reflexState, 0f).AddCondition(AnimatorConditionMode.Greater, settings.lidOpenAbove, settings.eyeLidLeft);
            Transition(closed, reflexState, 0f).AddCondition(AnimatorConditionMode.Greater, settings.lidOpenAbove, settings.eyeLidRight);

            AnimatorStateTransition settle = Transition(reflexState, open, 0.25f);
            settle.hasExitTime = true;
            settle.exitTime = 1f;

            return null;
        }

        private static void AddLayerControl(AnimatorState state, Type type, int layer, float goalWeight, float duration)
        {
            StateMachineBehaviour behaviour = state.AddStateMachineBehaviour(type);
            SetField(behaviour, "playable", "FX");
            SetField(behaviour, "layer", layer);
            SetField(behaviour, "goalWeight", goalWeight);
            SetField(behaviour, "blendDuration", duration);
        }

        // =====================================================================
        // Expressions menu / parameters (VRChat SDK, via reflection)
        // =====================================================================

        private static ScriptableObject CreateExpressionParameters(FTExtrasGenerationSettings settings)
        {
            Type paramsType = FindType("VRCExpressionParameters", typeof(ScriptableObject))
                ?? throw new InvalidOperationException("VRCExpressionParameters type not found");
            Type paramType = paramsType.GetNestedType("Parameter") ?? throw new InvalidOperationException("Parameter type not found");

            var asset = ScriptableObject.CreateInstance(paramsType);
            object parameter = Activator.CreateInstance(paramType);
            SetField(parameter, "name", settings.tailFollowsJawParameter);
            SetField(parameter, "valueType", "Bool");
            SetField(parameter, "defaultValue", 1f);
            SetField(parameter, "saved", true);
            SetField(parameter, "networkSynced", true);

            Array array = Array.CreateInstance(paramType, 1);
            array.SetValue(parameter, 0);
            SetField(asset, "parameters", array);
            return asset;
        }

        private static ScriptableObject CreateExpressionsMenu(FTExtrasGenerationSettings settings)
        {
            Type menuType = FindType("VRCExpressionsMenu", typeof(ScriptableObject))
                ?? throw new InvalidOperationException("VRCExpressionsMenu type not found");
            Type controlType = menuType.GetNestedType("Control") ?? throw new InvalidOperationException("Control type not found");
            Type controlParamType = controlType.GetNestedType("Parameter") ?? throw new InvalidOperationException("Control.Parameter type not found");

            var menu = ScriptableObject.CreateInstance(menuType);
            object control = Activator.CreateInstance(controlType);
            SetField(control, "name", "Tail follows Jaw");
            SetField(control, "type", "Toggle");
            SetField(control, "value", 1f);

            object parameter = Activator.CreateInstance(controlParamType);
            SetField(parameter, "name", settings.tailFollowsJawParameter);
            SetField(control, "parameter", parameter);
            FillNullArrays(control);

            if (GetFieldValue(menu, "controls") is IList controls) controls.Add(control);
            return menu;
        }

        // =====================================================================
        // Prefab (VRCFury public API, via reflection)
        // =====================================================================

        /// <summary>
        /// Saves a prefab holding a VRCFury Full Controller for the generated assets. Returns an error or null.
        /// </summary>
        private static string CreatePrefab(string prefabPath, AnimatorController controller, ScriptableObject menu, ScriptableObject parameters, FTExtrasGenerationSettings settings)
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
                object fullController = RequireMethod(furyComponents, "CreateFullController").Invoke(null, new object[] { go });
                Type fcType = fullController.GetType();

                MethodInfo addController = RequireMethod(fcType, "AddController");
                Type layerType = addController.GetParameters()[1].ParameterType;
                addController.Invoke(fullController, new object[] { controller, Enum.Parse(layerType, "FX") });

                if (menu != null) RequireMethod(fcType, "AddMenu").Invoke(fullController, new object[] { menu, settings.menuName });
                if (parameters != null) RequireMethod(fcType, "AddParams").Invoke(fullController, new object[] { parameters });

                // Keep every parameter name as-is so the face tracking inputs are shared with VRCFT.
                RequireMethod(fcType, "AddGlobalParam").Invoke(fullController, new object[] { "*" });

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

        /// <summary>
        /// Adds the generated prefab under the avatar root, unless it is already there. Returns true if added.
        /// </summary>
        internal static bool AddPrefabToAvatar(GameObject avatar)
        {
            string prefabPath = GetPrefabPath(avatar);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null || IsPrefabOnAvatar(avatar)) return false;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, avatar.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Add Face Tracking Extras");
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

        // =====================================================================
        // Helpers: transitions, blend trees, reflection, assets
        // =====================================================================

        private static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, float duration)
        {
            AnimatorStateTransition transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = duration;
            transition.canTransitionToSelf = false;
            return transition;
        }

        private static AnimatorStateTransition AnyStateTransition(AnimatorStateMachine sm, AnimatorState to)
        {
            AnimatorStateTransition transition = sm.AddAnyStateTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.canTransitionToSelf = false;
            return transition;
        }

        private static ChildMotion Direct(Motion motion, string weightParameter)
        {
            return new ChildMotion { motion = motion, directBlendParameter = weightParameter, timeScale = 1f };
        }

        private static Type FindType(string name, Type baseType)
        {
            return TypeCache.GetTypesDerivedFrom(baseType).FirstOrDefault(t => t.Name == name && t.Namespace != null && t.Namespace.StartsWith("VRC"));
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException(target.GetType().Name, name);

            if (field.FieldType.IsEnum && value is string enumName) value = Enum.Parse(field.FieldType, enumName);
            field.SetValue(target, value);
        }

        /// <summary>
        /// Replaces null array fields with empty arrays (the SDK expects e.g. subParameters and labels to exist).
        /// </summary>
        private static void FillNullArrays(object target)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType.IsArray && field.GetValue(target) == null)
                {
                    field.SetValue(target, Array.CreateInstance(field.FieldType.GetElementType(), 0));
                }
            }
        }

        private static MethodInfo RequireMethod(Type type, string name)
        {
            return type.GetMethod(name)
                ?? throw new MissingMethodException($"VRCFury's API has no '{type.Name}.{name}' method in this version.");
        }

        private static object GetFieldValue(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        }

        /// <summary>
        /// Saves <paramref name="asset"/> at <paramref name="path"/>, copying into the existing asset if there is
        /// one so its GUID (and anything referencing it) is kept.
        /// </summary>
        private static T SaveOrReplace<T>(T asset, string path) where T : UnityEngine.Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null && existing.GetType() == asset.GetType())
            {
                EditorUtility.CopySerialized(asset, existing);
                existing.name = Path.GetFileNameWithoutExtension(path);
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(asset);
                return existing;
            }

            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        /// <summary>
        /// Builds layers, states and blend trees inside a controller asset.
        /// </summary>
        private class ControllerBuilder
        {
            public AnimatorController Controller { get; }

            public ControllerBuilder(AnimatorController controller)
            {
                Controller = controller;
            }

            public void AddFloat(string name, float defaultValue)
            {
                if (string.IsNullOrEmpty(name) || Controller.parameters.Any(p => p.name == name)) return;
                Controller.AddParameter(new AnimatorControllerParameter { name = name, type = AnimatorControllerParameterType.Float, defaultFloat = defaultValue });
            }

            public void AddBool(string name, bool defaultValue)
            {
                if (string.IsNullOrEmpty(name) || Controller.parameters.Any(p => p.name == name)) return;
                Controller.AddParameter(new AnimatorControllerParameter { name = name, type = AnimatorControllerParameterType.Bool, defaultBool = defaultValue });
            }

            public BlendTree NewTree(string name, BlendTreeType type, string parameterX, string parameterY = null)
            {
                var tree = new BlendTree
                {
                    name = name,
                    blendType = type,
                    useAutomaticThresholds = false,
                    hideFlags = HideFlags.HideInHierarchy,
                };
                if (parameterX != null) tree.blendParameter = parameterX;
                if (parameterY != null) tree.blendParameterY = parameterY;
                AssetDatabase.AddObjectToAsset(tree, Controller);
                return tree;
            }

            /// <summary>
            /// Adds a layer and returns its state machine. The controller's default "Base Layer" is reused for the first layer.
            /// </summary>
            public AnimatorStateMachine AddLayer(string name, float weight, bool isFirstLayer = false)
            {
                AnimatorControllerLayer[] layers = Controller.layers;
                if (isFirstLayer && layers.Length == 1 && layers[0].stateMachine != null && layers[0].stateMachine.states.Length == 0)
                {
                    layers[0].name = name;
                    layers[0].defaultWeight = weight;
                    layers[0].stateMachine.name = name;
                    Controller.layers = layers;
                    return layers[0].stateMachine;
                }

                Controller.AddLayer(name);
                layers = Controller.layers;
                layers[layers.Length - 1].defaultWeight = weight;
                Controller.layers = layers;
                return layers[layers.Length - 1].stateMachine;
            }

            public void SingleStateLayer(string name, Motion motion, bool isFirstLayer = false)
            {
                AnimatorStateMachine sm = AddLayer(name, 1f, isFirstLayer);
                AnimatorState state = sm.AddState(name, new Vector3(300f, 100f));
                state.motion = motion;
                state.writeDefaultValues = true;
                sm.defaultState = state;
            }
        }

        /// <summary>
        /// Creates clips in the Animations folder, reusing existing clip assets so their GUIDs stay stable.
        /// </summary>
        private class ClipStore
        {
            private readonly string folder;
            private readonly HashSet<string> savedPaths = new HashSet<string>();
            public int Count => savedPaths.Count;

            public ClipStore(string folder)
            {
                this.folder = folder;
            }

            /// <summary>Clip that sets animator parameters to constant values.</summary>
            public AnimationClip Parameters(string name, params (string Parameter, float Value)[] values)
            {
                var clip = new AnimationClip();
                foreach (var (parameter, value) in values)
                {
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter),
                        AnimationCurve.Constant(0f, StaticClipLength, value));
                }
                return Save(name, clip);
            }

            /// <summary>
            /// Clip that rotates the given bones. Static clips hold one pose; animated clips sample
            /// <paramref name="sample"/> across the clip and loop.
            /// </summary>
            public AnimationClip Rotations(string name, List<FTExtrasProfileBone> bones, List<int> indices, float length, bool animated, Func<float, List<Quaternion>> sample)
            {
                // Every clip in a blend tree has the same length, so mixing static and looping clips never
                // stretches the loop timing.
                int steps = animated ? Mathf.Max(2, Mathf.CeilToInt(length * SampleRate)) : 1;
                float[] times = Enumerable.Range(0, steps + 1).Select(i => i * length / steps).ToArray();
                List<Quaternion>[] frames = times.Select(t => sample(t)).ToArray();

                var clip = new AnimationClip();
                foreach (int index in indices)
                {
                    var keys = new Keyframe[4][];
                    for (int c = 0; c < 4; c++) keys[c] = new Keyframe[times.Length];

                    Quaternion previous = frames[0][index];
                    for (int f = 0; f < times.Length; f++)
                    {
                        // Stay in one hemisphere so per-component interpolation never takes the long way round.
                        Quaternion q = frames[f][index];
                        if (Quaternion.Dot(q, previous) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                        previous = q;

                        keys[0][f] = new Keyframe(times[f], q.x);
                        keys[1][f] = new Keyframe(times[f], q.y);
                        keys[2][f] = new Keyframe(times[f], q.z);
                        keys[3][f] = new Keyframe(times[f], q.w);
                    }

                    string[] components = { "x", "y", "z", "w" };
                    for (int c = 0; c < 4; c++)
                    {
                        var curve = new AnimationCurve(keys[c]);
                        if (animated)
                        {
                            for (int k = 0; k < curve.length; k++) curve.SmoothTangents(k, 0f);
                        }
                        AnimationUtility.SetEditorCurve(clip,
                            EditorCurveBinding.FloatCurve(bones[index].path, typeof(Transform), $"m_LocalRotation.{components[c]}"), curve);
                    }
                }

                if (animated) SetLoop(clip, true);
                return Save(name, clip);
            }

            /// <summary>Clip with blendshape curves on several meshes.</summary>
            public AnimationClip BlendShapes(string name, bool loop, params (List<string> Paths, string Shape, AnimationCurve Curve)[] curves)
            {
                var clip = new AnimationClip();
                foreach (var (paths, shape, curve) in curves)
                {
                    foreach (string path in paths)
                    {
                        AnimationUtility.SetEditorCurve(clip,
                            EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), $"blendShape.{shape}"), curve);
                    }
                }

                SetLoop(clip, loop);
                return Save(name, clip);
            }

            public AnimationClip Empty(string name)
            {
                return Save(name, new AnimationClip());
            }

            private static void SetLoop(AnimationClip clip, bool loop)
            {
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }

            private AnimationClip Save(string name, AnimationClip clip)
            {
                clip.name = name;
                string path = PawlygonEditorUtils.CombineAssetPath(folder, SafeName(name) + ".anim");
                savedPaths.Add(path);
                return SaveOrReplace(clip, path);
            }

            /// <summary>
            /// Deletes clips in the Animations folder that this run did not write (left over from earlier
            /// generations). The folder belongs to the generator, so nothing else lives there.
            /// </summary>
            public int DeleteUnused()
            {
                int removed = 0;
                foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(path)) != folder || savedPaths.Contains(path)) continue;
                    if (AssetDatabase.DeleteAsset(path)) removed++;
                }
                return removed;
            }
        }
    }
}
