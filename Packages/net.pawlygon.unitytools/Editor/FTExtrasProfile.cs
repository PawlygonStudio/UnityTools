using System;
using System.Collections.Generic;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Identifies an ear or tail pose. Derived poses (Look Left, Tail Left) are computed by
    /// mirroring their source pose and are never stored.
    /// </summary>
    public enum FTExtrasPoseId
    {
        EarLookRight = 0,
        EarLookUp = 1,
        EarLookDown = 2,
        EarSad = 3,
        EarHappy = 4,
        EarHappyFlick = 5,

        TailRight = 10,
        TailHappy = 11,

        // Derived
        EarLookLeft = 100,
        TailLeft = 110,
    }

    /// <summary>
    /// A bone in one of the profile's chains, with its rest pose.
    /// </summary>
    [Serializable]
    public class FTExtrasProfileBone
    {
        /// <summary>Path relative to the avatar root, as used by AnimationClip bindings.</summary>
        public string path;

        /// <summary>Rest local rotation.</summary>
        public Quaternion restLocal;

        /// <summary>Rest rotation in avatar root space (+X right, +Y up, +Z forward). Used for mirroring.</summary>
        public Quaternion restAvatar;
    }

    /// <summary>
    /// Local rotations for one pose, parallel to the profile's bone lists. Ear poses fill
    /// <see cref="earLeft"/> and <see cref="earRight"/>; tail poses fill <see cref="tail"/>.
    /// </summary>
    [Serializable]
    public class FTExtrasPoseData
    {
        public FTExtrasPoseId id;
        public List<Quaternion> earLeft = new List<Quaternion>();
        public List<Quaternion> earRight = new List<Quaternion>();
        public List<Quaternion> tail = new List<Quaternion>();
    }

    /// <summary>
    /// Settings for generating the animator: face tracking parameter names, input ranges, and the
    /// fake pupil dilation.
    /// </summary>
    [Serializable]
    public class FTExtrasGenerationSettings
    {
        [Header("Face Tracking Parameters")]
        public string eyeLeftX = "OSCm/Proxy/FT/v2/EyeLeftX";
        public string eyeRightX = "OSCm/Proxy/FT/v2/EyeRightX";
        public string eyeY = "OSCm/Proxy/FT/v2/EyeY";
        public string smileFrownLeft = "OSCm/Proxy/FT/v2/SmileFrownLeft";
        public string smileFrownRight = "OSCm/Proxy/FT/v2/SmileFrownRight";
        public string jawX = "OSCm/Proxy/FT/v2/JawX";
        public string eyeLidLeft = "OSCm/Proxy/FT/v2/EyeLidLeft";
        public string eyeLidRight = "OSCm/Proxy/FT/v2/EyeLidRight";
        public string eyeTrackingActive = "EyeTrackingActive";
        public string lipTrackingActive = "LipTrackingActive";
        public string eyeDilationEnable = "EyeDilationEnable";

        [Header("Input Ranges")]
        [Tooltip("Eye X/Y value at which the Look poses are fully applied.")]
        public float gazeFullAt = 0.5f;
        [Tooltip("Average Smile/Frown value at which Happy or Sad is fully applied.")]
        public float moodFullAt = 0.6f;
        [Tooltip("Jaw X value at which Tail Left/Right is fully applied.")]
        public float jawFullAt = 0.7f;

        [Header("Menu")]
        [Tooltip("Synced, saved toggle that lets the jaw swing the tail.")]
        public string tailFollowsJawParameter = "Pawlygon/TailFollowsJaw";
        [Tooltip("Submenu the toggle is placed in.")]
        public string menuName = "Face Tracking Extras";

        [Header("Fake Pupil Dilation")]
        [Tooltip("Animates EyeDilation/EyeConstrict while eye tracking is active and real pupil dilation is off.")]
        public bool fakeDilation = true;
        [Range(0f, 100f)] public float idleDilationMin = 0f;
        [Range(0f, 100f)] public float idleDilationMax = 10f;
        [Tooltip("EyeDilation right after the eyes reopen from a blink.")]
        [Range(0f, 100f)] public float reflexPeakDilation = 70f;
        [Tooltip("EyeConstrict at the tightest point of the reflex.")]
        [Range(0f, 100f)] public float reflexConstrict = 50f;
        [Tooltip("Seconds for the blink reflex to settle back to idle.")]
        public float reflexDuration = 1.8f;
        [Tooltip("Both eyelids below this count as a blink.")]
        [Range(0f, 1f)] public float lidClosedBelow = 0.15f;
        [Tooltip("Either eyelid above this, after a blink, triggers the reflex.")]
        [Range(0f, 1f)] public float lidOpenAbove = 0.35f;
    }

    /// <summary>
    /// Per-avatar Face Tracking Extras settings: the ear and tail bone chains, their rest pose,
    /// and the poses authored for them. Clips are generated from this asset.
    /// </summary>
    public class FTExtrasProfile : ScriptableObject
    {
        public string avatarName;

        public List<FTExtrasProfileBone> earLeft = new List<FTExtrasProfileBone>();
        public List<FTExtrasProfileBone> earRight = new List<FTExtrasProfileBone>();
        public List<FTExtrasProfileBone> tail = new List<FTExtrasProfileBone>();

        public List<FTExtrasPoseData> poses = new List<FTExtrasPoseData>();

        [Tooltip("Seconds for one Happy → Happy Flick → Happy cycle.")]
        public float earFlickPeriod = 0.67f;

        [Tooltip("Seconds for one full tail wag (right → left → right).")]
        public float tailWagPeriod = 0.6f;

        [Tooltip("How far the wag swings, as a fraction of the Tail Right / Tail Left poses.")]
        public float tailWagAmount = 1f;

        [Tooltip("How much each bone down the tail lags behind the one before it, as a fraction of a wag.")]
        public float tailWagDelay = 0.1f;

        public FTExtrasGenerationSettings generation = new FTExtrasGenerationSettings();

        /// <summary>
        /// Returns the stored pose, or null if it is not set or no longer matches the chains.
        /// </summary>
        public FTExtrasPoseData GetStoredPose(FTExtrasPoseId id)
        {
            FTExtrasPoseData pose = poses.Find(p => p.id == id);
            if (pose == null) return null;

            bool isTail = FTExtrasPoses.Get(id).Group == FTExtrasPoses.PoseGroup.Tail;
            bool valid = isTail
                ? pose.tail.Count == tail.Count && tail.Count > 0
                : pose.earLeft.Count == earLeft.Count && pose.earRight.Count == earRight.Count && earLeft.Count + earRight.Count > 0;

            return valid ? pose : null;
        }

        public void SetStoredPose(FTExtrasPoseData pose)
        {
            poses.RemoveAll(p => p.id == pose.id);
            poses.Add(pose);
        }

        public void ClearStoredPose(FTExtrasPoseId id)
        {
            poses.RemoveAll(p => p.id == id);
        }
    }
}
