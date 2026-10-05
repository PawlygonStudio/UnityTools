using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Setup tab: detected bone chains, saving them to the profile (the action bar's main action), and the
    /// rig data export.
    /// </summary>
    public partial class FaceTrackingExtras
    {
        // =====================================================================
        // Setup tab
        // =====================================================================

        private void DrawSetupTab()
        {
            if (selectedAvatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar above.", MessageType.Info);
                return;
            }

            if (analysis == null || !analysis.Success)
            {
                using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                {
                    EditorGUILayout.LabelField("Bone Chains", sectionTitleStyle);
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField(
                        "Find the ear and tail bones on the avatar with Detect Ears & Tail below. The status bar says what went wrong if it fails.",
                        PawlygonEditorUI.SubLabelStyle);
                }
                return;
            }

            using (new EditorGUI.DisabledScope(mode != Mode.Idle))
            {
                DrawRigSection();
            }

            EditorGUILayout.Space(SectionSpacing);
            DrawDebugSection();
        }

        // =====================================================================
        // Drawing: Rig
        // =====================================================================

        private void DrawRigSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Bone Chains", sectionTitleStyle);
                    if (GUILayout.Button("Re-detect", GUILayout.Width(90f)))
                    {
                        RunAnalysis();
                        if (profile != null) ApplyProfileChains();
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Check the detected chains. Pick another candidate or drag a different root bone if a chain is wrong. Click a bone to select it in the scene.",
                    PawlygonEditorUI.SubLabelStyle);

                foreach (var slot in Slots)
                {
                    EditorGUILayout.Space(10f);
                    PawlygonEditorUI.DrawSeparator();
                    EditorGUILayout.Space(6f);
                    DrawSlot(slot.Slot, slot.Label, slot.Kind);
                }

                EditorGUILayout.Space(10f);
                PawlygonEditorUI.DrawSeparator();
                EditorGUILayout.Space(8f);
                DrawSaveChains();
            }
        }

        private void DrawSlot(string slot, string label, FaceTrackingExtrasCore.ChainKind kind)
        {
            selectedChains.TryGetValue(slot, out var chain);
            var candidates = kind == FaceTrackingExtrasCore.ChainKind.Ear ? analysis.EarChains : analysis.TailChains;

            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

            // Candidate picker
            if (candidates.Count > 0)
            {
                // A manually picked root is not one of the candidates; show it as "(custom)" in slot 0.
                bool isCustom = chain != null && !candidates.Contains(chain);
                var options = new List<string> { isCustom ? "(custom)" : "(none)" };
                options.AddRange(candidates.Select(c => $"{c.DisplayName} - {c.Side}"));

                int currentIndex = chain != null ? candidates.IndexOf(chain) + 1 : 0;
                int newIndex = EditorGUILayout.Popup("Detected", currentIndex, options.ToArray());
                if (newIndex != currentIndex)
                {
                    selectedChains[slot] = newIndex == 0 ? null : candidates[newIndex - 1];
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                EditorGUILayout.LabelField("Detected", "No candidates found by name");
            }

            // Manual root override
            EditorGUI.BeginChangeCheck();
            Transform newRoot = (Transform)EditorGUILayout.ObjectField("Root Bone", chain?.Root, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck())
            {
                if (newRoot == null)
                {
                    chain = null;
                }
                else if (!newRoot.IsChildOf(selectedAvatar.transform))
                {
                    SetStatus($"'{newRoot.name}' is not part of '{selectedAvatar.name}'.", MessageType.Warning);

                    // The status bar appears below; restart the event so the layout includes it.
                    GUIUtility.ExitGUI();
                }
                else
                {
                    chain = FaceTrackingExtrasCore.BuildChainFromRoot(newRoot, kind, analysis);
                }
                selectedChains[slot] = chain;
                GUIUtility.ExitGUI();
            }

            if (chain == null) return;

            EditorGUILayout.Space(4f);
            DrawBoneList(chain);
            DrawChainWarnings(chain, slot);
        }

        private void DrawBoneList(FaceTrackingExtrasCore.BoneChain chain)
        {
            if (chain.SkippedBones.Count > 0)
            {
                EditorGUILayout.LabelField(
                    $"<color=#909090>Skipped (constraint helpers): {string.Join(" → ", chain.SkippedBones.Select(b => b.name))}</color>",
                    PawlygonEditorUI.RichMiniLabelStyle);
            }

            for (int i = 0; i < chain.Bones.Count; i++)
            {
                Transform bone = chain.Bones[i];
                var badges = new List<string>();
                if (analysis.PhysBoneDrivers.ContainsKey(bone)) badges.Add("<color=#5FA8FF>PhysBone</color>");
                if (analysis.ConstraintDrivers.ContainsKey(bone)) badges.Add("<color=#FFA040>Constraint</color>");

                string text = $"{new string(' ', i * 3)}└ {bone.name}";
                if (badges.Count > 0) text += "   " + string.Join("  ", badges);

                if (GUILayout.Button(text, boneButtonStyle))
                {
                    SelectBone(bone);
                }
            }
        }

        private void DrawChainWarnings(FaceTrackingExtrasCore.BoneChain chain, string slot)
        {
            var constrained = chain.Bones.Where(b => analysis.ConstraintDrivers.ContainsKey(b)).ToList();
            if (constrained.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{string.Join(", ", constrained.Select(b => b.name))} driven by a constraint. Animating these bones will be overridden; animate the bones the constraint follows instead.",
                    MessageType.Warning);
            }

            if (chain.IsHelper)
            {
                EditorGUILayout.HelpBox("This looks like a helper chain (dummy/constraint bones), not the visible one.", MessageType.Warning);
            }

            if (chain.Branches)
            {
                EditorGUILayout.HelpBox("This chain branches. The longest branch was followed.", MessageType.Info);
            }

            if (slot != SlotTail && chain.Side == FaceTrackingExtrasCore.ChainSide.Centre)
            {
                EditorGUILayout.HelpBox("This chain sits on the centre line, so it may not be an ear.", MessageType.Warning);
            }

            bool sideMismatch = (slot == SlotEarLeft && chain.Side == FaceTrackingExtrasCore.ChainSide.Right)
                || (slot == SlotEarRight && chain.Side == FaceTrackingExtrasCore.ChainSide.Left);
            if (sideMismatch)
            {
                EditorGUILayout.HelpBox($"This chain is on the avatar's {chain.Side.ToString().ToLowerInvariant()} side.", MessageType.Warning);
            }
        }

        private void DrawSaveChains()
        {
            if (ProfileBelongsToAnotherAvatar)
            {
                bool ownProfile = profile.HasIdentity && profile.BelongsTo(selectedAvatar);
                EditorGUILayout.HelpBox(
                    ownProfile
                        ? $"Some bones saved in '{profile.name}' are missing from this avatar (renamed or deleted). {sessionError}"
                        : $"The profile '{profile.name}' was made for '{profile.avatarName}', and its bones are not on this avatar. " +
                          "Click Create New Profile below for this avatar instead of overwriting that one.",
                    MessageType.Warning);
                return;
            }

            if (profile != null && ChainsMatchProfile())
            {
                EditorGUILayout.LabelField("<color=#6BCB77>✓</color> Chains saved in the profile.", poseLabelStyle);
                return;
            }

            bool earsMismatch = selectedChains.TryGetValue(SlotEarLeft, out var left) && selectedChains.TryGetValue(SlotEarRight, out var right)
                && left != null && right != null && left.Bones.Count != right.Bones.Count;
            if (earsMismatch)
            {
                EditorGUILayout.HelpBox("The ears have different bone counts. Mirroring matches bones from the root, and the longer ear's extra bones stay at rest in mirrored poses.", MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                profile == null
                    ? "Click Create Profile to save these chains as this avatar's Face Tracking Extras profile. The bones' current rotations are stored as the rest pose."
                    : "The chains differ from the saved profile. Click Update Profile to save them.",
                MessageType.Info);
        }

        /// <summary>
        /// Setup's action bar: detect the chains, create or update the profile, or (when the profile belongs to
        /// another avatar) create a new one; once the chains are saved, move on to the poses.
        /// </summary>
        private void DrawSetupActions()
        {
            if (analysis == null || !analysis.Success)
            {
                PawlygonEditorUI.BeginActionBar();
                GUILayout.FlexibleSpace();
                if (PawlygonEditorUI.DrawPrimaryButton("Detect Ears & Tail", 28f, GUILayout.Width(170f)))
                {
                    RunAnalysis();
                    if (analysis.Success && profile != null)
                    {
                        ApplyProfileChains();
                        BindSession();
                    }
                    GUIUtility.ExitGUI();
                }
                PawlygonEditorUI.EndActionBar();
                return;
            }

            using (new EditorGUI.DisabledScope(mode != Mode.Idle))
            {
                PawlygonEditorUI.BeginActionBar();

                if (ProfileBelongsToAnotherAvatar)
                {
                    if (PawlygonEditorUI.DrawSecondaryButton("Overwrite That Profile", 28f)
                        && EditorUtility.DisplayDialog(
                            "Overwrite Profile",
                            $"Replace the bone chains in '{profile.name}' (made for '{profile.avatarName}') with this avatar's? Its poses for changed chains are cleared.",
                            "Overwrite", "Cancel"))
                    {
                        SaveChains();
                        GUIUtility.ExitGUI();
                    }

                    GUILayout.FlexibleSpace();

                    if (PawlygonEditorUI.DrawPrimaryButton("Create New Profile", 28f, GUILayout.Width(170f)))
                    {
                        profile = null;
                        session = null;
                        sessionError = null;
                        SaveChains();
                        GUIUtility.ExitGUI();
                    }
                }
                else if (profile != null && ChainsMatchProfile())
                {
                    GUILayout.FlexibleSpace();
                    if (PawlygonEditorUI.DrawPrimaryButton("Next: Ears & Tail", 28f, GUILayout.Width(170f)))
                    {
                        SwitchTab(Tab.EarsAndTail);
                        GUIUtility.ExitGUI();
                    }
                }
                else
                {
                    GUILayout.FlexibleSpace();
                    if (PawlygonEditorUI.DrawPrimaryButton(profile == null ? "Create Profile" : "Update Profile", 28f, GUILayout.Width(170f)))
                    {
                        SaveChains();
                        GUIUtility.ExitGUI();
                    }
                }

                PawlygonEditorUI.EndActionBar();
            }
        }

        // =====================================================================
        // Drawing: Debug
        // =====================================================================

        private void DrawDebugSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                showDebug = EditorGUILayout.Foldout(showDebug, "Rig Data (debug)", true);
                if (!showDebug) return;

                EditorGUILayout.LabelField(
                    $"Exports bone orientations, rest poses, PhysBones and constraints to <b>{FaceTrackingExtrasCore.DebugFolderName}/</b> in the project folder.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(8f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (PawlygonEditorUI.DrawSecondaryButton("Export Rig Data", 28f))
                    {
                        ExportRigData();
                        GUIUtility.ExitGUI();
                    }

                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(lastExportPath)))
                    {
                        if (GUILayout.Button("Show File", GUILayout.Height(28f), GUILayout.Width(100f)))
                        {
                            EditorUtility.RevealInFinder(lastExportPath);
                        }
                    }
                }
            }
        }

        private void ExportRigData()
        {
            try
            {
                lastExportPath = FaceTrackingExtrasCore.ExportJson(analysis, selectedChains);
                string exportedPath = lastExportPath;
                SetStatus($"Rig data exported to {exportedPath}", MessageType.Info, "Show File", () => EditorUtility.RevealInFinder(exportedPath));
                Debug.Log($"{FaceTrackingExtrasCore.LogPrefix} Rig data exported to {lastExportPath}");
            }
            catch (System.Exception ex)
            {
                SetStatus($"Export failed: {ex.Message}", MessageType.Error);
                Debug.LogException(ex);
            }
        }
    }
}
