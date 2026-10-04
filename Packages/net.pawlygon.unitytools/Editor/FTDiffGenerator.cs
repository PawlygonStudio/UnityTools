using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;

namespace Pawlygon.UnityTools.Editor
{
    [CreateAssetMenu(fileName = "NewFTDiffGenerator", menuName = "Pawlygon/FaceTracking Diff Generator")]
    public class FTDiffGenerator : ScriptableObject
    {
        private const string LogPrefix = "[FTDiffGenerator]";

        /// <summary>
        /// Upper bound for a single hdiffz run. Large FBX files take seconds, not minutes, so this
        /// only exists to stop a hung process from freezing the editor forever.
        /// </summary>
        private const int HdiffzTimeoutMilliseconds = 10 * 60 * 1000;

        /// <summary>Maximum amount of hdiffz output repeated in user-facing error messages.</summary>
        private const int MaxOutputInErrorMessage = 800;

        [Header("FBX References")]
        [FormerlySerializedAs("originalModelPrefab")]
        public GameObject originalModelFbx;

        [FormerlySerializedAs("modifiedModelPrefab")]
        public GameObject modifiedModelFbx;

        [Header("Output Settings")]
        public DefaultAsset outputDirectory;

        [ContextMenu("Generate Diff Files")]
        private void GenerateDiffFilesFromContextMenu()
        {
            if (!GenerateDiffFiles(out string errorMessage))
            {
                EditorUtility.DisplayDialog("Diff Generation Failed", errorMessage + "\n\nSee the Console for the full hdiffz output.", "OK");
            }
        }

        /// <summary>
        /// Generates the FBX and .meta .hdiff patch files. Returns true only when hdiffz succeeded
        /// for both files and each output file exists and is non-empty. Failures are logged as errors.
        /// </summary>
        public bool GenerateDiffFiles()
        {
            return GenerateDiffFiles(out _);
        }

        /// <summary>
        /// Generates the FBX and .meta .hdiff patch files into
        /// <c>&lt;outputDirectory&gt;/patcher/data/DiffFiles</c>, overwriting any previous output.
        /// Returns true only when hdiffz succeeded for both files and each output file exists and is
        /// non-empty; otherwise <paramref name="errorMessage"/> describes the failure (it is also
        /// logged as an error together with hdiffz's output).
        /// </summary>
        public bool GenerateDiffFiles(out string errorMessage)
        {
            string originalFbxPath = GetFBXPath(originalModelFbx);
            string modifiedFbxPath = GetFBXPath(modifiedModelFbx);

            if (string.IsNullOrEmpty(originalFbxPath) || string.IsNullOrEmpty(modifiedFbxPath))
            {
                return Fail("Failed to resolve FBX paths from the given models. Make sure your references are FBX models.", out errorMessage);
            }

            string outputFolderPath = outputDirectory != null ? AssetDatabase.GetAssetPath(outputDirectory) : null;
            if (string.IsNullOrEmpty(outputFolderPath) || !AssetDatabase.IsValidFolder(outputFolderPath))
            {
                return Fail("The output directory is not a valid folder asset.", out errorMessage);
            }

            foreach (string requiredFile in new[] { originalFbxPath + ".meta", modifiedFbxPath + ".meta" })
            {
                if (!File.Exists(requiredFile))
                {
                    return Fail($"Missing .meta file: {requiredFile}", out errorMessage);
                }
            }

            string hdiffExecutablePath = GetHdiffzExecutablePath();
            if (string.IsNullOrEmpty(hdiffExecutablePath))
            {
                return Fail($"hdiffz is not available for this platform ({Application.platform}).", out errorMessage);
            }

            // Check for the binary before touching its permissions so a missing file is reported as missing.
            if (!File.Exists(hdiffExecutablePath))
            {
                return Fail("Missing hdiffz executable at: " + hdiffExecutablePath, out errorMessage);
            }

#if !UNITY_EDITOR_WIN
            if (!SetExecutablePermission(hdiffExecutablePath))
            {
                return Fail("Failed to set executable permission for hdiffz at: " + hdiffExecutablePath, out errorMessage);
            }
#endif

            string baseName = GetDiffBaseName(originalFbxPath);
            string diffOutputPath = Path.Combine(Path.GetFullPath(outputFolderPath), "patcher", "data", "DiffFiles");
            string fbxDiffOutputPath = Path.Combine(diffOutputPath, GetFbxDiffFileName(baseName));
            string metaDiffOutputPath = Path.Combine(diffOutputPath, GetMetaDiffFileName(baseName));

            try
            {
                Directory.CreateDirectory(diffOutputPath);
            }
            catch (Exception exception)
            {
                return Fail($"Could not create the diff output folder '{diffOutputPath}': {exception.Message}", out errorMessage);
            }

            Debug.Log($"{LogPrefix} Generating FBX diff...");
            bool succeeded = RunHdiffz(hdiffExecutablePath, originalFbxPath, modifiedFbxPath, fbxDiffOutputPath, "FBX", out errorMessage);

            if (succeeded)
            {
                Debug.Log($"{LogPrefix} Generating meta diff...");
                succeeded = RunHdiffz(hdiffExecutablePath, originalFbxPath + ".meta", modifiedFbxPath + ".meta", metaDiffOutputPath, "meta", out errorMessage);
            }

            // Refresh even after a failure so the AssetDatabase reflects whatever is on disk now.
            AssetDatabase.Refresh();

            if (!succeeded)
            {
                // RunHdiffz already logged the details (including hdiffz's output).
                return false;
            }

            Debug.Log($"{LogPrefix} Diff files successfully created at: {diffOutputPath}", this);
            return true;
        }

