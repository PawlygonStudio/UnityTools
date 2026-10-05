using System.IO;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Pawlygon.UnityTools.Editor
{
    /// <summary>
    /// Resolves where this package lives, for every install type. Embedded packages sit in the
    /// project's <c>Packages/</c> folder, but git-URL, registry and tarball installs are extracted
    /// to <c>Library/PackageCache/…</c>, so a hardcoded <c>Packages/net.pawlygon.unitytools</c>
    /// file system path only works for embedded installs. AssetDatabase paths always use the
    /// virtual <c>Packages/&lt;name&gt;</c> root and are unaffected.
    /// </summary>
    internal static class PawlygonPackagePaths
    {
        /// <summary>The package name from package.json.</summary>
        public const string PackageName = "net.pawlygon.unitytools";

        /// <summary>Used when the Package Manager cannot identify the package (e.g. the code was copied into Assets).</summary>
        private const string FallbackRootPath = "Packages/" + PackageName;

        // Static fields are reset by every domain reload, which Unity performs whenever the
        // package is installed, moved or updated, so the cached lookup never goes stale.
        private static PackageInfo cachedPackageInfo;
        private static bool packageInfoLookedUp;

        /// <summary>
        /// Returns the Package Manager info for this package, or null when this code is not
        /// running from a package.
        /// </summary>
        public static PackageInfo GetPackageInfo()
        {
            if (packageInfoLookedUp) return cachedPackageInfo;

            packageInfoLookedUp = true;
            cachedPackageInfo = PackageInfo.FindForAssembly(typeof(PawlygonPackagePaths).Assembly);
            return cachedPackageInfo;
        }

        /// <summary>
        /// Absolute file system path of the package root (e.g. <c>…/Library/PackageCache/net.pawlygon.unitytools@1.7.0</c>
        /// for a git install). Falls back to <c>&lt;project&gt;/Packages/net.pawlygon.unitytools</c>.
        /// </summary>
        public static string GetPackageRootFullPath()
        {
            PackageInfo packageInfo = GetPackageInfo();
            string resolvedPath = packageInfo != null ? packageInfo.resolvedPath : null;
            return Path.GetFullPath(string.IsNullOrEmpty(resolvedPath) ? FallbackRootPath : resolvedPath);
        }

        /// <summary>
        /// AssetDatabase path of the package root (normally <c>Packages/net.pawlygon.unitytools</c>).
        /// </summary>
        public static string GetPackageRootAssetPath()
        {
            PackageInfo packageInfo = GetPackageInfo();
            string assetPath = packageInfo != null ? packageInfo.assetPath : null;
            return string.IsNullOrEmpty(assetPath) ? FallbackRootPath : assetPath;
        }

        /// <summary>
        /// Absolute file system path of a file or folder inside the package, e.g.
        /// <c>GetFullPath("hdiff", "hdiffz", "Windows", "hdiffz.exe")</c>.
        /// </summary>
        public static string GetFullPath(params string[] relativeSegments)
        {
            string path = GetPackageRootFullPath();
            foreach (string segment in relativeSegments)
            {
                path = Path.Combine(path, segment);
            }

            return Path.GetFullPath(path);
        }
    }
}
