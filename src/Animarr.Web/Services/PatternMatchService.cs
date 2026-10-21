using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Animarr.Web.Configuration;
using Animarr.Web.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Animarr.Web.Services;

public partial class PatternMatchService(
    IOptions<AppSettings> appOptions,
    ILogger<PatternMatchService> logger) : IPatternMatchService
{
    private readonly FrozenDictionary<string, FileKind> _fileKinds = BuildFileKinds(appOptions.Value);

    // ─── Regex for detecting season from folder name ─────────────────────────

    [GeneratedRegex(@"(?i)(?:season|s|сезон|serie[s]?)\s*0*(?<s>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(@"(?i)\bpart\s+0*(?<s>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();

    [GeneratedRegex(@"(?i)\bs0*(?<s>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SCodeRegex();

    /// <summary>Non-numbered "season 0" buckets — a folder whose whole name is
    /// Specials/Extras/OVA/… is treated as season 0 (the Specials group), so its
    /// files surface as-is instead of vanishing with season=null.</summary>
    [GeneratedRegex(@"(?i)^(?:specials?|extras?|ova|ovas|oad|ncop|nced|sp|sps|pv|pvs|bonus)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolderRegex();

    // ─── FileKind ─────────────────────────────────────────────────────────────

    public FileKind DetermineFileKind(string extension)
        => _fileKinds.TryGetValue(extension.ToLowerInvariant(), out var kind) ? kind : FileKind.Unknown;

    /// <summary>Extension → kind in one frozen lookup instead of four linear
    /// scans per call (it runs per file of the external-track sweep). Same
    /// semantics as before: the configured strings are the keys, compared
    /// ordinally against the lower-cased input, and when a list overlaps
    /// another the old check order wins (video, subtitle, audio, image).</summary>
    private static FrozenDictionary<string, FileKind> BuildFileKinds(AppSettings s)
    {
        var map = new Dictionary<string, FileKind>(StringComparer.Ordinal);
        // Lowest precedence first — later lists overwrite earlier ones.
        foreach (var e in s.ImageExtensions)    if (e is not null) map[e] = FileKind.Image;
        foreach (var e in s.AudioExtensions)    if (e is not null) map[e] = FileKind.Audio;
        foreach (var e in s.SubtitleExtensions) if (e is not null) map[e] = FileKind.Subtitle;
        foreach (var e in s.VideoExtensions)    if (e is not null) map[e] = FileKind.Video;
        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // ─── Parse filename ───────────────────────────────────────────────────────

    /// <summary>Rename-pattern regexes, compiled once and keyed by pattern text.
    /// ParseFileName runs for every file of every resolve (a library pass is
    /// thousands of files), and building each pattern's Regex from scratch per
    /// file was the dominant cost of a scan: ~32 µs and ~56 KB of garbage per
    /// file, vs ~0.9 µs cached + Compiled. The text fully determines the regex,
    /// so an edited pattern just gets a new key — nothing to invalidate. Invalid
    /// patterns are cached as null, so their L-7 warning is logged once per
    /// pattern text instead of once per file.</summary>
    private static readonly ConcurrentDictionary<string, Regex?> PatternRegexes = new(StringComparer.Ordinal);

    /// <summary>Safety cap on <see cref="PatternRegexes"/>: only reachable by
    /// editing patterns hundreds of times in one process lifetime, and then the
    /// cache simply starts over.</summary>
    private const int MaxCachedPatterns = 512;

    private Regex? GetPatternRegex(RenamePattern p)
    {
        if (PatternRegexes.TryGetValue(p.Pattern, out var cached)) return cached;

        Regex? rx;
        try
        {
            rx = new Regex(p.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            // L-7: surface invalid patterns instead of silently swallowing.
            logger.LogWarning(ex, "Skipping pattern «{Name}» (id={Id}): invalid regex `{Pattern}`",
                p.Name, p.Id, p.Pattern);
            rx = null;
        }

        if (PatternRegexes.Count >= MaxCachedPatterns) PatternRegexes.Clear();
        return PatternRegexes.GetOrAdd(p.Pattern, rx);
    }

    /// <summary>Fallback for bare-number thumb images ("1-thumb.jpg", "01_thumb.jpg").</summary>
    [GeneratedRegex(@"^0*(\d+)[_\-]thumb$")]
    private static partial Regex BareThumbRegex();

    public ParseResult ParseFileName(string fileName, IEnumerable<RenamePattern> patterns)
    {
        // The resolvers pass patterns already ordered by Priority ascending;
        // ExternalPlayerEndpoints passes them unsorted. A stable sort of an
        // already-sorted list is the list itself, so only sort when needed.
        var ordered = patterns is IReadOnlyList<RenamePattern> list && IsSortedByPriority(list)
            ? list
            : patterns.OrderBy(x => x.Priority).ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (string.IsNullOrWhiteSpace(p.Pattern)) continue;

            var rx = GetPatternRegex(p);
            if (rx is null) continue;

            var m = rx.Match(fileName);
            if (!m.Success) continue;

            int? season = null;
            var seasonGroup = m.Groups["season"];
            if (seasonGroup.Success && int.TryParse(seasonGroup.ValueSpan, out var s))
                season = s;

            var episodeGroup = m.Groups["episode"];
            if (!episodeGroup.Success) continue;
            if (!int.TryParse(episodeGroup.ValueSpan, out var episode)) continue;

            // Ordinal ignore-case on the span — same answer as lower-casing the
            // stem first, minus two allocations and a culture-aware compare per file.
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName.AsSpan());
            var isThumb = nameWithoutExt.EndsWith("-thumb", StringComparison.OrdinalIgnoreCase)
                       || nameWithoutExt.EndsWith("_thumb", StringComparison.OrdinalIgnoreCase)
                       || nameWithoutExt.EndsWith(".thumb", StringComparison.OrdinalIgnoreCase);

            return new ParseResult(true, season, episode, isThumb);
        }

        // Fallback: filename without extension is a pure integer → treat as episode number.
        // e.g. "1.mp4", "01.mkv", "12.mp4" are already-named episode files.
        var fnLower = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var thumbOnly = fnLower.EndsWith("-thumb", StringComparison.Ordinal)
                     || fnLower.EndsWith("_thumb", StringComparison.Ordinal);

        // Fallback for bare-number thumb images: "1-thumb.jpg", "01_thumb.jpg" → episode=1, isThumb=true
        var thumbNumMatch = BareThumbRegex().Match(fnLower);
        if (thumbNumMatch.Success && int.TryParse(thumbNumMatch.Groups[1].ValueSpan, out var thumbEp) && thumbEp > 0)
            return new ParseResult(true, null, thumbEp, true);

        if (int.TryParse(fnLower, out var bareEp) && bareEp > 0)
            return new ParseResult(true, null, bareEp, false);

        return new ParseResult(false, null, 0, thumbOnly);
    }

    private static bool IsSortedByPriority(IReadOnlyList<RenamePattern> list)
    {
        for (int i = 1; i < list.Count; i++)
            if (list[i - 1].Priority > list[i].Priority) return false;
        return true;
    }

    // ─── Detect season from folder path ──────────────────────────────────────

    /// <summary>
    /// M-10: walks up the directory tree from <paramref name="folderPath"/> looking
    /// for a season marker. Stops at <paramref name="rootPath"/> (the FolderWatcher
    /// root) if provided, or after <c>maxDepth</c> levels otherwise.
    /// </summary>
    public int? DetectSeasonFromPath(string folderPath, string? rootPath = null, int maxDepth = 5)
    {
        // Walks plain strings with DirectoryInfo's semantics (full path, then
        // Name / Parent per level) instead of allocating a DirectoryInfo chain —
        // this runs for every file a resolve can't read a season from.
        string? dir = Path.GetFullPath(folderPath);
        string? normalRoot = null;
        if (!string.IsNullOrEmpty(rootPath))
        {
            try
            {
                normalRoot = Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { /* invalid root — fall back to depth-only */ }
        }

        for (int i = 0; i < maxDepth && dir != null; i++, dir = ParentDirectory(dir))
        {
            var name = DirectoryName(dir);

            // Non-numbered specials/extras folder → season 0 (the "Specials"
            // bucket). Checked first so it wins over any stray season match.
            if (SpecialsFolderRegex().IsMatch(name)) return 0;

            var m = SeasonWordRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sw)) return sw;

            m = SCodeRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sc)) return sc;

            m = PartRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sp)) return sp;

            // Stop once we reach the FolderWatcher root — don't leak into parents above it.
            if (normalRoot != null)
            {
                var dirFull = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(dirFull, normalRoot, StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }

        return null;
    }

    // DirectoryInfo.Name / .Parent on a full path, without the objects: a root
    // names itself and has no parent; otherwise a trailing separator is ignored.
    private static bool IsRootPath(string fullPath) => Path.GetPathRoot(fullPath.AsSpan()).Length == fullPath.Length;

    private static string DirectoryName(string fullPath) =>
        IsRootPath(fullPath) ? fullPath : Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));

    private static string? ParentDirectory(string fullPath) =>
        Path.GetDirectoryName(IsRootPath(fullPath) ? fullPath : Path.TrimEndingDirectorySeparator(fullPath));
}
