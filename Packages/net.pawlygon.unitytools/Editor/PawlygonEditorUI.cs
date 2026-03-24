using System;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Shared UI helpers and styles for Pawlygon editor windows and inspectors.
    /// Call <see cref="EnsureStyles"/> once per OnGUI frame before using any style or drawing method.
    /// </summary>
    public static class PawlygonEditorUI
    {
        // --- Branding URLs ---
        private const string WebsiteUrl = "https://www.pawlygon.net";
        private const string TwitterUrl = "https://x.com/Pawlygon_studio";
        private const string YouTubeUrl = "https://www.youtube.com/@Pawlygon";
        private const string DiscordUrl = "https://discord.com/invite/pZew3JGpjb";

        private const string PackageJsonPath = "Packages/net.pawlygon.unitytools/package.json";

        // --- Cached resources ---
        private static Texture2D logoTexture;
        private static string packageVersion;

        // --- Shared styles ---
        public static GUIStyle SectionStyle { get; private set; }
        public static GUIStyle TitleStyle { get; private set; }
        public static GUIStyle SubLabelStyle { get; private set; }
        public static GUIStyle RichMiniLabelStyle { get; private set; }

        private static GUIStyle headerTitleStyle;
        private static GUIStyle headerSubtitleStyle;
        private static GUIStyle footerStyle;
        private static GUIStyle footerLinkStyle;
        private static GUIStyle headerBoxStyle;
        private static GUIStyle footerBoxStyle;
        private static GUIStyle primaryButtonStyle;
        private static GUIStyle sectionBoxStyle;
        private static GUIStyle sectionTitleStyle;

        // =====================================================================
        // Style Initialization
        // =====================================================================

        /// <summary>
        /// Ensures all shared styles are initialized. Safe to call every frame;
        /// styles are created only once and then cached.
        /// </summary>
        public static void EnsureStyles()
        {
            if (SectionStyle != null)
            {
                return;
            }

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

            SubLabelStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.72f, 0.72f, 0.72f)
                : new Color(0.35f, 0.35f, 0.35f);

            RichMiniLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                richText = true,
                wordWrap = true
            };

            headerTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleLeft
            };

            headerSubtitleStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true
            };

            headerSubtitleStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.72f, 0.72f, 0.72f)
                : new Color(0.35f, 0.35f, 0.35f);

            footerStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };

            footerLinkStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                stretchWidth = false
            };

            footerLinkStyle.normal.textColor = new Color(0.39f, 0.67f, 1f);
            footerLinkStyle.hover.textColor = new Color(0.58f, 0.79f, 1f);

            headerBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(14, 14, 12, 12)
            };

            footerBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(12, 12, 10, 10)
            };

            sectionBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(15, 15, 15, 15)
            };

            sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };
        }

        // =====================================================================
        // Header
        // =====================================================================

        /// <summary>
        /// Draws the branded Pawlygon header with logo, title, and subtitle.
        /// </summary>
        /// <param name="title">The window/tool title shown next to the logo.</param>
        /// <param name="subtitle">A short description shown below the title.</param>
        public static void DrawHeader(string title, string subtitle)
        {
            if (logoTexture == null)
            {
                logoTexture = Resources.Load<Texture2D>("pawlygon_logo");
            }

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.VerticalScope(headerBoxStyle))
            {
                Rect headerRect = EditorGUILayout.GetControlRect(false, 84f);

                float logoSize = 70f;
                float spacing = 16f;
                float textBlockWidth = Mathf.Min(520f, Mathf.Max(300f, headerRect.width - 140f));
                float totalWidth = logoSize + spacing + textBlockWidth;
                float startX = headerRect.x + Mathf.Max(0f, (headerRect.width - totalWidth) * 0.5f);
                float logoY = headerRect.y + (headerRect.height - logoSize) * 0.5f;
                float textX = startX + logoSize + spacing;
                float textWidth = textBlockWidth;

                if (logoTexture != null)
                {
                    GUI.DrawTexture(new Rect(startX, logoY, logoSize, logoSize), logoTexture, ScaleMode.ScaleToFit, true);
                }

                EditorGUI.LabelField(new Rect(textX, headerRect.y + 18f, textWidth, 22f), title, headerTitleStyle);
                EditorGUI.LabelField(new Rect(textX, headerRect.y + 40f, textWidth, 36f), subtitle, headerSubtitleStyle);
            }

            EditorGUILayout.Space(6f);
        }

        // =====================================================================
        // Footer
        // =====================================================================

        /// <summary>
        /// Draws the branded Pawlygon footer with version and social links.
        /// </summary>
        public static void DrawFooter()
        {
            string version = GetPackageVersion();

            using (new EditorGUILayout.VerticalScope(footerBoxStyle))
            {
                EditorGUILayout.LabelField($"Made with \u2764 by Pawlygon Studio  \u2022  v{version}", footerStyle);
                EditorGUILayout.Space(6f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    DrawFooterLink("Website", WebsiteUrl);
                    GUILayout.Space(28f);
                    DrawFooterLink("X (Twitter)", TwitterUrl);
                    GUILayout.Space(28f);
                    DrawFooterLink("YouTube", YouTubeUrl);
                    GUILayout.Space(28f);
                    DrawFooterLink("Discord", DiscordUrl);
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private static void DrawFooterLink(string label, string url)
        {
            if (GUILayout.Button(label, footerLinkStyle))
            {
                Application.OpenURL(url);
            }
        }

        // =====================================================================
        // Reusable Drawing Helpers
        // =====================================================================

        /// <summary>
        /// Draws a styled primary action button with blue tinting.
        /// </summary>
        /// <returns>True if the button was clicked.</returns>
        public static bool DrawPrimaryButton(string text, float height = 34f)
        {
            if (primaryButtonStyle == null)
            {
                primaryButtonStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 };
            }

            Color oldColor = GUI.backgroundColor;
            GUI.backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.2f, 0.6f, 1f)
                : new Color(0.1f, 0.4f, 0.8f);
            bool clicked = GUILayout.Button(text, primaryButtonStyle, GUILayout.Height(height));
            GUI.backgroundColor = oldColor;
            return clicked;
        }

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
            using (new EditorGUILayout.VerticalScope(sectionBoxStyle))
            {
                EditorGUILayout.LabelField(title, sectionTitleStyle);
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(description, SubLabelStyle);
                EditorGUILayout.Space(12);
                drawContent?.Invoke();
            }
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
}
