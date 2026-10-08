using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Step 1: the source avatars and target folders, their validation, and creating the avatar structure
    /// (copied FBX and prefab, working scene, diff generator asset).
    /// </summary>
    public partial class AvatarSetupWizard
    {
        // Validation snapshot, refreshed on Layout events so typing never adds or removes rows mid-event.
        private string setupFolderError = string.Empty;
        private bool showSetupFolderError;
        private string[] setupRowErrors = Array.Empty<string>();
        private bool[] showSetupRowErrors = Array.Empty<bool>();
        private string setupAvatarsError = string.Empty;
        private List<string> setupExistingRoots = new List<string>();

        // Errors appear only for fields the user has edited, or for everything after a click on Create.
        private bool touchedFolderFields;
        private readonly HashSet<AvatarEntry> touchedEntries = new HashSet<AvatarEntry>();
        private bool createAttempted;

        private void ResetSetupValidation()
        {
            touchedFolderFields = false;
            touchedEntries.Clear();
            createAttempted = false;
        }

        private void RefreshSetupSnapshot()
        {
            // Separate folders only exist with several avatars; normalised here so both passes agree.
            if (avatarEntries.Count <= 1 && !IsStructureCreated)
            {
                useSeparateFolderPerAvatar = false;
            }

            setupFolderError = GetFolderValidationMessage();
            showSetupFolderError = createAttempted || touchedFolderFields;

            setupRowErrors = new string[avatarEntries.Count];
            showSetupRowErrors = new bool[avatarEntries.Count];
            for (int i = 0; i < avatarEntries.Count; i++)
            {
                setupRowErrors[i] = GetEntryValidationMessage(avatarEntries[i]);
                showSetupRowErrors[i] = createAttempted || touchedEntries.Contains(avatarEntries[i]);
            }

            // Name clashes between avatars only make sense once every field is valid.
            bool fieldsValid = string.IsNullOrEmpty(setupFolderError) && setupRowErrors.All(string.IsNullOrEmpty);
            setupAvatarsError = fieldsValid ? GetAvatarsValidationMessage() : string.Empty;

            setupExistingRoots = IsStructureCreated
                ? new List<string>()
                : GetPlannedAvatarRootPaths().Where(AssetDatabase.IsValidFolder).ToList();
        }

        // =====================================================================
        // Drawing
        // =====================================================================

        private void DrawSetupStep()
        {
            bool locked = IsStructureCreated;

            if (locked)
            {
                EditorGUILayout.HelpBox(
                    "The avatar structure has been created, so these inputs are locked to keep matching the files. " +
                    "To set up other avatars, use Start Over (it can keep these inputs).",
                    MessageType.Info);
                EditorGUILayout.Space(SectionSpacing);
            }

            using (new EditorGUI.DisabledScope(locked))
            {
                DrawFolderSection();
                EditorGUILayout.Space(SectionSpacing);
                DrawAvatarsSection();
            }

            if (setupExistingRoots.Count > 0)
            {
                EditorGUILayout.Space(SectionSpacing);
                DrawExistingSetupNotice();
            }
        }

        private void DrawFolderSection()
        {
            bool hasMultipleEntries = avatarEntries.Count > 1;

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader("Where to save", "Everything the wizard creates goes into a folder per avatar inside Assets.");

                EditorGUI.BeginChangeCheck();
                mainFolderName = EditorGUILayout.TextField(
                    new GUIContent("Main Folder", "Folder inside Assets that holds all your avatar setups."), mainFolderName);

                if (!useSeparateFolderPerAvatar)
                {
                    sharedAvatarFolderName = EditorGUILayout.TextField(
                        new GUIContent(hasMultipleEntries ? "Shared Avatar Folder" : "Avatar Folder",
                            "Folder for this avatar's files, inside the main folder. Filled in from the FBX name."),
                        sharedAvatarFolderName);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    touchedFolderFields = true;
                }

                if (hasMultipleEntries)
                {
                    EditorGUI.BeginChangeCheck();
                    bool separate = EditorGUILayout.ToggleLeft(
                        new GUIContent("Use a separate folder per avatar",
                            "Each avatar gets its own folder, scene and patch. Off: all avatars share one folder and one working scene."),
                        useSeparateFolderPerAvatar);
                    if (EditorGUI.EndChangeCheck())
                    {
                        useSeparateFolderPerAvatar = separate;
                        // Adds or removes the folder fields.
                        GUIUtility.ExitGUI();
                    }
                }

                if (!useSeparateFolderPerAvatar)
                {
                    EditorGUILayout.LabelField(DescribePlannedRoot(GetPlannedAvatarRootPath(avatarEntries[0])), PawlygonEditorUI.SubLabelStyle);
                }

                if (showSetupFolderError && !string.IsNullOrEmpty(setupFolderError))
                {
                    DrawFieldError(setupFolderError);
                }
            }
        }

        private void DrawAvatarsSection()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader("Avatars",
                    "The original FBX and prefab of each avatar. The wizard works on copies; your originals stay unchanged.");

                for (int i = 0; i < avatarEntries.Count; i++)
                {
                    DrawAvatarEntryEditor(i, avatarEntries[i]);
                    EditorGUILayout.Space(4f);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("Add Avatar", "Set up several avatars (e.g. PC and Quest versions) in one go."), GUILayout.Width(100f)))
                    {
                        avatarEntries.Add(new AvatarEntry());
                        // The new entry adds controls mid-event; restart the layout pass.
                        GUIUtility.ExitGUI();
                    }

                    GUILayout.FlexibleSpace();
                    GUILayout.Label(Plural(avatarEntries.Count, "avatar"), mutedMiniStyle);
                }

                if (!string.IsNullOrEmpty(setupAvatarsError))
                {
                    DrawFieldError(setupAvatarsError);
                }
            }
        }

        private void DrawAvatarEntryEditor(int index, AvatarEntry entry)
        {
            using (new EditorGUILayout.VerticalScope(cardStyle))
            {
                if (avatarEntries.Count > 1)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Avatar {index + 1}", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();

                        if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(64f)))
                        {
                            touchedEntries.Remove(entry);
                            avatarEntries.RemoveAt(index);
                            selectedEntryIndex = Mathf.Clamp(selectedEntryIndex, 0, avatarEntries.Count - 1);
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                EditorGUI.BeginChangeCheck();
                GameObject fbx = DrawFilteredAssetField(
                    new GUIContent("Source FBX", "The avatar's original FBX model (before your face tracking edit)."),
                    entry.sourceFbx, "fbx", SourceFbxPickerControlId + index * 2);
                GameObject prefab = DrawFilteredAssetField(
                    new GUIContent("Source Prefab", "The avatar prefab that uses this FBX (the one with the VRC Avatar Descriptor)."),
                    entry.sourcePrefab, "prefab", SourcePrefabPickerControlId + index * 2);
                string folder = useSeparateFolderPerAvatar
                    ? EditorGUILayout.TextField(new GUIContent("Avatar Folder", "This avatar's folder inside the main folder. Filled in from the FBX name."), entry.avatarFolderName)
                    : entry.avatarFolderName;

                if (EditorGUI.EndChangeCheck())
                {
                    touchedEntries.Add(entry);
                    entry.sourcePrefab = prefab;
                    entry.avatarFolderName = folder;
                    if (fbx != entry.sourceFbx)
                    {
                        SetSourceFbx(entry, fbx);
                    }
                }

                if (useSeparateFolderPerAvatar)
                {
                    EditorGUILayout.LabelField(DescribePlannedRoot(GetPlannedAvatarRootPath(entry)), PawlygonEditorUI.SubLabelStyle);
                }

                if (index < setupRowErrors.Length && showSetupRowErrors[index] && !string.IsNullOrEmpty(setupRowErrors[index]))
                {
                    DrawFieldError(setupRowErrors[index]);
                }
            }
        }

        private string DescribePlannedRoot(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                return "Enter the folder names to see where the files go.";
            }

            if (IsStructureCreated)
            {
                return $"Saved in <b>{rootPath}</b>";
            }

            bool exists = setupExistingRoots.Contains(rootPath, StringComparer.OrdinalIgnoreCase);
            return exists
                ? $"Saves to <b>{rootPath}</b>   {PawlygonEditorUI.BadgeText("Already exists", PawlygonEditorUI.BadgeKind.Warning)}"
                : $"Saves to <b>{rootPath}</b>";
        }

        private void DrawExistingSetupNotice()
        {
            string folders = string.Join("\n", setupExistingRoots.Select(path => "• " + path));
            EditorGUILayout.HelpBox(
                $"These folders already exist from an earlier setup:\n{folders}\n\n" +
                "Resume continues that setup with the files that are there and opens the furthest step they allow. " +
                "Overwrite deletes the folders and starts fresh.",
                MessageType.Info);
        }

        private void DrawSetupActions()
        {
            if (IsStructureCreated)
            {
                if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Start Over…", "Forget this setup and start a new one. Created files stay in the project."), ActionButtonHeight))
                {
                    ConfirmStartOver();
                    GUIUtility.ExitGUI();
                }

                GUILayout.FlexibleSpace();

                if (PawlygonEditorUI.DrawPrimaryButton("Continue", ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
                {
                    GoToStep(WizardStep.WaitForImport);
                    GUIUtility.ExitGUI();
                }

                return;
            }

            bool hasExistingSetup = setupExistingRoots.Count > 0;

            if (hasExistingSetup &&
                PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Overwrite…", "Delete the existing folders (after a confirmation) and create everything again."),
                    ActionButtonHeight))
            {
                if (ValidateBeforeCreate())
                {
                    CreateAvatarStructures(overwriteConfirmed: false);
                }

                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            var primary = hasExistingSetup
                ? new GUIContent("Resume Setup", "Continue the earlier setup with the files in the existing folders. Nothing is deleted.")
                : new GUIContent("Create Avatar Structure", "Copy the FBX and prefab of each avatar into the folders above and create the working scene.");
            if (PawlygonEditorUI.DrawPrimaryButton(primary, ActionButtonHeight, GUILayout.MinWidth(PrimaryButtonMinWidth)))
            {
                if (ValidateBeforeCreate())
                {
                    if (hasExistingSetup) ResumeExistingSetup();
                    else CreateAvatarStructures(overwriteConfirmed: false);
                }

                // Creates assets and scenes, shows dialogs and changes the step.
                GUIUtility.ExitGUI();
            }
        }

        // =====================================================================
        // Inputs
        // =====================================================================

        /// <summary>
        /// Sets an entry's source FBX and fills in the folder names from the FBX name while they are empty,
        /// the old "Avatar Name" placeholder, or still the name taken from the previous FBX.
        /// </summary>
        private void SetSourceFbx(AvatarEntry entry, GameObject newFbx)
        {
            string previousName = entry.sourceFbx != null ? SanitizeFolderName(entry.sourceFbx.name) : null;
            entry.sourceFbx = newFbx;

            string newName = newFbx != null ? SanitizeFolderName(newFbx.name) : null;
            if (string.IsNullOrEmpty(newName))
            {
                return;
            }

            if (IsAutomaticFolderName(entry.avatarFolderName, previousName))
            {
                entry.avatarFolderName = newName;
            }

            if (avatarEntries.IndexOf(entry) == 0 && IsAutomaticFolderName(sharedAvatarFolderName, previousName))
            {
                sharedAvatarFolderName = newName;
            }
        }

        private static bool IsAutomaticFolderName(string current, string previousFbxName)
        {
            return string.IsNullOrWhiteSpace(current) ||
                   string.Equals(current.Trim(), PlaceholderAvatarName, StringComparison.Ordinal) ||
                   (!string.IsNullOrEmpty(previousFbxName) && string.Equals(current, previousFbxName, StringComparison.Ordinal));
        }

        private static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        }

        private GameObject DrawFilteredAssetField(GUIContent label, GameObject currentValue, string extension, int controlId)
        {
            Rect totalRect = EditorGUILayout.GetControlRect();
            Rect fieldRect = EditorGUI.PrefixLabel(totalRect, label);

            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && fieldRect.Contains(currentEvent.mousePosition) && GUI.enabled)
            {
                bool clickedPickerButton = currentEvent.mousePosition.x >= fieldRect.xMax - 19f;
                if (clickedPickerButton)
                {
                    EditorGUIUtility.ShowObjectPicker<GameObject>(currentValue, false, $"glob:\"*.{extension}\"", controlId);
                    currentEvent.Use();
                }
            }

            return (GameObject)EditorGUI.ObjectField(fieldRect, GUIContent.none, currentValue, typeof(GameObject), false);
        }

        private void HandleObjectPickerSelection()
        {
            Event currentEvent = Event.current;
            if (currentEvent.type != EventType.ExecuteCommand && currentEvent.type != EventType.ValidateCommand)
            {
                return;
            }

            if (currentEvent.commandName != "ObjectSelectorUpdated" && currentEvent.commandName != "ObjectSelectorClosed")
            {
                return;
            }

            if (currentStep != WizardStep.Setup || IsStructureCreated)
            {
                return;
            }

            int pickerControlId = EditorGUIUtility.GetObjectPickerControlID();
            UnityEngine.Object pickedObject = EditorGUIUtility.GetObjectPickerObject();
            if (pickedObject is not GameObject pickedGameObject)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(pickedGameObject);
            string extension = Path.GetExtension(assetPath);

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                AvatarEntry entry = avatarEntries[i];

                if (pickerControlId == SourceFbxPickerControlId + i * 2 && string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.sourceFbx != pickedGameObject)
                    {
                        touchedEntries.Add(entry);
                        SetSourceFbx(entry, pickedGameObject);
                    }

                    Repaint();
                    return;
                }

                if (pickerControlId == SourcePrefabPickerControlId + i * 2 && string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.sourcePrefab != pickedGameObject)
                    {
                        touchedEntries.Add(entry);
                        entry.sourcePrefab = pickedGameObject;
                    }

                    Repaint();
                    return;
                }
            }
        }

        // =====================================================================
        // Validation
        // =====================================================================

        /// <summary>The first problem with the inputs (prefixed with the avatar number), or empty when valid.</summary>
        private string GetSetupValidationMessage()
        {
            if (avatarEntries.Count == 0)
            {
                return "Add at least one avatar.";
            }

            string folderError = GetFolderValidationMessage();
            if (!string.IsNullOrEmpty(folderError))
            {
                return folderError;
            }

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                string entryError = GetEntryValidationMessage(avatarEntries[i]);
                if (!string.IsNullOrEmpty(entryError))
                {
                    return avatarEntries.Count > 1 ? $"Avatar {i + 1}: {entryError}" : entryError;
                }
            }

            return GetAvatarsValidationMessage();
        }

        /// <summary>Problems with the main folder and (in shared-folder mode) the avatar folder name.</summary>
        private string GetFolderValidationMessage()
        {
            if (string.IsNullOrWhiteSpace(mainFolderName))
            {
                return "Enter a main folder name.";
            }

            if (mainFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "The main folder name contains characters that can't be used in a folder name.";
            }

            if (!useSeparateFolderPerAvatar)
            {
                string folderError = GetFolderNameError(sharedAvatarFolderName);
                if (folderError != null)
                {
                    return folderError;
                }
            }

            return string.Empty;
        }

        private static string GetFolderNameError(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName) || string.Equals(folderName.Trim(), PlaceholderAvatarName, StringComparison.Ordinal))
            {
                return "Enter a name for the avatar folder (pick the source FBX to fill it in automatically).";
            }

            if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "The avatar folder name contains characters that can't be used in a folder name.";
            }

            return null;
        }

        /// <summary>Problems with one avatar's own fields, without the "Avatar n:" prefix.</summary>
        private string GetEntryValidationMessage(AvatarEntry entry)
        {
            if (entry.sourceFbx == null)
            {
                return "Pick the source FBX.";
            }

            if (entry.sourcePrefab == null)
            {
                return "Pick the source prefab.";
            }

            string fbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
            if (!string.Equals(Path.GetExtension(fbxPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return "The source FBX must be an .fbx model asset.";
            }

            string prefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);
            if (!string.Equals(Path.GetExtension(prefabPath), ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return "The source prefab must be a .prefab asset.";
            }

            if (useSeparateFolderPerAvatar)
            {
                string folderError = GetFolderNameError(entry.avatarFolderName);
                if (folderError != null)
                {
                    return folderError;
                }

                bool duplicate = avatarEntries.Any(other => other != entry &&
                    string.Equals(other.avatarFolderName?.Trim(), entry.avatarFolderName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (duplicate)
                {
                    return "Another avatar uses the same folder name. Each avatar needs its own folder.";
                }
            }

            // Recreating the structure deletes existing target folders, so a source living inside one of
            // them would be destroyed before it could be copied.
            foreach (string targetRoot in GetPlannedAvatarRootPaths())
            {
                if (IsAssetPathInsideFolder(fbxPath, targetRoot))
                {
                    return $"The source FBX is inside '{targetRoot}', which the setup creates (and may delete to start fresh). Move the source files elsewhere or pick another folder name.";
                }

                if (IsAssetPathInsideFolder(prefabPath, targetRoot))
                {
                    return $"The source prefab is inside '{targetRoot}', which the setup creates (and may delete to start fresh). Move the source files elsewhere or pick another folder name.";
                }
            }

            return string.Empty;
        }

        /// <summary>Clashes between avatars: files that would get the same name in a shared folder, or the same patch files.</summary>
        private string GetAvatarsValidationMessage()
        {
            if (!useSeparateFolderPerAvatar)
            {
                if (HasDuplicateSharedTargetNames(GetCopiedFbxFileName, avatarEntries.Select(entry => entry.sourceFbx)))
                {
                    return "Two avatars have FBX files with the same name, so their copies would overwrite each other in the shared folder. Rename one or use a separate folder per avatar.";
                }

                if (HasDuplicateSharedTargetNames(path => Path.GetFileName(path), avatarEntries.Select(entry => entry.sourcePrefab)))
                {
                    return "Two avatars have prefabs with the same name, so their copies would overwrite each other in the shared folder. Rename one or use a separate folder per avatar.";
                }

                if (HasDuplicateSharedTargetNames(path => $"{Path.GetFileNameWithoutExtension(path)} Face Tracking DiffGenerator.asset", avatarEntries.Select(entry => entry.sourceFbx)))
                {
                    return "Two avatars would get the same diff generator asset in the shared folder. Rename one of the FBX files or use a separate folder per avatar.";
                }
            }

            return GetPatchTargetConflictMessage();
        }

        /// <summary>
        /// Checks that no two entries would write the same PatcherHub config asset or the same
        /// .hdiff files. Diff file names replace spaces with underscores, so e.g. "My Avatar.fbx"
        /// and "My_Avatar.fbx" collide even though their copied FBX names differ. Expects every
        /// entry to have passed <see cref="GetEntryValidationMessage"/>.
        /// </summary>
        private string GetPatchTargetConflictMessage()
        {
            var configOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var diffOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                AvatarEntry entry = avatarEntries[i];
                string avatarRootPath = GetPlannedAvatarRootPath(entry);
                if (string.IsNullOrEmpty(avatarRootPath))
                {
                    continue;
                }

                string configPath = GetPatchConfigAssetPath(entry, avatarRootPath);
                if (configOwners.TryGetValue(configPath, out int configOwner))
                {
                    return $"Avatars {configOwner + 1} and {i + 1} would both write the PatcherHub config '{configPath}'. Rename one of the source FBXs or use separate folders.";
                }

                configOwners[configPath] = i;

                string diffBaseName = FTDiffGenerator.GetDiffBaseName(AssetDatabase.GetAssetPath(entry.sourceFbx));
                if (string.IsNullOrEmpty(diffBaseName))
                {
                    continue;
                }

                string diffPath = FTDiffGenerator.GetFbxDiffAssetPath(avatarRootPath, diffBaseName);
                if (diffOwners.TryGetValue(diffPath, out int diffOwner))
                {
                    return $"Avatars {diffOwner + 1} and {i + 1} would both write the diff file '{diffPath}' (spaces in FBX names become underscores in diff file names). Rename one of the source FBXs or use separate folders.";
                }

                diffOwners[diffPath] = i;
            }

            return string.Empty;
        }

        /// <summary>
        /// Returns the avatar root folder the setup will create for an entry with the current
        /// settings. This is also the folder that "Overwrite" deletes when it already exists.
        /// Returns null while the main or avatar folder name is still blank.
        /// </summary>
        private string GetPlannedAvatarRootPath(AvatarEntry entry)
        {
            string avatarFolderName = useSeparateFolderPerAvatar ? entry.avatarFolderName : sharedAvatarFolderName;
            if (string.IsNullOrWhiteSpace(mainFolderName) || string.IsNullOrWhiteSpace(avatarFolderName))
            {
                return null;
            }

            return PawlygonEditorUtils.CombineAssetPath("Assets", mainFolderName.Trim(), avatarFolderName.Trim());
        }

        /// <summary>
        /// Returns the distinct avatar root folders the setup will create (and delete on overwrite).
        /// </summary>
        private List<string> GetPlannedAvatarRootPaths()
        {
            return avatarEntries
                .Select(GetPlannedAvatarRootPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasDuplicateSharedTargetNames(Func<string, string> pathSelector, IEnumerable<GameObject> assets)
        {
            return assets
                .Where(asset => asset != null)
                .Select(asset => pathSelector(AssetDatabase.GetAssetPath(asset)))
                .Where(name => !string.IsNullOrEmpty(name))
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() > 1);
        }

        // =====================================================================
        // Creating the structure
        // =====================================================================

        /// <summary>Shows every validation error from now on; returns false (with a status error) when the inputs aren't valid.</summary>
        private bool ValidateBeforeCreate()
        {
            createAttempted = true;
            string validationMessage = GetSetupValidationMessage();
            if (string.IsNullOrEmpty(validationMessage))
            {
                return true;
            }

            status.Error($"Fix the marked fields first: {validationMessage}");
            return false;
        }

        /// <summary>
        /// Copies every avatar's FBX and prefab, creates the working scene(s) and diff generator assets, then
        /// moves to Import. Existing target folders are deleted after a confirmation.
        /// </summary>
        private void CreateAvatarStructures(bool overwriteConfirmed)
        {
            status.Clear();

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                status.Warning("Cancelled: the open scene has unsaved changes. Save or discard them so the working scene can be created.");
                return;
            }

            if (!ConfirmAndClearExistingTargets(overwriteConfirmed))
            {
                return;
            }

            bool created;
            isBuildingStructure = true;

            try
            {
                PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath("Assets", mainFolderName.Trim()));

                if (useSeparateFolderPerAvatar)
                {
                    created = true;
                    for (int i = 0; i < avatarEntries.Count && created; i++)
                    {
                        created = CreateSeparateAvatarStructure(avatarEntries[i], i, openAfterCreate: i == avatarEntries.Count - 1);
                    }
                }
                else
                {
                    created = CreateSharedAvatarStructure();
                }

                if (created)
                {
                    EditorUtility.DisplayProgressBar(ProgressTitle, "Saving assets…", 1f);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                isBuildingStructure = false;
            }

            if (!created)
            {
                if (!status.HasMessage || status.Type != MessageType.Error)
                {
                    status.Error("Creating the avatar structure failed. See the Console for details, then try again.");
                }

                Repaint();
                return;
            }

            foreach (AvatarEntry entry in avatarEntries)
            {
                ResetEntryProgress(entry);
            }

            selectedEntryIndex = 0;
            GoToStep(WizardStep.WaitForImport);

            string rootPath = avatarEntries[0].avatarRootPath;
            status.Info(
                $"Created {Plural(avatarEntries.Count, "avatar")}. Now replace each copied FBX with your edited version.",
                "Ping Folder", () => PingAssetPath(rootPath));
        }

        /// <summary>Forgets everything an entry did after Setup, and starts watching its copied FBX for the edited version.</summary>
        private static void ResetEntryProgress(AvatarEntry entry)
        {
            entry.watchedFbxWriteTimeUtcTicks = GetAssetWriteTimeUtcTicks(entry.copiedFbxPath);
            entry.watchedFbxFileSize = GetAssetFileSize(entry.copiedFbxPath);
            entry.hasImportedModifiedFbx = false;
            entry.needsProcessing = true;
            entry.diffGenerationFailed = false;
            entry.diffGenerationError = string.Empty;
            entry.isMeshReviewComplete = false;
            entry.reviewResultLabel = string.Empty;
            entry.animatorReplacement = new AnimatorReplacementState();
            entry.meshSelections = new List<MeshSelectionState>();
            entry.copiedFxControllerPath = string.Empty;
            entry.originalFxControllerPath = string.Empty;
            ResetEntryFXState(entry);
        }

        /// <summary>
        /// When target avatar folders already exist from a previous run, asks the user to confirm deleting
        /// them (unless <paramref name="alreadyConfirmed"/>) and deletes them. Returns false, with a status
        /// message, when the user cancels or a folder could not be removed.
        /// </summary>
        private bool ConfirmAndClearExistingTargets(bool alreadyConfirmed)
        {
            // Use the same root list as validation, which rejects any source asset inside these
            // folders, so the deletion below can never remove the assets being copied.
            List<string> existingRoots = GetPlannedAvatarRootPaths()
                .Where(AssetDatabase.IsValidFolder)
                .ToList();

            if (existingRoots.Count == 0)
            {
                return true;
            }

            if (!alreadyConfirmed)
            {
                string folderList = string.Join("\n", existingRoots.Select(path => "• " + path));
                bool overwrite = EditorUtility.DisplayDialog(
                    "Delete Existing Folders?",
                    $"These folders from an earlier setup already exist:\n\n{folderList}\n\n" +
                    "Overwriting deletes each folder and everything inside it (copied FBX, prefab, working scene, patch files " +
                    "and FX controller copies), then creates the structure again. This cannot be undone.",
                    "Delete and Recreate",
                    "Cancel");

                if (!overwrite)
                {
                    status.Info("Nothing was changed: the existing folders were left as they are.");
                    return false;
                }
            }

            // Release any scene that lives inside a folder we are about to delete so the asset
            // deletion is not blocked by an open scene. Modified scenes were already offered for
            // saving by the caller.
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            foreach (string root in existingRoots)
            {
                if (!AssetDatabase.DeleteAsset(root))
                {
                    status.Error($"Couldn't delete the existing folder '{root}'. Close anything opened from it and try again.");
                    return false;
                }
            }

            AssetDatabase.Refresh();
            return true;
        }

        /// <summary>
        /// Sets an entry's target paths from the current inputs: the avatar root, the copied FBX and prefab,
        /// the working scene and the diff generator asset.
        /// </summary>
        private void AssignPlannedPaths(AvatarEntry entry)
        {
            string avatarFolderName = (useSeparateFolderPerAvatar ? entry.avatarFolderName : sharedAvatarFolderName).Trim();
            string sourceFbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
            string sourcePrefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);
            string rootPath = PawlygonEditorUtils.CombineAssetPath("Assets", mainFolderName.Trim(), avatarFolderName);
            string internalFolderPath = PawlygonEditorUtils.CombineAssetPath(rootPath, "Internal");

            entry.avatarRootPath = rootPath;
            entry.copiedFbxPath = PawlygonEditorUtils.CombineAssetPath(rootPath, "FBX", GetCopiedFbxFileName(sourceFbxPath));
            entry.copiedPrefabPath = PawlygonEditorUtils.CombineAssetPath(rootPath, "Prefabs", Path.GetFileName(sourcePrefabPath));
            entry.createdScenePath = PawlygonEditorUtils.CombineAssetPath(internalFolderPath, "Scenes", $"{avatarFolderName} - Pawlygon VRCFT.unity");
            entry.diffGeneratorAssetPath = PawlygonEditorUtils.CombineAssetPath(internalFolderPath,
                $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} Face Tracking DiffGenerator.asset");
        }

        private static void EnsureAvatarFolders(string avatarRootPath)
        {
            PawlygonEditorUtils.EnsureFolderExists(avatarRootPath);
            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "FBX"));
            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "Prefabs"));
            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "Internal"));
            PawlygonEditorUtils.EnsureFolderExists(PawlygonEditorUtils.CombineAssetPath(avatarRootPath, "Internal", "Scenes"));
        }

        private void ShowCreationProgress(string message, int index, int count)
        {
            EditorUtility.DisplayProgressBar(ProgressTitle, message, count > 0 ? Mathf.Clamp01((float)index / count) : 0f);
        }

        private bool CreateSeparateAvatarStructure(AvatarEntry entry, int index, bool openAfterCreate)
        {
            string displayName = GetEntryDisplayName(entry);
            ShowCreationProgress($"Copying '{displayName}' ({index + 1}/{avatarEntries.Count})…", index, avatarEntries.Count);

            AssignPlannedPaths(entry);
            EnsureAvatarFolders(entry.avatarRootPath);

            if (!CopyAvatarAssets(entry))
            {
                return false;
            }

            ShowCreationProgress($"Creating the working scene for '{displayName}'…", index, avatarEntries.Count);
            if (!CreateSceneAsset(entry.createdScenePath, new[] { entry.copiedPrefabPath }))
            {
                status.Error($"Couldn't create the working scene for '{displayName}' at '{entry.createdScenePath}'.");
                return false;
            }

            if (!CreateDiffGeneratorAsset(entry, entry.diffGeneratorAssetPath))
            {
                return false;
            }

            if (openAfterCreate)
            {
                EditorSceneManager.OpenScene(entry.createdScenePath, OpenSceneMode.Single);
            }

            return true;
        }

        private bool CreateSharedAvatarStructure()
        {
            foreach (AvatarEntry entry in avatarEntries)
            {
                AssignPlannedPaths(entry);
            }

            string sharedScenePath = avatarEntries[0].createdScenePath;
            EnsureAvatarFolders(avatarEntries[0].avatarRootPath);

            for (int i = 0; i < avatarEntries.Count; i++)
            {
                ShowCreationProgress($"Copying '{GetEntryDisplayName(avatarEntries[i])}' ({i + 1}/{avatarEntries.Count})…", i, avatarEntries.Count);
                if (!CopyAvatarAssets(avatarEntries[i]))
                {
                    return false;
                }
            }

            ShowCreationProgress("Creating the working scene…", avatarEntries.Count, avatarEntries.Count);
            if (!CreateSceneAsset(sharedScenePath, avatarEntries.Select(entry => entry.copiedPrefabPath).ToList()))
            {
                status.Error($"Couldn't create the shared working scene at '{sharedScenePath}'.");
                return false;
            }

            foreach (AvatarEntry entry in avatarEntries)
            {
                if (!CreateDiffGeneratorAsset(entry, entry.diffGeneratorAssetPath))
                {
                    return false;
                }
            }

            EditorSceneManager.OpenScene(sharedScenePath, OpenSceneMode.Single);
            return true;
        }

        /// <summary>Copies the source FBX and prefab to the entry's planned paths, skipping copies that already exist.</summary>
        private bool CopyAvatarAssets(AvatarEntry entry)
        {
            string sourceFbxPath = AssetDatabase.GetAssetPath(entry.sourceFbx);
            string sourcePrefabPath = AssetDatabase.GetAssetPath(entry.sourcePrefab);

            if (!AssetFileExists(entry.copiedFbxPath) && !AssetDatabase.CopyAsset(sourceFbxPath, entry.copiedFbxPath))
            {
                status.Error($"Couldn't copy the FBX '{sourceFbxPath}' to '{entry.copiedFbxPath}'.");
                return false;
            }

            if (!AssetFileExists(entry.copiedPrefabPath) && !AssetDatabase.CopyAsset(sourcePrefabPath, entry.copiedPrefabPath))
            {
                status.Error($"Couldn't copy the prefab '{sourcePrefabPath}' to '{entry.copiedPrefabPath}'.");
                return false;
            }

            return true;
        }

        private static bool CreateSceneAsset(string sceneAssetPath, IReadOnlyList<string> prefabAssetPaths)
        {
            // Unity refuses to create a scene additively while any loaded scene is an unsaved
            // untitled scene. That situation is common (a fresh untitled scene, or a working
            // scene whose asset was just deleted during an overwrite), so fall back to creating
            // the working scene in Single mode and leave the saved result open instead of closing it.
            bool hasUntitledLoadedScene = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                {
                    hasUntitledLoadedScene = true;
                    break;
                }
            }

            NewSceneMode sceneMode = hasUntitledLoadedScene ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, sceneMode);

            try
            {
                for (int i = 0; i < prefabAssetPaths.Count; i++)
                {
                    GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabAssetPaths[i]);
                    if (prefabAsset == null)
                    {
                        continue;
                    }

                    GameObject instance = PrefabUtility.InstantiatePrefab(prefabAsset, newScene) as GameObject;
                    if (instance != null)
                    {
                        instance.transform.position = GetGridPosition(i);
                    }
                }

                EditorSceneManager.MarkSceneDirty(newScene);
                return EditorSceneManager.SaveScene(newScene, sceneAssetPath);
            }
            finally
            {
                // Only close the temporary scene when it was created additively alongside the
                // user's existing scene. In Single mode it is now the only (saved) scene, so
                // closing it would leave the editor with no valid scene loaded.
                if (sceneMode == NewSceneMode.Additive && newScene.IsValid())
                {
                    EditorSceneManager.CloseScene(newScene, true);
                }
            }
        }

        private bool CreateDiffGeneratorAsset(AvatarEntry entry, string assetPath)
        {
            AssetDatabase.ImportAsset(entry.copiedFbxPath, ImportAssetOptions.ForceSynchronousImport);

            GameObject copiedFbx = AssetDatabase.LoadAssetAtPath<GameObject>(entry.copiedFbxPath);
            DefaultAsset outputDirectory = AssetDatabase.LoadAssetAtPath<DefaultAsset>(entry.avatarRootPath);
            if (copiedFbx == null || outputDirectory == null)
            {
                status.Error($"Couldn't create the diff generator asset for '{GetEntryDisplayName(entry)}': the copied FBX or its folder can't be loaded.");
                return false;
            }

            var diffGenerator = ScriptableObject.CreateInstance<FTDiffGenerator>();
            diffGenerator.originalModelFbx = entry.sourceFbx;
            diffGenerator.modifiedModelFbx = copiedFbx;
            diffGenerator.outputDirectory = outputDirectory;
            AssetDatabase.CreateAsset(diffGenerator, assetPath);
            return true;
        }

        private static string GetCopiedFbxFileName(string sourceFbxPath)
        {
            return $"{Path.GetFileNameWithoutExtension(sourceFbxPath)} FT{Path.GetExtension(sourceFbxPath)}";
        }

        private static Vector3 GetGridPosition(int index)
        {
            int column = index % SharedSceneGridColumns;
            int row = index / SharedSceneGridColumns;
            return new Vector3(column * SharedSceneGridSpacingX, 0f, row * SharedSceneGridSpacingZ);
        }
    }
}
