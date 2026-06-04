using System;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// A curated catalog of common VRChat avatar packages used by the Patch Config Package Rules
    /// editor. It is not a separate picker: the editor always lists the packages actually installed
    /// in the project, and uses this catalog only to (a) sort these common packages to the top of
    /// that list and (b) enrich an authored rule with the package's VCC install URL.
    ///
    /// Each entry pairs a human-readable name with the exact UPM/VPM package id (PatcherHub matches
    /// this literally against installed package names) and the URL a user opens to add the package's
    /// repository to the VRChat Creator Companion. Ids/URLs were verified against each project's live
    /// VPM listing JSON or package.json; update here if a project changes them. Array order defines
    /// the sort priority (earlier = higher).
    /// </summary>
    internal static class CommonVrcPackages
    {
        internal sealed class Entry
        {
            public readonly string DisplayName;
            public readonly string PackageName;
            public readonly string VccUrl;

            public Entry(string displayName, string packageName, string vccUrl)
            {
                DisplayName = displayName;
                PackageName = packageName;
                VccUrl = vccUrl;
            }
        }

        /// <summary>Common avatar dependencies an end user may need installed, ordered by priority.</summary>
        internal static readonly Entry[] All =
        {
            new Entry("Poiyomi Toon Shader", "com.poiyomi.toon", "https://poiyomi.github.io/vpm/index.json"),
            new Entry("lilToon", "jp.lilxyzw.liltoon", "https://lilxyzw.github.io/vpm-repos/vpm.json"),
            new Entry("Modular Avatar", "nadena.dev.modular-avatar", "https://vpm.nadena.dev/vpm.json"),
            new Entry("GoGo Loco", "gogoloco", "https://spokeek.github.io/goloco/index.json"),
        };

        /// <summary>
        /// Packages that PatcherHub already validates at the global PackageRules level, so adding a
        /// per-config rule for them is usually redundant. The editor surfaces a note when one is chosen.
        /// </summary>
        internal static readonly string[] GloballyHandled =
        {
            "com.vrchat.avatars",
            "com.vrchat.base",
            "com.vrcfury.vrcfury",
        };

        /// <summary>Whether the given package id is already covered by the global PackageRules.</summary>
        internal static bool IsGloballyHandled(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return false;

            foreach (string id in GloballyHandled)
            {
                if (string.Equals(id, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the catalog sort priority for a package id (lower sorts first), or
        /// <see cref="int.MaxValue"/> when the package is not in the curated catalog.
        /// </summary>
        internal static int GetPriority(string packageName)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].PackageName, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        /// <summary>Returns the curated VCC install URL for a package id, or null if not catalogued.</summary>
        internal static string GetVccUrl(string packageName)
        {
            foreach (Entry entry in All)
            {
                if (string.Equals(entry.PackageName, packageName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.VccUrl;
                }
            }

            return null;
        }
    }
}
