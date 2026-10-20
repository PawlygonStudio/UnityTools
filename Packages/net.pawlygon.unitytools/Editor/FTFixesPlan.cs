using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// The fix shapes Blendshape Fixes drives on an avatar, each with the shapes it fixes and how Pawlygon VRCFT 1.x
    /// drives them. Resolved the same way for the window and the generator.
    /// </summary>
    internal sealed class FTFixesPlan
    {
        internal sealed class Entry
        {
            public string Blendshape;
            public List<FTFixesSources.Source> Sources = new List<FTFixesSources.Source>();
            /// <summary>Paths, from the avatar root, of every mesh with the fix shape.</summary>
            public List<string> MeshPaths = new List<string>();
            public bool IsManual;
            public bool Excluded;
            public string Problem;

            public bool Generated => Problem == null && !Excluded;
        }

        public readonly List<Entry> Entries = new List<Entry>();

        public IEnumerable<Entry> Generated => Entries.Where(e => e.Generated);

        public static FTFixesPlan Resolve(FTFixesProfile profile, GameObject avatar)
        {
            var plan = new FTFixesPlan();
            if (profile == null || avatar == null) return plan;

            // Blendshape name -> paths of the meshes that have it
            var meshesByShape = new Dictionary<string, List<string>>();
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, avatar.transform);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (!meshesByShape.TryGetValue(name, out var paths)) meshesByShape[name] = paths = new List<string>();
                    if (!paths.Contains(path)) paths.Add(path);
                }
            }

            if (profile.autoDetect)
            {
                foreach (var name in meshesByShape.Keys.Where(n => n.Contains('-') && FTFixesSources.Find(n) == null).OrderBy(n => n))
                {
                    var sources = name.Split('-').Select(p => FTFixesSources.Find(p)).ToList();
                    if (sources.Count < 2 || sources.Any(s => s == null)) continue;
                    plan.Entries.Add(new Entry
                    {
                        Blendshape = name,
                        Sources = sources,
                        MeshPaths = meshesByShape[name],
                        Excluded = profile.excludedFixes.Contains(name),
                    });
                }
            }

            foreach (var fix in profile.fixes.Where(f => f != null && !string.IsNullOrWhiteSpace(f.blendshape)))
            {
                string name = fix.blendshape.Trim();
                plan.Entries.RemoveAll(e => e.Blendshape == name);

                var entry = new Entry { Blendshape = name, IsManual = true };
                var parts = (fix.sources ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                var unknown = parts.Where(p => FTFixesSources.Find(p) == null).ToList();
                entry.Sources = parts.Select(FTFixesSources.Find).Where(s => s != null).ToList();

                if (!meshesByShape.TryGetValue(name, out var paths)) entry.Problem = "No mesh on the avatar has this blendshape.";
                else entry.MeshPaths = paths;

                if (entry.Problem == null && parts.Count == 0) entry.Problem = "Add the blendshapes it fixes.";
                if (entry.Problem == null && unknown.Count > 0)
                {
                    entry.Problem = $"Pawlygon VRCFT 1.x doesn't drive {string.Join(", ", unknown.Select(u => $"'{u}'"))}.";
                }
                plan.Entries.Add(entry);
            }

            plan.Entries.Sort((a, b) => string.CompareOrdinal(a.Blendshape, b.Blendshape));
            return plan;
        }

        /// <summary>Everything the generated animator depends on, to tell when it's out of date.</summary>
        public static string Signature(FTFixesProfile profile, FTFixesPlan plan)
        {
            var fixes = plan.Generated.Select(e =>
                $"{e.Blendshape}<{string.Join("+", e.Sources.Select(s => s.Shape))}@{string.Join(",", e.MeshPaths)}");
            string text = $"{profile.combine}|{profile.parameterPrefix}|{profile.eyeTrackingActive}|{profile.lipTrackingActive}|{string.Join(";", fixes)}";
            return Hash128.Compute(text).ToString();
        }
    }
}
