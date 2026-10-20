using System.Security.Claims;
using Animarr.Web.Data;
using Animarr.Web.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// The single place that decides whether a caller-supplied disk path may be
/// served. Every byte-serving endpoint (/api/video, /api/file, /api/image,
/// /api/probe, /api/subtitle, HLS start, DLNA cast …) funnels through here so
/// the "must live under a registered library root" invariant, the symlink
/// rejection and the traversal checks stay in one auditable spot.
///
/// It also applies the CALLER's access (the endpoints themselves only prove
/// that somebody is signed in):
///   • the user's role folder allowlist (<see cref="RoleFolderFilter"/>) — a
///     folder-restricted profile can't read files of other sections, whether it
///     arrives with the cookie or with a media token;
///   • a file-bound link token (<see cref="MediaTokenKind.Link"/> /
///     <see cref="MediaTokenKind.DlnaLink"/>) only opens the file it was signed for.
/// </summary>
public sealed class MediaPathValidator(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaCachePaths cachePaths,
    IHttpContextAccessor httpAccessor)
{
    /// <summary>Outcome of a validation. When <see cref="Ok"/> is false,
    /// <see cref="Error"/> carries the HTTP result to return (400/401/403/404);
    /// when true, <see cref="FullPath"/> is the canonical absolute path.
    /// Deconstructs into <c>(ok, fullPath, earlyResult)</c> so call sites read
    /// like the old inline tuple helper.</summary>
    public readonly record struct Result(bool Ok, string? FullPath, IResult? Error)
    {
        public void Deconstruct(out bool ok, out string? fullPath, out IResult? earlyResult)
            => (ok, fullPath, earlyResult) = (Ok, FullPath, Error);
    }

    private sealed record FolderRow(Guid Id, string Path, Guid? ParentSectionId, string? SingleFilePath);

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
        var folders = await db.FolderWatchers
            .AsNoTracking()
            .Select(f => new FolderRow(f.Id, f.Path, f.ParentSectionId, f.SingleFilePath))
            .ToListAsync(ct);

        bool inLibrary = folders.Any(f => IsUnder(fullPath, f.Path));
        bool inCache   = !inLibrary && includeImageCache && IsUnder(fullPath, cachePaths.CacheRoot);
        if (!inLibrary && !inCache)
            return new(false, null, Results.Forbid());

        // Caller access — before the existence check, so a restricted caller
        // can't probe which files exist in sections it may not see.
        var denied = await CheckCallerAsync(fullPath, folders, inCache, ct);
        if (denied is not null)
            return new(false, null, denied);

        if (!File.Exists(fullPath))
            return new(false, null, Results.NotFound());

        return new(true, fullPath, null);
    }

    /// <summary>Null when the current caller may read <paramref name="fullPath"/>,
    /// otherwise the 401/403 to answer.</summary>
    private async Task<IResult?> CheckCallerAsync(
        string fullPath, List<FolderRow> folders, bool inCache, CancellationToken ct)
    {
        var http = httpAccessor.HttpContext;
        if (http is null) return null;   // not a request (no caller today) — roots check only

        var user = http.User;
        if (user?.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        // A file-bound link token opens exactly the file it was signed for.
        var bound = user.FindFirst(MediaAuth.Claims.Resource)?.Value;
        if (bound is not null)
        {
            var actual = Convert.ToHexString(MediaTokenService.ResourceHash(fullPath));
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(bound)))
                return Results.Forbid();
        }

        // DLNA MediaServer links carry no account; the handler already required
        // the admin's opt-in + a LAN client, and the MediaServer exposes the whole
        // library by design. The bound-file check above still applies.
        if (user.FindFirst(MediaAuth.Claims.Kind)?.Value == ((byte)MediaTokenKind.DlnaLink).ToString())
            return null;

        var me = await http.RequestServices.GetRequiredService<IUserContext>().GetCurrentUserAsync(ct);
        if (me?.Role is null) return Results.Unauthorized();

        var allowed = me.Role.GetAllowedFolderIds();
        if (allowed is null) return null;   // unrestricted role

        bool Permitted(FolderRow f) =>
            allowed.Contains(f.Id) || (f.ParentSectionId is Guid p && allowed.Contains(p));

        if (inCache)
        {
            // Image cache layout: <CacheRoot>/<folderId:N>/… (posters, fanart,
            // episode stills). Anything else in there (e.g. the resized/ variants,
            // keyed by hash) isn't addressable per folder → unrestricted roles only.
            var rel = Path.GetRelativePath(cachePaths.CacheRoot, fullPath);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return Guid.TryParseExact(first, "N", out var fid)
                   && folders.FirstOrDefault(f => f.Id == fid) is { } owner && Permitted(owner)
                ? null
                : Results.Forbid();
        }

        foreach (var f in folders)
        {
            if (!Permitted(f)) continue;

            // Media-adjacent asset dir of this folder (trickplay sprites, theme
            // music): <base>/.animarr/<folderId:N>/…
            if (IsUnder(fullPath, Path.Combine(f.Path, ".animarr", f.Id.ToString("N"))))
                return null;

            // A flat-section movie that's allowlisted on its own (its section
            // isn't): Path is the SHARED section dir, so only the movie file and
            // its same-stem sidecars (Movie.ru.srt, Movie.eng.mka …) count.
            if (f.SingleFilePath is { } single
                && !(f.ParentSectionId is Guid sec && allowed.Contains(sec)))
            {
                var dir  = Path.GetDirectoryName(Path.GetFullPath(single));
                var stem = Path.GetFileNameWithoutExtension(single);
                if (string.Equals(Path.GetDirectoryName(fullPath), dir, PathComparison)
                    && Path.GetFileName(fullPath).StartsWith(stem, PathComparison))
                    return null;
                continue;
            }

            if (IsUnder(fullPath, f.Path)) return null;
        }
        return Results.Forbid();
    }

    // Ignore case only where the filesystem does: on Linux "/media/Anime" and
    // "/media/anime" are different directories, and an ignore-case prefix
    // match would let the second ride on the first's registration.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsUnder(string fullPath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string normalRoot;
        try
        {
            normalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch { return false; }
        return fullPath.StartsWith(normalRoot, PathComparison);
    }
}
