using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Shared IMGUI rendering for FX gesture/blink analysis results. Both the standalone
    /// <see cref="FXGestureChecker"/> window and the Avatar Setup Wizard's FX Check step draw
    /// their results through this class so the layer/transition/blink UI lives in exactly one place.
    /// The selection toggles mutate the shared <see cref="FXGestureCheckerCore"/> data models in
    /// place; each consumer keeps ownership of its own expand/show state and apply action.
    /// </summary>
    internal static class FXGestureCheckerUI
    {
        private static GUIStyle layerHeaderStyle;
        private static GUIStyle guardedLabelStyle;
        private static GUIStyle confidenceHighStyle;
        private static GUIStyle confidenceMediumStyle;
        private static GUIStyle confidenceLowStyle;

        private static void EnsureStyles()
        {
            if (layerHeaderStyle != null) return;

            layerHeaderStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };

            guardedLabelStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Italic };
            guardedLabelStyle.normal.textColor = new Color(0.3f, 0.75f, 0.3f);

            confidenceHighStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            confidenceHighStyle.normal.textColor = new Color(0.3f, 0.85f, 0.3f);

            confidenceMediumStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            confidenceMediumStyle.normal.textColor = new Color(0.9f, 0.75f, 0.2f);

            confidenceLowStyle = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
            confidenceLowStyle.normal.textColor = new Color(0.85f, 0.4f, 0.3f);
        }

        // =====================================================================
        // Gesture layers
        // =====================================================================

        /// <summary>
        /// Draws a foldout per gesture layer, with a layer-disable toggle and individual
        /// transition rows. Expansion state is owned by the caller via <paramref name="expandedLayers"/>.
        /// </summary>
        internal static void DrawGestureLayers(
            List<FXGestureCheckerCore.LayerAnalysis> layers,
            HashSet<int> expandedLayers)
        {
            if (layers == null) return;
            EnsureStyles();

            for (int i = 0; i < layers.Count; i++)
            {
                DrawLayerAnalysis(layers[i], expandedLayers);
                EditorGUILayout.Space(4f);
            }
        }

        private static void DrawLayerAnalysis(
            FXGestureCheckerCore.LayerAnalysis layer,
            HashSet<int> expandedLayers)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                bool isExpanded = expandedLayers.Contains(layer.LayerIndex);
                string gestureCount = $"{layer.GestureTransitions.Count} gesture transition{(layer.GestureTransitions.Count != 1 ? "s" : "")}";

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newExpanded = EditorGUILayout.Foldout(isExpanded, "", true);
                    EditorGUILayout.LabelField($"Layer: {layer.LayerName}", layerHeaderStyle);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"({gestureCount})", EditorStyles.miniLabel, GUILayout.Width(150f));

                    if (newExpanded != isExpanded)
                    {
                        if (newExpanded) expandedLayers.Add(layer.LayerIndex);
                        else expandedLayers.Remove(layer.LayerIndex);
                    }
                }

                if (!isExpanded) return;

                EditorGUI.indentLevel++;

                EditorGUILayout.Space(4f);
                if (layer.AlreadyHasLayerGuard)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ToggleLeft("Disable entire layer when FacialExpressionsDisabled", true);
                        }
                        EditorGUILayout.LabelField("[Applied]", guardedLabelStyle, GUILayout.Width(60f));
                    }
                }
                else
                {
                    layer.SelectedForLayerDisable = EditorGUILayout.ToggleLeft(
                        "Disable entire layer when FacialExpressionsDisabled",
                        layer.SelectedForLayerDisable);
                }

                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(4f);

                foreach (FXGestureCheckerCore.TransitionAnalysis transition in layer.GestureTransitions)
                {
                    DrawTransitionRow(transition);
                }

                EditorGUI.indentLevel--;
            }
        }

        private static void DrawTransitionRow(FXGestureCheckerCore.TransitionAnalysis transition)
        {
            string gestureName = FXGestureCheckerCore.GetGestureName(transition.GestureValue);
            string label = $"{transition.SourceName} -> {transition.DestinationName} ({transition.GestureParameter}={gestureName})";

            using (new EditorGUILayout.HorizontalScope())
            {
                if (transition.HasDisabledGuard)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(label, true);
                    }
                    EditorGUILayout.LabelField("[Applied]", guardedLabelStyle, GUILayout.Width(60f));
                }
                else
                {
                    transition.SelectedForFix = EditorGUILayout.ToggleLeft(label, transition.SelectedForFix);
                }
            }
        }

        // =====================================================================
        // Blink layers
        // =====================================================================

        /// <summary>
        /// Draws the "Blink Layer Detection" section: a "Blink Layer Detection" header, the
        /// highest-confidence tier of detected blink layers (with the rest behind a "show more"
        /// toggle owned by the caller via <paramref name="showAll"/>), and a guard toggle plus
        /// detection reasons per layer.
        /// </summary>
        internal static void DrawBlinkSection(
            List<FXGestureCheckerCore.BlinkLayerAnalysis> blinkLayers,
            HashSet<int> expandedBlinkLayers,
            ref bool showAll)
        {
            if (blinkLayers == null || blinkLayers.Count == 0) return;
            EnsureStyles();

            EditorGUILayout.LabelField("Blink Layer Detection", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            // By default show only the highest-confidence tier; lower-confidence layers are opt-in.
            FXGestureCheckerCore.BlinkConfidence topConfidence = blinkLayers[0].Confidence;
            int topCount = blinkLayers.Count(l => l.Confidence == topConfidence);
            int lowerCount = blinkLayers.Count - topCount;
            int visibleCount = showAll ? blinkLayers.Count : topCount;

            for (int i = 0; i < visibleCount; i++)
            {
                DrawBlinkLayerAnalysis(blinkLayers[i], expandedBlinkLayers);
                EditorGUILayout.Space(4f);
            }

            if (lowerCount > 0)
            {
                showAll = EditorGUILayout.ToggleLeft(
                    $"Show {lowerCount} more layer{(lowerCount != 1 ? "s" : "")} with lower confidence",
                    showAll);
            }
        }

        private static void DrawBlinkLayerAnalysis(
            FXGestureCheckerCore.BlinkLayerAnalysis blinkLayer,
            HashSet<int> expandedBlinkLayers)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                bool isExpanded = expandedBlinkLayers.Contains(blinkLayer.LayerIndex);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newExpanded = EditorGUILayout.Foldout(isExpanded, "", true);
                    EditorGUILayout.LabelField($"Layer: {blinkLayer.LayerName}", layerHeaderStyle);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(blinkLayer.Confidence.ToString(),
                        GetConfidenceStyle(blinkLayer.Confidence), GUILayout.Width(60f));

                    if (newExpanded != isExpanded)
                    {
                        if (newExpanded) expandedBlinkLayers.Add(blinkLayer.LayerIndex);
                        else expandedBlinkLayers.Remove(blinkLayer.LayerIndex);
                    }
                }

                if (!isExpanded) return;

                EditorGUI.indentLevel++;

                EditorGUILayout.Space(4f);
                if (blinkLayer.AlreadyHasBlinkGuard)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ToggleLeft("Disable blink when EyeTrackingActive > 0.5", true);
                        }
                        EditorGUILayout.LabelField("[Applied]", guardedLabelStyle, GUILayout.Width(60f));
                    }
                }
                else
                {
                    blinkLayer.SelectedForGuard = EditorGUILayout.ToggleLeft(
                        "Disable blink when EyeTrackingActive > 0.5",
                        blinkLayer.SelectedForGuard);
                }

                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(4f);

                EditorGUILayout.LabelField("Detection reasons:", EditorStyles.miniLabel);
                foreach (string reason in blinkLayer.DetectionReasons)
                {
                    EditorGUILayout.LabelField($"  • {reason}", PawlygonEditorUI.RichMiniLabelStyle);
                }

                EditorGUI.indentLevel--;
            }
        }

        private static GUIStyle GetConfidenceStyle(FXGestureCheckerCore.BlinkConfidence confidence)
        {
            switch (confidence)
            {
                case FXGestureCheckerCore.BlinkConfidence.High:
                    return confidenceHighStyle;
                case FXGestureCheckerCore.BlinkConfidence.Medium:
                    return confidenceMediumStyle;
                default:
                    return confidenceLowStyle;
            }
        }
    }
}
