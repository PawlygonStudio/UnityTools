using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for Blendshape Fixes: finds the fix shapes on an avatar (helper blendshapes that prevent clipping
    /// while two face tracking shapes are active together, like MouthSmileLeft-EyeClosedLeft) and generates an
    /// animator that drives them from Pawlygon VRCFT 1.x face tracking, as a VRCFury prefab. The plan comes from
    /// <see cref="FTFixesPlan"/>, the animator from <see cref="FTFixesGenerator"/>.
    /// </summary>
    public class BlendshapeFixes : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Blendshape Fixes";
        private const string WindowTitle = "Blendshape Fixes";
        private const float SectionSpacing = 10f;

        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedAvatar;
        private FTFixesProfile profile;
        private SerializedObject profileObject;
        private FTFixesPlan plan = new FTFixesPlan();
        private bool planDirty = true;
        private bool showParameters;
        private bool? prefabOnAvatarCache;
        private FTFixesGenerator.Result lastResult;
        private readonly PawlygonStatus status = new PawlygonStatus();

        [MenuItem(MenuPath, priority = 42)] // Tools: Tune
        public static void ShowWindow()
        {
            var window = GetWindow<BlendshapeFixes>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(420f, 420f);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            Undo.undoRedoPerformed += OnUndoRedo;
            if (selectedAvatar == null) selectedAvatar = EyeMuscleSettingsCore.FindFirstAvatarInScene();
            LoadAvatar(selectedAvatar);
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnUndoRedo()
        {
            profileObject?.Update();
            MarkDirty();
        }

        // Meshes, blendshapes or the generated prefab may have changed meanwhile
        private void OnHierarchyChange() => MarkDirty();
        private void OnProjectChange() => MarkDirty();
        private void OnFocus() => MarkDirty();

        private void MarkDirty()
        {
            planDirty = true;
            prefabOnAvatarCache = null;
            Repaint();
        }

        private void LoadAvatar(GameObject avatar)
        {
            selectedAvatar = avatar;
            lastResult = null;
            status.Clear();

            profile = avatar != null ? FTFixesGenerator.FindProfile(avatar) : null;
            if (profile == null && avatar != null)
            {
                // Saved with the generated files on the first Generate
                profile = CreateInstance<FTFixesProfile>();
                profile.hideFlags = HideFlags.DontSave;
            }
            profileObject = profile != null ? new SerializedObject(profile) : null;
            MarkDirty();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();

            // Everything that decides which controls are drawn is refreshed before the layout pass.
            if (Event.current.type == EventType.Layout && planDirty)
            {
                planDirty = false;
                plan = FTFixesPlan.Resolve(profile, selectedAvatar);
            }

            PawlygonEditorUI.DrawHeader(
                WindowTitle,
                "Drives fix blendshapes like MouthSmileLeft-EyeClosedLeft from VRCFT face tracking (Pawlygon VRCFT 1.x).",
                PawlygonEditorUI.DocumentationUrl);

            if (PawlygonEditorUI.DrawAvatarBar(this, ref selectedAvatar, "Avatar"))
            {
                LoadAvatar(selectedAvatar);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.Space(6f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
            if (selectedAvatar == null || profileObject == null)
            {
                DrawNoAvatar();
            }
            else
            {
                profileObject.Update();
                EditorGUI.BeginChangeCheck();

                DrawFixes();
                EditorGUILayout.Space(SectionSpacing);
                DrawManualFixes();
                EditorGUILayout.Space(SectionSpacing);
                DrawParameters();
                EditorGUILayout.Space(SectionSpacing);
                DrawGenerate();

                if (EditorGUI.EndChangeCheck())
                {
                    profileObject.ApplyModifiedProperties();
                    planDirty = true;
                }
            }
            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);
            if (selectedAvatar != null && profile != null) DrawActions();
            PawlygonEditorUI.DrawFooter();
        }

        private void DrawNoAvatar()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Pick an Avatar",
                    "Choose your avatar above: drag it from the Hierarchy, pick it from the Scene list, or select it and click Use Selection.");
            }
        }

        private void DrawFixes()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Fixes",
                    "Fix shapes are helper blendshapes that prevent clipping while two face tracking shapes are active together, " +
                    "named after the shapes they fix and joined with '-' (e.g. MouthSmileLeft-EyeClosedLeft). Each one follows the " +
                    "same VRCFT parameters as the shapes it fixes.");

                EditorGUILayout.PropertyField(profileObject.FindProperty(nameof(FTFixesProfile.autoDetect)), new GUIContent("Auto-detect"));
                EditorGUILayout.PropertyField(profileObject.FindProperty(nameof(FTFixesProfile.combine)), new GUIContent("Combine"));
                EditorGUILayout.Space(4f);

                var detected = plan.Entries.Where(e => !e.IsManual).ToList();
                if (!profile.autoDetect)
                {
                    EditorGUILayout.LabelField("Auto-detect is off: only the manual fixes below are driven.", PawlygonEditorUI.SubLabelStyle);
                    return;
                }
                if (detected.Count == 0)
                {
                    EditorGUILayout.LabelField(
                        "No fix shapes found: no blendshape on the avatar is named after face tracking shapes joined with '-'.",
                        PawlygonEditorUI.SubLabelStyle);
                    return;
                }

                foreach (var entry in detected) DrawDetectedRow(entry);
            }
        }

        private void DrawDetectedRow(FTFixesPlan.Entry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool include = EditorGUILayout.ToggleLeft(new GUIContent(entry.Blendshape, "Drive this fix."), !entry.Excluded, GUILayout.MinWidth(200f));
                if (include == entry.Excluded)
                {
                    Undo.RecordObject(profile, include ? "Include Blendshape Fix" : "Exclude Blendshape Fix");
                    if (include) profile.excludedFixes.Remove(entry.Blendshape);
                    else if (!profile.excludedFixes.Contains(entry.Blendshape)) profile.excludedFixes.Add(entry.Blendshape);
                    EditorUtility.SetDirty(profile);
                    profileObject.Update();
                    planDirty = true;
                }

                GUILayout.FlexibleSpace();
                if (entry.Excluded) PawlygonEditorUI.DrawBadge("Off", PawlygonEditorUI.BadgeKind.Neutral, "Not driven.");
                else PawlygonEditorUI.DrawBadge(Plural(entry.MeshPaths.Count, "mesh", "meshes"), PawlygonEditorUI.BadgeKind.Ok, string.Join("\n", entry.MeshPaths));
            }
            EditorGUILayout.LabelField(DescribeSources(entry), PawlygonEditorUI.RichMiniLabelStyle);
        }

        private void DrawManualFixes()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Manual Fixes",
                    "Fix shapes named differently: the fix blendshape and the shapes it fixes, separated by commas. " +
                    "An entry with the same blendshape as a detected fix replaces it.");

                EditorGUILayout.PropertyField(profileObject.FindProperty(nameof(FTFixesProfile.fixes)), new GUIContent("Fixes"), true);

                foreach (var entry in plan.Entries.Where(e => e.IsManual))
                {
                    if (entry.Problem != null) EditorGUILayout.HelpBox($"{entry.Blendshape}: {entry.Problem}", MessageType.Warning);
                    else EditorGUILayout.LabelField($"<b>{entry.Blendshape}</b>  {DescribeSources(entry)}", PawlygonEditorUI.RichMiniLabelStyle);
                }
            }
        }

        private void DrawParameters()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                showParameters = EditorGUILayout.Foldout(showParameters, "Parameters", true);
                if (!showParameters) return;

                EditorGUILayout.LabelField(
                    "The fixes read the same VRCFT parameters as the Pawlygon VRCFT 1.x animator: the synced OSCmooth proxies by default. " +
                    "Fixes of eye shapes only run while eye tracking is on, fixes of mouth shapes while lip tracking is on.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.PropertyField(profileObject.FindProperty(nameof(FTFixesProfile.parameterPrefix)), new GUIContent("Parameter Prefix"));
                EditorGUILayout.PropertyField(profileObject.FindProperty(nameof(FTFixesProfile.eyeTrackingActive)), new GUIContent("Eye Tracking Active"));
                EditorGUILayout.PropertyField(profileObject.FindProperty(nameof(FTFixesProfile.lipTrackingActive)), new GUIContent("Lip Tracking Active"));
            }
        }

        private void DrawGenerate()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Generate",
                    "Builds an FX controller that drives the fixes and a VRCFury prefab to add to the avatar. Needs VRCFury and the Pawlygon VRCFT 1.x prefab on the avatar.");

                int count = plan.Generated.Count();
                EditorGUILayout.LabelField($"<b>Fixes:</b> {count}", PawlygonEditorUI.RichMiniLabelStyle);
                EditorGUILayout.LabelField($"<b>Output:</b> {FTFixesGenerator.GetOutputFolder(selectedAvatar)}", PawlygonEditorUI.RichMiniLabelStyle);

                if (!AssetDatabase.Contains(profile))
                {
                    EditorGUILayout.LabelField("These settings are saved with the generated files.", PawlygonEditorUI.SubLabelStyle);
                }
                else if (!string.IsNullOrEmpty(profile.lastGeneratedSignature) && profile.lastGeneratedSignature != FTFixesPlan.Signature(profile, plan))
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.HelpBox("The fixes or settings changed since the last generation. Generate again to update the animator.", MessageType.Warning);
                }

                if (lastResult != null)
                {
                    foreach (string warning in lastResult.Warnings) EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }
        }

        private void DrawActions()
        {
            bool hasPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FTFixesGenerator.GetPrefabPath(selectedAvatar)) != null;
            if (prefabOnAvatarCache == null) prefabOnAvatarCache = hasPrefab && FTFixesGenerator.IsPrefabOnAvatar(selectedAvatar);
            bool onAvatar = prefabOnAvatarCache.Value;

            PawlygonEditorUI.BeginActionBar();

            using (new EditorGUI.DisabledScope(!hasPrefab || onAvatar))
            {
                var addContent = new GUIContent(
                    onAvatar ? "✓ Prefab Is on the Avatar" : "Add Prefab to Avatar",
                    !hasPrefab ? "Generate first: this adds the generated prefab to the avatar." : $"Adds {FTFixesGenerator.PrefabName} to {selectedAvatar.name}.");
                if (PawlygonEditorUI.DrawSecondaryButton(addContent, 28f))
                {
                    FTFixesGenerator.AddPrefabToAvatar(selectedAvatar);
                    prefabOnAvatarCache = null;
                    GUIUtility.ExitGUI();
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!plan.Generated.Any()))
            {
                if (PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Generate", "Builds the FX controller and the VRCFury prefab."), 28f, GUILayout.Width(150f)))
                {
                    Generate();
                    GUIUtility.ExitGUI();
                }
            }

            PawlygonEditorUI.EndActionBar();
        }

        private void Generate()
        {
            try
            {
                bool wasSaved = AssetDatabase.Contains(profile);
                if (!wasSaved) profile.hideFlags = HideFlags.None;

                lastResult = FTFixesGenerator.Generate(profile, selectedAvatar);
                if (!wasSaved && AssetDatabase.Contains(profile)) profileObject = new SerializedObject(profile);

                if (!lastResult.Success) status.Error(lastResult.Message);
                else if (lastResult.PrefabPath != null)
                {
                    status.Info(lastResult.Message, "Ping", PawlygonStatus.Ping(AssetDatabase.LoadAssetAtPath<GameObject>(lastResult.PrefabPath)));
                }
                else status.Warning(lastResult.Message);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                status.Error($"Generation failed: {ex.Message}");
            }
            finally
            {
                if (!AssetDatabase.Contains(profile)) profile.hideFlags = HideFlags.DontSave;
                MarkDirty();
            }
        }

        private static string DescribeSources(FTFixesPlan.Entry entry)
        {
            return "Fixes " + string.Join(" + ", entry.Sources.Select(s => $"{s.Shape} <color=#888888>({s.Parameter})</color>"));
        }

        private static string Plural(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";
    }
}
