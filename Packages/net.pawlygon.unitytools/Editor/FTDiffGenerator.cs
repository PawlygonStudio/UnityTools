using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;

namespace Pawlygon.UnityTools.Editor
{
    [CreateAssetMenu(fileName = "NewFTDiffGenerator", menuName = "Pawlygon/FaceTracking Diff Generator")]
    public class FTDiffGenerator : ScriptableObject
    {
        [Header("FBX References")]
        [FormerlySerializedAs("originalModelPrefab")]
        public GameObject originalModelFbx;

        [FormerlySerializedAs("modifiedModelPrefab")]
        public GameObject modifiedModelFbx;

        [Header("Output Settings")]
        public DefaultAsset outputDirectory;

        [ContextMenu("Generate Diff Files")]
        public void GenerateDiffFiles()
        {
            string originalFbxPath = GetFBXPath(originalModelFbx);
            string modifiedFbxPath = GetFBXPath(modifiedModelFbx);

            if (string.IsNullOrEmpty(originalFbxPath) || string.IsNullOrEmpty(modifiedFbxPath))
            {
                Debug.LogError("Failed to resolve FBX paths from the given models. Make sure your references are FBX models.");
                return;
            }

            string baseName = Path.GetFileNameWithoutExtension(originalFbxPath).Replace(" ", "_");

            string outputFolderPath = AssetDatabase.GetAssetPath(outputDirectory);
            string absoluteOutputPath = Path.GetFullPath(outputFolderPath);
            string diffOutputPath = Path.Combine(absoluteOutputPath, "patcher", "data", "DiffFiles");

            Directory.CreateDirectory(diffOutputPath);

            string fbxDiffOutputPath = Path.Combine(diffOutputPath, baseName + ".hdiff");
            string metaDiffOutputPath = Path.Combine(diffOutputPath, baseName + "Meta.hdiff");

            string basePackagePath = Path.Combine("Packages", "net.pawlygon.unitytools", "hdiff", "hdiffz");

            string hdiffExecutablePath = Application.platform switch
            {
                RuntimePlatform.WindowsEditor => Path.Combine(basePackagePath, "Windows", "hdiffz.exe"),
                RuntimePlatform.OSXEditor => Path.Combine(basePackagePath, "Mac", "hdiffz"),
                RuntimePlatform.LinuxEditor => Path.Combine(basePackagePath, "Linux", "hdiffz"),
                _ => throw new PlatformNotSupportedException("Unsupported platform for hdiffz")
            };

            hdiffExecutablePath = Path.GetFullPath(hdiffExecutablePath);

#if !UNITY_EDITOR_WIN
            if (!SetExecutablePermission(hdiffExecutablePath))
            {
                Debug.LogError("Failed to set executable permission for hdiffz.");
                return;
            }
#endif

            if (!File.Exists(hdiffExecutablePath))
            {
                Debug.LogWarning("Skipping diff generation. Missing hdiffz executable at: " + hdiffExecutablePath);
                return;
            }

            string fbxArguments = $"\"{originalFbxPath}\" \"{modifiedFbxPath}\" \"{fbxDiffOutputPath}\"";
            string metaArguments = $"\"{originalFbxPath}.meta\" \"{modifiedFbxPath}.meta\" \"{metaDiffOutputPath}\"";

            Debug.Log("Generating FBX diff...");
            LaunchProcess(hdiffExecutablePath, fbxArguments);

            Debug.Log("Generating meta diff...");
            LaunchProcess(hdiffExecutablePath, metaArguments);

            Debug.Log("Diff files successfully created at: " + diffOutputPath);
            AssetDatabase.Refresh();
        }

        private void LaunchProcess(string executablePath, string arguments)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = arguments,
                        WorkingDirectory = Application.dataPath,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                string stdOutput = process.StandardOutput.ReadToEnd();
                string stdError = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrEmpty(stdOutput))
                {
                    Debug.Log("hdiffz output: " + stdOutput);
                }

                if (!string.IsNullOrEmpty(stdError))
                {
                    Debug.LogWarning("hdiffz error: " + stdError);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to execute patch process: {exception.Message}");
            }
        }

        private string GetFBXPath(GameObject model)
        {
            if (model == null)
            {
                return null;
            }

            string modelPath = AssetDatabase.GetAssetPath(model);
            if (string.IsNullOrEmpty(modelPath))
            {
                return null;
            }

            if (!string.Equals(Path.GetExtension(modelPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("The selected model is not an FBX file: " + modelPath);
                return null;
            }

            return Path.GetFullPath(modelPath);
        }

        /// <summary>
        /// Gets the base name used for diff file naming (FBX filename with spaces replaced by underscores).
        /// </summary>
        public string GetBaseName()
        {
            if (originalModelFbx == null) return null;
            string originalFbxPath = GetFBXPath(originalModelFbx);
            if (string.IsNullOrEmpty(originalFbxPath)) return null;
            return Path.GetFileNameWithoutExtension(originalFbxPath).Replace(" ", "_");
        }

        /// <summary>
        /// Gets the Unity asset path of the output directory's patcher folder.
        /// </summary>
        public string GetPatcherFolderAssetPath()
        {
            if (outputDirectory == null) return null;
            return AssetDatabase.GetAssetPath(outputDirectory) + "/patcher";
        }

        private bool SetExecutablePermission(string path)
        {
            try
            {
                using var chmod = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/chmod",
                        Arguments = $"+x \"{path}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                chmod.Start();
                chmod.WaitForExit();
                return chmod.ExitCode == 0;
            }
            catch (Exception exception)
            {
                Debug.LogError("Error while setting executable permission: " + exception);
                return false;
            }
        }
    }
}
