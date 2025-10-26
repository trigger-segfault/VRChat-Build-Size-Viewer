// VRC Build Size Viewer
// Licensed under the MIT License.
// Created by MunifiSense
// <https://github.com/MunifiSense/VRChat-Build-Size-Viewer>
//
// Modified by trigger_segfault
// <https://github.com/trigger-segfault/VRChat-Build-Size-Viewer>
//
// 2025-10-12, trigger_segfault
// * Added support for reading build logs on Linux and OSX. See:
//   <https://github.com/nfya-lab/VRChat-Build-Size-Viewer/commit/44797c50ef04cb049f7b763e5fbe0a0a6a51ca03>
// * Multiple build logs can be read and switched between.
// * `Editor-prev.log` log is now read as well.
// * Added preference to restrict how many build logs are kept in memory.
// * Language support (no translations besides English added yet).
// * Total Uncompressed Size is now shown just below Total Compressed Size.
// * File icons are shown next to file paths (if the file still exists).
// * Fancy data grid style for files and categories, with proper column right
//   alignment for numbers.
// * Unidirectional column sorting by clicking on column headers. "Extension"
//   and "Original Order" column headers are included for extra options.
// * "Go" button replaced with just clicking the file path.
// * Horizontal scrollbar is now shown if the file paths are too long.
// * Added preference for hiding the Categories list, since it takes up an
//   excessive amount of vertical space
// * Added preference to disable mouse hover highlighting for performance.
// * List item virtualization and moving away from the layout system. This
//   drastically reduces the number of allocations and processing per `OnGUI()`
//   call.
// * Menu path for showing window is now under `Trigger Segfault/`.
// * Changed window name to "Build Size" and added window icon.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace TriggerSegfault.editor
{
    // TODO: Check if new builds since last Read Build Logs (using Bundle Name
    // as unique ID).
    [EditorWindowTitle(title = "Build Size",
                       icon = "UnityEditor.ConsoleWindow")]
    public class BuildSizeViewer : EditorWindow
    {
        const string MenuPath = "Window/Trigger Segfault/VRC Build Size Viewer";
        const string PrefsNamespace     = "TriggerSegfault/BuildSizeViewer/";
        const string LanguagePref       = PrefsNamespace + "Language";
        const string ShowCategoriesPref = PrefsNamespace + "ShowCategories";
        const string EnableHoverPref    = PrefsNamespace + "EnableHover";
        const string MaxLogCountPref    = PrefsNamespace + "MaxLogCount";

        // Not readonly to allow for serialization.
        private List<BuildLog> m_buildLogs = new List<BuildLog>();
        private string[] m_buildLogPopupNames = Array.Empty<string>();
        private int m_buildLogIndex = -1;
        [NonSerialized]
        private int m_hoverIndex = -1;
        private Vector2 m_scrollPos = Vector2.zero;

        // Temporary fields for the lifetime of DrawBuildLog().
        [NonSerialized]
        private int m_hoverableIndex;
        [NonSerialized]
        private int m_newHoverIndex;
        [NonSerialized]
        private float m_columnPercent;
        [NonSerialized]
        private float m_columnSize;
        [NonSerialized]
        private Rect m_fileVisibleRect = Rect.zero;

        [NonSerialized]
        private float m_cachedFileMaxWidth = 0f;

        #region Preferences
        public const Languages DefaultLanguage = Languages.English;
        public enum Languages
        {
            English,
            // No other languages yet... you can help by expanding it.
        }

        private static TranslationLanguage s_translation;
        private static TranslationLanguage Translation
        {
            get
            {
                if (s_translation == null &&
                    !Translations.TryGetValue(Language, out s_translation))
                {
                    s_translation = Translations[Languages.English];
                }
                return s_translation;
            }
        }

        private static Languages? s_language;
        public static Languages Language
        {
            // Store enum pref as a string, since that's less-likely to change.
            get
            {
                if (!s_language.HasValue)
                {
                    string value = EditorPrefs.GetString(
                        LanguagePref, DefaultLanguage.ToString().ToLowerInvariant()
                    );
                    if (!Enum.TryParse(value, ignoreCase: true, out Languages lang))
                    {
                        lang = DefaultLanguage;
                    }
                    s_language = lang;
                    // Invalidate to reload translation on next access.
                    s_translation = null;
                }
                return s_language.Value;
            }
            set
            {
                if (Language != value)
                {
                    s_language = value;
                    EditorPrefs.SetString(LanguagePref, value.ToString().ToLowerInvariant());
                }
            }
        }

        private static bool? s_showCategories;
        public static bool ShowCategories
        {
            get
            {
                s_showCategories ??= EditorPrefs.GetBool(ShowCategoriesPref, true);
                return s_showCategories.Value;
            }
            set
            {
                if (ShowCategories != value)
                {
                    s_showCategories = value;
                    EditorPrefs.SetBool(ShowCategoriesPref, value);
                }
            }
        }

        private static bool? s_enableHover;
        public static bool EnableHover
        {
            get
            {
                s_enableHover ??= EditorPrefs.GetBool(EnableHoverPref, true);
                return s_enableHover.Value;
            }
            set
            {
                if (EnableHover != value)
                {
                    s_enableHover = value;
                    EditorPrefs.SetBool(EnableHoverPref, value);
                }
            }
        }

        private static int? s_maxLogCount;
        public static int MaxLogCount
        {
            get
            {
                s_maxLogCount ??= EditorPrefs.GetInt(MaxLogCountPref, 20);
                return Math.Max(1, s_maxLogCount.Value);
            }
            set
            {
                if (MaxLogCount != value)
                {
                    s_maxLogCount = value;
                    EditorPrefs.SetInt(MaxLogCountPref, value);
                }
            }
        }
        #endregion

        #region OnGUI and Events
        [MenuItem(MenuPath, false)]
        static void ShowWindow()
        {
            EditorWindow.GetWindow<BuildSizeViewer>();
        }

        void OnProjectChange()
        {
            // Uncache asset objects, since files may have changed.
            foreach (var buildLog in m_buildLogs)
            {
                foreach (var file in buildLog.Files)
                {
                    file.AssetObject = null;
                }
            }
        }

        void OnGUI()
        {
            InitializeStyles();

            EditorGUILayout.LabelField(Translation.Instructions, EditorStyles.label);
            if (GUILayout.Button(TempContent(Translation.ReadBuildLogs, Translation.ReadBuildLogsTooltip)))
            {
                ReadBuildLogs();
                m_buildLogIndex = (m_buildLogs.Count > 0 ? 0 : -1);
                m_hoverIndex = -1;
                m_scrollPos = Vector2.zero;
                // Repaint to reflect new build log selection.
                Repaint();
                GUIUtility.ExitGUI();
            }

            DrawPreferences();

            int newIndex = EditorGUILayout.Popup(
                TempContent(Translation.SelectBuildLog, Translation.SelectBuildLogTooltip),
                m_buildLogIndex,
                m_buildLogPopupNames
            );
            if (m_buildLogIndex != newIndex)
            {
                m_buildLogIndex = newIndex;
                m_hoverIndex = -1;
                // Preserve scroll position, it may be useful when switching back
                // and forth between logs.
                // Repaint to reflect new build log selection.
                Repaint();
                GUIUtility.ExitGUI();
            }
            else if (m_buildLogIndex >= 0 && m_buildLogIndex < m_buildLogs.Count)
            {
                this.wantsMouseMove = EnableHover;
                this.wantsMouseEnterLeaveWindow = EnableHover;
                DrawBuildLog(m_buildLogs[m_buildLogIndex]);
            }
            else
            {
                this.wantsMouseMove = false;
                this.wantsMouseEnterLeaveWindow = false;
                // Reset scroll position if nothing is being viewed.
                m_scrollPos = Vector2.zero;
            }
        }

        private void DrawPreferences()
        {
            // Don't waste space showing language dropdown if there's only one.
            if (Translations.Count > 1)
            {
                Languages newLanguage = (Languages)EditorGUILayout.EnumPopup("Language", Language);
                if (Language != newLanguage)
                {
                    Language = newLanguage;
                    // Repaint to reflect new language.
                    Repaint();
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.BeginHorizontal();
            try
            {
                bool newShowCategories = EditorGUILayout.Toggle(
                    TempContent(Translation.ShowCategories, Translation.ShowCategoriesTooltip),
                    ShowCategories
                );
                if (ShowCategories != newShowCategories)
                {
                    ShowCategories = newShowCategories;
                    // Repaint to reflect categories visibility.
                    Repaint();
                    GUIUtility.ExitGUI();
                }

                EnableHover = EditorGUILayout.Toggle(
                    TempContent(Translation.EnableHover, Translation.EnableHoverTooltip),
                    EnableHover
                );
            }
            finally
            {
                EditorGUILayout.EndHorizontal();
            }

            MaxLogCount = Math.Clamp(
                EditorGUILayout.IntField(
                    TempContent(Translation.MaxLogCount, Translation.MaxLogCountTooltip),
                    MaxLogCount
                ),
                1, 1000
            );
        }

        private void DrawBuildLog(BuildLog log)
        {
            // Reset indices for detecting control receiving mouse hover.
            m_newHoverIndex = -1;
            m_hoverableIndex = 0;
            // Cache column widths that are used for every file item.
            m_columnPercent = Styles.RightHeader.CalcSize(TempContent("100.0%")).x + 10f;
            m_columnSize = Styles.RightHeader.CalcSize(TempContent("1000.0 mb")).x + 10f;

            float columnTotalSize = EditorStyles.label.CalcSize(
                TempContent(Translation.TotalUncompressedSize)
            ).x;
            void DrawTotalSize(string text, FileSize size)
            {
                EditorGUILayout.BeginHorizontal();
                try
                {
                    EditorGUILayout.LabelField(
                        text, EditorStyles.label, GUILayout.Width(columnTotalSize)
                    );
                    EditorGUILayout.LabelField(
                        size.ToString(), Styles.RightLabel, GUILayout.Width(m_columnSize)
                    );
                }
                finally
                {
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Separator();

            DrawTotalSize(Translation.TotalCompressedSize, log.CompressedSize);
            DrawTotalSize(Translation.TotalUncompressedSize, log.UncompressedSize);

            if (ShowCategories)
            {
                EditorGUILayout.Separator();

                DrawCategoryList(log);
            }

            EditorGUILayout.Separator();

            DrawFileList(log);

            // Update index of control receiving mouse hover.
            switch (Event.current.type)
            {
            case EventType.MouseMove:
            case EventType.MouseLeaveWindow:
                if (EnableHover && m_hoverIndex != m_newHoverIndex)
                {
                    m_hoverIndex = m_newHoverIndex;
                    // Repaint to reflect new control with mouse hover.
                    Repaint();
                }
                break;
            }
        }

        private void DrawCategoryList(BuildLog log)
        {
            DrawColumnHeaders(log.Categories, isFile: false);

            Rect areaRect = GUILayoutUtility.GetRect(
                this.position.width, log.Categories.Count * Styles.ItemHeight
            );

            // Category rows have no interaction, and so only need to be
            // processed during repaint.
            if (Event.current.type == EventType.Repaint)
            {
                DrawStyle(Styles.AreaBg, areaRect, GUIContent.none);

                Rect itemRect = areaRect;
                itemRect.height = Styles.ItemHeight;
                foreach (var category in log.Categories)
                {
                    DrawRow(category, itemRect, isFile: false);
                    itemRect.y += Styles.ItemHeight;
                }
            }
        }

        private void DrawFileList(BuildLog log)
        {
            Event e = Event.current;

            DrawColumnHeaders(log.Files, isFile: true);

            // Get the current Y position. This tells us how much remaining
            // space is available to be consumed by the scroll view.
            float yStart = GUILayoutUtility.GetLastRect().yMax;

            Vector2 origIconSize = EditorGUIUtility.GetIconSize();
            m_scrollPos = EditorGUILayout.BeginScrollView(m_scrollPos, Styles.AreaBg);
            try
            {
                if (e.type == EventType.Layout || e.type == EventType.Repaint)
                {
                    // We're either laying out, and need the icon dimensions
                    // for sizing, or need the icon dimensions for repainting.
                    EditorGUIUtility.SetIconSize(Styles.IconSize);
                }

                // Measure max width so that we can show horizontal scrollbar.
                // Layout is always the first event, so no need to check if the
                // cached width is assigned.
                if (e.type == EventType.Layout)
                {
                    float maxWidth = 0f;
                    foreach (var file in log.Files)
                    {
                        float width = Styles.LeftHover.CalcSize(TempContent(
                            file.FullName,
                            // Use dummy texture, since all we need is the area
                            // consumed by the fixed-size icon.
                            EditorGUIUtility.whiteTexture
                        )).x;
                        maxWidth = Math.Max(maxWidth, width);
                    }
                    m_cachedFileMaxWidth = maxWidth + m_columnPercent + m_columnSize
                                         + Styles.ColumnNameSpacing + Styles.TextPadding;
                }

                // Reserve the scrollable area dimensions, now that we know how
                // much horizontal space is needed.
                Rect areaRect = GUILayoutUtility.GetRect(
                    m_cachedFileMaxWidth, log.Files.Count * Styles.ItemHeight
                );

                if (e.type == EventType.Layout)
                {
                    // Layouting is done.
                    return;
                }

                // Get the rect used for collision checks and positioning.
                m_fileVisibleRect = GetVisibleScrollArea(
                    m_scrollPos, this.position, yStart, areaRect
                );

                // Only draw visible list items, based on scroll position.
                int visibleStart = Math.Max(
                    0, Mathf.FloorToInt(m_scrollPos.y / Styles.ItemHeight)
                );
                int visibleEnd = Math.Min(
                    log.Files.Count,
                    Mathf.CeilToInt(
                        (m_scrollPos.y + m_fileVisibleRect.height) / Styles.ItemHeight
                    )
                );

                Rect itemRect = areaRect;
                itemRect.height = Styles.ItemHeight;
                for (int i = visibleStart; i < visibleEnd; i++)
                {
                    itemRect.y = i * Styles.ItemHeight;
                    DrawRow(log.Files[i], itemRect, isFile: true);
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
                EditorGUIUtility.SetIconSize(origIconSize);
            }
        }

        private static Rect GetVisibleScrollArea(
            Vector2 scrollPos, Rect position, float yStart, Rect area
        )
        {
            Rect rect = new Rect(
                scrollPos.x, scrollPos.y,
                position.width, Math.Max(0f, position.height - yStart)
            );

            // Cut-off scrollbar sizes if present.
            if (area.height > rect.height)
            {
                rect.width -= GUI.skin.verticalScrollbar.fixedWidth;
                // Vertical is needed, check if horizontal is now needed.
                if (area.width > rect.width)
                {
                    rect.height -= GUI.skin.horizontalScrollbar.fixedHeight;
                }
            }
            else if (area.width > rect.width)
            {
                rect.height -= GUI.skin.horizontalScrollbar.fixedHeight;
                // Horizontal is needed, check if vertical is now needed.
                if (area.height > rect.height)
                {
                    rect.width -= GUI.skin.verticalScrollbar.fixedWidth;
                }
            }
            rect.size = Vector2.Max(Vector2.zero, rect.size);

            return rect;
        }

        private void DrawStyle(GUIStyle style, Rect rect, string text, bool hoverable = false)
        {
            DrawStyle(style, rect, TempContent(text), hoverable);
        }

        private void DrawStyle(GUIStyle style, Rect rect, GUIContent content, bool hoverable = false)
        {
            if (Event.current.type == EventType.Repaint)
            {
                style.Draw(
                    rect,
                    content,
                    isHover: (EnableHover && hoverable &&
                              m_hoverIndex == m_hoverableIndex),
                    isActive: false,
                    on: false,
                    hasKeyboardFocus: false
                );
            }
        }

        private void DrawColumnHeaders(List<BuildItem> list, bool isFile)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, Styles.HeaderHeight, Styles.HeaderBg);

            string nameText = isFile
                ? Translation.FilePathColumn : Translation.CategoryColumn;
            string extraText = isFile
                ? Translation.ExtensionColumn : Translation.OriginalOrderColumn;
            float columnName = Styles.LeftHeader.CalcSize(TempContent(nameText)).x
                             + Styles.TextPadding;
            float columnExtra = Styles.RightHeader.CalcSize(TempContent(extraText)).x;

            Rect percentRect = rect;
            Rect sizeRect = rect;
            Rect nameRect = rect;
            Rect extraRect = rect;
            percentRect.width = m_columnPercent;
            sizeRect.xMin = percentRect.xMax;
            sizeRect.width = m_columnSize;
            nameRect.xMin = sizeRect.xMax + Styles.ColumnNameSpacing;
            nameRect.width = columnName;
            extraRect.xMin = nameRect.xMax;
            extraRect.width = Math.Max(columnExtra, extraRect.width - Styles.TextPadding);

            DrawStyle(Styles.HeaderBg, rect, GUIContent.none);

            DrawStyle(Styles.RightHeader, percentRect, Translation.PercentColumn, true);
            HandleColumnHeader(list, percentRect, CompareSize);
            DrawStyle(Styles.RightHeader, sizeRect, Translation.SizeColumn, true);
            HandleColumnHeader(list, sizeRect, CompareSize);
            DrawStyle(Styles.LeftHeader, nameRect, nameText, true);
            HandleColumnHeader(list, nameRect, CompareName);
            DrawStyle(Styles.RightHeader, extraRect, extraText, true);
            HandleColumnHeader(
                list, extraRect, isFile ? CompareExtension : CompareIndex
            );
        }

        private void DrawRow(BuildItem item, Rect rect, bool isFile)
        {
            Rect percentRect = rect;
            Rect sizeRect = rect;
            Rect nameRect = rect;
            percentRect.width = m_columnPercent;
            sizeRect.xMin = percentRect.xMax;
            sizeRect.width = m_columnSize;
            nameRect.xMin = sizeRect.xMax + Styles.ColumnNameSpacing;

            DrawStyle(Styles.RightLabel, percentRect, item.PercentString);
            DrawStyle(Styles.RightLabel, sizeRect, item.Size.ToString());
            if (!isFile)
            {
                DrawStyle(EditorStyles.label, nameRect, item.FullName);
            }
            else
            {
                // Don't waste time looking up the icon when not repainting.
                var icon = (Event.current.type == EventType.Repaint)
                    ? item.Icon : Texture2D.whiteTexture;
                GUIContent nameContent = TempContent(item.FullName, icon, item.FullName);
                DrawStyle(Styles.LeftHover, nameRect, nameContent, true);
                HandleFileRow(item, nameRect);
            }
        }

        private void HandleColumnHeader(List<BuildItem> list, Rect rect, Comparison<BuildItem> comparison)
        {
            Event e = Event.current;
            switch (e.type)
            {
            case EventType.MouseMove:
            //case EventType.MouseEnterWindow:
                if (EnableHover && rect.Contains(e.mousePosition))
                {
                    m_newHoverIndex = m_hoverableIndex;
                }
                break;
            case EventType.MouseDown:
                if (e.button == 0 && rect.Contains(e.mousePosition))
                {
                    list.Sort(comparison);
                    // Repaint to reflect new list order.
                    Repaint();
                    e.Use();
                    GUIUtility.ExitGUI();
                }
                break;
            }
            m_hoverableIndex++;
        }

        private void HandleFileRow(BuildItem item, Rect rect)
        {
            Event e = Event.current;
            switch (e.type)
            {
            case EventType.MouseMove:
            //case EventType.MouseEnterWindow:
                if (EnableHover && rect.Contains(e.mousePosition) &&
                    m_fileVisibleRect.Contains(e.mousePosition))
                {
                    m_newHoverIndex = m_hoverableIndex;
                }
                break;
            case EventType.MouseDown:
                if (e.button == 0 && rect.Contains(e.mousePosition))
                {
                    if (item.FullName != "Resources/unity_builtin_extra")
                    {
                        var assetObject = item.AssetObject;
                        if (assetObject != null)
                        {
                            Selection.activeObject = assetObject;
                            EditorGUIUtility.PingObject(assetObject);
                        }
                    }
                    e.Use();
                    GUIUtility.ExitGUI();
                }
                break;
            }
            m_hoverableIndex++;
        }
        #endregion

        #region GUI Helpers
        private static class Styles
        {
            public static readonly Vector2 IconSize = new Vector2(16f, 16f);
            public const float ItemHeight = 18f;
            public const float HeaderHeight = 21f;
            public const float ColumnNameSpacing = 24f;
            public const float TextPadding = 6f;

            public static Texture2D TransparentIcon;

            public static GUIStyle RightLabel;
            public static GUIStyle LeftHeader;
            public static GUIStyle RightHeader;
            public static GUIStyle LeftHover;
            public static GUIStyle HeaderBg;
            public static GUIStyle AreaBg;
        }

        private static bool s_stylesInitialized = false;
        private static void InitializeStyles()
        {
            if (s_stylesInitialized)
            {
                return;
            }
            s_stylesInitialized = true;

            {
                Texture2D texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
                texture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
                texture.Apply();
                Styles.TransparentIcon = texture;
            }
            {
                GUIStyle style = new GUIStyle(EditorStyles.label);
                style.alignment = TextAnchor.MiddleRight;
                Styles.RightLabel = style;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("OL ResultLabel"));
                style.fontStyle = FontStyle.Bold;
                style.fixedHeight = Styles.HeaderHeight;
                style.margin = new RectOffset();
                style.padding = new RectOffset();
                // Highlight column header on hover.
                style.hover.textColor = EditorStyles.whiteLabel.normal.textColor;
                style.alignment = TextAnchor.MiddleLeft;
                Styles.LeftHeader = style;
            }
            {
                GUIStyle style = new GUIStyle(Styles.LeftHeader);
                style.alignment = TextAnchor.MiddleRight;
                Styles.RightHeader = style;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("OL ResultLabel"));
                style.margin = new RectOffset();
                style.padding = new RectOffset();
                // Highlight column on hover.
                style.hover.textColor = EditorStyles.whiteLabel.normal.textColor;
                Styles.LeftHover = style;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("ProjectBrowserTopBarBg"));
                style.fixedHeight = Styles.HeaderHeight;
                style.margin = new RectOffset();
                style.padding = new RectOffset();
                Styles.HeaderBg = style;
            }
            {
                GUIStyle style = new GUIStyle(new GUIStyle("ProjectBrowserIconAreaBg"));
                // The style is being used as-is, but it's good practice to
                // copy it anyway, in-case future changes are mistakenly
                // made without changing it back to a copy.
                Styles.AreaBg = style;
            }
        }

        private readonly static GUIContent s_tempContent = new GUIContent();
        private static GUIContent TempContent(string text, string tooltip)
        {
            return TempContent(text, null, tooltip);
        }
        private static GUIContent TempContent(string text, Texture2D image = null, string tooltip = null)
        {
            s_tempContent.text = text;
            s_tempContent.tooltip = tooltip;
            s_tempContent.image = image;
            return s_tempContent;
        }
        #endregion

        #region BuildLog Reading
        private static string GetBuildLogPath(bool previous)
        {
            string name = (previous ? "Editor-prev.log" : "Editor.log");
            switch (Application.platform)
            {
            case RuntimePlatform.WindowsEditor:
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Unity/Editor/" + name
                );
            case RuntimePlatform.OSXEditor:
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Library/Logs/Unity/" + name
                );
            case RuntimePlatform.LinuxEditor:
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    ".config/unity3d/" + name
                );
            default:
                Debug.LogWarning($"Unsupported OS: {Application.platform}");
                return string.Empty;
            }
        }

        private static string TemporaryBuildLogPath => GetBuildLogPath(false) + "copy";

        private void ReadBuildLogs()
        {
            m_buildLogs.Clear();
            m_buildLogPopupNames = Array.Empty<string>();
            var prevBuildLogPath = GetBuildLogPath(previous: true);
            var currentBuildLogPath = GetBuildLogPath(previous: false);
            var tempBuildLogPath = TemporaryBuildLogPath;
            try
            {
                if (File.Exists(currentBuildLogPath))
                {
                    FileUtil.ReplaceFile(currentBuildLogPath, tempBuildLogPath);

                    using var reader = File.OpenText(tempBuildLogPath);
                    m_buildLogs.AddRange(BuildLog.ReadAll(tempBuildLogPath));
                }
                // OPTIMIZE: Avoid reading previous editor log if we already
                // reached the max log count. This is helpful if the previous
                // log is noticeably large, since we have to read least-to-most
                // recent.
                if (m_buildLogs.Count < MaxLogCount && File.Exists(prevBuildLogPath))
                {
                    FileUtil.ReplaceFile(prevBuildLogPath, tempBuildLogPath);

                    using var reader = File.OpenText(tempBuildLogPath);
                    m_buildLogs.AddRange(BuildLog.ReadAll(tempBuildLogPath));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Error reading build logs:\n{ex}");
            }
            finally
            {
                try
                {
                    FileUtil.DeleteFileOrDirectory(tempBuildLogPath);
                }
                catch {}
                if (m_buildLogs.Count > MaxLogCount)
                {
                    m_buildLogs.RemoveRange(MaxLogCount, m_buildLogs.Count - MaxLogCount);
                }
                m_buildLogPopupNames = m_buildLogs.Select((b, i) => $"[{i}] {b.Name}")
                                                  .ToArray();
            }
        }
        #endregion

        #region BuildLog Sorting
        private static int CompareIndex(BuildItem x, BuildItem y)
        {
            return x.Index.CompareTo(y.Index);
        }

        private static int CompareSize(BuildItem x, BuildItem y)
        {
            int cmp;
            // It's possible for Percent to have more precision than Size if low kilobytes.
            // Compare this first, since it's cheaper to calculate.
            if (0 != (cmp = x.Percent.CompareTo(y.Percent)) ||
                0 != (cmp = x.Size.SizeInBytes.CompareTo(y.Size.SizeInBytes)))
            {
                // Negate for descending size.
                return -cmp;
            }
            // Size is identical (or we don't have enough precision to tell).
            return CompareIndex(x, y);
        }

        private static int CompareName(BuildItem x, BuildItem y)
        {
            return string.Compare(
                x.FullName,
                y.FullName,
                StringComparison.InvariantCultureIgnoreCase
            );
        }

        private static int CompareExtension(BuildItem x, BuildItem y)
        {
            int cmp = string.Compare(
                Path.GetExtension(x.FullName),
                Path.GetExtension(y.FullName),
                StringComparison.InvariantCultureIgnoreCase
            );
            if (cmp != 0)
            {
                return cmp;
            }
            return CompareName(x, y);
        }
        #endregion

        #region BuildLog Classes
        [Serializable]
        private struct FileSize
        {
            public float Size;
            public string Units;

            public long SizeInBytes
            {
                get
                {
                    switch (Units)
                    {
                    case "b":  // Not sure if bytes are ever used as units.
                    case "byte":
                    case "bytes":
                    default:   return (long)Size;
                    case "kb": return (long)((double)Size * (1024));
                    case "mb": return (long)((double)Size * (1024 * 1024));
                    case "gb": return (long)((double)Size * (1024 * 1024 * 1024));
                    }
                }
            }

            [NonSerialized]
            private string m_cachedString;
            public override string ToString() => (m_cachedString ??= $"{Size:0.0} {Units}");

            public static FileSize Parse(Match m)
            {
                return new FileSize
                {
                    Size = float.Parse(m.Groups["size"].Value),
                    Units = m.Groups["units"].Value,
                };
            }
        }

        [Serializable]
        private class BuildItem
        {
            public int Index; // Only exists for sorting
            public string FullName; // File path or category name
            public FileSize Size;
            public float Percent;

            [NonSerialized]
            private UnityEngine.Object m_cachedAssetObject;
            [NonSerialized]
            private bool m_isAssetObjectCached = false;
            public UnityEngine.Object AssetObject
            {
                get
                {
                    if (!m_isAssetObjectCached)
                    {
                        m_isAssetObjectCached = true;
                        m_cachedAssetObject = AssetDatabase.LoadMainAssetAtPath(FullName);
                    }
                    return m_cachedAssetObject;
                }
                set
                {
                    m_isAssetObjectCached = (value != null);
                    m_cachedAssetObject = value;
                }
            }

            [NonSerialized]
            private string m_cachedPercentString;
            public string PercentString => (m_cachedPercentString ??= $"{Percent:0.0}%");

            public Texture2D Icon
            {
                get
                {
                    var icon = AssetDatabase.GetCachedIcon(FullName) as Texture2D;
                    if (icon != null)
                    {
                        return icon;
                    }
                    // Icon may not be cached yet.
                    icon = AssetPreview.GetMiniThumbnail(AssetObject);
                    if (icon != null)
                    {
                        return icon;
                    }
                    // File may not exist anymore, or icon can't be obtained.
                    return Styles.TransparentIcon;
                }
            }

            public static BuildItem Parse(string line, Regex regex)
            {
                Match m = regex.Match(line);
                if (m != null)
                {
                    string percent = m.Groups["percent"].Value;
                    if (string.IsNullOrEmpty(percent))
                    {
                        percent = "0.0";
                    }
                    string fullName = m.Groups["name"].Value;
                    return new BuildItem
                    {
                        FullName = fullName,
                        Percent = float.Parse(percent),
                        Size = FileSize.Parse(m),
                    };
                }
                else
                {
                    return null;
                }
            }
        }

        [Serializable]
        private class BuildLog
        {
            public string Name;
            public FileSize CompressedSize;
            public FileSize UncompressedSize;
            // Not readonly to allow for serialization.
            public List<BuildItem> Categories = new List<BuildItem>();
            public List<BuildItem> Files = new List<BuildItem>();

            public static List<BuildLog> ReadAll(string filePath)
            {
                using var reader = File.OpenText(filePath);
                List<BuildLog> logs = new List<BuildLog>();
                string line;
                while (null != (line = reader.ReadLine()))
                {
                    if (IsLogBegin(line))
                    {
                        BuildLog log = new BuildLog();
                        if (log.Read(reader, ref line))
                        {
                            logs.Add(log);
                        }
                    }
                }
                // Order by most-recent first.
                logs.Reverse();
                return logs;
            }

            private bool Read(StreamReader reader, ref string line)
            {
                bool hasBundleName = false;
                bool hasCompressedSize = false;
                bool hasCategories = false;
                bool hasFiles = false;
                do
                {
                    if (!hasBundleName)
                    {
                        if (line.StartsWith(BundleNamePrefix))
                        {
                            Match m = BundleNameRegex.Match(line);
                            if (m != null)
                            {
                                Name = m.Groups["name"].Value;
                                hasBundleName = true;
                            }
                        }
                    }
                    else if (!hasCompressedSize)
                    {
                        if (line.StartsWith(CompressedSizePrefix))
                        {
                            Match m = CompressedSizeRegex.Match(line);
                            if (m != null)
                            {
                                CompressedSize = FileSize.Parse(m);
                                hasCompressedSize = true;
                            }
                        }
                    }
                    else if (!hasCategories || !hasFiles)
                    {
                        if (!hasCategories && line == CategoriesBeginLine)
                        {
                            ReadCategories(reader, ref line);
                            hasCategories = true;
                        }
                        if (hasCategories && !hasFiles && line == FilesBeginLine)
                        {
                            ReadFiles(reader, ref line);
                            hasFiles = true;
                        }
                    }
                    else
                    {
                        break;
                    }
                    // Avoid runnaway situations where our conditions for
                    // identifying a log file produced false positives. Build
                    // logs ALWAYS start and end with a terminator line, so
                    // this is a safe solution.
                    if (line == TerminatorLine)
                    {
                        break;
                    }
                }
                while (null != (line = reader.ReadLine()));

                return (hasBundleName && hasCompressedSize &&
                        hasCategories && hasFiles);
            }

            private void ReadCategories(StreamReader reader, ref string line)
            {
                while (null != (line = reader.ReadLine()))
                {
                    if (line == FilesBeginLine)
                    {
                        break;
                    }
                    BuildItem category = BuildItem.Parse(line, CategoryRegex);
                    if (category == null)
                    {
                        Debug.LogWarning($"Unexpected break in build log for category:\n\"{line}\"");
                        break;
                    }
                    else if (category.FullName == CompleteBuildSizeName)
                    {
                        UncompressedSize = category.Size;
                        //break;
                    }
                    else
                    {
                        category.Index = Categories.Count;
                        Categories.Add(category);
                    }
                }
            }

            private void ReadFiles(StreamReader reader, ref string line)
            {
                while (null != (line = reader.ReadLine()))
                {
                    if (line == TerminatorLine)
                    {
                        break;
                    }
                    BuildItem file = BuildItem.Parse(line, FileRegex);
                    if (file == null)
                    {
                        Debug.LogWarning($"Unexpected break in build log for file:\n\"{line}\"");
                        break;
                    }
                    else
                    {
                        file.Index = Files.Count;
                        Files.Add(file);
                    }
                }
            }

            #region BuildLog Matching
            const string BundleNamePrefix      = "Bundle Name:";
            const string CompressedSizePrefix  = "Compressed Size:";
            const string CompleteBuildSizeName = "Complete build size";
            const string CategoriesBeginLine   = "Uncompressed usage by category (Percentages based on user generated assets only):";
            const string FilesBeginLine        = "Used Assets and files from the Resources folder, sorted by uncompressed size:";
            const string TerminatorLine        = "-------------------------------------------------------------------------------";

            const string NamePattern    = @"(?'name'.+?)";
            const string SizePattern    = @"(?'size'\d+(?:\.\d+)?)\s+(?'units'[A-Za-z]{1,2})";
            const string PercentPattern = @"(?'percent'\d+\.\d+)%";

            const RegexOptions RegexFlags = RegexOptions.Compiled
                                          | RegexOptions.Singleline
                                          | RegexOptions.CultureInvariant;

            private static readonly Regex BundleNameRegex = new Regex(
                @$"^{Regex.Escape(BundleNamePrefix)}\s*{NamePattern}\s*$",
                RegexFlags
            );
            private static readonly Regex CompressedSizeRegex = new Regex(
                @$"^{Regex.Escape(CompressedSizePrefix)}\s*{SizePattern}\s*$",
                RegexFlags
            );
            private static readonly Regex CategoryRegex = new Regex(
                @$"^\s*{NamePattern}\s+{SizePattern}(?:\s+{PercentPattern})?\s*$",
                RegexFlags
            );
            private static readonly Regex FileRegex = new Regex(
                @$"^\s*{SizePattern}\s+{PercentPattern}\s+{NamePattern}\s*$",
                RegexFlags
            );

            private static bool IsAvatarHeader(string line)
                => line.Contains("avtr") && line.Contains(".prefab.unity3d");

            private static bool IsWorldHeader(string line)
                => line.Contains("scene-") && line.Contains(".vrcw");

            private static bool IsLogBegin(string line)
            {
                return line.StartsWith(BundleNamePrefix) &&
                       (IsAvatarHeader(line) || IsWorldHeader(line));
            }
            #endregion
        }
        #endregion

        #region Translations
        private class TranslationLanguage
        {
            public string Instructions   = "Create a build of your world/avatar and click the button!";

            public string ReadBuildLogs  = "Read Build Logs";
            public string SelectBuildLog = "Select Build Log";
            public string ShowCategories = "Show Categories";
            public string EnableHover    = "Enable Hover";
            public string MaxLogCount    = "Max Build Logs";

            public string ReadBuildLogsTooltip  = null;
            public string SelectBuildLogTooltip = null;
            public string ShowCategoriesTooltip = null;
            public string EnableHoverTooltip    = "Disable to improve performance. Hoverable elements may still be clicked.";
            public string MaxLogCountTooltip    = "Only store up to this many logs after reading.";

            public string TotalCompressedSize   = "Total Compressed Size:";
            public string TotalUncompressedSize = "Total Uncompressed Size:";

            public string SizeColumn          = "Size";
            public string PercentColumn       = "Size%";
            public string CategoryColumn      = "Category";
            public string OriginalOrderColumn = "Original Order";
            public string FilePathColumn      = "File Path";
            public string ExtensionColumn     = "Extension";
        }

        private static Dictionary<Languages, TranslationLanguage> Translations { get; } =
            new Dictionary<Languages, TranslationLanguage>
        {
            {Languages.English, new TranslationLanguage()},
        };
        #endregion
    }
}

#endif
