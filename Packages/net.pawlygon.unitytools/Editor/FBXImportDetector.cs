using System;
using System.IO;
using UnityEditor;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Raises <see cref="FbxReimported"/> for every FBX that Unity has finished importing.
    /// <c>OnPostprocessAllAssets</c> runs after the whole import batch has completed, so listeners
    /// can load the imported model straight away (unlike <c>OnPostprocessModel</c>, which runs
    /// while the model is still being imported).
    /// </summary>
    public class FBXImportDetector : AssetPostprocessor
    {
        /// <summary>Invoked with the asset path of each imported FBX.</summary>
        public static event Action<string> FbxReimported;

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (FbxReimported == null)
            {
                return;
            }

            foreach (string assetPath in importedAssets)
            {
                if (string.Equals(Path.GetExtension(assetPath), ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    FbxReimported?.Invoke(assetPath);
                }
            }
        }
    }
}
