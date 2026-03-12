using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    [CustomEditor(typeof(FTDiffGenerator))]
    public class FTDiffGeneratorEditor : UnityEditor.Editor
    {
        private SerializedProperty originalModelPrefabProperty;
        private SerializedProperty modifiedModelPrefabProperty;
        private SerializedProperty outputDirectoryProperty;

        private void OnEnable()
        {
            originalModelPrefabProperty = serializedObject.FindProperty("originalModelPrefab");
            modifiedModelPrefabProperty = serializedObject.FindProperty("modifiedModelPrefab");
            outputDirectoryProperty = serializedObject.FindProperty("outputDirectory");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Premium Header
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField(new GUIContent(" FaceTracking Diff Generator", EditorGUIUtility.IconContent("AvatarSelector").image), new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
            EditorGUILayout.Space(2);
            Rect sep = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(sep, EditorGUIUtility.isProSkin ? new Color(0.2f, 0.2f, 0.2f) : new Color(0.7f, 0.7f, 0.7f));
            EditorGUILayout.Space(5);

            EditorGUILayout.HelpBox(
                "Generate .hdiff patch files from the original and modified FBX-backed prefabs, then write them into the selected output folder.",
                MessageType.Info);

            EditorGUILayout.Space();

            using (new EditorGUILayout.VerticalScope(new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(10, 10, 10, 10) }))
            {
                EditorGUILayout.LabelField(new GUIContent(" Prefab References", EditorGUIUtility.IconContent("Prefab Icon").image), EditorStyles.boldLabel);
                EditorGUILayout.Space(2);
                EditorGUILayout.PropertyField(originalModelPrefabProperty);
                EditorGUILayout.PropertyField(modifiedModelPrefabProperty);

                EditorGUILayout.Space(10);

                EditorGUILayout.LabelField(new GUIContent(" Output Settings", EditorGUIUtility.IconContent("Folder Icon").image), EditorStyles.boldLabel);
                EditorGUILayout.Space(2);
                EditorGUILayout.PropertyField(outputDirectoryProperty);
            }

            string validationMessage = GetValidationMessage();
            if (!string.IsNullOrEmpty(validationMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(validationMessage, MessageType.Warning);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(10);

            using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(validationMessage)))
            {
                // Premium Primary Button
                Color oldColor = GUI.backgroundColor;
                if (string.IsNullOrEmpty(validationMessage))
                    GUI.backgroundColor = EditorGUIUtility.isProSkin ? new Color(0.2f, 0.6f, 1f) : new Color(0.1f, 0.4f, 0.8f);
                
                if (GUILayout.Button("Generate Diff Files", new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.Height(36f)))
                {
                    foreach (Object targetObject in targets)
                    {
                        if (targetObject is FTDiffGenerator generator)
                        {
                            generator.GenerateDiffFiles();
                        }
                    }
                }
                GUI.backgroundColor = oldColor;
            }
            EditorGUILayout.Space(5);
        }

        private string GetValidationMessage()
        {
            var generator = (FTDiffGenerator)target;

            if (generator.originalModelPrefab == null)
            {
                return "Assign the original model prefab.";
            }

            if (generator.modifiedModelPrefab == null)
            {
                return "Assign the modified model prefab.";
            }

            if (generator.outputDirectory == null)
            {
                return "Choose an output directory asset.";
            }

            if (!IsPrefabAsset(generator.originalModelPrefab))
            {
                return "The original model reference must point to a prefab asset.";
            }

            if (!IsPrefabAsset(generator.modifiedModelPrefab))
            {
                return "The modified model reference must point to a prefab asset.";
            }

            if (!PrefabBacksToFbx(generator.originalModelPrefab))
            {
                return "The original model prefab must be linked to an FBX model.";
            }

            if (!PrefabBacksToFbx(generator.modifiedModelPrefab))
            {
                return "The modified model prefab must be linked to an FBX model.";
            }

            string outputPath = AssetDatabase.GetAssetPath(generator.outputDirectory);
            if (string.IsNullOrEmpty(outputPath) || !AssetDatabase.IsValidFolder(outputPath))
            {
                return "The output directory must be a valid folder asset.";
            }

            return string.Empty;
        }

        private static bool IsPrefabAsset(GameObject prefab)
        {
            return PrefabUtility.GetPrefabAssetType(prefab) != PrefabAssetType.NotAPrefab;
        }

        private static bool PrefabBacksToFbx(GameObject prefab)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(prefab);
            if (source == null)
            {
                return false;
            }

            string sourcePath = AssetDatabase.GetAssetPath(source);
            return string.Equals(System.IO.Path.GetExtension(sourcePath), ".fbx", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
