using System;
using System.Collections.Generic;
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
        // Scene avatars
        // =====================================================================

        private const string LastAvatarSessionKey = "Pawlygon.UnityTools.LastAvatar";

        private static List<GameObject> cachedSceneAvatars;
        private static bool hierarchyHooked;

        /// <summary>
        /// Every avatar in the open scenes: objects with a VRCAvatarDescriptor (inactive ones included), or,
        /// without the VRChat SDK, root objects with a humanoid Animator. Cached until the hierarchy changes,
        /// so it is cheap to call from OnGUI.
        /// </summary>
        internal static List<GameObject> FindSceneAvatars()
        {
            if (!hierarchyHooked)
            {
                hierarchyHooked = true;
                EditorApplication.hierarchyChanged += () => cachedSceneAvatars = null;
                UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (scene, mode) => cachedSceneAvatars = null;
                UnityEditor.SceneManagement.EditorSceneManager.sceneClosed += scene => cachedSceneAvatars = null;
            }

            if (cachedSceneAvatars != null && cachedSceneAvatars.All(a => a != null)) return cachedSceneAvatars;

            var avatars = new List<GameObject>();
            Type descriptorType = FindVRCAvatarDescriptorType();

            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (descriptorType != null)
                    {
                        avatars.AddRange(root.GetComponentsInChildren(descriptorType, true).Select(c => c.gameObject));
                    }
                    else if (root.TryGetComponent(out Animator animator) && animator.avatar != null && animator.avatar.isHuman)
                    {
                        avatars.Add(root);
                    }
                }
            }

            cachedSceneAvatars = avatars.Distinct().ToList();
            return cachedSceneAvatars;
        }

        /// <summary>
        /// The avatar that contains <paramref name="anyObject"/>: the nearest object (itself or a parent) with a
        /// VRCAvatarDescriptor, or with a humanoid Animator when the SDK is not installed. Null if none.
        /// </summary>
        internal static GameObject FindAvatarRoot(GameObject anyObject)
        {
            if (anyObject == null) return null;
            Type descriptorType = FindVRCAvatarDescriptorType();

            for (Transform t = anyObject.transform; t != null; t = t.parent)
            {
                if (descriptorType != null)
                {
                    if (t.GetComponent(descriptorType) != null) return t.gameObject;
                }
                else if (t.TryGetComponent(out Animator animator) && animator.avatar != null && animator.avatar.isHuman)
                {
                    return t.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// The avatar the tools should open on: the one last picked in any Pawlygon tool this session if it is
        /// still in an open scene, otherwise the first avatar in the open scenes.
        /// </summary>
        internal static GameObject GetPreferredAvatar()
        {
            string remembered = SessionState.GetString(LastAvatarSessionKey, string.Empty);
            if (!string.IsNullOrEmpty(remembered) && GlobalObjectId.TryParse(remembered, out GlobalObjectId id)
                && GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is GameObject last && last != null)
            {
                return last;
            }

            return FindSceneAvatars().FirstOrDefault();
        }

        /// <summary>
        /// Remembers <paramref name="avatar"/> for <see cref="GetPreferredAvatar"/>, so every tool opens on it.
        /// </summary>
        internal static void RememberAvatar(GameObject avatar)
        {
            if (avatar == null || EditorUtility.IsPersistent(avatar)) return;
            SessionState.SetString(LastAvatarSessionKey, GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString());
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
        /// The blendshape names required for VRChat Unified Expression face tracking.
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
        /// are missing from the given mesh. Names are matched case-sensitively, because animation
        /// bindings are: a "jawopen" blendshape is not driven by a "JawOpen" animation. If <paramref name="mesh"/> is null every
        /// required blendshape is considered missing.
        /// </summary>
        internal static string[] GetMissingRequiredUnifiedBlendshapes(Mesh mesh)
        {
            if (mesh == null)
            {
                return RequiredUnifiedExpressionBlendshapes.ToArray();
            }

            var availableBlendshapes = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

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

            var availableBlendshapes = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

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
