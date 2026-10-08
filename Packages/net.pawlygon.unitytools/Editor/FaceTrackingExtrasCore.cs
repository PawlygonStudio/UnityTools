using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Animations;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Core logic for the Face Tracking Extras: finds ear and tail bone chains on an avatar,
    /// reports which bones are driven by PhysBones or constraints, and exports the rig data
    /// to JSON for inspection.
    /// UI lives in <see cref="FaceTrackingExtras"/>.
    /// </summary>
    internal static class FaceTrackingExtrasCore
    {
        internal const string LogPrefix = "[Pawlygon Face Tracking Extras]";
        internal const string DebugFolderName = "PawlygonDebug";

        // "ear" not preceded by a letter (avoids Year/Hear/Beard), or a CamelCase "Ear" after a lowercase letter
        // or digit (LeftEar, Head_LeftEar), never followed by "ring".
        private static readonly Regex EarNamePattern = new Regex(@"(?i:(^|[^a-z])ear(?!ring))|(?<=[a-z0-9])Ear(?!ring)|(?i:mimi)|耳");
        private static readonly Regex TailNamePattern = new Regex(@"(?<!pony)tail|shippo|尻尾|しっぽ", RegexOptions.IgnoreCase);

        /// <summary>Helper bones that rigs use to drive the visible chain (e.g. Tail_Dummy_*, Tail_Constraint_*).</summary>
        private static readonly Regex HelperNamePattern = new Regex(@"dummy|helper|constraint|target|(^|[^a-zA-Z])ik([^a-zA-Z]|$)", RegexOptions.IgnoreCase);

        /// <summary>Distance from the head's centre line (in avatar space) below which a bone is treated as centred.</summary>
        private const float CentreThreshold = 0.002f;

        internal enum ChainKind { Ear, Tail }
        internal enum ChainSide { Left, Right, Centre }

        // =====================================================================
        // Data types
        // =====================================================================

        /// <summary>
        /// A single bone chain (e.g. Ear_01_L → Ear_02_L → Ear_03_L).
        /// </summary>
        internal class BoneChain
        {
            public ChainKind Kind;
            public ChainSide Side;
            public List<Transform> Bones = new List<Transform>();
            public bool Branches;

            /// <summary>Leading bones dropped because a constraint drives them (animating them would be overridden).</summary>
            public List<Transform> SkippedBones = new List<Transform>();

            /// <summary>True when the chain looks like a helper rig rather than the visible chain.</summary>
            public bool IsHelper => Bones.Count > 0 && Bones.All(b => HelperNamePattern.IsMatch(b.name));

            public Transform Root => Bones.Count > 0 ? Bones[0] : null;
            public string DisplayName => Root != null ? $"{Root.name} ({Bones.Count} bones)" : "(none)";
        }

        /// <summary>
        /// Result of analysing an avatar's rig.
        /// </summary>
        internal class RigAnalysis
        {
            public bool Success;
            public string StatusMessage;

            public GameObject Avatar;
            public Animator Animator;
            public Transform Head;
            public Transform Hips;

            public List<BoneChain> EarChains = new List<BoneChain>();
            public List<BoneChain> TailChains = new List<BoneChain>();

            /// <summary>Bone → PhysBone components whose chain includes it.</summary>
            public Dictionary<Transform, List<Component>> PhysBoneDrivers = new Dictionary<Transform, List<Component>>();

            /// <summary>Bone → constraint components that drive it.</summary>
            public Dictionary<Transform, List<Component>> ConstraintDrivers = new Dictionary<Transform, List<Component>>();

            /// <summary>PhysBone component → the root transform its chain starts at.</summary>
            public Dictionary<Component, Transform> PhysBoneRoots = new Dictionary<Component, Transform>();

            public IEnumerable<BoneChain> ChainsOfSide(ChainKind kind, ChainSide side)
            {
                return (kind == ChainKind.Ear ? EarChains : TailChains).Where(c => c.Side == side);
            }
        }

        // =====================================================================
        // Analysis
        // =====================================================================

        /// <summary>
        /// Analyses the avatar's humanoid rig and finds candidate ear and tail chains.
        /// </summary>
        internal static RigAnalysis Analyze(GameObject avatar)
        {
            var result = new RigAnalysis { Avatar = avatar };

            if (avatar == null)
            {
                result.StatusMessage = "Select an avatar in the scene.";
                return result;
            }

            result.Animator = avatar.GetComponent<Animator>();
            if (result.Animator == null || !result.Animator.isHuman)
            {
                result.StatusMessage = $"'{avatar.name}' has no humanoid Animator.";
                return result;
            }

            result.Head = result.Animator.GetBoneTransform(HumanBodyBones.Head);
            result.Hips = result.Animator.GetBoneTransform(HumanBodyBones.Hips);
            if (result.Head == null || result.Hips == null)
            {
                result.StatusMessage = "The humanoid rig is missing a Head or Hips bone.";
                return result;
            }

            CollectDrivers(avatar, result);

            // Ears: anywhere under the head.
            result.EarChains = FindChains(result.Head, ChainKind.Ear, EarNamePattern, result, excluded: null);

            // Tails: under the hips, but not inside the spine or leg branches (keeps ponytails and
            // other hair out).
            var excluded = new HashSet<Transform>();
            foreach (HumanBodyBones bone in new[] { HumanBodyBones.Spine, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg })
            {
                Transform t = result.Animator.GetBoneTransform(bone);
                if (t != null) excluded.Add(t);
            }
            result.TailChains = FindChains(result.Hips, ChainKind.Tail, TailNamePattern, result, excluded);

            result.Success = true;
            result.StatusMessage = $"Found {result.EarChains.Count} ear chain(s) and {result.TailChains.Count} tail chain(s).";
            return result;
        }

        /// <summary>
        /// Builds a chain starting at <paramref name="root"/>, used when the user picks a root bone manually.
        /// </summary>
        internal static BoneChain BuildChainFromRoot(Transform root, ChainKind kind, RigAnalysis analysis)
        {
            Regex pattern = kind == ChainKind.Ear ? EarNamePattern : TailNamePattern;
            return BuildChain(root, kind, pattern, analysis);
        }

        private static List<BoneChain> FindChains(Transform searchRoot, ChainKind kind, Regex pattern, RigAnalysis analysis, HashSet<Transform> excluded)
        {
            var candidates = new HashSet<Transform>();
            CollectCandidates(searchRoot, pattern, excluded, candidates);

            // Chain roots are candidates whose parent is not a candidate.
            var roots = candidates.Where(t => !candidates.Contains(t.parent)).ToList();

            var chains = new List<BoneChain>();
            foreach (Transform root in roots)
            {
                ExpandRoot(root, kind, pattern, candidates, analysis, chains);
            }

            return chains;
        }

        /// <summary>
        /// A candidate with two or more candidate children (e.g. an "Ear" bone holding both
        /// Ear_01_L and Ear_01_R) is a grouping node, not a chain; recurse into its children.
        /// </summary>
        private static void ExpandRoot(Transform root, ChainKind kind, Regex pattern, HashSet<Transform> candidates, RigAnalysis analysis, List<BoneChain> chains)
        {
            var candidateChildren = Children(root).Where(candidates.Contains).ToList();
            if (candidateChildren.Count >= 2)
            {
                foreach (Transform child in candidateChildren)
                {
                    ExpandRoot(child, kind, pattern, candidates, analysis, chains);
                }
                return;
            }

            chains.Add(BuildChain(root, kind, pattern, analysis));
        }

        private static BoneChain BuildChain(Transform root, ChainKind kind, Regex pattern, RigAnalysis analysis)
        {
            var chain = new BoneChain { Kind = kind };
            Transform current = root;

            while (current != null)
            {
                chain.Bones.Add(current);

                var children = Children(current).ToList();
                var matching = children.Where(c => pattern.IsMatch(c.name)).ToList();
                if (matching.Count == 0 && children.Count == 1)
                {
                    // Unnamed single child (common for end bones) still belongs to the chain.
                    matching = children;
                }

                if (matching.Count > 1)
                {
                    chain.Branches = true;
                    current = matching.OrderByDescending(c => c.GetComponentsInChildren<Transform>(true).Length).First();
                }
                else
                {
                    current = matching.FirstOrDefault();
                }
            }

            TrimConstrainedBones(chain, analysis);
            chain.Side = DetectSide(root, analysis);
            return chain;
        }

        /// <summary>
        /// Drops the leading bones up to the last constraint-driven one, so animation targets bones the
        /// constraints do not override. When a PhysBone starts further down (e.g. Tail_01 below
        /// Tail_Constraint_Aim_Joint), the chain starts there instead, skipping the helper joints.
        /// </summary>
        private static void TrimConstrainedBones(BoneChain chain, RigAnalysis analysis)
        {
            int lastConstrained = chain.Bones.FindLastIndex(b => analysis.ConstraintDrivers.ContainsKey(b));
            if (lastConstrained < 0 || lastConstrained == chain.Bones.Count - 1) return;

            int start = lastConstrained + 1;
            int physBoneRoot = chain.Bones.FindIndex(start, b => analysis.PhysBoneRoots.ContainsValue(b));
            if (physBoneRoot >= 0) start = physBoneRoot;

            chain.SkippedBones = chain.Bones.Take(start).ToList();
            chain.Bones.RemoveRange(0, start);
        }

        private static void CollectCandidates(Transform t, Regex pattern, HashSet<Transform> excluded, HashSet<Transform> candidates)
        {
            foreach (Transform child in Children(t))
            {
                if (excluded != null && excluded.Contains(child)) continue;
                if (pattern.IsMatch(child.name)) candidates.Add(child);
                CollectCandidates(child, pattern, excluded, candidates);
            }
        }

        private static IEnumerable<Transform> Children(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                yield return t.GetChild(i);
            }
        }

        /// <summary>
        /// Side is decided by position relative to the head in avatar space, which works no matter
        /// how bones are named. The avatar's right is +X.
        /// </summary>
        private static ChainSide DetectSide(Transform bone, RigAnalysis analysis)
        {
            Transform avatarRoot = analysis.Avatar.transform;
            float boneX = avatarRoot.InverseTransformPoint(bone.position).x;
            float centreX = avatarRoot.InverseTransformPoint(analysis.Head.position).x;
            float offset = boneX - centreX;

            if (Mathf.Abs(offset) < CentreThreshold) return ChainSide.Centre;
            return offset < 0f ? ChainSide.Left : ChainSide.Right;
        }

        // =====================================================================
        // PhysBone and constraint detection (via reflection, no SDK reference)
        // =====================================================================

        private static void CollectDrivers(GameObject avatar, RigAnalysis result)
        {
            foreach (Component component in avatar.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                Type type = component.GetType();

                if (type.Name == "VRCPhysBone")
                {
                    Transform root = GetMember<Transform>(component, "rootTransform") ?? component.transform;
                    var ignored = new HashSet<Transform>();
                    if (GetMember<object>(component, "ignoreTransforms") is IEnumerable ignoreList)
                    {
                        foreach (object item in ignoreList)
                        {
                            if (item is Transform ignoredTransform && ignoredTransform != null) ignored.Add(ignoredTransform);
                        }
                    }

                    result.PhysBoneRoots[component] = root;
                    AddPhysBoneCoverage(root, ignored, component, result.PhysBoneDrivers);
                }
                else if (component is IConstraint)
                {
                    AddDriver(result.ConstraintDrivers, component.transform, component);
                }
                else if (IsVrcConstraint(type))
                {
                    Transform target = GetMember<Transform>(component, "TargetTransform") ?? component.transform;
                    AddDriver(result.ConstraintDrivers, target, component);
                }
            }
        }

        private static void AddPhysBoneCoverage(Transform t, HashSet<Transform> ignored, Component physBone, Dictionary<Transform, List<Component>> drivers)
        {
            if (ignored.Contains(t)) return;
            AddDriver(drivers, t, physBone);
            foreach (Transform child in Children(t))
            {
                AddPhysBoneCoverage(child, ignored, physBone, drivers);
            }
        }

        private static bool IsVrcConstraint(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.Name == "VRCConstraintBase") return true;
            }
            return false;
        }

        private static void AddDriver(Dictionary<Transform, List<Component>> drivers, Transform bone, Component component)
        {
            if (!drivers.TryGetValue(bone, out var list))
            {
                list = new List<Component>();
                drivers[bone] = list;
            }
            list.Add(component);
        }

        private static T GetMember<T>(object target, string name) where T : class
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = target.GetType();

            FieldInfo field = type.GetField(name, flags);
            if (field != null) return AsLiveValue<T>(field.GetValue(target));

            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0) return AsLiveValue<T>(property.GetValue(target));

            return null;
        }

        /// <summary>
        /// An unassigned or destroyed Unity object field (e.g. a PhysBone with no Root Transform) reads back as a
        /// non-null placeholder that throws on use; return real null so callers' fallbacks apply.
        /// </summary>
        private static T AsLiveValue<T>(object value) where T : class
        {
            if (value is UnityEngine.Object unityObject && unityObject == null) return null;
            return value as T;
        }

        // =====================================================================
        // Paths
        // =====================================================================

        /// <summary>
        /// Returns the transform path relative to <paramref name="root"/>, as used by AnimationClip bindings.
        /// </summary>
        internal static string GetRelativePath(Transform t, Transform root)
        {
            if (t == null) return null;
            if (t == root) return "";

            var parts = new List<string>();
            for (Transform current = t; current != null && current != root; current = current.parent)
            {
                parts.Add(current.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // =====================================================================
        // JSON export
        // =====================================================================

        [Serializable]
        private class RigDump
        {
            public string avatar;
            public string exportedAt;
            public string unityVersion;
            public BoneDump head;
            public BoneDump hips;
            public List<ChainDump> earChains = new List<ChainDump>();
            public List<ChainDump> tailChains = new List<ChainDump>();
            public List<ChainDump> selected = new List<ChainDump>();
            public List<string> headTree = new List<string>();
            public List<string> hipsTree = new List<string>();
            public List<DriverDump> physBones = new List<DriverDump>();
            public List<DriverDump> constraints = new List<DriverDump>();
        }

        [Serializable]
        private class ChainDump
        {
            public string slot;
            public string kind;
            public string side;
            public bool branches;
            public List<BoneDump> bones = new List<BoneDump>();
        }

        /// <summary>
        /// Per-bone data. All "Avatar" vectors are expressed in the avatar root's space
        /// (+X right, +Y up, +Z forward), so they can be compared across rigs.
        /// </summary>
        [Serializable]
        private class BoneDump
        {
            public string path;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localEuler;
            public Vector3 localScale;
            public Vector3 positionAvatar;
            public Quaternion rotationAvatar;
            public Vector3 axisXAvatar;
            public Vector3 axisYAvatar;
            public Vector3 axisZAvatar;
            public Vector3 toChildAvatar;
            public float length;
            public List<string> physBones = new List<string>();
            public List<string> constraints = new List<string>();
        }

        [Serializable]
        private class DriverDump
        {
            public string component;
            public string onObject;
            public string drives;
        }

        /// <summary>
        /// Writes the analysis to &lt;project&gt;/PawlygonDebug/FTExtrasRig_&lt;avatar&gt;.json and returns the full path.
        /// <paramref name="selectedChains"/> maps slot names ("EarLeft", "EarRight", "Tail") to the chosen chains.
        /// </summary>
        internal static string ExportJson(RigAnalysis analysis, IDictionary<string, BoneChain> selectedChains)
        {
            Transform avatarRoot = analysis.Avatar.transform;

            var dump = new RigDump
            {
                avatar = analysis.Avatar.name,
                exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                unityVersion = Application.unityVersion,
                head = DumpBone(analysis.Head, null, analysis),
                hips = DumpBone(analysis.Hips, null, analysis),
            };

            foreach (BoneChain chain in analysis.EarChains) dump.earChains.Add(DumpChain(chain, null, analysis));
            foreach (BoneChain chain in analysis.TailChains) dump.tailChains.Add(DumpChain(chain, null, analysis));
            foreach (var pair in selectedChains)
            {
                if (pair.Value != null) dump.selected.Add(DumpChain(pair.Value, pair.Key, analysis));
            }

            AppendTree(analysis.Head, avatarRoot, 0, dump.headTree, null);
            var excluded = new HashSet<Transform>
            {
                analysis.Animator.GetBoneTransform(HumanBodyBones.Spine),
                analysis.Animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg),
                analysis.Animator.GetBoneTransform(HumanBodyBones.RightUpperLeg),
            };
            AppendTree(analysis.Hips, avatarRoot, 0, dump.hipsTree, excluded);

            foreach (var pair in analysis.PhysBoneRoots)
            {
                dump.physBones.Add(new DriverDump
                {
                    component = pair.Key.GetType().Name,
                    onObject = GetRelativePath(pair.Key.transform, avatarRoot),
                    drives = GetRelativePath(pair.Value, avatarRoot),
                });
            }
            AppendDrivers(analysis.ConstraintDrivers, avatarRoot, dump.constraints);

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string folder = Path.Combine(projectRoot, DebugFolderName);
            Directory.CreateDirectory(folder);

            string safeName = string.Concat(analysis.Avatar.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string filePath = Path.Combine(folder, $"FTExtrasRig_{safeName}.json");
            File.WriteAllText(filePath, JsonUtility.ToJson(dump, true));
            return filePath;
        }

        private static ChainDump DumpChain(BoneChain chain, string slot, RigAnalysis analysis)
        {
            var dump = new ChainDump
            {
                slot = slot,
                kind = chain.Kind.ToString(),
                side = chain.Side.ToString(),
                branches = chain.Branches,
            };

            for (int i = 0; i < chain.Bones.Count; i++)
            {
                Transform next = i + 1 < chain.Bones.Count ? chain.Bones[i + 1] : null;
                dump.bones.Add(DumpBone(chain.Bones[i], next, analysis));
            }

            return dump;
        }

        private static BoneDump DumpBone(Transform bone, Transform next, RigAnalysis analysis)
        {
            Transform avatarRoot = analysis.Avatar.transform;
            Quaternion rootInverse = Quaternion.Inverse(avatarRoot.rotation);

            var dump = new BoneDump
            {
                path = GetRelativePath(bone, avatarRoot),
                localPosition = bone.localPosition,
                localRotation = bone.localRotation,
                localEuler = bone.localEulerAngles,
                localScale = bone.localScale,
                positionAvatar = avatarRoot.InverseTransformPoint(bone.position),
                rotationAvatar = rootInverse * bone.rotation,
                axisXAvatar = rootInverse * bone.right,
                axisYAvatar = rootInverse * bone.up,
                axisZAvatar = rootInverse * bone.forward,
            };

            if (next != null)
            {
                Vector3 delta = next.position - bone.position;
                dump.length = delta.magnitude;
                dump.toChildAvatar = rootInverse * delta.normalized;
            }

            if (analysis.PhysBoneDrivers.TryGetValue(bone, out var physBones))
            {
                dump.physBones = physBones.Select(c => GetRelativePath(c.transform, avatarRoot)).ToList();
            }

            if (analysis.ConstraintDrivers.TryGetValue(bone, out var constraints))
            {
                dump.constraints = constraints.Select(c => $"{c.GetType().Name} on {GetRelativePath(c.transform, avatarRoot)}").ToList();
            }

            return dump;
        }

        private static void AppendTree(Transform t, Transform avatarRoot, int depth, List<string> lines, HashSet<Transform> excluded)
        {
            string components = string.Join(", ", t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name));
            lines.Add(new string(' ', depth * 2) + t.name + (components.Length > 0 ? $"  [{components}]" : ""));

            foreach (Transform child in Children(t))
            {
                if (excluded != null && excluded.Contains(child)) continue;
                AppendTree(child, avatarRoot, depth + 1, lines, excluded);
            }
        }

        private static void AppendDrivers(Dictionary<Transform, List<Component>> drivers, Transform avatarRoot, List<DriverDump> output)
        {
            foreach (var pair in drivers)
            {
                foreach (Component component in pair.Value)
                {
                    output.Add(new DriverDump
                    {
                        component = component.GetType().Name,
                        onObject = GetRelativePath(component.transform, avatarRoot),
                        drives = GetRelativePath(pair.Key, avatarRoot),
                    });
                }
            }
        }
    }
}
