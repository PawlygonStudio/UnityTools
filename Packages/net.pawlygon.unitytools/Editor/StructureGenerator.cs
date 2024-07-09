using UnityEngine;
using UnityEditor;
using System.IO;

public class StructureGenerator : EditorWindow
{
    private Object selectedFBX;
    private Object selectedScene;
    private string baseFolderName = "!Pawlygon";
    private string subFolderName = "Avatar Name";

    [MenuItem("!Pawlygon/Structure Generator")]
    public static void ShowWindow()
    {
        GetWindow<StructureGenerator>("Structure Generator");
    }

    private void OnGUI()
    {
        GUILayout.Label("Select FBX and Scene to copy", EditorStyles.boldLabel);

        selectedFBX = EditorGUILayout.ObjectField("FBX Object", selectedFBX, typeof(Object), false);
        selectedScene = EditorGUILayout.ObjectField("Scene Object", selectedScene, typeof(Object), false);
        subFolderName = EditorGUILayout.TextField("Subfolder Name", subFolderName);

        if (GUILayout.Button("Copy Files"))
        {
            if (selectedFBX == null || selectedScene == null)
            {
                EditorUtility.DisplayDialog("Error", "Please select both an FBX object and a Scene object.", "OK");
            }
            else
            {
                CopyFiles();
            }
        }
    }

    private void CopyFiles()
    {
        string baseFolderPath = Path.Combine("Assets", baseFolderName);
        string subFolderPath = Path.Combine(baseFolderPath, subFolderName);
        string fbxFolderPath = Path.Combine(subFolderPath, "FBX");
        string sceneFolderPath = Path.Combine(subFolderPath, "Scene");
        string vrChatFolderPath = Path.Combine(subFolderPath, "VRChat");
        string prefabsFolderPath = Path.Combine(subFolderPath, "Prefabs");

        if (!AssetDatabase.IsValidFolder(baseFolderPath))
        {
            AssetDatabase.CreateFolder("Assets", baseFolderName);
        }

        if (!AssetDatabase.IsValidFolder(subFolderPath))
        {
            AssetDatabase.CreateFolder(baseFolderPath, subFolderName);
        }

        if (!AssetDatabase.IsValidFolder(fbxFolderPath))
        {
            AssetDatabase.CreateFolder(subFolderPath, "FBX");
        }

        if (!AssetDatabase.IsValidFolder(sceneFolderPath))
        {
            AssetDatabase.CreateFolder(subFolderPath, "Scene");
        }
        if (!AssetDatabase.IsValidFolder(vrChatFolderPath))
        {
            AssetDatabase.CreateFolder(subFolderPath, "VRChat");
        }
        if (!AssetDatabase.IsValidFolder(prefabsFolderPath))
        {
            AssetDatabase.CreateFolder(subFolderPath, "Prefabs");
        }

        string fbxPath = AssetDatabase.GetAssetPath(selectedFBX);
        string scenePath = AssetDatabase.GetAssetPath(selectedScene);

        string fbxFileName = Path.GetFileNameWithoutExtension(fbxPath) + " FT" + Path.GetExtension(fbxPath);
        string sceneFileName = subFolderName + " - Pawlygon VRCFT" + Path.GetExtension(scenePath);

        string fbxDestPath = Path.Combine(fbxFolderPath, fbxFileName);
        string sceneDestPath = Path.Combine(sceneFolderPath, sceneFileName);

        AssetDatabase.CopyAsset(fbxPath, fbxDestPath);
        AssetDatabase.CopyAsset(scenePath, sceneDestPath);

        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Success", "Files copied and renamed successfully!", "OK");
    }
}
