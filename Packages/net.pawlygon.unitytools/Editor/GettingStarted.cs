using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Entry point for the Pawlygon tools: lists them in workflow order (Prepare → Check → Tune → Publish)
    /// with what each one is for, a quick status for the selected avatar where that is cheap to work out,
    /// and a button to open it. Opens once after the package is installed or updated (can be turned off).
    /// </summary>
    public class GettingStarted : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Getting Started";
        private const string WindowTitle = "Getting Started";
        private const string ReleasesUrl = "https://github.com/PawlygonStudio/UnityTools/releases";

        internal const string ShowOnUpdatePrefKey = "Pawlygon.UnityTools.GettingStarted.ShowOnUpdate";
        internal const string LastShownVersionPrefKey = "Pawlygon.UnityTools.GettingStarted.LastShownVersion";

        [SerializeField] private Vector2 scrollPosition;
        private GameObject avatar;

        // Per-avatar statuses are cached and refreshed when the avatar or the hierarchy changes.
        private GameObject statusAvatar;
        private (string Text, PawlygonEditorUI.BadgeKind Kind)? blendshapeStatus;
        private (string Text, PawlygonEditorUI.BadgeKind Kind)? extrasStatus;
        private double extrasStatusTime;

        private class ToolEntry
        {
            public string Name;
            public string Description;
            public Action Open;
            public Func<GettingStarted, (string Text, PawlygonEditorUI.BadgeKind Kind)?> Status;
        }

        private static readonly (string Stage, string Description, ToolEntry[] Tools)[] Workflow =
        {
            ("1. Prepare", "Set up a working copy of the avatar with your face tracking edit.", new[]
            {
                new ToolEntry
                {
                    Name = "Avatar Setup Wizard",
                    Description = "Copies a vendor avatar, swaps in your face tracking FBX, checks the FX controller and generates the patch files.",
                    Open = AvatarSetupWizard.ShowWindow,
                },
            }),
            ("2. Check", "Make sure the avatar is ready for face tracking.", new[]
            {
                new ToolEntry
                {
                    Name = "Face Tracking Blendshapes",
                    Description = "Checks the meshes for the Unified Expressions blendshapes face tracking needs, and lists what's missing.",
                    Open = UnifiedExpressionChecker.ShowWindow,
                    Status = w => w.blendshapeStatus,
                },
                new ToolEntry
                {
                    Name = "FX Gesture Checker",
                    Description = "Stops hand-gesture expressions and blinking from fighting face tracking while it's active.",
                    Open = FXGestureChecker.ShowWindow,
                },
            }),
            ("3. Tune", "Adjust and extend the face tracking.", new[]
            {
                new ToolEntry
                {
                    Name = "Eye Muscle Settings",
                    Description = "Sets how far the eyes can look in each direction, with a live preview.",
                    Open = EyeMuscleSettings.ShowWindow,
                },
                new ToolEntry
                {
                    Name = "Face Tracking Extras",
                    Description = "Ears, tail and pupils that react to your face: pose them once and generate the animations.",
                    Open = FaceTrackingExtras.ShowWindow,
                    Status = w => w.extrasStatus,
                },
            }),
            ("4. Publish", "Prepare the patch for your customers.", new[]
            {
                new ToolEntry
                {
                    Name = "Patch Config Package Rules",
                    Description = "Adds required packages (and minimum versions) to PatcherHub patch configs.",
                    Open = PatchConfigRulesEditor.ShowWindow,
                },
            }),
        };

        [MenuItem(MenuPath, priority = 0)]
        public static void ShowWindow()
        {
            GettingStarted window = GetWindow<GettingStarted>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(520f, 460f);
        }

        private void OnEnable()
        {
            if (avatar == null) avatar = PawlygonEditorUtils.GetPreferredAvatar();
        }

        private void OnHierarchyChange()
        {
            statusAvatar = null;
            Repaint();
        }

        private void OnFocus()
        {
            // Other tools may have changed the avatar's state (e.g. generated Face Tracking Extras).
            statusAvatar = null;
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            PawlygonEditorUI.DrawHeader(WindowTitle, "The Pawlygon face tracking workflow, step by step.", PawlygonEditorUI.DocumentationUrl);

            if (PawlygonEditorUI.DrawAvatarBar(this, ref avatar))
            {
                statusAvatar = null;
                GUIUtility.ExitGUI();
            }

            RefreshStatuses();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            foreach (var stage in Workflow)
            {
                EditorGUILayout.Space(6f);
                using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                {
                    PawlygonEditorUI.DrawSectionHeader(stage.Stage, stage.Description);

                    for (int i = 0; i < stage.Tools.Length; i++)
                    {
                        if (i > 0)
                        {
                            EditorGUILayout.Space(4f);
                            PawlygonEditorUI.DrawSeparator();
                            EditorGUILayout.Space(4f);
                        }
                        DrawTool(stage.Tools[i]);
                    }
                }
            }

            EditorGUILayout.Space(8f);
            DrawPreferences();

            EditorGUILayout.EndScrollView();
            PawlygonEditorUI.DrawFooter();
        }

        private void DrawTool(ToolEntry tool)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(tool.Name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                        var status = tool.Status?.Invoke(this);
                        if (status.HasValue)
                        {
                            PawlygonEditorUI.DrawBadge(status.Value.Text, status.Value.Kind);
                        }
                        GUILayout.FlexibleSpace();
                    }
                    EditorGUILayout.LabelField(tool.Description, PawlygonEditorUI.SubLabelStyle);
                }

                if (PawlygonEditorUI.DrawSecondaryButton("Open", 24f, GUILayout.Width(72f)))
                {
                    tool.Open();
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void DrawPreferences()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                bool showOnUpdate = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show this window when the package updates"),
                    EditorPrefs.GetBool(ShowOnUpdatePrefKey, true));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetBool(ShowOnUpdatePrefKey, showOnUpdate);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button($"What's new in v{PawlygonEditorUI.GetPackageVersion()}", EditorStyles.linkLabel))
                {
                    Application.OpenURL(ReleasesUrl);
                }
                EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
            }
        }

        // =====================================================================
        // Per-avatar status
        // =====================================================================

        private void RefreshStatuses()
        {
            // Face Tracking Extras' "out of date" state changes as the user edits poses elsewhere; recheck it
            // every few seconds while the window is visible.
            bool extrasExpired = EditorApplication.timeSinceStartup - extrasStatusTime > 3.0;
            if (statusAvatar == avatar && !extrasExpired) return;

            if (statusAvatar != avatar) blendshapeStatus = GetBlendshapeStatus(avatar);
            extrasStatus = GetExtrasStatus(avatar);
            extrasStatusTime = EditorApplication.timeSinceStartup;
            statusAvatar = avatar;
        }

        private static (string, PawlygonEditorUI.BadgeKind)? GetBlendshapeStatus(GameObject target)
        {
            if (target == null) return null;

            int complete = 0, incomplete = 0;
            int required = PawlygonEditorUtils.RequiredUnifiedExpressionBlendshapes.Length;
            foreach (SkinnedMeshRenderer renderer in target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                int missing = PawlygonEditorUtils.GetMissingRequiredUnifiedBlendshapes(mesh).Length;
                if (missing == 0) complete++;
                else if (missing < required) incomplete++;
            }

            if (complete > 0 && incomplete == 0) return ("Ready", PawlygonEditorUI.BadgeKind.Ok);
            if (complete + incomplete == 0) return ("No face tracking shapes", PawlygonEditorUI.BadgeKind.Error);
            return ("Shapes missing", PawlygonEditorUI.BadgeKind.Warning);
        }

        private static (string, PawlygonEditorUI.BadgeKind)? GetExtrasStatus(GameObject target)
        {
            if (target == null) return null;

            FTExtrasProfile profile = AssetDatabase.FindAssets($"t:{nameof(FTExtrasProfile)}")
                .Select(guid => AssetDatabase.LoadAssetAtPath<FTExtrasProfile>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(p => p != null && p.BelongsTo(target))
                .OrderByDescending(p => p.HasIdentity)
                .FirstOrDefault();

            if (profile == null) return ("Not set up", PawlygonEditorUI.BadgeKind.Neutral);
            if (profile.IsGenerationStale) return ("Out of date", PawlygonEditorUI.BadgeKind.Warning);
            if (FTExtrasGenerator.IsPrefabOnAvatar(target)) return ("Generated", PawlygonEditorUI.BadgeKind.Ok);
            return ("Not generated", PawlygonEditorUI.BadgeKind.Info);
        }
    }

    /// <summary>
    /// Opens <see cref="GettingStarted"/> once after the package is installed or updated to a new version,
    /// unless the user turned that off in the window.
    /// </summary>
    [InitializeOnLoad]
    internal static class GettingStartedAutoOpen
    {
        static GettingStartedAutoOpen()
        {
            EditorApplication.delayCall += () =>
            {
                if (Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (!EditorPrefs.GetBool(GettingStarted.ShowOnUpdatePrefKey, true)) return;

                string version = PawlygonEditorUI.GetPackageVersion();
                if (EditorPrefs.GetString(GettingStarted.LastShownVersionPrefKey, string.Empty) == version) return;

                EditorPrefs.SetString(GettingStarted.LastShownVersionPrefKey, version);
                GettingStarted.ShowWindow();
            };
        }
    }
}
