using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for creating and editing per-config package rules
    /// (<c>configSpecificPackages</c>) on PatcherHub <c>FTPatchConfig</c> assets.
    ///
    /// The flow is selection-driven: pick which discovered FTPatchConfig assets to update, build a
    /// package rule (optionally auto-filled from an installed package), and apply it to all selected
    /// configs at once. When a single config is selected its existing rules can be edited in place.
    ///
    /// All editing is done through <see cref="SerializedObject"/>/<see cref="SerializedProperty"/>
    /// by field name, so this tool needs no compile-time dependency on PatcherHub and gets full Undo
    /// support for free. The PatcherHub <c>PackageRequirement</c> shape is <c>packageName</c>,
    /// <c>minVersion</c>, <c>vccURL</c>, and two nested <c>VersionError</c> objects (<c>missingError</c>,
    /// <c>versionError</c>) each with <c>message</c> + <c>messageType</c>. PatcherHub matches
    /// <c>packageName</c> against the UPM package name and compares versions with System.Version
    /// (treating "Any" as always satisfied).
    /// </summary>
    public class PatchConfigRulesEditor : EditorWindow
    {
        private const string MenuPath = "!Pawlygon/Tools/Patch Config Package Rules";
        private const float SectionSpacing = 10f;

        // Serialized field names on the PatcherHub types.
        private const string ConfigSpecificField = "configSpecificPackages";
        private const string DisplayNameField = "avatarDisplayName";
        private const string PackageNameField = "packageName";
        private const string MinVersionField = "minVersion";
        private const string VccUrlField = "vccURL";
        private const string MissingErrorField = "missingError";
        private const string VersionErrorField = "versionError";
        private const string MessageField = "message";
        private const string MessageTypeField = "messageType";

        private const string DefaultMinVersion = "Any";

        [SerializeField] private Vector2 scrollPosition;

        // Discovered FTPatchConfig assets.
        private readonly List<ConfigItem> configItems = new List<ConfigItem>();
        private string configSearch = string.Empty;

        // Installed packages (for auto-fill), curated common packages sorted to the top.
        private readonly List<PackageItem> installedPackages = new List<PackageItem>();
        private string[] packagePopupLabels = { "— Custom (type manually) —" };
        private int selectedPackageIndex;

        // Rule being authored.
        private readonly RuleDraft draft = new RuleDraft();
        private bool draftMessagesExpanded;
        private bool skipDuplicates = true;

        // Existing-rules editing for a single selected config.
        private SerializedObject singleConfigSerialized;
        private UnityEngine.Object singleConfigTarget;

        private string statusMessage;
        private MessageType statusMessageType = MessageType.Info;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            PatchConfigRulesEditor window = GetWindow<PatchConfigRulesEditor>();
            window.titleContent = new GUIContent("Patch Config Rules");
            window.minSize = new Vector2(560f, 520f);
        }

        private void OnEnable()
        {
            RefreshConfigs();
            RefreshInstalledPackages();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();

            PawlygonEditorUI.DrawHeader(
                "Patch Config Package Rules",
                "Require packages (and versions) per avatar by adding rules to FTPatchConfig assets.");
            EditorGUILayout.Space(SectionSpacing);

            Type configType = FTPatchConfigGenerator.GetFTPatchConfigType();
            if (configType == null)
            {
                EditorGUILayout.HelpBox(
                    "PatcherHub is not installed, so no FTPatchConfig type is available. Install PatcherHub " +
                    "(via the Avatar Setup Wizard's Prefabs step or manually) and reopen this window.",
                    MessageType.Warning);
                EditorGUILayout.Space(8f);
                PawlygonEditorUI.DrawFooter();
                return;
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            DrawConfigSelection();
            EditorGUILayout.Space(SectionSpacing);

            List<ConfigItem> selected = configItems.Where(c => c.Selected && c.Asset != null).ToList();
            UpdateSingleConfigEditor(selected);

            if (selected.Count == 1)
            {
                DrawSingleConfigRules(selected[0]);
                EditorGUILayout.Space(SectionSpacing);
            }
            else if (selected.Count > 1)
            {
                EditorGUILayout.HelpBox(
                    $"{selected.Count} configs selected. Select exactly one to view and edit its existing rules.",
                    MessageType.None);
                EditorGUILayout.Space(SectionSpacing);
            }

            DrawRuleBuilder(selected);

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
        // Config selection
        // =====================================================================

        private void DrawConfigSelection()
        {
            int total = configItems.Count;
            int selectedCount = configItems.Count(c => c.Selected);

            PawlygonEditorUI.DrawSection(
                $"Configs to Update  ({selectedCount}/{total} selected)",
                "Pick the FTPatchConfig assets that the rule should be added to.",
                () =>
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        float prevLabelWidth = EditorGUIUtility.labelWidth;
                        EditorGUIUtility.labelWidth = 50f;
                        configSearch = EditorGUILayout.TextField("Search", configSearch);
                        EditorGUIUtility.labelWidth = prevLabelWidth;

                        if (GUILayout.Button("All", GUILayout.Width(50f)))
                            SetAllSelected(true);
                        if (GUILayout.Button("None", GUILayout.Width(50f)))
                            SetAllSelected(false);
                        if (GUILayout.Button("Refresh", GUILayout.Width(70f)))
                            RefreshConfigs();
                    }

                    EditorGUILayout.Space(4f);

                    if (total == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "No FTPatchConfig assets found in the project. Create one via the patcher workflow, then Refresh.",
                            MessageType.Info);
                        return;
                    }

                    string filter = configSearch?.Trim();
                    bool hasFilter = !string.IsNullOrEmpty(filter);
                    int shown = 0;

                    foreach (ConfigItem item in configItems)
                    {
                        if (item.Asset == null) continue;
                        if (hasFilter &&
                            (item.DisplayName?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0 &&
                            (item.Path?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                        {
                            continue;
                        }

                        shown++;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            item.Selected = EditorGUILayout.ToggleLeft(
                                new GUIContent(item.DisplayName, item.Path), item.Selected, GUILayout.ExpandWidth(true));

                            EditorGUILayout.LabelField(
                                $"{item.RuleCount} rule{(item.RuleCount == 1 ? "" : "s")}",
                                EditorStyles.miniLabel, GUILayout.Width(60f));

                            if (GUILayout.Button("Ping", GUILayout.Width(44f)))
                                EditorGUIUtility.PingObject(item.Asset);
                        }
                    }

                    if (hasFilter && shown == 0)
                    {
                        EditorGUILayout.HelpBox($"No configs match '{filter}'.", MessageType.None);
                    }
                });
        }

        private void SetAllSelected(bool value)
        {
            string filter = configSearch?.Trim();
            bool hasFilter = !string.IsNullOrEmpty(filter);

            foreach (ConfigItem item in configItems)
            {
                if (item.Asset == null) continue;
                if (hasFilter &&
                    (item.DisplayName?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0 &&
                    (item.Path?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) < 0)
                {
                    continue;
                }

                item.Selected = value;
            }
        }

        private void RefreshConfigs()
        {
            // Preserve current selection by asset path across refreshes.
            var previouslySelected = new HashSet<string>(
                configItems.Where(c => c.Selected && c.Path != null).Select(c => c.Path));

            configItems.Clear();

            Type configType = FTPatchConfigGenerator.GetFTPatchConfigType();
            if (configType == null) return;

            string[] guids = AssetDatabase.FindAssets("t:" + configType.Name);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(path, configType);
                if (asset == null) continue;

                configItems.Add(new ConfigItem
                {
                    Asset = asset,
                    Path = path,
                    DisplayName = ResolveConfigLabel(asset, path),
                    RuleCount = GetRuleCount(asset),
                    Selected = previouslySelected.Contains(path)
                });
            }

            configItems.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveConfigLabel(UnityEngine.Object asset, string path)
        {
            var so = new SerializedObject(asset);
            SerializedProperty displayName = so.FindProperty(DisplayNameField);
            string assetName = System.IO.Path.GetFileNameWithoutExtension(path);

            if (displayName != null && !string.IsNullOrWhiteSpace(displayName.stringValue) &&
                displayName.stringValue != "Avatar Name")
            {
                return $"{displayName.stringValue}  ({assetName})";
            }

            return assetName;
        }

        private static int GetRuleCount(UnityEngine.Object asset)
        {
            var so = new SerializedObject(asset);
            SerializedProperty rules = so.FindProperty(ConfigSpecificField);
            return rules != null ? rules.arraySize : 0;
        }

        // =====================================================================
        // Single-config existing rules
        // =====================================================================

        private void UpdateSingleConfigEditor(List<ConfigItem> selected)
        {
            UnityEngine.Object target = selected.Count == 1 ? selected[0].Asset : null;
            if (target != singleConfigTarget)
            {
                singleConfigTarget = target;
                singleConfigSerialized = target != null ? new SerializedObject(target) : null;
            }
        }

        private void DrawSingleConfigRules(ConfigItem item)
        {
            if (singleConfigSerialized == null) return;

            PawlygonEditorUI.DrawSection(
                $"Existing Rules — {item.DisplayName}",
                "Edit, reorder, or remove the package rules already on this config.",
                () =>
                {
                    singleConfigSerialized.Update();

                    SerializedProperty rules = singleConfigSerialized.FindProperty(ConfigSpecificField);
                    if (rules == null)
                    {
                        EditorGUILayout.HelpBox(
                            $"This asset has no '{ConfigSpecificField}' field. The installed PatcherHub version may differ.",
                            MessageType.Error);
                        return;
                    }

                    DrawRulesList(rules, item);

                    if (singleConfigSerialized.ApplyModifiedProperties())
                    {
                        item.RuleCount = rules.arraySize;
                    }
                });
        }

        private void DrawRulesList(SerializedProperty rules, ConfigItem item)
        {
            if (rules.arraySize == 0)
            {
                EditorGUILayout.HelpBox("No package rules on this config yet. Add one below.", MessageType.Info);
                return;
            }

            int removeIndex = -1;
            int moveFrom = -1;
            int moveTo = -1;

            for (int i = 0; i < rules.arraySize; i++)
            {
                SerializedProperty element = rules.GetArrayElementAtIndex(i);

                using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                {
                    SerializedProperty packageName = element.FindPropertyRelative(PackageNameField);
                    string title = packageName != null && !string.IsNullOrEmpty(packageName.stringValue)
                        ? packageName.stringValue
                        : "(unnamed package)";

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"Rule {i + 1}: {title}", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();

                        using (new EditorGUI.DisabledScope(i == 0))
                        {
                            if (GUILayout.Button("▲", GUILayout.Width(26f)))
                            {
                                moveFrom = i;
                                moveTo = i - 1;
                            }
                        }

                        using (new EditorGUI.DisabledScope(i == rules.arraySize - 1))
                        {
                            if (GUILayout.Button("▼", GUILayout.Width(26f)))
                            {
                                moveFrom = i;
                                moveTo = i + 1;
                            }
                        }

                        if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                        {
                            removeIndex = i;
                        }
                    }

                    EditorGUILayout.Space(2f);
                    DrawRuleFields(element);
                }

                EditorGUILayout.Space(4f);
            }

            if (removeIndex >= 0)
            {
                rules.DeleteArrayElementAtIndex(removeIndex);
            }
            else if (moveFrom >= 0 && moveTo >= 0)
            {
                rules.MoveArrayElement(moveFrom, moveTo);
            }
        }

        private static void DrawRuleFields(SerializedProperty element)
        {
            SerializedProperty packageName = element.FindPropertyRelative(PackageNameField);
            SerializedProperty minVersion = element.FindPropertyRelative(MinVersionField);
            SerializedProperty vccUrl = element.FindPropertyRelative(VccUrlField);
            SerializedProperty missingError = element.FindPropertyRelative(MissingErrorField);
            SerializedProperty versionError = element.FindPropertyRelative(VersionErrorField);

            if (packageName != null)
                EditorGUILayout.PropertyField(packageName, new GUIContent("Package Name"));
            if (minVersion != null)
                EditorGUILayout.PropertyField(minVersion, new GUIContent("Min Version", "Use \"Any\" to accept any installed version."));
            if (vccUrl != null)
                EditorGUILayout.PropertyField(vccUrl, new GUIContent("VCC URL", "Optional link opened when this package is missing or invalid."));

            if (missingError != null)
                EditorGUILayout.PropertyField(missingError, new GUIContent("Missing Error"), true);
            if (versionError != null)
                EditorGUILayout.PropertyField(versionError, new GUIContent("Version Error"), true);
        }

        // =====================================================================
        // Rule builder
        // =====================================================================

        private void DrawRuleBuilder(List<ConfigItem> selected)
        {
            PawlygonEditorUI.DrawSection(
                "Add a Package Rule",
                "Pick an installed package to auto-fill the requirement, then add it to the selected configs.",
                () =>
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        int newIndex = EditorGUILayout.Popup(
                            new GUIContent("From Installed", "Auto-fill the rule from an installed package (common avatar packages listed first)."),
                            selectedPackageIndex, packagePopupLabels);

                        if (GUILayout.Button("Refresh", GUILayout.Width(70f)))
                        {
                            RefreshInstalledPackages();
                        }

                        if (newIndex != selectedPackageIndex)
                        {
                            selectedPackageIndex = newIndex;
                            ApplyPackageAutoFill();
                        }
                    }

                    EditorGUILayout.Space(6f);

                    draft.packageName = EditorGUILayout.TextField(
                        new GUIContent("Package Name", "UPM package id, e.g. com.vrchat.avatars."), draft.packageName);
                    draft.minVersion = EditorGUILayout.TextField(
                        new GUIContent("Min Version", "Use \"Any\" to accept any installed version."), draft.minVersion);
                    draft.vccUrl = EditorGUILayout.TextField(
                        new GUIContent("VCC URL", "Optional link opened when this package is missing or invalid."), draft.vccUrl);

                    if (CommonVrcPackages.IsGloballyHandled(draft.packageName))
                    {
                        EditorGUILayout.HelpBox(
                            "This package is already validated globally by PatcherHub's PackageRules, so a per-config rule usually isn't needed.",
                            MessageType.Info);
                    }

                    draftMessagesExpanded = EditorGUILayout.Foldout(draftMessagesExpanded, "Error messages", true);
                    if (draftMessagesExpanded)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.LabelField("Shown when the package is missing", EditorStyles.miniBoldLabel);
                        draft.missingMessage = EditorGUILayout.TextArea(draft.missingMessage, GUILayout.MinHeight(34f));
                        draft.missingType = (MessageType)EditorGUILayout.EnumPopup("Severity", draft.missingType);

                        EditorGUILayout.Space(4f);
                        EditorGUILayout.LabelField("Shown when the installed version is too old", EditorStyles.miniBoldLabel);
                        draft.versionMessage = EditorGUILayout.TextArea(draft.versionMessage, GUILayout.MinHeight(34f));
                        draft.versionType = (MessageType)EditorGUILayout.EnumPopup("Severity", draft.versionType);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.Space(4f);
                    skipDuplicates = EditorGUILayout.ToggleLeft(
                        "Skip configs that already list this package", skipDuplicates);

                    EditorGUILayout.Space(6f);

                    int targetCount = selected.Count;
                    bool canApply = targetCount > 0 && !string.IsNullOrWhiteSpace(draft.packageName);

                    using (new EditorGUI.DisabledScope(!canApply))
                    {
                        if (PawlygonEditorUI.DrawPrimaryButton($"Add Rule to {targetCount} Selected Config(s)"))
                        {
                            ApplyDraftToSelected(selected);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(draft.packageName))
                    {
                        EditorGUILayout.HelpBox("Enter or pick a package name for the rule.", MessageType.Info);
                    }
                    else if (targetCount == 0)
                    {
                        EditorGUILayout.HelpBox("Select one or more configs above to add this rule to.", MessageType.Info);
                    }
                });
        }

        private void ApplyDraftToSelected(List<ConfigItem> selected)
        {
            int applied = 0;
            int skipped = 0;

            foreach (ConfigItem item in selected)
            {
                if (item.Asset == null) continue;

                var so = new SerializedObject(item.Asset);
                SerializedProperty rules = so.FindProperty(ConfigSpecificField);
                if (rules == null)
                {
                    skipped++;
                    continue;
                }

                if (skipDuplicates && ContainsPackage(rules, draft.packageName))
                {
                    skipped++;
                    continue;
                }

                AppendRule(rules, draft);
                so.ApplyModifiedProperties();
                item.RuleCount = rules.arraySize;
                applied++;
            }

            // Force the single-config editor to re-read from the asset on the next frame.
            singleConfigSerialized = null;
            singleConfigTarget = null;

            statusMessage = skipped > 0
                ? $"Added '{draft.packageName}' to {applied} config(s); skipped {skipped} (missing field or already listed)."
                : $"Added '{draft.packageName}' to {applied} config(s).";
            statusMessageType = applied > 0 ? MessageType.Info : MessageType.Warning;
        }

        // =====================================================================
        // Installed packages
        // =====================================================================

        private void RefreshInstalledPackages()
        {
            installedPackages.Clear();

            try
            {
                UnityEditor.PackageManager.PackageInfo[] packages =
                    UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();

                if (packages != null)
                {
                    foreach (UnityEditor.PackageManager.PackageInfo p in packages)
                    {
                        // Skip Unity's built-in modules (com.unity.modules.*) to keep the list focused.
                        if (p.source == UnityEditor.PackageManager.PackageSource.BuiltIn) continue;

                        installedPackages.Add(new PackageItem
                        {
                            Name = p.name,
                            DisplayName = string.IsNullOrEmpty(p.displayName) ? p.name : p.displayName,
                            Version = p.version,
                            VccUrl = CommonVrcPackages.GetVccUrl(p.name),
                            Priority = CommonVrcPackages.GetPriority(p.name)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                statusMessage = $"Could not read installed packages: {ex.Message}";
                statusMessageType = MessageType.Warning;
            }

            // Curated common avatar packages first (in catalog order), then everything else by name.
            installedPackages.Sort((a, b) =>
            {
                if (a.Priority != b.Priority) return a.Priority.CompareTo(b.Priority);
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            var labels = new List<string> { "— Custom (type manually) —" };
            labels.AddRange(installedPackages.Select(p =>
                $"{(p.Priority != int.MaxValue ? "★ " : string.Empty)}{p.DisplayName}   ({p.Name})   v{p.Version}"));
            packagePopupLabels = labels.ToArray();

            if (selectedPackageIndex >= packagePopupLabels.Length)
            {
                selectedPackageIndex = 0;
            }
        }

        private void ApplyPackageAutoFill()
        {
            int packageIndex = selectedPackageIndex - 1; // index 0 is the "Custom" entry
            if (packageIndex < 0 || packageIndex >= installedPackages.Count)
            {
                return;
            }

            PackageItem pkg = installedPackages[packageIndex];
            draft.packageName = pkg.Name;
            draft.minVersion = string.IsNullOrEmpty(pkg.Version) ? DefaultMinVersion : pkg.Version;
            draft.vccUrl = pkg.VccUrl ?? string.Empty;
            draft.missingMessage =
                $"{pkg.DisplayName} is required for this avatar but is not installed. Please install it before patching.";
            draft.missingType = MessageType.Warning;
            draft.versionMessage =
                $"This avatar requires {pkg.DisplayName} {draft.minVersion} or newer. Please update {pkg.DisplayName} before patching.";
            draft.versionType = MessageType.Warning;
            draftMessagesExpanded = true;
            statusMessage = null;
        }

        // =====================================================================
        // Rule helpers
        // =====================================================================

        private static bool ContainsPackage(SerializedProperty rules, string packageName)
        {
            for (int i = 0; i < rules.arraySize; i++)
            {
                SerializedProperty name = rules.GetArrayElementAtIndex(i).FindPropertyRelative(PackageNameField);
                if (name != null && string.Equals(name.stringValue, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Appends a new package requirement to <paramref name="rules"/> and writes the draft's
        /// values into it. The serialized array is grown by one and every known field is set
        /// explicitly so the new element never inherits stale values from a duplicated entry.
        /// </summary>
        private static void AppendRule(SerializedProperty rules, RuleDraft draft)
        {
            int index = rules.arraySize;
            rules.arraySize++;
            SerializedProperty element = rules.GetArrayElementAtIndex(index);

            SetString(element, PackageNameField, draft.packageName);
            SetString(element, MinVersionField, string.IsNullOrEmpty(draft.minVersion) ? DefaultMinVersion : draft.minVersion);
            SetString(element, VccUrlField, draft.vccUrl);

            SerializedProperty missingError = element.FindPropertyRelative(MissingErrorField);
            if (missingError != null)
            {
                SetString(missingError, MessageField, draft.missingMessage);
                SetEnum(missingError, MessageTypeField, draft.missingType);
            }

            SerializedProperty versionError = element.FindPropertyRelative(VersionErrorField);
            if (versionError != null)
            {
                SetString(versionError, MessageField, draft.versionMessage);
                SetEnum(versionError, MessageTypeField, draft.versionType);
            }
        }

        private static void SetString(SerializedProperty parent, string relativeName, string value)
        {
            SerializedProperty prop = parent.FindPropertyRelative(relativeName);
            if (prop != null)
            {
                prop.stringValue = value ?? string.Empty;
            }
        }

        private static void SetEnum(SerializedProperty parent, string relativeName, MessageType value)
        {
            SerializedProperty prop = parent.FindPropertyRelative(relativeName);
            if (prop != null)
            {
                prop.enumValueIndex = (int)value;
            }
        }

        private class ConfigItem
        {
            public UnityEngine.Object Asset;
            public string Path;
            public string DisplayName;
            public int RuleCount;
            public bool Selected;
        }

        private class PackageItem
        {
            public string Name;
            public string DisplayName;
            public string Version;
            public string VccUrl;
            public int Priority;
        }

        private class RuleDraft
        {
            public string packageName = string.Empty;
            public string minVersion = DefaultMinVersion;
            public string vccUrl = string.Empty;
            public string missingMessage = string.Empty;
            public MessageType missingType = MessageType.Warning;
            public string versionMessage = string.Empty;
            public MessageType versionType = MessageType.Warning;
        }
    }
}
