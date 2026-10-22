using System.Security.Claims;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
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
///
/// This runs for every poster of a grid (hundreds per page) and every Range
/// request of a player, so the rows it decides on — library roots, which role
/// each user holds, each role's allowlist — are held in an in-memory snapshot
/// with the roots pre-normalised. The snapshot is rebuilt when
/// <see cref="LibraryAccessVersion"/> moves (bumped after any SaveChanges that
/// touched FolderWatchers/Users/Roles — a removed folder or narrowed role is
/// refused on the next request) and at least every <see cref="SnapshotTtl"/>
/// as a safety net for writes that bypass the change tracker.
/// </summary>
public sealed class MediaPathValidator(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaCachePaths cachePaths,
    IHttpContextAccessor httpAccessor,
    LibraryAccessVersion accessVersion)
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

    /// <summary>Upper bound on snapshot age, independent of the version bump.</summary>
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    /// <summary>One FolderWatcher with every path the checks below compare
    /// against already normalised (null = never matches, like an empty or
    /// unparsable root did before): <c>Root</c> = Path + separator,
    /// <c>AssetRoot</c> = &lt;Path&gt;/.animarr/&lt;Id:N&gt; + separator, and for a
    /// flat-section movie the directory of its full SingleFilePath plus the
    /// file-name stem its sidecars share.</summary>
    private sealed record FolderRoot(
        Guid Id,
        Guid? ParentSectionId,
        string? Root,
        string? AssetRoot,
        bool IsSingleFile,
        string? SingleDir,
        string? SingleStem);

    private sealed class AccessSnapshot
    {
        public required long Version { get; init; }
        public required long LoadedAtMs { get; init; }
        public required FolderRoot[] Folders { get; init; }
        public required Dictionary<Guid, FolderRoot> FoldersById { get; init; }
        /// <summary>Every distinct <see cref="FolderRoot.Root"/>; looked up
        /// per separator of the candidate path instead of a scan.</summary>
        public required HashSet<string> Roots { get; init; }
        /// <summary>User id → role id.</summary>
        public required Dictionary<Guid, Guid> UserRoles { get; init; }
        /// <summary>Role id → allowed folder ids (null = all folders).</summary>
        public required Dictionary<Guid, HashSet<Guid>?> RoleFolders { get; init; }
    }

    private enum PathKind { Missing, File, Directory, Link }

    private readonly string  _cacheRootPath = cachePaths.CacheRoot;
    private readonly string? _cacheRoot     = NormaliseRoot(cachePaths.CacheRoot);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private AccessSnapshot? _snapshot;

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

        // One stat answers what used to take four (Directory.Exists,
        // File.Exists, GetAttributes, File.Exists) — on a FUSE/NAS mount each
        // of those is a round-trip.
        var kind = Probe(fullPath);

        // Must point at a file, not a directory.
        if (kind == PathKind.Directory)
            return new(false, null, Results.BadRequest());

        // C-6: reject symlinks (reparse points) so a link planted inside an
        // allowed folder can't leak files from outside the library.
        if (kind == PathKind.Link)
            return new(false, null, Results.Forbid());

        var snap = await GetSnapshotAsync(ct);

        bool inLibrary = IsUnderAny(fullPath, snap.Roots);
        bool inCache   = !inLibrary && includeImageCache && IsUnder(fullPath, _cacheRoot);
        if (!inLibrary && !inCache)
            return new(false, null, Results.Forbid());

        // Caller access — before the existence check, so a restricted caller
        // can't probe which files exist in sections it may not see.
        var denied = CheckCaller(fullPath, snap, inCache);
        if (denied is not null)
            return new(false, null, denied);

        if (kind != PathKind.File)
            return new(false, null, Results.NotFound());

        return new(true, fullPath, null);
    }

    /// <summary>Null when the current caller may read <paramref name="fullPath"/>,
    /// otherwise the 401/403 to answer.</summary>
    private IResult? CheckCaller(string fullPath, AccessSnapshot snap, bool inCache)
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
        // the admin's opt-in + a LAN client + one of the DLNA byte endpoints
        // (DlnaLinkEndpoints), and the MediaServer exposes the whole library by
        // design. The bound-file check above still applies.
        if (user.FindFirst(MediaAuth.Claims.Kind)?.Value == ((byte)MediaTokenKind.DlnaLink).ToString())
            return null;

        // The account behind the cookie / token (same claim UserContext reads)
        // must still exist and hold an existing role — as of the snapshot,
        // which a user delete or role change invalidates immediately.
        if (!Guid.TryParse(user.FindFirst(AuthConstants.UserIdClaim)?.Value, out var uid)
            || !snap.UserRoles.TryGetValue(uid, out var roleId)
            || !snap.RoleFolders.TryGetValue(roleId, out var allowed))
            return Results.Unauthorized();

        if (allowed is null) return null;   // unrestricted role

        bool Permitted(FolderRoot f) =>
            allowed.Contains(f.Id) || (f.ParentSectionId is Guid p && allowed.Contains(p));

        if (inCache)
        {
            // Image cache layout: <CacheRoot>/<folderId:N>/… (posters, fanart,
            // episode stills). Anything else in there (e.g. the resized/ variants,
            // keyed by hash) isn't addressable per folder → unrestricted roles only.
            var rel = Path.GetRelativePath(_cacheRootPath, fullPath);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            return Guid.TryParseExact(first, "N", out var fid)
                   && snap.FoldersById.TryGetValue(fid, out var owner) && Permitted(owner)
                ? null
                : Results.Forbid();
        }

        var fileDir  = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        foreach (var f in snap.Folders)
        {
            if (!Permitted(f)) continue;

            // Media-adjacent asset dir of this folder (trickplay sprites, theme
            // music): <base>/.animarr/<folderId:N>/…
            if (IsUnder(fullPath, f.AssetRoot))
                return null;

            // A flat-section movie that's allowlisted on its own (its section
            // isn't): Path is the SHARED section dir, so only the movie file and
            // its same-stem sidecars (Movie.ru.srt, Movie.eng.mka …) count.
            if (f.IsSingleFile
                && !(f.ParentSectionId is Guid sec && allowed.Contains(sec)))
            {
                if (f.SingleDir is not null
                    && string.Equals(fileDir, f.SingleDir, PathComparison)
                    && fileName.StartsWith(f.SingleStem!, PathComparison))
                    return null;
                continue;
            }

            if (IsUnder(fullPath, f.Root)) return null;
        }
        return Results.Forbid();
    }

    // ── snapshot ────────────────────────────────────────────────────────────

    private ValueTask<AccessSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var snap = Volatile.Read(ref _snapshot);
        return IsCurrent(snap) ? ValueTask.FromResult(snap!) : new(ReloadAsync(ct));
    }

    private bool IsCurrent(AccessSnapshot? snap) =>
        snap is not null
        && snap.Version == accessVersion.Current
        && Environment.TickCount64 - snap.LoadedAtMs < (long)SnapshotTtl.TotalMilliseconds;

    private async Task<AccessSnapshot> ReloadAsync(CancellationToken ct)
    {
        // One reload at a time: after a folder edit the next grid fires
        // hundreds of poster requests at once, and they should share one load.
        await _reloadGate.WaitAsync(ct);
        try
        {
            var snap = Volatile.Read(ref _snapshot);
            if (IsCurrent(snap)) return snap!;
            snap = await LoadSnapshotAsync(ct);
            Volatile.Write(ref _snapshot, snap);
            return snap;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<AccessSnapshot> LoadSnapshotAsync(CancellationToken ct)
    {
        // Read the version BEFORE the rows: a write committing mid-load bumps
        // past it, so this snapshot is already stale when it lands.
        var version  = accessVersion.Current;
        var loadedAt = Environment.TickCount64;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var folderRows = await db.FolderWatchers
            .AsNoTracking()
            .Select(f => new { f.Id, f.Path, f.ParentSectionId, f.SingleFilePath })
            .ToListAsync(ct);
        var userRows = await db.Users
            .AsNoTracking()
            .Select(u => new { u.Id, u.RoleId })
            .ToListAsync(ct);
        var roleRows = await db.Roles
            .AsNoTracking()
            .Select(r => new { r.Id, r.FoldersJson })
            .ToListAsync(ct);

        var folders = new FolderRoot[folderRows.Count];
        for (var i = 0; i < folderRows.Count; i++)
        {
            var f = folderRows[i];
            string? singleDir = null, singleStem = null;
            if (f.SingleFilePath is { } single)
            {
                try { singleDir = Path.GetDirectoryName(Path.GetFullPath(single)); }
                catch { /* unparsable → this entry can't grant its file */ }
                singleStem = Path.GetFileNameWithoutExtension(single);
            }
            folders[i] = new FolderRoot(
                f.Id, f.ParentSectionId,
                NormaliseRoot(f.Path),
                f.Path is null ? null : NormaliseRoot(Path.Combine(f.Path, ".animarr", f.Id.ToString("N"))),
                f.SingleFilePath is not null, singleDir, singleStem);
        }

        var roots = new HashSet<string>(PathComparer);
        foreach (var f in folders)
            if (f.Root is not null) roots.Add(f.Root);

        return new AccessSnapshot
        {
            Version     = version,
            LoadedAtMs  = loadedAt,
            Folders     = folders,
            FoldersById = folders.ToDictionary(f => f.Id),
            Roots       = roots,
            UserRoles   = userRows.ToDictionary(u => u.Id, u => u.RoleId),
            RoleFolders = roleRows.ToDictionary(
                r => r.Id,
                r => Role.ParseAllowedFolderIds(r.FoldersJson) is { } ids ? new HashSet<Guid>(ids) : null),
        };
    }

    // ── path helpers ────────────────────────────────────────────────────────

    /// <summary>What's on disk at <paramref name="fullPath"/>, from a single
    /// attribute read. Mirrors the old checks: a directory or a link to one is
    /// a Directory (Directory.Exists followed links); any other reparse point,
    /// dangling links included, is a Link; anything unreadable is Missing.</summary>
    private static PathKind Probe(string fullPath)
    {
        // File.Exists is false for a path with a trailing separator, while
        // GetAttributes would trim it and describe the file — keep the old answer.
        if (Path.EndsInDirectorySeparator(fullPath))
            return Directory.Exists(fullPath) ? PathKind.Directory : PathKind.Missing;

        FileAttributes attrs;
        try { attrs = File.GetAttributes(fullPath); }
        catch { return PathKind.Missing; }

        if ((attrs & FileAttributes.Directory) != 0)    return PathKind.Directory;
        if ((attrs & FileAttributes.ReparsePoint) != 0) return PathKind.Link;
        return PathKind.File;
    }

    // Ignore case only where the filesystem does: on Linux "/media/Anime" and
    // "/media/anime" are different directories, and an ignore-case prefix
    // match would let the second ride on the first's registration.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Canonical "root + separator" prefix, or null when the root is
    /// blank or unparsable (such a root never matches).</summary>
    private static string? NormaliseRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            return Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch { return null; }
    }

    private static bool IsUnder(string fullPath, string? normalRoot) =>
        normalRoot is not null && fullPath.StartsWith(normalRoot, PathComparison);

    /// <summary>Same answer as <c>roots.Any(r => IsUnder(fullPath, r))</c>:
    /// every root ends with a separator, so a matching root is exactly the
    /// prefix of <paramref name="fullPath"/> up to one of its separators.
    /// O(path depth) set probes instead of a scan of every folder.</summary>
    private static bool IsUnderAny(string fullPath, HashSet<string> roots)
    {
        var lookup = roots.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var i = fullPath.IndexOf(Path.DirectorySeparatorChar);
             i >= 0;
             i = fullPath.IndexOf(Path.DirectorySeparatorChar, i + 1))
        {
            if (lookup.Contains(fullPath.AsSpan(0, i + 1))) return true;
        }
        return false;
    }
}
