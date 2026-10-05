using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Editor window for creating and editing per-config package rules
    /// (<c>configSpecificPackages</c>) on PatcherHub <c>FTPatchConfig</c> assets ("patch configs").
    ///
    /// Two tabs: <b>Add Rule</b> ticks any number of patch configs, builds one package rule (optionally
    /// auto-filled from an installed package) and adds it to all of them; <b>Edit Rules</b> shows one config's
    /// existing rules as collapsed rows that expand for editing. The ticked configs and the edited config are
    /// remembered per project.
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
        private const string WindowTitle = "Patch Config Package Rules";
        private const string ImportPatcherHubMenuPath = "!Pawlygon/Import Latest PatcherHub";
        private const float SectionSpacing = 10f;
        private const float ConfigRowHeight = 20f;
        private const float ConfigListMaxHeight = 200f;

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
        private const string CustomPackageLabel = "— None (type it in) —";

        private const string SelectedConfigsPrefPrefix = "Pawlygon.UnityTools.PatchConfigRules.Selected.";
        private const string EditedConfigPrefPrefix = "Pawlygon.UnityTools.PatchConfigRules.Edited.";

        private enum Tab { AddRule, EditRules }

        private static readonly PawlygonEditorUI.TabSpec[] Tabs =
        {
            new PawlygonEditorUI.TabSpec("Add Rule", tooltip: "Add one rule to several patch configs at once."),
            new PawlygonEditorUI.TabSpec("Edit Rules", tooltip: "Review, change, reorder or remove the rules on one patch config."),
        };

        private static readonly string[] SeverityLabels = { "Info", "Warning", "Error" };
        private static readonly MessageType[] SeverityValues = { MessageType.Info, MessageType.Warning, MessageType.Error };
        private const string SeverityTooltip = "How strongly PatcherHub shows this message: Info, Warning (yellow) or Error (red).";

        [SerializeField] private Vector2 scrollPosition;
        [SerializeField] private Vector2 configListScroll;
        [SerializeField] private Tab currentTab;

        // Discovered FTPatchConfig assets.
        private readonly List<ConfigItem> configItems = new List<ConfigItem>();
        private string configSearch = string.Empty;

        // Installed packages (for auto-fill), curated common packages sorted to the top.
        private readonly List<PackageItem> installedPackages = new List<PackageItem>();
        private string[] packagePopupLabels = { CustomPackageLabel };
        private int selectedPackageIndex;

        // Rule being authored.
        private RuleDraft draft = new RuleDraft();

        /// <summary>The values the last auto-fill wrote, so re-picking only asks when the user typed something.</summary>
        private RuleDraft lastAutoFill;

        private bool draftMessagesExpanded;
        private bool skipDuplicates = true;

        // Existing-rules editing for one config.
        private string editedConfigGuid;
        private SerializedObject editedConfigSerialized;
        private UnityEngine.Object editedConfigTarget;
        private readonly HashSet<int> expandedRules = new HashSet<int>();

        private readonly PawlygonStatus status = new PawlygonStatus();

        // The FTPatchConfig type, resolved once per window lifetime. Resolving it scans every loaded
        // ScriptableObject type, which is far too slow to repeat on every OnGUI event. Installing
        // PatcherHub triggers a domain reload, which re-runs OnEnable and resets this cache.
        private Type cachedConfigType;
        private bool configTypeResolved;

        // --- Styles ---
        private GUIStyle wrappedTextAreaStyle;
        private GUIStyle placeholderStyle;
        private GUIStyle ruleFoldoutStyle;
        private GUIStyle hintStyle;

        private static string ProjectKey => PlayerSettings.productGUID.ToString();

        [MenuItem(MenuPath, priority = 60)] // Tools: Publish
        public static void ShowWindow()
        {
            PatchConfigRulesEditor window = GetWindow<PatchConfigRulesEditor>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(540f, 480f);
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            configTypeResolved = false;
            cachedConfigType = null;
            editedConfigGuid = EditorPrefs.GetString(EditedConfigPrefPrefix + ProjectKey, string.Empty);
            RefreshConfigs();
            RefreshInstalledPackages();
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnProjectChange()
        {
            // Configs may have been created, deleted or renamed.
            RefreshConfigs();
            Repaint();
        }

        /// <summary>Undo can bring back a removed rule or take away an added one: re-read the counts and rules.</summary>
        private void OnUndoRedo()
        {
            foreach (ConfigItem item in configItems)
            {
                if (item.Asset != null) item.RuleCount = GetRuleCount(item.Asset);
            }

            editedConfigSerialized?.Update();
            Repaint();
        }

        // =====================================================================
        // OnGUI
        // =====================================================================

        private void OnGUI()
        {
            PawlygonEditorUI.EnsureStyles();
            EnsureStyles();

            PawlygonEditorUI.DrawHeader(
                WindowTitle,
                "Make the patcher check that an avatar's required packages are installed before patching.",
                PawlygonEditorUI.DocumentationUrl);

            Type configType = GetConfigType();
            if (configType == null)
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));
                DrawPatcherHubMissing();
                EditorGUILayout.EndScrollView();
                PawlygonEditorUI.DrawStatusBar(status);
                PawlygonEditorUI.DrawFooter();
                return;
            }

            int clicked = PawlygonEditorUI.DrawTabBar(Tabs, (int)currentTab);
            if (clicked >= 0)
            {
                SwitchTab((Tab)clicked);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(6f);

            // Read before any field so conditional controls stay in step with the layout pass while typing.
            List<ConfigItem> selected = configItems.Where(c => c.Selected && c.Asset != null).ToList();
            string searchAtStart = configSearch;
            RuleDraft draftAtStart = draft.Clone();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.ExpandHeight(true));

            if (currentTab == Tab.AddRule)
            {
                DrawAddRuleTab(searchAtStart, draftAtStart);
            }
            else
            {
                DrawEditRulesTab();
            }

            EditorGUILayout.EndScrollView();

            PawlygonEditorUI.DrawStatusBar(status);

            if (currentTab == Tab.AddRule)
            {
                DrawAddRuleActionBar(selected, draftAtStart);
            }

            PawlygonEditorUI.DrawFooter();
        }

        private void SwitchTab(Tab tab)
        {
            currentTab = tab;
            scrollPosition = Vector2.zero;
            status.Clear();
        }

        private void EnsureStyles()
        {
            if (wrappedTextAreaStyle != null) return;

            wrappedTextAreaStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };

            placeholderStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };
            placeholderStyle.normal.textColor = PawlygonEditorUI.MutedColor;
            placeholderStyle.padding = EditorStyles.textField.padding;

            ruleFoldoutStyle = new GUIStyle(EditorStyles.foldout) { richText = true };

            hintStyle = new GUIStyle(EditorStyles.label);
            hintStyle.normal.textColor = PawlygonEditorUI.MutedColor;
        }

        // =====================================================================
        // Empty state: PatcherHub missing
        // =====================================================================

        private void DrawPatcherHubMissing()
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "PatcherHub Isn't Installed",
                    "Package rules live in PatcherHub's patch configs (one per avatar), so this tool needs PatcherHub in the project. " +
                    "Import it, then create the patch configs with the Avatar Setup Wizard.");

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (PawlygonEditorUI.DrawPrimaryButton("Import PatcherHub", 30f))
                    {
                        // Downloads and imports a unitypackage; the domain reload reopens this window's checks.
                        EditorApplication.ExecuteMenuItem(ImportPatcherHubMenuPath);
                        GUIUtility.ExitGUI();
                    }

                    if (PawlygonEditorUI.DrawSecondaryButton("Open Avatar Setup Wizard"))
                    {
                        AvatarSetupWizard.ShowWindow();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        // =====================================================================
        // Add Rule tab
        // =====================================================================

        private void DrawAddRuleTab(string searchAtStart, RuleDraft draftAtStart)
        {
            DrawConfigSelection(searchAtStart);
            EditorGUILayout.Space(SectionSpacing);
            DrawRuleBuilder(draftAtStart);
        }

        private void DrawConfigSelection(string searchAtStart)
        {
            int total = configItems.Count;
            int selectedCount = configItems.Count(c => c.Selected);

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Configs to Update",
                    "Each avatar has a patch config: the PatcherHub settings file the Avatar Setup Wizard creates for it. Tick the ones that should get this rule.");

                if (total == 0)
                {
                    DrawNoConfigs();
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    float prevLabelWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 50f;
                    configSearch = EditorGUILayout.TextField("Search", configSearch);
                    EditorGUIUtility.labelWidth = prevLabelWidth;

                    if (GUILayout.Button(new GUIContent("All", "Tick every config shown."), EditorStyles.miniButton, GUILayout.Width(40f)))
                        SetAllSelected(true, searchAtStart);
                    if (GUILayout.Button(new GUIContent("None", "Untick every config shown."), EditorStyles.miniButton, GUILayout.Width(44f)))
                        SetAllSelected(false, searchAtStart);
                    if (GUILayout.Button(new GUIContent("Refresh", "Look for patch configs again."), EditorStyles.miniButton, GUILayout.Width(58f)))
                    {
                        RefreshConfigs();
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUILayout.Space(4f);

                List<ConfigItem> shownItems = configItems.Where(c => c.Asset != null && MatchesSearch(c, searchAtStart)).ToList();
                if (shownItems.Count == 0)
                {
                    EditorGUILayout.HelpBox($"No configs match '{searchAtStart.Trim()}'.", MessageType.None);
                }
                else
                {
                    float listHeight = Mathf.Min(shownItems.Count * ConfigRowHeight + 6f, ConfigListMaxHeight);
                    configListScroll = EditorGUILayout.BeginScrollView(configListScroll, EditorStyles.helpBox, GUILayout.Height(listHeight));

                    foreach (ConfigItem item in shownItems)
                    {
                        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(ConfigRowHeight - 2f)))
                        {
                            bool newSelected = EditorGUILayout.ToggleLeft(
                                new GUIContent(item.DisplayName, item.Path), item.Selected, GUILayout.ExpandWidth(true));
                            if (newSelected != item.Selected)
                            {
                                item.Selected = newSelected;
                                SaveSelection();
                            }

                            EditorGUILayout.LabelField(
                                $"{item.RuleCount} rule{(item.RuleCount == 1 ? "" : "s")}",
                                EditorStyles.miniLabel, GUILayout.Width(50f));

                            if (GUILayout.Button(new GUIContent("Ping", "Show this config in the Project window."), EditorStyles.miniButton, GUILayout.Width(40f)))
                                PawlygonStatus.Ping(item.Asset)();
                        }
                    }

                    EditorGUILayout.EndScrollView();
                }

                int hiddenSelected = configItems.Count(c => c.Selected && !MatchesSearch(c, searchAtStart));
                string hiddenNote = hiddenSelected > 0 ? $" ({hiddenSelected} hidden by the search, still ticked)" : string.Empty;
                EditorGUILayout.LabelField($"{selectedCount} of {total} ticked{hiddenNote}", PawlygonEditorUI.RichMiniLabelStyle);
            }
        }

        private void DrawNoConfigs()
        {
            EditorGUILayout.HelpBox(
                "There are no patch configs in this project yet. The Avatar Setup Wizard creates one for each avatar when it generates the face tracking patch.",
                MessageType.Info);
            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (PawlygonEditorUI.DrawSecondaryButton("Open Avatar Setup Wizard", 26f))
                {
                    AvatarSetupWizard.ShowWindow();
                    GUIUtility.ExitGUI();
                }

                if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Look Again", "Search the project for patch configs again."), 26f, GUILayout.Width(100f)))
                {
                    RefreshConfigs();
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void SetAllSelected(bool value, string search)
        {
            foreach (ConfigItem item in configItems)
            {
                if (item.Asset == null || !MatchesSearch(item, search)) continue;
                item.Selected = value;
            }

            SaveSelection();
        }

        private void DrawRuleBuilder(RuleDraft draftAtStart)
        {
            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                PawlygonEditorUI.DrawSectionHeader(
                    "Rule",
                    "Before patching, PatcherHub checks that this package is installed and new enough, and shows your message if it isn't. Pick an installed package to fill this in.");

                using (new EditorGUILayout.HorizontalScope())
                {
                    int newIndex = EditorGUILayout.Popup(
                        new GUIContent("Fill from Package", "Fill the rule in from a package installed in this project (common avatar packages are listed first, with ★)."),
                        selectedPackageIndex, packagePopupLabels);

                    if (GUILayout.Button(new GUIContent("Refresh", "Read the installed packages again."), EditorStyles.miniButton, GUILayout.Width(58f)))
                    {
                        RefreshInstalledPackages();
                        GUIUtility.ExitGUI();
                    }

                    if (newIndex != selectedPackageIndex)
                    {
                        PickPackage(newIndex);
                        GUIUtility.ExitGUI();
                    }
                }

                EditorGUILayout.Space(6f);

                draft.packageName = TextFieldWithPlaceholder(
                    new GUIContent("Package ID", "The package's ID as listed in the Package Manager or VRChat Creator Companion. It must match exactly."),
                    draft.packageName, "e.g. com.vrchat.avatars");

                draft.minVersion = TextFieldWithPlaceholder(
                    new GUIContent("Minimum Version", "The oldest version that works with this avatar, e.g. 1.2.3. Use \"Any\" (or leave empty) to accept every version."),
                    draft.minVersion, DefaultMinVersion);
                if (!IsPatcherHubReadableVersion(draftAtStart.minVersion))
                {
                    EditorGUILayout.HelpBox(
                        $"PatcherHub compares plain version numbers (e.g. 1.2.3) and treats anything it can't read as not installed. Use '{ToPatcherHubVersion(draftAtStart.minVersion)}' or \"Any\".",
                        MessageType.Warning);
                }

                draft.vccUrl = TextFieldWithPlaceholder(
                    new GUIContent("VCC Listing Link", "Optional. The package's VRChat Creator Companion listing (a link ending in .json). PatcherHub offers it so the user can add the package."),
                    draft.vccUrl, "optional");

                if (CommonVrcPackages.IsGloballyHandled(draftAtStart.packageName))
                {
                    EditorGUILayout.HelpBox(
                        "PatcherHub already checks this package for every avatar, so you usually don't need a rule for it.",
                        MessageType.Info);
                }

                EditorGUILayout.Space(4f);
                bool expanded = EditorGUILayout.Foldout(draftMessagesExpanded, "Messages Shown to the User", true);
                if (expanded != draftMessagesExpanded)
                {
                    draftMessagesExpanded = expanded;
                    GUIUtility.ExitGUI();
                }

                if (draftMessagesExpanded)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("When the package is missing", EditorStyles.miniBoldLabel);
                    draft.missingMessage = EditorGUILayout.TextArea(draft.missingMessage, wrappedTextAreaStyle, GUILayout.MinHeight(34f));
                    draft.missingType = SeverityPopup(draft.missingType);

                    EditorGUILayout.Space(4f);
                    EditorGUILayout.LabelField("When the installed version is too old", EditorStyles.miniBoldLabel);
                    draft.versionMessage = EditorGUILayout.TextArea(draft.versionMessage, wrappedTextAreaStyle, GUILayout.MinHeight(34f));
                    draft.versionType = SeverityPopup(draft.versionType);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(6f);
                skipDuplicates = EditorGUILayout.ToggleLeft(
                    new GUIContent("Skip configs that already have a rule for this package", "Leave those configs unchanged instead of adding a second rule."),
                    skipDuplicates);
            }
        }

        private void DrawAddRuleActionBar(List<ConfigItem> selected, RuleDraft draftAtStart)
        {
            int targetCount = selected.Count;
            bool hasPackage = !string.IsNullOrWhiteSpace(draftAtStart.packageName);
            string hint = configItems.Count == 0 ? string.Empty
                : targetCount == 0 ? "Tick at least one config."
                : !hasPackage ? "Enter or pick a package."
                : string.Empty;

            PawlygonEditorUI.BeginActionBar();

            if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Clear Form", "Empty the rule fields."), 28f, GUILayout.Width(96f)))
            {
                draft = new RuleDraft();
                lastAutoFill = null;
                selectedPackageIndex = 0;
                GUI.FocusControl(null);
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Space(6f);
                GUILayout.Label(hint, hintStyle);
            }

            using (new EditorGUI.DisabledScope(targetCount == 0 || !hasPackage))
            {
                string label = $"Add Rule to {targetCount} Config{(targetCount == 1 ? "" : "s")}";
                if (PawlygonEditorUI.DrawPrimaryButton(label, 28f, GUILayout.MinWidth(170f)))
                {
                    GUI.FocusControl(null);
                    ApplyDraftToSelected(selected);
                    GUIUtility.ExitGUI();
                }
            }

            PawlygonEditorUI.EndActionBar();
        }

        private void ApplyDraftToSelected(List<ConfigItem> selected)
        {
            int applied = 0;
            int alreadyListed = 0;
            int missingField = 0;

            foreach (ConfigItem item in selected)
            {
                if (item.Asset == null) continue;

                var so = new SerializedObject(item.Asset);
                SerializedProperty rules = so.FindProperty(ConfigSpecificField);
                if (rules == null)
                {
                    missingField++;
                    continue;
                }

                if (skipDuplicates && ContainsPackage(rules, draft.packageName))
                {
                    alreadyListed++;
                    continue;
                }

                AppendRule(rules, draft);
                so.ApplyModifiedProperties();
                item.RuleCount = rules.arraySize;
                applied++;
            }

            // Force the Edit Rules tab to re-read from the asset.
            editedConfigSerialized = null;
            editedConfigTarget = null;

            string package = draft.packageName.Trim();
            var skippedParts = new List<string>();
            if (alreadyListed > 0) skippedParts.Add($"{alreadyListed} already had it");
            if (missingField > 0) skippedParts.Add($"{missingField} can't hold rules (different PatcherHub version?)");
            string skippedNote = skippedParts.Count > 0 ? $" Skipped {string.Join(", ", skippedParts)}." : string.Empty;

            if (applied > 0)
            {
                status.Info($"Added '{package}' to {applied} config{(applied == 1 ? "" : "s")} — Ctrl+Z to undo.{skippedNote}");
            }
            else
            {
                status.Warning($"Nothing was added.{skippedNote}");
            }
        }

        // =====================================================================
        // Edit Rules tab
        // =====================================================================

        private void DrawEditRulesTab()
        {
            if (configItems.Count == 0)
            {
                using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
                {
                    PawlygonEditorUI.DrawSectionHeader("No Patch Configs Yet");
                    DrawNoConfigs();
                }
                return;
            }

            ConfigItem item = GetEditedConfig();

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string[] labels = configItems.Select(c => c.DisplayName.Replace("/", "∕")).ToArray();
                    int currentIndex = configItems.IndexOf(item);
                    int newIndex = EditorGUILayout.Popup(new GUIContent("Patch Config", "The avatar's patch config to review."), currentIndex, labels);
                    if (newIndex != currentIndex && newIndex >= 0)
                    {
                        SetEditedConfig(configItems[newIndex]);
                        GUIUtility.ExitGUI();
                    }

                    if (GUILayout.Button(new GUIContent("Ping", "Show this config in the Project window."), EditorStyles.miniButton, GUILayout.Width(44f)))
                    {
                        PawlygonStatus.Ping(item.Asset)();
                    }
                }

                EditorGUILayout.LabelField(item.Path, PawlygonEditorUI.RichMiniLabelStyle);
            }

            EditorGUILayout.Space(SectionSpacing);

            if (editedConfigTarget != item.Asset || editedConfigSerialized == null)
            {
                editedConfigTarget = item.Asset;
                editedConfigSerialized = new SerializedObject(item.Asset);
            }

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                editedConfigSerialized.Update();
                SerializedProperty rules = editedConfigSerialized.FindProperty(ConfigSpecificField);
                if (rules == null)
                {
                    PawlygonEditorUI.DrawSectionHeader("Rules");
                    EditorGUILayout.HelpBox(
                        $"This config has no '{ConfigSpecificField}' list, so it can't hold package rules. The installed PatcherHub version may be different.",
                        MessageType.Error);
                    return;
                }

                PawlygonEditorUI.DrawSectionHeader(
                    $"Rules ({rules.arraySize})",
                    "Each rule makes the patcher check a package before patching this avatar. Click a rule to edit it. Changes are saved as you type and can be undone with Ctrl+Z.");

                if (rules.arraySize == 0)
                {
                    EditorGUILayout.LabelField("This config has no package rules yet.", PawlygonEditorUI.SubLabelStyle);
                    EditorGUILayout.Space(4f);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (PawlygonEditorUI.DrawSecondaryButton(new GUIContent("Add a Rule to This Config", "Open the Add Rule tab with this config ticked."), 26f, GUILayout.Width(190f)))
                        {
                            item.Selected = true;
                            SaveSelection();
                            SwitchTab(Tab.AddRule);
                            GUIUtility.ExitGUI();
                        }
                    }
                    return;
                }

                DrawRulesList(rules, item);
            }
        }

        private ConfigItem GetEditedConfig()
        {
            ConfigItem item = configItems.FirstOrDefault(c => c.Guid == editedConfigGuid && c.Asset != null)
                ?? configItems.FirstOrDefault(c => c.Selected && c.Asset != null)
                ?? configItems.First();

            if (item.Guid != editedConfigGuid)
            {
                SetEditedConfig(item);
            }

            return item;
        }

        private void SetEditedConfig(ConfigItem item)
        {
            editedConfigGuid = item.Guid;
            EditorPrefs.SetString(EditedConfigPrefPrefix + ProjectKey, editedConfigGuid ?? string.Empty);
            editedConfigSerialized = null;
            editedConfigTarget = null;
            expandedRules.Clear();
        }

        private void DrawRulesList(SerializedProperty rules, ConfigItem item)
        {
            int removeIndex = -1;
            int moveFrom = -1;
            int moveTo = -1;
            int toggleIndex = -1;

            for (int i = 0; i < rules.arraySize; i++)
            {
                SerializedProperty element = rules.GetArrayElementAtIndex(i);
                bool expanded = expandedRules.Contains(i);

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool newExpanded = EditorGUILayout.Foldout(expanded, DescribeRule(element), true, ruleFoldoutStyle);
                        if (newExpanded != expanded)
                        {
                            toggleIndex = i;
                        }

                        SerializedProperty minVersion = element.FindPropertyRelative(MinVersionField);
                        if (minVersion != null && !IsPatcherHubReadableVersion(minVersion.stringValue))
                        {
                            PawlygonEditorUI.DrawBadge("Version unreadable", PawlygonEditorUI.BadgeKind.Warning,
                                "PatcherHub can't read this version, so the check always fails. Expand the rule to fix it.");
                        }

                        using (new EditorGUI.DisabledScope(i == 0))
                        {
                            if (GUILayout.Button(new GUIContent("▲", "Move up"), EditorStyles.miniButtonLeft, GUILayout.Width(24f)))
                            {
                                moveFrom = i;
                                moveTo = i - 1;
                            }
                        }

                        using (new EditorGUI.DisabledScope(i == rules.arraySize - 1))
                        {
                            if (GUILayout.Button(new GUIContent("▼", "Move down"), EditorStyles.miniButtonRight, GUILayout.Width(24f)))
                            {
                                moveFrom = i;
                                moveTo = i + 1;
                            }
                        }

                        if (GUILayout.Button(new GUIContent("Remove", "Remove this rule (Ctrl+Z brings it back)."), EditorStyles.miniButton, GUILayout.Width(60f)))
                        {
                            removeIndex = i;
                        }
                    }

                    if (expanded)
                    {
                        EditorGUILayout.Space(2f);
                        EditorGUI.indentLevel++;
                        DrawRuleFields(element);
                        EditorGUI.indentLevel--;
                    }
                }
            }

            if (removeIndex >= 0)
            {
                string removedName = GetPackageName(rules.GetArrayElementAtIndex(removeIndex));
                rules.DeleteArrayElementAtIndex(removeIndex);
                ShiftExpandedAfterRemove(removeIndex);
                editedConfigSerialized.ApplyModifiedProperties();
                item.RuleCount = rules.arraySize;
                status.Info($"Removed '{removedName}' from {item.DisplayName} — Ctrl+Z to undo.", "Undo", Undo.PerformUndo);
                GUIUtility.ExitGUI();
            }

            if (moveFrom >= 0 && moveTo >= 0)
            {
                rules.MoveArrayElement(moveFrom, moveTo);
                bool fromExpanded = expandedRules.Remove(moveFrom);
                bool toExpanded = expandedRules.Remove(moveTo);
                if (fromExpanded) expandedRules.Add(moveTo);
                if (toExpanded) expandedRules.Add(moveFrom);
                editedConfigSerialized.ApplyModifiedProperties();
                GUIUtility.ExitGUI();
            }

            if (editedConfigSerialized.ApplyModifiedProperties())
            {
                item.RuleCount = rules.arraySize;
            }

            if (toggleIndex >= 0)
            {
                if (!expandedRules.Remove(toggleIndex)) expandedRules.Add(toggleIndex);
                GUIUtility.ExitGUI();
            }
        }

        private void ShiftExpandedAfterRemove(int removedIndex)
        {
            var shifted = expandedRules.Where(i => i != removedIndex).Select(i => i > removedIndex ? i - 1 : i).ToList();
            expandedRules.Clear();
            foreach (int i in shifted) expandedRules.Add(i);
        }

        /// <summary>One-line summary of a rule: the package (with its installed name when known) and the version it needs.</summary>
        private string DescribeRule(SerializedProperty element)
        {
            string packageName = GetPackageName(element);
            PackageItem installed = installedPackages.FirstOrDefault(p => string.Equals(p.Name, packageName, StringComparison.OrdinalIgnoreCase));
            string name = installed != null && installed.DisplayName != installed.Name
                ? $"<b>{installed.DisplayName}</b>  ({packageName})"
                : $"<b>{packageName}</b>";

            string minVersion = element.FindPropertyRelative(MinVersionField)?.stringValue;
            string version = string.IsNullOrEmpty(minVersion) || minVersion == DefaultMinVersion ? "any version" : $"{minVersion} or newer";
            string muted = ColorUtility.ToHtmlStringRGB(PawlygonEditorUI.MutedColor);
            return $"{name}  <color=#{muted}>— {version}</color>";
        }

        private static string GetPackageName(SerializedProperty element)
        {
            SerializedProperty packageName = element.FindPropertyRelative(PackageNameField);
            return packageName != null && !string.IsNullOrEmpty(packageName.stringValue) ? packageName.stringValue : "(no package ID)";
        }

        private void DrawRuleFields(SerializedProperty element)
        {
            SerializedProperty packageName = element.FindPropertyRelative(PackageNameField);
            SerializedProperty minVersion = element.FindPropertyRelative(MinVersionField);
            bool unreadableVersion = minVersion != null && !IsPatcherHubReadableVersion(minVersion.stringValue);
            SerializedProperty vccUrl = element.FindPropertyRelative(VccUrlField);
            SerializedProperty missingError = element.FindPropertyRelative(MissingErrorField);
            SerializedProperty versionError = element.FindPropertyRelative(VersionErrorField);

            if (packageName != null)
                EditorGUILayout.PropertyField(packageName, new GUIContent("Package ID", "The package's ID, e.g. com.vrchat.avatars. It must match exactly."));
            if (minVersion != null)
            {
                EditorGUILayout.PropertyField(minVersion, new GUIContent("Minimum Version", "The oldest version that works, e.g. 1.2.3. Use \"Any\" to accept every version."));
                if (unreadableVersion)
                {
                    EditorGUILayout.HelpBox(
                        $"PatcherHub can't read '{minVersion.stringValue}', so this check always fails. Use '{ToPatcherHubVersion(minVersion.stringValue)}' or \"Any\".",
                        MessageType.Warning);
                }
            }
            if (vccUrl != null)
                EditorGUILayout.PropertyField(vccUrl, new GUIContent("VCC Listing Link", "Optional. The package's VRChat Creator Companion listing, offered when the package is missing or too old."));

            DrawMessageFields(missingError, "When the package is missing");
            DrawMessageFields(versionError, "When the installed version is too old");
        }

        private void DrawMessageFields(SerializedProperty error, string title)
        {
            if (error == null) return;

            SerializedProperty message = error.FindPropertyRelative(MessageField);
            SerializedProperty messageType = error.FindPropertyRelative(MessageTypeField);
            if (message == null || messageType == null)
            {
                // Unknown PatcherHub shape: fall back to Unity's default drawer.
                EditorGUILayout.PropertyField(error, new GUIContent(title), true);
                return;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            string newMessage = EditorGUILayout.TextArea(message.stringValue, wrappedTextAreaStyle, GUILayout.MinHeight(34f));
            if (EditorGUI.EndChangeCheck())
            {
                message.stringValue = newMessage;
            }

            DrawSeverityProperty(messageType);
        }

        /// <summary>
        /// Draws a severity enum without its "None" option (a message with no severity isn't useful), keeping
        /// "None" only when the rule already uses it.
        /// </summary>
        private static void DrawSeverityProperty(SerializedProperty messageType)
        {
            if (messageType.propertyType != SerializedPropertyType.Enum)
            {
                EditorGUILayout.PropertyField(messageType, new GUIContent("Severity", SeverityTooltip));
                return;
            }

            string[] names = messageType.enumDisplayNames;
            var indices = Enumerable.Range(0, names.Length)
                .Where(i => names[i] != "None" || i == messageType.enumValueIndex)
                .ToList();

            int current = indices.IndexOf(messageType.enumValueIndex);
            int picked = EditorGUILayout.Popup(new GUIContent("Severity", SeverityTooltip), current,
                indices.Select(i => new GUIContent(names[i])).ToArray());
            if (picked != current && picked >= 0)
            {
                messageType.enumValueIndex = indices[picked];
            }
        }

        private static MessageType SeverityPopup(MessageType value)
        {
            int current = Array.IndexOf(SeverityValues, value);
            if (current < 0) current = 1;
            int picked = EditorGUILayout.Popup(new GUIContent("Severity", SeverityTooltip), current,
                SeverityLabels.Select(l => new GUIContent(l)).ToArray());
            return SeverityValues[Mathf.Clamp(picked, 0, SeverityValues.Length - 1)];
        }

        /// <summary>A text field that shows a muted hint inside it while empty.</summary>
        private string TextFieldWithPlaceholder(GUIContent label, string value, string placeholder)
        {
            string result = EditorGUILayout.TextField(label, value);

            if (Event.current.type == EventType.Repaint && string.IsNullOrEmpty(result))
            {
                Rect rect = GUILayoutUtility.GetLastRect();
                rect.xMin += EditorGUIUtility.labelWidth + 2f;
                GUI.Label(rect, placeholder, placeholderStyle);
            }

            return result;
        }

        // =====================================================================
        // Config discovery and selection memory
        // =====================================================================

        /// <summary>
        /// Returns the PatcherHub FTPatchConfig type, resolving it via reflection only on the first
        /// call for this window instance (null when PatcherHub is not installed).
        /// </summary>
        private Type GetConfigType()
        {
            if (!configTypeResolved)
            {
                cachedConfigType = FTPatchConfigGenerator.GetFTPatchConfigType();
                configTypeResolved = true;
            }

            return cachedConfigType;
        }

        private void RefreshConfigs()
        {
            // Ticks are remembered per project (by asset GUID), so they survive refreshes and reopening the window.
            HashSet<string> selectedGuids = LoadSelection();

            configItems.Clear();

            Type configType = GetConfigType();
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
                    Guid = guid,
                    Path = path,
                    DisplayName = ResolveConfigLabel(asset, path),
                    RuleCount = GetRuleCount(asset),
                    Selected = selectedGuids.Contains(guid)
                });
            }

            configItems.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            editedConfigSerialized = null;
            editedConfigTarget = null;
        }

        private static HashSet<string> LoadSelection()
        {
            string stored = EditorPrefs.GetString(SelectedConfigsPrefPrefix + ProjectKey, string.Empty);
            return new HashSet<string>(stored.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private void SaveSelection()
        {
            // Keep ticks of configs that are temporarily missing (e.g. mid-reimport) as well as the current ones.
            HashSet<string> stored = LoadSelection();
            foreach (ConfigItem item in configItems)
            {
                if (item.Selected) stored.Add(item.Guid);
                else stored.Remove(item.Guid);
            }

            EditorPrefs.SetString(SelectedConfigsPrefPrefix + ProjectKey, string.Join(";", stored));
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

        /// <summary>
        /// Whether a config is visible with the given search text. Ticked configs stay ticked (and
        /// receive the rule) while hidden; the list footer says how many are hidden.
        /// </summary>
        private static bool MatchesSearch(ConfigItem item, string search)
        {
            string filter = search?.Trim();
            if (string.IsNullOrEmpty(filter)) return true;
            return (item.DisplayName?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (item.Path?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
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
                status.Warning($"Couldn't read the installed packages: {ex.Message}");
            }

            // Curated common avatar packages first (in catalog order), then everything else by name.
            installedPackages.Sort((a, b) =>
            {
                if (a.Priority != b.Priority) return a.Priority.CompareTo(b.Priority);
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            var labels = new List<string> { CustomPackageLabel };
            labels.AddRange(installedPackages.Select(p =>
                $"{(p.Priority != int.MaxValue ? "★ " : string.Empty)}{p.DisplayName}   ({p.Name})   v{p.Version}".Replace("/", "∕")));
            packagePopupLabels = labels.ToArray();

            if (selectedPackageIndex >= packagePopupLabels.Length)
            {
                selectedPackageIndex = 0;
            }
        }

        /// <summary>
        /// Handles a pick in the "Fill from Package" list: fills the rule in from that package, asking first
        /// when that would replace something the user typed.
        /// </summary>
        private void PickPackage(int newIndex)
        {
            int packageIndex = newIndex - 1; // index 0 is the "type it in" entry
            if (packageIndex < 0 || packageIndex >= installedPackages.Count)
            {
                selectedPackageIndex = 0;
                return;
            }

            PackageItem pkg = installedPackages[packageIndex];
            bool typedByUser = !draft.IsEmpty && (lastAutoFill == null || !draft.SameAs(lastAutoFill));
            if (typedByUser && !EditorUtility.DisplayDialog(
                    "Replace the Rule Fields",
                    $"Fill the rule in from {pkg.DisplayName}? This replaces the package ID, version, link and messages you entered.",
                    "Replace", "Cancel"))
            {
                return;
            }

            selectedPackageIndex = newIndex;
            draft = BuildDraftFromPackage(pkg);
            lastAutoFill = draft.Clone();
            draftMessagesExpanded = true;
            status.Clear();
            GUI.FocusControl(null);
        }

        private static RuleDraft BuildDraftFromPackage(PackageItem pkg)
        {
            string minVersion = string.IsNullOrEmpty(pkg.Version) ? DefaultMinVersion : ToPatcherHubVersion(pkg.Version);
            return new RuleDraft
            {
                packageName = pkg.Name,
                minVersion = minVersion,
                vccUrl = pkg.VccUrl ?? string.Empty,
                missingMessage = $"{pkg.DisplayName} is required for this avatar but is not installed. Please install it before patching.",
                missingType = MessageType.Warning,
                versionMessage = $"This avatar requires {pkg.DisplayName} {minVersion} or newer. Please update {pkg.DisplayName} before patching.",
                versionType = MessageType.Warning
            };
        }

        // =====================================================================
        // Rule helpers
        // =====================================================================

        private static bool ContainsPackage(SerializedProperty rules, string packageName)
        {
            string wanted = packageName?.Trim();
            for (int i = 0; i < rules.arraySize; i++)
            {
                SerializedProperty name = rules.GetArrayElementAtIndex(i).FindPropertyRelative(PackageNameField);
                if (name != null && string.Equals(name.stringValue?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
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

            SetString(element, PackageNameField, draft.packageName?.Trim());
            SetString(element, MinVersionField, string.IsNullOrWhiteSpace(draft.minVersion) ? DefaultMinVersion : draft.minVersion.Trim());
            SetString(element, VccUrlField, draft.vccUrl?.Trim());

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

        /// <summary>
        /// PatcherHub checks requirements with <c>new System.Version(installed) &gt;= new System.Version(min)</c> and
        /// treats a parse failure as "too old". Prerelease and build suffixes (e.g. <c>3.8.0-beta.1</c>,
        /// <c>1.2.0+abc</c>) can't be parsed, so they are stripped to the plain version.
        /// </summary>
        private static string ToPatcherHubVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return DefaultMinVersion;

            string trimmed = version.Trim();
            int suffix = trimmed.IndexOfAny(new[] { '-', '+' });
            if (suffix > 0) trimmed = trimmed.Substring(0, suffix);
            return Version.TryParse(trimmed, out _) ? trimmed : DefaultMinVersion;
        }

        private static bool IsPatcherHubReadableVersion(string version)
        {
            return string.IsNullOrWhiteSpace(version) || version.Trim() == DefaultMinVersion || Version.TryParse(version.Trim(), out _);
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
            public string Guid;
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

            /// <summary>Nothing has been entered (the version still says "Any").</summary>
            public bool IsEmpty =>
                string.IsNullOrWhiteSpace(packageName) &&
                (string.IsNullOrWhiteSpace(minVersion) || minVersion.Trim() == DefaultMinVersion) &&
                string.IsNullOrWhiteSpace(vccUrl) &&
                string.IsNullOrWhiteSpace(missingMessage) &&
                string.IsNullOrWhiteSpace(versionMessage);

            public RuleDraft Clone()
            {
                return (RuleDraft)MemberwiseClone();
            }

            public bool SameAs(RuleDraft other)
            {
                return other != null &&
                       packageName == other.packageName &&
                       minVersion == other.minVersion &&
                       vccUrl == other.vccUrl &&
                       missingMessage == other.missingMessage &&
                       missingType == other.missingType &&
                       versionMessage == other.versionMessage &&
                       versionType == other.versionType;
            }
        }
    }
}
