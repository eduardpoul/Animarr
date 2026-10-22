using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace Animarr.Web.Services;

/// <summary>
/// Directory listings of the external-track sweep, reused while the tree is
/// unchanged. Every player open used to re-list the whole title folder
/// (thousands of files for a long series with dub folders) — and every episode
/// of one series sweeps the same tree.
///
/// A listing is keyed by (sweep root, recursive) and holds each directory of
/// the tree with its last-write time, read BEFORE that directory was listed.
/// Adding, removing or renaming an entry bumps the mtime of the directory that
/// contains it, so "every stamp still matches" proves the listing current —
/// checking that is one stat per directory instead of a walk. The episode parse
/// of every file is cached inside the listing too (per pattern set), so a
/// repeated open neither walks nor re-parses.
///
/// Dot-folders (<c>.animarr</c> holds the title's theme music / trailers, which
/// are audio files but no dub) and Synology <c>@eaDir</c> are not descended.
/// </summary>
public sealed class ExternalTrackListingCache
{
    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<(string Root, bool Recursive), Listing> _listings = new();
    private readonly ILogger<ExternalTrackListingCache> _logger;
    private long _clock;

    public ExternalTrackListingCache(ILogger<ExternalTrackListingCache> logger) => _logger = logger;

    /// <summary>One file of a listing (audio, subtitle or video only).</summary>
    public readonly record struct Entry(string Path, FileKind Kind);

    public sealed class Listing
    {
        internal Listing(string root, (string Path, DateTime LastWriteUtc)[] directories, Entry[] files)
        {
            Root        = root;
            Directories = directories;
            Files       = files;
        }

        public string Root { get; }
        internal (string Path, DateTime LastWriteUtc)[] Directories { get; }
        public IReadOnlyList<Entry> Files { get; }
        internal long LastUse;

        /// <summary>Parsed (season, episode) per file path, per pattern-set
        /// signature (see <see cref="GetParse"/>).</summary>
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (int? Season, int? Episode)>> _parses
            = new(StringComparer.Ordinal);

        /// <summary>The cached parse of <paramref name="path"/> under
        /// <paramref name="signature"/> (the effective patterns + season
        /// defaulting), computed by <paramref name="parse"/> on a miss.</summary>
        public (int? Season, int? Episode) GetParse(string signature, string path,
            Func<string, (int? Season, int? Episode)> parse)
        {
            var bySig = _parses.GetOrAdd(signature, _ => new(StringComparer.Ordinal));
            if (_parses.Count > 4)
                foreach (var key in _parses.Keys.Where(k => k != signature).ToList())
                    _parses.TryRemove(key, out _);
            return bySig.GetOrAdd(path, parse);
        }
    }

    /// <summary>The current listing of <paramref name="root"/> — the cached one
    /// when every directory stamp still matches, else a fresh walk.</summary>
    public Listing Get(string root, bool recursive, Func<string, FileKind> kindOf)
    {
        var key = (root, recursive);
        if (_listings.TryGetValue(key, out var cached) && IsCurrent(cached))
        {
            cached.LastUse = Interlocked.Increment(ref _clock);
            return cached;
        }

        var started = Environment.TickCount64;
        var fresh = Scan(root, recursive, kindOf);
        _logger.LogDebug("External-track listing of {Root}: walked {Dirs} folder(s), {Files} file(s) in {Ms} ms",
            root, fresh.Directories.Length, fresh.Files.Count, Environment.TickCount64 - started);
        fresh.LastUse = Interlocked.Increment(ref _clock);
        _listings[key] = fresh;
        if (_listings.Count > MaxEntries) Evict();
        return fresh;
    }

    private static bool IsCurrent(Listing listing)
    {
        foreach (var (path, stamp) in listing.Directories)
        {
            try
            {
                // A vanished directory reads as 1601-01-01 — a mismatch too.
                if (Directory.GetLastWriteTimeUtc(path) != stamp) return false;
            }
            catch { return false; }
        }
        return true;
    }

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = false,   // descent is decided per directory below
        MatchType             = MatchType.Win32,
        // Symlinks / junctions (files and directories) are not followed: a
        // link planted in the library would otherwise list — and hand to the
        // players — files from outside it, and a directory link can loop.
        AttributesToSkip      = FileAttributes.ReparsePoint,
        IgnoreInaccessible    = true,
    };

    /// <summary>Directories one listing walks at most (a title's tree is a few dozen).</summary>
    private const int MaxDirectories = 4096;

    private static Listing Scan(string root, bool recursive, Func<string, FileKind> kindOf)
    {
        var directories = new List<(string, DateTime)>();
        var files = new List<Entry>();
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        while (pending.Count > 0 && directories.Count < MaxDirectories)
        {
            var dir = pending.Pop();
            if (!visited.Add(dir)) continue;   // a cycle (bind mount) can't run away
            directories.Add((dir, Directory.GetLastWriteTimeUtc(dir)));

            IEnumerable<(string Path, bool IsDirectory)> entries;
            try
            {
                entries = new FileSystemEnumerable<(string, bool)>(dir,
                    static (ref FileSystemEntry e) => (e.ToSpecifiedFullPath(), e.IsDirectory), Walk).ToList();
            }
            catch (Exception) when (dir != root)
            {
                continue;   // an unreadable sub-directory just contributes nothing
            }

            foreach (var (path, isDirectory) in entries)
            {
                if (isDirectory)
                {
                    if (recursive && !IsSkippedDirectory(Path.GetFileName(path))) pending.Push(path);
                    continue;
                }
                var kind = kindOf(Path.GetExtension(path));
                if (kind is FileKind.Audio or FileKind.Subtitle or FileKind.Video)
                    files.Add(new Entry(path, kind));
            }
        }
        // Stable order regardless of the walk (labels / results sort on it).
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return new Listing(root, directories.ToArray(), files.ToArray());
    }

    private static bool IsSkippedDirectory(string name)
        => name.StartsWith('.') || string.Equals(name, "@eaDir", StringComparison.OrdinalIgnoreCase);

    /// <summary>Drop the least-recently-used eighth once over capacity.</summary>
    private void Evict()
    {
        var excess = _listings.Count - MaxEntries + MaxEntries / 8;
        if (excess <= 0) return;
        foreach (var kv in _listings.ToArray().OrderBy(kv => Volatile.Read(ref kv.Value.LastUse)).Take(excess))
            _listings.TryRemove(kv.Key, out _);
    }
}
