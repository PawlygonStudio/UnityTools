using System;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Static utility class containing all non-UI logic for reading and writing
    /// humanoid eye muscle limits on a model's Avatar via the ModelImporter.
    /// Extracted from <see cref="EyeMuscleSettings"/> to enable headless/programmatic usage.
    /// </summary>
    internal static class EyeMuscleSettingsCore
    {
        // =====================================================================
        // Constants
        // =====================================================================

        private const string LogPrefix = "[EyeMuscleSettingsCore]";

        /// <summary>
        /// Muscle values set by Pawlygon's Face Tracking animation clips.
        /// The FT clips animate humanoid muscle values directly (not bone transforms).
        /// These constants represent the clip values for each direction and are used
        /// to calculate the preview rotation: <c>muscleLimit * ftAnimValue</c>.
        /// </summary>
        internal static class FaceTrackingPreset
        {
            internal const float In = -2f;
            internal const float Out = 2f;
            internal const float Up = 2.8f;
            internal const float Down = -3.5f;
        }

        // =====================================================================
        // Data model
        // =====================================================================

        /// <summary>
        /// Holds the eye muscle limit values for a single eye (In, Out, Up, Down).
        /// </summary>
        internal class EyeMuscleValues
        {
            public float In;
            public float Out;
            public float Up;
            public float Down;

            internal EyeMuscleValues() { }

            internal EyeMuscleValues(float inVal, float outVal, float upVal, float downVal)
            {
                In = inVal;
                Out = outVal;
                Up = upVal;
                Down = downVal;
            }

            internal EyeMuscleValues Clone()
            {
                return new EyeMuscleValues(In, Out, Up, Down);
            }

            internal bool Equals(EyeMuscleValues other)
            {
                if (other == null) return false;
                return Mathf.Approximately(In, other.In) &&
                       Mathf.Approximately(Out, other.Out) &&
                       Mathf.Approximately(Up, other.Up) &&
                       Mathf.Approximately(Down, other.Down);
            }
        }

        /// <summary>
        /// Result of analyzing an avatar for eye muscle settings.
        /// </summary>
        internal class AnalysisResult
        {
            public bool Success;
            public string StatusMessage;
            public MessageType StatusMessageType;

            /// <summary>The avatar's Animator component.</summary>
            public Animator Animator;

            /// <summary>The ModelImporter for the avatar's model asset.</summary>
            public ModelImporter Importer;

            /// <summary>The asset path of the model (FBX).</summary>
            public string ModelAssetPath;

            /// <summary>Current eye muscle values for the left eye.</summary>
            public EyeMuscleValues LeftEye;

            /// <summary>Current eye muscle values for the right eye.</summary>
            public EyeMuscleValues RightEye;

            /// <summary>
            /// Why the model's import settings cannot be changed (e.g. it lives in an immutable
            /// package), or null when they can.
            /// </summary>
            public string ReadOnlyReason;
        }

        /// <summary>
        /// The serialized limit of one eye bone as stored on the ModelImporter.
        /// </summary>
        internal class EyeBoneLimit
        {
            public bool Found;
            public bool Modified;
            public Vector3 Min;
            public Vector3 Max;
        }

        /// <summary>
        /// The eye bone limits of a model exactly as they were stored on its ModelImporter
        /// (including whether they used Unity's defaults), so they can be written back later.
        /// </summary>
        internal class EyeLimitSnapshot
        {
            public string ModelAssetPath;
            public EyeBoneLimit LeftEye;
            public EyeBoneLimit RightEye;
        }

        // =====================================================================
        // Avatar discovery
        // =====================================================================

        /// <summary>
        /// The avatar to open on: the one last picked in any Pawlygon tool this session, otherwise the first
        /// avatar in the open scenes. See <see cref="PawlygonEditorUtils.GetPreferredAvatar"/>.
        /// </summary>
        internal static GameObject FindFirstAvatarInScene()
        {
            return PawlygonEditorUtils.GetPreferredAvatar();
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        /// <summary>
        /// Analyzes a scene avatar GameObject to extract its eye muscle settings.
        /// Validates: non-null, has Animator, is humanoid, can find ModelImporter.
        /// </summary>
        internal static AnalysisResult Analyze(GameObject avatar)
        {
            if (avatar == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = "Select an avatar GameObject from the scene.",
                    StatusMessageType = MessageType.Warning
                };
            }

            Animator animator = avatar.GetComponent<Animator>();
            if (animator == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = $"No Animator component found on '{avatar.name}'.",
                    StatusMessageType = MessageType.Warning
                };
            }

            if (animator.avatar == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = $"The Animator on '{avatar.name}' has no Avatar assigned.",
                    StatusMessageType = MessageType.Warning
                };
            }

            if (!animator.avatar.isHuman)
            {
                return new AnalysisResult
                {
                    Success = false,
                    StatusMessage = "Eye muscle adjusting is only supported on humanoid rigs. " +
                                    "The current Avatar is not configured as Humanoid. " +
                                    "Change the Animation Type to Humanoid in the model's import settings.",
                    StatusMessageType = MessageType.Error
                };
            }

            // Find the source model asset to get the ModelImporter
            string assetPath = AssetDatabase.GetAssetPath(animator.avatar);
            if (string.IsNullOrEmpty(assetPath))
            {
                return new AnalysisResult
                {
                    Success = false,
                    Animator = animator,
                    StatusMessage = "Could not determine the asset path for the Avatar. It may be a built-in or runtime-created Avatar.",
                    StatusMessageType = MessageType.Error
                };
            }

            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                return new AnalysisResult
                {
                    Success = false,
                    Animator = animator,
                    StatusMessage = $"Could not find a ModelImporter at '{assetPath}'. The Avatar may not come from an FBX/model asset.",
                    StatusMessageType = MessageType.Error
                };
            }

            // Read current muscle values
            SerializedObject serializedImporter = new SerializedObject(importer);
            EyeMuscleValues leftEye = ReadEyeMuscleValues(serializedImporter, "LeftEye", true);
            EyeMuscleValues rightEye = ReadEyeMuscleValues(serializedImporter, "RightEye", false);

            string readOnlyReason = GetReadOnlyReason(assetPath);

            return new AnalysisResult
            {
                Success = true,
                Animator = animator,
                Importer = importer,
                ModelAssetPath = assetPath,
                LeftEye = leftEye,
                RightEye = rightEye,
                ReadOnlyReason = readOnlyReason,
                StatusMessage = $"Loaded eye muscle settings from '{System.IO.Path.GetFileName(assetPath)}'." +
                                (readOnlyReason != null ? " The model is read-only; values can be previewed but not applied." : string.Empty),
                StatusMessageType = readOnlyReason != null ? MessageType.Warning : MessageType.Info
            };
        }

        /// <summary>
        /// Returns why the import settings of the model at <paramref name="assetPath"/> cannot be
        /// changed, or null when they can. Models inside immutable packages (registry, git,
        /// tarball, built-in) are read-only: Unity refuses or silently reverts changes to them.
        /// Embedded and local packages are regular files on disk and are editable.
        /// </summary>
        internal static string GetReadOnlyReason(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return null;

            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);

            if (packageInfo != null &&
                (packageInfo.source == UnityEditor.PackageManager.PackageSource.Embedded ||
                 packageInfo.source == UnityEditor.PackageManager.PackageSource.Local))
            {
                return null;
            }

            string packageName = packageInfo != null
                ? (string.IsNullOrEmpty(packageInfo.displayName) ? packageInfo.name : packageInfo.displayName)
                : "an immutable package";

            return $"'{System.IO.Path.GetFileName(assetPath)}' is inside the read-only package '{packageName}', " +
                   "so its import settings cannot be changed. Copy the model into the Assets folder, use the copy " +
                   "on the avatar, then click Reload.";
        }

        // =====================================================================
        // Serialized property paths & muscle indices
        // =====================================================================

        // Unity stores per-bone limits in the ModelImporter's serialized data at
        // m_HumanDescription.m_Human[].m_Limit. Each limit has:
        //   m_Modified (bool)  — false means "use HumanTrait defaults"
        //   m_Min (Vector3)    — custom minimum limits (only used when m_Modified=true)
        //   m_Max (Vector3)    — custom maximum limits (only used when m_Modified=true)
        //
        // For eye bones, the per-bone limit axes map as:
        //   X axis = unused (always 0 for eye bones)
        //   Y axis = In/Out (yaw):    min.y = In,   max.y = Out
        //   Z axis = Down/Up (pitch): min.z = Down,  max.z = Up
        //
        // When m_Modified is false, the effective muscle limits come from
        // HumanTrait.GetMuscleDefaultMin/Max at these muscle indices:
        //   [15] "Left Eye Down-Up"   — default -10 to 15
        //   [16] "Left Eye In-Out"    — default -20 to 20
        //   [17] "Right Eye Down-Up"  — default -10 to 15
        //   [18] "Right Eye In-Out"   — default -20 to 20

        /// <summary>
        /// SerializedProperty path for the human bone limit array in the ModelImporter.
        /// </summary>
        private const string HumanBoneArrayPath = "m_HumanDescription.m_Human";

        // =====================================================================
        // Muscle index helpers
        // =====================================================================

        /// <summary>
        /// Finds the HumanTrait muscle index for a given muscle name.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindMuscleIndex(string muscleName)
        {
            string[] names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == muscleName) return i;
            }
            return -1;
        }

        // =====================================================================
        // Read muscle values
        // =====================================================================

        /// <summary>
        /// Reads the eye muscle limit values for a single eye from the ModelImporter.
        /// If the per-bone limit has been customized (m_Modified=true), reads from
        /// the serialized m_Limit.m_Min/m_Max vectors. Otherwise, falls back to
        /// Unity's built-in default muscle ranges via HumanTrait.
        /// </summary>
        /// <param name="serializedImporter">SerializedObject for the ModelImporter.</param>
        /// <param name="humanBoneName">The human bone name ("LeftEye" or "RightEye").</param>
        /// <param name="isLeftEye">Whether this is the left eye (determines muscle name prefix).</param>
        private static EyeMuscleValues ReadEyeMuscleValues(SerializedObject serializedImporter, string humanBoneName, bool isLeftEye)
        {
            SerializedProperty humanArray = serializedImporter.FindProperty(HumanBoneArrayPath);

            if (humanArray == null || !humanArray.isArray)
            {
                Debug.LogWarning($"{LogPrefix} Could not find '{HumanBoneArrayPath}' property on ModelImporter.");
                return GetDefaultEyeMuscleValues(isLeftEye);
            }

            for (int i = 0; i < humanArray.arraySize; i++)
            {
                SerializedProperty element = humanArray.GetArrayElementAtIndex(i);
                SerializedProperty nameProp = element.FindPropertyRelative("m_HumanName");

                if (nameProp == null || nameProp.stringValue != humanBoneName)
                {
                    continue;
                }

                SerializedProperty modified = element.FindPropertyRelative("m_Limit.m_Modified");
                bool isModified = modified != null && modified.boolValue;

                if (!isModified)
                {
                    // Bone uses Unity's default muscle limits
                    return GetDefaultEyeMuscleValues(isLeftEye);
                }

                // Bone has custom limits — read them
                SerializedProperty limitMin = element.FindPropertyRelative("m_Limit.m_Min");
                SerializedProperty limitMax = element.FindPropertyRelative("m_Limit.m_Max");

                if (limitMin == null || limitMax == null)
                {
                    Debug.LogWarning($"{LogPrefix} Could not find limit min/max properties for '{humanBoneName}'.");
                    return GetDefaultEyeMuscleValues(isLeftEye);
                }

                Vector3 min = limitMin.vector3Value;
                Vector3 max = limitMax.vector3Value;

                return new EyeMuscleValues
                {
                    In = min.y,
                    Out = max.y,
                    Down = min.z,
                    Up = max.z
                };
            }

            Debug.LogWarning($"{LogPrefix} '{humanBoneName}' not found in HumanDescription bone array. Using defaults.");
            return GetDefaultEyeMuscleValues(isLeftEye);
        }

        /// <summary>
        /// Returns Unity's default eye muscle limits (from HumanTrait) for the given eye: what a model uses
        /// until its eye limits are customized. In and Down are negative, like the stored values.
        /// </summary>
        internal static EyeMuscleValues GetDefaultEyeMuscleValues(bool isLeftEye)
        {
            string prefix = isLeftEye ? "Left" : "Right";
            int downUpIdx = FindMuscleIndex($"{prefix} Eye Down-Up");
            int inOutIdx = FindMuscleIndex($"{prefix} Eye In-Out");

            float downUpMin = downUpIdx >= 0 ? HumanTrait.GetMuscleDefaultMin(downUpIdx) : -10f;
            float downUpMax = downUpIdx >= 0 ? HumanTrait.GetMuscleDefaultMax(downUpIdx) : 15f;
            float inOutMin = inOutIdx >= 0 ? HumanTrait.GetMuscleDefaultMin(inOutIdx) : -20f;
            float inOutMax = inOutIdx >= 0 ? HumanTrait.GetMuscleDefaultMax(inOutIdx) : 20f;

            return new EyeMuscleValues
            {
                In = inOutMin,
                Out = inOutMax,
                Down = downUpMin,
                Up = downUpMax
            };
        }

        // =====================================================================
        // Write muscle values
        // =====================================================================

        /// <summary>
        /// Applies new eye muscle values to the ModelImporter using SerializedObject
        /// and reimports the model. Sets m_Modified=true on the per-bone limits so
        /// Unity uses custom values instead of HumanTrait defaults.
        /// <para>
        /// The write is not registered with Undo: Unity's undo would only revert the in-memory
        /// importer, leaving the reimported model (and the .meta on disk) out of sync. Callers
        /// take a <see cref="CaptureEyeLimits"/> snapshot first and offer
        /// <see cref="RestoreEyeLimits"/> as the way back.
        /// </para>
        /// </summary>
        /// <param name="importer">The ModelImporter to modify.</param>
        /// <param name="leftEye">New left eye muscle values.</param>
        /// <param name="rightEye">New right eye muscle values.</param>
        /// <returns>True if the values were applied and reimport was triggered. False when the
        /// model is read-only (see <see cref="GetReadOnlyReason"/>) or its eye bones are missing.</returns>
        internal static bool ApplyEyeMuscleValues(ModelImporter importer, EyeMuscleValues leftEye, EyeMuscleValues rightEye)
        {
            if (importer == null)
            {
                Debug.LogError($"{LogPrefix} ModelImporter is null, cannot apply muscle values.");
                return false;
            }

            string readOnlyReason = GetReadOnlyReason(importer.assetPath);
            if (readOnlyReason != null)
            {
                Debug.LogError($"{LogPrefix} {readOnlyReason}");
                return false;
            }

            SerializedObject serializedImporter = new SerializedObject(importer);
            SerializedProperty humanArray = serializedImporter.FindProperty(HumanBoneArrayPath);

            if (humanArray == null || !humanArray.isArray)
            {
                Debug.LogError($"{LogPrefix} Could not find '{HumanBoneArrayPath}' property on ModelImporter.");
                return false;
            }

            bool leftFound = false;
            bool rightFound = false;

            for (int i = 0; i < humanArray.arraySize; i++)
            {
                SerializedProperty element = humanArray.GetArrayElementAtIndex(i);
                SerializedProperty nameProp = element.FindPropertyRelative("m_HumanName");

                if (nameProp == null) continue;

                string boneName = nameProp.stringValue;
                EyeMuscleValues values = null;

                if (boneName == "LeftEye")
                {
                    values = leftEye;
                    leftFound = true;
                }
                else if (boneName == "RightEye")
                {
                    values = rightEye;
                    rightFound = true;
                }

                if (values == null) continue;

                SerializedProperty limitMin = element.FindPropertyRelative("m_Limit.m_Min");
                SerializedProperty limitMax = element.FindPropertyRelative("m_Limit.m_Max");
                SerializedProperty modified = element.FindPropertyRelative("m_Limit.m_Modified");

                if (limitMin == null || limitMax == null)
                {
                    Debug.LogWarning($"{LogPrefix} Could not find limit properties for '{boneName}'.");
                    continue;
                }

                // Mark limits as customized so Unity uses our values instead of defaults
                if (modified != null)
                {
                    modified.boolValue = true;
                }

                Vector3 currentMin = limitMin.vector3Value;
                Vector3 currentMax = limitMax.vector3Value;

                // Eye bone limit axes:
                // X axis = unused (always 0) — preserved as-is
                // Y axis = In/Out (yaw): min.y = In, max.y = Out
                // Z axis = Down/Up (pitch): min.z = Down, max.z = Up
                Vector3 newMin = new Vector3(currentMin.x, values.In, values.Down);
                Vector3 newMax = new Vector3(currentMax.x, values.Out, values.Up);

                limitMin.vector3Value = newMin;
                limitMax.vector3Value = newMax;

                if (leftFound && rightFound) break;
            }

            if (!leftFound)
            {
                Debug.LogWarning($"{LogPrefix} LeftEye bone not found in HumanDescription. Cannot apply left eye muscle values.");
            }

            if (!rightFound)
            {
                Debug.LogWarning($"{LogPrefix} RightEye bone not found in HumanDescription. Cannot apply right eye muscle values.");
            }

            if (!leftFound && !rightFound)
            {
                return false;
            }

            serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();

            return true;
        }

        // =====================================================================
        // Snapshot / revert
        // =====================================================================

        /// <summary>
        /// Captures the eye bone limits currently stored on the importer, including whether they
        /// were customized at all, so <see cref="RestoreEyeLimits"/> can put them back exactly.
        /// </summary>
        internal static EyeLimitSnapshot CaptureEyeLimits(ModelImporter importer)
        {
            if (importer == null) return null;

            SerializedObject serializedImporter = new SerializedObject(importer);
            SerializedProperty humanArray = serializedImporter.FindProperty(HumanBoneArrayPath);
            if (humanArray == null || !humanArray.isArray) return null;

            return new EyeLimitSnapshot
            {
                ModelAssetPath = importer.assetPath,
                LeftEye = ReadBoneLimit(humanArray, "LeftEye"),
                RightEye = ReadBoneLimit(humanArray, "RightEye")
            };
        }

        /// <summary>
        /// Writes a snapshot taken by <see cref="CaptureEyeLimits"/> back to the importer and
        /// reimports the model. Bones that used Unity's default limits go back to the defaults.
        /// </summary>
        /// <returns>True when the snapshot was written and the model reimported.</returns>
        internal static bool RestoreEyeLimits(ModelImporter importer, EyeLimitSnapshot snapshot)
        {
            if (importer == null || snapshot == null) return false;

            if (importer.assetPath != snapshot.ModelAssetPath)
            {
                Debug.LogError($"{LogPrefix} The saved eye muscle settings belong to '{snapshot.ModelAssetPath}', not '{importer.assetPath}'.");
                return false;
            }

            string readOnlyReason = GetReadOnlyReason(importer.assetPath);
            if (readOnlyReason != null)
            {
                Debug.LogError($"{LogPrefix} {readOnlyReason}");
                return false;
            }

            SerializedObject serializedImporter = new SerializedObject(importer);
            SerializedProperty humanArray = serializedImporter.FindProperty(HumanBoneArrayPath);
            if (humanArray == null || !humanArray.isArray)
            {
                Debug.LogError($"{LogPrefix} Could not find '{HumanBoneArrayPath}' property on ModelImporter.");
                return false;
            }

            bool restored = WriteBoneLimit(humanArray, "LeftEye", snapshot.LeftEye) |
                            WriteBoneLimit(humanArray, "RightEye", snapshot.RightEye);
            if (!restored) return false;

            serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();

            Debug.Log($"{LogPrefix} Restored the previous eye muscle settings of '{System.IO.Path.GetFileName(importer.assetPath)}'.");
            return true;
        }

        private static SerializedProperty FindHumanBone(SerializedProperty humanArray, string humanBoneName)
        {
            for (int i = 0; i < humanArray.arraySize; i++)
            {
                SerializedProperty element = humanArray.GetArrayElementAtIndex(i);
                SerializedProperty nameProp = element.FindPropertyRelative("m_HumanName");
                if (nameProp != null && nameProp.stringValue == humanBoneName) return element;
            }

            return null;
        }

        private static EyeBoneLimit ReadBoneLimit(SerializedProperty humanArray, string humanBoneName)
        {
            SerializedProperty element = FindHumanBone(humanArray, humanBoneName);
            SerializedProperty modified = element?.FindPropertyRelative("m_Limit.m_Modified");
            SerializedProperty limitMin = element?.FindPropertyRelative("m_Limit.m_Min");
            SerializedProperty limitMax = element?.FindPropertyRelative("m_Limit.m_Max");

            if (limitMin == null || limitMax == null) return new EyeBoneLimit { Found = false };

            return new EyeBoneLimit
            {
                Found = true,
                Modified = modified != null && modified.boolValue,
                Min = limitMin.vector3Value,
                Max = limitMax.vector3Value
            };
        }

        private static bool WriteBoneLimit(SerializedProperty humanArray, string humanBoneName, EyeBoneLimit limit)
        {
            if (limit == null || !limit.Found) return false;

            SerializedProperty element = FindHumanBone(humanArray, humanBoneName);
            SerializedProperty modified = element?.FindPropertyRelative("m_Limit.m_Modified");
            SerializedProperty limitMin = element?.FindPropertyRelative("m_Limit.m_Min");
            SerializedProperty limitMax = element?.FindPropertyRelative("m_Limit.m_Max");

            if (limitMin == null || limitMax == null) return false;

            if (modified != null) modified.boolValue = limit.Modified;
            limitMin.vector3Value = limit.Min;
            limitMax.vector3Value = limit.Max;
            return true;
        }

        // =====================================================================
        // Preview helpers
        // =====================================================================

        /// <summary>
        /// Calculates an approximate eye rotation quaternion for previewing how the
        /// Face Tracking Controller will drive the eye at its extreme animation value.
        /// The actual bone rotation is approximately <c>muscleLimit * ftAnimationValue</c>,
        /// where the FT animation clips set muscle values like -2 (In), 2 (Out),
        /// 2.8 (Up), -3.5 (Down). This previews the maximum rotation the FT system
        /// will produce for the given rig limits.
        /// <para>
        /// The rotation is expressed in the avatar's space (X = the avatar's right, so pitch;
        /// Y = the avatar's up, so yaw), like humanoid eye muscles, and not in the eye bone's local
        /// space: bone axes depend on how the rig was authored (e.g. Blender bones point along
        /// their local Y), so a local X/Y rotation turns the eye the wrong way on many rigs.
        /// Convert it to world space with the avatar root's rotation before applying it.
        /// </para>
        /// </summary>
        /// <param name="direction">The direction: "In", "Out", "Up", or "Down".</param>
        /// <param name="muscleLimit">The muscle limit value (degrees) set on the rig.</param>
        /// <param name="isLeftEye">Whether this is the left eye (affects In/Out yaw sign).</param>
        /// <returns>A rotation in avatar space representing the predicted eye movement.</returns>
        internal static Quaternion GetEyeRotation(string direction, float muscleLimit, bool isLeftEye)
        {
            float ftAnimValue;
            switch (direction)
            {
                case "In":  ftAnimValue = FaceTrackingPreset.In;  break;
                case "Out": ftAnimValue = FaceTrackingPreset.Out; break;
                case "Up":  ftAnimValue = FaceTrackingPreset.Up;  break;
                case "Down":ftAnimValue = FaceTrackingPreset.Down;break;
                default: return Quaternion.identity;
            }

            // Bone rotation ≈ muscleLimit * ftAnimationValue
            float rotation = muscleLimit * ftAnimValue;

            switch (direction)
            {
                case "Up":
                    // Negative pitch = look up; rotation is positive (e.g. 8 * 2.8 = 22.4)
                    return Quaternion.Euler(-Mathf.Abs(rotation), 0f, 0f);
                case "Down":
                    // Positive pitch = look down; rotation is positive (e.g. -8 * -3.5 = 28)
                    return Quaternion.Euler(Mathf.Abs(rotation), 0f, 0f);
                case "In":
                    // Left eye In = look toward the avatar's right (+Y), Right eye In = toward its left (-Y)
                    float inYaw = isLeftEye ? rotation : -rotation;
                    return Quaternion.Euler(0f, inYaw, 0f);
                case "Out":
                    // Left eye Out = look left (-Y), Right eye Out = look right (+Y)
                    float outYaw = isLeftEye ? -rotation : rotation;
                    return Quaternion.Euler(0f, outYaw, 0f);
                default:
                    return Quaternion.identity;
            }
        }
    }
}
