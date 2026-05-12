namespace Animarr.Web.Services;

/// <summary>
/// Resolves the on-disk location of Animarr's image cache (posters, fanart, logos).
///
/// Previously these lived inside <c>FolderWatcher.Path/.animarr/</c> — i.e. inside
/// each watched media folder. That polluted the user's library tree with dot-prefixed
/// cache directories that kept reappearing every time they tried to delete them.
///
/// The cache now lives next to the database (e.g. <c>/app/data/image-cache/</c> in
/// the docker image) and never touches the user's media folders. Each watched
/// folder gets its own subdir keyed by FolderWatcher.Id so unrelated items can't
/// collide.
/// </summary>
public sealed class MediaCachePaths
{
    public string CacheRoot { get; }

    public MediaCachePaths(IConfiguration cfg)
    {
        // Mirror Program.cs: derive cache root from the same data dir the
        // SQLite db lives in. Falls back to current dir for the dev profile.
        var conn   = cfg.GetConnectionString("DefaultConnection") ?? "";
        var dbPath = conn.Replace("Data Source=", "").Trim();
        var dbDir  = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrWhiteSpace(dbDir)) dbDir = ".";
        CacheRoot = Path.GetFullPath(Path.Combine(dbDir, "image-cache"));
        Directory.CreateDirectory(CacheRoot);
    }

    /// <summary>Per-folder cache subdirectory. Created on first call.</summary>
    public string ForFolder(Guid folderId)
    {
        var dir = Path.Combine(CacheRoot, folderId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
