using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Pawlygon.UnityTools.Editor
{
    public partial class AvatarSetupWizard
    {
        [Serializable]
        private class GitHubReleaseInfo
        {
            public string tag_name;
            public string name;
            public GitHubReleaseAsset[] assets;
        }

        [Serializable]
        private class GitHubReleaseAsset
        {
            public string name;
            public string browser_download_url;
        }

        private const string ImportPatcherHubMenuPath = "!Pawlygon/Import Latest PatcherHub";

        [MenuItem(ImportPatcherHubMenuPath, validate = true)]
        private static bool ValidateImportPatcherHub()
        {
            Menu.SetChecked(ImportPatcherHubMenuPath, FTPatchConfigGenerator.IsPatcherHubAvailable());
            return true;
        }

        [MenuItem(ImportPatcherHubMenuPath, priority = 200)]
        private static void ImportPatcherHubMenuItem()
        {
            if (FTPatchConfigGenerator.IsPatcherHubAvailable())
            {
                bool reimport = EditorUtility.DisplayDialog(
                    "PatcherHub Already Installed",
                    "PatcherHub is already installed in this project. Do you want to re-import the latest version?",
                    "Re-import", "Cancel");
                if (!reimport) return;
            }

            if (!DownloadAndImportLatestPatcherHub(out string resultMessage, out bool cancelled) && !cancelled)
            {
                EditorUtility.DisplayDialog("Import Failed", resultMessage, "OK");
            }
        }

        private void DrawPrefabsStep()
        {
            bool isVrcftAvailable = IsVrcftPackageAvailable(out string vrcftPrefabPath);

            PawlygonEditorUI.DrawSection(
                "Prefabs",
                "Optional tools for adding prefab helpers and distributing patch assets.",
                () =>
                {
                    DrawVrcftPrefabBlock(isVrcftAvailable, vrcftPrefabPath);
                    EditorGUILayout.Space(SectionSpacing);
                    DrawPatcherHubBlock();
                    EditorGUILayout.Space(SectionSpacing);

                    if (PawlygonEditorUI.DrawPrimaryButton("Continue", 34f))
                    {
                        currentStep = WizardStep.FXCheck;
                        statusMessage = string.Empty;
                        GUIUtility.ExitGUI();
                    }
                });
        }

        private void DrawVrcftPrefabBlock(bool isVrcftAvailable, string vrcftPrefabPath)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("Pawlygon VRCFT", boldLabel13);
                EditorGUILayout.Space(2f);

                if (isVrcftAvailable)
                {
                    EditorGUILayout.LabelField("Package detected. Add the VRCFT setup to each generated prefab.", PawlygonEditorUI.SubLabelStyle);
                    EditorGUILayout.Space(8f);

                    if (PawlygonEditorUI.DrawPrimaryButton("Add VRCFT To Prefabs", 32f))
                    {
                        AddVrcftSetupToPrefabs(vrcftPrefabPath);
                        // Saves prefabs and refreshes the AssetDatabase.
                        GUIUtility.ExitGUI();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox($"Install Pawlygon - VRC Facetracking from VCC at {VrcftPackageListingUrl}. Once installed, this wizard can auto-add the VRCFT setup for you.", MessageType.Info);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Refresh", GUILayout.Height(28f)))
                        {
                            bool refreshedAvailability = IsVrcftPackageAvailable(out _);
                            vrcftSetupStatusMessage = refreshedAvailability
                                ? "Pawlygon VRCFT package detected. You can now add the setup to the generated prefabs."
                                : "Pawlygon VRCFT package is still not available in this project.";
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(vrcftSetupStatusMessage))
                {
                    EditorGUILayout.Space(6f);
                    EditorGUILayout.HelpBox(vrcftSetupStatusMessage, MessageType.None);
                }
            }
        }

        private void DrawPatcherHubBlock()
        {
            bool isPatcherHubInstalled = FTPatchConfigGenerator.IsPatcherHubAvailable();

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField("PatcherHub", boldLabel13);
                EditorGUILayout.Space(2f);

                if (isPatcherHubInstalled)
                {
                    EditorGUILayout.LabelField("PatcherHub detected. You can re-import to update to the latest version.", PawlygonEditorUI.SubLabelStyle);
                }
                else
                {
                    EditorGUILayout.LabelField("Import the latest PatcherHub unitypackage so end users can patch the face-tracking changes onto the avatar FBX model.", PawlygonEditorUI.SubLabelStyle);
                }

                EditorGUILayout.Space(8f);

                if (PawlygonEditorUI.DrawPrimaryButton(isPatcherHubInstalled ? "Re-import Latest PatcherHub" : "Import Latest PatcherHub", 32f))
                {
                    ImportLatestPatcherHub();
                    // Shows progress bars and dialogs, then imports a package.
                    GUIUtility.ExitGUI();
                }

                if (patcherHubImportedThisSession || !string.IsNullOrEmpty(patcherHubImportStatusMessage))
                {
                    EditorGUILayout.Space(6f);
                    EditorGUILayout.HelpBox(patcherHubImportStatusMessage, MessageType.None);
                }

                if (isPatcherHubInstalled)
                {
                    DrawMissingPatchConfigsNotice();
                }
            }
        }

        /// <summary>
        /// Patch configs are normally written during diff generation, which only happens if
        /// PatcherHub was already installed at that point. When it was imported later (step 4 or
        /// via the menu), offer to build the missing configs from the existing diff files.
        /// </summary>
        private void DrawMissingPatchConfigsNotice()
        {
            if (!FTPatchConfigGenerator.IsPatcherHubAvailable())
            {
                return;
            }

            int missingCount = GetEntriesMissingPatchConfig().Count;
            if (missingCount == 0)
            {
                return;
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                $"{missingCount} avatar entr{(missingCount == 1 ? "y has" : "ies have")} diff files but no PatcherHub patch config yet.",
                MessageType.Warning);

            if (PawlygonEditorUI.DrawPrimaryButton("Generate Patch Configs", 30f))
            {
                GenerateMissingPatchConfigs();
                // Creates assets and saves the AssetDatabase, which invalidates the current layout pass.
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// Returns the entries whose diff files were generated successfully and exist on disk but
        /// whose PatcherHub config asset does not exist yet.
        /// </summary>
        private List<AvatarEntry> GetEntriesMissingPatchConfig()
        {
            var result = new List<AvatarEntry>();

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (entry.diffGenerationFailed ||
                    string.IsNullOrEmpty(entry.avatarRootPath) ||
                    string.IsNullOrEmpty(entry.diffGeneratorAssetPath))
                {
                    continue;
                }

                FTDiffGenerator diffGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath);
                string baseName = diffGenerator != null ? diffGenerator.GetBaseName() : null;
                if (string.IsNullOrEmpty(baseName))
                {
                    continue;
                }

                string fbxDiffPath = FTDiffGenerator.GetFbxDiffAssetPath(entry.avatarRootPath, baseName);
                string metaDiffPath = FTDiffGenerator.GetMetaDiffAssetPath(entry.avatarRootPath, baseName);
                if (!File.Exists(ToAbsolutePath(fbxDiffPath)) || !File.Exists(ToAbsolutePath(metaDiffPath)))
                {
                    continue;
                }

                if (!File.Exists(ToAbsolutePath(GetPatchConfigAssetPath(entry, entry.avatarRootPath))))
                {
                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>
        /// Builds the PatcherHub configs for every entry returned by
        /// <see cref="GetEntriesMissingPatchConfig"/>, using the same context as diff generation.
        /// </summary>
        private void GenerateMissingPatchConfigs()
        {
            int createdCount = 0;
            var failedNames = new List<string>();

            foreach (AvatarEntry entry in GetEntriesMissingPatchConfig())
            {
                FTDiffGenerator diffGenerator = AssetDatabase.LoadAssetAtPath<FTDiffGenerator>(entry.diffGeneratorAssetPath);
                FTPatchConfigGenerator.ConfigContext configContext = BuildPatchConfigContext(entry, diffGenerator);
                string configPath = configContext != null ? FTPatchConfigGenerator.GenerateConfig(configContext) : null;

                if (string.IsNullOrEmpty(configPath))
                {
                    failedNames.Add(GetEntryDisplayName(entry));
                }
                else
                {
                    createdCount++;
                }
            }

            statusMessage = $"Generated {createdCount} PatcherHub patch config{(createdCount == 1 ? string.Empty : "s")}.";

            if (failedNames.Count > 0)
            {
                statusMessage += $" Could not generate a config for: {string.Join(", ", failedNames)}. See the Console for details.";
                EditorUtility.DisplayDialog("Patch Config Generation Failed",
                    $"Could not generate a PatcherHub patch config for:\n\n{string.Join("\n", failedNames)}\n\nSee the Console for details.", "OK");
            }

            Repaint();
        }

        private bool IsVrcftPackageAvailable(out string prefabAssetPath)
        {
            prefabAssetPath = AssetDatabase.GUIDToAssetPath(VrcftPrefabGuid);
            return !string.IsNullOrEmpty(prefabAssetPath) && AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) != null;
        }

        private void AddVrcftSetupToPrefabs(string vrcftPrefabPath)
        {
            GameObject vrcftPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(vrcftPrefabPath);
            if (vrcftPrefabAsset == null)
            {
                vrcftSetupStatusMessage = "The VRCFT prefab could not be loaded from the installed package.";
                return;
            }

            int addedCount = 0;
            int alreadyConfiguredCount = 0;

            foreach (AvatarEntry entry in avatarEntries)
            {
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);

                try
                {
                    Transform container = prefabRoot.transform.Find("!Pawlygon - VRCFT");
                    if (container == null)
                    {
                        var containerObject = new GameObject("!Pawlygon - VRCFT");
                        containerObject.transform.SetParent(prefabRoot.transform, false);
                        container = containerObject.transform;
                    }

                    bool hasExistingSetup = false;
                    foreach (Transform child in container)
                    {
                        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject);
                        if (source == vrcftPrefabAsset)
                        {
                            hasExistingSetup = true;
                            break;
                        }
                    }

                    if (hasExistingSetup)
                    {
                        alreadyConfiguredCount++;
                        continue;
                    }

                    GameObject instance = PrefabUtility.InstantiatePrefab(vrcftPrefabAsset) as GameObject;
                    if (instance != null)
                    {
                        instance.transform.SetParent(container, false);
                        addedCount++;
                    }

                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, entry.copiedPrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }

            vrcftSetupStatusMessage = $"Added VRCFT setup to {addedCount} prefab{(addedCount == 1 ? string.Empty : "s")}";
            if (alreadyConfiguredCount > 0)
            {
                vrcftSetupStatusMessage += $", already present on {alreadyConfiguredCount}.";
            }
            else
            {
                vrcftSetupStatusMessage += ".";
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        private void ImportLatestPatcherHub()
        {
            bool imported = DownloadAndImportLatestPatcherHub(out string resultMessage, out bool cancelled);
            patcherHubImportStatusMessage = resultMessage;

            if (imported)
            {
                patcherHubImportedThisSession = true;
            }
            else if (!cancelled)
            {
                EditorUtility.DisplayDialog("Import Failed", resultMessage, "OK");
            }

            Repaint();
        }

        /// <summary>
        /// Downloads the latest PatcherHub .unitypackage from GitHub and imports it. Every network
        /// request has a timeout and shows a cancelable progress bar, so a stalled connection can
        /// never hang the editor. Returns false with a user-facing message on failure or cancel.
        /// </summary>
        private static bool DownloadAndImportLatestPatcherHub(out string resultMessage, out bool cancelled)
        {
            cancelled = false;

            try
            {
                GitHubReleaseInfo releaseInfo = FetchLatestPatcherHubReleaseInfo();
                GitHubReleaseAsset unityPackageAsset = releaseInfo.assets?.FirstOrDefault(asset =>
                    !string.IsNullOrEmpty(asset.name) &&
                    asset.name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(asset.browser_download_url));

                if (unityPackageAsset == null)
                {
                    resultMessage = "Could not find a .unitypackage asset in the latest PatcherHub release.";
                    Debug.LogError($"[AvatarSetupWizard] {resultMessage}");
                    return false;
                }

                string downloadPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(unityPackageAsset.name));
                DownloadFile(unityPackageAsset.browser_download_url, downloadPath, unityPackageAsset.name);
                AssetDatabase.ImportPackage(downloadPath, false);

                resultMessage = $"Imported {unityPackageAsset.name} from {releaseInfo.tag_name}.";
                Debug.Log($"[AvatarSetupWizard] {resultMessage}");
                return true;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                resultMessage = "PatcherHub import was cancelled.";
                return false;
            }
            catch (Exception ex)
            {
                resultMessage = $"Failed to import PatcherHub: {ex.Message}";
                Debug.LogError($"[AvatarSetupWizard] {resultMessage}");
                return false;
            }
        }

        private static GitHubReleaseInfo FetchLatestPatcherHubReleaseInfo()
        {
            using var request = UnityWebRequest.Get(PatcherHubLatestReleaseApiUrl);
            request.SetRequestHeader("User-Agent", "PawlygonUnityTools");
            SendWebRequestWithProgress(request, "Checking the latest PatcherHub release", PatcherHubReleaseInfoTimeoutSeconds);

            GitHubReleaseInfo releaseInfo = JsonUtility.FromJson<GitHubReleaseInfo>(request.downloadHandler.text);
            if (releaseInfo == null)
            {
                throw new InvalidOperationException("GitHub returned an invalid release payload.");
            }

            return releaseInfo;
        }

        private static void DownloadFile(string url, string destinationPath, string displayName)
        {
            bool succeeded = false;

            try
            {
                // Dispose the request (and with it the file handle) before any cleanup below.
                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("User-Agent", "PawlygonUnityTools");
                    request.downloadHandler = new DownloadHandlerFile(destinationPath) { removeFileOnAbort = true };
                    SendWebRequestWithProgress(request, $"Downloading {displayName}", PatcherHubDownloadTimeoutSeconds);
                }

                succeeded = true;
            }
            finally
            {
                if (!succeeded)
                {
                    TryDeleteFile(destinationPath);
                }
            }
        }

        /// <summary>
        /// Sends a request and polls it while showing a cancelable progress bar. The transfer runs
        /// on Unity's background transport; <see cref="UnityWebRequest.timeout"/> bounds the total
        /// time so a stalled connection fails instead of hanging. Throws
        /// <see cref="OperationCanceledException"/> when the user cancels and
        /// <see cref="InvalidOperationException"/> when the request fails.
        /// </summary>
        private static void SendWebRequestWithProgress(UnityWebRequest request, string description, int timeoutSeconds)
        {
            request.timeout = timeoutSeconds;
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            try
            {
                while (!operation.isDone)
                {
                    float progress = Mathf.Clamp01(request.downloadProgress);
                    string info = $"{description}... {request.downloadedBytes / 1024} KB";

                    if (EditorUtility.DisplayCancelableProgressBar("PatcherHub", info, progress))
                    {
                        request.Abort();
                        throw new OperationCanceledException($"{description} was cancelled.");
                    }

                    System.Threading.Thread.Sleep(50);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                string httpStatus = request.responseCode > 0 ? $" (HTTP {request.responseCode})" : string.Empty;
                throw new InvalidOperationException($"{description} failed: {request.error}{httpStatus}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AvatarSetupWizard] Could not delete incomplete download '{path}': {ex.Message}");
            }
        }
    }
}
