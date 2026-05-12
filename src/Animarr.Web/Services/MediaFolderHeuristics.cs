namespace Animarr.Web.Services;

/// <summary>
/// Heuristics for deciding whether a subdirectory inside a section root
/// looks like a real media folder vs an empty/junk directory the user
/// (or some OS-level tool) accidentally created.
/// </summary>
public static class MediaFolderHeuristics
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm",
        ".ts", ".m2ts", ".mts", ".vob", ".mpg", ".mpeg", ".ogv",
    };

    /// <summary>
    /// Names that file managers tend to produce as scaffolding when a user
    /// accidentally right-clicks → New Folder. We never auto-register a
    /// directory with one of these names unless it already contains media.
    /// </summary>
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "New Folder",
        "New folder",
        "Новая папка",
        "Untitled Folder",
        "Untitled folder",
        ".animarr",        // our own metadata cache
        ".trash-1000",
        "$RECYCLE.BIN",
        "System Volume Information",
        ".DS_Store",
        "@eaDir",          // Synology / NAS thumbnails
    };

    /// <summary>
    /// Returns true if <paramref name="dirPath"/> looks like a real media
    /// folder worth auto-registering as a section child. The directory must
    /// not be a junk-named scaffold and must contain at least one video file
    /// (recursively, up to <paramref name="maxDepth"/>) — empty directories
    /// cannot be identified anyway and produce a never-ending re-registration
    /// loop when the user deletes them from the catalog.
    /// </summary>
    public static bool LooksLikeMediaFolder(string dirPath, int maxDepth = 3)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return false;

            var name = Path.GetFileName(dirPath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (name.StartsWith('.') || JunkNames.Contains(name))
                return false;

            // Has a video file anywhere within maxDepth levels?
            return ContainsVideoFile(dirPath, maxDepth);
        }
        catch
        {
            // If we can't read the dir (permissions, race), don't auto-register.
            return false;
        }
    }

    private static bool ContainsVideoFile(string dirPath, int maxDepth)
    {
        if (maxDepth < 0) return false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath))
            {
                if (VideoExtensions.Contains(Path.GetExtension(file)))
                    return true;
            }

            foreach (var sub in Directory.EnumerateDirectories(dirPath))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || JunkNames.Contains(name)) continue;
                if (ContainsVideoFile(sub, maxDepth - 1))
                    return true;
            }
        }
        catch
        {
            // Permission error inside — be conservative and say "no media here".
        }
        return false;
    }
}
