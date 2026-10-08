using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window that checks an avatar's FX AnimatorController for gesture-driven facial expressions
    /// and blink animations, and adds guards so they turn off while face / eye tracking is active.
    /// Shows a summary with one recommended action ("Apply Recommended"), and an advanced section to pick
    /// guards individually. Delegates all analysis and fix logic to <see cref="FXGestureCheckerCore"/> and
    /// the result drawing to <see cref="FXGestureCheckerUI"/> (shared with the Avatar Setup Wizard).
    /// </summary>
    public class FXGestureChecker : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/FX Gesture Checker";
        private const string WindowTitle = "FX Gesture Checker";
        private const float SectionSpacing = 8f;

        private enum ApplyScope { Recommended, Expressions, Blink }

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private GameObject selectedAvatar;
        [SerializeField] private bool workOnCopy = true;

        private FXGestureCheckerCore.AnalysisResult analysis;
        private FXGestureCheckerCore.RecommendedPlan plan;

        /// <summary>Set when the FX layer uses an override controller; <see cref="analysis"/> is then of its base.</summary>
        private AnimatorOverrideController overrideController;

        /// <summary>Why the avatar couldn't be analyzed (no descriptor, no FX controller, ...), or null.</summary>
        private string analysisProblem;
        private MessageType analysisProblemType;

        private bool needsAnalysis = true;
        private bool resetViewOnAnalysis = true;
        private readonly FXGestureCheckerUI.ViewState view = new FXGestureCheckerUI.ViewState();
        private readonly PawlygonStatus status = new PawlygonStatus();

        /// <summary>Folder picked with Browse; null = next to the original controller.</summary>
        private string customCopyFolder;

        /// <summary>
        /// The controller copy this window created and assigned to the analyzed avatar. Later
        /// applies in copy mode (e.g. blink guards after gesture guards) reuse it instead of
        /// copying the copy. Forgotten when the avatar changes.
        /// </summary>
        private AnimatorController sessionCopy;

        private AnimatorController FXController => analysis?.FXController;

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath, priority = 21)] // Tools: Check
        public static void ShowWindow()
        {
            FXGestureChecker window = GetWindow<FXGestureChecker>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(520f, 460f);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            Undo.undoRedoPerformed += OnUndoRedo;

            if (selectedAvatar == null)
            {
                selectedAvatar = PawlygonEditorUtils.GetPreferredAvatar();
            }

            RequestAnalysis(resetView: true);
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        /// <summary>Undo/redo can add or remove guards (or switch the avatar back to the original controller).</summary>
        private void OnUndoRedo()
        {
            RequestAnalysis(resetView: false);
            Repaint();
        }

        /// <summary>The controller may have been edited in the Animator window while this one was in the background.</summary>
        private void OnFocus()
        {
            RequestAnalysis(resetView: false);
        }

        private void OnHierarchyChange()
        {
            // The avatar was deleted or its scene closed: show the empty state instead of stale results.
            if (selectedAvatar == null && (analysis != null || analysisProblem != null))
            {
                RequestAnalysis(resetView: true);
                Repaint();
            }
        }

        private void RequestAnalysis(bool resetView)
        {
            needsAnalysis = true;
            resetViewOnAnalysis |= resetView;
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();

            // Analyze before anything is laid out, so the Layout and Repaint passes draw the same controls.
            if (needsAnalysis && Event.current.type == EventType.Layout)
            {
                Analyze(keepGestureSelection: !resetViewOnAnalysis, keepBlinkSelection: !resetViewOnAnalysis, resetView: resetViewOnAnalysis);
            }

            PawlygonEditorUI.DrawHeader(
                WindowTitle,
                "Turns gesture expressions and blinking off while face tracking is active.",
                PawlygonEditorUI.DocumentationUrl);

            if (PawlygonEditorUI.DrawAvatarBar(this, ref selectedAvatar, "Avatar"))
            {
                // Results, copy and folder belong to the previous avatar: drop them so an apply can never
                // write into another avatar's controller.
                sessionCopy = null;
                customCopyFolder = null;
                status.Clear();
                RequestAnalysis(resetView: true);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(4f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
            DrawBody();
            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);
            DrawActionBar();
            PawlygonEditorUI.DrawFooter();
        }

        private void DrawBody()
        {
            if (PawlygonEditorUtils.FindVRCAvatarDescriptorType() == null)
            {
                EditorGUILayout.HelpBox("The VRChat Avatars SDK isn't installed. Add it with the VRChat Creator Companion to use this check.", MessageType.Error);
                return;
            }

            if (selectedAvatar == null)
            {
                EditorGUILayout.HelpBox("Pick your avatar above (or choose one from Scene ▾) to check its FX controller.", MessageType.Info);
                return;
            }

            if (analysis == null)
            {
                if (analysisProblem != null) EditorGUILayout.HelpBox(analysisProblem, analysisProblemType);
                return;
            }

            FXGestureCheckerUI.DrawSummaryCard(plan);
            EditorGUILayout.Space(SectionSpacing);

            DrawControllerSection();
            EditorGUILayout.Space(SectionSpacing);

            FXGestureCheckerUI.AdvancedAction action = FXGestureCheckerUI.DrawAdvancedSection(
                analysis, plan, view,
                GetApplyBlockedReason(ApplyScope.Expressions),
                GetApplyBlockedReason(ApplyScope.Blink));

            if (action == FXGestureCheckerUI.AdvancedAction.ApplyExpressions)
            {
                Apply(ApplyScope.Expressions);
                GUIUtility.ExitGUI();
            }
            else if (action == FXGestureCheckerUI.AdvancedAction.ApplyBlink)
            {
                Apply(ApplyScope.Blink);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(SectionSpacing);
        }

        // =====================================================================
        // Drawing: controller and copy options
        // =====================================================================

        private void DrawControllerSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        if (overrideController != null)
                        {
                            EditorGUILayout.ObjectField("FX Override", overrideController, typeof(AnimatorOverrideController), false);
                        }
                        else
                        {
                            EditorGUILayout.ObjectField("FX Controller", FXController, typeof(AnimatorController), false);
                        }
                    }

                    if (GUILayout.Button(new GUIContent("Re-analyse", "Check the FX controller again (it is also checked automatically)."),
                            EditorStyles.miniButton, GUILayout.Width(80f)))
                    {
                        RequestAnalysis(resetView: false);
                        Repaint();
                        GUIUtility.ExitGUI();
                    }
                }

                if (overrideController != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(new GUIContent("Base Controller", "The guards go into this controller's layers."),
                            FXController, typeof(AnimatorController), false);
                    }
                }

                EditorGUILayout.Space(4f);
                DrawCopyOptions();
            }
        }

        private void DrawCopyOptions()
        {
            string forcedReason = GetForcedCopyReason();
            bool usingSessionCopy = FXController != null && FXController == sessionCopy;

            using (new EditorGUI.DisabledScope(forcedReason != null || usingSessionCopy))
            {
                EditorGUI.BeginChangeCheck();
                bool copy = EditorGUILayout.ToggleLeft(
                    new GUIContent("Work on a copy (recommended)",
                        "Saves the changes to a copy of the FX controller and gives the copy to this avatar. The original file isn't touched."),
                    forcedReason != null || usingSessionCopy || workOnCopy);
                if (EditorGUI.EndChangeCheck())
                {
                    workOnCopy = copy;
                    // Toggling adds/removes the output folder row.
                    GUIUtility.ExitGUI();
                }
            }

            string explanation = usingSessionCopy
                ? $"Changes go to '{FXController.name}', the copy made earlier in this session."
                : forcedReason ?? (workOnCopy
                    ? "Your original FX controller stays unchanged; the avatar gets an edited copy."
                    : "Changes are written into the original FX controller. Other avatars using it change too.");
            EditorGUILayout.LabelField(explanation, PawlygonEditorUI.SubLabelStyle);

            if (WillCreateCopy())
            {
                EditorGUILayout.Space(2f);
                DrawOutputFolderRow();
            }
        }

        private void DrawOutputFolderRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Save Copy In", "Folder for the copy. By default, next to the original controller."));
                string folder = GetCopyFolder();
                EditorGUILayout.LabelField(new GUIContent(folder, folder), EditorStyles.textField, GUILayout.ExpandWidth(true));

                if (customCopyFolder != null && GUILayout.Button(new GUIContent("Reset", "Save next to the original controller."),
                        EditorStyles.miniButton, GUILayout.Width(48f)))
                {
                    customCopyFolder = null;
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button("Browse", EditorStyles.miniButton, GUILayout.Width(60f)))
                {
                    BrowseForCopyFolder(folder);

                    // The modal folder panel invalidates the current layout group.
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void BrowseForCopyFolder(string currentFolder)
        {
            string selected = EditorUtility.OpenFolderPanel("Folder for the FX Controller Copy", currentFolder, "");
            if (string.IsNullOrEmpty(selected)) return;

            string projectPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            selected = selected.Replace('\\', '/').TrimEnd('/');

            // Must be Assets itself or a folder inside it (not a sibling such as
            // ".../AssetsBackup", which a plain prefix check would accept).
            bool isAssetsFolder = string.Equals(selected, projectPath, StringComparison.OrdinalIgnoreCase);
            bool isInsideAssets = selected.StartsWith(projectPath + "/", StringComparison.OrdinalIgnoreCase);

            if (isAssetsFolder || isInsideAssets)
            {
                customCopyFolder = "Assets" + selected.Substring(projectPath.Length);
            }
            else
            {
                EditorUtility.DisplayDialog("Pick a Folder Inside Assets",
                    "The copy has to be saved inside your project's Assets folder.", "OK");
            }
        }

        // =====================================================================
        // Drawing: action bar
        // =====================================================================

        private void DrawActionBar()
        {
            PawlygonEditorUI.BeginActionBar();
            GUILayout.FlexibleSpace();

            bool canApply = analysis != null && plan != null && plan.HasChanges && GetApplyBlockedReason(ApplyScope.Recommended) == null;
            using (new EditorGUI.DisabledScope(!canApply))
            {
                string label = plan != null ? plan.ButtonLabel : "Apply Recommended";
                if (PawlygonEditorUI.DrawPrimaryButton(label, 30f, GUILayout.MinWidth(240f)))
                {
                    Apply(ApplyScope.Recommended);
                    GUIUtility.ExitGUI();
                }
            }

            PawlygonEditorUI.EndActionBar();
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        /// <summary>
        /// Analyzes the selected avatar's FX controller (the base controller when the FX layer uses an
        /// override), selects the recommendation and builds the plan.
        /// </summary>
        /// <param name="keepGestureSelection">Carry the previous gesture selection over instead of re-selecting the recommendation.</param>
        /// <param name="keepBlinkSelection">Same for blink layers.</param>
        /// <param name="resetView">Expand only the layers needing attention (new avatar); otherwise keep expansions.</param>
        private void Analyze(bool keepGestureSelection, bool keepBlinkSelection, bool resetView)
        {
            needsAnalysis = false;
            resetViewOnAnalysis = false;

            FXGestureCheckerCore.AnalysisResult previous = analysis;
            analysis = null;
            plan = null;
            overrideController = null;
            analysisProblem = null;

            if (selectedAvatar == null || PawlygonEditorUtils.FindVRCAvatarDescriptorType() == null)
            {
                view.ExpandLayersNeedingAttention(null);
                return;
            }

            FXGestureCheckerCore.AnalysisResult result = FXGestureCheckerCore.Analyze(selectedAvatar);

            if (!result.Success && result.OverrideController != null &&
                result.OverrideController.runtimeAnimatorController is AnimatorController baseController)
            {
                // Guards go into a copy of the base controller (see Apply), so analyze the base.
                overrideController = result.OverrideController;
                FXGestureCheckerCore.AnalysisResult baseResult = FXGestureCheckerCore.Analyze(baseController);
                baseResult.Descriptor = result.Descriptor;
                baseResult.DescriptorType = result.DescriptorType;
                result = baseResult;
            }

            if (!result.Success)
            {
                analysisProblem = result.StatusMessage;
                analysisProblemType = result.StatusMessageType == MessageType.None ? MessageType.Info : result.StatusMessageType;
                view.ExpandLayersNeedingAttention(null);
                return;
            }

            analysis = result;
            FXGestureCheckerCore.SelectRecommended(analysis);
            if (previous != null)
            {
                FXGestureCheckerCore.CopySelection(previous, analysis, keepGestureSelection, keepBlinkSelection);
            }

            plan = FXGestureCheckerCore.GetRecommendedPlan(analysis);

            if (resetView)
            {
                view.ExpandLayersNeedingAttention(analysis);
            }
        }

        // =====================================================================
        // Copy mode
        // =====================================================================

        /// <summary>
        /// Why the analyzed controller must not be edited in place, or null. Not when it already is the
        /// copy this window made.
        /// </summary>
        private string GetForcedCopyReason()
        {
            AnimatorController controller = FXController;
            if (controller == null || controller == sessionCopy) return null;

            if (overrideController != null)
            {
                return $"The avatar uses the override controller '{overrideController.name}'. Its base controller " +
                       $"'{controller.name}' may be shared by other avatars, so the guards always go into a copy.";
            }

            if (FXGestureCheckerCore.IsControllerReadOnly(controller))
            {
                return $"'{controller.name}' is inside a read-only package and can't be edited, so the guards go into a copy.";
            }

            return null;
        }

        /// <summary>
        /// True when applying will create a new copy: copy mode is on (or forced) and the analyzed
        /// controller isn't already the copy this window created for the avatar.
        /// </summary>
        private bool WillCreateCopy()
        {
            return FXController != null && FXController != sessionCopy && (workOnCopy || GetForcedCopyReason() != null);
        }

        private string GetCopyFolder()
        {
            return customCopyFolder ?? FXGestureCheckerCore.GetDefaultCopyFolder(FXController);
        }

        /// <summary>
        /// Completes "This changes ..." in the confirmation dialog: where the changes will be written.
        /// </summary>
        private string DescribeTarget()
        {
            if (WillCreateCopy())
            {
                string copyPath = FXGestureCheckerCore.GetCopyPath(FXController, GetCopyFolder());
                string original = AssetDatabase.GetAssetPath(FXController);
                return overrideController != null
                    ? $"a copy of the base controller at '{copyPath}' and a copy of the override '{overrideController.name}' that uses it " +
                      $"('{selectedAvatar.name}' will use them; the originals stay unchanged)"
                    : $"a copy at '{copyPath}' ('{selectedAvatar.name}' will use it; the original '{original}' stays unchanged)";
            }

            string path = AssetDatabase.GetAssetPath(FXController);
            return FXController == sessionCopy
                ? $"'{path}', the copy made earlier, in place"
                : $"the original controller '{path}', in place (every avatar that uses it changes)";
        }

        /// <summary>
        /// Copies the analyzed controller (and the override controller, if any), assigns the copy to the
        /// analyzed avatar's descriptor and remembers it as <see cref="sessionCopy"/>.
        /// </summary>
        private AnimatorController CreateAndAssignCopy()
        {
            string folder = GetCopyFolder();
            AnimatorController copy = FXGestureCheckerCore.CopyFXController(FXController, folder, out string error);
            if (copy == null)
            {
                status.Error(error ?? "Couldn't copy the FX controller.");
                return null;
            }

            RuntimeAnimatorController assigned = copy;
            if (overrideController != null)
            {
                AnimatorOverrideController overrideCopy = FXGestureCheckerCore.CopyOverrideController(overrideController, copy, folder, out error);
                if (overrideCopy == null)
                {
                    status.Error(error ?? "Couldn't copy the override controller.", "Ping", PawlygonStatus.Ping(copy));
                    return null;
                }
                assigned = overrideCopy;
            }

            if (!FXGestureCheckerCore.AssignFXRuntimeControllerToDescriptor(analysis.Descriptor, analysis.DescriptorType, assigned))
            {
                status.Error($"Made a copy, but couldn't give it to '{selectedAvatar.name}'. Assign it in the Avatar Descriptor's FX slot.",
                    "Ping", PawlygonStatus.Ping(assigned));
                return null;
            }

            sessionCopy = copy;
            return copy;
        }

        // =====================================================================
        // Applying
        // =====================================================================

        /// <summary>
        /// Why <paramref name="scope"/> can't be applied right now, or null.
        /// </summary>
        private string GetApplyBlockedReason(ApplyScope scope)
        {
            if (analysis == null || FXController == null) return "Pick an avatar with an FX controller first.";
            if (analysis.Descriptor == null) return "The avatar changed since the last check. Click Re-analyse.";

            // Read-only and override controllers never reach here in place: WillCreateCopy forces a copy.
            switch (scope)
            {
                case ApplyScope.Expressions: return plan?.GestureGuardError;
                case ApplyScope.Blink: return plan?.BlinkGuardError;
                default: return null;
            }
        }

        private void Apply(ApplyScope scope)
        {
            string blocked = GetApplyBlockedReason(scope);
            if (blocked != null)
            {
                status.Error(blocked);
                return;
            }

            // A part blocked by a parameter conflict is skipped (the plan already leaves it out).
            bool gestures = scope != ApplyScope.Blink && plan.GestureGuardError == null;
            bool blink = scope != ApplyScope.Expressions && plan.BlinkGuardError == null;

            List<string> changes = scope == ApplyScope.Recommended
                ? plan.Changes
                : FXGestureCheckerCore.DescribeSelectedChanges(analysis, gestures, blink);
            if (changes.Count == 0)
            {
                status.Warning("Nothing to apply: tick at least one guard first.");
                return;
            }

            string title = scope == ApplyScope.Recommended ? "Apply Recommended Guards" : "Apply Selected Guards";
            if (!EditorUtility.DisplayDialog(title, FXGestureCheckerCore.BuildConfirmationMessage(changes, DescribeTarget()), "Apply", "Cancel"))
            {
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(title);

            AnimatorController target = FXController;
            FXGestureCheckerCore.AnalysisResult targetAnalysis = analysis;

            if (WillCreateCopy())
            {
                target = CreateAndAssignCopy();
                if (target == null)
                {
                    RequestAnalysis(resetView: false);
                    return;
                }

                // Analyze the copy itself so the guards are written into its own states and transitions,
                // then carry the user's selection over (keys and layer indices match the original).
                targetAnalysis = FXGestureCheckerCore.Analyze(target);
                if (!targetAnalysis.Success)
                {
                    status.Error($"Made a copy at '{AssetDatabase.GetAssetPath(target)}', but couldn't read it back.", "Ping", PawlygonStatus.Ping(target));
                    RequestAnalysis(resetView: false);
                    return;
                }
                FXGestureCheckerCore.CopySelection(analysis, targetAnalysis);
            }

            if (scope == ApplyScope.Recommended)
            {
                FXGestureCheckerCore.SelectRecommended(targetAnalysis);
            }

            FXGestureCheckerCore.ApplyCounts counts = FXGestureCheckerCore.ApplySelected(target, targetAnalysis, gestures, blink);
            Undo.CollapseUndoOperations(undoGroup);

            // Fresh recommendation for what was applied; keep the user's picks on the other tab.
            Analyze(keepGestureSelection: !gestures, keepBlinkSelection: !blink, resetView: false);

            string where = target == sessionCopy ? $"the copy '{target.name}'" : $"'{target.name}'";
            status.Info($"{counts.Describe()} in {where}.", "Ping", PawlygonStatus.Ping(target));
        }
    }
}
