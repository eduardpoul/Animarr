using Animarr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// The single place that decides whether a caller-supplied disk path may be
/// served. Every byte-serving endpoint (/api/video, /api/file, /api/image,
/// /api/probe, /api/subtitle, HLS start, DLNA cast …) funnels through here so
/// the "must live under a registered library root" invariant, the symlink
/// rejection and the traversal checks stay in one auditable spot.
/// </summary>
public sealed class MediaPathValidator(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaCachePaths cachePaths)
{
    /// <summary>Outcome of a validation. When <see cref="Ok"/> is false,
    /// <see cref="Error"/> carries the HTTP result to return (400/403/404);
    /// when true, <see cref="FullPath"/> is the canonical absolute path.</summary>
    public readonly record struct Result(bool Ok, string? FullPath, IResult? Error);

    /// <summary>Validate a path that must resolve to a FILE inside one of the
    /// registered FolderWatcher roots.</summary>
    public Task<Result> ResolveLibraryFileAsync(string? path, CancellationToken ct = default)
        => ResolveAsync(path, includeImageCache: false, ct);

    /// <summary>Same as <see cref="ResolveLibraryFileAsync"/> but additionally
    /// accepts files inside Animarr's own image cache — used by /api/image,
    /// whose payloads live next to the database rather than the media tree.</summary>
    public Task<Result> ResolveLibraryOrCacheFileAsync(string? path, CancellationToken ct = default)
        => ResolveAsync(path, includeImageCache: true, ct);

    private async Task<Result> ResolveAsync(string? path, bool includeImageCache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(false, null, Results.BadRequest());

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return new(false, null, Results.BadRequest()); }

        // Must point at a file, not a directory.
        if (Directory.Exists(fullPath))
            return new(false, null, Results.BadRequest());

        // C-6: reject symlinks (reparse points) so a link planted inside an
        // allowed folder can't leak files from outside the library.
        try
        {
            if (File.Exists(fullPath))
            {
                var attrs = File.GetAttributes(fullPath);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    return new(false, null, Results.Forbid());
            }
        }
        catch { /* unreadable attributes — fall through to the root check */ }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var allowedRoots = await db.FolderWatchers
            .Select(f => f.Path)
            .ToListAsync(ct);
        if (includeImageCache)
            allowedRoots.Add(cachePaths.CacheRoot);

        bool allowed = allowedRoots.Any(root =>
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            var normalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase);
        });

        if (!allowed)
            return new(false, null, Results.Forbid());

        if (!File.Exists(fullPath))
            return new(false, null, Results.NotFound());

        return new(true, fullPath, null);
    }
}
