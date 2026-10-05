using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// The recommended FX guard flow, shared by the FX Gesture Checker window and the Avatar Setup Wizard:
    /// <list type="bullet">
    /// <item>a layer guard on every gesture expression layer that isn't protected yet;</item>
    /// <item>a repair of every outdated guard (layer or blink, at any blink confidence);</item>
    /// <item>a blink guard on every blink layer detected with High confidence.</item>
    /// </list>
    /// Per-transition guards are never part of it; they stay an advanced, manual choice.
    /// </summary>
    internal static partial class FXGestureCheckerCore
    {
        /// <summary>How well blinking is protected, for the summary card.</summary>
        internal enum BlinkProtection
        {
            /// <summary>No High-confidence blink layer and no blink guard.</summary>
            NotFound,
            /// <summary>A High-confidence blink layer has no blink guard.</summary>
            NotProtected,
            /// <summary>Every blink guard is in place, but at least one is outdated.</summary>
            NeedsRepair,
            /// <summary>Every High-confidence or guarded blink layer has an up-to-date guard.</summary>
            Protected
        }

        /// <summary>
        /// What "Apply Recommended" would change on an analyzed controller, plus the protection counts the
        /// summary card shows. Built by <see cref="GetRecommendedPlan"/>; it never changes the analysis.
        /// </summary>
        internal sealed class RecommendedPlan
        {
            /// <summary>Gesture expression layers found.</summary>
            public int GestureLayerCount;

            /// <summary>Gesture expression layers with an up-to-date layer guard.</summary>
            public int GestureLayersProtected;

            public int LayerGuardsToAdd;
            public int LayerGuardsToRepair;
            public int BlinkGuardsToAdd;
            public int BlinkGuardsToRepair;

            /// <summary>Every outdated guard (layer and blink), including ones a parameter conflict blocks.</summary>
            public int OutdatedGuards;

            public BlinkProtection Blink;

            /// <summary>Names of the High-confidence or guarded blink layers (the ones the Blink row is about).</summary>
            public readonly List<string> BlinkLayerNames = new List<string>();

            /// <summary>Unguarded Medium/Low-confidence blink candidates, left for the user to review.</summary>
            public int PossibleBlinkLayers;

            /// <summary>Why FacialExpressionsDisabled guards can't be written (parameter type conflict), or null.</summary>
            public string GestureGuardError;

            /// <summary>Why EyeTrackingActive guards can't be written (parameter type conflict), or null.</summary>
            public string BlinkGuardError;

            /// <summary>One plain-language line per change, in apply order. Blocked changes are left out.</summary>
            public readonly List<string> Changes = new List<string>();

            public int TotalChanges => LayerGuardsToAdd + LayerGuardsToRepair + BlinkGuardsToAdd + BlinkGuardsToRepair;
            public bool HasChanges => TotalChanges > 0;
            public bool GesturesProtected => GestureLayerCount > 0 && GestureLayersProtected == GestureLayerCount;

            /// <summary>Label for the primary button, e.g. "Apply Recommended (3 changes)".</summary>
            public string ButtonLabel => HasChanges
                ? $"Apply Recommended ({TotalChanges} change{(TotalChanges != 1 ? "s" : "")})"
                : "Apply Recommended";
        }

        /// <summary>
        /// What an apply wrote, for the status message.
        /// </summary>
        internal struct ApplyCounts
        {
            public int TransitionGuards;
            public int LayerGuards;
            public int BlinkGuards;

            public int Total => TransitionGuards + LayerGuards + BlinkGuards;

            /// <summary>E.g. "Protected 2 expression layers and 1 blink layer".</summary>
            public string Describe()
            {
                var parts = new List<string>();
                if (LayerGuards > 0) parts.Add($"{LayerGuards} expression layer{(LayerGuards != 1 ? "s" : "")}");
                if (BlinkGuards > 0) parts.Add($"{BlinkGuards} blink layer{(BlinkGuards != 1 ? "s" : "")}");
                if (TransitionGuards > 0) parts.Add($"{TransitionGuards} transition{(TransitionGuards != 1 ? "s" : "")}");
                if (parts.Count == 0) return "Nothing needed changing";

                string joined = parts.Count == 1
                    ? parts[0]
                    : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
                return "Protected " + joined;
            }
        }

        // =====================================================================
        // Recommendation rules
        // =====================================================================

        /// <summary>True when the recommended flow adds or repairs this gesture layer's layer guard.</summary>
        internal static bool IsLayerGuardRecommended(LayerAnalysis layer)
        {
            return layer != null && !layer.AlreadyHasLayerGuard;
        }

        /// <summary>True when the recommended flow adds or repairs this layer's blink guard.</summary>
        internal static bool IsBlinkGuardRecommended(BlinkLayerAnalysis blinkLayer)
        {
            if (blinkLayer == null || blinkLayer.AlreadyHasBlinkGuard) return false;
            return blinkLayer.BlinkGuardNeedsRepair || blinkLayer.Confidence == BlinkConfidence.High;
        }

        /// <summary>
        /// True when a return-to-neutral transition carries a FacialExpressionsDisabled condition that
        /// keeps it from firing while face tracking is on (and no layer guard resets the face instead).
        /// </summary>
        internal static bool BlocksReset(LayerAnalysis layer, TransitionAnalysis neutralTransition)
        {
            return neutralTransition != null && neutralTransition.HasDisabledGuard && !layer.AlreadyHasLayerGuard;
        }

        /// <summary>
        /// True when a gesture layer should be shown expanded: it isn't protected, its guard needs a repair,
        /// or a return-to-neutral transition is blocked.
        /// </summary>
        internal static bool LayerNeedsAttention(LayerAnalysis layer)
        {
            return layer != null && (!layer.AlreadyHasLayerGuard || layer.NeutralTransitions.Any(t => BlocksReset(layer, t)));
        }

        /// <summary>True when a blink layer should be shown expanded (the recommendation touches it).</summary>
        internal static bool BlinkLayerNeedsAttention(BlinkLayerAnalysis blinkLayer)
        {
            return IsBlinkGuardRecommended(blinkLayer);
        }

        // =====================================================================
        // Plan
        // =====================================================================

        /// <summary>
        /// Builds the recommended plan for an analysis without changing its selection. Returns an empty
        /// plan for a null or failed analysis.
        /// </summary>
        internal static RecommendedPlan GetRecommendedPlan(AnalysisResult analysis)
        {
            var plan = new RecommendedPlan();
            if (analysis == null || !analysis.Success) return plan;

            List<LayerAnalysis> layers = analysis.Layers ?? new List<LayerAnalysis>();
            List<BlinkLayerAnalysis> blinkLayers = analysis.BlinkLayers ?? new List<BlinkLayerAnalysis>();

            plan.GestureGuardError = GetLayerGuardParameterError(analysis.FXController);
            plan.BlinkGuardError = GetBlinkGuardParameterError(analysis.FXController);

            plan.GestureLayerCount = layers.Count;
            plan.GestureLayersProtected = layers.Count(l => l.AlreadyHasLayerGuard);
            plan.OutdatedGuards = layers.Count(l => l.LayerGuardNeedsRepair) + blinkLayers.Count(b => b.BlinkGuardNeedsRepair);

            if (plan.GestureGuardError == null)
            {
                foreach (LayerAnalysis layer in layers.Where(IsLayerGuardRecommended))
                {
                    if (layer.LayerGuardNeedsRepair) plan.LayerGuardsToRepair++;
                    else plan.LayerGuardsToAdd++;
                    plan.Changes.Add(DescribeLayerGuardChange(layer));
                }
            }

            if (plan.BlinkGuardError == null)
            {
                foreach (BlinkLayerAnalysis blinkLayer in blinkLayers.Where(IsBlinkGuardRecommended))
                {
                    if (blinkLayer.BlinkGuardNeedsRepair) plan.BlinkGuardsToRepair++;
                    else plan.BlinkGuardsToAdd++;
                    plan.Changes.Add(DescribeBlinkGuardChange(blinkLayer));
                }
            }

            // The Blink row covers the layers the recommendation is about: High confidence, or already guarded.
            List<BlinkLayerAnalysis> relevant = blinkLayers
                .Where(b => b.Confidence == BlinkConfidence.High || b.AlreadyHasBlinkGuard || b.BlinkGuardNeedsRepair)
                .ToList();
            plan.BlinkLayerNames.AddRange(relevant.Select(b => b.LayerName));
            plan.PossibleBlinkLayers = blinkLayers.Count - relevant.Count;

            if (relevant.Count == 0) plan.Blink = BlinkProtection.NotFound;
            else if (relevant.Any(b => !b.AlreadyHasBlinkGuard && !b.BlinkGuardNeedsRepair)) plan.Blink = BlinkProtection.NotProtected;
            else if (relevant.Any(b => b.BlinkGuardNeedsRepair)) plan.Blink = BlinkProtection.NeedsRepair;
            else plan.Blink = BlinkProtection.Protected;

            return plan;
        }

        /// <summary>
        /// Plain-language lines for what applying the current selection would change. Pass
        /// <paramref name="gestures"/> / <paramref name="blink"/> to describe only one part.
        /// </summary>
        internal static List<string> DescribeSelectedChanges(AnalysisResult analysis, bool gestures = true, bool blink = true)
        {
            var changes = new List<string>();
            if (analysis == null || !analysis.Success) return changes;

            if (gestures && analysis.Layers != null)
            {
                foreach (LayerAnalysis layer in analysis.Layers)
                {
                    if (layer.SelectedForLayerDisable && !layer.AlreadyHasLayerGuard)
                    {
                        changes.Add(DescribeLayerGuardChange(layer));
                    }

                    int transitions = layer.GestureTransitions.Count(t => t.SelectedForFix && !t.HasDisabledGuard);
                    if (transitions > 0)
                    {
                        changes.Add($"Block {transitions} gesture transition{(transitions != 1 ? "s" : "")} in layer '{layer.LayerName}' while face tracking is active");
                    }
                }
            }

            if (blink && analysis.BlinkLayers != null)
            {
                foreach (BlinkLayerAnalysis blinkLayer in analysis.BlinkLayers)
                {
                    if (blinkLayer.SelectedForGuard && !blinkLayer.AlreadyHasBlinkGuard)
                    {
                        changes.Add(DescribeBlinkGuardChange(blinkLayer));
                    }
                }
            }

            return changes;
        }

        private static string DescribeLayerGuardChange(LayerAnalysis layer)
        {
            return layer.LayerGuardNeedsRepair
                ? $"Repair the outdated face tracking guard on layer '{layer.LayerName}'"
                : $"Turn off the gesture expressions in layer '{layer.LayerName}' while face tracking is active";
        }

        private static string DescribeBlinkGuardChange(BlinkLayerAnalysis blinkLayer)
        {
            return blinkLayer.BlinkGuardNeedsRepair
                ? $"Repair the outdated blink guard on layer '{blinkLayer.LayerName}'"
                : $"Stop the blinking in layer '{blinkLayer.LayerName}' while eye tracking is active";
        }

        /// <summary>
        /// Text for the confirmation dialog: the target, then one bullet per change (capped), then how to
        /// undo. <paramref name="targetDescription"/> completes "This changes ...", e.g. "a copy at
        /// 'Assets/FX_Modified.controller'" or "the original controller 'Assets/FX.controller', in place".
        /// </summary>
        internal static string BuildConfirmationMessage(IList<string> changes, string targetDescription)
        {
            const int MaxLines = 12;
            var text = new StringBuilder();
            text.Append("This changes ").Append(targetDescription).Append(":\n\n");

            for (int i = 0; i < changes.Count && i < MaxLines; i++)
            {
                text.Append("• ").Append(changes[i]).Append('\n');
            }

            if (changes.Count > MaxLines)
            {
                text.Append($"• …and {changes.Count - MaxLines} more\n");
            }

            text.Append("\nUndo (Ctrl+Z) reverts it.");
            return text.ToString();
        }

        // =====================================================================
        // Selection
        // =====================================================================

        /// <summary>
        /// Sets the selection flags to the recommendation: layer guards on unprotected gesture layers,
        /// repairs of outdated guards, blink guards on High-confidence blink layers, and no per-transition
        /// guards. Everything else is deselected.
        /// </summary>
        internal static void SelectRecommended(AnalysisResult analysis)
        {
            if (analysis == null) return;
            SelectRecommendedGestures(analysis.Layers);
            SelectRecommendedBlink(analysis.BlinkLayers);
        }

        /// <summary>The gesture part of <see cref="SelectRecommended"/>.</summary>
        internal static void SelectRecommendedGestures(List<LayerAnalysis> layers)
        {
            if (layers == null) return;

            foreach (LayerAnalysis layer in layers)
            {
                layer.SelectedForLayerDisable = IsLayerGuardRecommended(layer);
                foreach (TransitionAnalysis transition in layer.GestureTransitions)
                {
                    transition.SelectedForFix = false;
                }
            }
        }

        /// <summary>The blink part of <see cref="SelectRecommended"/>.</summary>
        internal static void SelectRecommendedBlink(List<BlinkLayerAnalysis> blinkLayers)
        {
            if (blinkLayers == null) return;

            foreach (BlinkLayerAnalysis blinkLayer in blinkLayers)
            {
                blinkLayer.SelectedForGuard = IsBlinkGuardRecommended(blinkLayer);
            }
        }

        /// <summary>
        /// Copies the selection flags from one analysis to another of the same controller structure (e.g.
        /// the original and its copy, or before and after a re-analysis), matching layers by index and
        /// transitions by <see cref="TransitionAnalysis.Key"/>. Items missing from <paramref name="from"/>
        /// keep their flags.
        /// </summary>
        internal static void CopySelection(AnalysisResult from, AnalysisResult to, bool gestures = true, bool blink = true)
        {
            if (from == null || to == null) return;

            if (gestures && from.Layers != null && to.Layers != null)
            {
                var layersByIndex = from.Layers.ToDictionary(l => l.LayerIndex);
                foreach (LayerAnalysis layer in to.Layers)
                {
                    if (!layersByIndex.TryGetValue(layer.LayerIndex, out LayerAnalysis source)) continue;

                    layer.SelectedForLayerDisable = source.SelectedForLayerDisable;
                    var selectedKeys = new HashSet<string>(source.GestureTransitions
                        .Where(t => t.SelectedForFix && t.Key != null)
                        .Select(t => t.Key));

                    foreach (TransitionAnalysis transition in layer.GestureTransitions)
                    {
                        transition.SelectedForFix = transition.Key != null && selectedKeys.Contains(transition.Key);
                    }
                }
            }

            if (blink && from.BlinkLayers != null && to.BlinkLayers != null)
            {
                var blinkByIndex = from.BlinkLayers.ToDictionary(b => b.LayerIndex);
                foreach (BlinkLayerAnalysis blinkLayer in to.BlinkLayers)
                {
                    if (blinkByIndex.TryGetValue(blinkLayer.LayerIndex, out BlinkLayerAnalysis source))
                    {
                        blinkLayer.SelectedForGuard = source.SelectedForGuard;
                    }
                }
            }
        }

        // =====================================================================
        // Applying
        // =====================================================================

        /// <summary>
        /// Applies the selected gesture and/or blink guards of <paramref name="analysis"/> (which must be an
        /// analysis of <paramref name="controller"/>) as one undo step. Parts blocked by a parameter
        /// conflict are skipped (and logged).
        /// </summary>
        internal static ApplyCounts ApplySelected(AnimatorController controller, AnalysisResult analysis,
            bool gestures = true, bool blink = true)
        {
            var counts = new ApplyCounts();
            if (controller == null || analysis == null) return counts;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Face Tracking Guards");

            if (gestures && analysis.Layers != null &&
                analysis.Layers.Any(l => (l.SelectedForLayerDisable && !l.AlreadyHasLayerGuard) ||
                                         l.GestureTransitions.Any(t => t.SelectedForFix && !t.HasDisabledGuard)))
            {
                (counts.TransitionGuards, counts.LayerGuards) = ApplySelectedFixes(controller, analysis.Layers);
            }

            if (blink && analysis.BlinkLayers != null &&
                analysis.BlinkLayers.Any(b => b.SelectedForGuard && !b.AlreadyHasBlinkGuard))
            {
                counts.BlinkGuards = ApplySelectedBlinkGuards(controller, analysis.BlinkLayers);
            }

            Undo.CollapseUndoOperations(undoGroup);
            return counts;
        }

        /// <summary>
        /// Analyzes <paramref name="controller"/>, selects the recommendation and applies it as one undo
        /// step. Use it on the controller that should be written (e.g. a copy made with
        /// <see cref="CopyFXController"/>); re-analyze afterwards to refresh the UI.
        /// </summary>
        internal static ApplyCounts ApplyRecommended(AnimatorController controller)
        {
            if (controller == null) return new ApplyCounts();

            AnalysisResult analysis = Analyze(controller);
            SelectRecommended(analysis);
            return ApplySelected(controller, analysis);
        }
    }
}
