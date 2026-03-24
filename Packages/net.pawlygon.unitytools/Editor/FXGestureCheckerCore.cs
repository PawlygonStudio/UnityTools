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
            public List<TransitionAnalysis> GestureTransitions = new List<TransitionAnalysis>();
            public bool SelectedForLayerDisable;
            public bool AlreadyHasLayerGuard;
        }

        /// <summary>
        /// Holds the analysis results for a single animator state transition
        /// that references a gesture parameter (GestureLeft or GestureRight).
        /// </summary>
        internal class TransitionAnalysis
        {
            public string SourceName;
            public string DestinationName;
            public string GestureParameter;
            public int GestureValue;
            public bool HasDisabledGuard;
            public bool SelectedForFix;
            public AnimatorStateTransition TransitionRef;
        }

        /// <summary>
        /// Aggregated result of analyzing an FX controller, containing all layer analyses,
        /// a status message, and references to the source descriptor and controller.
        /// </summary>
        internal class AnalysisResult
        {
            public AnimatorController FXController;
            public List<LayerAnalysis> Layers;
            public string StatusMessage;
            public MessageType StatusMessageType;
            public bool Success;
            public Component Descriptor;
            public Type DescriptorType;
        }

        // =====================================================================
        // Cached reflection types
        // =====================================================================

        // =====================================================================
        // Reflection helpers
        // =====================================================================

        /// <summary>
        /// Extracts the FX AnimatorController from a VRCAvatarDescriptor component using reflection.
        /// Reads baseAnimationLayers array and finds the entry where type == AnimLayerType.FX (5).
        /// </summary>
        internal static AnimatorController GetFXController(Component descriptor, Type descriptorType)
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
                RuntimeAnimatorController runtimeController = controllerValue as RuntimeAnimatorController;
                if (runtimeController == null) return null;

                return runtimeController as AnimatorController;
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

            AnimatorController controller = GetFXController(descriptor, descriptorType);
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

            for (int i = 0; i < controllerLayers.Length; i++)
            {
                AnimatorControllerLayer layer = controllerLayers[i];
                LayerAnalysis layerAnalysis = AnalyzeLayer(layer, i);

                if (layerAnalysis.GestureTransitions.Count > 0)
                {
                    analysisResults.Add(layerAnalysis);
                }
            }

            result.Layers = analysisResults;

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
                result.StatusMessage = $"Found {totalTransitions} gesture transition(s) across {analysisResults.Count} layer(s). " +
                                       $"{guardedTransitions} transition(s) and {guardedLayers} layer(s) already guarded.";
                result.StatusMessageType = MessageType.Info;
            }

            return result;
        }

        /// <summary>
        /// Analyzes a single animator controller layer for gesture transitions.
        /// </summary>
        private static LayerAnalysis AnalyzeLayer(AnimatorControllerLayer layer, int layerIndex)
        {
            LayerAnalysis analysis = new LayerAnalysis
            {
                LayerName = layer.name,
                LayerIndex = layerIndex,
                AlreadyHasLayerGuard = HasLayerLevelGuard(layer)
            };

            AnimatorStateMachine stateMachine = layer.stateMachine;
            if (stateMachine == null) return analysis;

            // Check AnyState transitions
            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                AnalyzeTransition(transition, "AnyState", analysis);
            }

            // Check per-state transitions
            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                AnimatorState state = childState.state;
                if (state == null) continue;

                foreach (AnimatorStateTransition transition in state.transitions)
                {
                    AnalyzeTransition(transition, state.name, analysis);
                }
            }

            return analysis;
        }

        /// <summary>
        /// Analyzes a single transition for gesture parameter conditions and records findings.
        /// </summary>
        private static void AnalyzeTransition(AnimatorStateTransition transition, string sourceName, LayerAnalysis analysis)
        {
            if (transition == null) return;

            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == GestureLeftParam || condition.parameter == GestureRightParam)
                {
                    string destinationName = transition.destinationState != null
                        ? transition.destinationState.name
                        : transition.destinationStateMachine != null
                            ? transition.destinationStateMachine.name
                            : "(exit)";

                    bool hasGuard = HasDisabledGuard(transition);

                    analysis.GestureTransitions.Add(new TransitionAnalysis
                    {
                        SourceName = sourceName,
                        DestinationName = destinationName,
                        GestureParameter = condition.parameter,
                        GestureValue = Mathf.RoundToInt(condition.threshold),
                        HasDisabledGuard = hasGuard,
                        SelectedForFix = false,
                        TransitionRef = transition
                    });

                    // Only add once per transition even if it has both GestureLeft and GestureRight conditions
                    break;
                }
            }
        }

        /// <summary>
        /// Checks whether a transition already has a FacialExpressionsDisabled condition.
        /// </summary>
        private static bool HasDisabledGuard(AnimatorStateTransition transition)
        {
            foreach (AnimatorCondition condition in transition.conditions)
            {
                if (condition.parameter == DisabledParamName)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a layer has a layer-level FacialExpressionsDisabled guard,
        /// i.e. an AnyState transition to the empty guard state with the disabled condition.
        /// </summary>
        private static bool HasLayerLevelGuard(AnimatorControllerLayer layer)
        {
            AnimatorStateMachine stateMachine = layer.stateMachine;
            if (stateMachine == null) return false;

            // Check if there is an AnyState transition to a state named EmptyStateName
            // with a FacialExpressionsDisabled condition
            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                if (transition.destinationState != null &&
                    transition.destinationState.name == EmptyStateName)
                {
                    foreach (AnimatorCondition condition in transition.conditions)
                    {
                        if (condition.parameter == DisabledParamName)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // =====================================================================
        // Fix application
        // =====================================================================

        /// <summary>
        /// Ensures the FacialExpressionsDisabled bool parameter exists on the controller.
        /// If it already exists, this method is a no-op.
        /// </summary>
        internal static void EnsureParameterExists(AnimatorController controller)
        {
            AnimatorControllerParameter[] existingParams = controller.parameters;
            foreach (AnimatorControllerParameter param in existingParams)
            {
                if (param.name == DisabledParamName)
                {
                    return;
                }
            }

            controller.AddParameter(DisabledParamName, AnimatorControllerParameterType.Bool);
            Debug.Log($"{LogPrefix} Added parameter '{DisabledParamName}' to FX controller.");
        }

        /// <summary>
        /// Adds a FacialExpressionsDisabled == false (IfNot) condition to a single transition.
        /// </summary>
        internal static void ApplyTransitionGuard(TransitionAnalysis transition)
        {
            AnimatorStateTransition t = transition.TransitionRef;
            if (t == null) return;

            // Record undo on the transition asset itself
            Undo.RecordObject(t, "Add FacialExpressionsDisabled condition");

            AnimatorCondition newCondition = new AnimatorCondition
            {
                mode = AnimatorConditionMode.IfNot, // IfNot = false for bool
                parameter = DisabledParamName,
                threshold = 0f
            };

            List<AnimatorCondition> conditions = new List<AnimatorCondition>(t.conditions) { newCondition };
            t.conditions = conditions.ToArray();

            EditorUtility.SetDirty(t);
        }

        /// <summary>
        /// Applies a layer-level guard by creating an empty state and an AnyState transition
        /// that activates when FacialExpressionsDisabled is true, preventing all other
        /// transitions in the layer from firing.
        /// </summary>
        internal static void ApplyLayerGuard(AnimatorController controller, LayerAnalysis layer)
        {
            AnimatorControllerLayer[] controllerLayers = controller.layers;
            if (layer.LayerIndex < 0 || layer.LayerIndex >= controllerLayers.Length) return;

            AnimatorControllerLayer targetLayer = controllerLayers[layer.LayerIndex];
            AnimatorStateMachine stateMachine = targetLayer.stateMachine;
            if (stateMachine == null) return;

            Undo.RecordObject(stateMachine, "Add layer-level FacialExpressionsDisabled guard");

            // Create the empty state
            AnimatorState emptyState = stateMachine.AddState(EmptyStateName, new Vector3(30f, -80f, 0f));
            emptyState.writeDefaultValues = false;

            // Create AnyState -> Empty State transition with FacialExpressionsDisabled == true
            AnimatorStateTransition anyTransition = stateMachine.AddAnyStateTransition(emptyState);
            anyTransition.hasExitTime = false;
            anyTransition.duration = 0f;
            anyTransition.canTransitionToSelf = true;
            anyTransition.AddCondition(AnimatorConditionMode.If, 0f, DisabledParamName);

            // Move the new transition to the top of the AnyState transitions list so it
            // has highest priority. Unity evaluates AnyState transitions in order — if
            // existing gesture transitions come first, they will match before our guard
            // and the layer won't be disabled.
            AnimatorStateTransition[] anyTransitions = stateMachine.anyStateTransitions;
            if (anyTransitions.Length > 1)
            {
                // The newly added transition is at the end; rotate it to index 0
                List<AnimatorStateTransition> reordered = new List<AnimatorStateTransition>(anyTransitions.Length);
                reordered.Add(anyTransitions[anyTransitions.Length - 1]); // our new guard
                for (int i = 0; i < anyTransitions.Length - 1; i++)
                {
                    reordered.Add(anyTransitions[i]);
                }
                stateMachine.anyStateTransitions = reordered.ToArray();
            }

            // Reassign layer array since Unity uses copy-on-read for layers
            controller.layers = controllerLayers;

            EditorUtility.SetDirty(stateMachine);
        }

        /// <summary>
        /// Orchestrates applying all selected fixes (transition guards and layer guards)
        /// to the given controller. Registers Undo, ensures the parameter exists,
        /// iterates layers, marks dirty, saves assets, and logs a summary.
        /// </summary>
        /// <returns>A tuple of (transitionFixes, layerFixes) counts.</returns>
        internal static (int transitionFixes, int layerFixes) ApplySelectedFixes(AnimatorController controller, List<LayerAnalysis> layers)
        {
            Undo.RegisterCompleteObjectUndo(controller, "Apply FacialExpressionsDisabled Guards");

            EnsureParameterExists(controller);

            int transitionFixCount = 0;
            int layerFixCount = 0;

            foreach (LayerAnalysis layer in layers)
            {
                // Per-transition fixes
                foreach (TransitionAnalysis transition in layer.GestureTransitions)
                {
                    if (transition.SelectedForFix && !transition.HasDisabledGuard)
                    {
                        ApplyTransitionGuard(transition);
                        transitionFixCount++;
                    }
                }

                // Layer-level fix
                if (layer.SelectedForLayerDisable && !layer.AlreadyHasLayerGuard)
                {
                    ApplyLayerGuard(controller, layer);
                    layerFixCount++;
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

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

            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
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
