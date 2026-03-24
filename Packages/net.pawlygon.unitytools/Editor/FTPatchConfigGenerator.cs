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

        /// <summary>
        /// Attempts to find the FTPatchConfig type using Unity's TypeCache.
        /// Returns null if PatcherHub is not installed.
        /// </summary>
        private static Type FindFTPatchConfigType()
        {
            var types = TypeCache.GetTypesDerivedFrom<ScriptableObject>();
            foreach (Type type in types)
            {
                if (type.Name == FTPatchConfigTypeName)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks whether PatcherHub is installed (i.e., the FTPatchConfig type is available).
        /// </summary>
        public static bool IsPatcherHubAvailable()
        {
            return FindFTPatchConfigType() != null;
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
                return context.AvatarDisplayName + " FTPatchConfig";
            }

            if (context.OriginalFbx != null)
            {
                string fbxName = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(context.OriginalFbx));
                return fbxName + " FTPatchConfig";
            }

            return "FTPatchConfig";
        }
    }
}
