using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// What a baseline copies into a profile.
    /// </summary>
    [Flags]
    public enum FTExtrasBaselineParts
    {
        None = 0,
        Settings = 1,
        CustomAnimations = 2,
        Poses = 4,
        All = Settings | CustomAnimations | Poses,
    }

    /// <summary>
    /// A starting point for other avatars' Face Tracking Extras profiles: settings and timings, custom
    /// animations, and ear/tail poses. Applying it copies the data once; later edits to the baseline don't
    /// change profiles that were made from it until it is applied again.
    ///
    /// Poses are stored as rotations in avatar space relative to each bone's rest pose, so they carry over to
    /// avatars whose bones are oriented differently, as long as the chains have the same number of bones.
    /// </summary>
    public class FTExtrasBaseline : ScriptableObject
    {
        [Tooltip("The avatar this baseline was saved from.")]
        public string sourceAvatarName;

        [Header("Chains")]
        public int earLeftBones;
        public int earRightBones;
        public int tailBones;

        [Tooltip("Pose rotations relative to rest, in avatar space, parallel to the chains.")]
        public List<FTExtrasPoseData> poses = new List<FTExtrasPoseData>();

        [Header("Timings")]
        public float earFlickPeriod = 0.67f;
        public float tailWagPeriod = 0.6f;
        public float tailWagAmount = 1f;
        public float tailWagDelay = 0.1f;

        public FTExtrasGenerationSettings generation = new FTExtrasGenerationSettings();
        public List<FTExtrasCustomAnimation> customAnimations = new List<FTExtrasCustomAnimation>();

        /// <summary>Outcome of applying a baseline, for the status message.</summary>
        public class ApplyResult
        {
            public bool Settings;
            public int CustomAnimations;
            public readonly List<FTExtrasPoseId> AppliedPoses = new List<FTExtrasPoseId>();
            public readonly List<string> SkippedPoses = new List<string>();

            public string Describe(string baselineName)
            {
                var parts = new List<string>();
                if (Settings) parts.Add("settings and timings");
                if (CustomAnimations > 0) parts.Add($"{CustomAnimations} custom animation{(CustomAnimations == 1 ? string.Empty : "s")}");
                if (AppliedPoses.Count > 0) parts.Add($"{AppliedPoses.Count} pose{(AppliedPoses.Count == 1 ? string.Empty : "s")}");

                string text = parts.Count > 0
                    ? $"Applied '{baselineName}': {string.Join(", ", parts)}."
                    : $"Nothing from '{baselineName}' could be applied.";
                if (SkippedPoses.Count > 0) text += $" Skipped: {string.Join("; ", SkippedPoses)}.";
                return text;
            }
        }

        public bool HasPoses => poses.Any(p => p.earLeft.Count + p.earRight.Count + p.tail.Count > 0);

        public string Summary
        {
            get
            {
                var parts = new List<string> { "settings" };
                if (customAnimations.Count > 0) parts.Add($"{customAnimations.Count} custom animation{(customAnimations.Count == 1 ? string.Empty : "s")}");
                int poseCount = poses.Count(p => p.earLeft.Count + p.earRight.Count + p.tail.Count > 0);
                if (poseCount > 0) parts.Add($"{poseCount} pose{(poseCount == 1 ? string.Empty : "s")}");
                string source = string.IsNullOrEmpty(sourceAvatarName) ? string.Empty : $" (from {sourceAvatarName})";
                return string.Join(", ", parts) + source;
            }
        }

        // =====================================================================
        // Save
        // =====================================================================

        /// <summary>
        /// Replaces this baseline's contents with <paramref name="profile"/>'s.
        /// </summary>
        public void CaptureFrom(FTExtrasProfile profile)
        {
            sourceAvatarName = profile.avatarName;
            earLeftBones = profile.earLeft.Count;
            earRightBones = profile.earRight.Count;
            tailBones = profile.tail.Count;

            earFlickPeriod = profile.earFlickPeriod;
            tailWagPeriod = profile.tailWagPeriod;
            tailWagAmount = profile.tailWagAmount;
            tailWagDelay = profile.tailWagDelay;
            generation = Clone(profile.generation);
            customAnimations = Clone(profile.customAnimations);

            poses = new List<FTExtrasPoseData>();
            foreach (FTExtrasPoseData stored in profile.poses)
            {
                FTExtrasPoseData pose = profile.GetStoredPose(stored.id);
                if (pose == null) continue;

                poses.Add(new FTExtrasPoseData
                {
                    id = pose.id,
                    earLeft = ToAvatarDeltas(profile.earLeft, pose.earLeft),
                    earRight = ToAvatarDeltas(profile.earRight, pose.earRight),
                    tail = ToAvatarDeltas(profile.tail, pose.tail),
                });
            }
        }

        // =====================================================================
        // Apply
        // =====================================================================

        /// <summary>
        /// Copies the chosen parts into <paramref name="profile"/>, replacing what it had. Poses need the
        /// profile's chains to be saved and to have the same bone counts as the baseline's.
        /// </summary>
        public ApplyResult ApplyTo(FTExtrasProfile profile, FTExtrasBaselineParts parts)
        {
            var result = new ApplyResult();

            if ((parts & FTExtrasBaselineParts.Settings) != 0)
            {
                profile.earFlickPeriod = earFlickPeriod;
                profile.tailWagPeriod = tailWagPeriod;
                profile.tailWagAmount = tailWagAmount;
                profile.tailWagDelay = tailWagDelay;
                profile.generation = Clone(generation);
                result.Settings = true;
            }

            if ((parts & FTExtrasBaselineParts.CustomAnimations) != 0)
            {
                profile.customAnimations = Clone(customAnimations);
                result.CustomAnimations = profile.customAnimations.Count;
            }

            if ((parts & FTExtrasBaselineParts.Poses) != 0)
            {
                ApplyPoses(profile, result);
            }

            return result;
        }

        private void ApplyPoses(FTExtrasProfile profile, ApplyResult result)
        {
            bool earsMatch = profile.earLeft.Count == earLeftBones && profile.earRight.Count == earRightBones
                && earLeftBones + earRightBones > 0;
            bool tailMatches = profile.tail.Count == tailBones && tailBones > 0;

            bool hasEarPoses = false, hasTailPoses = false;
            foreach (FTExtrasPoseData pose in poses)
            {
                bool isTail = FTExtrasPoses.Get(pose.id).Group == FTExtrasPoses.PoseGroup.Tail;
                if (isTail) hasTailPoses |= pose.tail.Count > 0;
                else hasEarPoses |= pose.earLeft.Count + pose.earRight.Count > 0;

                if (isTail ? !tailMatches : !earsMatch) continue;
                if (isTail ? pose.tail.Count != tailBones : pose.earLeft.Count != earLeftBones || pose.earRight.Count != earRightBones) continue;

                profile.SetStoredPose(new FTExtrasPoseData
                {
                    id = pose.id,
                    earLeft = FromAvatarDeltas(profile.earLeft, pose.earLeft),
                    earRight = FromAvatarDeltas(profile.earRight, pose.earRight),
                    tail = FromAvatarDeltas(profile.tail, pose.tail),
                });
                result.AppliedPoses.Add(pose.id);
            }

            if (hasEarPoses && !earsMatch)
            {
                result.SkippedPoses.Add(
                    $"ear poses (ears have {profile.earLeft.Count}/{profile.earRight.Count} bones, the baseline {earLeftBones}/{earRightBones})");
            }
            if (hasTailPoses && !tailMatches)
            {
                result.SkippedPoses.Add($"tail poses (tail has {profile.tail.Count} bones, the baseline {tailBones})");
            }
        }

        // =====================================================================
        // Pose conversion
        // =====================================================================

        // A pose stores local rotations: local = restLocal * d, where d rotates the bone in its own rest frame.
        // In avatar space that is D = restAvatar * d * restAvatar⁻¹, which doesn't depend on how the bone's axes
        // happen to be oriented, so it carries over to another rig: d' = restAvatar'⁻¹ * D * restAvatar'.

        private static List<Quaternion> ToAvatarDeltas(List<FTExtrasProfileBone> bones, List<Quaternion> local)
        {
            var result = new List<Quaternion>(local.Count);
            for (int i = 0; i < local.Count && i < bones.Count; i++)
            {
                Quaternion d = Quaternion.Inverse(bones[i].restLocal) * local[i];
                result.Add(Normalize(bones[i].restAvatar * d * Quaternion.Inverse(bones[i].restAvatar)));
            }
            return result;
        }

        private static List<Quaternion> FromAvatarDeltas(List<FTExtrasProfileBone> bones, List<Quaternion> deltas)
        {
            var result = new List<Quaternion>(deltas.Count);
            for (int i = 0; i < deltas.Count && i < bones.Count; i++)
            {
                Quaternion d = Quaternion.Inverse(bones[i].restAvatar) * deltas[i] * bones[i].restAvatar;
                result.Add(Normalize(bones[i].restLocal * d));
            }
            return result;
        }

        private static Quaternion Normalize(Quaternion q)
        {
            float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return length > 1e-6f ? new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length) : Quaternion.identity;
        }

        // =====================================================================
        // Copying
        // =====================================================================

        [Serializable]
        private class Copy
        {
            public FTExtrasGenerationSettings generation;
            public List<FTExtrasCustomAnimation> customAnimations;
        }

        /// <summary>
        /// Deep copies through the editor serializer, so asset references (animation clips) are kept.
        /// </summary>
        private static FTExtrasGenerationSettings Clone(FTExtrasGenerationSettings value)
        {
            var copy = new Copy { generation = new FTExtrasGenerationSettings() };
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(new Copy { generation = value }), copy);
            return copy.generation ?? new FTExtrasGenerationSettings();
        }

        private static List<FTExtrasCustomAnimation> Clone(List<FTExtrasCustomAnimation> value)
        {
            var copy = new Copy { customAnimations = new List<FTExtrasCustomAnimation>() };
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(new Copy { customAnimations = value }), copy);
            return copy.customAnimations ?? new List<FTExtrasCustomAnimation>();
        }

        // =====================================================================
        // Lookup
        // =====================================================================

        internal const string LastBaselinePrefKey = "Pawlygon.FaceTrackingExtras.LastBaseline";

        /// <summary>
        /// The baseline used most recently, or the only one in the project.
        /// </summary>
        internal static FTExtrasBaseline FindDefault()
        {
            string guid = EditorPrefs.GetString(LastBaselinePrefKey, string.Empty);
            if (!string.IsNullOrEmpty(guid))
            {
                var remembered = AssetDatabase.LoadAssetAtPath<FTExtrasBaseline>(AssetDatabase.GUIDToAssetPath(guid));
                if (remembered != null) return remembered;
            }

            string[] all = AssetDatabase.FindAssets($"t:{nameof(FTExtrasBaseline)}");
            return all.Length == 1 ? AssetDatabase.LoadAssetAtPath<FTExtrasBaseline>(AssetDatabase.GUIDToAssetPath(all[0])) : null;
        }

        internal static void Remember(FTExtrasBaseline baseline)
        {
            if (baseline == null) return;
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(baseline));
            if (!string.IsNullOrEmpty(guid)) EditorPrefs.SetString(LastBaselinePrefKey, guid);
        }

        /// <summary>
        /// Number of properties <paramref name="clip"/> animates that don't exist on <paramref name="avatar"/>
        /// (e.g. a custom animation made for another avatar's hierarchy).
        /// </summary>
        internal static int CountMissingBindings(AnimationClip clip, GameObject avatar)
        {
            if (clip == null || avatar == null) return 0;
            return AnimationUtility.GetCurveBindings(clip)
                .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip))
                .Count(b => AnimationUtility.GetAnimatedObject(avatar, b) == null);
        }
    }
}
