using UnityEditor;
using UnityEngine;

namespace MeshLODGenerator
{
    /// <summary>
    /// Lightweight styling and layout helpers for the Mesh LOD Generator Editor GUI.
    /// Clean card-based design that adapts automatically to Personal (Light) and Pro (Dark) editor skins.
    /// </summary>
    public static class MeshLodUI
    {
        public static bool IsProSkin => EditorGUIUtility.isProSkin;

        public static Color AccentColor => IsProSkin
            ? new Color(0.35f, 0.65f, 1.0f)
            : new Color(0.12f, 0.45f, 0.85f);

        public static Color SuccessColor => IsProSkin
            ? new Color(0.35f, 0.85f, 0.45f)
            : new Color(0.15f, 0.60f, 0.25f);

        public static Color WarnColor => IsProSkin
            ? new Color(1.0f, 0.80f, 0.25f)
            : new Color(0.75f, 0.50f, 0.05f);

        public static Color CardBackground => IsProSkin
            ? new Color(0.18f, 0.18f, 0.20f, 0.7f)
            : new Color(0.92f, 0.92f, 0.94f, 0.8f);

        private static GUIStyle _headerStyle;
        private static GUIStyle _subHeaderStyle;
        private static GUIStyle _sectionTitleStyle;
        private static GUIStyle _cardStyle;
        private static GUIStyle _primaryButtonStyle;

        public static void Header(string title, string subtitle)
        {
            _headerStyle ??= new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 15,
                margin = new RectOffset(4, 4, 6, 2)
            };
            _subHeaderStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                margin = new RectOffset(4, 4, 0, 8)
            };

            EditorGUILayout.LabelField(title, _headerStyle);
            if (!string.IsNullOrEmpty(subtitle))
            {
                EditorGUILayout.LabelField(subtitle, _subHeaderStyle);
            }
            DrawSeparator();
        }

        public static void SectionTitle(string title)
        {
            _sectionTitleStyle ??= new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                margin = new RectOffset(2, 2, 8, 4)
            };
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, _sectionTitleStyle);
        }

        public static void BeginCard()
        {
            _cardStyle ??= new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 6)
            };
            EditorGUILayout.BeginVertical(_cardStyle);
        }

        public static void EndCard()
        {
            EditorGUILayout.EndVertical();
        }

        public static bool PrimaryButton(string text, float height = 28f)
        {
            _primaryButtonStyle ??= new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12
            };
            return GUILayout.Button(text, _primaryButtonStyle, GUILayout.Height(height));
        }

        public static void DrawSeparator()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1f);
            r.height = 1f;
            EditorGUI.DrawRect(r, IsProSkin ? new Color(0.28f, 0.28f, 0.30f) : new Color(0.75f, 0.75f, 0.78f));
            EditorGUILayout.Space(2);
        }

        public static void Note(string text)
        {
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
        }

        public static void Help(string message, MessageType type = MessageType.Info)
        {
            EditorGUILayout.HelpBox(message, type);
        }
    }
}
