using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    public class AvatarSetupWizard : EditorWindow
    {
        private const string BaseFolderName = "!Pawlygon";
        private const string DefaultAvatarName = "Avatar Name";
        private const int MaxImportLoadAttempts = 10;
        private const float SectionSpacing = 10f;
        private const float StepBadgeHeight = 28f;

        [SerializeField] private GameObject sourceFbx;
        [SerializeField] private GameObject sourcePrefab;
        [SerializeField] private string avatarName = DefaultAvatarName;

        [SerializeField] private WizardStep currentStep = WizardStep.Setup;
        [SerializeField] private string copiedFbxPath;
        [SerializeField] private string copiedPrefabPath;
        [SerializeField] private string createdScenePath;
        [SerializeField] private string avatarRootPath;
        [SerializeField] private long watchedFbxWriteTimeUtcTicks;
        [SerializeField] private List<MeshSelectionState> meshSelections = new List<MeshSelectionState>();

        private Vector2 meshScrollPosition;
        private string statusMessage = string.Empty;
        private bool pendingImportTransition;
        private int importLoadAttempts;
        private GUIStyle sectionStyle;
        private GUIStyle sectionHeaderStyle;
        private GUIStyle titleStyle;
        private GUIStyle stepStyle;
        private GUIStyle currentStepStyle;
        private GUIStyle subLabelStyle;
        private GUIStyle richMiniLabelStyle;

        private enum WizardStep
        {
            Setup,
            WaitForImport,
            SelectMeshes,
            Complete
        }

        [Serializable]
        private class MeshSelectionState
        {
            public string fbxObjectName;
            public string fbxRelativePath;
            public string fbxMeshName;
            public string prefabObjectName;
            public string prefabRelativePath;
            public string prefabMeshName;
            public string matchReason;
            public bool hasMatch;
            public bool selected;
        }

        [MenuItem("!Pawlygon/Avatar Setup Wizard")]
        public static void ShowWindow()
        {
            AvatarSetupWizard window = GetWindow<AvatarSetupWizard>();
            window.titleContent = new GUIContent("Avatar Setup Wizard");
            window.minSize = new Vector2(520f, 480f);
        }

        private void OnEnable()
        {
            FBXImportDetector.FbxReimported += HandleFbxReimported;
        }

        private void OnDisable()
        {
            FBXImportDetector.FbxReimported -= HandleFbxReimported;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
        }

        private void OnGUI()
        {
            EnsureStyles();

            DrawHeader();
            DrawStepIndicator();
            EditorGUILayout.Space(SectionSpacing);

            switch (currentStep)
            {
                case WizardStep.Setup:
                    DrawSetupStep();
                    break;
                case WizardStep.WaitForImport:
                    DrawWaitForImportStep();
                    break;
                case WizardStep.SelectMeshes:
                    DrawMeshSelectionStep();
                    break;
                case WizardStep.Complete:
                    DrawCompleteStep();
                    break;
            }

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
            }
        }

        private void DrawSetupStep()
        {
            DrawSection(
                "Setup",
                "Duplicate the source FBX and prefab, then create a clean working scene under Internal/Scenes.",
                () =>
                {
                    sourceFbx = (GameObject)EditorGUILayout.ObjectField("Source FBX", sourceFbx, typeof(GameObject), false);
                    sourcePrefab = (GameObject)EditorGUILayout.ObjectField("Source Prefab", sourcePrefab, typeof(GameObject), false);
                    avatarName = EditorGUILayout.TextField("Avatar Folder Name", avatarName);

                    string validationMessage = GetSetupValidationMessage();

                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                    using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(validationMessage)))
                    {
                        if (DrawPrimaryButton("Create Avatar Structure", 36f))
                        {
                            CreateAvatarStructure();
                        }
                    }

                    if (!string.IsNullOrEmpty(validationMessage))
                    {
                        EditorGUILayout.HelpBox(validationMessage, MessageType.Warning);
                    }
                });
        }

        private void DrawWaitForImportStep()
        {
            DrawSection(
                "Import Modified FBX",
                "Replace the copied FBX on disk with your edited version. Unity will detect the reimport and advance automatically when the asset is ready.",
                () =>
                {
                    EditorGUILayout.HelpBox("If Unity already finished importing, use the button below to continue manually.", MessageType.Info);
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    DrawPathSummary();

                    EditorGUILayout.Space(SectionSpacing);

                    if (DrawPrimaryButton("Continue After Import", 34f))
                    {
                        MoveToMeshSelection();
                    }
                });
        }

        private void DrawMeshSelectionStep()
        {
            DrawSection(
                "Select Meshes",
                "Review the renderer matches found between the modified FBX and the duplicated prefab, then choose which meshes to replace.",
                () =>
                {
                    DrawPathSummary();
                    EditorGUILayout.Space(SectionSpacing);

                    if (meshSelections.Count == 0)
                    {
                        EditorGUILayout.HelpBox("No skinned mesh renderer mappings were found between the duplicated FBX and prefab.", MessageType.Warning);
                    }
                    else
                    {
                        DrawMeshSelectionToolbar();
                        EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);

                        using (new EditorGUILayout.VerticalScope(sectionStyle))
                        {
                            meshScrollPosition = EditorGUILayout.BeginScrollView(meshScrollPosition, GUILayout.Height(260f));

                            foreach (MeshSelectionState meshSelection in meshSelections)
                            {
                                DrawMeshSelectionRow(meshSelection);
                            }

                            EditorGUILayout.EndScrollView();
                        }
                    }

                    EditorGUILayout.Space(SectionSpacing);

                    using (new EditorGUI.DisabledScope(meshSelections.All(selection => !selection.selected)))
                    {
                        if (DrawPrimaryButton("Replace Selected Meshes", 36f))
                        {
                            ApplySelectedMeshesToPrefab();
                        }
                    }
                });
        }

        private void DrawCompleteStep()
        {
            DrawSection(
                "Complete",
                "The duplicated prefab now points to the selected meshes from the modified FBX.",
                () =>
                {
                    GUIContent successIcon = EditorGUIUtility.IconContent("TestPassed");
                    EditorGUILayout.LabelField(new GUIContent(" Avatar setup completed successfully", successIcon.image), new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
                    EditorGUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                    DrawPathSummary(includeAvatarRoot: true);

                    EditorGUILayout.Space(SectionSpacing);

                    if (DrawPrimaryButton("Start Over", 34f))
                    {
                        ResetWizard();
                    }
                });
        }

        private void CreateAvatarStructure()
        {
            statusMessage = string.Empty;

            if (!ValidateSetupInputs(out string validationMessage))
            {
                statusMessage = validationMessage;
                return;
            }

            string sourceFbxPath = AssetDatabase.GetAssetPath(sourceFbx);
            string sourcePrefabPath = AssetDatabase.GetAssetPath(sourcePrefab);
            string sanitizedAvatarName = avatarName.Trim();

            avatarRootPath = CombineAssetPath("Assets", BaseFolderName, sanitizedAvatarName);
            string fbxFolderPath = CombineAssetPath(avatarRootPath, "FBX");
            string prefabFolderPath = CombineAssetPath(avatarRootPath, "Prefabs");
            string internalFolderPath = CombineAssetPath(avatarRootPath, "Internal");
            string scenesFolderPath = CombineAssetPath(internalFolderPath, "Scenes");

            string copiedFbxFileName = $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} FT{Path.GetExtension(sourceFbxPath)}";
            string copiedPrefabFileName = Path.GetFileName(sourcePrefabPath);
            string sceneFileName = $"{sanitizedAvatarName} - Pawlygon VRCFT.unity";

            copiedFbxPath = CombineAssetPath(fbxFolderPath, copiedFbxFileName);
            copiedPrefabPath = CombineAssetPath(prefabFolderPath, copiedPrefabFileName);
            createdScenePath = CombineAssetPath(scenesFolderPath, sceneFileName);

            if (AssetDatabase.IsValidFolder(avatarRootPath) || File.Exists(ToAbsolutePath(copiedFbxPath)) || File.Exists(ToAbsolutePath(copiedPrefabPath)))
            {
                EditorUtility.DisplayDialog(
                    "Avatar Already Exists",
                    $"The folder '{avatarRootPath}' already exists. Choose a new avatar name or remove the existing folder first.",
                    "OK");
                return;
            }

            EnsureFolderExists(CombineAssetPath("Assets", BaseFolderName));
            EnsureFolderExists(avatarRootPath);
            EnsureFolderExists(fbxFolderPath);
            EnsureFolderExists(prefabFolderPath);
            EnsureFolderExists(internalFolderPath);
            EnsureFolderExists(scenesFolderPath);

            if (!AssetDatabase.CopyAsset(sourceFbxPath, copiedFbxPath))
            {
                EditorUtility.DisplayDialog("Copy Failed", "The source FBX could not be copied.", "OK");
                return;
            }

            if (!AssetDatabase.CopyAsset(sourcePrefabPath, copiedPrefabPath))
            {
                EditorUtility.DisplayDialog("Copy Failed", "The source prefab could not be copied.", "OK");
                return;
            }

            if (!CreateWorkingScene(createdScenePath))
            {
                EditorUtility.DisplayDialog("Scene Creation Failed", "The working scene could not be created.", "OK");
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            watchedFbxWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(copiedFbxPath);
            currentStep = WizardStep.WaitForImport;
            statusMessage = "Avatar structure created. Replace the copied FBX with your modified file and let Unity import it.";
            Repaint();
        }

        private bool ValidateSetupInputs(out string validationMessage)
        {
            validationMessage = GetSetupValidationMessage();
            return string.IsNullOrEmpty(validationMessage);
        }

        private string GetSetupValidationMessage()
        {
            if (sourceFbx == null)
            {
                return "Select an FBX asset to duplicate.";
            }

            if (sourcePrefab == null)
            {
                return "Select a prefab asset to duplicate.";
            }

            if (string.IsNullOrWhiteSpace(avatarName))
            {
                return "Enter an avatar folder name.";
            }

            if (avatarName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "The avatar folder name contains invalid characters.";
            }

            string fbxPath = AssetDatabase.GetAssetPath(sourceFbx);
            if (!string.Equals(Path.GetExtension(fbxPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return "The selected source FBX must point to an .fbx asset.";
            }

            string prefabPath = AssetDatabase.GetAssetPath(sourcePrefab);
            if (!string.Equals(Path.GetExtension(prefabPath), ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return "The selected source prefab must point to a .prefab asset.";
            }

            return string.Empty;
        }

        private void HandleFbxReimported(string importedAssetPath)
        {
            if (currentStep != WizardStep.WaitForImport || string.IsNullOrEmpty(copiedFbxPath))
            {
                return;
            }

            if (!string.Equals(NormalizeAssetPath(importedAssetPath), NormalizeAssetPath(copiedFbxPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long currentWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(copiedFbxPath);
            if (currentWriteTimeUtcTicks <= watchedFbxWriteTimeUtcTicks)
            {
                return;
            }

            watchedFbxWriteTimeUtcTicks = currentWriteTimeUtcTicks;
            QueueMoveToMeshSelection();
        }

        private void QueueMoveToMeshSelection()
        {
            if (pendingImportTransition)
            {
                return;
            }

            pendingImportTransition = true;
            importLoadAttempts = 0;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            EditorApplication.delayCall += TryMoveToMeshSelectionAfterImport;
        }

        private void TryMoveToMeshSelectionAfterImport()
        {
            pendingImportTransition = false;
            importLoadAttempts++;

            if (CanLoadImportedAssets())
            {
                MoveToMeshSelection();
                return;
            }

            if (importLoadAttempts >= MaxImportLoadAttempts)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Imported FBX is still unavailable after {importLoadAttempts} delayed attempt(s): '{copiedFbxPath}'.");
                statusMessage = "The modified FBX import was detected, but Unity has not finished making it loadable yet. Wait a moment, then use the skip button if needed.";
                Repaint();
                return;
            }

            Debug.Log($"[AvatarSetupWizard] FBX not ready yet, retrying delayed load attempt {importLoadAttempts + 1}/{MaxImportLoadAttempts} for '{copiedFbxPath}'.");
            pendingImportTransition = true;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            EditorApplication.delayCall += TryMoveToMeshSelectionAfterImport;
        }

        private bool CanLoadImportedAssets()
        {
            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(copiedFbxPath);
            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(copiedPrefabPath);
            return fbxRoot != null && prefabRoot != null;
        }

        private void MoveToMeshSelection()
        {
            LoadMeshSelections();
            currentStep = WizardStep.SelectMeshes;
            statusMessage = meshSelections.Count > 0
                ? "Modified FBX imported. Select the mapped skinned mesh renderers that should replace meshes on the copied prefab."
                : "Modified FBX imported, but no skinned mesh renderer mappings were found.";
            Repaint();
        }

        private void LoadMeshSelections()
        {
            meshSelections = new List<MeshSelectionState>();

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(copiedFbxPath);
            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(copiedPrefabPath);

            if (fbxRoot == null)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not load FBX asset at '{copiedFbxPath}'.");
                return;
            }

            if (prefabRoot == null)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not load prefab asset at '{copiedPrefabPath}'.");
                return;
            }

            // Collect all Mesh sub-assets from the FBX file.
            // In Unity 2022.3, sharedMesh on SkinnedMeshRenderers loaded via LoadAssetAtPath<GameObject>
            // can be null, so we load the meshes directly as sub-assets and match by name.
            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(copiedFbxPath);
            Debug.Log($"[AvatarSetupWizard] Found {fbxMeshSubAssets.Count} mesh sub-asset(s) in FBX '{copiedFbxPath}': [{string.Join(", ", fbxMeshSubAssets.Keys)}]");

            // Collect SkinnedMeshRenderers from the FBX hierarchy (without filtering by sharedMesh).
            List<RendererInfo> fbxRenderers = GetRendererInfos(fbxRoot, fbxMeshSubAssets);
            Debug.Log($"[AvatarSetupWizard] Found {fbxRenderers.Count} SkinnedMeshRenderer(s) in FBX hierarchy.");

            // Collect SkinnedMeshRenderers from the prefab hierarchy.
            List<RendererInfo> prefabRenderers = GetRendererInfos(prefabRoot, meshSubAssets: null);
            Debug.Log($"[AvatarSetupWizard] Found {prefabRenderers.Count} SkinnedMeshRenderer(s) in prefab hierarchy.");

            HashSet<string> usedPrefabPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            meshSelections = fbxRenderers
                .Select(fbxRenderer => CreateMeshSelectionState(fbxRenderer, prefabRenderers, usedPrefabPaths))
                .OrderBy(selection => selection.fbxRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Debug.Log($"[AvatarSetupWizard] Created {meshSelections.Count} mesh selection(s). " +
                      $"Matched: {meshSelections.Count(s => s.hasMatch)}, Unmatched: {meshSelections.Count(s => !s.hasMatch)}.");
        }

        private void ApplySelectedMeshesToPrefab()
        {
            List<MeshSelectionState> selectedMappings = meshSelections
                .Where(selection => selection.selected && selection.hasMatch)
                .ToList();

            if (selectedMappings.Count == 0)
            {
                EditorUtility.DisplayDialog("No Meshes Selected", "Select at least one mapped skinned mesh renderer to replace on the prefab.", "OK");
                return;
            }

            // Load mesh sub-assets from the FBX so we can reliably find each Mesh by name.
            Dictionary<string, Mesh> fbxMeshSubAssets = LoadMeshSubAssets(copiedFbxPath);
            if (fbxMeshSubAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("FBX Missing Meshes", "No mesh sub-assets could be loaded from the duplicated FBX.", "OK");
                return;
            }

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(copiedPrefabPath);

            try
            {
                // Build a lookup of prefab SkinnedMeshRenderers keyed by relative path (excluding root).
                Dictionary<string, SkinnedMeshRenderer> prefabRendererLookup = prefabRoot
                    .GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .ToDictionary(
                        renderer => GetRelativeTransformPath(renderer.transform),
                        renderer => renderer,
                        StringComparer.OrdinalIgnoreCase);

                int replacedCount = 0;

                foreach (MeshSelectionState mapping in selectedMappings)
                {
                    if (string.IsNullOrEmpty(mapping.fbxMeshName))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] Skipping '{mapping.fbxObjectName}': no FBX mesh name recorded.");
                        continue;
                    }

                    if (!fbxMeshSubAssets.TryGetValue(mapping.fbxMeshName, out Mesh fbxMesh))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] Mesh sub-asset '{mapping.fbxMeshName}' not found in FBX '{copiedFbxPath}'.");
                        continue;
                    }

                    if (!prefabRendererLookup.TryGetValue(mapping.prefabRelativePath, out SkinnedMeshRenderer prefabRenderer))
                    {
                        Debug.LogWarning($"[AvatarSetupWizard] No SkinnedMeshRenderer at relative path '{mapping.prefabRelativePath}' in prefab.");
                        continue;
                    }

                    prefabRenderer.sharedMesh = fbxMesh;
                    replacedCount++;
                    Debug.Log($"[AvatarSetupWizard] Replaced mesh on '{mapping.prefabRelativePath}' with '{mapping.fbxMeshName}'.");
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, copiedPrefabPath);

                currentStep = WizardStep.Complete;
                statusMessage = replacedCount > 0
                    ? $"Updated {replacedCount} mesh reference(s) on the copied prefab."
                    : "No mapped skinned mesh renderers were updated on the copied prefab.";
                EditorUtility.DisplayDialog("Avatar Setup Complete", statusMessage, "OK");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static bool CreateWorkingScene(string sceneAssetPath)
        {
            var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            try
            {
                return EditorSceneManager.SaveScene(newScene, sceneAssetPath);
            }
            finally
            {
                EditorSceneManager.CloseScene(newScene, true);
            }
        }

        private static void EnsureFolderExists(string folderPath)
        {
            folderPath = NormalizeAssetPath(folderPath);
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            string parentPath = Path.GetDirectoryName(folderPath)?.Replace("\\", "/");
            string folderName = Path.GetFileName(folderPath);

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(folderName))
            {
                throw new InvalidOperationException($"Cannot create folder at '{folderPath}'.");
            }

            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                EnsureFolderExists(parentPath);
            }

            AssetDatabase.CreateFolder(parentPath, folderName);
        }

        private static string CombineAssetPath(params string[] parts)
        {
            return string.Join("/", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim('/')));
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            return assetPath.Replace("\\", "/");
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, NormalizeAssetPath(assetPath)));
        }

        private static long GetAssetWriteTimeUtcTicks(string assetPath)
        {
            string absolutePath = ToAbsolutePath(assetPath);
            return File.Exists(absolutePath) ? File.GetLastWriteTimeUtc(absolutePath).Ticks : 0L;
        }

        /// <summary>
        /// Loads all Mesh sub-assets from an FBX file. This is the reliable way to get meshes
        /// from model assets in Unity 2022.3, since sharedMesh on SkinnedMeshRenderers obtained
        /// via LoadAssetAtPath&lt;GameObject&gt; can be null.
        /// </summary>
        private static Dictionary<string, Mesh> LoadMeshSubAssets(string fbxAssetPath)
        {
            var result = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);

            UnityEngine.Object[] allSubAssets = AssetDatabase.LoadAllAssetsAtPath(fbxAssetPath);
            if (allSubAssets == null || allSubAssets.Length == 0)
            {
                Debug.LogWarning($"[AvatarSetupWizard] LoadAllAssetsAtPath returned no assets for '{fbxAssetPath}'.");
                return result;
            }

            foreach (UnityEngine.Object subAsset in allSubAssets)
            {
                if (subAsset is Mesh mesh && !string.IsNullOrEmpty(mesh.name))
                {
                    result[mesh.name] = mesh;
                }
            }

            return result;
        }

        /// <summary>
        /// Collects SkinnedMeshRenderer info from a root GameObject hierarchy.
        /// Does NOT filter by sharedMesh != null. Instead, if meshSubAssets is provided,
        /// tries to resolve the mesh name from the sub-asset dictionary by matching the
        /// GameObject name to mesh names (common Unity FBX convention).
        /// </summary>
        private static List<RendererInfo> GetRendererInfos(GameObject root, Dictionary<string, Mesh> meshSubAssets)
        {
            var results = new List<RendererInfo>();

            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            Debug.Log($"[AvatarSetupWizard] GetRendererInfos: root='{root.name}', " +
                      $"GetComponentsInChildren found {renderers.Length} SkinnedMeshRenderer(s).");

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                string objectName = renderer.gameObject.name;
                string relativePath = GetRelativeTransformPath(renderer.transform);

                // Determine the mesh name. Priority:
                // 1. sharedMesh.name (if available)
                // 2. Match from meshSubAssets by GameObject name
                // 3. Empty string (no mesh found)
                string meshName = string.Empty;

                if (renderer.sharedMesh != null)
                {
                    meshName = renderer.sharedMesh.name;
                }
                else if (meshSubAssets != null)
                {
                    // Try exact match by GO name first, then case-insensitive search.
                    if (meshSubAssets.TryGetValue(objectName, out Mesh _))
                    {
                        meshName = objectName;
                    }
                    else
                    {
                        // Try to find a mesh whose name contains the GO name or vice versa.
                        string found = meshSubAssets.Keys.FirstOrDefault(
                            key => key.IndexOf(objectName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   objectName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (found != null)
                        {
                            meshName = found;
                        }
                    }
                }

                Debug.Log($"[AvatarSetupWizard]   SMR '{objectName}' at '{relativePath}' => " +
                          $"sharedMesh={(renderer.sharedMesh != null ? renderer.sharedMesh.name : "null")}, " +
                          $"resolvedMeshName='{meshName}'");

                results.Add(new RendererInfo
                {
                    Renderer = renderer,
                    ObjectName = objectName,
                    RelativePath = relativePath,
                    MeshName = meshName
                });
            }

            return results;
        }

        private static MeshSelectionState CreateMeshSelectionState(
            RendererInfo fbxRenderer,
            List<RendererInfo> prefabRenderers,
            ISet<string> usedPrefabPaths)
        {
            RendererInfo matchedRenderer = FindBestRendererMatch(fbxRenderer, prefabRenderers, usedPrefabPaths, out string matchReason);

            if (matchedRenderer != null)
            {
                usedPrefabPaths.Add(matchedRenderer.RelativePath);
            }

            return new MeshSelectionState
            {
                fbxObjectName = fbxRenderer.ObjectName,
                fbxRelativePath = fbxRenderer.RelativePath,
                fbxMeshName = fbxRenderer.MeshName,
                prefabObjectName = matchedRenderer?.ObjectName ?? string.Empty,
                prefabRelativePath = matchedRenderer?.RelativePath ?? string.Empty,
                prefabMeshName = matchedRenderer?.MeshName ?? string.Empty,
                matchReason = matchReason,
                hasMatch = matchedRenderer != null,
                selected = matchedRenderer != null
            };
        }

        private static RendererInfo FindBestRendererMatch(
            RendererInfo fbxRenderer,
            List<RendererInfo> prefabRenderers,
            ISet<string> usedPrefabPaths,
            out string matchReason)
        {
            // 1. Match by relative path (strongest match).
            RendererInfo relativePathMatch = prefabRenderers.FirstOrDefault(renderer =>
                !usedPrefabPaths.Contains(renderer.RelativePath) &&
                string.Equals(renderer.RelativePath, fbxRenderer.RelativePath, StringComparison.OrdinalIgnoreCase));

            if (relativePathMatch != null)
            {
                matchReason = "Relative path";
                return relativePathMatch;
            }

            // 2. Match by GameObject name.
            RendererInfo objectNameMatch = prefabRenderers.FirstOrDefault(renderer =>
                !usedPrefabPaths.Contains(renderer.RelativePath) &&
                string.Equals(renderer.ObjectName, fbxRenderer.ObjectName, StringComparison.OrdinalIgnoreCase));

            if (objectNameMatch != null)
            {
                matchReason = "GameObject name";
                return objectNameMatch;
            }

            // 3. Match by mesh name (fallback).
            if (!string.IsNullOrEmpty(fbxRenderer.MeshName))
            {
                RendererInfo meshNameMatch = prefabRenderers.FirstOrDefault(renderer =>
                    !usedPrefabPaths.Contains(renderer.RelativePath) &&
                    !string.IsNullOrEmpty(renderer.MeshName) &&
                    string.Equals(renderer.MeshName, fbxRenderer.MeshName, StringComparison.OrdinalIgnoreCase));

                if (meshNameMatch != null)
                {
                    matchReason = "Mesh name";
                    return meshNameMatch;
                }
            }

            matchReason = string.Empty;
            return null;
        }

        /// <summary>
        /// Returns the relative transform path (excluding the root object name).
        /// For example, if the hierarchy is Root/Armature/Body, this returns "Armature/Body".
        /// If the transform IS the root, returns its own name.
        /// </summary>
        private static string GetRelativeTransformPath(Transform transform)
        {
            List<string> names = new List<string>();

            Transform current = transform;
            while (current != null && current.parent != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            // If this IS the root (no parent), return its own name.
            if (names.Count == 0)
            {
                return transform.name;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private void DrawReadOnlyPathField(string label, string value)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(label, value ?? string.Empty);
            }
        }

        private void EnsureStyles()
        {
            if (sectionStyle != null)
            {
                return;
            }

            sectionStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 0, 0)
            };

            sectionHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13
            };

            titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                fixedHeight = 24f
            };

            stepStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 6, 6)
            };

            currentStepStyle = new GUIStyle(stepStyle);
            currentStepStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.8f, 0.92f, 1f)
                : new Color(0.1f, 0.35f, 0.7f);

            subLabelStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = true
            };

            richMiniLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                richText = true,
                wordWrap = true
            };
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField(new GUIContent(" Avatar Setup Wizard", EditorGUIUtility.IconContent("AvatarSelector").image), new GUIStyle(EditorStyles.boldLabel) { fontSize = 18, fixedHeight = 28f });
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Guide the full avatar duplication flow without leaving the editor.", subLabelStyle);
            EditorGUILayout.Space(4);
            Rect separatorRect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(separatorRect, EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.7f, 0.7f, 0.7f));
            EditorGUILayout.Space(5);
        }

        private void DrawStepIndicator()
        {
            using (new EditorGUILayout.HorizontalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(5, 5, 5, 5) }))
            {
                DrawStepBadge(WizardStep.Setup, "1. Setup");
                DrawStepArrow();
                DrawStepBadge(WizardStep.WaitForImport, "2. Import");
                DrawStepArrow();
                DrawStepBadge(WizardStep.SelectMeshes, "3. Meshes");
                DrawStepArrow();
                DrawStepBadge(WizardStep.Complete, "4. Finish");
            }
        }

        private void DrawStepBadge(WizardStep step, string label)
        {
            bool isPast = currentStep > step;
            bool isCurrent = currentStep == step;

            GUIStyle style = new GUIStyle(isCurrent ? currentStepStyle : stepStyle);
            if (isPast) style.normal.textColor = new Color(0.3f, 0.7f, 0.3f); // Green text for past steps

            GUIContent content = isPast 
                ? new GUIContent($" {label}", EditorGUIUtility.IconContent("TestPassed").image)
                : new GUIContent(label);

            using (new EditorGUILayout.VerticalScope(GUILayout.Height(StepBadgeHeight)))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(content, style, GUILayout.ExpandWidth(true));
                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawStepArrow()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Height(StepBadgeHeight)))
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(EditorGUIUtility.IconContent("IN foldout").image, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(16f));
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawSection(string title, string description, Action drawContent)
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(15, 15, 15, 15) }))
            {
                EditorGUILayout.LabelField(title, new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(description, subLabelStyle);
                EditorGUILayout.Space(12);
                drawContent?.Invoke();
            }
        }

        private bool DrawPrimaryButton(string text, float height = 34f)
        {
            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.6f, 1f) : new Color(0.1f, 0.4f, 0.8f);
            bool clicked = GUILayout.Button(text, new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.Height(height));
            GUI.backgroundColor = oldColor;
            return clicked;
        }

        private void DrawPathSummary(bool includeAvatarRoot = false)
        {
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 8, 8) }))
            {
                if (includeAvatarRoot)
                {
                    DrawReadOnlyPathField("Avatar Root", avatarRootPath);
                }

                if (!string.IsNullOrEmpty(copiedFbxPath))
                {
                    DrawReadOnlyPathField("Modified FBX", copiedFbxPath);
                }

                if (!string.IsNullOrEmpty(copiedPrefabPath))
                {
                    DrawReadOnlyPathField("Target Prefab", copiedPrefabPath);
                }

                if (!string.IsNullOrEmpty(createdScenePath))
                {
                    DrawReadOnlyPathField("Working Scene", createdScenePath);
                }
            }
        }

        private void DrawMeshSelectionToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All", GUILayout.Width(90f)))
                {
                    SetMeshSelectionState(true);
                }

                if (GUILayout.Button("Deselect All", GUILayout.Width(90f)))
                {
                    SetMeshSelectionState(false);
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label($"{meshSelections.Count(selection => selection.selected)} selected", EditorStyles.miniBoldLabel);
            }
        }

        private void DrawMeshSelectionRow(MeshSelectionState meshSelection)
        {
            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = meshSelection.hasMatch ? originalColor : new Color(1f, 0.9f, 0.7f, 0.5f);
            
            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(8, 8, 6, 6) }))
            {
                GUI.backgroundColor = originalColor;

                using (new EditorGUI.DisabledScope(!meshSelection.hasMatch))
                {
                    string meshLabel = string.IsNullOrEmpty(meshSelection.fbxMeshName)
                        ? meshSelection.fbxObjectName
                        : $"{meshSelection.fbxObjectName} ({meshSelection.fbxMeshName})";
                    meshSelection.selected = EditorGUILayout.ToggleLeft(meshLabel, meshSelection.selected, EditorStyles.boldLabel);
                }

                EditorGUILayout.LabelField($"FBX: {meshSelection.fbxRelativePath}", richMiniLabelStyle);

                GUIContent statusIcon = meshSelection.hasMatch
                    ? EditorGUIUtility.IconContent("TestPassed")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");

                string matchText = meshSelection.hasMatch
                    ? $"<b>Prefab:</b> {meshSelection.prefabRelativePath} ({meshSelection.prefabMeshName}) [{meshSelection.matchReason}]"
                    : "<b>Prefab:</b> <color=#c27725>No matching skinned mesh renderer found</color>";

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(statusIcon, GUILayout.Width(18f), GUILayout.Height(16f));
                    EditorGUILayout.LabelField(matchText, richMiniLabelStyle);
                }
            }

            EditorGUILayout.Space(2f);
        }

        private void SetMeshSelectionState(bool selected)
        {
            foreach (MeshSelectionState meshSelection in meshSelections)
            {
                if (meshSelection.hasMatch)
                {
                    meshSelection.selected = selected;
                }
            }
        }

        private void ResetWizard()
        {
            sourceFbx = null;
            sourcePrefab = null;
            avatarName = DefaultAvatarName;
            copiedFbxPath = string.Empty;
            copiedPrefabPath = string.Empty;
            createdScenePath = string.Empty;
            avatarRootPath = string.Empty;
            watchedFbxWriteTimeUtcTicks = 0L;
            meshSelections.Clear();
            statusMessage = string.Empty;
            EditorApplication.delayCall -= TryMoveToMeshSelectionAfterImport;
            pendingImportTransition = false;
            importLoadAttempts = 0;
            currentStep = WizardStep.Setup;
        }

        private class RendererInfo
        {
            public SkinnedMeshRenderer Renderer;
            public string ObjectName;
            public string RelativePath;
            public string MeshName;
        }
    }
}
