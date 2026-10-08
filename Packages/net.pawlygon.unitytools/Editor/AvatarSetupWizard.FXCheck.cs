using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step 5: per avatar, the FX Gesture Checker's recommended guards (gesture expressions and blinking turn
    /// off while face tracking is active), written into a copy of the prefab's FX controller that the prefab
    /// then uses. Shares the summary card and the advanced section with the FX Gesture Checker window.
    /// </summary>
    public partial class AvatarSetupWizard
    {
        private const string FxCopyFolderName = "VRChat";

        private enum FxOutcome
        {
            /// <summary>Not analyzed yet.</summary>
            NotChecked,

            /// <summary>Recommended guards are missing.</summary>
            Pending,

            /// <summary>Guards are missing but a parameter conflict blocks them.</summary>
            NeedsAttention,

            /// <summary>Every recommended guard is in place, written by this wizard.</summary>
            Fixed,

            /// <summary>Every recommended guard was already in place.</summary>
            NoChangesNeeded,

            /// <summary>No FX controller to check (none assigned, no SDK, prefab not loadable).</summary>
            NothingToCheck,

            Skipped
        }

        private enum FxApplyScope { Recommended, Expressions, Blink }

        /// <summary>Re-analyze every analyzed avatar on the next layout pass (after undo or focus).</summary>
        private bool fxRefreshRequested;

        private void RequestFxRefresh()
        {
            fxRefreshRequested = true;
        }

        private void ResetFXCheckState()
        {
            fxRefreshRequested = false;
        }

        private static void ResetEntryFXState(AvatarEntry entry)
        {
            entry.fxSkipped = false;
            entry.fxGuardsWritten = 0;
            entry.fxAnalyzed = false;
            entry.fxAnalysisResult = null;
            entry.fxPlan = null;
            entry.fxOverrideController = null;
            entry.fxView = null;
            entry.fxProblem = null;
        }

        private void OnEnterFXCheck()
        {
            EnsureFxAnalyzed(showProgress: true);
            SelectFirstEntry(entry => !IsFxComplete(entry));
        }

        // =====================================================================
        // Outcome
        // =====================================================================

        private static FxOutcome GetFxOutcome(AvatarEntry entry)
        {
            if (entry.fxSkipped) return FxOutcome.Skipped;
            if (!entry.fxAnalyzed) return FxOutcome.NotChecked;
            if (entry.fxAnalysisResult == null || entry.fxPlan == null) return FxOutcome.NothingToCheck;
            if (entry.fxPlan.HasChanges) return FxOutcome.Pending;
            if (GetFxBlockingConflict(entry.fxPlan) != null) return FxOutcome.NeedsAttention;

            return entry.fxGuardsWritten > 0 || IsWizardFxCopy(entry, entry.fxAnalysisResult.FXController)
                ? FxOutcome.Fixed
                : FxOutcome.NoChangesNeeded;
        }

        private static bool IsFxComplete(AvatarEntry entry)
        {
            FxOutcome outcome = GetFxOutcome(entry);
            return outcome != FxOutcome.NotChecked && outcome != FxOutcome.Pending && outcome != FxOutcome.NeedsAttention;
        }

        /// <summary>
        /// A parameter conflict that keeps a needed guard from being written, or null. A conflict on a part
        /// that needs nothing (e.g. no blink layer) doesn't count.
        /// </summary>
        private static string GetFxBlockingConflict(FXGestureCheckerCore.RecommendedPlan plan)
        {
            if (plan == null) return null;

            if (plan.GestureGuardError != null && plan.GestureLayersProtected < plan.GestureLayerCount)
            {
                return plan.GestureGuardError;
            }

            bool blinkNeedsGuard = plan.Blink == FXGestureCheckerCore.BlinkProtection.NotProtected ||
                                   plan.Blink == FXGestureCheckerCore.BlinkProtection.NeedsRepair;
            return plan.BlinkGuardError != null && blinkNeedsGuard ? plan.BlinkGuardError : null;
        }

        private (string Text, PawlygonEditorUI.BadgeKind Kind, string Tooltip) GetFxBadge(AvatarEntry entry)
        {
            switch (GetFxOutcome(entry))
            {
                case FxOutcome.Pending:
                    return ("Pending", PawlygonEditorUI.BadgeKind.Info, $"Apply Recommended makes {Plural(entry.fxPlan.TotalChanges, "change")}.");
                case FxOutcome.NeedsAttention:
                    return ("Needs attention", PawlygonEditorUI.BadgeKind.Error, GetFxBlockingConflict(entry.fxPlan));
                case FxOutcome.Fixed:
                    return ("Fixed", PawlygonEditorUI.BadgeKind.Ok, "The prefab uses a guarded copy of its FX controller.");
                case FxOutcome.NoChangesNeeded:
                    return ("No changes needed", PawlygonEditorUI.BadgeKind.Ok, "Every recommended guard was already in place.");
                case FxOutcome.NothingToCheck:
                    return ("Nothing to check", PawlygonEditorUI.BadgeKind.Neutral, FirstLine(entry.fxProblem));
                case FxOutcome.Skipped:
                    return ("Skipped", PawlygonEditorUI.BadgeKind.Neutral, entry.fxGuardsWritten > 0
                        ? $"Skipped after adding {Plural(entry.fxGuardsWritten, "guard")}; the remaining recommendations were left out."
                        : "The FX controller was left unchanged.");
                default:
                    return ("Not checked", PawlygonEditorUI.BadgeKind.Neutral, "Open the FX Check step to check it.");
            }
        }

        /// <summary>True when <paramref name="controller"/> is a copy this wizard made (so it is edited in place).</summary>
        private static bool IsWizardFxCopy(AvatarEntry entry, UnityEngine.Object controller)
        {
            string path = controller != null ? AssetDatabase.GetAssetPath(controller) : null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return string.Equals(path, entry.copiedFxControllerPath, StringComparison.OrdinalIgnoreCase) ||
                   IsAssetPathInsideFolder(path, GetFxCopyFolder(entry));
        }

        private static string GetFxCopyFolder(AvatarEntry entry)
        {
            return PawlygonEditorUtils.CombineAssetPath(entry.avatarRootPath, FxCopyFolderName);
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        /// <summary>
        /// Analyzes every avatar that hasn't been analyzed yet (all of them after an undo or focus), except
        /// skipped ones. Cheap when there is nothing to do, so it runs on every layout pass.
        /// </summary>
        private void EnsureFxAnalyzed(bool showProgress)
        {
            bool refreshAll = fxRefreshRequested;
            fxRefreshRequested = false;

            List<AvatarEntry> todo = avatarEntries
                .Where(entry => !entry.fxSkipped && (!entry.fxAnalyzed || refreshAll))
                .ToList();
            if (todo.Count == 0)
            {
                return;
            }

            try
            {
                for (int i = 0; i < todo.Count; i++)
                {
                    if (showProgress)
                    {
                        EditorUtility.DisplayProgressBar(ProgressTitle,
                            $"Checking the FX controller of '{GetEntryDisplayName(todo[i])}' ({i + 1}/{todo.Count})…", (float)i / todo.Count);
                    }

                    // A first analysis selects the recommendation and expands what needs attention; a refresh
                    // keeps the user's picks and expansions.
                    bool firstTime = !todo[i].fxAnalyzed;
                    AnalyzeFxEntry(todo[i], resetView: firstTime, keepSelection: !firstTime);
                }
            }
            finally
            {
                if (showProgress)
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        /// <summary>
        /// Analyzes the FX controller of an avatar's copied prefab (the base controller when the FX layer uses
        /// an override) and builds the recommended plan. Leaves <c>fxAnalysisResult</c> null with
        /// <c>fxProblem</c> set when there is nothing to analyze.
        /// </summary>
        private void AnalyzeFxEntry(AvatarEntry entry, bool resetView, bool keepSelection)
        {
            FXGestureCheckerCore.AnalysisResult previous = entry.fxAnalysisResult;

            entry.fxAnalyzed = true;
            entry.fxAnalysisResult = null;
            entry.fxPlan = null;
            entry.fxOverrideController = null;
            entry.fxProblem = null;
            entry.fxProblemType = MessageType.Info;

            if (string.IsNullOrEmpty(entry.copiedPrefabPath) || AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedPrefabPath) == null)
            {
                entry.fxProblem = $"The prefab '{entry.copiedPrefabPath}' can't be loaded, so its FX controller can't be checked.";
                entry.fxProblemType = MessageType.Error;
                return;
            }

            FXGestureCheckerCore.AnalysisResult result;
            GameObject prefabRoot = null;
            try
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                result = FXGestureCheckerCore.Analyze(prefabRoot);
            }
            finally
            {
                if (prefabRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            // The descriptor lived in the unloaded prefab contents; applying reopens the prefab instead.
            result.Descriptor = null;

            if (!result.Success && result.OverrideController != null &&
                result.OverrideController.runtimeAnimatorController is AnimatorController baseController)
            {
                // Guards go into a copy of the base controller and a copy of the override that uses it.
                entry.fxOverrideController = result.OverrideController;
                FXGestureCheckerCore.AnalysisResult baseResult = FXGestureCheckerCore.Analyze(baseController);
                baseResult.DescriptorType = result.DescriptorType;
                result = baseResult;
            }

            if (!result.Success)
            {
                entry.fxProblem = result.StatusMessage;
                entry.fxProblemType = result.StatusMessageType == MessageType.None ? MessageType.Info : result.StatusMessageType;
                return;
            }

            if (string.IsNullOrEmpty(entry.originalFxControllerPath))
            {
                UnityEngine.Object assigned = entry.fxOverrideController != null ? entry.fxOverrideController : result.FXController;
                entry.originalFxControllerPath = AssetDatabase.GetAssetPath(assigned);
            }

            entry.fxAnalysisResult = result;
            FXGestureCheckerCore.SelectRecommended(result);
            if (keepSelection && previous != null)
            {
                FXGestureCheckerCore.CopySelection(previous, result);
            }

            entry.fxPlan = FXGestureCheckerCore.GetRecommendedPlan(result);

            if (resetView || entry.fxView == null)
            {
                entry.fxView = new FXGestureCheckerUI.ViewState();
                entry.fxView.ExpandLayersNeedingAttention(result);
            }
        }

        // =====================================================================
        // Drawing
        // =====================================================================

        private void DrawFXCheckStep()
        {
            if (PawlygonEditorUtils.FindVRCAvatarDescriptorType() == null)
            {
                EditorGUILayout.HelpBox(
                    "The VRChat Avatars SDK isn't installed, so the FX controllers can't be checked. Continue to Finish; " +
                    "you can run !Pawlygon/Tools/FX Gesture Checker once the SDK is installed.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(
                "Gesture expressions and blinking get turned off while face tracking is active. The changes go into a copy of " +
                "each prefab's FX controller; the original stays unchanged.",
                PawlygonEditorUI.SubLabelStyle);
            EditorGUILayout.Space(SectionSpacing);

            int doneCount = avatarEntries.Count(IsFxComplete);
            if (DrawAvatarList("Avatars", $"{doneCount}/{avatarEntries.Count} done", GetFxBadge))
            {
                GUIUtility.ExitGUI();
            }

            AvatarEntry entry = GetSelectedEntry();
            if (entry == null)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(GetEntryDisplayName(entry), PawlygonEditorUI.SectionTitleStyle);
                GUILayout.FlexibleSpace();
                var badge = GetFxBadge(entry);
                PawlygonEditorUI.DrawBadge(badge.Text, badge.Kind, badge.Tooltip);
            }

            EditorGUILayout.Space(4f);

            if (entry.fxSkipped)
            {
                EditorGUILayout.HelpBox(entry.fxGuardsWritten > 0
                    ? "Skipped: the guards added so far stay, the remaining recommendations are left out."
                    : "Skipped: this avatar's FX controller is left unchanged.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Check Again", "Check this avatar's FX controller after all."), GUILayout.Width(110f)))
                    {
                        entry.fxSkipped = false;
                        entry.fxAnalyzed = false;
                        status.Clear();
                        GUIUtility.ExitGUI();
                    }
                }

                return;
            }

            if (!entry.fxAnalyzed)
            {
                EditorGUILayout.LabelField("Checking…", PawlygonEditorUI.SubLabelStyle);
                return;
            }

            if (entry.fxAnalysisResult == null)
            {
                EditorGUILayout.HelpBox(entry.fxProblem ?? "The FX controller couldn't be checked.", entry.fxProblemType);
                return;
            }

            FXGestureCheckerUI.DrawSummaryCard(entry.fxPlan);
            EditorGUILayout.Space(SectionSpacing);

            DrawFxControllerSection(entry);
            EditorGUILayout.Space(SectionSpacing);

            FXGestureCheckerUI.AdvancedAction action = FXGestureCheckerUI.DrawAdvancedSection(
                entry.fxAnalysisResult, entry.fxPlan, entry.fxView,
                entry.fxPlan.GestureGuardError, entry.fxPlan.BlinkGuardError);

            if (action == FXGestureCheckerUI.AdvancedAction.ApplyExpressions)
            {
                ApplyFxGuards(entry, FxApplyScope.Expressions);
                GUIUtility.ExitGUI();
            }
            else if (action == FXGestureCheckerUI.AdvancedAction.ApplyBlink)
            {
                ApplyFxGuards(entry, FxApplyScope.Blink);
                GUIUtility.ExitGUI();
            }
        }

        private void DrawFxControllerSection(AvatarEntry entry)
        {
            AnimatorController controller = entry.fxAnalysisResult.FXController;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    if (entry.fxOverrideController != null)
                    {
                        EditorGUILayout.ObjectField("FX Override", entry.fxOverrideController, typeof(AnimatorOverrideController), false);
                        EditorGUILayout.ObjectField(new GUIContent("Base Controller", "The guards go into this controller's layers."),
                            controller, typeof(AnimatorController), false);
                    }
                    else
                    {
                        EditorGUILayout.ObjectField("FX Controller", controller, typeof(AnimatorController), false);
                    }
                }

                string where = IsWizardFxCopy(entry, controller)
                    ? $"Changes go to '{controller.name}', the copy this wizard made, in place."
                    : $"Changes go to a copy saved in '{GetFxCopyFolder(entry)}'; the prefab gets the copy and the original stays unchanged.";
                EditorGUILayout.LabelField(where, PawlygonEditorUI.SubLabelStyle);

                // The checker's own summary, including notes on adapted parameter types.
                if (!string.IsNullOrEmpty(entry.fxAnalysisResult.StatusMessage))
                {
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField(entry.fxAnalysisResult.StatusMessage, PawlygonEditorUI.RichMiniLabelStyle);
                }
            }
        }

        private void DrawFXCheckActions()
        {
            DrawBackButton(WizardStep.Prefabs);

            AvatarEntry entry = GetSelectedEntry();
            FxOutcome outcome = entry != null ? GetFxOutcome(entry) : FxOutcome.NothingToCheck;
            bool needsWork = outcome == FxOutcome.Pending || outcome == FxOutcome.NeedsAttention;

            if (needsWork &&
                PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Skip This Avatar", "Leave this avatar's FX controller unchanged."), ActionButtonHeight))
            {
                entry.fxSkipped = true;
                status.Info(entry.fxGuardsWritten > 0
                    ? $"Skipped the remaining FX recommendations of '{GetEntryDisplayName(entry)}'."
                    : $"Skipped the FX Check of '{GetEntryDisplayName(entry)}'. Its FX controller is unchanged.");
                SelectNextFxEntry();
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            if (outcome == FxOutcome.Pending)
            {
                var content = new GUIContent(entry.fxPlan.ButtonLabel, "Shows exactly what changes and asks before applying.");
                if (PawlygonEditorUI.DrawPrimaryButton(content, ActionButtonHeight, GUILayout.MinWidth(240f)))
                {
                    ApplyFxGuards(entry, FxApplyScope.Recommended);
                    // Copies the controller, saves the prefab and shows dialogs.
                    GUIUtility.ExitGUI();
                }

                return;
            }

            if (outcome == FxOutcome.NeedsAttention)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Apply Recommended", GetFxBlockingConflict(entry.fxPlan) + " Or skip this avatar."),
                        ActionButtonHeight, GUILayout.MinWidth(240f));
                }

                return;
            }

            int nextIndex = FindNextIncompleteFxIndex();
            if (nextIndex >= 0)
            {
                if (PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Next Avatar", $"Check '{GetEntryDisplayName(avatarEntries[nextIndex])}'."),
                        ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
                {
                    selectedEntryIndex = nextIndex;
                    scrollPosition = Vector2.zero;
                    status.Clear();
                    GUIUtility.ExitGUI();
                }
            }
            else if (PawlygonEditorUI.DrawPrimaryButton("Continue to Finish", ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
            {
                GoToStep(WizardStep.Complete);
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>The next avatar (after the selected one, wrapping) that still needs the FX Check, or -1.</summary>
        private int FindNextIncompleteFxIndex()
        {
            for (int offset = 1; offset < avatarEntries.Count; offset++)
            {
                int index = (selectedEntryIndex + offset) % avatarEntries.Count;
                if (!IsFxComplete(avatarEntries[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        private void SelectNextFxEntry()
        {
            int nextIndex = FindNextIncompleteFxIndex();
            if (nextIndex >= 0)
            {
                selectedEntryIndex = nextIndex;
                scrollPosition = Vector2.zero;
            }
        }

        // =====================================================================
        // Applying
        // =====================================================================

        /// <summary>
        /// Writes the recommended (or, from the advanced section, the selected) guards for an avatar. Checks
        /// the plan before touching anything: a parameter conflict blocks only its own part, and nothing is
        /// copied when there is nothing to write. Unless the prefab already uses a copy made by this wizard,
        /// the controller (and its override) is copied into the avatar's VRChat folder, the guards are written
        /// into the copy and the copy is given to the prefab. The avatar only counts as fixed when guards were
        /// actually written and the prefab uses them; on failure the new copies are removed again.
        /// </summary>
        private void ApplyFxGuards(AvatarEntry entry, FxApplyScope scope)
        {
            FXGestureCheckerCore.AnalysisResult analysis = entry.fxAnalysisResult;
            FXGestureCheckerCore.RecommendedPlan plan = entry.fxPlan;
            if (analysis == null || plan == null || analysis.FXController == null)
            {
                status.Error("This avatar has no FX controller to change.");
                return;
            }

            // A part blocked by a parameter conflict is skipped (the plan already leaves it out).
            bool gestures = scope != FxApplyScope.Blink && plan.GestureGuardError == null;
            bool blink = scope != FxApplyScope.Expressions && plan.BlinkGuardError == null;

            List<string> changes = scope == FxApplyScope.Recommended
                ? plan.Changes
                : FXGestureCheckerCore.DescribeSelectedChanges(analysis, gestures, blink);
            if (changes.Count == 0)
            {
                string conflict = scope == FxApplyScope.Blink ? plan.BlinkGuardError
                    : scope == FxApplyScope.Expressions ? plan.GestureGuardError
                    : GetFxBlockingConflict(plan);
                if (conflict != null) status.Error(conflict);
                else status.Warning("Nothing to apply: tick at least one guard first.");
                return;
            }

            AnimatorController sourceController = analysis.FXController;
            bool inPlace = IsWizardFxCopy(entry, sourceController);
            string folder = GetFxCopyFolder(entry);
            List<AvatarEntry> sharing = inPlace ? new List<AvatarEntry>() : GetEntriesSharingFxController(entry);

            string title = scope == FxApplyScope.Recommended ? "Apply Recommended Guards" : "Apply Selected Guards";
            string targetDescription = DescribeFxTarget(entry, sourceController, inPlace, folder, sharing);
            if (!EditorUtility.DisplayDialog(title, FXGestureCheckerCore.BuildConfirmationMessage(changes, targetDescription,
                    "The original FX controller isn't changed; the avatar's prefab is switched to the guarded copy."), "Apply", "Cancel"))
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(title);

            AnimatorController target = sourceController;
            FXGestureCheckerCore.AnalysisResult targetAnalysis = analysis;
            var createdAssets = new List<string>();

            if (!inPlace)
            {
                target = FXGestureCheckerCore.CopyFXController(sourceController, folder, out string copyError);
                if (target == null)
                {
                    status.Error(copyError ?? "Couldn't copy the FX controller, so nothing was changed.");
                    return;
                }

                createdAssets.Add(AssetDatabase.GetAssetPath(target));

                // Analyze the copy itself so the guards are written into its own states and transitions,
                // then carry the selection over (layer indices and transition keys match the original).
                targetAnalysis = FXGestureCheckerCore.Analyze(target);
                if (!targetAnalysis.Success)
                {
                    RemoveCreatedAssets(createdAssets);
                    status.Error("Made a copy of the FX controller but couldn't read it back, so nothing was changed.");
                    return;
                }

                FXGestureCheckerCore.CopySelection(analysis, targetAnalysis);
            }

            if (scope == FxApplyScope.Recommended)
            {
                FXGestureCheckerCore.SelectRecommended(targetAnalysis);
            }

            FXGestureCheckerCore.ApplyCounts counts = FXGestureCheckerCore.ApplySelected(target, targetAnalysis, gestures, blink);
            Undo.CollapseUndoOperations(undoGroup);

            if (counts.Total == 0)
            {
                RemoveCreatedAssets(createdAssets);
                AnalyzeFxEntry(entry, resetView: false, keepSelection: true);
                status.Error("No guards were written to the FX controller, so nothing was changed. " +
                             (GetFxBlockingConflict(plan) ?? "See the Console for details."));
                return;
            }

            if (!inPlace)
            {
                RuntimeAnimatorController assigned = target;
                if (entry.fxOverrideController != null)
                {
                    AnimatorOverrideController overrideCopy = FXGestureCheckerCore.CopyOverrideController(entry.fxOverrideController, target, folder, out string overrideError);
                    if (overrideCopy == null)
                    {
                        RemoveCreatedAssets(createdAssets);
                        AnalyzeFxEntry(entry, resetView: false, keepSelection: true);
                        status.Error((overrideError ?? "Couldn't copy the override controller.") + " Nothing was changed.");
                        return;
                    }

                    createdAssets.Add(AssetDatabase.GetAssetPath(overrideCopy));
                    assigned = overrideCopy;
                }

                if (!AssignFxControllerToPrefab(entry, assigned, out string assignError))
                {
                    RemoveCreatedAssets(createdAssets);
                    AnalyzeFxEntry(entry, resetView: false, keepSelection: true);
                    status.Error($"{assignError} Nothing was changed.");
                    return;
                }

                entry.copiedFxControllerPath = AssetDatabase.GetAssetPath(target);

                foreach (AvatarEntry other in sharing)
                {
                    if (AssignFxControllerToPrefab(other, assigned, out string otherError))
                    {
                        other.copiedFxControllerPath = entry.copiedFxControllerPath;
                        other.fxGuardsWritten += counts.Total;
                        AnalyzeFxEntry(other, resetView: true, keepSelection: false);
                    }
                    else
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] Couldn't give the guarded FX controller to '{GetEntryDisplayName(other)}': {otherError}");
                    }
                }
            }

            entry.fxGuardsWritten += counts.Total;
            AnalyzeFxEntry(entry, resetView: false, keepSelection: false);

            string message = $"{counts.Describe()} in '{target.name}'.";
            if (sharing.Count > 0)
            {
                message += $" Also given to {string.Join(", ", sharing.Select(other => $"'{GetEntryDisplayName(other)}'"))}, which used the same controller.";
            }

            status.Info(message, "Ping", PawlygonStatus.Ping(target));
        }

        /// <summary>
        /// Other avatars still to check whose prefab uses the same original FX controller, and so should get
        /// the same guarded copy.
        /// </summary>
        private List<AvatarEntry> GetEntriesSharingFxController(AvatarEntry entry)
        {
            if (string.IsNullOrEmpty(entry.originalFxControllerPath))
            {
                return new List<AvatarEntry>();
            }

            return avatarEntries
                .Where(other => other != entry &&
                                !other.fxSkipped &&
                                other.fxAnalysisResult != null &&
                                !IsWizardFxCopy(other, other.fxAnalysisResult.FXController) &&
                                string.Equals(other.originalFxControllerPath, entry.originalFxControllerPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>Completes "This changes …" in the confirmation dialog.</summary>
        private static string DescribeFxTarget(AvatarEntry entry, AnimatorController controller, bool inPlace, string folder, List<AvatarEntry> sharing)
        {
            if (inPlace)
            {
                return $"'{AssetDatabase.GetAssetPath(controller)}', the copy this wizard made, in place";
            }

            string prefabName = Path.GetFileName(entry.copiedPrefabPath);
            string copyPath = FXGestureCheckerCore.GetCopyPath(controller, folder);
            string users = sharing.Count == 0
                ? $"the prefab '{prefabName}' will use it"
                : $"the prefab '{prefabName}' and {Plural(sharing.Count, "other avatar")} with the same controller will use it";

            return entry.fxOverrideController != null
                ? $"a copy of the base controller at '{copyPath}' and a copy of the override '{entry.fxOverrideController.name}' that uses it " +
                  $"({users}; the originals stay unchanged)"
                : $"a copy at '{copyPath}' ({users}; the original '{AssetDatabase.GetAssetPath(controller)}' stays unchanged)";
        }

        /// <summary>Puts <paramref name="controller"/> into the FX slot of the copied prefab's avatar descriptor and saves the prefab.</summary>
        private static bool AssignFxControllerToPrefab(AvatarEntry entry, RuntimeAnimatorController controller, out string error)
        {
            error = null;
            Type descriptorType = PawlygonEditorUtils.FindVRCAvatarDescriptorType();
            GameObject prefabRoot = null;

            try
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);
                Component descriptor = descriptorType != null ? prefabRoot.GetComponent(descriptorType) : null;
                if (descriptor == null)
                {
                    error = $"The prefab '{entry.copiedPrefabPath}' has no VRC Avatar Descriptor on its root.";
                    return false;
                }

                if (!FXGestureCheckerCore.AssignFXRuntimeControllerToDescriptor(descriptor, descriptorType, controller))
                {
                    error = $"Couldn't set the FX slot of '{entry.copiedPrefabPath}'.";
                    return false;
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
                return true;
            }
            finally
            {
                if (prefabRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }
        }

        /// <summary>Deletes the copies made by a failed apply; nothing references them yet.</summary>
        private static void RemoveCreatedAssets(List<string> assetPaths)
        {
            foreach (string assetPath in assetPaths)
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
            }
        }
    }
}
