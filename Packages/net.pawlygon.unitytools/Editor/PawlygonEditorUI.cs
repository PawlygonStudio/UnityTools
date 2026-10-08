using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Shared UI helpers and styles for Pawlygon editor windows and inspectors.
    /// Call <see cref="EnsureStyles"/> once per OnGUI frame before using any style or drawing method.
    ///
    /// Window layout convention (top to bottom):
    /// <list type="number">
    /// <item><see cref="DrawHeader"/>: compact one-line branded header.</item>
    /// <item><see cref="DrawAvatarBar"/> when the tool works on an avatar.</item>
    /// <item><see cref="DrawTabBar"/> when the tool has tabs or steps.</item>
    /// <item>A scroll view with the content.</item>
    /// <item><see cref="DrawStatusBar"/>: pinned, so results and errors are always visible.</item>
    /// <item>An action bar (<see cref="BeginActionBar"/>) with at most one primary button.</item>
    /// <item><see cref="DrawFooter"/>.</item>
    /// </list>
    /// </summary>
    public static class PawlygonEditorUI
    {
        // --- Branding URLs ---
        private const string WebsiteUrl = "https://www.pawlygon.net";
        private const string TwitterUrl = "https://x.com/Pawlygon_studio";
        private const string YouTubeUrl = "https://www.youtube.com/@Pawlygon";
        private const string DiscordUrl = "https://discord.com/invite/pZew3JGpjb";

        /// <summary>Documentation home, used as the default help link.</summary>
        public const string DocumentationUrl = "https://github.com/PawlygonStudio/UnityTools#readme";

        private const string PackageJsonPath = "Packages/net.pawlygon.unitytools/package.json";

        private const float HeaderHeight = 30f;
        private const float HeaderLogoSize = 26f;

        // --- Cached resources ---
        private static Texture2D logoTexture;
        private static string packageVersion;

        // --- Shared styles ---
        public static GUIStyle SectionStyle { get; private set; }
        public static GUIStyle TitleStyle { get; private set; }
        public static GUIStyle SubLabelStyle { get; private set; }
        public static GUIStyle RichMiniLabelStyle { get; private set; }

        /// <summary>Bold 13pt title for sections inside a window.</summary>
        public static GUIStyle SectionTitleStyle { get; private set; }

        /// <summary>Rich-text label for inline status text (supports <c>&lt;color&gt;</c> and <c>&lt;b&gt;</c>).</summary>
        public static GUIStyle RichLabelStyle { get; private set; }

        private static GUIStyle headerTitleStyle;
        private static GUIStyle headerSubtitleStyle;
        private static GUIStyle footerStyle;
        private static GUIStyle footerLinkStyle;
        private static GUIStyle headerBoxStyle;
        private static GUIStyle footerBoxStyle;
        private static GUIStyle primaryButtonStyle;
        private static GUIStyle secondaryButtonStyle;
        private static GUIStyle sectionBoxStyle;
        private static GUIStyle sectionTitleStyle;
        private static GUIStyle badgeStyle;
        private static GUIStyle actionBarStyle;
        private static GUIStyle statusTextStyle;
        private static bool stylesBuiltForProSkin;

        // --- Shared colours ---
        public static Color OkColor => EditorGUIUtility.isProSkin ? new Color(0.42f, 0.8f, 0.47f) : new Color(0.16f, 0.55f, 0.22f);
        public static Color WarningColor => EditorGUIUtility.isProSkin ? new Color(1f, 0.72f, 0.3f) : new Color(0.75f, 0.45f, 0f);
        public static Color ErrorColor => EditorGUIUtility.isProSkin ? new Color(1f, 0.45f, 0.42f) : new Color(0.75f, 0.15f, 0.12f);
        public static Color InfoColor => EditorGUIUtility.isProSkin ? new Color(0.45f, 0.7f, 1f) : new Color(0.12f, 0.4f, 0.75f);
        public static Color MutedColor => EditorGUIUtility.isProSkin ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.4f, 0.4f, 0.4f);

        // =====================================================================
        // Style Initialization
        // =====================================================================

        /// <summary>
        /// Ensures all shared styles are initialized. Safe to call every frame;
        /// styles are created only once and then cached.
        /// </summary>
        public static void EnsureStyles()
        {
            // Several styles bake in skin-dependent colours, so rebuild them when the editor skin changes.
            if (SectionStyle != null && stylesBuiltForProSkin == EditorGUIUtility.isProSkin)
            {
                return;
            }

            stylesBuiltForProSkin = EditorGUIUtility.isProSkin;
            Color muted = EditorGUIUtility.isProSkin ? new Color(0.72f, 0.72f, 0.72f) : new Color(0.35f, 0.35f, 0.35f);

            SectionStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 12, 12),
                margin = new RectOffset(0, 0, 0, 0)
            };

            TitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                fixedHeight = 24f
            };

            SubLabelStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = true
            };
            SubLabelStyle.normal.textColor = muted;

            RichMiniLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                richText = true,
                wordWrap = true
            };

            SectionTitleStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };

            RichLabelStyle = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };

            headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft
            };

            headerSubtitleStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };
            headerSubtitleStyle.normal.textColor = muted;

            footerStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };
            footerStyle.normal.textColor = muted;

            footerLinkStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                stretchWidth = false
            };
            footerLinkStyle.normal.textColor = new Color(0.39f, 0.67f, 1f);
            footerLinkStyle.hover.textColor = new Color(0.58f, 0.79f, 1f);

            headerBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 4, 4),
                margin = new RectOffset(4, 4, 4, 2)
            };

            footerBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 3, 3),
                margin = new RectOffset(4, 4, 2, 4)
            };

            sectionBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(15, 15, 15, 15)
            };

            sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };

            badgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(6, 6, 1, 1),
                margin = new RectOffset(2, 2, 2, 2)
            };

            actionBarStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 6, 6),
                margin = new RectOffset(4, 4, 2, 2)
            };

            statusTextStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                richText = true,
                alignment = TextAnchor.MiddleLeft
            };
        }

        // =====================================================================
        // Header
        // =====================================================================

        /// <summary>
        /// Draws the compact branded header: a small logo, the title and a one-line subtitle (the full text
        /// is in the tooltip when it doesn't fit), with an optional help button that opens <paramref name="helpUrl"/>.
        /// </summary>
        /// <param name="title">The window/tool title shown next to the logo.</param>
        /// <param name="subtitle">A short description shown after the title.</param>
        /// <param name="helpUrl">Documentation link for the "?" button; null hides the button.</param>
        public static void DrawHeader(string title, string subtitle, string helpUrl = null)
        {
            EnsureStyles();

            if (logoTexture == null)
            {
                logoTexture = Resources.Load<Texture2D>("pawlygon_logo");
            }

            using (new EditorGUILayout.VerticalScope(headerBoxStyle))
            {
                Rect row = EditorGUILayout.GetControlRect(false, HeaderHeight);
                float x = row.x;

                if (logoTexture != null)
                {
                    GUI.DrawTexture(new Rect(x, row.y + (row.height - HeaderLogoSize) * 0.5f, HeaderLogoSize, HeaderLogoSize),
                        logoTexture, ScaleMode.ScaleToFit, true);
                    x += HeaderLogoSize + 8f;
                }

                float helpWidth = string.IsNullOrEmpty(helpUrl) ? 0f : 24f;
                var titleContent = new GUIContent(title);
                float titleWidth = headerTitleStyle.CalcSize(titleContent).x;
                EditorGUI.LabelField(new Rect(x, row.y, titleWidth, row.height), titleContent, headerTitleStyle);
                x += titleWidth + 10f;

                float subtitleWidth = row.xMax - helpWidth - x;
                if (!string.IsNullOrEmpty(subtitle) && subtitleWidth > 40f)
                {
                    EditorGUI.LabelField(new Rect(x, row.y, subtitleWidth, row.height), new GUIContent(subtitle, subtitle), headerSubtitleStyle);
                }

                if (helpWidth > 0f)
                {
                    var helpContent = new GUIContent(EditorGUIUtility.IconContent("_Help").image, "Open the documentation");
                    if (GUI.Button(new Rect(row.xMax - 22f, row.y + (row.height - 20f) * 0.5f, 22f, 20f), helpContent, EditorStyles.iconButton))
                    {
                        Application.OpenURL(helpUrl);
                    }
                }
            }
        }

        // =====================================================================
        // Footer
        // =====================================================================

        /// <summary>
        /// Draws the compact branded footer: version on the left, social links on the right.
        /// </summary>
        public static void DrawFooter()
        {
            EnsureStyles();
            string version = GetPackageVersion();

            using (new EditorGUILayout.HorizontalScope(footerBoxStyle))
            {
                GUILayout.Label($"Made with ❤ by Pawlygon Studio  •  v{version}", footerStyle);
                GUILayout.FlexibleSpace();
                DrawFooterLink("Website", WebsiteUrl);
                DrawFooterLink("X", TwitterUrl);
                DrawFooterLink("YouTube", YouTubeUrl);
                DrawFooterLink("Discord", DiscordUrl);
            }
        }

        private static void DrawFooterLink(string label, string url)
        {
            if (GUILayout.Button(label, footerLinkStyle))
            {
                Application.OpenURL(url);
            }

            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
        }

        // =====================================================================
        // Buttons
        // =====================================================================

        /// <summary>
        /// Draws the screen's main action: a blue, bold button. Use at most one per view.
        /// </summary>
        /// <returns>True if the button was clicked.</returns>
        public static bool DrawPrimaryButton(string text, float height = 30f, params GUILayoutOption[] options)
        {
            return DrawPrimaryButton(new GUIContent(text), height, options);
        }

        /// <summary>
        /// Primary button with a tooltip, e.g. to explain why the main action is disabled.
        /// </summary>
        public static bool DrawPrimaryButton(GUIContent content, float height = 30f, params GUILayoutOption[] options)
        {
            if (primaryButtonStyle == null)
            {
                primaryButtonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 12 };
            }

            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.2f, 0.6f, 1f)
                : new Color(0.1f, 0.4f, 0.8f);
            bool clicked = GUILayout.Button(content, primaryButtonStyle, WithHeight(height, options));
            GUI.backgroundColor = oldColor;
            return clicked;
        }

        /// <summary>
        /// Draws a normal (secondary) button with the same height as primary buttons, for every action that
        /// isn't the screen's main one.
        /// </summary>
        public static bool DrawSecondaryButton(string text, float height = 30f, params GUILayoutOption[] options)
        {
            return DrawSecondaryButton(new GUIContent(text), height, options);
        }

        /// <inheritdoc cref="DrawSecondaryButton(string, float, GUILayoutOption[])"/>
        public static bool DrawSecondaryButton(GUIContent content, float height = 30f, params GUILayoutOption[] options)
        {
            if (secondaryButtonStyle == null)
            {
                secondaryButtonStyle = new GUIStyle(GUI.skin.button);
            }

            return GUILayout.Button(content, secondaryButtonStyle, WithHeight(height, options));
        }

        private static GUILayoutOption[] WithHeight(float height, GUILayoutOption[] options)
        {
            var all = new List<GUILayoutOption> { GUILayout.Height(height) };
            if (options != null) all.AddRange(options);
            return all.ToArray();
        }

        // =====================================================================
        // Sections
        // =====================================================================

        /// <summary>
        /// Draws a thin 1px horizontal separator line.
        /// </summary>
        public static void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1f);
            rect.height = 1f;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
        }

        /// <summary>
        /// Draws a boxed section with a bold title, description, and custom content.
        /// </summary>
        public static void DrawSection(string title, string description, Action drawContent)
        {
            EnsureStyles();
            using (new EditorGUILayout.VerticalScope(sectionBoxStyle))
            {
                EditorGUILayout.LabelField(title, sectionTitleStyle);
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(description, SubLabelStyle);
                EditorGUILayout.Space(12);
                drawContent?.Invoke();
            }
        }

        /// <summary>
        /// Draws a section title with an optional one-line description below it.
        /// </summary>
        public static void DrawSectionHeader(string title, string description = null)
        {
            EnsureStyles();
            EditorGUILayout.LabelField(title, SectionTitleStyle);
            if (!string.IsNullOrEmpty(description))
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField(description, SubLabelStyle);
            }
            EditorGUILayout.Space(6f);
        }

        // =====================================================================
        // Badges
        // =====================================================================

        public enum BadgeKind { Ok, Warning, Error, Info, Neutral }

        public static Color GetBadgeColor(BadgeKind kind)
        {
            switch (kind)
            {
                case BadgeKind.Ok: return OkColor;
                case BadgeKind.Warning: return WarningColor;
                case BadgeKind.Error: return ErrorColor;
                case BadgeKind.Info: return InfoColor;
                default: return MutedColor;
            }
        }

        /// <summary>
        /// Draws a small pill-shaped status badge (e.g. "Protected", "Needs repair") inside a horizontal layout.
        /// </summary>
        public static void DrawBadge(string text, BadgeKind kind, string tooltip = null)
        {
            EnsureStyles();
            var content = new GUIContent(text, tooltip);
            Vector2 size = badgeStyle.CalcSize(content);
            Rect rect = GUILayoutUtility.GetRect(content, badgeStyle, GUILayout.Width(size.x), GUILayout.Height(16f));

            Color color = GetBadgeColor(kind);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(color.r, color.g, color.b, EditorGUIUtility.isProSkin ? 0.22f : 0.16f));
            }

            Color old = badgeStyle.normal.textColor;
            badgeStyle.normal.textColor = color;
            GUI.Label(rect, content, badgeStyle);
            badgeStyle.normal.textColor = old;
        }

        /// <summary>
        /// Rich-text version of a badge for use inside labels with <c>richText</c> enabled.
        /// </summary>
        public static string BadgeText(string text, BadgeKind kind)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(GetBadgeColor(kind))}><b>{text}</b></color>";
        }

        // =====================================================================
        // Status bar
        // =====================================================================

        /// <summary>
        /// Draws <paramref name="status"/> as a pinned bar with an icon, an optional action button and a dismiss
        /// button. Draws nothing when there is no message. Place it outside the scroll view so it stays visible.
        /// Setting a message from inside OnGUI adds the bar in the same event, so follow the change with
        /// <c>GUIUtility.ExitGUI()</c> to avoid layout errors.
        /// </summary>
        public static void DrawStatusBar(PawlygonStatus status)
        {
            if (status == null || !status.HasMessage) return;
            EnsureStyles();

            string iconName = status.Type == MessageType.Error ? "console.erroricon.sml"
                : status.Type == MessageType.Warning ? "console.warnicon.sml"
                : "console.infoicon.sml";

            using (new EditorGUILayout.HorizontalScope(actionBarStyle))
            {
                GUILayout.Label(EditorGUIUtility.IconContent(iconName), GUILayout.Width(18f), GUILayout.Height(18f));
                GUILayout.Label(status.Message, statusTextStyle);

                if (!string.IsNullOrEmpty(status.ActionLabel) && status.Action != null)
                {
                    if (GUILayout.Button(status.ActionLabel, GUILayout.ExpandWidth(false), GUILayout.Height(20f)))
                    {
                        Action action = status.Action;
                        action();
                        GUIUtility.ExitGUI();
                    }
                }

                if (GUILayout.Button(new GUIContent("✕", "Dismiss"), EditorStyles.miniButton, GUILayout.Width(22f), GUILayout.Height(20f)))
                {
                    status.Clear();
                    GUIUtility.ExitGUI();
                }
            }
        }

        // =====================================================================
        // Action bar
        // =====================================================================

        /// <summary>
        /// Starts a pinned horizontal action bar (draw it outside the scroll view, above the footer). Put
        /// secondary actions first, then <c>GUILayout.FlexibleSpace()</c>, then the single primary button.
        /// Close with <see cref="EndActionBar"/>.
        /// </summary>
        public static void BeginActionBar()
        {
            EnsureStyles();
            EditorGUILayout.BeginHorizontal(actionBarStyle);
        }

        public static void EndActionBar()
        {
            EditorGUILayout.EndHorizontal();
        }

        // =====================================================================
        // Tabs / steps
        // =====================================================================

        /// <summary>
        /// One tab or step in <see cref="DrawTabBar"/>.
        /// </summary>
        public struct TabSpec
        {
            public string Label;

            /// <summary>Shows a green tick: the tab's work is complete.</summary>
            public bool Done;

            /// <summary>The tab can't be opened yet; <see cref="Tooltip"/> should say why.</summary>
            public bool Locked;

            public string Tooltip;

            public TabSpec(string label, bool done = false, bool locked = false, string tooltip = null)
            {
                Label = label;
                Done = done;
                Locked = locked;
                Tooltip = tooltip;
            }
        }

        /// <summary>
        /// Draws a segmented tab/step bar. Done tabs show a green tick; locked tabs are disabled with their
        /// tooltip. Returns the index the user clicked (only when it differs from <paramref name="current"/>),
        /// otherwise -1. Call <c>GUIUtility.ExitGUI()</c> after switching to avoid layout errors.
        /// </summary>
        public static int DrawTabBar(IList<TabSpec> tabs, int current)
        {
            EnsureStyles();
            int clicked = -1;

            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < tabs.Count; i++)
                {
                    TabSpec tab = tabs[i];
                    string styleName = tabs.Count == 1 ? "LargeButton"
                        : i == 0 ? "LargeButtonLeft"
                        : i == tabs.Count - 1 ? "LargeButtonRight"
                        : "LargeButtonMid";
                    GUIStyle style = GUI.skin.FindStyle(styleName) ?? EditorStyles.miniButton;

                    bool showTick = tab.Done && !tab.Locked;
                    var content = new GUIContent(
                        showTick ? $" {tab.Label}" : tab.Label,
                        showTick ? EditorGUIUtility.IconContent("TestPassed").image : null,
                        tab.Tooltip);

                    using (new EditorGUI.DisabledScope(tab.Locked))
                    {
                        bool selected = GUILayout.Toggle(i == current, content, style, GUILayout.Height(24f));
                        if (selected && i != current) clicked = i;
                    }
                }
            }

            return clicked;
        }

        // =====================================================================
        // Avatar bar
        // =====================================================================

        private static readonly Dictionary<EditorWindow, GameObject> pendingAvatarPicks = new Dictionary<EditorWindow, GameObject>();

        /// <summary>
        /// Draws the shared avatar picker: an object field (drag-and-drop and ping), a dropdown of the avatars in
        /// the open scenes, and "Use Selection", which picks the avatar that contains the selected object.
        /// Returns true when the avatar changed; the choice is remembered for the session so other Pawlygon
        /// tools open on the same avatar.
        /// </summary>
        /// <param name="owner">The window drawing the bar (used to deliver dropdown picks).</param>
        /// <param name="avatar">The current avatar; updated when the user picks another.</param>
        /// <param name="label">Field label.</param>
        /// <param name="allowAssets">Also accept prefab/model assets (e.g. for checks that don't need a scene object).</param>
        public static bool DrawAvatarBar(EditorWindow owner, ref GameObject avatar, string label = "Avatar", bool allowAssets = false)
        {
            EnsureStyles();
            bool changed = false;

            if (owner != null && pendingAvatarPicks.TryGetValue(owner, out GameObject picked))
            {
                pendingAvatarPicks.Remove(owner);
                if (picked != avatar)
                {
                    avatar = picked;
                    changed = true;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var newAvatar = (GameObject)EditorGUILayout.ObjectField(label, avatar, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck() && newAvatar != avatar)
                {
                    if (newAvatar != null && !allowAssets && EditorUtility.IsPersistent(newAvatar))
                    {
                        EditorUtility.DisplayDialog("Pick a Scene Avatar", "Drag the avatar from the scene (Hierarchy), not from the Project window.", "OK");
                    }
                    else
                    {
                        avatar = newAvatar != null && !EditorUtility.IsPersistent(newAvatar)
                            ? PawlygonEditorUtils.FindAvatarRoot(newAvatar) ?? newAvatar
                            : newAvatar;
                        changed = true;
                    }
                }

                List<GameObject> sceneAvatars = PawlygonEditorUtils.FindSceneAvatars();
                using (new EditorGUI.DisabledScope(sceneAvatars.Count == 0 || owner == null))
                {
                    var dropdown = new GUIContent("Scene ▾", sceneAvatars.Count == 0 ? "No avatars in the open scenes." : "Pick one of the avatars in the open scenes.");
                    if (GUILayout.Button(dropdown, EditorStyles.miniButton, GUILayout.Width(EditorStyles.miniButton.CalcSize(dropdown).x + 4f)))
                    {
                        ShowSceneAvatarMenu(owner, sceneAvatars, avatar);
                    }
                }

                GameObject fromSelection = GetSelectionAvatar(allowAssets);
                using (new EditorGUI.DisabledScope(fromSelection == null || fromSelection == avatar))
                {
                    var useSelection = new GUIContent("Use Selection", fromSelection != null
                        ? $"Use '{fromSelection.name}', the avatar containing the selected object."
                        : "Select an avatar (or any object inside it) in the Hierarchy.");
                    if (GUILayout.Button(useSelection, EditorStyles.miniButton, GUILayout.Width(EditorStyles.miniButton.CalcSize(useSelection).x + 4f)))
                    {
                        avatar = fromSelection;
                        changed = true;
                    }
                }
            }

            if (changed && avatar != null && !EditorUtility.IsPersistent(avatar))
            {
                PawlygonEditorUtils.RememberAvatar(avatar);
            }

            return changed;
        }

        private static void ShowSceneAvatarMenu(EditorWindow owner, List<GameObject> sceneAvatars, GameObject current)
        {
            var menu = new GenericMenu();
            var nameCounts = new Dictionary<string, int>();
            foreach (GameObject candidate in sceneAvatars)
            {
                nameCounts.TryGetValue(candidate.name, out int seen);
                nameCounts[candidate.name] = seen + 1;

                // Same-named avatars (PC/Quest variants) get their scene and a number so they can be told apart.
                string itemName = sceneAvatars.Count(a => a.name == candidate.name) > 1
                    ? $"{candidate.name} ({seen + 1}, {candidate.scene.name})"
                    : candidate.name;

                GameObject target = candidate;
                menu.AddItem(new GUIContent(itemName.Replace("/", "∕")), candidate == current, () =>
                {
                    pendingAvatarPicks[owner] = target;
                    owner.Repaint();
                });
            }
            menu.ShowAsContext();
        }

        private static GameObject GetSelectionAvatar(bool allowAssets)
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null) return null;
            if (EditorUtility.IsPersistent(selected)) return allowAssets ? selected : null;
            return PawlygonEditorUtils.FindAvatarRoot(selected);
        }

        // =====================================================================
        // Package Version
        // =====================================================================

        /// <summary>
        /// Returns the cached package version string. Loaded from package.json on first call.
        /// </summary>
        public static string GetPackageVersion()
        {
            if (packageVersion != null)
            {
                return packageVersion;
            }

            packageVersion = "1.0.0";

            TextAsset packageJson = AssetDatabase.LoadAssetAtPath<TextAsset>(PackageJsonPath);
            if (packageJson == null || string.IsNullOrWhiteSpace(packageJson.text))
            {
                return packageVersion;
            }

            var manifestInfo = JsonUtility.FromJson<PackageManifestInfo>(packageJson.text);
            if (!string.IsNullOrWhiteSpace(manifestInfo?.version))
            {
                packageVersion = manifestInfo.version;
            }

            return packageVersion;
        }

        [Serializable]
        private class PackageManifestInfo
        {
            public string version;
        }
    }

    /// <summary>
    /// A status message for <see cref="PawlygonEditorUI.DrawStatusBar"/>: text, severity and an optional action
    /// (e.g. "Ping" the asset that was created). Clear it when the user switches tab, step or avatar so stale
    /// messages don't follow them around.
    /// </summary>
    public class PawlygonStatus
    {
        public string Message { get; private set; }
        public MessageType Type { get; private set; }
        public string ActionLabel { get; private set; }
        public Action Action { get; private set; }

        public bool HasMessage => !string.IsNullOrEmpty(Message);

        public void Set(string message, MessageType type = MessageType.Info, string actionLabel = null, Action action = null)
        {
            Message = message;
            Type = type;
            ActionLabel = actionLabel;
            Action = action;
        }

        public void Info(string message, string actionLabel = null, Action action = null) => Set(message, MessageType.Info, actionLabel, action);
        public void Warning(string message, string actionLabel = null, Action action = null) => Set(message, MessageType.Warning, actionLabel, action);
        public void Error(string message, string actionLabel = null, Action action = null) => Set(message, MessageType.Error, actionLabel, action);

        public void Clear()
        {
            Message = null;
            Type = MessageType.None;
            ActionLabel = null;
            Action = null;
        }

        /// <summary>Convenience action that selects and pings an asset or scene object.</summary>
        public static Action Ping(UnityEngine.Object target)
        {
            return () =>
            {
                if (target == null) return;
                Selection.activeObject = target;
                EditorGUIUtility.PingObject(target);
            };
        }
    }
}
