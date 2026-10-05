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
    /// Static utility class containing all non-UI logic for FX gesture analysis and fix application.
    /// Extracted from <see cref="FXGestureChecker"/> to enable headless/programmatic usage and testing.
    /// </summary>
    internal static class FXGestureCheckerCore
    {
        // =====================================================================
        // Constants
        // =====================================================================

        internal const string DisabledParamName = "FacialExpressionsDisabled";
        internal const string GestureLeftParam = "GestureLeft";
        internal const string GestureRightParam = "GestureRight";
        internal const string EmptyStateName = "FacialExpressionsDisabled_Empty";
        internal const int AnimLayerTypeFX = 5;
        private const string LogPrefix = "[FXGestureCheckerCore]";

        // Blink detection constants
        internal const string EyeTrackingActiveParam = "EyeTrackingActive";
        internal const float EyeTrackingActiveThreshold = 0.5f;
        internal const string BlinkGuardStateName = "EyeTrackingActive_BlinkDisabled";

        // Confidence thresholds
        internal const int BlinkConfidenceHigh = 5;
        internal const int BlinkConfidenceMedium = 3;

        /// <summary>
        /// Strong blink keywords for layer names (case-insensitive).
        /// A match awards 3 points.
        /// </summary>
        private static readonly string[] BlinkLayerNameKeywordsStrong =
        {
            "blink", "blinking",
            "まばたき", "瞬き", "瞬目",
            "マバタキ"
        };

        /// <summary>
        /// Weak blink keywords for layer names (case-insensitive).
        /// A match awards 1 point.
        /// </summary>
        private static readonly string[] BlinkLayerNameKeywordsWeak =
        {
            "eye_close", "eyeclose", "eyes close", "eye close",
            "目閉じ", "目を閉じ"
        };

        /// <summary>
        /// Blendshape name keywords checked in animation clips (case-insensitive).
        /// A match awards 3 points per layer (not per clip/binding).
        /// </summary>
        private static readonly string[] BlinkBlendshapeKeywords =
        {
            // English
            "blink",
            "blink_l", "blink_r",
            "blink_left", "blink_right",
            "eye_close", "eyeclosed",
            "eyeclosedleft", "eyeclosedright",
            "eye_close_l", "eye_close_r",
            // Japanese
            "まばたき",
            "まばたき左", "まばたき右",
            "瞬き",
            "瞬き左", "瞬き右",
            "目閉じ",
            "目閉じ左", "目閉じ右",
            "目を閉じ",
            "目を閉じ左", "目を閉じ右",
            "ウィンク",
            "ウィンク左", "ウィンク右",
            "ウインク",
            "ウインク左", "ウインク右",
            "mabataki"
        };

        internal static readonly string[] GestureNames =
        {
            "Neutral",     // 0
            "Fist",        // 1
            "HandOpen",    // 2
            "FingerPoint", // 3
            "Victory",     // 4
            "RockNRoll",   // 5
            "HandGun",     // 6
            "ThumbsUp"     // 7
        };

        // =====================================================================
        // Data model
        // =====================================================================

        /// <summary>
        /// Holds the analysis results for a single animator controller layer,
        /// including all detected gesture transitions and layer-level guard status.
        /// </summary>
        internal class LayerAnalysis
        {
            public string LayerName;
            public int LayerIndex;

            /// <summary>Gesture transitions that enter an expression and can be guarded.</summary>
            public List<TransitionAnalysis> GestureTransitions = new List<TransitionAnalysis>();

            /// <summary>
            /// Gesture transitions that return to neutral (see <see cref="ReturnToNeutralRule"/>).
            /// Listed for information only: guarding them would freeze the face on the current
            /// expression, so they are never selected or guarded per transition.
            /// </summary>
            public List<TransitionAnalysis> NeutralTransitions = new List<TransitionAnalysis>();

            public bool SelectedForLayerDisable;

            /// <summary>
            /// True when the layer already has an up-to-date layer-level guard (nothing to apply).
            /// </summary>
            public bool AlreadyHasLayerGuard;

            /// <summary>
            /// True when the layer has a layer-level guard written by an older version (no way back
            /// out of the guard state, self-retriggering, or wrong Write Defaults). In that case
            /// <see cref="AlreadyHasLayerGuard"/> is false and applying the layer guard repairs the
            /// existing guard in place instead of adding a second one.
            /// </summary>
            public bool LayerGuardNeedsRepair;
        }

        /// <summary>
        /// Holds the analysis results for a single animator transition that references a gesture
        /// parameter (GestureLeft or GestureRight). The transition can be an AnyState or state
        /// transition (<see cref="AnimatorStateTransition"/>) or an Entry / sub-state machine
        /// transition (<see cref="AnimatorTransition"/>), anywhere in the layer's state machine tree.
        /// </summary>
        internal class TransitionAnalysis
        {
            /// <summary>
            /// Where the transition starts, qualified with its sub-state machine path, e.g.
            /// "AnyState", "Entry", "Idle", "Left Hand/Fist", "Left Hand/Entry" or
            /// "Left Hand/Exit" (a transition leaving the "Left Hand" sub-state machine).
            /// </summary>
            public string SourceName;

            /// <summary>Destination state or sub-state machine, path-qualified like <see cref="SourceName"/>.</summary>
            public string DestinationName;

            /// <summary>The first gesture parameter the transition tests.</summary>
            public string GestureParameter;

            /// <summary>The threshold of the first gesture condition, rounded to a gesture value.</summary>
            public int GestureValue;

            /// <summary>Readable form of all the transition's gesture conditions, e.g. "GestureLeft=Fist".</summary>
            public string ConditionLabel;

            /// <summary>
            /// Identifies the transition within its controller across re-analysis and controller
            /// copies (layer index, path-qualified source/destination, conditions and an occurrence
            /// counter, so two transitions with the same endpoints never share a key).
            /// </summary>
            public string Key;

            /// <summary>
            /// True when the transition returns to neutral (see <see cref="ReturnToNeutralRule"/>);
            /// such transitions are listed in <see cref="LayerAnalysis.NeutralTransitions"/>.
            /// </summary>
            public bool IsReturnToNeutral;

            public bool HasDisabledGuard;
            public bool SelectedForFix;
            public AnimatorTransitionBase TransitionRef;
        }

        /// <summary>
        /// Explains which transitions count as "return to neutral" and why they are not guarded.
        /// Shown as a tooltip in the UI.
        /// </summary>
        internal const string ReturnToNeutralRule =
            "A transition returns to neutral when it goes to the layer's default state or to Exit, " +
            "or when every gesture condition on it also passes with the hand in Neutral " +
            "(e.g. Gesture = Neutral, or Gesture != Fist when leaving Fist). " +
            "These are not guarded per transition: with FacialExpressionsDisabled on, a guarded way " +
            "back would keep the face stuck on the current expression. To clear an expression that " +
            "is already showing when FacialExpressionsDisabled turns on, use the layer guard.";

        /// <summary>
        /// Aggregated result of analyzing an FX controller, containing all layer analyses,
        /// a status message, and references to the source descriptor and controller.
        /// </summary>
        internal class AnalysisResult
        {
            public AnimatorController FXController;
            public List<LayerAnalysis> Layers;
            public List<BlinkLayerAnalysis> BlinkLayers;
            public string StatusMessage;
            public MessageType StatusMessageType;
            public bool Success;
            public Component Descriptor;
            public Type DescriptorType;
        }

        /// <summary>
        /// Confidence level for blink layer detection heuristics.
        /// </summary>
        internal enum BlinkConfidence
        {
            Low,
            Medium,
            High
        }

        /// <summary>
        /// Holds the analysis results for a single animator controller layer
        /// that was identified as a potential blink layer.
        /// </summary>
        internal class BlinkLayerAnalysis
        {
            public string LayerName;
            public int LayerIndex;
            public int ConfidenceScore;
            public BlinkConfidence Confidence;
            public bool SelectedForGuard;

            /// <summary>
            /// True when the layer already has an up-to-date blink guard (nothing to apply).
            /// </summary>
            public bool AlreadyHasBlinkGuard;

            /// <summary>
            /// True when the layer has a blink guard written by an older version (blinking never
            /// resumes after eye tracking turns off). In that case <see cref="AlreadyHasBlinkGuard"/>
            /// is false and applying the blink guard repairs the existing guard in place.
            /// </summary>
            public bool BlinkGuardNeedsRepair;

            public List<string> DetectionReasons = new List<string>();
        }

        // =====================================================================
        // Guard model
        // =====================================================================

        /// <summary>
        /// State of a guard (layer-level or blink) on a layer.
        /// </summary>
        private enum GuardStatus
        {
            /// <summary>No guard on the layer.</summary>
            None,
            /// <summary>A guard exists but was written by an older version and needs repair.</summary>
            Outdated,
            /// <summary>A complete, up-to-date guard exists.</summary>
            Current
        }

        /// <summary>
        /// Describes one kind of guard: an empty state entered from AnyState while
        /// <see cref="ParamName"/> satisfies the "active" condition, and left again (back to the
        /// layer's default state) once it satisfies the "clear" condition.
        /// The static specs below describe the guard for the parameter type this tool creates;
        /// <see cref="ResolveGuardSpec"/> adapts them when the controller already declares the
        /// parameter with another type.
        /// </summary>
        private sealed class GuardSpec
        {
            public string StateName;
            public string ParamName;

            /// <summary>The parameter's type the conditions below are written for.</summary>
            public AnimatorControllerParameterType ParamType;

            public AnimatorConditionMode ActiveMode;
            public float ActiveThreshold;
            public AnimatorConditionMode ClearMode;
            public float ClearThreshold;
            public Vector3 StatePosition;
            public string UndoName;

            /// <summary>
            /// AnyState transitions into this state are never gated by this guard's parameter.
            /// Used so the blink guard does not gate the layer guard: if both gated each other,
            /// neither could fire while both parameters are active.
            /// </summary>
            public string ExemptSiblingStateName;

            /// <summary>
            /// Returns a copy of this spec whose conditions are written for a parameter of
            /// <paramref name="type"/>.
            /// </summary>
            public GuardSpec WithConditions(AnimatorControllerParameterType type,
                AnimatorConditionMode activeMode, float activeThreshold,
                AnimatorConditionMode clearMode, float clearThreshold)
            {
                GuardSpec copy = (GuardSpec)MemberwiseClone();
                copy.ParamType = type;
                copy.ActiveMode = activeMode;
                copy.ActiveThreshold = activeThreshold;
                copy.ClearMode = clearMode;
                copy.ClearThreshold = clearThreshold;
                return copy;
            }
        }

        private static readonly GuardSpec LayerGuardSpec = new GuardSpec
        {
            StateName = EmptyStateName,
            ParamName = DisabledParamName,
            ParamType = AnimatorControllerParameterType.Bool,
            ActiveMode = AnimatorConditionMode.If,
            ActiveThreshold = 0f,
            ClearMode = AnimatorConditionMode.IfNot,
            ClearThreshold = 0f,
            StatePosition = new Vector3(30f, -80f, 0f),
            UndoName = "Add layer-level FacialExpressionsDisabled guard",
            ExemptSiblingStateName = null
        };

        private static readonly GuardSpec BlinkGuardSpec = new GuardSpec
        {
            StateName = BlinkGuardStateName,
            ParamName = EyeTrackingActiveParam,
            ParamType = AnimatorControllerParameterType.Float,
            ActiveMode = AnimatorConditionMode.Greater,
            ActiveThreshold = EyeTrackingActiveThreshold,
            ClearMode = AnimatorConditionMode.Less,
            ClearThreshold = EyeTrackingActiveThreshold,
            StatePosition = new Vector3(30f, -140f, 0f),
            UndoName = "Add blink layer EyeTrackingActive guard",
            ExemptSiblingStateName = EmptyStateName
        };

        // =====================================================================
        // Guard parameter types
        // =====================================================================

        /// <summary>
        /// Returns the guard spec to use with <paramref name="controller"/>. When the controller
        /// already declares the guard parameter with a different type, the conditions are adapted
        /// to that type (Bool: If / IfNot; Float: Greater / Less 0.5; Int: Greater 0 / Less 1),
        /// because Unity ignores conditions whose mode does not fit the parameter's type.
        /// Returns null when the existing parameter is a Trigger, which cannot express the guard.
        /// </summary>
        private static GuardSpec ResolveGuardSpec(AnimatorController controller, GuardSpec spec)
        {
            AnimatorControllerParameter existing = FindParameter(controller, spec.ParamName);
            if (existing == null || existing.type == spec.ParamType) return spec;

            switch (existing.type)
            {
                case AnimatorControllerParameterType.Bool:
                    return spec.WithConditions(AnimatorControllerParameterType.Bool,
                        AnimatorConditionMode.If, 0f, AnimatorConditionMode.IfNot, 0f);
                case AnimatorControllerParameterType.Float:
                    return spec.WithConditions(AnimatorControllerParameterType.Float,
                        AnimatorConditionMode.Greater, EyeTrackingActiveThreshold,
                        AnimatorConditionMode.Less, EyeTrackingActiveThreshold);
                case AnimatorControllerParameterType.Int:
                    return spec.WithConditions(AnimatorControllerParameterType.Int,
                        AnimatorConditionMode.Greater, 0f, AnimatorConditionMode.Less, 1f);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Like <see cref="ResolveGuardSpec"/>, but falls back to the default spec when the
        /// parameter type is unusable, so analysis can still find and classify existing guards.
        /// </summary>
        private static GuardSpec ResolveGuardSpecForAnalysis(AnimatorController controller, GuardSpec spec)
        {
            return ResolveGuardSpec(controller, spec) ?? spec;
        }

        private static AnimatorControllerParameter FindParameter(AnimatorController controller, string name)
        {
            if (controller == null) return null;

            foreach (AnimatorControllerParameter parameter in controller.parameters)
            {
                if (parameter.name == name) return parameter;
            }

            return null;
        }

        /// <summary>
        /// Returns why the FacialExpressionsDisabled guards (transition and layer guards) cannot
        /// be applied to <paramref name="controller"/>, or null when they can.
        /// </summary>
        internal static string GetLayerGuardParameterError(AnimatorController controller)
        {
            return GetGuardParameterError(controller, LayerGuardSpec);
        }

        /// <summary>
        /// Returns why the EyeTrackingActive blink guards cannot be applied to
        /// <paramref name="controller"/>, or null when they can.
        /// </summary>
        internal static string GetBlinkGuardParameterError(AnimatorController controller)
        {
            return GetGuardParameterError(controller, BlinkGuardSpec);
        }

        private static string GetGuardParameterError(AnimatorController controller, GuardSpec spec)
        {
            if (controller == null || ResolveGuardSpec(controller, spec) != null) return null;

            AnimatorControllerParameter existing = FindParameter(controller, spec.ParamName);
            return $"The FX controller already has a '{spec.ParamName}' parameter of type {existing.type}, " +
                   $"which cannot be used for the guard. Change its type to {spec.ParamType} (or Bool, Int or Float) " +
                   "in the Animator window's Parameters tab, then analyze again.";
        }

        /// <summary>
        /// Describes how the guard conditions were adapted to an existing parameter of another
        /// type, or returns null when the parameter is missing or has the expected type.
        /// </summary>
        private static string GetGuardParameterNote(AnimatorController controller, GuardSpec spec)
        {
            GuardSpec resolved = ResolveGuardSpec(controller, spec);
            if (resolved == null || resolved == spec) return null;

            return $"'{spec.ParamName}' already exists as {resolved.ParamType}; its guards use " +
                   $"{DescribeCondition(resolved.ActiveMode, resolved.ActiveThreshold)} / " +
                   $"{DescribeCondition(resolved.ClearMode, resolved.ClearThreshold)} conditions.";
        }

        private static string DescribeCondition(AnimatorConditionMode mode, float threshold)
        {
            switch (mode)
            {
                case AnimatorConditionMode.If: return "true";
                case AnimatorConditionMode.IfNot: return "false";
                case AnimatorConditionMode.Greater: return $"> {threshold:0.##}";
                case AnimatorConditionMode.Less: return $"< {threshold:0.##}";
                case AnimatorConditionMode.Equals: return $"== {threshold:0.##}";
                case AnimatorConditionMode.NotEqual: return $"!= {threshold:0.##}";
                default: return mode.ToString();
            }
        }

        /// <summary>
        /// Checks whether Unity can evaluate a condition mode on a parameter of the given type.
        /// </summary>
        private static bool IsModeValidForType(AnimatorConditionMode mode, AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                    return mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;
                case AnimatorControllerParameterType.Trigger:
                    return mode == AnimatorConditionMode.If;
                case AnimatorControllerParameterType.Float:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less;
                case AnimatorControllerParameterType.Int:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less ||
                           mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
                default:
                    return false;
            }
        }

        // =====================================================================
        // Reflection helpers
        // =====================================================================

        /// <summary>
        /// Extracts the FX AnimatorController from a VRCAvatarDescriptor component using reflection.
        /// Reads baseAnimationLayers array and finds the entry where type == AnimLayerType.FX (5).
        /// Returns null when the FX layer uses the default controller, has none, or uses an
        /// <see cref="AnimatorOverrideController"/> (see <see cref="GetFXRuntimeController"/>).
        /// </summary>
        internal static AnimatorController GetFXController(Component descriptor, Type descriptorType)
        {
            return GetFXRuntimeController(descriptor, descriptorType) as AnimatorController;
        }

        /// <summary>
        /// Returns the custom controller assigned to the FX layer of a VRCAvatarDescriptor (an
        /// <see cref="AnimatorController"/> or an <see cref="AnimatorOverrideController"/>), or
        /// null when the FX layer uses the SDK default or has no controller.
        /// </summary>
        internal static RuntimeAnimatorController GetFXRuntimeController(Component descriptor, Type descriptorType)
        {
            // VRCAvatarDescriptor has a field: CustomAnimLayer[] baseAnimationLayers
            FieldInfo layersField = descriptorType.GetField("baseAnimationLayers",
                BindingFlags.Public | BindingFlags.Instance);

            if (layersField == null)
            {
                Debug.LogWarning($"{LogPrefix} Could not find 'baseAnimationLayers' field on VRCAvatarDescriptor. SDK version may differ.");
                return null;
            }

            object layersValue = layersField.GetValue(descriptor);
            if (layersValue == null) return null;

            // baseAnimationLayers is an array of CustomAnimLayer structs
            Array layersArray = layersValue as Array;
            if (layersArray == null) return null;

            Type elementType = layersArray.GetType().GetElementType();
            if (elementType == null) return null;

            FieldInfo typeField = elementType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo controllerField = elementType.GetField("animatorController", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo isDefaultField = elementType.GetField("isDefault", BindingFlags.Public | BindingFlags.Instance);

            if (typeField == null || controllerField == null)
            {
                Debug.LogWarning($"{LogPrefix} Could not find expected fields on CustomAnimLayer. SDK version may differ.");
                return null;
            }

            foreach (object layerEntry in layersArray)
            {
                object typeValue = typeField.GetValue(layerEntry);
                int layerType = Convert.ToInt32(typeValue);

                if (layerType != AnimLayerTypeFX) continue;

                // Check if using default
                if (isDefaultField != null)
                {
                    object isDefault = isDefaultField.GetValue(layerEntry);
                    if (isDefault is bool isDefaultBool && isDefaultBool)
                    {
                        return null; // Using default FX layer, no custom controller
                    }
                }

                object controllerValue = controllerField.GetValue(layerEntry);
                return controllerValue as RuntimeAnimatorController;
            }

            return null;
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        /// <summary>
        /// Analyzes the FX controller on a scene avatar GameObject. Validates the avatar,
        /// locates the VRCAvatarDescriptor via reflection, extracts the FX controller,
        /// and delegates to <see cref="Analyze(AnimatorController)"/>.
        /// </summary>
        /// <param name="avatar">The root GameObject of the avatar in the scene.</param>
        /// <returns>An <see cref="AnalysisResult"/> containing layer analyses and status information.</returns>
        internal static AnalysisResult Analyze(GameObject avatar)
        {
            if (avatar == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = "Select a GameObject in the scene that has a VRCAvatarDescriptor.",
                    StatusMessageType = MessageType.Warning
                };
            }

            Type descriptorType = PawlygonEditorUtils.FindVRCAvatarDescriptorType();
            if (descriptorType == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = "VRChat SDK not detected. Install the VRChat Avatars SDK to use this tool.",
                    StatusMessageType = MessageType.Error
                };
            }

            Component descriptor = avatar.GetComponent(descriptorType);
            if (descriptor == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = $"No VRCAvatarDescriptor found on '{avatar.name}'.",
                    StatusMessageType = MessageType.Warning
                };
            }

            RuntimeAnimatorController runtimeController = GetFXRuntimeController(descriptor, descriptorType);
            if (runtimeController is AnimatorOverrideController overrideController)
            {
                // Guards live in the state machine, which an override controller borrows from its
                // base controller. Writing to the base would change every avatar using it, and
                // replacing the override with a copy of the base would drop the clip overrides.
                RuntimeAnimatorController baseController = overrideController.runtimeAnimatorController;
                string baseName = baseController != null ? $" (based on '{baseController.name}')" : string.Empty;
                return new AnalysisResult
                {
                    Success = false,
                    Descriptor = descriptor,
                    DescriptorType = descriptorType,
                    StatusMessage = $"The FX layer uses the Animator Override Controller '{overrideController.name}'{baseName}. " +
                                    "Guards are part of the state machine, which the override takes from its base controller, " +
                                    "so they cannot be added through the override. To guard it, assign the base controller " +
                                    "to the FX layer temporarily, apply the guards with 'Work on a copy' off, then assign " +
                                    "the override again (this changes every avatar that uses the base controller).",
                    StatusMessageType = MessageType.Warning
                };
            }

            AnimatorController controller = runtimeController as AnimatorController;
            if (controller == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    Descriptor = descriptor,
                    DescriptorType = descriptorType,
                    StatusMessage = "No custom FX controller assigned on the VRCAvatarDescriptor.",
                    StatusMessageType = MessageType.Warning
                };
            }

            AnalysisResult result = Analyze(controller);
            result.Descriptor = descriptor;
            result.DescriptorType = descriptorType;
            return result;
        }

        /// <summary>
        /// Analyzes an AnimatorController directly, iterating its layers to find
        /// gesture-based facial expression transitions (GestureLeft/GestureRight).
        /// </summary>
        /// <param name="controller">The AnimatorController to analyze.</param>
        /// <returns>An <see cref="AnalysisResult"/> with Success=true and layer analyses populated.</returns>
        internal static AnalysisResult Analyze(AnimatorController controller)
        {
            AnalysisResult result = new AnalysisResult
            {
                FXController = controller,
                Success = true
            };

            List<LayerAnalysis> analysisResults = new List<LayerAnalysis>();
            AnimatorControllerLayer[] controllerLayers = controller.layers;
            GuardSpec layerSpec = ResolveGuardSpecForAnalysis(controller, LayerGuardSpec);

            for (int i = 0; i < controllerLayers.Length; i++)
            {
                AnimatorControllerLayer layer = controllerLayers[i];
                LayerAnalysis layerAnalysis = AnalyzeLayer(layer, i, layerSpec);

                // A layer whose gesture transitions all return to neutral is still listed, so its
                // layer guard can be applied.
                if (layerAnalysis.GestureTransitions.Count > 0 || layerAnalysis.NeutralTransitions.Count > 0)
                {
                    analysisResults.Add(layerAnalysis);
                }
            }

            result.Layers = analysisResults;
            result.BlinkLayers = AnalyzeBlinkLayers(controller);

            if (analysisResults.Count == 0)
            {
                result.StatusMessage = $"No gesture-based facial expression transitions found in '{controller.name}'.";
                result.StatusMessageType = MessageType.Info;
            }
            else
            {
                int totalTransitions = analysisResults.Sum(l => l.GestureTransitions.Count);
                int guardedTransitions = analysisResults.Sum(l => l.GestureTransitions.Count(t => t.HasDisabledGuard));
                int guardedLayers = analysisResults.Count(l => l.AlreadyHasLayerGuard);
                int neutralTransitions = analysisResults.Sum(l => l.NeutralTransitions.Count);
                result.StatusMessage = $"Found {totalTransitions} gesture transition(s) across {analysisResults.Count} layer(s). " +
                                       $"{guardedTransitions} transition(s) and {guardedLayers} layer(s) already guarded.";
                if (neutralTransitions > 0)
                {
                    result.StatusMessage += $" {neutralTransitions} return-to-neutral transition(s) are left unguarded on purpose.";
                }
                result.StatusMessageType = MessageType.Info;
            }

            int outdatedGuards = analysisResults.Count(l => l.LayerGuardNeedsRepair) +
                                 result.BlinkLayers.Count(b => b.BlinkGuardNeedsRepair);
            if (outdatedGuards > 0)
            {
                result.StatusMessage += $" {outdatedGuards} guard(s) from an older version need repair and are pre-selected.";
                result.StatusMessageType = MessageType.Warning;
            }

            foreach (GuardSpec spec in new[] { LayerGuardSpec, BlinkGuardSpec })
            {
                string parameterError = GetGuardParameterError(controller, spec);
                if (parameterError != null)
                {
                    result.StatusMessage += " " + parameterError;
                    result.StatusMessageType = MessageType.Error;
                    continue;
                }

                string parameterNote = GetGuardParameterNote(controller, spec);
                if (parameterNote != null)
                {
                    result.StatusMessage += " " + parameterNote;
                }
            }

            return result;
        }

        /// <summary>
        /// State shared while walking one layer's state machine tree.
        /// </summary>
        private sealed class LayerWalk
        {
            public LayerAnalysis Analysis;

            /// <summary>Path-qualified display names of every state and sub-state machine.</summary>
            public Dictionary<UnityEngine.Object, string> Names;

            /// <summary>The layer's default state (the root state machine's default state).</summary>
            public AnimatorState DefaultState;

            /// <summary>The layer guard spec resolved for the controller's parameter type.</summary>
            public GuardSpec LayerSpec;

            public HashSet<AnimatorStateMachine> Visited = new HashSet<AnimatorStateMachine>();
        }

        /// <summary>
        /// Analyzes a single animator controller layer for gesture transitions. Walks the whole
        /// state machine tree: the root's AnyState transitions (Unity only evaluates AnyState
        /// transitions on the root state machine), and for the root and every nested sub-state
        /// machine its Entry transitions, its states' transitions and the transitions that leave
        /// its child sub-state machines.
        /// </summary>
        private static LayerAnalysis AnalyzeLayer(AnimatorControllerLayer layer, int layerIndex, GuardSpec layerSpec)
        {
            GuardStatus guardStatus = GetGuardStatus(layer.stateMachine, layerSpec);

            LayerAnalysis analysis = new LayerAnalysis
            {
                LayerName = layer.name,
                LayerIndex = layerIndex,
                AlreadyHasLayerGuard = guardStatus == GuardStatus.Current,
                LayerGuardNeedsRepair = guardStatus == GuardStatus.Outdated,
                // The user opted into this guard in the past; pre-select the repair.
                SelectedForLayerDisable = guardStatus == GuardStatus.Outdated
            };

            AnimatorStateMachine stateMachine = layer.stateMachine;
            if (stateMachine == null) return analysis;

            LayerWalk walk = new LayerWalk
            {
                Analysis = analysis,
                Names = BuildPathNames(stateMachine),
                DefaultState = stateMachine.defaultState,
                LayerSpec = layerSpec
            };

            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                AnalyzeTransition(transition, "AnyState", walk);
            }

            AnalyzeStateMachine(stateMachine, string.Empty, walk);
            AssignTransitionKeys(analysis);

            return analysis;
        }

        /// <summary>
        /// Analyzes the Entry transitions, state transitions and sub-state machine transitions of
        /// one state machine, then recurses into its child sub-state machines.
        /// </summary>
        /// <param name="path">The state machine's path ("" for the layer's root state machine).</param>
        private static void AnalyzeStateMachine(AnimatorStateMachine stateMachine, string path, LayerWalk walk)
        {
            if (stateMachine == null || !walk.Visited.Add(stateMachine)) return;

            string entryName = path.Length > 0 ? path + "/Entry" : "Entry";
            foreach (AnimatorTransition transition in stateMachine.entryTransitions)
            {
                AnalyzeTransition(transition, entryName, walk);
            }

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                AnimatorState state = childState.state;
                if (state == null) continue;

                string stateName = GetPathName(walk.Names, state);
                foreach (AnimatorStateTransition transition in state.transitions)
                {
                    AnalyzeTransition(transition, stateName, walk);
                }
            }

            foreach (ChildAnimatorStateMachine childMachine in stateMachine.stateMachines)
            {
                AnimatorStateMachine child = childMachine.stateMachine;
                if (child == null) continue;

                string childPath = GetPathName(walk.Names, child);

                // Transitions out of a sub-state machine live on its parent and fire once the
                // sub-state machine reaches its Exit node.
                foreach (AnimatorTransition transition in stateMachine.GetStateMachineTransitions(child))
                {
                    AnalyzeTransition(transition, childPath + "/Exit", walk);
                }

                AnalyzeStateMachine(child, childPath, walk);
            }
        }

        /// <summary>
        /// Analyzes a single transition for gesture parameter conditions and records findings.
        /// </summary>
        private static void AnalyzeTransition(AnimatorTransitionBase transition, string sourceName, LayerWalk walk)
        {
            if (transition == null) return;

            List<AnimatorCondition> gestureConditions = new List<AnimatorCondition>();
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == GestureLeftParam || condition.parameter == GestureRightParam)
                {
                    gestureConditions.Add(condition);
                }
            }

            if (gestureConditions.Count == 0) return;

            string destinationName = transition.destinationState != null
                ? GetPathName(walk.Names, transition.destinationState)
                : transition.destinationStateMachine != null
                    ? GetPathName(walk.Names, transition.destinationStateMachine)
                    : "(exit)";

            bool returnsToNeutral = IsReturnToNeutral(transition, gestureConditions, walk.DefaultState);

            // Listed once per transition even if it has both GestureLeft and GestureRight conditions
            TransitionAnalysis analysis = new TransitionAnalysis
            {
                SourceName = sourceName,
                DestinationName = destinationName,
                GestureParameter = gestureConditions[0].parameter,
                GestureValue = Mathf.RoundToInt(gestureConditions[0].threshold),
                ConditionLabel = string.Join(" & ", gestureConditions.Select(FormatGestureCondition)),
                IsReturnToNeutral = returnsToNeutral,
                HasDisabledGuard = HasGuardCondition(transition, walk.LayerSpec),
                SelectedForFix = false,
                TransitionRef = transition
            };

            if (returnsToNeutral)
            {
                walk.Analysis.NeutralTransitions.Add(analysis);
            }
            else
            {
                walk.Analysis.GestureTransitions.Add(analysis);
            }
        }

        /// <summary>
        /// Applies <see cref="ReturnToNeutralRule"/>: the transition goes to the layer's default
        /// state or to Exit, or every gesture condition on it also passes for gesture 0 (Neutral).
        /// </summary>
        private static bool IsReturnToNeutral(AnimatorTransitionBase transition,
            List<AnimatorCondition> gestureConditions, AnimatorState layerDefaultState)
        {
            if (transition.isExit) return true;
            if (layerDefaultState != null && transition.destinationState == layerDefaultState) return true;

            return gestureConditions.All(AdmitsNeutralGesture);
        }

        /// <summary>
        /// Checks whether a gesture condition passes when the gesture is 0 (Neutral).
        /// </summary>
        private static bool AdmitsNeutralGesture(AnimatorCondition condition)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.Equals: return Mathf.RoundToInt(condition.threshold) == 0;
                case AnimatorConditionMode.NotEqual: return Mathf.RoundToInt(condition.threshold) != 0;
                case AnimatorConditionMode.Greater: return condition.threshold < 0f;
                case AnimatorConditionMode.Less: return condition.threshold > 0f;
                default: return false;
            }
        }

        /// <summary>
        /// Formats a gesture condition for display, e.g. "GestureLeft=Fist" or "GestureRight!=Victory".
        /// </summary>
        private static string FormatGestureCondition(AnimatorCondition condition)
        {
            string gesture = GetGestureName(Mathf.RoundToInt(condition.threshold));
            switch (condition.mode)
            {
                case AnimatorConditionMode.Equals: return $"{condition.parameter}={gesture}";
                case AnimatorConditionMode.NotEqual: return $"{condition.parameter}!={gesture}";
                case AnimatorConditionMode.Greater: return $"{condition.parameter}>{condition.threshold:0.##}";
                case AnimatorConditionMode.Less: return $"{condition.parameter}<{condition.threshold:0.##}";
                default: return $"{condition.parameter} {condition.mode}";
            }
        }

        /// <summary>
        /// Gives every transition of the layer a <see cref="TransitionAnalysis.Key"/>. Transitions
        /// with identical source, destination and conditions are told apart by their order of
        /// appearance, which is the same in a copy of the controller.
        /// </summary>
        private static void AssignTransitionKeys(LayerAnalysis analysis)
        {
            Dictionary<string, int> occurrences = new Dictionary<string, int>();

            foreach (TransitionAnalysis transition in analysis.GestureTransitions.Concat(analysis.NeutralTransitions))
            {
                string baseKey = $"{analysis.LayerIndex}:{transition.SourceName}->{transition.DestinationName}:{transition.ConditionLabel}";
                occurrences.TryGetValue(baseKey, out int count);
                occurrences[baseKey] = count + 1;
                transition.Key = $"{baseKey}#{count}";
            }
        }

        /// <summary>
        /// Maps every state and sub-state machine under <paramref name="root"/> to its path-qualified
        /// display name ("State" at the root, "Sub/State" and "Sub/Nested" below it).
        /// </summary>
        private static Dictionary<UnityEngine.Object, string> BuildPathNames(AnimatorStateMachine root)
        {
            Dictionary<UnityEngine.Object, string> names = new Dictionary<UnityEngine.Object, string>();
            AddPathNames(root, string.Empty, names);
            return names;
        }

        private static void AddPathNames(AnimatorStateMachine stateMachine, string prefix,
            Dictionary<UnityEngine.Object, string> names)
        {
            if (stateMachine == null) return;

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                if (childState.state != null && !names.ContainsKey(childState.state))
                {
                    names[childState.state] = prefix + childState.state.name;
                }
            }

            foreach (ChildAnimatorStateMachine childMachine in stateMachine.stateMachines)
            {
                AnimatorStateMachine child = childMachine.stateMachine;
                if (child == null || names.ContainsKey(child)) continue;

                string path = prefix + child.name;
                names[child] = path;
                AddPathNames(child, path + "/", names);
            }
        }

        private static string GetPathName(Dictionary<UnityEngine.Object, string> names, UnityEngine.Object target)
        {
            return names.TryGetValue(target, out string name) ? name : target.name;
        }

        /// <summary>
        /// Returns every state in a state machine and its nested sub-state machines.
        /// </summary>
        private static List<AnimatorState> GetAllStates(AnimatorStateMachine stateMachine)
        {
            List<AnimatorState> states = new List<AnimatorState>();
            CollectStates(stateMachine, states, new HashSet<AnimatorStateMachine>());
            return states;
        }

        private static void CollectStates(AnimatorStateMachine stateMachine, List<AnimatorState> states,
            HashSet<AnimatorStateMachine> visited)
        {
            if (stateMachine == null || !visited.Add(stateMachine)) return;

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                if (childState.state != null) states.Add(childState.state);
            }

            foreach (ChildAnimatorStateMachine childMachine in stateMachine.stateMachines)
            {
                CollectStates(childMachine.stateMachine, states, visited);
            }
        }

        // =====================================================================
        // Guard conditions
        // =====================================================================

        /// <summary>
        /// Checks whether a transition already has a usable condition on the guard parameter
        /// (one whose mode fits the parameter's type; Unity ignores the others).
        /// </summary>
        private static bool HasGuardCondition(AnimatorTransitionBase transition, GuardSpec spec)
        {
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == spec.ParamName && IsModeValidForType(condition.mode, spec.ParamType))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a transition has a condition on the guard parameter whose mode does not
        /// fit the parameter's type (e.g. If/IfNot written for a Bool while the parameter is a Float).
        /// </summary>
        private static bool HasInvalidGuardCondition(AnimatorTransitionBase transition, GuardSpec spec)
        {
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == spec.ParamName && !IsModeValidForType(condition.mode, spec.ParamType))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCondition(AnimatorTransitionBase transition, string parameter, AnimatorConditionMode mode)
        {
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == parameter && condition.mode == mode) return true;
            }

            return false;
        }

        /// <summary>
        /// Makes sure <paramref name="transition"/> cannot fire while the guard is active: drops
        /// conditions on the guard parameter whose mode does not fit its type, then adds the
        /// guard's "clear" condition unless a usable condition on the parameter remains.
        /// </summary>
        /// <returns>True when the transition was changed.</returns>
        private static bool EnsureClearCondition(AnimatorTransitionBase transition, GuardSpec spec, string undoName)
        {
            bool hasValid = HasGuardCondition(transition, spec);
            bool hasInvalid = HasInvalidGuardCondition(transition, spec);
            if (hasValid && !hasInvalid) return false;

            Undo.RecordObject(transition, undoName);

            List<AnimatorCondition> conditions = transition.conditions
                .Where(c => c.parameter != spec.ParamName || IsModeValidForType(c.mode, spec.ParamType))
                .ToList();

            if (!hasValid)
            {
                conditions.Add(new AnimatorCondition
                {
                    mode = spec.ClearMode,
                    parameter = spec.ParamName,
                    threshold = spec.ClearThreshold
                });
            }

            transition.conditions = conditions.ToArray();
            EditorUtility.SetDirty(transition);
            return true;
        }

        /// <summary>
        /// Replaces every condition on the guard parameter with a single
        /// <paramref name="mode"/>/<paramref name="threshold"/> condition. Used on the guard's own
        /// transitions so they always test the parameter in a way its type supports.
        /// </summary>
        private static void SetGuardCondition(AnimatorTransitionBase transition, GuardSpec spec,
            AnimatorConditionMode mode, float threshold, string undoName)
        {
            Undo.RecordObject(transition, undoName);

            List<AnimatorCondition> conditions = transition.conditions
                .Where(c => c.parameter != spec.ParamName)
                .ToList();
            conditions.Add(new AnimatorCondition { mode = mode, parameter = spec.ParamName, threshold = threshold });

            transition.conditions = conditions.ToArray();
            EditorUtility.SetDirty(transition);
        }

        // =====================================================================
        // Guard detection (shared by the layer guard and the blink guard)
        // =====================================================================

        /// <summary>
        /// Finds an existing guard: an AnyState transition into a state named
        /// <see cref="GuardSpec.StateName"/> that has a condition on <see cref="GuardSpec.ParamName"/>.
        /// </summary>
        /// <returns>The guard state, or null when the layer has no guard.</returns>
        private static AnimatorState FindGuard(AnimatorStateMachine stateMachine, GuardSpec spec,
            out AnimatorStateTransition guardTransition)
        {
            guardTransition = null;
            if (stateMachine == null) return null;

            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                if (transition == null || transition.destinationState == null ||
                    transition.destinationState.name != spec.StateName)
                {
                    continue;
                }

                if (HasConditionOn(transition, spec.ParamName))
                {
                    guardTransition = transition;
                    return transition.destinationState;
                }
            }

            return null;
        }

        /// <summary>
        /// Classifies the guard described by <paramref name="spec"/> on a layer. A guard is
        /// <see cref="GuardStatus.Outdated"/> when it was written by an older version of this tool:
        /// its AnyState transition can re-enter itself, the guard state has no exit transition back
        /// once the parameter clears, its Write Defaults differs from the layer's dominant setting,
        /// another AnyState transition could still pull the layer out of the guard state, or its
        /// conditions do not fit the parameter's current type, or (on a Write Defaults off layer)
        /// its reset clip is missing or no longer covers what the layer animates.
        /// </summary>
        /// <param name="spec">The guard spec, resolved for the controller's parameter type.</param>
        private static GuardStatus GetGuardStatus(AnimatorStateMachine stateMachine, GuardSpec spec)
        {
            AnimatorState guardState = FindGuard(stateMachine, spec, out AnimatorStateTransition guardTransition);
            if (guardState == null) return GuardStatus.None;

            bool upToDate =
                !guardTransition.canTransitionToSelf &&
                HasCondition(guardTransition, spec.ParamName, spec.ActiveMode) &&
                !HasInvalidGuardCondition(guardTransition, spec) &&
                HasClearExit(guardState, spec) &&
                !guardState.transitions.Any(t => t != null && HasInvalidGuardCondition(t, spec)) &&
                guardState.writeDefaultValues == GetDominantWriteDefaultValues(stateMachine) &&
                (guardState.writeDefaultValues || IsResetMotionCurrent(guardState, BuildResetCurves(stateMachine))) &&
                !HasUngatedSiblingTransitions(stateMachine, guardTransition, spec);

            return upToDate ? GuardStatus.Current : GuardStatus.Outdated;
        }

        /// <summary>
        /// Checks whether the guard state has a transition out of it that fires once the guard
        /// parameter clears (e.g. <c>FacialExpressionsDisabled == false</c>).
        /// </summary>
        private static bool HasClearExit(AnimatorState guardState, GuardSpec spec)
        {
            foreach (AnimatorStateTransition transition in guardState.transitions)
            {
                if (transition == null) continue;

                bool hasDestination = transition.destinationState != null ||
                                      transition.destinationStateMachine != null ||
                                      transition.isExit;
                if (!hasDestination) continue;

                foreach (AnimatorCondition condition in transition.conditions)
                {
                    if (condition.parameter == spec.ParamName && condition.mode == spec.ClearMode)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether any other AnyState transition in the layer could fire while the guard is
        /// active (i.e. it has no condition on the guard parameter). Because the guard transition
        /// cannot re-enter its own state, such a transition would pull the layer out of the guard
        /// state and the guard would then pull it back, flickering every frame.
        /// </summary>
        private static bool HasUngatedSiblingTransitions(AnimatorStateMachine stateMachine,
            AnimatorStateTransition guardTransition, GuardSpec spec)
        {
            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                if (IsExemptSibling(transition, guardTransition, spec)) continue;
                if (!HasGuardCondition(transition, spec) || HasInvalidGuardCondition(transition, spec)) return true;
            }

            return false;
        }

        private static bool IsExemptSibling(AnimatorStateTransition transition,
            AnimatorStateTransition guardTransition, GuardSpec spec)
        {
            if (transition == null || transition == guardTransition) return true;

            return spec.ExemptSiblingStateName != null &&
                   transition.destinationState != null &&
                   transition.destinationState.name == spec.ExemptSiblingStateName;
        }

        private static bool HasConditionOn(AnimatorTransitionBase transition, string parameter)
        {
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == parameter) return true;
            }

            return false;
        }

        private static bool IsGuardStateName(string stateName)
        {
            return stateName == EmptyStateName || stateName == BlinkGuardStateName;
        }

        // =====================================================================
        // Fix application
        // =====================================================================

        /// <summary>
        /// Ensures the FacialExpressionsDisabled bool parameter exists on the controller.
        /// If a parameter with that name already exists (of any type), this method is a no-op:
        /// the guard conditions are adapted to its type instead (see <see cref="ResolveGuardSpec"/>).
        /// </summary>
        internal static void EnsureParameterExists(AnimatorController controller)
        {
            if (FindParameter(controller, DisabledParamName) != null) return;

            controller.AddParameter(DisabledParamName, AnimatorControllerParameterType.Bool);
            Debug.Log($"{LogPrefix} Added parameter '{DisabledParamName}' to FX controller.");
        }

        /// <summary>
        /// Adds a FacialExpressionsDisabled == false (IfNot) condition to a single transition,
        /// assuming the parameter is a Bool. Prefer
        /// <see cref="ApplyTransitionGuard(AnimatorController, TransitionAnalysis)"/>, which adapts
        /// the condition to the controller's parameter type.
        /// </summary>
        internal static void ApplyTransitionGuard(TransitionAnalysis transition)
        {
            ApplyTransitionGuard(LayerGuardSpec, transition);
        }

        /// <summary>
        /// Adds the "FacialExpressionsDisabled is off" condition to a single transition, written
        /// for the parameter's type on <paramref name="controller"/> (Bool: IfNot; Float: Less 0.5;
        /// Int: Less 1). Conditions on the parameter whose mode does not fit its type are replaced.
        /// Does nothing when the parameter's type cannot express the guard (see
        /// <see cref="GetLayerGuardParameterError"/>).
        /// </summary>
        internal static void ApplyTransitionGuard(AnimatorController controller, TransitionAnalysis transition)
        {
            GuardSpec spec = ResolveGuardSpec(controller, LayerGuardSpec);
            if (spec == null) return;

            ApplyTransitionGuard(spec, transition);
        }

        private static void ApplyTransitionGuard(GuardSpec spec, TransitionAnalysis transition)
        {
            // The condition goes on the transition itself, wherever it lives (root, sub-state
            // machine, Entry or sub-state machine exit), so no placement logic is needed here.
            AnimatorTransitionBase t = transition.TransitionRef;
            if (t == null) return;

            // Never adds the condition twice (stale analysis, or an AnyState transition that a
            // layer guard already gated).
            EnsureClearCondition(t, spec, "Add FacialExpressionsDisabled condition");
        }

        /// <summary>
        /// Applies (or repairs) a layer-level guard: an empty state entered from AnyState while
        /// FacialExpressionsDisabled is true, which returns to the layer's default state once it is
        /// false again. The guard state uses the layer's dominant Write Defaults setting. If the
        /// layer already has a guard from an older version, it is repaired in place.
        /// </summary>
        internal static void ApplyLayerGuard(AnimatorController controller, LayerAnalysis layer)
        {
            EnsureGuard(controller, layer.LayerIndex, LayerGuardSpec);
        }

        /// <summary>
        /// Creates or repairs the guard described by <paramref name="baseSpec"/> on one layer:
        /// <list type="bullet">
        /// <item>a guard state using the layer's dominant Write Defaults setting: empty with
        /// Write Defaults on, or playing a reset clip (see <see cref="EnsureResetMotion"/>) with
        /// Write Defaults off;</item>
        /// <item>an AnyState transition into it (highest priority, no exit time, duration 0,
        /// <c>canTransitionToSelf = false</c> so it does not restart the state every frame);</item>
        /// <item>an exit transition from the guard state back to the layer's default state once
        /// the parameter clears (no exit time, duration 0);</item>
        /// <item>the "clear" condition on every other AnyState transition in the layer, so nothing
        /// can pull the layer out of the guard state while the guard is active.</item>
        /// </list>
        /// An existing guard (e.g. from an older version) is reused and fixed instead of duplicated.
        /// All conditions are written for the guard parameter's type on the controller.
        /// </summary>
        /// <param name="baseSpec">The default guard spec; resolved here for the controller.</param>
        /// <returns>False when the guard could not be applied (e.g. unusable parameter type).</returns>
        private static bool EnsureGuard(AnimatorController controller, int layerIndex, GuardSpec baseSpec)
        {
            GuardSpec spec = ResolveGuardSpec(controller, baseSpec);
            if (spec == null)
            {
                Debug.LogError($"{LogPrefix} {GetGuardParameterError(controller, baseSpec)}");
                return false;
            }

            AnimatorControllerLayer[] controllerLayers = controller.layers;
            if (layerIndex < 0 || layerIndex >= controllerLayers.Length) return false;

            AnimatorStateMachine stateMachine = controllerLayers[layerIndex].stateMachine;
            if (stateMachine == null) return false;
            if (GetGuardStatus(stateMachine, spec) == GuardStatus.Current) return true;

            Undo.RecordObject(stateMachine, spec.UndoName);

            // Determine WD from the layer's own states (guard states excluded) before adding ours
            bool writeDefaults = GetDominantWriteDefaultValues(stateMachine);

            AnimatorState guardState = FindGuard(stateMachine, spec, out AnimatorStateTransition guardTransition);
            if (guardState == null)
            {
                // Reuse a leftover guard state whose AnyState transition was removed by hand,
                // otherwise create the empty guard state (no motion attached).
                guardState = FindTopLevelState(stateMachine, spec.StateName) ??
                             stateMachine.AddState(spec.StateName, spec.StatePosition);

                guardTransition = stateMachine.AddAnyStateTransition(guardState);
                guardTransition.AddCondition(spec.ActiveMode, spec.ActiveThreshold, spec.ParamName);
            }
            else
            {
                Undo.RecordObject(guardState, spec.UndoName);
                Undo.RecordObject(guardTransition, spec.UndoName);

                // Rewrite the active condition when it was written for another parameter type
                if (!HasCondition(guardTransition, spec.ParamName, spec.ActiveMode) ||
                    HasInvalidGuardCondition(guardTransition, spec))
                {
                    SetGuardCondition(guardTransition, spec, spec.ActiveMode, spec.ActiveThreshold, spec.UndoName);
                }
            }

            guardState.writeDefaultValues = writeDefaults;

            // With Write Defaults off an empty state holds the last animated values, so an
            // expression showing when the guard engages would stay visible. Give the guard state
            // a clip that puts the layer's properties back to their defaults.
            if (!writeDefaults)
            {
                EnsureResetMotion(controller, controllerLayers[layerIndex].name, stateMachine, guardState, spec);
            }

            guardTransition.hasExitTime = false;
            guardTransition.duration = 0f;
            guardTransition.canTransitionToSelf = false;

            // Unity evaluates AnyState transitions in order; keep the guard first so it wins.
            MoveAnyStateTransitionToTop(stateMachine, guardTransition);

            EnsureClearExit(stateMachine, guardState, spec);
            GateSiblingTransitions(stateMachine, guardTransition, spec);
            GateForOtherGuards(controller, stateMachine, guardTransition, spec);

            // Reassign layer array since Unity uses copy-on-read for layers
            controller.layers = controllerLayers;

            EditorUtility.SetDirty(guardState);
            EditorUtility.SetDirty(guardTransition);
            EditorUtility.SetDirty(stateMachine);
            return true;
        }

        /// <summary>
        /// Adds the transition that leaves the guard state once the guard parameter clears, unless
        /// one already exists. It targets the layer's default state; if the default state is
        /// missing or is a guard state itself, the first other state is used, and if there is
        /// none the transition goes to the Exit node (which re-enters via Entry). Exit transitions
        /// whose condition was written for another parameter type are rewritten instead.
        /// </summary>
        private static void EnsureClearExit(AnimatorStateMachine stateMachine, AnimatorState guardState, GuardSpec spec)
        {
            foreach (AnimatorStateTransition transition in guardState.transitions)
            {
                if (transition != null && HasInvalidGuardCondition(transition, spec))
                {
                    SetGuardCondition(transition, spec, spec.ClearMode, spec.ClearThreshold, spec.UndoName);
                }
            }

            if (HasClearExit(guardState, spec)) return;

            Undo.RecordObject(guardState, spec.UndoName);

            AnimatorState returnState = FindReturnState(stateMachine, guardState);
            AnimatorStateTransition exitTransition = returnState != null
                ? guardState.AddTransition(returnState)
                : guardState.AddExitTransition();

            exitTransition.hasExitTime = false;
            exitTransition.exitTime = 0f;
            exitTransition.hasFixedDuration = true;
            exitTransition.duration = 0f;
            exitTransition.offset = 0f;
            exitTransition.AddCondition(spec.ClearMode, spec.ClearThreshold, spec.ParamName);

            EditorUtility.SetDirty(exitTransition);
        }

        /// <summary>
        /// Adds the guard's "clear" condition to every other AnyState transition in the layer that
        /// has no usable condition on the guard parameter yet. Without this, such a transition
        /// could fire from inside the guard state (the guard transition cannot re-enter its own
        /// state), and the layer would flicker between the guard and that transition's destination.
        /// </summary>
        private static void GateSiblingTransitions(AnimatorStateMachine stateMachine,
            AnimatorStateTransition guardTransition, GuardSpec spec)
        {
            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                if (IsExemptSibling(transition, guardTransition, spec)) continue;

                EnsureClearCondition(transition, spec, spec.UndoName);
            }
        }

        /// <summary>
        /// When the layer also carries the other kind of guard (layer guard + blink guard on the
        /// same layer), gates this guard's AnyState transition by that guard's "clear" condition
        /// as well, honouring its exemptions. This keeps the other guard complete (otherwise it
        /// would be reported as outdated) and makes the layer guard take precedence over the
        /// blink guard when both parameters are active.
        /// </summary>
        private static void GateForOtherGuards(AnimatorController controller, AnimatorStateMachine stateMachine,
            AnimatorStateTransition guardTransition, GuardSpec spec)
        {
            foreach (GuardSpec otherBase in new[] { LayerGuardSpec, BlinkGuardSpec })
            {
                if (otherBase.StateName == spec.StateName) continue;

                GuardSpec other = ResolveGuardSpec(controller, otherBase);
                if (other == null) continue;
                if (FindGuard(stateMachine, other, out AnimatorStateTransition otherTransition) == null) continue;
                if (IsExemptSibling(guardTransition, otherTransition, other)) continue;

                EnsureClearCondition(guardTransition, other, spec.UndoName);
            }
        }

        /// <summary>
        /// Picks the state the guard returns to: the layer's default state, or the first other
        /// state (top-level first, then inside sub-state machines) when the default is missing or
        /// is a guard state. Null if none exists.
        /// </summary>
        private static AnimatorState FindReturnState(AnimatorStateMachine stateMachine, AnimatorState guardState)
        {
            AnimatorState defaultState = stateMachine.defaultState;
            if (defaultState != null && defaultState != guardState && !IsGuardStateName(defaultState.name))
            {
                return defaultState;
            }

            foreach (AnimatorState state in GetAllStates(stateMachine))
            {
                if (state != guardState && !IsGuardStateName(state.name))
                {
                    return state;
                }
            }

            return null;
        }

        private static AnimatorState FindTopLevelState(AnimatorStateMachine stateMachine, string stateName)
        {
            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                if (childState.state != null && childState.state.name == stateName)
                {
                    return childState.state;
                }
            }

            return null;
        }

        // =====================================================================
        // Reset clip for guards on Write Defaults off layers
        // =====================================================================

        /// <summary>Length of the generated reset clip (one frame at 60 fps).</summary>
        private const float ResetClipLength = 1f / 60f;

        /// <summary>
        /// One property the reset clip writes: a float value or an object reference (e.g. a
        /// material swap).
        /// </summary>
        private sealed class ResetCurve
        {
            public EditorCurveBinding Binding;
            public bool IsObjectReference;
            public float Value;
            public UnityEngine.Object ObjectValue;
        }

        /// <summary>
        /// Lists the properties a guard state on a Write Defaults off layer must reset: every
        /// property animated by the layer's own states (guard states excluded, nested sub-state
        /// machines and blend trees included). The value is taken from the layer's default state
        /// clip at time 0 when it animates the property (that is the layer's neutral pose);
        /// otherwise blendshapes reset to 0, and other properties are left out because their
        /// default value is unknown.
        /// </summary>
        private static List<ResetCurve> BuildResetCurves(AnimatorStateMachine stateMachine)
        {
            List<ResetCurve> result = new List<ResetCurve>();
            if (stateMachine == null) return result;

            HashSet<AnimationClip> clips = new HashSet<AnimationClip>();
            foreach (AnimatorState state in GetAllStates(stateMachine))
            {
                if (IsGuardStateName(state.name)) continue;
                CollectClips(state.motion, clips);
            }

            SortedDictionary<string, EditorCurveBinding> floatBindings = new SortedDictionary<string, EditorCurveBinding>(StringComparer.Ordinal);
            SortedDictionary<string, EditorCurveBinding> objectBindings = new SortedDictionary<string, EditorCurveBinding>(StringComparer.Ordinal);

            foreach (AnimationClip clip in clips)
            {
                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                {
                    floatBindings[GetBindingKey(binding)] = binding;
                }

                foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    objectBindings[GetBindingKey(binding)] = binding;
                }
            }

            AnimatorState defaultState = stateMachine.defaultState;
            AnimationClip defaultClip = defaultState != null && !IsGuardStateName(defaultState.name)
                ? defaultState.motion as AnimationClip
                : null;

            foreach (EditorCurveBinding binding in floatBindings.Values)
            {
                AnimationCurve defaultCurve = defaultClip != null ? AnimationUtility.GetEditorCurve(defaultClip, binding) : null;

                float value;
                if (defaultCurve != null && defaultCurve.length > 0)
                {
                    value = defaultCurve.Evaluate(0f);
                }
                else if (IsBlendshapeBinding(binding))
                {
                    value = 0f;
                }
                else
                {
                    continue;
                }

                result.Add(new ResetCurve { Binding = binding, Value = value });
            }

            foreach (EditorCurveBinding binding in objectBindings.Values)
            {
                ObjectReferenceKeyframe[] defaultKeys = defaultClip != null
                    ? AnimationUtility.GetObjectReferenceCurve(defaultClip, binding)
                    : null;
                if (defaultKeys == null || defaultKeys.Length == 0) continue;

                result.Add(new ResetCurve { Binding = binding, IsObjectReference = true, ObjectValue = defaultKeys[0].value });
            }

            return result;
        }

        private static void CollectClips(Motion motion, HashSet<AnimationClip> clips)
        {
            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
            }
            else if (motion is BlendTree blendTree)
            {
                foreach (ChildMotion child in blendTree.children)
                {
                    CollectClips(child.motion, clips);
                }
            }
        }

        private static string GetBindingKey(EditorCurveBinding binding)
        {
            return $"{binding.path}|{(binding.type != null ? binding.type.FullName : string.Empty)}|{binding.propertyName}";
        }

        private static bool IsBlendshapeBinding(EditorCurveBinding binding)
        {
            return binding.type == typeof(SkinnedMeshRenderer) &&
                   binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
        }

        /// <summary>
        /// Checks whether the guard state's motion is a clip that writes exactly
        /// <paramref name="desired"/> (same properties, same values at time 0).
        /// </summary>
        private static bool IsResetMotionCurrent(AnimatorState guardState, List<ResetCurve> desired)
        {
            if (desired.Count == 0) return true;

            AnimationClip clip = guardState.motion as AnimationClip;
            if (clip == null) return false;

            int floatCount = desired.Count(c => !c.IsObjectReference);
            if (AnimationUtility.GetCurveBindings(clip).Length != floatCount ||
                AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != desired.Count - floatCount)
            {
                return false;
            }

            foreach (ResetCurve curve in desired)
            {
                if (curve.IsObjectReference)
                {
                    ObjectReferenceKeyframe[] keys = AnimationUtility.GetObjectReferenceCurve(clip, curve.Binding);
                    if (keys == null || keys.Length == 0 || keys[0].value != curve.ObjectValue) return false;
                }
                else
                {
                    AnimationCurve existing = AnimationUtility.GetEditorCurve(clip, curve.Binding);
                    if (existing == null || !Mathf.Approximately(existing.Evaluate(0f), curve.Value)) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gives a guard state on a Write Defaults off layer a clip that writes the layer's
        /// default values (see <see cref="BuildResetCurves"/>), so an expression that is showing
        /// when the guard engages is cleared instead of frozen. The clip is stored as a sub-asset
        /// of the controller (so it travels with controller copies) and reused on repair: an
        /// existing reset clip on the guard state is rewritten in place, never duplicated.
        /// </summary>
        private static void EnsureResetMotion(AnimatorController controller, string layerName,
            AnimatorStateMachine stateMachine, AnimatorState guardState, GuardSpec spec)
        {
            List<ResetCurve> curves = BuildResetCurves(stateMachine);
            if (IsResetMotionCurrent(guardState, curves)) return;

            Undo.RecordObject(guardState, spec.UndoName);

            AnimationClip clip = GetOwnedResetClip(controller, guardState, spec);
            if (clip == null)
            {
                clip = new AnimationClip { name = $"{spec.StateName} Reset ({layerName})" };

                string controllerPath = AssetDatabase.GetAssetPath(controller);
                if (!string.IsNullOrEmpty(controllerPath))
                {
                    AssetDatabase.AddObjectToAsset(clip, controller);
                }

                Undo.RegisterCreatedObjectUndo(clip, spec.UndoName);
            }
            else
            {
                Undo.RecordObject(clip, spec.UndoName);

                foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
                {
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                }

                foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                }
            }

            List<ResetCurve> floatCurves = curves.Where(c => !c.IsObjectReference).ToList();
            if (floatCurves.Count > 0)
            {
                AnimationUtility.SetEditorCurves(clip,
                    floatCurves.Select(c => c.Binding).ToArray(),
                    floatCurves.Select(c => AnimationCurve.Constant(0f, ResetClipLength, c.Value)).ToArray());
            }

            foreach (ResetCurve curve in curves.Where(c => c.IsObjectReference))
            {
                AnimationUtility.SetObjectReferenceCurve(clip, curve.Binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = curve.ObjectValue },
                    new ObjectReferenceKeyframe { time = ResetClipLength, value = curve.ObjectValue }
                });
            }

            guardState.motion = clip;

            EditorUtility.SetDirty(clip);
            Debug.Log($"{LogPrefix} Layer '{layerName}' uses Write Defaults off: guard state '{spec.StateName}' " +
                      $"now plays reset clip '{clip.name}' ({curves.Count} propert{(curves.Count == 1 ? "y" : "ies")}).");
        }

        /// <summary>
        /// Returns the guard state's current reset clip when this tool created it (a clip named
        /// after the guard state, stored inside the controller asset), otherwise null.
        /// </summary>
        private static AnimationClip GetOwnedResetClip(AnimatorController controller, AnimatorState guardState, GuardSpec spec)
        {
            AnimationClip clip = guardState.motion as AnimationClip;
            if (clip == null || !clip.name.StartsWith(spec.StateName, StringComparison.Ordinal)) return null;

            string controllerPath = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(controllerPath) || AssetDatabase.GetAssetPath(clip) != controllerPath) return null;

            return clip;
        }

        /// <summary>
        /// Moves <paramref name="transition"/> to index 0 of the AnyState transitions so it has the
        /// highest priority.
        /// </summary>
        private static void MoveAnyStateTransitionToTop(AnimatorStateMachine stateMachine, AnimatorStateTransition transition)
        {
            AnimatorStateTransition[] anyTransitions = stateMachine.anyStateTransitions;
            int index = Array.IndexOf(anyTransitions, transition);
            if (index <= 0) return;

            List<AnimatorStateTransition> reordered = new List<AnimatorStateTransition>(anyTransitions.Length) { transition };
            for (int i = 0; i < anyTransitions.Length; i++)
            {
                if (i != index) reordered.Add(anyTransitions[i]);
            }

            stateMachine.anyStateTransitions = reordered.ToArray();
        }

        /// <summary>
        /// Orchestrates applying all selected fixes (transition guards and layer guards)
        /// to the given controller. Registers Undo, ensures the parameter exists,
        /// iterates layers, marks dirty, saves assets, and logs a summary.
        /// </summary>
        /// <returns>A tuple of (transitionFixes, layerFixes) counts. Layer fixes include outdated
        /// layer guards that were repaired in place.</returns>
        internal static (int transitionFixes, int layerFixes) ApplySelectedFixes(AnimatorController controller, List<LayerAnalysis> layers)
        {
            if (controller == null || layers == null) return (0, 0);

            // Never write conditions that the existing parameter's type cannot evaluate
            string parameterError = GetLayerGuardParameterError(controller);
            if (parameterError != null)
            {
                Debug.LogError($"{LogPrefix} {parameterError}");
                return (0, 0);
            }

            Undo.RegisterCompleteObjectUndo(controller, "Apply FacialExpressionsDisabled Guards");

            EnsureParameterExists(controller);

            int transitionFixCount = 0;
            int layerFixCount = 0;
            int layerRepairCount = 0;

            foreach (LayerAnalysis layer in layers)
            {
                // Per-transition fixes
                foreach (TransitionAnalysis transition in layer.GestureTransitions)
                {
                    if (transition.SelectedForFix && !transition.HasDisabledGuard)
                    {
                        ApplyTransitionGuard(controller, transition);
                        transitionFixCount++;
                    }
                }

                // Layer-level fix (creates a new guard or repairs an outdated one)
                if (layer.SelectedForLayerDisable && !layer.AlreadyHasLayerGuard &&
                    EnsureGuard(controller, layer.LayerIndex, LayerGuardSpec))
                {
                    layerFixCount++;
                    if (layer.LayerGuardNeedsRepair) layerRepairCount++;
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            if (layerRepairCount > 0)
            {
                Debug.Log($"{LogPrefix} Repaired {layerRepairCount} outdated layer guard(s).");
            }

            Debug.Log($"{LogPrefix} Applied {transitionFixCount} transition guard(s) and {layerFixCount} layer guard(s).");

            return (transitionFixCount, layerFixCount);
        }

        // =====================================================================
        // Copy and assignment helpers
        // =====================================================================

        /// <summary>
        /// Duplicates an FX controller asset into the specified output folder.
        /// Returns the new AnimatorController, or null on failure (with errorMessage set).
        /// </summary>
        /// <param name="source">The source AnimatorController to copy.</param>
        /// <param name="outputFolder">The asset folder to place the copy in (e.g. "Assets/MyFolder").</param>
        /// <param name="errorMessage">Set to a descriptive error string on failure; null on success.</param>
        /// <returns>The copied AnimatorController, or null if the copy failed.</returns>
        internal static AnimatorController CopyFXController(AnimatorController source, string outputFolder, out string errorMessage)
        {
            errorMessage = null;

            string sourcePath = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(sourcePath))
            {
                errorMessage = "Cannot determine the asset path of the current FX controller.";
                return null;
            }

            string folder = string.IsNullOrEmpty(outputFolder) ? "Assets" : outputFolder;
            PawlygonEditorUtils.EnsureFolderExists(folder);

            // Copying a copy yields "X_Modified 1", not "X_Modified_Modified"
            string fileName = StripModifiedSuffix(Path.GetFileNameWithoutExtension(sourcePath));
            string extension = Path.GetExtension(sourcePath);
            string destinationPath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}_Modified{extension}");

            if (!AssetDatabase.CopyAsset(sourcePath, destinationPath))
            {
                errorMessage = $"Failed to copy FX controller to '{destinationPath}'.";
                return null;
            }

            AssetDatabase.Refresh();

            AnimatorController copy = AssetDatabase.LoadAssetAtPath<AnimatorController>(destinationPath);
            if (copy == null)
            {
                errorMessage = $"Copied asset at '{destinationPath}' could not be loaded as an AnimatorController.";
                return null;
            }

            Debug.Log($"{LogPrefix} Created FX controller copy at '{destinationPath}'.");
            return copy;
        }

        /// <summary>
        /// Removes a "_Modified" suffix (optionally followed by the " N" that
        /// <see cref="AssetDatabase.GenerateUniqueAssetPath"/> appends) from a controller file name.
        /// </summary>
        private static string StripModifiedSuffix(string fileName)
        {
            return System.Text.RegularExpressions.Regex.Replace(fileName, @"_Modified(?: \d+)?$", string.Empty);
        }

        /// <summary>
        /// Checks whether a controller asset cannot be edited in place because it lives inside an
        /// immutable package (registry, git, built-in, ...) under <c>Packages/</c>. Embedded and
        /// local packages are regular files on disk and are treated as editable.
        /// </summary>
        internal static bool IsControllerReadOnly(AnimatorController controller)
        {
            if (controller == null) return false;

            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Packages/", StringComparison.Ordinal)) return false;

            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            if (packageInfo == null) return true;

            return packageInfo.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                   packageInfo.source != UnityEditor.PackageManager.PackageSource.Local;
        }

        /// <summary>
        /// Assigns a new FX AnimatorController to a VRCAvatarDescriptor component using reflection.
        /// Finds the FX entry in baseAnimationLayers and replaces the controller reference.
        /// </summary>
        /// <param name="descriptor">The VRCAvatarDescriptor component.</param>
        /// <param name="descriptorType">The reflected Type of VRCAvatarDescriptor.</param>
        /// <param name="newController">The new AnimatorController to assign.</param>
        /// <returns>True if assignment succeeded, false otherwise.</returns>
        internal static bool AssignFXControllerToDescriptor(Component descriptor, Type descriptorType, AnimatorController newController)
        {
            if (descriptor == null || descriptorType == null)
            {
                Debug.LogWarning($"{LogPrefix} No VRCAvatarDescriptor to assign the controller to.");
                return false;
            }

            FieldInfo layersField = descriptorType.GetField("baseAnimationLayers",
                BindingFlags.Public | BindingFlags.Instance);

            if (layersField == null)
            {
                Debug.LogWarning($"{LogPrefix} Could not find 'baseAnimationLayers' field for assignment.");
                return false;
            }

            object layersValue = layersField.GetValue(descriptor);
            Array layersArray = layersValue as Array;
            if (layersArray == null) return false;

            Type elementType = layersArray.GetType().GetElementType();
            if (elementType == null) return false;

            FieldInfo typeField = elementType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo controllerField = elementType.GetField("animatorController", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo isDefaultField = elementType.GetField("isDefault", BindingFlags.Public | BindingFlags.Instance);

            if (typeField == null || controllerField == null) return false;

            Undo.RecordObject(descriptor, "Assign copied FX controller");

            for (int i = 0; i < layersArray.Length; i++)
            {
                object layerEntry = layersArray.GetValue(i);
                int layerType = Convert.ToInt32(typeField.GetValue(layerEntry));

                if (layerType != AnimLayerTypeFX) continue;

                controllerField.SetValue(layerEntry, newController);

                if (isDefaultField != null)
                {
                    isDefaultField.SetValue(layerEntry, false);
                }

                // Struct: write back into the array
                layersArray.SetValue(layerEntry, i);
                break;
            }

            // Write the modified array back to the descriptor
            layersField.SetValue(descriptor, layersArray);
            EditorUtility.SetDirty(descriptor);

            Debug.Log($"{LogPrefix} Assigned copied FX controller to VRCAvatarDescriptor on '{descriptor.gameObject.name}'.");
            return true;
        }

        // =====================================================================
        // Selection helpers
        // =====================================================================

        /// <summary>
        /// Selects all unguarded transitions and layers for fix application.
        /// </summary>
        internal static void SelectAllUnguarded(List<LayerAnalysis> layers)
        {
            foreach (LayerAnalysis layer in layers)
            {
                if (!layer.AlreadyHasLayerGuard)
                {
                    layer.SelectedForLayerDisable = true;
                }

                foreach (TransitionAnalysis t in layer.GestureTransitions)
                {
                    if (!t.HasDisabledGuard)
                    {
                        t.SelectedForFix = true;
                    }
                }
            }
        }

        /// <summary>
        /// Deselects all transitions and layers.
        /// </summary>
        internal static void DeselectAll(List<LayerAnalysis> layers)
        {
            foreach (LayerAnalysis layer in layers)
            {
                layer.SelectedForLayerDisable = false;
                foreach (TransitionAnalysis t in layer.GestureTransitions)
                {
                    t.SelectedForFix = false;
                }
            }
        }

        // =====================================================================
        // Blink layer analysis
        // =====================================================================

        /// <summary>
        /// Analyzes all layers of the FX controller to identify potential blink layers
        /// using name and animation clip heuristics with confidence scoring.
        /// </summary>
        internal static List<BlinkLayerAnalysis> AnalyzeBlinkLayers(AnimatorController controller)
        {
            List<BlinkLayerAnalysis> results = new List<BlinkLayerAnalysis>();
            AnimatorControllerLayer[] controllerLayers = controller.layers;
            GuardSpec blinkSpec = ResolveGuardSpecForAnalysis(controller, BlinkGuardSpec);

            for (int i = 0; i < controllerLayers.Length; i++)
            {
                AnimatorControllerLayer layer = controllerLayers[i];
                BlinkLayerAnalysis analysis = AnalyzeBlinkLayer(layer, i, blinkSpec);

                if (analysis.ConfidenceScore > 0)
                {
                    results.Add(analysis);
                }
            }

            return results;
        }

        /// <summary>
        /// Scores a single layer for blink likelihood using name keywords,
        /// animation clip blendshape bindings, and state count heuristics.
        /// </summary>
        private static BlinkLayerAnalysis AnalyzeBlinkLayer(AnimatorControllerLayer layer, int layerIndex, GuardSpec blinkSpec)
        {
            GuardStatus guardStatus = GetGuardStatus(layer.stateMachine, blinkSpec);

            BlinkLayerAnalysis analysis = new BlinkLayerAnalysis
            {
                LayerName = layer.name,
                LayerIndex = layerIndex,
                AlreadyHasBlinkGuard = guardStatus == GuardStatus.Current,
                BlinkGuardNeedsRepair = guardStatus == GuardStatus.Outdated
            };

            string layerNameLower = layer.name.ToLowerInvariant();

            // --- Layer name: strong keywords (+3) ---
            foreach (string keyword in BlinkLayerNameKeywordsStrong)
            {
                if (layerNameLower.Contains(keyword.ToLowerInvariant()))
                {
                    analysis.ConfidenceScore += 3;
                    analysis.DetectionReasons.Add($"Layer name contains \"{keyword}\"");
                    break; // Only award once for name match
                }
            }

            // --- Layer name: weak keywords (+1) ---
            if (analysis.ConfidenceScore == 0) // Only check weak if no strong match
            {
                foreach (string keyword in BlinkLayerNameKeywordsWeak)
                {
                    if (layerNameLower.Contains(keyword.ToLowerInvariant()))
                    {
                        analysis.ConfidenceScore += 1;
                        analysis.DetectionReasons.Add($"Layer name contains \"{keyword}\"");
                        break;
                    }
                }
            }

            // --- Animation clip blendshape analysis (+3) ---
            if (LayerClipsContainBlinkBlendshapes(layer, out List<string> matchedBlendshapes))
            {
                analysis.ConfidenceScore += 3;
                string joined = string.Join(", ", matchedBlendshapes);
                analysis.DetectionReasons.Add($"Clips animate blink blendshapes: {joined}");
            }

            // --- State count heuristic (+1 for simple layers) ---
            AnimatorStateMachine stateMachine = layer.stateMachine;
            if (stateMachine != null)
            {
                // Our own guard states are not part of the layer's blink logic. States inside
                // sub-state machines count too.
                int stateCount = GetAllStates(stateMachine).Count(s => !IsGuardStateName(s.name));
                if (stateCount >= 1 && stateCount <= 4)
                {
                    analysis.ConfidenceScore += 1;
                    analysis.DetectionReasons.Add($"Simple layer ({stateCount} state{(stateCount != 1 ? "s" : "")})");
                }
            }

            // --- Determine confidence level ---
            if (analysis.ConfidenceScore >= BlinkConfidenceHigh)
            {
                analysis.Confidence = BlinkConfidence.High;
                analysis.SelectedForGuard = !analysis.AlreadyHasBlinkGuard;
            }
            else if (analysis.ConfidenceScore >= BlinkConfidenceMedium)
            {
                analysis.Confidence = BlinkConfidence.Medium;
            }
            else
            {
                analysis.Confidence = BlinkConfidence.Low;
            }

            // The user opted into this guard in the past; pre-select the repair at any confidence.
            if (analysis.BlinkGuardNeedsRepair)
            {
                analysis.SelectedForGuard = true;
            }

            return analysis;
        }

        /// <summary>
        /// Checks whether any animation clips in the layer (including states inside sub-state
        /// machines) animate blendshape properties whose names match known blink blendshape keywords.
        /// </summary>
        private static bool LayerClipsContainBlinkBlendshapes(AnimatorControllerLayer layer, out List<string> matchedBlendshapes)
        {
            matchedBlendshapes = new List<string>();
            AnimatorStateMachine stateMachine = layer.stateMachine;
            if (stateMachine == null) return false;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (AnimatorState state in GetAllStates(stateMachine))
            {
                CollectBlinkBlendshapesFromMotion(state.motion, seen);
            }

            matchedBlendshapes.AddRange(seen);
            return matchedBlendshapes.Count > 0;
        }

        /// <summary>
        /// Recursively inspects a Motion (AnimationClip or BlendTree) for blendshape
        /// property bindings that match blink keywords.
        /// </summary>
        private static void CollectBlinkBlendshapesFromMotion(Motion motion, HashSet<string> seen)
        {
            if (motion == null) return;

            if (motion is AnimationClip clip)
            {
                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                foreach (EditorCurveBinding binding in bindings)
                {
                    // Blendshape bindings use "blendShape." prefix on the propertyName
                    if (!binding.propertyName.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string blendshapeName = binding.propertyName.Substring("blendShape.".Length);

                    foreach (string keyword in BlinkBlendshapeKeywords)
                    {
                        if (string.Equals(blendshapeName, keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            seen.Add(blendshapeName);
                            break;
                        }
                    }
                }
            }
            else if (motion is BlendTree blendTree)
            {
                ChildMotion[] children = blendTree.children;
                foreach (ChildMotion child in children)
                {
                    CollectBlinkBlendshapesFromMotion(child.motion, seen);
                }
            }
        }

        // =====================================================================
        // Blink guard application
        // =====================================================================

        /// <summary>
        /// Ensures the EyeTrackingActive float parameter exists on the controller.
        /// If a parameter with that name already exists (of any type, e.g. a Bool declared by
        /// another tool), this method is a no-op: the blink guard's conditions are adapted to its
        /// type instead (see <see cref="ResolveGuardSpec"/>).
        /// </summary>
        internal static void EnsureEyeTrackingParameterExists(AnimatorController controller)
        {
            if (FindParameter(controller, EyeTrackingActiveParam) != null) return;

            controller.AddParameter(EyeTrackingActiveParam, AnimatorControllerParameterType.Float);
            Debug.Log($"{LogPrefix} Added float parameter '{EyeTrackingActiveParam}' to FX controller.");
        }

        /// <summary>
        /// Applies (or repairs) a blink guard: an empty state entered from AnyState while
        /// EyeTrackingActive &gt; 0.5, which returns to the layer's default state once
        /// EyeTrackingActive &lt; 0.5 so blinking resumes when eye tracking turns off
        /// (If / IfNot when EyeTrackingActive already exists as a Bool).
        /// The guard state's writeDefaultValues matches the layer's dominant WD setting.
        /// If the layer already has a guard from an older version, it is repaired in place.
        /// </summary>
        internal static void ApplyBlinkGuard(AnimatorController controller, BlinkLayerAnalysis blinkLayer)
        {
            EnsureGuard(controller, blinkLayer.LayerIndex, BlinkGuardSpec);
        }

        /// <summary>
        /// Determines the dominant writeDefaultValues setting among the states of a state machine
        /// (including nested sub-state machines, excluding this tool's own guard states).
        /// Returns the value used by the majority of states, defaulting to true if there are no
        /// states or an even split.
        /// </summary>
        private static bool GetDominantWriteDefaultValues(AnimatorStateMachine stateMachine)
        {
            int wdTrue = 0;
            int wdFalse = 0;

            foreach (AnimatorState state in GetAllStates(stateMachine))
            {
                if (IsGuardStateName(state.name)) continue;

                if (state.writeDefaultValues)
                    wdTrue++;
                else
                    wdFalse++;
            }

            // Default to true if no states or even split
            return wdFalse <= wdTrue;
        }

        /// <summary>
        /// Orchestrates applying all selected blink guards to the given controller.
        /// Ensures the EyeTrackingActive parameter exists and applies guards to each
        /// selected layer.
        /// </summary>
        /// <returns>The number of blink guards applied, including outdated blink guards that
        /// were repaired in place.</returns>
        internal static int ApplySelectedBlinkGuards(AnimatorController controller, List<BlinkLayerAnalysis> blinkLayers)
        {
            if (controller == null || blinkLayers == null || blinkLayers.Count == 0) return 0;

            // Never write conditions that the existing parameter's type cannot evaluate
            string parameterError = GetBlinkGuardParameterError(controller);
            if (parameterError != null)
            {
                Debug.LogError($"{LogPrefix} {parameterError}");
                return 0;
            }

            Undo.RegisterCompleteObjectUndo(controller, "Apply EyeTrackingActive Blink Guards");

            EnsureEyeTrackingParameterExists(controller);

            int guardCount = 0;
            int repairCount = 0;

            foreach (BlinkLayerAnalysis blinkLayer in blinkLayers)
            {
                if (blinkLayer.SelectedForGuard && !blinkLayer.AlreadyHasBlinkGuard &&
                    EnsureGuard(controller, blinkLayer.LayerIndex, BlinkGuardSpec))
                {
                    guardCount++;
                    if (blinkLayer.BlinkGuardNeedsRepair) repairCount++;
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            if (repairCount > 0)
            {
                Debug.Log($"{LogPrefix} Repaired {repairCount} outdated blink guard(s).");
            }

            Debug.Log($"{LogPrefix} Applied {guardCount} blink layer guard(s).");

            return guardCount;
        }

        // =====================================================================
        // Blink selection helpers
        // =====================================================================

        /// <summary>
        /// Selects all unguarded blink layers for guard application.
        /// </summary>
        internal static void SelectAllUnguardedBlink(List<BlinkLayerAnalysis> blinkLayers)
        {
            if (blinkLayers == null) return;

            foreach (BlinkLayerAnalysis layer in blinkLayers)
            {
                if (!layer.AlreadyHasBlinkGuard)
                {
                    layer.SelectedForGuard = true;
                }
            }
        }

        /// <summary>
        /// Deselects all blink layers.
        /// </summary>
        internal static void DeselectAllBlink(List<BlinkLayerAnalysis> blinkLayers)
        {
            if (blinkLayers == null) return;

            foreach (BlinkLayerAnalysis layer in blinkLayers)
            {
                layer.SelectedForGuard = false;
            }
        }

        // =====================================================================
        // Utility
        // =====================================================================

        /// <summary>
        /// Returns the human-readable gesture name for a given integer value (0-7),
        /// or the integer as a string if out of range.
        /// </summary>
        internal static string GetGestureName(int value)
        {
            if (value >= 0 && value < GestureNames.Length)
            {
                return GestureNames[value];
            }

            return value.ToString();
        }
    }
}
