using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step 4: optional helpers. Adds the Pawlygon VRCFT prefab to the copied prefabs, imports PatcherHub and
    /// writes the PatcherHub patch configs that are still missing. Also hosts the "Import Latest PatcherHub" menu item.
    /// </summary>
    public partial class AvatarSetupWizard
    {
        private const string ImportPatcherHubMenuPath = "!Pawlygon/Import Latest PatcherHub";

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

        // =====================================================================
        // Drawing
        // =====================================================================

        private void DrawPrefabsStep()
        {
            EditorGUILayout.LabelField(
                "Optional helpers for your avatars. You can skip both and come back to this step later.",
                PawlygonEditorUI.SubLabelStyle);
            EditorGUILayout.Space(SectionSpacing);

            DrawVrcftCard();
            EditorGUILayout.Space(SectionSpacing);
            DrawPatcherHubCard();
        }

        private void DrawVrcftCard()
        {
            int presentCount = avatarEntries.Where((entry, index) => IsVrcftOnEntry(index)).Count();
            bool allPresent = presentCount == avatarEntries.Count;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Pawlygon VRCFT prefab", PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();

                    if (!cachedVrcftAvailable)
                    {
                        PawlygonEditorUI.DrawBadge("Not installed", PawlygonEditorUI.BadgeKind.Neutral, "The Pawlygon - VRC Facetracking package isn't in this project.");
                    }
                    else if (allPresent)
                    {
                        PawlygonEditorUI.DrawBadge("Done", PawlygonEditorUI.BadgeKind.Ok, "Every prefab already has the VRCFT setup.");
                    }
                    else if (presentCount > 0)
                    {
                        PawlygonEditorUI.DrawBadge($"{presentCount}/{avatarEntries.Count} added", PawlygonEditorUI.BadgeKind.Warning, "Some prefabs don't have the VRCFT setup yet.");
                    }
                    else
                    {
                        PawlygonEditorUI.DrawBadge("Not added", PawlygonEditorUI.BadgeKind.Neutral, "The prefabs don't have the VRCFT setup yet.");
                    }
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "Adds the Pawlygon VRC Face Tracking setup (face tracking animations, parameters and menu) to each copied prefab.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (!cachedVrcftAvailable)
                    {
                        EditorGUILayout.LabelField($"Install 'Pawlygon - VRC Facetracking' with the VRChat Creator Companion ({VrcftPackageListingUrl}), then check again.",
                            PawlygonEditorUI.RichMiniLabelStyle);

                        if (GUILayout.Button(new GUIContent("Open Listing", "Open the Pawlygon VCC listing in your browser."), GUILayout.Width(96f)))
                        {
                            Application.OpenURL(VrcftPackageListingUrl);
                        }

                        if (GUILayout.Button(new GUIContent("Check Again", "Look for the package again after installing it."), GUILayout.Width(96f)))
                        {
                            InvalidateProjectCaches();
                            GUIUtility.ExitGUI();
                        }
                    }
                    else if (allPresent)
                    {
                        EditorGUILayout.LabelField("Already added to every prefab.", PawlygonEditorUI.RichMiniLabelStyle);
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                        string label = presentCount > 0 ? $"Add to the Other {Plural(avatarEntries.Count - presentCount, "Prefab")}" : "Add to Prefabs";
                        if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent(label, "Adds the VRCFT prefab under '!Pawlygon - VRCFT' in each copied prefab."), 24f, GUILayout.MinWidth(160f)))
                        {
                            AddVrcftSetupToPrefabs(cachedVrcftPrefabPath);
                            // Saves prefabs and refreshes the AssetDatabase.
                            GUIUtility.ExitGUI();
                        }
                    }
                }
            }
        }

        private void DrawPatcherHubCard()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("PatcherHub", PawlygonEditorUI.SectionTitleStyle);
                    GUILayout.FlexibleSpace();

                    if (cachedPatcherHubAvailable)
                    {
                        PawlygonEditorUI.DrawBadge("Done", PawlygonEditorUI.BadgeKind.Ok, "PatcherHub is installed in this project.");
                    }
                    else
                    {
                        PawlygonEditorUI.DrawBadge("Not installed", PawlygonEditorUI.BadgeKind.Neutral, "PatcherHub isn't in this project yet.");
                    }
                }

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(
                    "PatcherHub is what your customers run to apply the face tracking patch to their copy of the avatar. " +
                    "With it installed, the wizard also writes a patch config for each avatar.",
                    PawlygonEditorUI.SubLabelStyle);
                EditorGUILayout.Space(6f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    var content = cachedPatcherHubAvailable
                        ? new GUIContent("Re-import Latest PatcherHub", "Download the latest PatcherHub release from GitHub and import it again.")
                        : new GUIContent("Import Latest PatcherHub", "Download the latest PatcherHub release from GitHub and import it.");
                    if (PawlygonEditorUI.DrawSecondaryButton(content, 24f, GUILayout.MinWidth(200f)))
                    {
                        ImportLatestPatcherHub();
                        // Shows progress bars and dialogs, then imports a package.
                        GUIUtility.ExitGUI();
                    }
                }

                DrawMissingPatchConfigsNotice();
            }
        }

        /// <summary>
        /// Patch configs are normally written during diff generation, which only happens if
        /// PatcherHub was already installed at that point. When it was imported later (in Prefabs or
        /// via the menu), offer to build the missing configs from the existing diff files.
        /// </summary>
        private void DrawMissingPatchConfigsNotice()
        {
            int missingCount = cachedEntriesMissingPatchConfig.Count;
            if (missingCount == 0)
            {
                return;
            }

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(18f));
                EditorGUILayout.LabelField(
                    $"{Plural(missingCount, "avatar has", "avatars have")} patch files but no PatcherHub patch config yet.",
                    PawlygonEditorUI.RichLabelStyle);

                if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Generate Patch Configs", "Write the missing PatcherHub configs from the existing patch files."),
                        22f, GUILayout.Width(170f)))
                {
                    GenerateMissingPatchConfigs();
                    // Creates assets and saves the AssetDatabase, which invalidates the current layout pass.
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void DrawPrefabsActions()
        {
            DrawBackButton(WizardStep.SelectMeshes);
            GUILayout.FlexibleSpace();

            if (PawlygonEditorUI.DrawPrimaryButton(new GUIContent("Continue", "Check the FX controllers next."), ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
            {
                GoToStep(WizardStep.FXCheck);
                GUIUtility.ExitGUI();
            }
        }

        // =====================================================================
        // Patch configs
        // =====================================================================

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
            string lastConfigPath = null;

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
                    lastConfigPath = configPath;
                }
            }

            InvalidateProjectCaches();

            if (failedNames.Count > 0)
            {
                status.Error($"Generated {Plural(createdCount, "patch config")}, but couldn't generate one for {string.Join(", ", failedNames)}. See the Console for details.");
            }
            else
            {
                status.Info($"Generated {Plural(createdCount, "PatcherHub patch config")}.", "Ping",
                    () => PingAssetPath(lastConfigPath));
            }

            Repaint();
        }

        // =====================================================================
        // VRCFT
        // =====================================================================

        private static bool IsVrcftPackageAvailable(out string prefabAssetPath)
        {
            prefabAssetPath = AssetDatabase.GUIDToAssetPath(VrcftPrefabGuid);
            return !string.IsNullOrEmpty(prefabAssetPath) && AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPath) != null;
        }

        /// <summary>True when <paramref name="prefabRoot"/> has an instance of the VRCFT prefab under its "!Pawlygon - VRCFT" child.</summary>
        private static bool HasVrcftSetup(GameObject prefabRoot, GameObject vrcftPrefabAsset)
        {
            if (prefabRoot == null || vrcftPrefabAsset == null)
            {
                return false;
            }

            Transform container = prefabRoot.transform.Find(VrcftContainerName);
            if (container == null)
            {
                return false;
            }

            foreach (Transform child in container)
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject) == vrcftPrefabAsset)
                {
                    return true;
                }
            }

            return false;
        }

        private void AddVrcftSetupToPrefabs(string vrcftPrefabPath)
        {
            GameObject vrcftPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(vrcftPrefabPath);
            if (vrcftPrefabAsset == null)
            {
                status.Error("The VRCFT prefab couldn't be loaded from the installed package. Try reinstalling it.");
                return;
            }

            int addedCount = 0;
            int alreadyConfiguredCount = 0;

            try
            {
                for (int i = 0; i < avatarEntries.Count; i++)
                {
                    AvatarEntry entry = avatarEntries[i];
                    EditorUtility.DisplayProgressBar(ProgressTitle, $"Adding VRCFT to '{GetEntryDisplayName(entry)}'…", (float)i / avatarEntries.Count);
                    GameObject prefabRoot = PrefabUtility.LoadPrefabContents(entry.copiedPrefabPath);

                    try
                    {
                        if (HasVrcftSetup(prefabRoot, vrcftPrefabAsset))
                        {
                            alreadyConfiguredCount++;
                            continue;
                        }

                        Transform container = prefabRoot.transform.Find(VrcftContainerName);
                        if (container == null)
                        {
                            var containerObject = new GameObject(VrcftContainerName);
                            containerObject.transform.SetParent(prefabRoot.transform, false);
                            container = containerObject.transform;
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

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            InvalidateProjectCaches();
            string message = $"Added the VRCFT setup to {Plural(addedCount, "prefab")}";
            message += alreadyConfiguredCount > 0 ? $"; {alreadyConfiguredCount} already had it." : ".";
            status.Info(message, "Ping", () => PingAssetPath(avatarEntries[0].copiedPrefabPath));
        }

        // =====================================================================
        // PatcherHub
        // =====================================================================

        private void ImportLatestPatcherHub()
        {
            bool imported = DownloadAndImportLatestPatcherHub(out string resultMessage, out bool cancelled);
            InvalidateProjectCaches();

            if (imported)
            {
                status.Info(resultMessage + " If patch configs are missing, generate them below once the import finishes.");
            }
            else if (cancelled)
            {
                status.Info(resultMessage);
            }
            else
            {
                status.Error(resultMessage);
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
