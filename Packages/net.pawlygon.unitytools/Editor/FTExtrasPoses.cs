using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Pose definitions and pose math for the Face Tracking Extras.
    ///
    /// Poses are stored as absolute local rotations. Mirroring works on the rotation away from rest,
    /// expressed in avatar space, and reflects it across the avatar's YZ plane. This makes mirroring
    /// independent of how each rig orients its bone axes.
    /// </summary>
    internal static class FTExtrasPoses
    {
        internal enum PoseGroup { Ears, Tail }

        internal enum PoseSymmetry
        {
            /// <summary>Both ears mirror each other (Look Up, Sad...). Editing one ear updates the other.</summary>
            MirroredEars,
            /// <summary>Each ear is posed separately (Look Right).</summary>
            IndependentEars,
            /// <summary>Tail chain.</summary>
            Tail,
        }

        internal class PoseDefinition
        {
            public FTExtrasPoseId Id;
            public string Label;
            public PoseGroup Group;
            public PoseSymmetry Symmetry;
            public string Hint;

            /// <summary>For derived poses: the stored pose they are mirrored from.</summary>
            public FTExtrasPoseId? DerivedFrom;
            public bool IsDerived => DerivedFrom.HasValue;
        }

        private static readonly PoseDefinition[] Definitions =
        {
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarLookRight, Label = "Look Right", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.IndependentEars,
                Hint = "Eyes looking to the avatar's right. Pose both ears; the far (left) ear usually swings more. Look Left is mirrored from this.",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarLookLeft, Label = "Look Left", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.IndependentEars,
                DerivedFrom = FTExtrasPoseId.EarLookRight,
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarLookUp, Label = "Look Up", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.MirroredEars,
                Hint = "Eyes looking up. Pose one ear; the other mirrors it.",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarLookDown, Label = "Look Down", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.MirroredEars,
                Hint = "Eyes looking down. Pose one ear; the other mirrors it.",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarSad, Label = "Sad", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.MirroredEars,
                Hint = "Drooped ears while frowning. Pose one ear; the other mirrors it.",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarHappy, Label = "Happy", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.MirroredEars,
                Hint = "Perked ears while smiling. While smiling, the ears loop Happy → Happy Flick → Happy.",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.EarHappyFlick, Label = "Happy Flick", Group = PoseGroup.Ears, Symmetry = PoseSymmetry.MirroredEars,
                Hint = "The other end of the happy flick loop (e.g. ears flicked further outward).",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.TailRight, Label = "Tail Right", Group = PoseGroup.Tail, Symmetry = PoseSymmetry.Tail,
                Hint = "Tail swung to the avatar's right. Tail Left is mirrored from this. Used by the jaw control and as the edge of the happy wag.",
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.TailLeft, Label = "Tail Left", Group = PoseGroup.Tail, Symmetry = PoseSymmetry.Tail,
                DerivedFrom = FTExtrasPoseId.TailRight,
            },
            new PoseDefinition
            {
                Id = FTExtrasPoseId.TailHappy, Label = "Tail Happy", Group = PoseGroup.Tail, Symmetry = PoseSymmetry.Tail,
                Hint = "Optional raised tail while happy. The wag swings around this pose.",
            },
        };

        private static readonly Dictionary<FTExtrasPoseId, PoseDefinition> ById = Definitions.ToDictionary(d => d.Id);

        internal static IEnumerable<PoseDefinition> All => Definitions;

        internal static PoseDefinition Get(FTExtrasPoseId id) => ById[id];

        // =====================================================================
        // Mirroring
        // =====================================================================

        /// <summary>
        /// Reflects an avatar-space rotation across the avatar's YZ plane (left ↔ right).
        /// </summary>
        private static Quaternion ReflectX(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        /// <summary>
        /// Mirrors <paramref name="sourceLocal"/> (a local rotation of <paramref name="source"/>) onto
        /// <paramref name="target"/>. Source and target can be the same bone (centred tail bones).
        /// </summary>
        internal static Quaternion MirrorLocal(Quaternion sourceLocal, FTExtrasProfileBone source, FTExtrasProfileBone target)
        {
            // Rotation away from rest, in the source bone's local frame.
            Quaternion delta = Quaternion.Inverse(source.restLocal) * sourceLocal;

            // The same rotation expressed in avatar space, reflected, then brought into the target's frame.
            Quaternion avatarDelta = source.restAvatar * delta * Quaternion.Inverse(source.restAvatar);
            Quaternion mirrored = ReflectX(avatarDelta);
            Quaternion targetDelta = Quaternion.Inverse(target.restAvatar) * mirrored * target.restAvatar;

            return target.restLocal * targetDelta;
        }

        /// <summary>
        /// Mirrors a chain onto another chain bone by bone (index i → index i). Returns null when lengths differ.
        /// </summary>
        internal static List<Quaternion> MirrorChain(List<Quaternion> sourceLocals, List<FTExtrasProfileBone> source, List<FTExtrasProfileBone> target)
        {
            if (sourceLocals == null || sourceLocals.Count != source.Count || source.Count != target.Count) return null;

            var result = new List<Quaternion>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                result.Add(MirrorLocal(sourceLocals[i], source[i], target[i]));
            }
            return result;
        }

        // =====================================================================
        // Pose resolution
        // =====================================================================

        /// <summary>
        /// Returns a pose, deriving it by mirroring when needed. Null if the pose (or its source) is not set.
        /// </summary>
        internal static FTExtrasPoseData Resolve(FTExtrasProfile profile, FTExtrasPoseId id)
        {
            PoseDefinition definition = Get(id);
            if (!definition.IsDerived) return profile.GetStoredPose(id);

            FTExtrasPoseData source = profile.GetStoredPose(definition.DerivedFrom.Value);
            if (source == null) return null;

            var derived = new FTExtrasPoseData { id = id };
            if (definition.Group == PoseGroup.Ears)
            {
                // Swap sides: the left ear of Look Left is the mirrored right ear of Look Right.
                derived.earLeft = MirrorChain(source.earRight, profile.earRight, profile.earLeft);
                derived.earRight = MirrorChain(source.earLeft, profile.earLeft, profile.earRight);
                if (derived.earLeft == null || derived.earRight == null) return null;
            }
            else
            {
                derived.tail = MirrorChain(source.tail, profile.tail, profile.tail);
                if (derived.tail == null) return null;
            }

            return derived;
        }

        /// <summary>
        /// Rest local rotations for every bone, as a pose.
        /// </summary>
        internal static FTExtrasPoseData RestPose(FTExtrasProfile profile)
        {
            return new FTExtrasPoseData
            {
                earLeft = profile.earLeft.Select(b => b.restLocal).ToList(),
                earRight = profile.earRight.Select(b => b.restLocal).ToList(),
                tail = profile.tail.Select(b => b.restLocal).ToList(),
            };
        }

        // =====================================================================
        // Blending
        // =====================================================================

        /// <summary>
        /// Face tracking inputs that drive the ears and tail.
        /// </summary>
        internal struct BlendInputs
        {
            /// <summary>-1 = looking left, +1 = looking right.</summary>
            public float GazeX;
            /// <summary>-1 = looking down, +1 = looking up.</summary>
            public float GazeY;
            /// <summary>-1 = sad, +1 = happy.</summary>
            public float Mood;
            /// <summary>-1 = jaw left, +1 = jaw right.</summary>
            public float JawX;

            /// <summary>When true, the happy ear flick and tail wag animate using <see cref="Time"/>.</summary>
            public bool PlayLoops;
            /// <summary>Seconds into the loops.</summary>
            public float Time;
        }

        /// <summary>
        /// Blends the poses for the given inputs. Each pose contributes its rotation away from rest,
        /// scaled by its weight; mood is applied first and gaze on top, so looking around still works
        /// while the ears are drooped or perked. Happiness also plays the ear flick and tail wag loops.
        /// Missing poses contribute nothing.
        /// </summary>
        internal static FTExtrasPoseData Blend(FTExtrasProfile profile, BlendInputs inputs)
        {
            FTExtrasPoseData mood = inputs.Mood < 0f
                ? Resolve(profile, FTExtrasPoseId.EarSad)
                : HappyEars(profile, inputs.PlayLoops, inputs.Time);
            FTExtrasPoseData horizontal = Resolve(profile, inputs.GazeX < 0f ? FTExtrasPoseId.EarLookLeft : FTExtrasPoseId.EarLookRight);
            FTExtrasPoseData vertical = Resolve(profile, inputs.GazeY < 0f ? FTExtrasPoseId.EarLookDown : FTExtrasPoseId.EarLookUp);
            FTExtrasPoseData tailHappy = inputs.Mood > 0f ? Resolve(profile, FTExtrasPoseId.TailHappy) : null;
            FTExtrasPoseData tailWag = inputs.Mood > 0f && inputs.PlayLoops ? TailWag(profile, inputs.Time) : null;
            FTExtrasPoseData tailSide = Resolve(profile, inputs.JawX < 0f ? FTExtrasPoseId.TailLeft : FTExtrasPoseId.TailRight);

            float moodWeight = Mathf.Abs(inputs.Mood);
            float xWeight = Mathf.Abs(inputs.GazeX);
            float yWeight = Mathf.Abs(inputs.GazeY);
            float jawWeight = Mathf.Abs(inputs.JawX);

            return new FTExtrasPoseData
            {
                earLeft = BlendChain(profile.earLeft, p => p.earLeft,
                    (mood, moodWeight), (horizontal, xWeight), (vertical, yWeight)),
                earRight = BlendChain(profile.earRight, p => p.earRight,
                    (mood, moodWeight), (horizontal, xWeight), (vertical, yWeight)),
                tail = BlendChain(profile.tail, p => p.tail,
                    (tailHappy, Mathf.Max(0f, inputs.Mood)), (tailWag, Mathf.Max(0f, inputs.Mood)), (tailSide, jawWeight)),
            };
        }

        // =====================================================================
        // Loops
        // =====================================================================

        /// <summary>
        /// Happy ears at a point in the flick loop: Happy → Happy Flick → Happy, eased in and out at
        /// both ends (like a clip with flat tangents). Without Happy Flick, or when not playing, this is
        /// the Happy pose. Null if Happy is not set.
        /// </summary>
        internal static FTExtrasPoseData HappyEars(FTExtrasProfile profile, bool playLoops, float time)
        {
            FTExtrasPoseData happy = Resolve(profile, FTExtrasPoseId.EarHappy);
            if (happy == null) return null;

            FTExtrasPoseData flick = Resolve(profile, FTExtrasPoseId.EarHappyFlick);
            if (!playLoops || flick == null || profile.earFlickPeriod <= 0f) return happy;

            float phase = Mathf.Repeat(time / profile.earFlickPeriod, 1f);
            float t = (1f - Mathf.Cos(phase * 2f * Mathf.PI)) * 0.5f;

            return new FTExtrasPoseData
            {
                id = FTExtrasPoseId.EarHappy,
                earLeft = SlerpList(happy.earLeft, flick.earLeft, t),
                earRight = SlerpList(happy.earRight, flick.earRight, t),
            };
        }

        /// <summary>
        /// The tail at a point in the wag: each bone swings between Tail Left and Tail Right on a sine
        /// wave, with bones further down the tail lagging behind so the wag ripples along it.
        /// Null if Tail Right is not set.
        /// </summary>
        internal static FTExtrasPoseData TailWag(FTExtrasProfile profile, float time)
        {
            FTExtrasPoseData right = Resolve(profile, FTExtrasPoseId.TailRight);
            FTExtrasPoseData left = Resolve(profile, FTExtrasPoseId.TailLeft);
            if (right == null || left == null || profile.tailWagPeriod <= 0f) return null;

            var tail = new List<Quaternion>(profile.tail.Count);
            for (int i = 0; i < profile.tail.Count; i++)
            {
                float phase = time / profile.tailWagPeriod - i * profile.tailWagDelay;
                float swing = Mathf.Sin(phase * 2f * Mathf.PI) * profile.tailWagAmount;
                Quaternion edge = swing >= 0f ? right.tail[i] : left.tail[i];
                tail.Add(Quaternion.Slerp(profile.tail[i].restLocal, edge, Mathf.Abs(swing)));
            }

            return new FTExtrasPoseData { id = FTExtrasPoseId.TailRight, tail = tail };
        }

        private static List<Quaternion> SlerpList(List<Quaternion> from, List<Quaternion> to, float t)
        {
            return from.Select((q, i) => Quaternion.Slerp(q, to[i], t)).ToList();
        }

        private static List<Quaternion> BlendChain(
            List<FTExtrasProfileBone> bones,
            System.Func<FTExtrasPoseData, List<Quaternion>> select,
            params (FTExtrasPoseData Pose, float Weight)[] layers)
        {
            var result = new List<Quaternion>(bones.Count);
            for (int i = 0; i < bones.Count; i++)
            {
                Quaternion rotation = bones[i].restLocal;
                foreach (var layer in layers)
                {
                    if (layer.Pose == null || layer.Weight <= 0f) continue;

                    List<Quaternion> locals = select(layer.Pose);
                    if (locals == null || locals.Count != bones.Count) continue;

                    Quaternion delta = Quaternion.Inverse(bones[i].restLocal) * locals[i];
                    rotation *= Quaternion.Slerp(Quaternion.identity, delta, layer.Weight);
                }
                result.Add(rotation);
            }
            return result;
        }
    }
}
