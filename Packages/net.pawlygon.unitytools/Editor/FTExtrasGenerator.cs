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

        /// <summary>Name of the generated controller asset (other tools skip it when reading the avatar's parameters).</summary>
        internal const string ControllerAssetName = ControllerName;
        private const string BuildControllerName = "_Building - FX - Face Tracking Extras";

        /// <summary>Menu icon for the Tail follows Jaw toggle, copied into each output folder.</summary>
        internal const string TailJawIconFileName = "Icon - Tail Follows Jaw.png";
        private const string BundledTailJawIconPath = "Editor/Icons/TailFollowsJaw.png";
        private const int MenuIconMaxSize = 256;
        private const string AnimationsFolderName = "Animations";

        private const string ParamPrefix = "Pawlygon/FTExtras/";
        private const string ParamOne = ParamPrefix + "One";
        private const string ParamGazeLeftX = ParamPrefix + "GazeLeftX";
        private const string ParamGazeRightX = ParamPrefix + "GazeRightX";
        private const string ParamGazeY = ParamPrefix + "GazeY";
        private const string ParamMood = ParamPrefix + "Mood";
        private const string ParamJaw = ParamPrefix + "Jaw";
        private const string ParamTimer = ParamPrefix + "Timer";
        private const string CustomParamPrefix = ParamPrefix + "Custom/";

        /// <summary>VRChat expression menus hold at most eight controls.</summary>
        private const int MaxMenuControls = 8;

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

            FTExtrasProfile folderOwner = FindOtherProfileInFolder(profile, GetOutputFolder(avatar));
            if (folderOwner != null)
            {
                result.Message = $"Nothing was generated: {GetOutputFolder(avatar)} already holds the profile '{folderOwner.name}' " +
                                 $"for '{folderOwner.avatarName}'. Rename one of the avatars or make it a prefab so each gets its own folder.";
                return result;
            }

            var customs = new CustomAnimationPlan(profile, settings);

            Type layerControlType = null;
            if (settings.fakeDilation || customs.Animations.Count > 0)
            {
                layerControlType = FindLayerControlType(out string layerControlProblem);
                if (layerControlType == null)
                {
                    if (settings.fakeDilation) result.Warnings.Add($"{layerControlProblem} The fake pupil dilation was skipped.");
                    if (customs.Animations.Count > 0) result.Warnings.Add($"{layerControlProblem} The custom animations were skipped.");
                }
            }
            result.Warnings.AddRange(customs.Problems);

            // Profiles created before the VRCFT/Extra default still carry an old default submenu name.
            if (FTExtrasGenerationSettings.LegacyMenuNames.Contains(settings.menuName))
            {
                Undo.RecordObject(profile, "Update Face Tracking Extras Menu");
                settings.menuName = FTExtrasGenerationSettings.DefaultMenuName;
                EditorUtility.SetDirty(profile);
            }

            string moveError = MoveProfileToOutputFolder(profile, avatar);
            if (moveError != null) result.Warnings.Add($"Could not move the profile: {moveError}");

            string folder = GetOutputFolder(avatar);
            string clipFolder = PawlygonEditorUtils.CombineAssetPath(folder, AnimationsFolderName);
            PawlygonEditorUtils.EnsureFolderExists(clipFolder);

            var clips = new ClipStore(clipFolder);

            // --- Controller ---
            // The controller is built into a temporary asset first. Only when that succeeds is its content moved
            // into the real controller, which keeps its GUID (so the prefab's VRCFury component and any hand-made
            // reference stay valid) and is never left half-built by a failure.
            string controllerPath = PawlygonEditorUtils.CombineAssetPath(folder, ControllerName + ".controller");
            string buildPath = PawlygonEditorUtils.CombineAssetPath(folder, BuildControllerName + ".controller");
            if (AssetDatabase.LoadMainAssetAtPath(buildPath) != null) AssetDatabase.DeleteAsset(buildPath);

            AnimatorController building = AnimatorController.CreateAnimatorControllerAtPath(buildPath);
            try
            {
                var builder = new ControllerBuilder(building);

                AddParameters(builder, settings);
                customs.AddParameters(builder);

                BuildInputsLayer(builder, clips, settings, customs);
                bool earLayer = BuildEarsLayer(builder, clips, profile);
                bool tailLayer = BuildTailLayer(builder, clips, profile);

                if (!earLayer) result.Warnings.Add("No ear poses move any bones, so no ear layer was generated.");
                if (!tailLayer) result.Warnings.Add("No tail poses move any bones, so no tail layer was generated.");

                if (settings.fakeDilation && layerControlType != null)
                {
                    string dilationWarning = BuildFakeDilationLayer(builder, clips, avatar, settings, layerControlType);
                    if (dilationWarning != null) result.Warnings.Add(dilationWarning);
                }

                if (layerControlType != null)
                {
                    BuildCustomAnimationLayers(builder, clips, customs, layerControlType);
                }
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
                string moveControllerError = AssetDatabase.MoveAsset(buildPath, controllerPath);
                if (!string.IsNullOrEmpty(moveControllerError)) throw new InvalidOperationException($"Could not save the controller: {moveControllerError}");
                controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            }
            else
            {
                ReplaceControllerContent(building, controller);
                AssetDatabase.DeleteAsset(buildPath);
            }

            EditorUtility.SetDirty(controller);

            int removed = clips.DeleteUnused();
            if (removed > 0) Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} Removed {removed} clip(s) no longer used by the controller.");

            // --- Menu, parameters, prefab ---
            ScriptableObject menu = null;
            ScriptableObject customMenu = null;
            ScriptableObject parameters = null;
            string customMenuPath = PawlygonEditorUtils.CombineAssetPath(folder, "Menu - Face Tracking Extras Custom.asset");
            try
            {
                Texture2D tailJawIcon = EnsureTailJawIcon(folder, result.Warnings);
                var toggles = layerControlType != null ? customs.Toggles : new List<CustomToggle>();
                parameters = SaveOrReplace(CreateExpressionParameters(settings, toggles), PawlygonEditorUtils.CombineAssetPath(folder, "Parameters - Face Tracking Extras.asset"));
                menu = SaveOrReplace(CreateExpressionsMenu(settings, tailJawIcon), PawlygonEditorUtils.CombineAssetPath(folder, "Menu - Face Tracking Extras.asset"));

                if (toggles.Count > 0)
                {
                    if (toggles.Count > MaxMenuControls)
                    {
                        result.Warnings.Add($"{toggles.Count} custom animations have menu toggles, but a menu holds {MaxMenuControls}; only the first {MaxMenuControls} were added.");
                    }
                    customMenu = SaveOrReplace(CreateToggleMenu(toggles.Take(MaxMenuControls)), customMenuPath);
                }
                else if (AssetDatabase.LoadMainAssetAtPath(customMenuPath) != null)
                {
                    AssetDatabase.DeleteAsset(customMenuPath);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not create the expressions menu/parameters, so the Tail follows Jaw toggle has no menu entry: {ex.Message}");
            }

            AssetDatabase.SaveAssets();

            string prefabPath = GetPrefabPath(avatar);
            string prefabError = CreatePrefab(prefabPath, controller, menu, customMenu, parameters, settings);
            if (prefabError != null)
            {
                result.Warnings.Add(prefabError);
            }
            else
            {
                result.PrefabPath = prefabPath;
            }

            AssetDatabase.SaveAssets();

            Undo.RecordObject(profile, "Generate Face Tracking Extras");
            profile.lastGeneratedHash = profile.ComputeGenerationHash();
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);

            result.Success = true;
            result.Message = $"Generated {clips.Count} clips and {controller.layers.Length} layers in {folder}.";
            return result;
        }

        /// <summary>
        /// Another profile already living in <paramref name="folder"/>, which would mean two avatars share one
        /// output folder (non-prefab avatars with the same name). Null when the folder is free or already ours.
        /// </summary>
        private static FTExtrasProfile FindOtherProfileInFolder(FTExtrasProfile profile, string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return null;

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(FTExtrasProfile)}", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (PawlygonEditorUtils.NormalizeAssetPath(Path.GetDirectoryName(path)) != folder) continue;

                var other = AssetDatabase.LoadAssetAtPath<FTExtrasProfile>(path);
                if (other != null && other != profile) return other;
            }
            return null;
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
        /// Replaces <paramref name="target"/>'s layers, parameters and sub-assets with <paramref name="source"/>'s.
        /// The sub-assets (state machines, states, transitions, blend trees, behaviours) are moved rather than
        /// copied, so every reference between them stays intact, and the target keeps its GUID.
        /// </summary>
        private static void ReplaceControllerContent(AnimatorController source, AnimatorController target)
        {
            AnimatorControllerLayer[] layers = source.layers;
            AnimatorControllerParameter[] parameters = source.parameters;

            target.layers = Array.Empty<AnimatorControllerLayer>();
            target.parameters = Array.Empty<AnimatorControllerParameter>();
            foreach (UnityEngine.Object oldSubAsset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(target)))
            {
                if (oldSubAsset == null || oldSubAsset == target) continue;
                AssetDatabase.RemoveObjectFromAsset(oldSubAsset);
                UnityEngine.Object.DestroyImmediate(oldSubAsset, true);
            }

            foreach (UnityEngine.Object newSubAsset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(source)))
            {
                if (newSubAsset == null || newSubAsset == source) continue;
                AssetDatabase.RemoveObjectFromAsset(newSubAsset);
                AssetDatabase.AddObjectToAsset(newSubAsset, target);
            }

            target.parameters = parameters;
            target.layers = layers;
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
            builder.AddFloat(ParamTimer, 0f);
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
        private static void BuildInputsLayer(ControllerBuilder builder, ClipStore clips, FTExtrasGenerationSettings settings, CustomAnimationPlan customs)
        {
            AnimatorStateMachine sm = builder.AddLayer("Inputs", 1f, isFirstLayer: true);

            AnimatorState jawOn = sm.AddState("Inputs (Jaw On)", new Vector3(300f, 100f));
            jawOn.motion = InputsTree(builder, clips, settings, customs, includeJaw: true);
            jawOn.writeDefaultValues = true;

            AnimatorState jawOff = sm.AddState("Inputs (Jaw Off)", new Vector3(300f, 200f));
            jawOff.motion = InputsTree(builder, clips, settings, customs, includeJaw: false);
            jawOff.writeDefaultValues = true;

            sm.defaultState = jawOn;
            Transition(jawOn, jawOff, 0f).AddCondition(AnimatorConditionMode.IfNot, 0f, settings.tailFollowsJawParameter);
            Transition(jawOff, jawOn, 0f).AddCondition(AnimatorConditionMode.If, 0f, settings.tailFollowsJawParameter);
        }

        /// <summary>
        /// A Direct blend tree adds its children's values, so a zero clip at weight 1 keeps every output written
        /// and each gated input adds on top of it.
        /// </summary>
        private static BlendTree InputsTree(ControllerBuilder builder, ClipStore clips, FTExtrasGenerationSettings settings, CustomAnimationPlan customs, bool includeJaw)
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

            // Scrubbed custom animations: map the parameter's from..to range onto a 0..1 playback position.
            foreach (CustomAnimationSpec spec in customs.Animations.Where(a => a.ScrubParameter != null))
            {
                children.Add(Direct(PositionTree(builder, clips, spec), ParamOne));
            }

            root.children = children.ToArray();
            return root;
        }

        /// <summary>
        /// 1D tree mapping the Follow parameter's from..to range to a 0..1 playback position (clamped outside).
        /// </summary>
        private static BlendTree PositionTree(ControllerBuilder builder, ClipStore clips, CustomAnimationSpec spec)
        {
            FTExtrasCustomAnimation animation = spec.Animation;
            AnimationClip start = clips.Parameters($"Custom - {spec.SafeName} - Position 0", (spec.ScrubParameter, 0f));
            AnimationClip end = clips.Parameters($"Custom - {spec.SafeName} - Position 1", (spec.ScrubParameter, 1f));

            BlendTree tree = builder.NewTree($"{animation.name} position from {animation.followParameter}", BlendTreeType.Simple1D, animation.followParameter);
            AddSorted(tree, (animation.fromValue, start), (animation.toValue, end));
            return tree;
        }

        /// <summary>
        /// Adds 1D children in ascending threshold order, which Unity requires.
        /// </summary>
        private static void AddSorted(BlendTree tree, params (float Threshold, Motion Motion)[] children)
        {
            foreach (var child in children.OrderBy(c => c.Threshold))
            {
                tree.AddChild(child.Motion, child.Threshold);
            }
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

        // =====================================================================
        // Custom animations
        // =====================================================================

        private const string CustomMenuName = "Custom Animations";

        /// <summary>A menu toggle for one custom animation.</summary>
        private class CustomToggle
        {
            public string Label;
            public string Parameter;
            public bool DefaultOn;
        }

        /// <summary>One valid custom animation with its generated parameter names.</summary>
        private class CustomAnimationSpec
        {
            public FTExtrasCustomAnimation Animation;
            public string SafeName;
            public List<string> Gates = new List<string>();
            public string Toggle;
            public string ScrubParameter;
        }

        /// <summary>
        /// The enabled, valid custom animations of a profile, with unique parameter names, their tracking gates
        /// and the problems of the ones that had to be skipped.
        /// </summary>
        private class CustomAnimationPlan
        {
            public readonly List<CustomAnimationSpec> Animations = new List<CustomAnimationSpec>();
            public readonly List<string> Problems = new List<string>();
            public readonly List<CustomToggle> Toggles = new List<CustomToggle>();

            public CustomAnimationPlan(FTExtrasProfile profile, FTExtrasGenerationSettings settings)
            {
                var usedNames = new HashSet<string>();
                foreach (FTExtrasCustomAnimation animation in profile.customAnimations ?? new List<FTExtrasCustomAnimation>())
                {
                    if (animation == null || !animation.enabled) continue;

                    string problem = DescribeProblem(animation);
                    if (problem != null)
                    {
                        Problems.Add($"Custom animation '{animation.name}' was skipped: {problem}");
                        continue;
                    }

                    string safeName = UniqueName(SafeParameterName(animation.name), usedNames);
                    var spec = new CustomAnimationSpec { Animation = animation, SafeName = safeName };

                    switch (animation.gate)
                    {
                        case FTExtrasTrackingGate.EyeTracking: spec.Gates.Add(settings.eyeTrackingActive); break;
                        case FTExtrasTrackingGate.LipTracking: spec.Gates.Add(settings.lipTrackingActive); break;
                        case FTExtrasTrackingGate.Auto:
                            foreach (string parameter in animation.UsedParameters)
                            {
                                string gate = FTExtrasParameterCatalog.IsEyeParameter(parameter) ? settings.eyeTrackingActive : settings.lipTrackingActive;
                                if (!spec.Gates.Contains(gate)) spec.Gates.Add(gate);
                            }
                            break;
                    }

                    if (animation.menuToggle)
                    {
                        spec.Toggle = CustomParamPrefix + safeName;
                        Toggles.Add(new CustomToggle { Label = animation.name, Parameter = spec.Toggle, DefaultOn = animation.toggleDefaultOn });
                    }

                    if (animation.mode == FTExtrasCustomMode.Follow && animation.followStyle == FTExtrasFollowStyle.Scrub)
                    {
                        spec.ScrubParameter = CustomParamPrefix + safeName + "/Position";
                    }

                    Animations.Add(spec);
                }
            }

            public void AddParameters(ControllerBuilder builder)
            {
                foreach (CustomAnimationSpec spec in Animations)
                {
                    foreach (string parameter in spec.Animation.UsedParameters) builder.AddFloat(parameter, 0f);
                    foreach (string gate in spec.Gates) builder.AddFloat(gate, 0f);
                    if (spec.Toggle != null) builder.AddBool(spec.Toggle, spec.Animation.toggleDefaultOn);
                    if (spec.ScrubParameter != null) builder.AddFloat(spec.ScrubParameter, 0f);
                }
            }

            private static string DescribeProblem(FTExtrasCustomAnimation animation)
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

            private static string SafeParameterName(string name)
            {
                string cleaned = new string((name ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' ? c : '_').ToArray()).Trim();
                return string.IsNullOrEmpty(cleaned) ? "Custom Animation" : cleaned;
            }

            private static string UniqueName(string name, HashSet<string> used)
            {
                string unique = name;
                for (int i = 2; !used.Add(unique); i++) unique = $"{name} {i}";
                return unique;
            }
        }

        private static void BuildCustomAnimationLayers(ControllerBuilder builder, ClipStore clips, CustomAnimationPlan customs, Type layerControlType)
        {
            foreach (CustomAnimationSpec spec in customs.Animations)
            {
                clips.MarkUsed(spec.Animation.clip);
                clips.MarkUsed(spec.Animation.negativeClip);

                if (spec.Animation.mode == FTExtrasCustomMode.Follow) BuildFollowLayer(builder, clips, spec, layerControlType);
                else BuildTriggerLayer(builder, clips, spec, layerControlType);
            }
        }

        /// <summary>
        /// Follow a parameter. Off (layer weight 0) while tracking or the toggle is off, and for Fade In also while
        /// the value is at its resting "from" end, so the layer only takes over its properties while it is
        /// visible. Active plays a blend tree (Fade In, Two-sided) or the clip at a position (Scrub).
        /// </summary>
        private static void BuildFollowLayer(ControllerBuilder builder, ClipStore clips, CustomAnimationSpec spec, Type layerControlType)
        {
            FTExtrasCustomAnimation animation = spec.Animation;
            const float Blend = 0.1f;

            int layerIndex = builder.Controller.layers.Length;
            AnimatorStateMachine sm = builder.AddLayer($"Custom - {animation.name}", 0f);
            AnimationClip empty = clips.Empty("Custom - Off");

            AnimatorState active = sm.AddState("Active", new Vector3(300f, 200f));
            active.writeDefaultValues = true;
            switch (animation.followStyle)
            {
                case FTExtrasFollowStyle.FadeIn:
                {
                    BlendTree tree = builder.NewTree($"{animation.name} (fade in)", BlendTreeType.Simple1D, animation.followParameter);
                    AddSorted(tree, (animation.fromValue, empty), (animation.toValue, animation.clip));
                    active.motion = tree;
                    break;
                }
                case FTExtrasFollowStyle.TwoSided:
                {
                    BlendTree tree = builder.NewTree($"{animation.name} (two-sided)", BlendTreeType.Simple1D, animation.followParameter);
                    float middle = (animation.fromValue + animation.toValue) * 0.5f;
                    AddSorted(tree, (animation.fromValue, animation.negativeClip != null ? (Motion)animation.negativeClip : empty), (middle, empty), (animation.toValue, animation.clip));
                    active.motion = tree;
                    break;
                }
                case FTExtrasFollowStyle.Scrub:
                    active.motion = animation.clip;
                    active.timeParameterActive = true;
                    active.timeParameter = spec.ScrubParameter;
                    break;
            }
            AddLayerControl(active, layerControlType, layerIndex, 1f, Blend);

            bool fadeInRest = animation.followStyle == FTExtrasFollowStyle.FadeIn;
            if (spec.Gates.Count == 0 && spec.Toggle == null && !fadeInRest)
            {
                // Nothing turns it off: always active.
                sm.defaultState = active;
                return;
            }

            AnimatorState off = sm.AddState("Off", new Vector3(300f, 80f));
            off.motion = empty;
            off.writeDefaultValues = true;
            AddLayerControl(off, layerControlType, layerIndex, 0f, Blend);
            sm.defaultState = off;

            AnimatorStateTransition start = Transition(off, active, Blend);
            AddStartConditions(start, spec);

            if (fadeInRest)
            {
                // Leave the rest position by a small margin before taking over, and return a little closer to it,
                // so tracking noise at rest doesn't flicker the layer on and off.
                float direction = Mathf.Sign(animation.toValue - animation.fromValue);
                float span = Mathf.Abs(animation.toValue - animation.fromValue);
                float enter = animation.fromValue + direction * span * 0.02f;
                float leave = animation.fromValue + direction * span * 0.01f;
                start.AddCondition(direction > 0f ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less, enter, animation.followParameter);
                Transition(active, off, Blend).AddCondition(direction > 0f ? AnimatorConditionMode.Less : AnimatorConditionMode.Greater, leave, animation.followParameter);
            }

            AddStopTransitions(sm, off, spec, Blend);
        }

        /// <summary>
        /// Play when conditions are met: Waiting → Armed (hold timer) → Playing → Done (Play once, waits for the
        /// conditions to clear) → Cooldown → Waiting. Waiting, Done and Cooldown set the layer weight to 0, so the
        /// layer never overrides other animations while it isn't playing. Fades are the transition durations.
        /// </summary>
        private static void BuildTriggerLayer(ControllerBuilder builder, ClipStore clips, CustomAnimationSpec spec, Type layerControlType)
        {
            FTExtrasCustomAnimation animation = spec.Animation;
            float fadeIn = Mathf.Max(0f, animation.fadeIn);
            float fadeOut = Mathf.Max(0f, animation.fadeOut);

            int layerIndex = builder.Controller.layers.Length;
            AnimatorStateMachine sm = builder.AddLayer($"Custom - {animation.name}", 0f);
            AnimationClip empty = clips.Empty("Custom - Off");

            AnimatorState waiting = sm.AddState("Waiting", new Vector3(300f, 0f));
            waiting.motion = empty;
            AddLayerControl(waiting, layerControlType, layerIndex, 0f, fadeOut);
            sm.defaultState = waiting;

            AnimatorState armed = null;
            if (animation.holdSeconds > 0f)
            {
                armed = sm.AddState("Armed", new Vector3(300f, 120f));
                armed.motion = clips.Timer($"Custom - {spec.SafeName} - Hold", animation.holdSeconds, ParamTimer);
            }

            AnimatorState playing = sm.AddState("Playing", new Vector3(300f, 240f));
            playing.motion = PlayableClip(clips, spec);
            AddLayerControl(playing, layerControlType, layerIndex, 1f, fadeIn);

            AnimatorState cooldown = null;
            if (animation.cooldown > 0f)
            {
                cooldown = sm.AddState("Cooldown", new Vector3(600f, 120f));
                cooldown.motion = clips.Timer($"Custom - {spec.SafeName} - Cooldown", animation.cooldown, ParamTimer);
                AddLayerControl(cooldown, layerControlType, layerIndex, 0f, fadeOut);
                AnimatorStateTransition ready = Transition(cooldown, waiting, 0f);
                ready.hasExitTime = true;
                ready.exitTime = 1f;
            }
            AnimatorState after = cooldown ?? waiting;

            foreach (AnimatorState state in sm.states.Select(s => s.state)) state.writeDefaultValues = true;

            // Waiting → Armed / Playing when the conditions are met.
            AnimatorState first = armed ?? playing;
            float firstDuration = armed != null ? 0f : fadeIn;
            if (animation.combine == FTExtrasConditionCombine.All)
            {
                AnimatorStateTransition start = Transition(waiting, first, firstDuration);
                foreach (FTExtrasCondition condition in animation.conditions) AddMet(start, condition);
                AddStartConditions(start, spec);
            }
            else
            {
                foreach (FTExtrasCondition condition in animation.conditions)
                {
                    AnimatorStateTransition start = Transition(waiting, first, firstDuration);
                    AddMet(start, condition);
                    AddStartConditions(start, spec);
                }
            }

            if (armed != null)
            {
                // Armed → Waiting as soon as the conditions stop holding; Armed → Playing when the timer ends.
                if (animation.combine == FTExtrasConditionCombine.All)
                {
                    foreach (FTExtrasCondition condition in animation.conditions) AddNotMet(Transition(armed, waiting, 0f), condition);
                }
                else
                {
                    AnimatorStateTransition broken = Transition(armed, waiting, 0f);
                    foreach (FTExtrasCondition condition in animation.conditions) AddNotMet(broken, condition);
                }

                AnimatorStateTransition fire = Transition(armed, playing, fadeIn);
                fire.hasExitTime = true;
                fire.exitTime = 1f;
            }

            if (animation.play == FTExtrasTriggerPlay.PlayOnce)
            {
                AnimatorState done = sm.AddState("Done", new Vector3(600f, 240f));
                done.motion = empty;
                done.writeDefaultValues = true;
                AddLayerControl(done, layerControlType, layerIndex, 0f, fadeOut);

                AnimatorStateTransition finished = Transition(playing, done, fadeOut);
                finished.hasExitTime = true;
                finished.exitTime = 1f;

                AddReleaseTransitions(done, after, animation, 0f);
            }
            else
            {
                AddReleaseTransitions(playing, after, animation, fadeOut);
            }

            AddStopTransitions(sm, waiting, spec, fadeOut);
        }

        /// <summary>
        /// The clip as the play mode needs it: Loop while active needs a looping clip and Hold while active a
        /// non-looping one. When the user's clip has the other setting, a copy with the right setting is made in
        /// the Animations folder; the user's clip is never changed.
        /// </summary>
        private static AnimationClip PlayableClip(ClipStore clips, CustomAnimationSpec spec)
        {
            AnimationClip clip = spec.Animation.clip;
            switch (spec.Animation.play)
            {
                case FTExtrasTriggerPlay.LoopWhileActive when !clip.isLooping:
                    return clips.LoopVariant(clip, true, $"Custom - {spec.SafeName} (Loop)");
                case FTExtrasTriggerPlay.HoldWhileActive when clip.isLooping:
                    return clips.LoopVariant(clip, false, $"Custom - {spec.SafeName} (Once)");
                default:
                    return clip;
            }
        }

        private static void AddMet(AnimatorStateTransition transition, FTExtrasCondition condition)
        {
            transition.AddCondition(condition.comparison == FTExtrasComparison.Above ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less,
                condition.threshold, condition.parameter);
        }

        private static void AddNotMet(AnimatorStateTransition transition, FTExtrasCondition condition)
        {
            transition.AddCondition(condition.comparison == FTExtrasComparison.Above ? AnimatorConditionMode.Less : AnimatorConditionMode.Greater,
                condition.threshold, condition.parameter);
        }

        /// <summary>
        /// Transitions out of <paramref name="from"/> once the conditions are clearly over (past the release
        /// margin): All of → any one released (one transition each); Any of → all released (one transition).
        /// </summary>
        private static void AddReleaseTransitions(AnimatorState from, AnimatorState to, FTExtrasCustomAnimation animation, float duration)
        {
            float margin = Mathf.Max(0f, animation.releaseMargin);
            void AddReleased(AnimatorStateTransition transition, FTExtrasCondition condition)
            {
                bool above = condition.comparison == FTExtrasComparison.Above;
                transition.AddCondition(above ? AnimatorConditionMode.Less : AnimatorConditionMode.Greater,
                    above ? condition.threshold - margin : condition.threshold + margin, condition.parameter);
            }

            if (animation.combine == FTExtrasConditionCombine.All)
            {
                foreach (FTExtrasCondition condition in animation.conditions) AddReleased(Transition(from, to, duration), condition);
            }
            else
            {
                AnimatorStateTransition released = Transition(from, to, duration);
                foreach (FTExtrasCondition condition in animation.conditions) AddReleased(released, condition);
            }
        }

        /// <summary>Requires face tracking (the animation's gates) and its menu toggle to start.</summary>
        private static void AddStartConditions(AnimatorStateTransition transition, CustomAnimationSpec spec)
        {
            foreach (string gate in spec.Gates) transition.AddCondition(AnimatorConditionMode.Greater, 0.5f, gate);
            if (spec.Toggle != null) transition.AddCondition(AnimatorConditionMode.If, 0f, spec.Toggle);
        }

        /// <summary>Any State → <paramref name="off"/> when face tracking stops or the menu toggle is turned off.</summary>
        private static void AddStopTransitions(AnimatorStateMachine sm, AnimatorState off, CustomAnimationSpec spec, float duration)
        {
            foreach (string gate in spec.Gates)
            {
                AnimatorStateTransition stop = AnyStateTransition(sm, off);
                stop.duration = duration;
                stop.AddCondition(AnimatorConditionMode.Less, 0.5f, gate);
            }

            if (spec.Toggle != null)
            {
                AnimatorStateTransition stop = AnyStateTransition(sm, off);
                stop.duration = duration;
                stop.AddCondition(AnimatorConditionMode.IfNot, 0f, spec.Toggle);
            }
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

        /// <summary>
        /// Synced, saved bools: the Tail follows Jaw toggle plus one per custom animation with a menu toggle.
        /// </summary>
        private static ScriptableObject CreateExpressionParameters(FTExtrasGenerationSettings settings, IList<CustomToggle> customToggles)
        {
            Type paramsType = FindType("VRCExpressionParameters", typeof(ScriptableObject))
                ?? throw new InvalidOperationException("VRCExpressionParameters type not found");
            Type paramType = paramsType.GetNestedType("Parameter") ?? throw new InvalidOperationException("Parameter type not found");

            var bools = new List<(string Name, bool DefaultOn)> { (settings.tailFollowsJawParameter, true) };
            bools.AddRange(customToggles.Select(t => (t.Parameter, t.DefaultOn)));

            var asset = ScriptableObject.CreateInstance(paramsType);
            Array array = Array.CreateInstance(paramType, bools.Count);
            for (int i = 0; i < bools.Count; i++)
            {
                object parameter = Activator.CreateInstance(paramType);
                SetField(parameter, "name", bools[i].Name);
                SetField(parameter, "valueType", "Bool");
                SetField(parameter, "defaultValue", bools[i].DefaultOn ? 1f : 0f);
                SetField(parameter, "saved", true);
                SetField(parameter, "networkSynced", true);
                array.SetValue(parameter, i);
            }

            SetField(asset, "parameters", array);
            return asset;
        }

        /// <summary>
        /// A menu of on/off toggles for the custom animations.
        /// </summary>
        private static ScriptableObject CreateToggleMenu(IEnumerable<CustomToggle> toggles)
        {
            Type menuType = FindType("VRCExpressionsMenu", typeof(ScriptableObject))
                ?? throw new InvalidOperationException("VRCExpressionsMenu type not found");
            Type controlType = menuType.GetNestedType("Control") ?? throw new InvalidOperationException("Control type not found");
            Type controlParamType = controlType.GetNestedType("Parameter") ?? throw new InvalidOperationException("Control.Parameter type not found");

            var menu = ScriptableObject.CreateInstance(menuType);
            if (!(GetFieldValue(menu, "controls") is IList controls)) return menu;

            foreach (CustomToggle toggle in toggles)
            {
                object control = Activator.CreateInstance(controlType);
                SetField(control, "name", toggle.Label);
                SetField(control, "type", "Toggle");
                SetField(control, "value", 1f);

                object parameter = Activator.CreateInstance(controlParamType);
                SetField(parameter, "name", toggle.Parameter);
                SetField(control, "parameter", parameter);
                FillNullArrays(control);
                controls.Add(control);
            }
            return menu;
        }

        /// <summary>
        /// Copies the bundled Tail follows Jaw icon into the output folder, so the generated menu does not
        /// reference a file inside the package. An icon already in the folder is kept, which lets users swap
        /// in their own. Returns null (with a warning) if the bundled icon is missing.
        /// </summary>
        private static Texture2D EnsureTailJawIcon(string folder, List<string> warnings)
        {
            string target = PawlygonEditorUtils.CombineAssetPath(folder, TailJawIconFileName);
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(target);
            if (existing != null) return existing;

            string source = PawlygonEditorUtils.CombineAssetPath(PawlygonPackagePaths.GetPackageRootAssetPath(), BundledTailJawIconPath);
            if (!AssetDatabase.CopyAsset(source, target))
            {
                warnings.Add($"Could not copy the Tail follows Jaw icon from {source}; the menu toggle has no icon.");
                return null;
            }

            // VRChat menu icons are small; keep the copy light.
            if (AssetImporter.GetAtPath(target) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = MenuIconMaxSize;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(target);
        }

        private static ScriptableObject CreateExpressionsMenu(FTExtrasGenerationSettings settings, Texture2D icon)
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
            if (icon != null) SetField(control, "icon", icon);

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
        private static string CreatePrefab(string prefabPath, AnimatorController controller, ScriptableObject menu, ScriptableObject customMenu, ScriptableObject parameters, FTExtrasGenerationSettings settings)
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
                if (customMenu != null) RequireMethod(fcType, "AddMenu").Invoke(fullController, new object[] { customMenu, $"{settings.menuName}/{CustomMenuName}" });
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

            /// <summary>
            /// A clip that lasts <paramref name="seconds"/> and does nothing visible (it holds a helper parameter at
            /// 0), used to time states with exit time.
            /// </summary>
            public AnimationClip Timer(string name, float seconds, string parameter)
            {
                var clip = new AnimationClip();
                var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(Mathf.Max(StaticClipLength, seconds), 0f));
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), parameter), curve);
                return Save(name, clip);
            }

            /// <summary>A copy of a user clip with its loop setting changed; the original is left untouched.</summary>
            public AnimationClip LoopVariant(AnimationClip source, bool loop, string name)
            {
                AnimationClip copy = UnityEngine.Object.Instantiate(source);
                SetLoop(copy, loop);
                return Save(name, copy);
            }

            /// <summary>
            /// Keeps a user clip that happens to live in the Animations folder from being deleted as unused.
            /// </summary>
            public void MarkUsed(AnimationClip clip)
            {
                if (clip == null) return;
                string path = AssetDatabase.GetAssetPath(clip);
                if (!string.IsNullOrEmpty(path)) savedPaths.Add(path);
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