        /// <summary>
        /// Runs hdiffz once and verifies the result: the exit code must be 0 and the output file must
        /// exist and be non-empty. stdout and stderr are read asynchronously so a chatty process can
        /// never deadlock on a full pipe buffer.
        /// </summary>
        private bool RunHdiffz(string executablePath, string oldPath, string newPath, string outputPath, string label, out string errorMessage)
        {
            // -f: without it hdiffz refuses to overwrite an existing output file and exits with an
            // error, which would leave the previous (stale) .hdiff in place.
            string arguments = $"-f \"{oldPath}\" \"{newPath}\" \"{outputPath}\"";
            var output = new StringBuilder();
            int exitCode;

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

                DataReceivedEventHandler appendLine = (_, args) =>
                {
                    if (args.Data == null) return;
                    lock (output)
                    {
                        output.AppendLine(args.Data);
                    }
                };

                process.OutputDataReceived += appendLine;
                process.ErrorDataReceived += appendLine;

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(HdiffzTimeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch (Exception)
                    {
                        // The process may already have exited; nothing else to clean up.
                    }

                    return FailWithOutput($"hdiffz did not finish generating the {label} diff within {HdiffzTimeoutMilliseconds / 60000} minutes and was stopped.",
                        arguments, ReadOutput(output), out errorMessage);
                }

                // The parameterless overload also waits for the asynchronous output readers to drain.
                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            catch (Exception exception)
            {
                return FailWithOutput($"Failed to run hdiffz for the {label} diff: {exception.Message}", arguments, ReadOutput(output), out errorMessage);
            }

            string hdiffzOutput = ReadOutput(output);

            if (exitCode != 0)
            {
                return FailWithOutput($"hdiffz exited with code {exitCode} while generating the {label} diff.", arguments, hdiffzOutput, out errorMessage);
            }

            var outputFile = new FileInfo(outputPath);
            if (!outputFile.Exists || outputFile.Length == 0)
            {
                return FailWithOutput($"hdiffz reported success but the {label} diff '{outputPath}' is missing or empty.", arguments, hdiffzOutput, out errorMessage);
            }

            if (!string.IsNullOrEmpty(hdiffzOutput))
            {
                Debug.Log($"{LogPrefix} hdiffz {label} output:\n{hdiffzOutput}");
            }

            errorMessage = null;
            return true;
        }

        private static string ReadOutput(StringBuilder output)
        {
            lock (output)
            {
                return output.ToString().Trim();
            }
        }

        private bool Fail(string message, out string errorMessage)
        {
            errorMessage = message;
            Debug.LogError($"{LogPrefix} {message}", this);
            return false;
        }

        private bool FailWithOutput(string message, string arguments, string hdiffzOutput, out string errorMessage)
        {
            string outputText = string.IsNullOrEmpty(hdiffzOutput) ? "(no output)" : hdiffzOutput;
            Debug.LogError($"{LogPrefix} {message}\nCommand: hdiffz {arguments}\nhdiffz output:\n{outputText}", this);

            errorMessage = message;
            if (!string.IsNullOrEmpty(hdiffzOutput))
            {
                string shortOutput = hdiffzOutput.Length > MaxOutputInErrorMessage
                    ? "..." + hdiffzOutput.Substring(hdiffzOutput.Length - MaxOutputInErrorMessage)
                    : hdiffzOutput;
                errorMessage += "\n\nhdiffz output:\n" + shortOutput;
            }

            return false;
        }

        /// <summary>
        /// Returns the absolute path of the bundled hdiffz binary for the current editor platform,
        /// or null when the platform is not supported.
        /// </summary>
        private static string GetHdiffzExecutablePath()
        {
            string basePackagePath = Path.Combine("Packages", "net.pawlygon.unitytools", "hdiff", "hdiffz");

            string relativePath = Application.platform switch
            {
                RuntimePlatform.WindowsEditor => Path.Combine(basePackagePath, "Windows", "hdiffz.exe"),
                RuntimePlatform.OSXEditor => Path.Combine(basePackagePath, "Mac", "hdiffz"),
                RuntimePlatform.LinuxEditor => Path.Combine(basePackagePath, "Linux", "hdiffz"),
                _ => null
            };

            return relativePath != null ? Path.GetFullPath(relativePath) : null;
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
                Debug.LogWarning($"{LogPrefix} The selected model is not an FBX file: {modelPath}");
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
            return GetDiffBaseName(originalFbxPath);
        }

        /// <summary>
        /// Returns the diff base name for an original FBX path: the file name without extension,
        /// with spaces replaced by underscores. Two FBXs can therefore map to the same base name
        /// (e.g. "My Avatar.fbx" and "My_Avatar.fbx").
        /// </summary>
        public static string GetDiffBaseName(string originalFbxPath)
        {
            if (string.IsNullOrEmpty(originalFbxPath)) return null;
            return Path.GetFileNameWithoutExtension(originalFbxPath).Replace(" ", "_");
        }

        /// <summary>File name of the FBX diff for the given base name.</summary>
        public static string GetFbxDiffFileName(string baseName)
        {
            return baseName + ".hdiff";
        }

        /// <summary>File name of the .meta diff for the given base name.</summary>
        public static string GetMetaDiffFileName(string baseName)
        {
            return baseName + "Meta.hdiff";
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
                Debug.LogError($"{LogPrefix} Error while setting executable permission: {exception}");
                return false;
            }
        }
    }
}
