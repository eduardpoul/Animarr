using Animarr.Shared.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Animarr.Web.Services;

/// <summary>
/// Process-wide memo of <see cref="MediaFileResolver"/> results, one entry per
/// media item. A resolve walks the item's folder and parses every video file;
/// it used to run from scratch on every MediaDetail open (twice), on every Home
/// load for up to 30 next-up candidates, per airing title on the calendar, and
/// once per episode thumbnail — O(N²) for a long show's first open.
///
/// An entry is served only while it provably matches what a fresh resolve
/// would return:
///   • <b>Database inputs</b> — the caller always loads the current inputs
///     (item fields, effective patterns, overrides) and they must compare equal
///     to the ones the entry was built from (<see cref="MediaFileResolver.Inputs.SameAs"/>).
///     That covers every write path — manual/LLM mappings, pattern edits,
///     re-identification, folder renames — without having to hook any of them.
///   • <b>Disk listing</b> — the last-write time of every directory in the
///     folder tree, read before that directory was listed, must be unchanged.
///     Adding, removing or renaming an entry bumps its directory's mtime.
///     Results whose directories changed within <see cref="RacyWindow"/> of
///     the walk are not stored at all (coarse mtime granularity — FAT's 2 s,
///     a jiffy on Linux — could otherwise hide a change made in the same tick).
///   • <b>TTL</b> (<see cref="Ttl"/>) — the safety net for what mtimes can't
///     see: a file growing in place (only <c>SizeBytes</c>, display-only) and
///     union/FUSE mounts that report one branch's directory mtime.
/// Bounded by total file count (<see cref="MaxCachedFiles"/>).
/// </summary>
public sealed class MediaFileResolveCache : IDisposable
{
    internal static readonly TimeSpan Ttl        = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(3);
    /// <summary>Entries are sized by file count; ~0.5-1 KB per cached file.</summary>
    internal const long MaxCachedFiles = 50_000;

    private sealed record Entry(
        MediaFileResolver.Inputs Inputs,
        (string Path, DateTime LastWriteUtc)[] Directories,
        MediaFileDto[] Files);

    private readonly MemoryCache _entries = new(new MemoryCacheOptions { SizeLimit = MaxCachedFiles });

    /// <summary>The cached result for <paramref name="inputs"/>' item, if it is
    /// still valid; returns a copy the caller may keep or mutate.</summary>
    internal bool TryGet(MediaFileResolver.Inputs inputs, out MediaFileDto[] files)
    {
        files = [];
        if (!_entries.TryGetValue(inputs.MediaItemId, out Entry? entry) || entry is null)
            return false;

        if (!entry.Inputs.SameAs(inputs) || !DirectoriesUnchanged(entry.Directories))
        {
            _entries.Remove(inputs.MediaItemId);
            return false;
        }

        files = (MediaFileDto[])entry.Files.Clone();
        return true;
    }

    internal void Store(
        MediaFileResolver.Inputs inputs, MediaFileDto[] files, List<(string Path, DateTime LastWriteUtc)> directories)
    {
        // A directory touched just now may be touched again within the same
        // mtime tick without the stamp moving — don't vouch for such a listing.
        var racyAfter = DateTime.UtcNow - RacyWindow;
        foreach (var (_, lastWrite) in directories)
            if (lastWrite > racyAfter) return;

        _entries.Set(inputs.MediaItemId, new Entry(inputs, directories.ToArray(), files),
            new MemoryCacheEntryOptions
            {
                Size = files.Length + 1,
                AbsoluteExpirationRelativeToNow = Ttl,
            });
    }

    private static bool DirectoriesUnchanged((string Path, DateTime LastWriteUtc)[] directories)
    {
        try
        {
            foreach (var (path, lastWrite) in directories)
                // A vanished directory reads as 1601-01-01 and so differs too.
                if (Directory.GetLastWriteTimeUtc(path) != lastWrite) return false;
            return true;
        }
        catch
        {
            return false;   // unreadable now (permissions, unmounted share) — re-resolve
        }
    }

    public void Dispose() => _entries.Dispose();
}
