using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Generates and updates FTPatchConfig assets (from the PatcherHub package) using reflection.
    /// This avoids a compile-time dependency on PatcherHub while still being able to
    /// create pre-populated configuration assets when diff files are generated.
    /// </summary>
    public static class FTPatchConfigGenerator
    {
        private const string FTPatchConfigTypeName = "FTPatchConfig";
        private const string LogPrefix = "[FTPatchConfigGenerator]";

        /// <summary>
        /// Context data used to populate the FTPatchConfig asset.
        /// Fields left null/empty will be skipped (or use defaults).
        /// </summary>
        public class ConfigContext
        {
            /// <summary>The original unmodified FBX asset (set as originalModelPrefab on the config).</summary>
            public GameObject OriginalFbx;

            /// <summary>Display name for the avatar (falls back to FBX filename if null).</summary>
            public string AvatarDisplayName;

            /// <summary>Asset path to the generated FBX .hdiff diff file.</summary>
            public string FbxDiffAssetPath;

            /// <summary>Asset path to the generated meta .hdiff diff file.</summary>
            public string MetaDiffAssetPath;

            /// <summary>
            /// The folder where the FTPatchConfig asset should be created.
            /// Expected to be a Unity asset path (e.g., "Assets/!Pawlygon/AvatarName/patcher").
            /// </summary>
            public string ConfigOutputFolder;

            /// <summary>
            /// The path PatcherHub should use as the FBX output folder when patching on the end user's machine.
            /// Expected to be a Unity asset path (e.g., "Assets/!Pawlygon/AvatarName/FBX").
            /// </summary>
            public string FbxOutputPath;

            /// <summary>Optional list of patched prefabs to include in the config.</summary>
            public List<GameObject> PatchedPrefabs;

            /// <summary>Name used for the config asset file. Falls back to AvatarDisplayName or FBX name.</summary>
            public string ConfigAssetName;
        }

        // Static fields are reset by every domain reload, which Unity performs after scripts are
        // compiled (e.g. after importing PatcherHub), so the cached lookup never goes stale.
        private static Type cachedConfigType;
        private static bool configTypeLookedUp;

        /// <summary>
        /// Attempts to find the FTPatchConfig type using Unity's TypeCache, caching the result
        /// for the current domain because callers run on every GUI event and menu validation.
        /// Returns null if PatcherHub is not installed.
        /// </summary>
        private static Type FindFTPatchConfigType()
        {
            if (configTypeLookedUp) return cachedConfigType;

            configTypeLookedUp = true;
            cachedConfigType = null;

            var types = TypeCache.GetTypesDerivedFrom<ScriptableObject>();
            foreach (Type type in types)
            {
                if (type.Name == FTPatchConfigTypeName)
                {
                    cachedConfigType = type;
                    break;
                }
            }

            return cachedConfigType;
        }

        /// <summary>
        /// Checks whether PatcherHub is installed (i.e., the FTPatchConfig type is available).
        /// </summary>
        public static bool IsPatcherHubAvailable()
        {
            return FindFTPatchConfigType() != null;
        }

        /// <summary>
        /// Returns the PatcherHub <c>FTPatchConfig</c> ScriptableObject type, or null if PatcherHub
        /// is not installed. Exposed so other editor tools can filter object pickers and validate
        /// assets without taking a compile-time dependency on PatcherHub.
        /// </summary>
        public static Type GetFTPatchConfigType()
        {
            return FindFTPatchConfigType();
        }

        // =====================================================================
        // Context building (shared by the Avatar Setup Wizard and the diff generator inspector)
        // =====================================================================

        /// <summary>File name (without extension) of the config asset for an avatar name.</summary>
        public static string GetConfigAssetName(string avatarName)
        {
            return avatarName + " FTPatchConfig";
        }

        /// <summary>Asset path of the config asset for an avatar root folder and avatar name.</summary>
        public static string GetConfigAssetPath(string avatarRootPath, string avatarName)
        {
            return PawlygonEditorUtils.CombineAssetPath(FTDiffGenerator.GetPatcherFolderAssetPath(avatarRootPath), GetConfigAssetName(avatarName) + ".asset");
        }

        /// <summary>
        /// The folder PatcherHub writes the patched FBX to on the end user's machine: the avatar
        /// root's <c>FBX</c> folder, where the wizard keeps the modified FBX.
        /// </summary>
        public static string GetFbxOutputFolderPath(string avatarRootPath)
        {
            return PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "FBX");
        }

        /// <summary>
        /// Builds the config context for an avatar whose diff files were (or will be) written to
        /// <paramref name="avatarRootPath"/> (the diff generator's output folder). Every caller
        /// goes through here so the asset name, display name, diff references and FBX output path
        /// are always the same. Returns null when a required value is missing.
        /// </summary>
        public static ConfigContext BuildContext(GameObject originalFbx, string avatarRootPath, string diffBaseName, string avatarName, IEnumerable<GameObject> patchedPrefabs)
        {
            if (originalFbx == null ||
                string.IsNullOrEmpty(avatarRootPath) ||
                string.IsNullOrEmpty(diffBaseName) ||
                string.IsNullOrWhiteSpace(avatarName))
            {
                return null;
            }

            avatarName = avatarName.Trim();
            List<GameObject> prefabs = patchedPrefabs?.Where(prefab => prefab != null).Distinct().ToList();

            return new ConfigContext
            {
                OriginalFbx = originalFbx,
                AvatarDisplayName = avatarName,
                FbxDiffAssetPath = FTDiffGenerator.GetFbxDiffAssetPath(avatarRootPath, diffBaseName),
                MetaDiffAssetPath = FTDiffGenerator.GetMetaDiffAssetPath(avatarRootPath, diffBaseName),
                ConfigOutputFolder = FTDiffGenerator.GetPatcherFolderAssetPath(avatarRootPath),
                FbxOutputPath = GetFbxOutputFolderPath(avatarRootPath),
                PatchedPrefabs = prefabs != null && prefabs.Count > 0 ? prefabs : null,
                ConfigAssetName = GetConfigAssetName(avatarName)
            };
        }

        /// <summary>
        /// Builds the config context for a diff generator used outside the wizard (its inspector
        /// or context menu), where the wizard's avatar entry is not available. The generator's
        /// output folder is treated as the avatar root, as the wizard sets it up, and the rest is
        /// derived so a wizard-created generator yields the wizard's config:
        /// <list type="bullet">
        /// <item>Asset name: an existing config in the patcher folder whose original FBX is this
        /// generator's keeps its name (so it is updated, never duplicated). Otherwise the avatar
        /// name is the output folder's name, or the original FBX's file name when several diff
        /// generators write to the same folder (the wizard's shared-folder rule) or a config with
        /// the folder name already belongs to another FBX.</item>
        /// <item>Patched prefabs: prefabs under the output folder (outside <c>patcher</c>) that
        /// depend on the modified or original FBX.</item>
        /// </list>
        /// Returns null when the generator is incomplete.
        /// </summary>
        public static ConfigContext BuildContextForGenerator(FTDiffGenerator generator)
        {
            if (generator == null || generator.originalModelFbx == null || generator.outputDirectory == null)
            {
                return null;
            }

            string avatarRootPath = AssetDatabase.GetAssetPath(generator.outputDirectory);
            if (string.IsNullOrEmpty(avatarRootPath) || !AssetDatabase.IsValidFolder(avatarRootPath))
            {
                return null;
            }

            string originalFbxPath = AssetDatabase.GetAssetPath(generator.originalModelFbx);
            string fbxName = Path.GetFileNameWithoutExtension(originalFbxPath);
            string avatarName = Path.GetFileName(avatarRootPath);

            if (CountDiffGeneratorsWritingTo(generator.outputDirectory) > 1 ||
                IsConfigOwnedByAnotherFbx(GetConfigAssetPath(avatarRootPath, avatarName), generator.originalModelFbx))
            {
                avatarName = fbxName;
            }

            ConfigContext context = BuildContext(
                generator.originalModelFbx,
                avatarRootPath,
                generator.GetBaseName(),
                avatarName,
                FindPatchedPrefabs(avatarRootPath, generator.originalModelFbx, generator.modifiedModelFbx));

            string existingConfigName = FindExistingConfigName(FTDiffGenerator.GetPatcherFolderAssetPath(avatarRootPath), generator.originalModelFbx);
            if (context != null && !string.IsNullOrEmpty(existingConfigName))
            {
                context.ConfigAssetName = existingConfigName;
            }

            return context;
        }

        private static int CountDiffGeneratorsWritingTo(DefaultAsset outputDirectory)
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(FTDiffGenerator)))
            {
                var otherGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(AssetDatabase.GUIDToAssetPath(guid));
                if (otherGenerator != null && otherGenerator.outputDirectory == outputDirectory)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsConfigOwnedByAnotherFbx(string configAssetPath, GameObject originalFbx)
        {
            Type configType = FindFTPatchConfigType();
            if (configType == null) return false;

            ScriptableObject config = AssetDatabase.LoadAssetAtPath<ScriptableObject>(configAssetPath);
            if (config == null || !configType.IsInstanceOfType(config)) return false;

            GameObject configFbx = GetConfigOriginalFbx(config, configType);
            return configFbx != null && configFbx != originalFbx;
        }

        /// <summary>
        /// Returns the asset name of a config in <paramref name="patcherFolder"/> whose original
        /// FBX is <paramref name="originalFbx"/>, or null when there is none.
        /// </summary>
        private static string FindExistingConfigName(string patcherFolder, GameObject originalFbx)
        {
            Type configType = FindFTPatchConfigType();
            if (configType == null || originalFbx == null || !AssetDatabase.IsValidFolder(patcherFolder)) return null;

            foreach (string guid in AssetDatabase.FindAssets("t:ScriptableObject", new[] { patcherFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                ScriptableObject config = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                if (config != null && configType.IsInstanceOfType(config) && GetConfigOriginalFbx(config, configType) == originalFbx)
                {
                    return Path.GetFileNameWithoutExtension(path);
                }
            }

            return null;
        }

        private static GameObject GetConfigOriginalFbx(ScriptableObject config, Type configType)
        {
            FieldInfo field = configType.GetField("originalModelPrefab", BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(config) as GameObject : null;
        }

        /// <summary>
        /// Finds prefabs under <paramref name="avatarRootPath"/> (excluding its patcher folder)
        /// that depend on the modified or the original FBX.
        /// </summary>
        private static List<GameObject> FindPatchedPrefabs(string avatarRootPath, GameObject originalFbx, GameObject modifiedFbx)
        {
            var fbxPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameObject fbx in new[] { modifiedFbx, originalFbx })
            {
                string fbxPath = fbx != null ? AssetDatabase.GetAssetPath(fbx) : null;
                if (!string.IsNullOrEmpty(fbxPath)) fbxPaths.Add(fbxPath);
            }

            var prefabs = new List<GameObject>();
            if (fbxPaths.Count == 0) return prefabs;

            string patcherFolderPrefix = FTDiffGenerator.GetPatcherFolderAssetPath(avatarRootPath) + "/";

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { avatarRootPath }))
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                if (prefabPath.StartsWith(patcherFolderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!AssetDatabase.GetDependencies(prefabPath, true).Any(fbxPaths.Contains))
                {
                    continue;
                }

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab != null)
                {
                    prefabs.Add(prefab);
                }
            }

            return prefabs;
        }

        /// <summary>
        /// Creates or updates an FTPatchConfig asset with the provided context.
        /// If an asset already exists at the target path, only diff-related fields and hashes are updated
        /// (preserving user-edited fields like avatarVersion, requiredDependency, etc.).
        /// </summary>
        /// <param name="context">The context data to populate the config with.</param>
        /// <returns>The asset path of the created/updated config, or null if PatcherHub is not installed.</returns>
        public static string GenerateConfig(ConfigContext context)
        {
            if (context == null)
            {
                Debug.LogError($"{LogPrefix} ConfigContext is null.");
                return null;
            }

            Type configType = FindFTPatchConfigType();
            if (configType == null)
            {
                Debug.Log($"{LogPrefix} PatcherHub is not installed. Skipping FTPatchConfig generation.");
                return null;
            }

            string configFolder = context.ConfigOutputFolder;
            if (string.IsNullOrEmpty(configFolder))
            {
                Debug.LogError($"{LogPrefix} ConfigOutputFolder is not specified.");
                return null;
            }

            string assetName = ResolveAssetName(context);
            string assetPath = PawlygonEditorUtils.CombineAssetPath(configFolder, assetName + ".asset");

            // Check if an existing config already exists at this path
            ScriptableObject existingConfig = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            bool isUpdate = existingConfig != null && configType.IsInstanceOfType(existingConfig);

            ScriptableObject config = isUpdate ? existingConfig : ScriptableObject.CreateInstance(configType);

            if (isUpdate)
            {
                Undo.RecordObject(config, "Update FTPatchConfig");
            }

            PopulateConfig(config, configType, context, isUpdate);

            if (isUpdate)
            {
                EditorUtility.SetDirty(config);
                Debug.Log($"{LogPrefix} Updated existing FTPatchConfig at '{assetPath}'.");
            }
            else
            {
                PawlygonEditorUtils.EnsureFolderExists(configFolder);
                AssetDatabase.CreateAsset(config, assetPath);
                Debug.Log($"{LogPrefix} Created new FTPatchConfig at '{assetPath}'.");
            }

            AssetDatabase.SaveAssets();
            return assetPath;
        }

        private static void PopulateConfig(ScriptableObject config, Type configType, ConfigContext context, bool isUpdate)
        {
            // --- Always set (even on update): diff files, hashes, original FBX ---

            // originalModelPrefab (it's actually the FBX)
            if (context.OriginalFbx != null)
            {
                SetField(config, configType, "originalModelPrefab", context.OriginalFbx);
            }

            // Diff file references
            if (!string.IsNullOrEmpty(context.FbxDiffAssetPath))
            {
                UnityEngine.Object fbxDiff = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(context.FbxDiffAssetPath);
                if (fbxDiff != null)
                {
                    SetField(config, configType, "fbxDiffFile", fbxDiff);
                }
                else
                {
                    Debug.LogWarning($"{LogPrefix} Could not load FBX diff file at '{context.FbxDiffAssetPath}'.");
                }
            }

            if (!string.IsNullOrEmpty(context.MetaDiffAssetPath))
            {
                UnityEngine.Object metaDiff = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(context.MetaDiffAssetPath);
                if (metaDiff != null)
                {
                    SetField(config, configType, "metaDiffFile", metaDiff);
                }
                else
                {
                    Debug.LogWarning($"{LogPrefix} Could not load meta diff file at '{context.MetaDiffAssetPath}'.");
                }
            }

            // Compute and set MD5 hashes from the original FBX
            if (context.OriginalFbx != null)
            {
                string originalFbxAssetPath = AssetDatabase.GetAssetPath(context.OriginalFbx);
                if (!string.IsNullOrEmpty(originalFbxAssetPath))
                {
                    string fullFbxPath = Path.GetFullPath(originalFbxAssetPath);
                    string fullMetaPath = fullFbxPath + ".meta";

                    if (File.Exists(fullFbxPath))
                    {
                        string fbxHash = ComputeMD5(fullFbxPath);
                        if (fbxHash != null)
                        {
                            SetField(config, configType, "expectedFbxHash", fbxHash);
                        }
                    }

                    if (File.Exists(fullMetaPath))
                    {
                        string metaHash = ComputeMD5(fullMetaPath);
                        if (metaHash != null)
                        {
                            SetField(config, configType, "expectedMetaHash", metaHash);
                        }
                    }
                }
            }

            // --- Only set on new creation (not update, to preserve user edits) ---
            if (!isUpdate)
            {
                // Avatar display name
                string displayName = context.AvatarDisplayName;
                if (string.IsNullOrEmpty(displayName) && context.OriginalFbx != null)
                {
                    displayName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(context.OriginalFbx));
                }

                if (!string.IsNullOrEmpty(displayName))
                {
                    SetField(config, configType, "avatarDisplayName", displayName);
                }

                // FBX output path
                if (!string.IsNullOrEmpty(context.FbxOutputPath))
                {
                    SetField(config, configType, "outputPath", context.FbxOutputPath);
                }

                // Patched prefabs
                if (context.PatchedPrefabs != null && context.PatchedPrefabs.Count > 0)
                {
                    SetPatchedPrefabs(config, configType, context.PatchedPrefabs);
                }
            }
        }

        private static void SetField(ScriptableObject config, Type configType, string fieldName, object value)
        {
            FieldInfo field = configType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(config, value);
            }
            else
            {
                Debug.LogWarning($"{LogPrefix} Field '{fieldName}' not found on {configType.Name}. PatcherHub version may differ.");
            }
        }

        private static void SetPatchedPrefabs(ScriptableObject config, Type configType, List<GameObject> prefabs)
        {
            FieldInfo field = configType.GetField("patchedPrefabs", BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                Debug.LogWarning($"{LogPrefix} Field 'patchedPrefabs' not found on {configType.Name}.");
                return;
            }

            // The field is List<GameObject>, create it via reflection to be safe
            object currentValue = field.GetValue(config);
            if (currentValue is IList list)
            {
                list.Clear();
                foreach (GameObject prefab in prefabs)
                {
                    if (prefab != null)
                    {
                        list.Add(prefab);
                    }
                }
            }
            else
            {
                // Field exists but is null or unexpected type, create a new List<GameObject>
                var newList = new List<GameObject>(prefabs.Where(p => p != null));
                field.SetValue(config, newList);
            }
        }

        private static string ComputeMD5(string filePath)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hash = md5.ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} Failed to compute MD5 for '{filePath}': {ex.Message}");
                return null;
            }
        }

        private static string ResolveAssetName(ConfigContext context)
        {
            if (!string.IsNullOrEmpty(context.ConfigAssetName))
            {
                return context.ConfigAssetName;
            }

            if (!string.IsNullOrEmpty(context.AvatarDisplayName))
            {
                return GetConfigAssetName(context.AvatarDisplayName);
            }

            if (context.OriginalFbx != null)
            {
                string fbxName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(context.OriginalFbx));
                return GetConfigAssetName(fbxName);
            }

            return "FTPatchConfig";
        }
    }
}
