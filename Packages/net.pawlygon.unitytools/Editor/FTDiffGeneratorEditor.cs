using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    [CustomEditor(typeof(FTDiffGenerator))]
    public class FTDiffGeneratorEditor : UnityEditor.Editor
    {
        private SerializedProperty originalModelFbxProperty;
        private SerializedProperty modifiedModelFbxProperty;
        private SerializedProperty outputDirectoryProperty;

        private void OnEnable()
        {
            originalModelFbxProperty = serializedObject.FindProperty("originalModelFbx");
            modifiedModelFbxProperty = serializedObject.FindProperty("modifiedModelFbx");
            outputDirectoryProperty = serializedObject.FindProperty("outputDirectory");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            PawlygonEditorUI.EnsureStyles();

            // Header
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField(new GUIContent(" FaceTracking Diff Generator", EditorGUIUtility.IconContent("AvatarSelector").image), new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 });
            EditorGUILayout.Space(2);
            PawlygonEditorUI.DrawSeparator();
            EditorGUILayout.Space(5);

            EditorGUILayout.HelpBox(
                "Generate .hdiff patch files from the original and modified FBX models, then write them into the selected output folder.",
                MessageType.Info);

            EditorGUILayout.Space();

            using (new EditorGUILayout.VerticalScope(PawlygonEditorUI.SectionStyle))
            {
                EditorGUILayout.LabelField(new GUIContent(" FBX References", EditorGUIUtility.IconContent("Prefab Icon").image), EditorStyles.boldLabel);
                EditorGUILayout.Space(2);
                EditorGUILayout.PropertyField(originalModelFbxProperty);
                EditorGUILayout.PropertyField(modifiedModelFbxProperty);

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
                if (PawlygonEditorUI.DrawPrimaryButton("Generate Diff Files", 36f))
                {
                    foreach (Object targetObject in targets)
                    {
                        if (targetObject is FTDiffGenerator generator)
                        {
                            generator.GenerateDiffFiles();
                            GeneratePatchConfig(generator);
                        }
                    }
                }
            }
            EditorGUILayout.Space(5);
        }

        private string GetValidationMessage()
        {
            var generator = (FTDiffGenerator)target;

            if (generator.originalModelFbx == null)
            {
                return "Assign the original FBX model.";
            }

            if (generator.modifiedModelFbx == null)
            {
                return "Assign the modified FBX model.";
            }

            if (generator.outputDirectory == null)
            {
                return "Choose an output directory asset.";
            }

            if (!IsFbxAsset(generator.originalModelFbx))
            {
                return "The original model reference must point to an FBX asset.";
            }

            if (!IsFbxAsset(generator.modifiedModelFbx))
            {
                return "The modified model reference must point to an FBX asset.";
            }

            string outputPath = AssetDatabase.GetAssetPath(generator.outputDirectory);
            if (string.IsNullOrEmpty(outputPath) || !AssetDatabase.IsValidFolder(outputPath))
            {
                return "The output directory must be a valid folder asset.";
            }

            return string.Empty;
        }

        private static bool IsFbxAsset(GameObject model)
        {
            if (model == null) return false;
            string path = AssetDatabase.GetAssetPath(model);
            return string.Equals(System.IO.Path.GetExtension(path), ".fbx", System.StringComparison.OrdinalIgnoreCase);
        }

        private static void GeneratePatchConfig(FTDiffGenerator generator)
        {
            if (!FTPatchConfigGenerator.IsPatcherHubAvailable())
            {
                return;
            }

            string baseName = generator.GetBaseName();
            string patcherFolder = generator.GetPatcherFolderAssetPath();
            if (string.IsNullOrEmpty(baseName) || string.IsNullOrEmpty(patcherFolder))
            {
                return;
            }

            string diffFilesFolder = patcherFolder + "/data/DiffFiles";
            string fbxFolder = System.IO.Path.GetDirectoryName(
                AssetDatabase.GetAssetPath(generator.originalModelFbx))?.Replace('\\', '/');

            var context = new FTPatchConfigGenerator.ConfigContext
            {
                OriginalFbx = generator.originalModelFbx,
                FbxDiffAssetPath = diffFilesFolder + "/" + baseName + ".hdiff",
                MetaDiffAssetPath = diffFilesFolder + "/" + baseName + "Meta.hdiff",
                ConfigOutputFolder = patcherFolder,
                FbxOutputPath = fbxFolder
            };

            FTPatchConfigGenerator.GenerateConfig(context);
        }
    }
}
