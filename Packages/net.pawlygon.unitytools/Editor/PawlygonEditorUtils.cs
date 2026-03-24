using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Shared utility methods used across multiple Pawlygon editor tools.
    /// Centralises reflection look-ups, asset-path helpers, and face-tracking
    /// blendshape constants so they are defined in exactly one place.
    /// </summary>
    internal static class PawlygonEditorUtils
    {
        // =====================================================================
        // VRChat SDK reflection
        // =====================================================================

        private static Type cachedDescriptorType;
        private static bool descriptorTypeLookedUp;

        /// <summary>
        /// Finds the VRCAvatarDescriptor type via TypeCache, caching the result.
        /// Returns null if the VRChat Avatars SDK is not installed.
        /// </summary>
        internal static Type FindVRCAvatarDescriptorType()
        {
            if (descriptorTypeLookedUp) return cachedDescriptorType;

            descriptorTypeLookedUp = true;
            cachedDescriptorType = null;

            TypeCache.TypeCollection monoBehaviourTypes = TypeCache.GetTypesDerivedFrom<MonoBehaviour>();
            foreach (Type type in monoBehaviourTypes)
            {
                if (type.Name == "VRCAvatarDescriptor" && type.Namespace != null && type.Namespace.StartsWith("VRC"))
                {
                    cachedDescriptorType = type;
                    break;
                }
            }

            return cachedDescriptorType;
        }

        // =====================================================================
        // Asset-path helpers
        // =====================================================================

        /// <summary>
        /// Recursively ensures that all folders in the given asset path exist,
        /// creating them via <see cref="AssetDatabase.CreateFolder"/> as needed.
        /// </summary>
        internal static void EnsureFolderExists(string folderPath)
        {
            folderPath = NormalizeAssetPath(folderPath);
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath)) return;

            string parentPath = Path.GetDirectoryName(folderPath)?.Replace("\\", "/");
            string folderName = Path.GetFileName(folderPath);

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(folderName))
            {
                throw new InvalidOperationException($"Cannot create folder at '{folderPath}'.");
            }

            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                EnsureFolderExists(parentPath);
            }

            AssetDatabase.CreateFolder(parentPath, folderName);
        }

        /// <summary>
        /// Joins path segments with '/' separators, skipping empty/whitespace segments
        /// and trimming leading/trailing slashes from each segment.
        /// </summary>
        internal static string CombineAssetPath(params string[] parts)
        {
            return string.Join("/", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part.Trim('/')));
        }

        /// <summary>
        /// Normalises backslashes to forward slashes in an asset path.
        /// </summary>
        internal static string NormalizeAssetPath(string assetPath)
        {
            return assetPath.Replace("\\", "/");
        }

        // =====================================================================
        // Unified Expression blendshapes
        // =====================================================================

        /// <summary>
        /// The 60 blendshape names required for VRChat Unified Expression face tracking.
        /// </summary>
        internal static readonly string[] RequiredUnifiedExpressionBlendshapes =
        {
            "BrowDownLeft",
            "BrowDownRight",
            "BrowInnerUpLeft",
            "BrowInnerUpRight",
            "BrowOuterUpLeft",
            "BrowOuterUpRight",
            "EyeClosedLeft",
            "EyeClosedRight",
            "EyeConstrict",
            "EyeDilation",
            "EyeLookDownLeft",
            "EyeLookDownRight",
            "EyeLookInLeft",
            "EyeLookInRight",
            "EyeLookOutLeft",
            "EyeLookOutRight",
            "EyeLookUpLeft",
            "EyeLookUpRight",
            "EyeSquintLeft",
            "EyeSquintRight",
            "EyeWideLeft",
            "EyeWideRight",
            "CheekPuffLeft",
            "CheekPuffRight",
            "CheekSquintLeft",
            "CheekSquintRight",
            "CheekSuckLeft",
            "CheekSuckRight",
            "LipFunnel",
            "LipPucker",
            "LipSuckLower",
            "LipSuckUpper",
            "JawForward",
            "JawLeft",
            "JawOpen",
            "JawRight",
            "MouthClosed",
            "MouthFrownLeft",
            "MouthFrownRight",
            "MouthLeft",
            "MouthLowerDown",
            "MouthPress",
            "MouthRaiserLower",
            "MouthRaiserUpper",
            "MouthRight",
            "MouthSmileLeft",
            "MouthSmileRight",
            "MouthStretchLeft",
            "MouthStretchRight",
            "MouthTightenerLeft",
            "MouthTightenerRight",
            "MouthUpperUp",
            "MouthUpperUpLeft",
            "MouthUpperUpRight",
            "NoseSneer",
            "NoseSneerLeft",
            "NoseSneerRight",
            "TongueDown",
            "TongueLeft",
            "TongueOut",
            "TongueRight",
            "TongueUp"
        };

        /// <summary>
        /// Returns the subset of <see cref="RequiredUnifiedExpressionBlendshapes"/> that
        /// are missing from the given mesh. If <paramref name="mesh"/> is null every
        /// required blendshape is considered missing.
        /// </summary>
        internal static string[] GetMissingRequiredUnifiedBlendshapes(Mesh mesh)
        {
            if (mesh == null)
            {
                return RequiredUnifiedExpressionBlendshapes.ToArray();
            }

            var availableBlendshapes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string blendshapeName = mesh.GetBlendShapeName(i);
                if (!string.IsNullOrWhiteSpace(blendshapeName))
                {
                    availableBlendshapes.Add(blendshapeName);
                }
            }

            return RequiredUnifiedExpressionBlendshapes
                .Where(required => !availableBlendshapes.Contains(required))
                .ToArray();
        }

        /// <summary>
        /// Overload that accepts a custom set of required blendshape names.
        /// </summary>
        internal static string[] GetMissingRequiredUnifiedBlendshapes(Mesh mesh, string[] requiredBlendshapes)
        {
            if (mesh == null)
            {
                return requiredBlendshapes?.ToArray() ?? Array.Empty<string>();
            }

            var availableBlendshapes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string blendshapeName = mesh.GetBlendShapeName(i);
                if (!string.IsNullOrWhiteSpace(blendshapeName))
                {
                    availableBlendshapes.Add(blendshapeName);
                }
            }

            return (requiredBlendshapes ?? Array.Empty<string>())
                .Where(required => !availableBlendshapes.Contains(required))
                .ToArray();
        }
    }
}
