using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Standalone editor window that checks meshes for the 60 required Unified Expression blendshapes.
    /// Supports two input modes: selecting a scene GameObject (scans all SkinnedMeshRenderers)
    /// or dragging in an FBX/Model asset (loads mesh sub-assets).
    /// </summary>
    public class UnifiedExpressionChecker : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Face Tracking Blendshapes";
        private const float SectionSpacing = 10f;

        private static readonly string[] RequiredUnifiedExpressionBlendshapes =
        {
            "BrowDownLeft",
            "BrowDownRight",
            "BrowInnerUpLeft",
            "BrowInnerUpRight",
            "BrowOuterUpLeft",
            "BrowOuterUpRight",
            "EyeClosedLeft",
            "EyeClosedRight",
            "EyeConstrict",
            "EyeDilation",
            "EyeLookDownLeft",
            "EyeLookDownRight",
            "EyeLookInLeft",
            "EyeLookInRight",
            "EyeLookOutLeft",
            "EyeLookOutRight",
            "EyeLookUpLeft",
            "EyeLookUpRight",
            "EyeSquintLeft",
            "EyeSquintRight",
            "EyeWideLeft",
            "EyeWideRight",
            "CheekPuffLeft",
            "CheekPuffRight",
            "CheekSquintLeft",
            "CheekSquintRight",
            "CheekSuckLeft",
            "CheekSuckRight",
            "LipFunnel",
            "LipPucker",
            "LipSuckLower",
            "LipSuckUpper",
            "JawForward",
            "JawLeft",
            "JawOpen",
            "JawRight",
            "MouthClosed",
            "MouthFrownLeft",
            "MouthFrownRight",
            "MouthLeft",
            "MouthLowerDown",
            "MouthPress",
            "MouthRaiserLower",
            "MouthRaiserUpper",
            "MouthRight",
            "MouthSmileLeft",
            "MouthSmileRight",
            "MouthStretchLeft",
            "MouthStretchRight",
            "MouthTightenerLeft",
            "MouthTightenerRight",
            "MouthUpperUp",
            "MouthUpperUpLeft",
            "MouthUpperUpRight",
            "NoseSneer",
            "NoseSneerLeft",
            "NoseSneerRight",
            "TongueDown",
            "TongueLeft",
            "TongueOut",
            "TongueRight",
            "TongueUp"
        };

        // --- State ---
        [SerializeField] private Vector2 scrollPosition;
        private GameObject selectedInput;
        private List<MeshAnalysis> results;
        private bool showAllMeshes;
        private string statusMessage;
        private MessageType statusMessageType;

        // =====================================================================
        // Data model
        // =====================================================================

        private class MeshAnalysis
        {
            public string MeshName;
            public string SourcePath;
            public int TotalBlendshapeCount;
            public string[] MissingBlendshapes;
            public bool HasAnyUnifiedBlendshapes;
            public bool ShowDetails;
        }

        // =====================================================================
        // Window lifecycle
        // =====================================================================

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            UnifiedExpressionChecker window = GetWindow<UnifiedExpressionChecker>();
            window.titleContent = new GUIContent("Face Tracking Blendshapes");
            window.minSize = new Vector2(520f, 400f);
        }

        private void OnEnable()
        {
            AutoSelectFirstSceneRoot();
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();

            PawlygonEditorUI.DrawHeader(
                "Face Tracking Blendshapes",
                "Check meshes for the required Unified Expression blendshapes used by VRC face tracking.");
            EditorGUILayout.Space(SectionSpacing);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            DrawInputSection();
            EditorGUILayout.Space(SectionSpacing);

            if (results != null && results.Count > 0)
            {
                DrawResults();
            }
            else if (results != null && results.Count == 0)
            {
                EditorGUILayout.HelpBox("No meshes with blendshapes were found.", MessageType.Info);
            }

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(statusMessage, statusMessageType);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(8f);
            PawlygonEditorUI.DrawFooter();
        }

        // =====================================================================
        // Input section
        // =====================================================================

        private void DrawInputSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Input", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
                EditorGUILayout.Space(4f);

                selectedInput = (GameObject)EditorGUILayout.ObjectField("GameObject / Model", selectedInput, typeof(GameObject), true);

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("Select a GameObject from the scene or drag an FBX/Model asset from the Project.", PawlygonEditorUI.SubLabelStyle);

                EditorGUILayout.Space(8f);

                using (new EditorGUI.DisabledScope(selectedInput == null))
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Check Blendshapes", 32f))
                    {
                        AnalyzeBlendshapes();
                    }
                }
            }
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        private void AnalyzeBlendshapes()
        {
            results = new List<MeshAnalysis>();
            statusMessage = null;
            showAllMeshes = false;

            if (selectedInput == null)
            {
                return;
            }

            bool isAsset = EditorUtility.IsPersistent(selectedInput);

            if (isAsset)
            {
                AnalyzeModelAsset(selectedInput);
            }
            else
            {
                AnalyzeSceneObject(selectedInput);
            }

            // Build status message
            if (results.Count == 0)
            {
                statusMessage = "No meshes with blendshapes were found.";
                statusMessageType = MessageType.Warning;
                return;
            }

            int relevantCount = results.Count(r => r.HasAnyUnifiedBlendshapes);
            int completeCount = results.Count(r => r.MissingBlendshapes.Length == 0 && r.HasAnyUnifiedBlendshapes);
            int incompleteCount = relevantCount - completeCount;
            int otherCount = results.Count - relevantCount;

            string source = isAsset ? "model asset" : "scene GameObject";
            statusMessage = $"Checked {results.Count} mesh{(results.Count == 1 ? "" : "es")} from {source}. " +
                $"{relevantCount} with Unified Expression blendshapes ({completeCount} complete, {incompleteCount} incomplete)" +
                (otherCount > 0 ? $", {otherCount} without." : ".");
            statusMessageType = incompleteCount > 0 ? MessageType.Warning : MessageType.Info;
        }

        private void AnalyzeSceneObject(GameObject sceneObject)
        {
            SkinnedMeshRenderer[] renderers = sceneObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                Mesh mesh = renderer.sharedMesh;

                if (mesh == null || mesh.blendShapeCount == 0)
                {
                    continue;
                }

                string hierarchyPath = GetRelativeHierarchyPath(sceneObject.transform, renderer.transform);
                string[] missing = GetMissingRequiredUnifiedBlendshapes(mesh);
                bool hasAny = missing.Length < RequiredUnifiedExpressionBlendshapes.Length;

                results.Add(new MeshAnalysis
                {
                    MeshName = string.IsNullOrEmpty(mesh.name) ? renderer.name : mesh.name,
                    SourcePath = hierarchyPath,
                    TotalBlendshapeCount = mesh.blendShapeCount,
                    MissingBlendshapes = missing,
                    HasAnyUnifiedBlendshapes = hasAny,
                    ShowDetails = false
                });
            }
        }

        private void AnalyzeModelAsset(GameObject modelAsset)
        {
            string assetPath = AssetDatabase.GetAssetPath(modelAsset);

            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);

            if (allSubAssets == null)
            {
                return;
            }

            foreach (UnityEngine.Object subAsset in allSubAssets)
            {
                if (subAsset is Mesh mesh && mesh.blendShapeCount > 0)
                {
                    string[] missing = GetMissingRequiredUnifiedBlendshapes(mesh);
                    bool hasAny = missing.Length < RequiredUnifiedExpressionBlendshapes.Length;

                    results.Add(new MeshAnalysis
                    {
                        MeshName = string.IsNullOrEmpty(mesh.name) ? "Unnamed Mesh" : mesh.name,
                        SourcePath = assetPath,
                        TotalBlendshapeCount = mesh.blendShapeCount,
                        MissingBlendshapes = missing,
                        HasAnyUnifiedBlendshapes = hasAny,
                        ShowDetails = false
                    });
                }
            }
        }

        // =====================================================================
        // Results display
        // =====================================================================

        private void DrawResults()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Results", new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 });
                EditorGUILayout.Space(4f);

                int relevantCount = results.Count(r => r.HasAnyUnifiedBlendshapes);
                int completeCount = results.Count(r => r.MissingBlendshapes.Length == 0 && r.HasAnyUnifiedBlendshapes);
                int incompleteCount = relevantCount - completeCount;
                int otherCount = results.Count - relevantCount;

                EditorGUILayout.LabelField(
                    $"{relevantCount} mesh{(relevantCount == 1 ? "" : "es")} with Unified Expression blendshapes \u2014 " +
                    $"{completeCount} complete, {incompleteCount} incomplete",
                    PawlygonEditorUI.SubLabelStyle);

                if (otherCount > 0)
                {
                    showAllMeshes = EditorGUILayout.ToggleLeft(
                        $"Show all meshes ({otherCount} without Unified Expression blendshapes)",
                        showAllMeshes);
                }

                EditorGUILayout.Space(8f);

                foreach (MeshAnalysis meshResult in results)
                {
                    if (!meshResult.HasAnyUnifiedBlendshapes && !showAllMeshes)
                    {
                        continue;
                    }

                    DrawMeshResult(meshResult);
                }
            }
        }

        private void DrawMeshResult(MeshAnalysis meshResult)
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6) }))
            {
                EditorGUILayout.LabelField(meshResult.MeshName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(meshResult.SourcePath, PawlygonEditorUI.RichMiniLabelStyle);
                EditorGUILayout.LabelField($"{meshResult.TotalBlendshapeCount} total blendshapes", PawlygonEditorUI.RichMiniLabelStyle);

                EditorGUILayout.Space(4f);

                if (meshResult.MissingBlendshapes.Length == 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("TestPassed"), GUILayout.Width(18f), GUILayout.Height(16f));
                        EditorGUILayout.LabelField("All Unified Expression Blendshapes found", PawlygonEditorUI.RichMiniLabelStyle);
                    }
                }
                else
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(16f));
                        EditorGUILayout.LabelField(
                            $"Missing {meshResult.MissingBlendshapes.Length} of {RequiredUnifiedExpressionBlendshapes.Length} Unified Expression Blendshapes",
                            PawlygonEditorUI.RichMiniLabelStyle);
                    }

                    meshResult.ShowDetails = EditorGUILayout.Foldout(
                        meshResult.ShowDetails,
                        $"Show missing blendshapes ({meshResult.MissingBlendshapes.Length})",
                        true);

                    if (meshResult.ShowDetails)
                    {
                        using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 6, 6) }))
                        {
                            foreach (string blendshapeName in meshResult.MissingBlendshapes)
                            {
                                EditorGUILayout.LabelField($"- {blendshapeName}", PawlygonEditorUI.RichMiniLabelStyle);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space(2f);
        }

        // =====================================================================
        // Utility
        // =====================================================================

        private static string[] GetMissingRequiredUnifiedBlendshapes(Mesh mesh)
        {
            if (mesh == null)
            {
                return RequiredUnifiedExpressionBlendshapes.ToArray();
            }

            var availableBlendshapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string blendshapeName = mesh.GetBlendShapeName(i);

                if (!string.IsNullOrWhiteSpace(blendshapeName))
                {
                    availableBlendshapes.Add(blendshapeName);
                }
            }

            return RequiredUnifiedExpressionBlendshapes
                .Where(required => !availableBlendshapes.Contains(required))
                .ToArray();
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

        private void AutoSelectFirstSceneRoot()
        {
            if (selectedInput != null)
            {
                return;
            }

            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            if (!activeScene.IsValid() || !activeScene.isLoaded)
            {
                return;
            }

            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            if (rootObjects == null || rootObjects.Length == 0)
            {
                return;
            }

            // Prefer a root object with a SkinnedMeshRenderer (has blendshapes to check)
            foreach (GameObject root in rootObjects)
            {
                if (root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                {
                    selectedInput = root;
                    return;
                }
            }

            // Fall back to first root with a VRCAvatarDescriptor
            Type descriptorType = FXGestureCheckerCore.FindVRCAvatarDescriptorType();

            if (descriptorType != null)
            {
                foreach (GameObject root in rootObjects)
                {
                    if (root.GetComponentInChildren(descriptorType, true) != null)
                    {
                        selectedInput = root;
                        return;
                    }
                }
            }
        }
    }
}
