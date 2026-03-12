using System;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    public class FBXImportDetector : AssetPostprocessor
    {
        public static event Action<string> FbxReimported;

        private void OnPostprocessModel(GameObject importedModel)
        {
            if (importedModel == null)
            {
                return;
            }

            FbxReimported?.Invoke(assetPath);
        }
    }
}
