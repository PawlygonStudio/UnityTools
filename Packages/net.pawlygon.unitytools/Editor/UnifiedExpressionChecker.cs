using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// "Face Tracking Blendshapes" window: checks meshes for the Unified Expressions blendshapes that VRChat
    /// face tracking drives. Accepts a scene avatar (scans its SkinnedMeshRenderers), a prefab asset (same,
    /// through the prefab's hierarchy) or a model asset (its mesh sub-assets). The check is read-only and runs
    /// automatically whenever the input or the project changes.
    /// </summary>
    public class UnifiedExpressionChecker : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Face Tracking Blendshapes";
        private const string WindowTitle = "Face Tracking Blendshapes";
        private const string DocsUrl = "https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/unified-blendshapes";
        private const float SectionSpacing = 8f;

        /// <summary>Face regions, in display order, taken from the required names' prefixes.</summary>
        private static readonly string[] Regions = { "Brow", "Eye", "Cheek", "Jaw", "Lip", "Mouth", "Nose", "Tongue" };

        private static readonly string[] Required = PawlygonEditorUtils.RequiredUnifiedExpressionBlendshapes;

        private static readonly Dictionary<string, int> RequiredPerRegion = Required
            .GroupBy(GetRegion)
            .ToDictionary(g => g.Key, g => g.Count());

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private GameObject selectedInput;
        private List<MeshAnalysis> results;
        private string sourceKind;
        private bool needsCheck = true;
        private bool showMeshesWithout;

        /// <summary>Foldout states the user changed, by <see cref="MeshAnalysis.Key"/>; others use the default.</summary>
        private readonly Dictionary<string, bool> foldouts = new Dictionary<string, bool>();
        private readonly PawlygonStatus status = new PawlygonStatus();

        // --- Styles ---
        private GUIStyle meshBoxStyle;
        private GUIStyle mutedMiniStyle;
        private GUIStyle wrappedMiniStyle;
        private GUIStyle boldFoldoutStyle;
        private bool stylesBuiltForProSkin;

        // =====================================================================
        // Data model
        // =====================================================================

        private class MeshAnalysis
        {
            public string Key;
            public string MeshName;
            public string SourcePath;
            public int TotalBlendshapeCount;

            /// <summary>Required names not found with exactly this spelling (case-sensitive).</summary>
            public string[] MissingBlendshapes;

            /// <summary>Required name to the mesh's name that matches it only when ignoring case.</summary>
            public Dictionary<string, string> CaseMismatches;

            public int FoundCount;
            public UnityEngine.Object PingTarget;

            public bool HasAnyUnifiedBlendshapes => FoundCount > 0 || CaseMismatches.Count > 0;
            public bool IsComplete => HasAnyUnifiedBlendshapes && MissingBlendshapes.Length == 0;
        }

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            UnifiedExpressionChecker window = GetWindow<UnifiedExpressionChecker>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(520f, 460f);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            if (selectedInput == null)
            {
                selectedInput = PawlygonEditorUtils.GetPreferredAvatar();
            }
            needsCheck = true;
        }

        // The meshes can change while the window is open (re-import, renamed blendshapes, edited hierarchy).
        private void OnFocus() => RequestCheck();
        private void OnProjectChange() => RequestCheck();
        private void OnHierarchyChange() => RequestCheck();

        private void RequestCheck()
        {
            needsCheck = true;
            Repaint();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();

            // Check before anything is laid out, so the Layout and Repaint passes draw the same controls.
            if (needsCheck && Event.current.type == EventType.Layout)
            {
                Check();
            }

            PawlygonEditorUI.DrawHeader(
                WindowTitle,
                "Checks your meshes for every Unified Expressions blendshape face tracking needs.",
                DocsUrl);

            if (PawlygonEditorUI.DrawAvatarBar(this, ref selectedInput, "Avatar or Model", allowAssets: true))
            {
                status.Clear();
                foldouts.Clear();
                showMeshesWithout = false;
                needsCheck = true;
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(4f);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
            DrawBody();
            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);
            PawlygonEditorUI.DrawFooter();
        }

        private void EnsureStyles()
        {
            if (meshBoxStyle != null && stylesBuiltForProSkin == EditorGUIUtility.isProSkin) return;
            stylesBuiltForProSkin = EditorGUIUtility.isProSkin;

            meshBoxStyle = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6) };

            mutedMiniStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };
            mutedMiniStyle.normal.textColor = PawlygonEditorUI.MutedColor;

            wrappedMiniStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, richText = true };

            boldFoldoutStyle = new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
        }

        private void DrawBody()
        {
            if (selectedInput == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick an avatar in the scene, or drag a model (FBX) or prefab from the Project window into the field above.",
                    MessageType.Info);
                return;
            }

            if (results == null) return;

            if (results.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    $"'{selectedInput.name}' has no meshes with blendshapes. Pick the avatar (or the model) that contains the face mesh.",
                    MessageType.Warning);
                return;
            }

            DrawSummary();
            EditorGUILayout.Space(SectionSpacing);
            DrawMeshList();
        }

        // =====================================================================
        // Check
        // =====================================================================

        private void Check()
        {
            needsCheck = false;
            results = null;
            sourceKind = null;
            if (selectedInput == null) return;

            results = new List<MeshAnalysis>();
            bool isAsset = EditorUtility.IsPersistent(selectedInput);

            // Model files store their meshes as sub-assets. Prefab assets (and scene objects) only reference
            // meshes from their renderers, so they are read through the hierarchy instead.
            bool isModelAsset = isAsset && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(selectedInput)) is ModelImporter;

            if (isModelAsset)
            {
                sourceKind = "model";
                AnalyzeModelAsset(selectedInput);
            }
            else
            {
                sourceKind = isAsset ? "prefab" : "scene object";
                AnalyzeHierarchy(selectedInput);
            }

            // Meshes that need work first, then complete ones, then meshes without any face tracking shapes.
            results = results
                .OrderBy(r => r.HasAnyUnifiedBlendshapes ? (r.IsComplete ? 1 : 0) : 2)
                .ToList();
        }

        private void AnalyzeHierarchy(GameObject root)
        {
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0) continue;

                string path = GetRelativeHierarchyPath(root.transform, renderer.transform);
                results.Add(AnalyzeMesh(mesh, string.IsNullOrEmpty(mesh.name) ? renderer.name : mesh.name, path, renderer));
            }
        }

        private void AnalyzeModelAsset(GameObject modelAsset)
        {
            string assetPath = AssetDatabase.GetAssetPath(modelAsset);
            if (string.IsNullOrEmpty(assetPath)) return;

            foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (subAsset is Mesh mesh && mesh.blendShapeCount > 0)
                {
                    results.Add(AnalyzeMesh(mesh, string.IsNullOrEmpty(mesh.name) ? "Unnamed Mesh" : mesh.name, assetPath, mesh));
                }
            }
        }

        private static MeshAnalysis AnalyzeMesh(Mesh mesh, string meshName, string sourcePath, UnityEngine.Object pingTarget)
        {
            string[] missing = PawlygonEditorUtils.GetMissingRequiredUnifiedBlendshapes(mesh);

            // Names that only differ in upper/lower case: animations bind by exact name, so they don't work.
            var namesIgnoringCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (!string.IsNullOrWhiteSpace(name) && !namesIgnoringCase.ContainsKey(name))
                {
                    namesIgnoringCase.Add(name, name);
                }
            }

            var caseMismatches = new Dictionary<string, string>();
            foreach (string required in missing)
            {
                if (namesIgnoringCase.TryGetValue(required, out string actual))
                {
                    caseMismatches[required] = actual;
                }
            }

            return new MeshAnalysis
            {
                Key = sourcePath + "|" + meshName,
                MeshName = meshName,
                SourcePath = sourcePath,
                TotalBlendshapeCount = mesh.blendShapeCount,
                MissingBlendshapes = missing,
                CaseMismatches = caseMismatches,
                FoundCount = Required.Length - missing.Length,
                PingTarget = pingTarget
            };
        }

        // =====================================================================
        // Summary
        // =====================================================================

        private void DrawSummary()
        {
            int complete = results.Count(r => r.IsComplete);
            int incomplete = results.Count(r => r.HasAnyUnifiedBlendshapes && !r.IsComplete);
            int without = results.Count - complete - incomplete;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader("Summary",
                    $"Face tracking moves the face through {Required.Length} Unified Expressions blendshapes, found by name. " +
                    "Names must match exactly, including upper and lower case.");

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label($"{results.Count} mesh{(results.Count != 1 ? "es" : "")} checked ({sourceKind})", GUILayout.ExpandWidth(false));
                    GUILayout.Space(6f);
                    if (complete > 0) PawlygonEditorUI.DrawBadge($"{complete} complete", PawlygonEditorUI.BadgeKind.Ok, "Every Unified Expressions blendshape is there.");
                    if (incomplete > 0) PawlygonEditorUI.DrawBadge($"{incomplete} incomplete", PawlygonEditorUI.BadgeKind.Warning, "Has some Unified Expressions blendshapes, but not all.");
                    if (without > 0) PawlygonEditorUI.DrawBadge($"{without} without UE shapes", PawlygonEditorUI.BadgeKind.Neutral, "Has blendshapes, but none of the Unified Expressions ones (e.g. a body or clothing mesh).");
                    GUILayout.FlexibleSpace();
                }

                EditorGUILayout.Space(4f);
                string verdict;
                if (incomplete > 0)
                {
                    verdict = "Missing blendshapes won't move with face tracking. Add or rename them in your 3D software, then re-import the model; this window updates on its own.";
                }
                else if (complete > 0)
                {
                    string names = string.Join(", ", results.Where(r => r.IsComplete).Select(r => $"'{r.MeshName}'"));
                    verdict = $"Ready for face tracking: {names} {(complete == 1 ? "has" : "have")} every Unified Expressions blendshape.";
                }
                else
                {
                    verdict = "None of these meshes has Unified Expressions blendshapes. Face tracking needs them on the face mesh; the ? button above opens the guide.";
                }
                EditorGUILayout.LabelField(verdict, PawlygonEditorUI.SubLabelStyle);
            }
        }

        // =====================================================================
        // Mesh list
        // =====================================================================

        private void DrawMeshList()
        {
            foreach (MeshAnalysis mesh in results.Where(r => r.HasAnyUnifiedBlendshapes))
            {
                DrawMeshResult(mesh);
            }

            int withoutCount = results.Count(r => !r.HasAnyUnifiedBlendshapes);
            if (withoutCount == 0) return;

            // Draw with this event's value so the layout matches; the new value shows on the next repaint.
            bool show = showMeshesWithout;
            EditorGUILayout.Space(2f);
            showMeshesWithout = EditorGUILayout.ToggleLeft(
                $"Show {withoutCount} mesh{(withoutCount != 1 ? "es" : "")} without Unified Expressions blendshapes", show);

            if (!show) return;

            foreach (MeshAnalysis mesh in results.Where(r => !r.HasAnyUnifiedBlendshapes))
            {
                DrawMeshResult(mesh);
            }
        }

        private void DrawMeshResult(MeshAnalysis mesh)
        {
            bool expandable = mesh.HasAnyUnifiedBlendshapes && !mesh.IsComplete;
            bool expanded = expandable && (foldouts.TryGetValue(mesh.Key, out bool stored) ? stored : true);

            using (new EditorGUILayout.VerticalScope(meshBoxStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var nameContent = new GUIContent(mesh.MeshName, $"{mesh.SourcePath}\n{mesh.TotalBlendshapeCount} blendshapes in total");
                    if (expandable)
                    {
                        Rect rect = GUILayoutUtility.GetRect(nameContent, boldFoldoutStyle, GUILayout.ExpandWidth(false));
                        bool newExpanded = EditorGUI.Foldout(rect, expanded, nameContent, true, boldFoldoutStyle);
                        if (newExpanded != expanded) foldouts[mesh.Key] = newExpanded;
                    }
                    else
                    {
                        GUILayout.Label(nameContent, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                    }

                    GUILayout.Label(new GUIContent(mesh.SourcePath, mesh.SourcePath), mutedMiniStyle, GUILayout.MinWidth(20f));
                    GUILayout.FlexibleSpace();

                    if (mesh.HasAnyUnifiedBlendshapes)
                    {
                        GUILayout.Label($"{mesh.FoundCount}/{Required.Length}", mutedMiniStyle, GUILayout.ExpandWidth(false));
                    }
                    DrawMeshBadge(mesh);
                }

                if (expanded)
                {
                    EditorGUILayout.Space(4f);
                    DrawCaseMismatches(mesh);
                    DrawMissingByRegion(mesh);
                    DrawMeshActions(mesh);
                }
            }

            EditorGUILayout.Space(2f);
        }

        private static void DrawMeshBadge(MeshAnalysis mesh)
        {
            if (mesh.IsComplete)
            {
                PawlygonEditorUI.DrawBadge("Complete", PawlygonEditorUI.BadgeKind.Ok, "Every Unified Expressions blendshape is there.");
            }
            else if (mesh.HasAnyUnifiedBlendshapes)
            {
                PawlygonEditorUI.DrawBadge($"{mesh.MissingBlendshapes.Length} missing", PawlygonEditorUI.BadgeKind.Warning,
                    "These blendshapes won't move with face tracking until they are added or renamed.");
            }
            else
            {
                PawlygonEditorUI.DrawBadge("No UE shapes", PawlygonEditorUI.BadgeKind.Neutral,
                    $"{mesh.TotalBlendshapeCount} blendshapes, none of them Unified Expressions. Fine for body or clothing meshes.");
            }
        }

        private void DrawCaseMismatches(MeshAnalysis mesh)
        {
            if (mesh.CaseMismatches.Count == 0) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                PawlygonEditorUI.DrawBadge("Wrong case", PawlygonEditorUI.BadgeKind.Warning,
                    "Face tracking finds blendshapes by their exact name, so 'jawopen' is not 'JawOpen'.");
                GUILayout.Label($"{mesh.CaseMismatches.Count} name{(mesh.CaseMismatches.Count != 1 ? "s" : "")} only differ in upper/lower case",
                    mutedMiniStyle, GUILayout.MinWidth(20f));
            }

            foreach (KeyValuePair<string, string> mismatch in mesh.CaseMismatches)
            {
                EditorGUILayout.LabelField(
                    $"• found '{mismatch.Value}': names are case-sensitive, rename it to '<b>{mismatch.Key}</b>'", wrappedMiniStyle);
            }

            EditorGUILayout.Space(4f);
        }

        private void DrawMissingByRegion(MeshAnalysis mesh)
        {
            foreach (string region in Regions.Append("Other"))
            {
                string[] missing = mesh.MissingBlendshapes.Where(n => GetRegion(n) == region).ToArray();
                if (missing.Length == 0) continue;

                string[] absent = missing.Where(n => !mesh.CaseMismatches.ContainsKey(n)).ToArray();
                int wrongCase = missing.Length - absent.Length;
                RequiredPerRegion.TryGetValue(region, out int regionTotal);

                string countText = $"{missing.Length} of {regionTotal} missing" + (wrongCase > 0 ? $" ({wrongCase} wrong case)" : "");
                EditorGUILayout.LabelField($"<b>{region}</b>  {countText}", wrappedMiniStyle);

                if (absent.Length > 0)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.LabelField(string.Join(", ", absent), wrappedMiniStyle);
                    }
                }
            }
        }

        private void DrawMeshActions(MeshAnalysis mesh)
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (mesh.PingTarget != null && GUILayout.Button(new GUIContent("Ping", "Show this mesh in the Hierarchy or Project window."),
                        EditorStyles.miniButtonLeft, GUILayout.Width(50f)))
                {
                    PawlygonStatus.Ping(mesh.PingTarget)();
                }

                if (GUILayout.Button(new GUIContent("Copy Missing Names", "Copies the missing blendshape names (one per line, with the correct spelling)."),
                        mesh.PingTarget != null ? EditorStyles.miniButtonRight : EditorStyles.miniButton, GUILayout.Width(130f)))
                {
                    EditorGUIUtility.systemCopyBuffer = string.Join("\n", mesh.MissingBlendshapes);
                    status.Info($"Copied {mesh.MissingBlendshapes.Length} missing blendshape name{(mesh.MissingBlendshapes.Length != 1 ? "s" : "")} from '{mesh.MeshName}' to the clipboard.");

                    // The status bar appears below; restart so the layout includes it.
                    GUIUtility.ExitGUI();
                }
            }
        }

        // =====================================================================
        // Utility
        // =====================================================================

        /// <summary>The face region of a required name, from its prefix ("JawOpen" is Jaw), or "Other".</summary>
        private static string GetRegion(string blendshapeName)
        {
            foreach (string region in Regions)
            {
                if (blendshapeName.StartsWith(region, StringComparison.Ordinal)) return region;
            }

            return "Other";
        }

        private static string GetRelativeHierarchyPath(Transform root, Transform target)
        {
            if (root == target)
            {
                return target.name;
            }

            var parts = new List<string>();
            Transform current = target;

            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
