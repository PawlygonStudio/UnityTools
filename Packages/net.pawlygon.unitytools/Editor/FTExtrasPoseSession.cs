using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Binds an <see cref="FTExtrasProfile"/> to an avatar in the scene, applies poses to its bones,
    /// and guarantees the original rotations come back: on <see cref="Restore"/>, before entering
    /// play mode, before a script reload, and around scene saves (so a pose being edited is never
    /// saved into the scene).
    /// </summary>
    internal class FTExtrasPoseSession
    {
        private const float ChangeThresholdDegrees = 0.01f;

        public FTExtrasProfile Profile { get; }
        public Transform AvatarRoot { get; }
        public Transform[] EarLeft { get; private set; }
        public Transform[] EarRight { get; private set; }
        public Transform[] Tail { get; private set; }

        private Dictionary<Transform, Quaternion> snapshot;
        private Dictionary<Transform, Quaternion> suspended;
        private Quaternion[] lastLeft;
        private Quaternion[] lastRight;

        /// <summary>The session currently holding bones away from their original rotations, if any.</summary>
        internal static FTExtrasPoseSession Active { get; private set; }

        public bool IsActive => snapshot != null;

        private FTExtrasPoseSession(FTExtrasProfile profile, Transform avatarRoot)
        {
            Profile = profile;
            AvatarRoot = avatarRoot;
        }

        // =====================================================================
        // Binding
        // =====================================================================

        /// <summary>
        /// Resolves the profile's bone paths on <paramref name="avatarRoot"/>. Returns null and sets
        /// <paramref name="error"/> if a bone is missing.
        /// </summary>
        internal static FTExtrasPoseSession Bind(FTExtrasProfile profile, Transform avatarRoot, out string error)
        {
            var session = new FTExtrasPoseSession(profile, avatarRoot);
            var missing = new List<string>();

            session.EarLeft = Resolve(profile.earLeft, avatarRoot, missing);
            session.EarRight = Resolve(profile.earRight, avatarRoot, missing);
            session.Tail = Resolve(profile.tail, avatarRoot, missing);

            if (missing.Count > 0)
            {
                error = $"Bones from the profile were not found on '{avatarRoot.name}': {string.Join(", ", missing)}";
                return null;
            }

            error = null;
            return session;
        }

        private static Transform[] Resolve(List<FTExtrasProfileBone> bones, Transform avatarRoot, List<string> missing)
        {
            var result = new Transform[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                result[i] = avatarRoot.Find(bones[i].path);
                if (result[i] == null) missing.Add(bones[i].path);
            }
            return result;
        }

        /// <summary>
        /// Captures a bone's current rotation as its rest pose.
        /// </summary>
        internal static FTExtrasProfileBone CaptureRest(Transform bone, Transform avatarRoot)
        {
            return new FTExtrasProfileBone
            {
                path = FaceTrackingExtrasCore.GetRelativePath(bone, avatarRoot),
                restLocal = bone.localRotation,
                restAvatar = Quaternion.Inverse(avatarRoot.rotation) * bone.rotation,
            };
        }

        private IEnumerable<Transform> AllBones => EarLeft.Concat(EarRight).Concat(Tail);

        // =====================================================================
        // Begin / apply / restore
        // =====================================================================

        /// <summary>
        /// Remembers the current bone rotations so they can be restored. Restores any other active session first.
        /// </summary>
        public void Begin()
        {
            if (IsActive) return;
            if (Active != null && Active != this) Active.Restore();

            snapshot = AllBones.Distinct().ToDictionary(t => t, t => t.localRotation);
            Active = this;
        }

        /// <summary>
        /// Writes a pose to the bones. Lists that are missing or the wrong length are skipped.
        /// </summary>
        public void Apply(FTExtrasPoseData pose)
        {
            if (pose == null) return;

            Write(EarLeft, pose.earLeft);
            Write(EarRight, pose.earRight);
            Write(Tail, pose.tail);
            SceneView.RepaintAll();
        }

        private static void Write(Transform[] bones, List<Quaternion> rotations)
        {
            if (rotations == null || rotations.Count != bones.Length) return;
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i].localRotation = rotations[i];
            }
        }

        /// <summary>
        /// Puts every bone back to the rotation it had when <see cref="Begin"/> was called.
        /// </summary>
        public void Restore()
        {
            if (!IsActive) return;

            WriteSnapshot();

            snapshot = null;
            suspended = null;
            if (Active == this) Active = null;
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Reads the current rotations of the bones in the pose's group.
        /// </summary>
        public FTExtrasPoseData Capture(FTExtrasPoseId id)
        {
            var pose = new FTExtrasPoseData { id = id };
            if (FTExtrasPoses.Get(id).Group == FTExtrasPoses.PoseGroup.Tail)
            {
                pose.tail = Tail.Select(t => t.localRotation).ToList();
            }
            else
            {
                pose.earLeft = EarLeft.Select(t => t.localRotation).ToList();
                pose.earRight = EarRight.Select(t => t.localRotation).ToList();
            }
            return pose;
        }

        // =====================================================================
        // Live mirroring
        // =====================================================================

        /// <summary>
        /// Starts tracking ear rotations so <see cref="UpdateLiveMirror"/> can tell which side the user moved.
        /// </summary>
        public void ResetMirrorTracking()
        {
            lastLeft = EarLeft.Select(t => t.localRotation).ToArray();
            lastRight = EarRight.Select(t => t.localRotation).ToArray();
        }

        /// <summary>
        /// For every ear bone the user rotated since the last call, writes the mirrored rotation to the
        /// matching bone on the other ear. Returns true if anything changed.
        /// </summary>
        public bool UpdateLiveMirror()
        {
            if (lastLeft == null || lastRight == null) ResetMirrorTracking();

            int count = Mathf.Min(EarLeft.Length, EarRight.Length);
            bool changed = false;

            for (int i = 0; i < count; i++)
            {
                bool leftMoved = Quaternion.Angle(EarLeft[i].localRotation, lastLeft[i]) > ChangeThresholdDegrees;
                bool rightMoved = Quaternion.Angle(EarRight[i].localRotation, lastRight[i]) > ChangeThresholdDegrees;

                if (leftMoved)
                {
                    EarRight[i].localRotation = FTExtrasPoses.MirrorLocal(EarLeft[i].localRotation, Profile.earLeft[i], Profile.earRight[i]);
                    changed = true;
                }
                else if (rightMoved)
                {
                    EarLeft[i].localRotation = FTExtrasPoses.MirrorLocal(EarRight[i].localRotation, Profile.earRight[i], Profile.earLeft[i]);
                    changed = true;
                }
            }

            if (changed)
            {
                ResetMirrorTracking();
                SceneView.RepaintAll();
            }
            return changed;
        }

        // =====================================================================
        // Scene save / reload / play mode safety
        // =====================================================================

        internal void SuspendForSave()
        {
            if (!IsActive || suspended != null) return;

            suspended = snapshot.Keys.Where(t => t != null).ToDictionary(t => t, t => t.localRotation);
            WriteSnapshot();
        }

        /// <summary>
        /// Writes the original rotations back. Rotating bones with the gizmo records prefab overrides
        /// with the posed values, so the overrides are re-recorded to match what is written.
        /// </summary>
        private void WriteSnapshot()
        {
            foreach (var pair in snapshot)
            {
                if (pair.Key == null) continue;

                pair.Key.localRotation = pair.Value;
                if (PrefabUtility.IsPartOfPrefabInstance(pair.Key))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(pair.Key);
                }
            }
        }

        internal void ResumeAfterSave()
        {
            if (suspended == null) return;

            foreach (var pair in suspended)
            {
                if (pair.Key != null) pair.Key.localRotation = pair.Value;
            }
            suspended = null;
            ResetMirrorTracking();
        }
    }

    /// <summary>
    /// Restores posed bones before play mode and script reloads, and keeps edited poses out of saved scenes.
    /// </summary>
    [InitializeOnLoad]
    internal static class FTExtrasPoseSessionSafety
    {
        static FTExtrasPoseSessionSafety()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => FTExtrasPoseSession.Active?.Restore();
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) FTExtrasPoseSession.Active?.Restore();
            };
            EditorSceneManager.sceneSaving += (scene, path) => FTExtrasPoseSession.Active?.SuspendForSave();
            EditorSceneManager.sceneSaved += scene => FTExtrasPoseSession.Active?.ResumeAfterSave();
        }
    }
}
