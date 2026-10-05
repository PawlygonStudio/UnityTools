using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Shared IMGUI rendering for FX gesture/blink analysis results. Both the standalone
    /// <see cref="FXGestureChecker"/> window and the Avatar Setup Wizard's FX Check step draw
    /// their results through this class so the summary card, layer, transition and blink UI live in
    /// exactly one place. The selection toggles mutate the shared <see cref="FXGestureCheckerCore"/> data
    /// models in place; each consumer keeps ownership of its own view state and apply action.
    /// </summary>
    internal static class FXGestureCheckerUI
    {
        // =====================================================================
        // Texts
        // =====================================================================

        /// <summary>One-paragraph explanation of what the guards do, shown on the summary card.</summary>
        internal const string GuardExplanation =
            "With face tracking on, your real face should drive the avatar: hand gestures shouldn't swap in " +
            "expressions and the blink animation shouldn't close the eyes over your real ones. Guards turn " +
            "those layers off only while face or eye tracking is active, and everything works as before when " +
            "tracking stops.";

        private const string LayerGuardTooltip =
            "Adds a guard to this layer: while face tracking is active (the FacialExpressionsDisabled parameter " +
            "is on) the layer switches to an empty state, so hand gestures can't change the face and the face " +
            "goes back to neutral. When face tracking stops, the layer works as before. On layers with Write " +
            "Defaults off, the guard plays a small reset animation (stored inside the controller) to put the face back.";

        private const string OutdatedLayerGuardTooltip =
            "This layer has a guard from an older version of this tool. Once face tracking turns on, the layer " +
            "can get stuck in the guard, or keep showing the expression that was active. Repairing fixes the " +
            "existing guard in place instead of adding a second one.";

        private const string OutdatedBlinkGuardTooltip =
            "This layer has a blink guard from an older version of this tool. Blinking may never come back after " +
            "eye tracking turns off, or the eyes can stay half-closed. Repairing fixes the existing guard in place " +
            "instead of adding a second one.";

        private const string BlinkGuardTooltip =
            "Adds a guard to this layer: while eye tracking is active (the EyeTrackingActive parameter is above " +
            "0.5) the blink animation stops, so it can't close the eyes over your real ones. Blinking comes back " +
            "when eye tracking stops.";

        private const string BlocksResetTooltip =
            "This 'back to neutral' transition only fires while face tracking is off (it has a " +
            "FacialExpressionsDisabled condition, added by hand or by an older version of this tool). With face " +
            "tracking on, the face can get stuck on the last expression. Apply the layer guard (it resets the " +
            "face itself) or remove that condition in the Animator window.";

        private const string PerTransitionTooltip =
            "Blocks single gesture transitions instead of the whole layer. An expression that is already showing " +
            "when face tracking turns on stays on, so the layer guard above is usually the better choice.";

        private const string PartlyProtectedTooltip =
            "Every gesture transition in this layer has its own guard, so no new expression starts while face " +
            "tracking is active. An expression already showing when tracking starts stays on; the layer guard also clears it.";

        private const string ConfidenceLegend =
            "Confidence: how sure the checker is that a layer is your blink animation. High: its name and its " +
            "animations both point to blinking (Apply Recommended guards these). Medium / Low: check the reasons first.";

        // =====================================================================
        // Styles
        // =====================================================================

        private static GUIStyle foldoutHeaderStyle;
        private static GUIStyle rowTitleStyle;
        private static GUIStyle mutedLabelStyle;
        private static GUIStyle mutedMiniStyle;
        private static bool stylesBuiltForProSkin;

        private static void EnsureStyles()
        {
            PawlygonEditorUI.EnsureStyles();
            if (foldoutHeaderStyle != null && stylesBuiltForProSkin == EditorGUIUtility.isProSkin) return;
            stylesBuiltForProSkin = EditorGUIUtility.isProSkin;

            foldoutHeaderStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };

            rowTitleStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };

            mutedLabelStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };
            mutedLabelStyle.normal.textColor = PawlygonEditorUI.MutedColor;

            mutedMiniStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            mutedMiniStyle.normal.textColor = PawlygonEditorUI.MutedColor;
        }

        // =====================================================================
        // View state
        // =====================================================================

        /// <summary>
        /// What the consumer's results view has expanded or open. Keep one per window (or wizard) and only
        /// reset it with <see cref="ExpandLayersNeedingAttention"/> when a different avatar or controller is
        /// analyzed, so expansions survive applies and re-analysis.
        /// </summary>
        internal sealed class ViewState
        {
            public readonly HashSet<int> ExpandedLayers = new HashSet<int>();
            public readonly HashSet<int> ExpandedBlinkLayers = new HashSet<int>();
            public bool ShowAllBlinkLayers;
            public bool AdvancedOpen;

            /// <summary>0 = Expressions, 1 = Blink.</summary>
            public int AdvancedTab;

            /// <summary>
            /// Expands only the layers that need attention (unprotected, outdated, blocked reset,
            /// recommended blink guard) and collapses the rest.
            /// </summary>
            public void ExpandLayersNeedingAttention(FXGestureCheckerCore.AnalysisResult analysis)
            {
                ExpandedLayers.Clear();
                ExpandedBlinkLayers.Clear();
                ShowAllBlinkLayers = false;
                if (analysis == null) return;

                if (analysis.Layers != null)
                {
                    foreach (FXGestureCheckerCore.LayerAnalysis layer in analysis.Layers.Where(FXGestureCheckerCore.LayerNeedsAttention))
                    {
                        ExpandedLayers.Add(layer.LayerIndex);
                    }
                }

                if (analysis.BlinkLayers != null)
                {
                    foreach (FXGestureCheckerCore.BlinkLayerAnalysis blinkLayer in analysis.BlinkLayers.Where(FXGestureCheckerCore.BlinkLayerNeedsAttention))
                    {
                        ExpandedBlinkLayers.Add(blinkLayer.LayerIndex);
                    }
                }
            }
        }

        // =====================================================================
        // Summary card
        // =====================================================================

        /// <summary>
        /// Draws the "Face tracking protection" card: what the guards do, then three rows with badges
        /// (gesture expressions x of y layers protected; blinking protected / not protected / not found;
        /// outdated guards to repair) and any parameter conflict. Draw the "Apply Recommended" button
        /// (<see cref="FXGestureCheckerCore.RecommendedPlan.ButtonLabel"/>) next to it.
        /// </summary>
        internal static void DrawSummaryCard(FXGestureCheckerCore.RecommendedPlan plan)
        {
            if (plan == null) return;
            EnsureStyles();

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader("Face tracking protection", GuardExplanation);

                DrawGestureSummaryRow(plan);
                DrawBlinkSummaryRow(plan);
                DrawOutdatedSummaryRow(plan);

                string conflict = plan.GestureGuardError ?? plan.BlinkGuardError;
                if (conflict != null)
                {
                    DrawSummaryRow("Parameters", "Conflict", PawlygonEditorUI.BadgeKind.Error, conflict,
                        "A parameter has a type the guards can't use");
                    if (plan.GestureGuardError != null) EditorGUILayout.HelpBox(plan.GestureGuardError, MessageType.Error);
                    if (plan.BlinkGuardError != null) EditorGUILayout.HelpBox(plan.BlinkGuardError, MessageType.Error);
                }

                EditorGUILayout.Space(4f);
                string footer = plan.HasChanges
                    ? $"Apply Recommended makes {plan.TotalChanges} change{(plan.TotalChanges != 1 ? "s" : "")}: layer guards on unprotected expression layers, repairs of outdated guards and blink guards on clearly detected blink layers."
                    : conflict != null
                        ? "Fix the parameter conflict above, then the recommended guards can be added."
                        : "Nothing to do: every recommended guard is in place.";
                EditorGUILayout.LabelField(footer, PawlygonEditorUI.SubLabelStyle);
            }
        }

        private static void DrawGestureSummaryRow(FXGestureCheckerCore.RecommendedPlan plan)
        {
            const string Title = "Gesture expressions";
            if (plan.GestureLayerCount == 0)
            {
                DrawSummaryRow(Title, "None found", PawlygonEditorUI.BadgeKind.Neutral, null,
                    "No hand-gesture expressions in this FX controller");
                return;
            }

            string detail = $"{plan.GestureLayersProtected} of {plan.GestureLayerCount} layer{(plan.GestureLayerCount != 1 ? "s" : "")} protected";
            if (plan.GesturesProtected)
            {
                DrawSummaryRow(Title, "Protected", PawlygonEditorUI.BadgeKind.Ok, LayerGuardTooltip, detail);
            }
            else if (plan.GestureGuardError != null)
            {
                DrawSummaryRow(Title, "Conflict", PawlygonEditorUI.BadgeKind.Error, plan.GestureGuardError, detail);
            }
            else
            {
                DrawSummaryRow(Title, "Not protected", PawlygonEditorUI.BadgeKind.Warning,
                    "Hand gestures can still change the face while face tracking is active.", detail);
            }
        }

        private static void DrawBlinkSummaryRow(FXGestureCheckerCore.RecommendedPlan plan)
        {
            const string Title = "Blinking";
            string layerNames = string.Join(", ", plan.BlinkLayerNames.Select(n => $"'{n}'"));

            switch (plan.Blink)
            {
                case FXGestureCheckerCore.BlinkProtection.Protected:
                    DrawSummaryRow(Title, "Protected", PawlygonEditorUI.BadgeKind.Ok, BlinkGuardTooltip,
                        $"Blink layer {layerNames} stops while eye tracking is active");
                    break;
                case FXGestureCheckerCore.BlinkProtection.NeedsRepair:
                    DrawSummaryRow(Title, "Needs repair", PawlygonEditorUI.BadgeKind.Warning, OutdatedBlinkGuardTooltip,
                        $"Blink layer {layerNames} has an outdated guard");
                    break;
                case FXGestureCheckerCore.BlinkProtection.NotProtected:
                    DrawSummaryRow(Title, plan.BlinkGuardError != null ? "Conflict" : "Not protected",
                        plan.BlinkGuardError != null ? PawlygonEditorUI.BadgeKind.Error : PawlygonEditorUI.BadgeKind.Warning,
                        plan.BlinkGuardError ?? "The blink animation can close the eyes over eye tracking.",
                        $"Blink layer {layerNames} keeps blinking during eye tracking");
                    break;
                default:
                    DrawSummaryRow(Title, "Not found", PawlygonEditorUI.BadgeKind.Neutral,
                        "No layer was clearly detected as a blink animation.",
                        plan.PossibleBlinkLayers > 0
                            ? $"{plan.PossibleBlinkLayers} possible blink layer{(plan.PossibleBlinkLayers != 1 ? "s" : "")}: check under Advanced"
                            : "No blink animation in this FX controller");
                    break;
            }
        }

        private static void DrawOutdatedSummaryRow(FXGestureCheckerCore.RecommendedPlan plan)
        {
            const string Title = "Outdated guards";
            if (plan.OutdatedGuards == 0)
            {
                DrawSummaryRow(Title, "None", PawlygonEditorUI.BadgeKind.Ok, null, "No guards from older versions");
            }
            else
            {
                DrawSummaryRow(Title, "Needs repair", PawlygonEditorUI.BadgeKind.Warning,
                    "Guards added by an older version of this tool can leave the face or eyes stuck. Repairing fixes them in place.",
                    $"{plan.OutdatedGuards} guard{(plan.OutdatedGuards != 1 ? "s" : "")} to repair");
            }
        }

        private static void DrawSummaryRow(string title, string badge, PawlygonEditorUI.BadgeKind kind, string tooltip, string detail)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(title, rowTitleStyle, GUILayout.Width(140f));
                PawlygonEditorUI.DrawBadge(badge, kind, tooltip);
                GUILayout.Space(4f);
                GUILayout.Label(new GUIContent(detail, detail), mutedLabelStyle, GUILayout.MinWidth(40f));
            }
        }

        // =====================================================================
        // Advanced section
        // =====================================================================

        /// <summary>What the user clicked in <see cref="DrawAdvancedSection"/>.</summary>
        internal enum AdvancedAction { None, ApplyExpressions, ApplyBlink }

        /// <summary>
        /// Draws the "Advanced: choose individually" foldout with two tabs, Expressions (per-layer and
        /// per-transition guards) and Blink, each with its own "Apply Selected" secondary button. Returns
        /// which apply button was clicked; the caller performs the apply (and should call
        /// <c>GUIUtility.ExitGUI()</c> afterwards). Pass a reason to disable a tab's apply button.
        /// </summary>
        internal static AdvancedAction DrawAdvancedSection(
            FXGestureCheckerCore.AnalysisResult analysis,
            FXGestureCheckerCore.RecommendedPlan plan,
            ViewState view,
            string expressionsBlockedReason,
            string blinkBlockedReason)
        {
            if (analysis == null || view == null) return AdvancedAction.None;
            EnsureStyles();

            List<FXGestureCheckerCore.LayerAnalysis> layers = analysis.Layers ?? new List<FXGestureCheckerCore.LayerAnalysis>();
            List<FXGestureCheckerCore.BlinkLayerAnalysis> blinkLayers = analysis.BlinkLayers ?? new List<FXGestureCheckerCore.BlinkLayerAnalysis>();
            AdvancedAction action = AdvancedAction.None;

            // Draw with this event's value so the layout matches; the new value shows on the next repaint.
            bool open = view.AdvancedOpen;
            bool newOpen = EditorGUILayout.Foldout(open, new GUIContent("Advanced: choose individually",
                "Pick guards layer by layer or transition by transition, including blink layers with lower confidence."), true, foldoutHeaderStyle);
            if (newOpen != open)
            {
                view.AdvancedOpen = newOpen;
                GUI.changed = true;
            }

            if (!open) return action;

            EditorGUILayout.Space(4f);

            var tabs = new[]
            {
                new PawlygonEditorUI.TabSpec($"Expressions ({layers.Count})", done: plan != null && plan.GesturesProtected,
                    tooltip: "Guards for layers that change facial expressions with hand gestures."),
                new PawlygonEditorUI.TabSpec($"Blink ({blinkLayers.Count})", done: plan != null && plan.Blink == FXGestureCheckerCore.BlinkProtection.Protected,
                    tooltip: "Guards for layers that play a blink animation."),
            };

            int tab = Mathf.Clamp(view.AdvancedTab, 0, 1);
            int clicked = PawlygonEditorUI.DrawTabBar(tabs, tab);
            if (clicked >= 0)
            {
                view.AdvancedTab = clicked;
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(6f);

            if (tab == 0)
            {
                if (DrawExpressionsTab(layers, view, expressionsBlockedReason)) action = AdvancedAction.ApplyExpressions;
            }
            else
            {
                if (DrawBlinkTab(blinkLayers, view, blinkBlockedReason)) action = AdvancedAction.ApplyBlink;
            }

            return action;
        }

        private static bool DrawExpressionsTab(List<FXGestureCheckerCore.LayerAnalysis> layers, ViewState view, string blockedReason)
        {
            if (layers.Count == 0)
            {
                EditorGUILayout.HelpBox("This FX controller has no layers that change the face with hand gestures.", MessageType.Info);
                return false;
            }

            EditorGUILayout.LabelField(
                "Each layer below switches facial expressions with hand gestures (GestureLeft / GestureRight). " +
                "The layer guard is recommended; per-transition guards are for special setups.",
                PawlygonEditorUI.SubLabelStyle);
            EditorGUILayout.Space(4f);

            DrawSelectionButtons(
                () => FXGestureCheckerCore.SelectRecommendedGestures(layers),
                () => FXGestureCheckerCore.DeselectAll(layers));

            DrawGestureLayers(layers, view.ExpandedLayers);

            bool anySelected = layers.Any(l => (l.SelectedForLayerDisable && !l.AlreadyHasLayerGuard) ||
                                                l.GestureTransitions.Any(t => t.SelectedForFix && !t.HasDisabledGuard));
            return DrawApplySelectedButton("Apply Selected Expression Guards", anySelected, blockedReason);
        }

        private static bool DrawBlinkTab(List<FXGestureCheckerCore.BlinkLayerAnalysis> blinkLayers, ViewState view, string blockedReason)
        {
            if (blinkLayers.Count == 0)
            {
                EditorGUILayout.HelpBox("No layer in this FX controller looks like a blink animation.", MessageType.Info);
                return false;
            }

            DrawSelectionButtons(
                () => FXGestureCheckerCore.SelectRecommendedBlink(blinkLayers),
                () => FXGestureCheckerCore.DeselectAllBlink(blinkLayers));

            DrawBlinkSection(blinkLayers, view.ExpandedBlinkLayers, ref view.ShowAllBlinkLayers, drawTitle: false);

            bool anySelected = blinkLayers.Any(b => b.SelectedForGuard && !b.AlreadyHasBlinkGuard);
            return DrawApplySelectedButton("Apply Selected Blink Guards", anySelected, blockedReason);
        }

        private static void DrawSelectionButtons(System.Action selectRecommended, System.Action clear)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Select Recommended", "Tick exactly what Apply Recommended would change."),
                        EditorStyles.miniButtonLeft, GUILayout.Width(130f)))
                {
                    selectRecommended();
                    GUI.changed = true;
                }

                if (GUILayout.Button("Clear", EditorStyles.miniButtonRight, GUILayout.Width(60f)))
                {
                    clear();
                    GUI.changed = true;
                }
            }

            EditorGUILayout.Space(4f);
        }

        private static bool DrawApplySelectedButton(string label, bool anySelected, string blockedReason)
        {
            EditorGUILayout.Space(4f);
            bool clicked;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!anySelected || blockedReason != null))
                {
                    clicked = PawlygonEditorUI.DrawSecondaryButton(label, 26f, GUILayout.Width(240f));
                }
            }

            if (blockedReason != null)
            {
                EditorGUILayout.HelpBox(blockedReason, MessageType.Warning);
            }
            else
            {
                // Always one label: ticking a toggle changes anySelected mid-event, and the control count
                // must stay the same as in the layout pass.
                EditorGUILayout.LabelField(anySelected ? " " : "Tick the guards to add, then apply.", PawlygonEditorUI.SubLabelStyle);
            }

            return clicked;
        }

        // =====================================================================
        // Gesture layers
        // =====================================================================

        /// <summary>
        /// Draws a foldout per gesture layer (header: name, "protected/total" transitions and a status
        /// badge), with the layer guard toggle and the per-transition rows. Expansion state is owned by the
        /// caller via <paramref name="expandedLayers"/>.
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
                int total = layer.GestureTransitions.Count;
                int protectedCount = layer.AlreadyHasLayerGuard ? total : layer.GestureTransitions.Count(t => t.HasDisabledGuard);
                bool blocksReset = layer.NeutralTransitions.Any(t => FXGestureCheckerCore.BlocksReset(layer, t));

                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect foldoutRect = GUILayoutUtility.GetRect(GUIContent.none, foldoutHeaderStyle, GUILayout.ExpandWidth(true), GUILayout.MinWidth(80f));
                    bool newExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, new GUIContent(layer.LayerName, $"FX layer {layer.LayerIndex}"), true, foldoutHeaderStyle);

                    string countText = total > 0 ? $"{protectedCount}/{total} protected" : "returns to neutral only";
                    GUILayout.Label(new GUIContent(countText, "Gesture transitions that can't start an expression while face tracking is active."),
                        mutedMiniStyle, GUILayout.ExpandWidth(false));

                    if (blocksReset)
                    {
                        PawlygonEditorUI.DrawBadge("Blocks reset", PawlygonEditorUI.BadgeKind.Warning, BlocksResetTooltip);
                    }
                    DrawLayerStatusBadge(layer, total, protectedCount);

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
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(new GUIContent("Turn this layer off while face tracking is active", LayerGuardTooltip), true);
                    }
                }
                else if (layer.LayerGuardNeedsRepair)
                {
                    layer.SelectedForLayerDisable = EditorGUILayout.ToggleLeft(
                        new GUIContent("Repair this layer's guard (recommended)", OutdatedLayerGuardTooltip),
                        layer.SelectedForLayerDisable);
                }
                else
                {
                    layer.SelectedForLayerDisable = EditorGUILayout.ToggleLeft(
                        new GUIContent("Turn this layer off while face tracking is active (recommended)", LayerGuardTooltip),
                        layer.SelectedForLayerDisable);
                }

                if (total > 0)
                {
                    EditorGUILayout.Space(4f);
                    PawlygonEditorUI.DrawSeparator();
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField(new GUIContent("Per-transition guards (advanced)", PerTransitionTooltip), EditorStyles.miniBoldLabel);

                    foreach (FXGestureCheckerCore.TransitionAnalysis transition in layer.GestureTransitions)
                    {
                        DrawTransitionRow(transition);
                    }
                }

                if (layer.NeutralTransitions.Count > 0)
                {
                    DrawNeutralTransitions(layer);
                }

                EditorGUI.indentLevel--;
            }
        }

        private static void DrawLayerStatusBadge(FXGestureCheckerCore.LayerAnalysis layer, int total, int protectedCount)
        {
            if (layer.AlreadyHasLayerGuard)
            {
                PawlygonEditorUI.DrawBadge("Protected", PawlygonEditorUI.BadgeKind.Ok, LayerGuardTooltip);
            }
            else if (layer.LayerGuardNeedsRepair)
            {
                PawlygonEditorUI.DrawBadge("Needs repair", PawlygonEditorUI.BadgeKind.Warning, OutdatedLayerGuardTooltip);
            }
            else if (total > 0 && protectedCount == total)
            {
                PawlygonEditorUI.DrawBadge("Partly protected", PawlygonEditorUI.BadgeKind.Info, PartlyProtectedTooltip);
            }
            else
            {
                PawlygonEditorUI.DrawBadge("Not protected", PawlygonEditorUI.BadgeKind.Warning,
                    "Hand gestures in this layer can still change the face while face tracking is active.");
            }
        }

        /// <summary>
        /// Row label for a transition: path-qualified source and destination (e.g.
        /// "Left Hand/Fist -> Left Hand/Idle") and its gesture conditions.
        /// </summary>
        private static string GetTransitionLabel(FXGestureCheckerCore.TransitionAnalysis transition)
        {
            string conditions = transition.ConditionLabel ??
                                $"{transition.GestureParameter}={FXGestureCheckerCore.GetGestureName(transition.GestureValue)}";
            return $"{transition.SourceName} → {transition.DestinationName} ({conditions})";
        }

        private static void DrawTransitionRow(FXGestureCheckerCore.TransitionAnalysis transition)
        {
            string label = GetTransitionLabel(transition);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (transition.HasDisabledGuard)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(new GUIContent(label, label), true);
                    }
                    PawlygonEditorUI.DrawBadge("Guarded", PawlygonEditorUI.BadgeKind.Ok,
                        "This transition can't fire while face tracking is active.");
                }
                else
                {
                    transition.SelectedForFix = EditorGUILayout.ToggleLeft(new GUIContent(label, label), transition.SelectedForFix);
                }
            }
        }

        /// <summary>
        /// Lists the layer's return-to-neutral transitions as read-only rows. They are never
        /// guarded per transition (see <see cref="FXGestureCheckerCore.ReturnToNeutralRule"/>).
        /// One that carries a FacialExpressionsDisabled condition gets a "Blocks reset" warning badge,
        /// unless the layer guard is in place (it resets the face itself).
        /// </summary>
        private static void DrawNeutralTransitions(FXGestureCheckerCore.LayerAnalysis layer)
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                new GUIContent("Back to neutral (never guarded, so the face can always reset)", FXGestureCheckerCore.ReturnToNeutralRule),
                EditorStyles.miniBoldLabel);

            foreach (FXGestureCheckerCore.TransitionAnalysis transition in layer.NeutralTransitions)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string label = GetTransitionLabel(transition);
                    EditorGUILayout.LabelField(new GUIContent(label, FXGestureCheckerCore.ReturnToNeutralRule), EditorStyles.miniLabel);

                    if (FXGestureCheckerCore.BlocksReset(layer, transition))
                    {
                        PawlygonEditorUI.DrawBadge("Blocks reset", PawlygonEditorUI.BadgeKind.Warning, BlocksResetTooltip);
                    }
                }
            }
        }

        // =====================================================================
        // Blink layers
        // =====================================================================

        /// <summary>
        /// Draws the detected blink layers: the highest-confidence tier (with the rest behind a "show more"
        /// toggle owned by the caller via <paramref name="showAll"/>), a one-line confidence legend, and a
        /// guard toggle plus detection reasons per layer. <paramref name="blinkLayers"/> should be sorted by
        /// confidence (as <see cref="FXGestureCheckerCore.Analyze(UnityEditor.Animations.AnimatorController)"/> returns them).
        /// </summary>
        internal static void DrawBlinkSection(
            List<FXGestureCheckerCore.BlinkLayerAnalysis> blinkLayers,
            HashSet<int> expandedBlinkLayers,
            ref bool showAll,
            bool drawTitle = true)
        {
            if (blinkLayers == null || blinkLayers.Count == 0) return;
            EnsureStyles();

            if (drawTitle)
            {
                EditorGUILayout.LabelField("Blink Layer Detection", EditorStyles.boldLabel);
                EditorGUILayout.Space(2f);
            }

            EditorGUILayout.LabelField(ConfidenceLegend, PawlygonEditorUI.SubLabelStyle);
            EditorGUILayout.Space(4f);

            // By default show only the highest-confidence tier; lower-confidence layers are opt-in.
            FXGestureCheckerCore.BlinkConfidence topConfidence = blinkLayers.Max(l => l.Confidence);
            List<FXGestureCheckerCore.BlinkLayerAnalysis> visible = showAll
                ? blinkLayers
                : blinkLayers.Where(l => l.Confidence == topConfidence || l.AlreadyHasBlinkGuard || l.BlinkGuardNeedsRepair).ToList();
            int hiddenCount = blinkLayers.Count - visible.Count;

            foreach (FXGestureCheckerCore.BlinkLayerAnalysis blinkLayer in visible)
            {
                DrawBlinkLayerAnalysis(blinkLayer, expandedBlinkLayers);
                EditorGUILayout.Space(4f);
            }

            // Layers the default view hides (also counted while they are shown, so the toggle stays put).
            int lowerCount = showAll
                ? blinkLayers.Count(l => l.Confidence != topConfidence && !l.AlreadyHasBlinkGuard && !l.BlinkGuardNeedsRepair)
                : hiddenCount;
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
                    Rect foldoutRect = GUILayoutUtility.GetRect(GUIContent.none, foldoutHeaderStyle, GUILayout.ExpandWidth(true), GUILayout.MinWidth(80f));
                    bool newExpanded = EditorGUI.Foldout(foldoutRect, isExpanded, new GUIContent(blinkLayer.LayerName, $"FX layer {blinkLayer.LayerIndex}"), true, foldoutHeaderStyle);

                    DrawConfidenceBadge(blinkLayer.Confidence);
                    DrawBlinkStatusBadge(blinkLayer);

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
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ToggleLeft(new GUIContent("Stop blinking while eye tracking is active", BlinkGuardTooltip), true);
                    }
                }
                else if (blinkLayer.BlinkGuardNeedsRepair)
                {
                    blinkLayer.SelectedForGuard = EditorGUILayout.ToggleLeft(
                        new GUIContent("Repair this layer's blink guard (recommended)", OutdatedBlinkGuardTooltip),
                        blinkLayer.SelectedForGuard);
                }
                else
                {
                    string label = blinkLayer.Confidence == FXGestureCheckerCore.BlinkConfidence.High
                        ? "Stop blinking while eye tracking is active (recommended)"
                        : "Stop blinking while eye tracking is active";
                    blinkLayer.SelectedForGuard = EditorGUILayout.ToggleLeft(new GUIContent(label, BlinkGuardTooltip), blinkLayer.SelectedForGuard);
                }

                EditorGUILayout.Space(4f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(2f);

                EditorGUILayout.LabelField("Why it looks like a blink layer:", EditorStyles.miniBoldLabel);
                foreach (string reason in blinkLayer.DetectionReasons)
                {
                    EditorGUILayout.LabelField($"• {reason}", PawlygonEditorUI.RichMiniLabelStyle);
                }

                EditorGUI.indentLevel--;
            }
        }

        private static void DrawConfidenceBadge(FXGestureCheckerCore.BlinkConfidence confidence)
        {
            switch (confidence)
            {
                case FXGestureCheckerCore.BlinkConfidence.High:
                    PawlygonEditorUI.DrawBadge("High", PawlygonEditorUI.BadgeKind.Info, "Clearly a blink layer: guarded by Apply Recommended.");
                    break;
                case FXGestureCheckerCore.BlinkConfidence.Medium:
                    PawlygonEditorUI.DrawBadge("Medium", PawlygonEditorUI.BadgeKind.Neutral, "Possibly a blink layer: check the reasons before guarding it.");
                    break;
                default:
                    PawlygonEditorUI.DrawBadge("Low", PawlygonEditorUI.BadgeKind.Neutral, "Unlikely to be a blink layer: only guard it if you know it blinks.");
                    break;
            }
        }

        private static void DrawBlinkStatusBadge(FXGestureCheckerCore.BlinkLayerAnalysis blinkLayer)
        {
            if (blinkLayer.AlreadyHasBlinkGuard)
            {
                PawlygonEditorUI.DrawBadge("Protected", PawlygonEditorUI.BadgeKind.Ok, BlinkGuardTooltip);
            }
            else if (blinkLayer.BlinkGuardNeedsRepair)
            {
                PawlygonEditorUI.DrawBadge("Needs repair", PawlygonEditorUI.BadgeKind.Warning, OutdatedBlinkGuardTooltip);
            }
            else if (blinkLayer.Confidence == FXGestureCheckerCore.BlinkConfidence.High)
            {
                PawlygonEditorUI.DrawBadge("Not protected", PawlygonEditorUI.BadgeKind.Warning,
                    "This blink animation can close the eyes over eye tracking.");
            }
            else
            {
                PawlygonEditorUI.DrawBadge("Optional", PawlygonEditorUI.BadgeKind.Neutral,
                    "Not part of the recommendation. Guard it only if this layer really blinks.");
            }
        }
    }
}
