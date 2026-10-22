using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Finds external (sideload) audio / subtitle files that belong to a given
/// video — fandub tracks and sidecar subtitles that live next to the video or
/// in a sub-folder of the title rather than muxed inside the container.
///
/// Two deterministic tiers, no LLM:
///   • <b>Tier 0 — sidecar.</b> Same directory, same stem (optionally with a
///     trailing language/tag suffix): <c>Ep.S01E05.mkv</c> ↔
///     <c>Ep.S01E05.mka</c> / <c>Ep.S01E05.rus.mka</c> / <c>Ep.S01E05.srt</c>.
///     The classic Plex/Jellyfin convention; highest confidence.
///   • <b>Tier 1 — elsewhere in the title's tree.</b> An audio/subtitle file in
///     the media folder tree that is
///       – the video's own name in a sub-folder (<c>RUS Sound/Movie.mka</c>,
///         <c>Subs/Ep.S01E05.eng.ass</c>);
///       – for a series, a file whose parsed (season, episode) equals the
///         video's — e.g. <c>/Russian Dub/05.mka</c> for <c>… S01E05 …</c>.
///         Reuses the exact <see cref="IPatternMatchService"/> machinery
///         (episode regexes + <see cref="IPatternMatchService.DetectSeasonFromPath"/>)
///         the library scanner uses for video files;
///       – for a movie whose folder holds no other video, any audio/subtitle
///         file in a sub-folder (<c>RUS Sound/AniLibria.mka</c>).
///
/// Names are read by <see cref="ExternalTrackNaming"/>: language from the
/// suffix, then the whole folder path below the title root (deepest first —
/// <c>Rus Sub/Signs/</c>), then what the name adds to the video's; the
/// <c>DELAY 500ms</c> tag; signs/forced; and a <see cref="ExternalTrackDto.GroupKey"/>
/// that names the same dub on every episode (per-profile track memory keys on
/// it). Codec / channels / duration come from ffprobe via the shared
/// <see cref="MediaProbeCache"/>. The directory walk and the per-file episode
/// parse are cached per tree (<see cref="ExternalTrackListingCache"/>), so a
/// repeated open — or the next episode of the same series — costs one stat per
/// folder.
///
/// The genuinely ambiguous residue (unparseable names, multiple competing dubs,
/// title disambiguation) is intentionally NOT handled here — that's where an
/// optional LLM pass would slot in later. This service stays deterministic,
/// offline-safe, and cheap so it can run on every player open.
/// </summary>
public sealed partial class ExternalTrackService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IPatternMatchService _patterns;
    private readonly ExternalTrackListingCache _listings;
    private readonly MediaProbeCache _probeCache;
    private readonly ILogger<ExternalTrackService> _logger;

    public ExternalTrackService(
        IDbContextFactory<AppDbContext> dbFactory,
        IPatternMatchService patterns,
        ExternalTrackListingCache listings,
        MediaProbeCache probeCache,
        ILogger<ExternalTrackService> logger)
    {
        _dbFactory  = dbFactory;
        _patterns   = patterns;
        _listings   = listings;
        _probeCache = probeCache;
        _logger     = logger;
    }

    /// <summary>
    /// Discover external audio + subtitle tracks for <paramref name="videoFullPath"/>,
    /// with codec / channels / duration probed. Returns an empty list (never
    /// throws) when nothing matches or the path is outside any watched folder.
    /// </summary>
    public Task<IReadOnlyList<ExternalTrackDto>> FindForVideoAsync(
        string videoFullPath, CancellationToken ct = default)
        => FindForVideoAsync(videoFullPath, probe: true, ct);

    /// <summary>Same as <see cref="FindForVideoAsync(string, CancellationToken)"/>;
    /// <paramref name="probe"/> = false skips ffprobe (Codec / Channels /
    /// DurationSec stay null) for callers that only need the names — the
    /// playback start's track defaults.</summary>
    public async Task<IReadOnlyList<ExternalTrackDto>> FindForVideoAsync(
        string videoFullPath, bool probe, CancellationToken ct)
    {
        try
        {
            return await FindCoreAsync(videoFullPath, probe, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External-track discovery failed for {Path}", videoFullPath);
            return Array.Empty<ExternalTrackDto>();
        }
    }

    /// <summary>One match before probing / DTO assembly.</summary>
    private sealed record Match(
        string Path, FileKind Kind, string Tier, bool SameDir, bool StemMatch, string? Suffix,
        IReadOnlyList<string> RelativeSegments, string Folder, bool NamedByFile);

    private async Task<IReadOnlyList<ExternalTrackDto>> FindCoreAsync(
        string videoFullPath, bool probe, CancellationToken ct)
    {
        var videoFull = Path.GetFullPath(videoFullPath);
        var videoDir  = Path.GetDirectoryName(videoFull);
        if (string.IsNullOrEmpty(videoDir)) return Array.Empty<ExternalTrackDto>();
        var videoStem = Path.GetFileNameWithoutExtension(videoFull);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ── Locate the owning FolderWatcher (most-specific matching root) ──────
        // Runs on every player open: read only the columns the choice needs,
        // untracked, and load patterns for the chosen folder alone below.
        // Ordered by Id so equal candidates always resolve the same way.
        var folders = await db.FolderWatchers.AsNoTracking()
            .Where(f => f.Path != null && f.Path != "")
            .OrderBy(f => f.Id)
            .Select(f => new OwnerCandidate(f.Id, f.Path, f.IsSection, f.SingleFilePath))
            .ToListAsync(ct);
        var folder = PickOwner(folders, videoFull);

        // Decide how wide to sweep. The hard constraint is NOT to cross into a
        // sibling series — otherwise an identically-numbered "05.mka" from
        // another show would false-match. So:
        //   • per-series / per-movie folder  → its whole subtree (dubs may sit
        //     in a /Dub or /Russian sub-folder of the title).
        //   • broad SECTION watcher          → narrow to the title sub-folder
        //     (the section's immediate child on the path to the video).
        //   • single loose file / no owner   → the video's own directory only.
        string searchRoot;
        bool recursive;
        if (folder is null || folder.SingleFilePath is not null)
        {
            searchRoot = videoDir;
            recursive  = false;
        }
        else if (folder.IsSection)
        {
            var seriesRoot = ImmediateChildUnder(folder.Path, videoFull);
            searchRoot = seriesRoot ?? videoDir;
            recursive  = seriesRoot is not null;
        }
        else
        {
            searchRoot = Path.GetFullPath(folder.Path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            recursive  = true;
        }

        var mediaItem = folder is null
            ? null
            : await db.MediaItems.AsNoTracking()
                .Where(m => m.FolderId == folder.Id)
                .Select(m => new { m.MediaType, m.SeasonsJson })
                .FirstOrDefaultAsync(ct);
        bool isMovie = mediaItem?.MediaType == MediaItemType.Movie;
        int declaredSeasons = CountSeasonsInJson(mediaItem?.SeasonsJson);
        bool defaultToSeasonOne = !isMovie && declaredSeasons <= 1;

        var patterns = await BuildEffectivePatternsAsync(db, folder?.Id, isMovie, ct);

        ExternalTrackListingCache.Listing listing;
        try { listing = _listings.Get(searchRoot, recursive, KindOf); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External-track sweep could not enumerate {Root}", searchRoot);
            return Array.Empty<ExternalTrackDto>();
        }

        // The path-season of a file depends only on its directory (the root is
        // fixed for this sweep) — ask once per directory, not once per file.
        var seasonByDir = new Dictionary<string, int?>(StringComparer.Ordinal);
        var parseSignature = ParseSignature(patterns, defaultToSeasonOne);
        (int? Season, int? Episode) Parse(string path) => listing.GetParse(parseSignature, path,
            p => ResolveEpisode(p, patterns, searchRoot, defaultToSeasonOne, seasonByDir));

        // ── Resolve the video's own (season, episode) ─────────────────────────
        var (videoSeason, videoEpisode) = isMovie ? (null, null) : Parse(videoFull);

        // A movie folder with this one video in it owns every dub / sub file
        // of its sub-folders, whatever they are called. With extras or a
        // second cut next to it, only the video's own name counts.
        bool movieOwnsTree = isMovie && recursive
            && listing.Files.Count(f => f.Kind == FileKind.Video) == 1;

        // VobSub sidecars come as .idx + .sub: the pair is ONE track, listed
        // as its .idx (with the .sub as companion). A .sub without an .idx is
        // a text format (MicroDVD) and stays a track of its own.
        var allPaths = listing.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Sweep candidate audio / subtitle files ────────────────────────────
        var matches = new List<Match>();
        foreach (var file in listing.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Kind is not (FileKind.Audio or FileKind.Subtitle)) continue;
            if (file.Path.EndsWith(".sub", StringComparison.OrdinalIgnoreCase)
                && allPaths.Contains(Path.ChangeExtension(file.Path, ".idx")))
                continue;

            var path     = file.Path;
            var candDir  = Path.GetDirectoryName(path) ?? searchRoot;
            var candStem = Path.GetFileNameWithoutExtension(path);
            bool sameDir = string.Equals(
                candDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                videoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

            string? tier = null;
            bool namedByFile = false;
            bool stemMatch = ExternalTrackNaming.TryMatchSidecar(candStem, videoStem, out var suffix);
            if (stemMatch)
            {
                // Tier 0 next to the video; Tier 1 when the same name sits in a
                // sub-folder — unless that folder is another season's.
                if (sameDir) tier = "sidecar";
                else if (isMovie || SeasonMatchesLoose(Parse(path).Season, videoSeason, defaultToSeasonOne))
                    tier = "episode";
            }
            else if (!sameDir && movieOwnsTree)
            {
                // The file's own name is the dub's ("RUS Sound/AniLibria.mka").
                tier = "episode";
                namedByFile = true;
            }
            else if (!isMovie && videoEpisode is not null)
            {
                // ── Tier 1 — same (season, episode) bucket anywhere in the tree ──
                var (cSeason, cEpisode) = Parse(path);
                if (cEpisode == videoEpisode && SeasonMatches(cSeason, videoSeason, defaultToSeasonOne)
                    && !ExternalTrackNaming.IsDecimalEpisodeVariant(candStem, cEpisode.Value, videoStem))
                    tier = "episode";
            }
            if (tier is null) continue;

            matches.Add(new Match(path, file.Kind, tier, sameDir, stemMatch, stemMatch ? suffix : null,
                sameDir ? [] : ExternalTrackNaming.RelativeSegments(searchRoot, candDir, videoDir),
                ExternalTrackNaming.RelativeFolder(searchRoot, candDir), namedByFile));
        }

        var probes = probe ? await ProbeAllAsync(matches, ct) : null;

        var results = new List<ExternalTrackDto>(matches.Count);
        var hints   = new List<string>(matches.Count);   // what tells two files of one group apart
        for (int i = 0; i < matches.Count; i++)
        {
            var m        = matches[i];
            var p        = probes?[i];
            var candStem = Path.GetFileNameWithoutExtension(m.Path);
            bool isAudio = m.Kind == FileKind.Audio;

            var groupSegments = ExternalTrackNaming.GroupSegments(m.RelativeSegments);
            var remainder     = m.StemMatch ? null : ExternalTrackNaming.StemRemainder(candStem, videoStem);
            var lang = ExternalTrackNaming.ExtractLanguage(m.Suffix, m.RelativeSegments.Reverse(), remainder)
                       ?? ExternalTrackNaming.FromLanguageTag(p?.Language);
            var ext   = Path.GetExtension(m.Path).TrimStart('.').ToLowerInvariant();
            var codec = p?.Codec ?? ext switch { "sup" => "hdmv_pgs_subtitle", "idx" => "dvd_subtitle", _ => null };
            var companion = ext == "idx" && allPaths.Contains(Path.ChangeExtension(m.Path, ".sub"))
                ? Path.ChangeExtension(m.Path, ".sub") : null;

            results.Add(new ExternalTrackDto(
                Path:      m.Path,
                FileName:  Path.GetFileName(m.Path),
                Ext:       ext,
                Kind:      isAudio ? "audio" : "subtitle",
                Label:     ExternalTrackNaming.BuildLabel(isAudio, groupSegments, m.Suffix, remainder, lang,
                               m.NamedByFile ? remainder : null),
                Language:  lang?.Code,
                MatchTier: m.Tier,
                Codec:        codec,
                Channels:     p?.Channels,
                DurationSec:  p?.DurationSec,
                DelayMs:      ExternalTrackNaming.ParseDelayMs(candStem),
                GroupKey:     ExternalTrackNaming.GroupKey(m.SameDir, m.StemMatch, groupSegments, m.Suffix, candStem),
                Folder:       m.Folder,
                SubtitleType: isAudio ? null
                    : ExternalTrackNaming.SubtitleTypeOf(m.Suffix, string.Join(' ', m.RelativeSegments), remainder),
                Image:         isAudio ? null : SubtitleCodecs.IsImage(codec),
                CompanionPath: companion));
            hints.Add(m.StemMatch ? m.Suffix ?? "" : remainder ?? "");
        }
        Disambiguate(results, hints);

        // Audio first, sidecars before tree matches, then alphabetic for stability.
        return results
            .OrderBy(t => t.Kind == "audio" ? 0 : 1)
            .ThenBy(t => t.MatchTier == "sidecar" ? 0 : 1)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Two files of one kind under one group key (<c>Dubs/05 AniLibria.mka</c>
    /// and <c>Dubs/05 AniDub.mka</c> are both <c>dir:dubs</c>) get the file
    /// name's template appended to the key (<c>dir:dubs|name:# anilibria</c>);
    /// two with one label get what their names add (<c>Dubs · AniLibria</c>).
    /// A key stays bare while it is unique, so the usual one-dub-per-folder
    /// layout keeps the same key on every episode.
    /// </summary>
    private static void Disambiguate(List<ExternalTrackDto> results, List<string> hints)
    {
        foreach (var group in Enumerable.Range(0, results.Count)
                     .GroupBy(i => (results[i].Kind, results[i].GroupKey))
                     .Where(g => g.Count() > 1))
            foreach (var i in group)
                results[i] = results[i] with
                {
                    GroupKey = results[i].GroupKey + "|name:"
                               + ExternalTrackNaming.NameTemplate(Path.GetFileNameWithoutExtension(results[i].Path)),
                };

        foreach (var group in Enumerable.Range(0, results.Count)
                     .GroupBy(i => (results[i].Kind, results[i].Label), (k, g) => g.ToList())
                     .Where(g => g.Count > 1))
            foreach (var i in group)
            {
                var hint = ExternalTrackNaming.CleanLabel(ExternalTrackNaming.StripDelay(hints[i]));
                if (hint.Length > 0 && !results[i].Label.EndsWith(hint, StringComparison.OrdinalIgnoreCase))
                    results[i] = results[i] with { Label = $"{results[i].Label} · {hint}" };
            }
    }

    /// <summary>The configured extension kinds, plus the bitmap subtitle
    /// sidecars the library scanner has no use for: <c>.sup</c> (PGS) and
    /// <c>.idx</c> (VobSub index; its <c>.sub</c> is the companion).</summary>
    private FileKind KindOf(string extension)
        => extension.Equals(".sup", StringComparison.OrdinalIgnoreCase) || extension.Equals(".idx", StringComparison.OrdinalIgnoreCase)
            ? FileKind.Subtitle
            : _patterns.DetermineFileKind(extension);

    // ─── ffprobe facts of the matched files ───────────────────────────────────

    private sealed record ProbeFacts(string? Codec, int? Channels, double? DurationSec, string? Language);

    /// <summary>Codec / channels / duration / language tag of every match, from
    /// the shared probe cache (keyed by path + mtime + size: one ffprobe per file
    /// version, ever). Four at a time; a failed probe just leaves the facts
    /// null.</summary>
    private async Task<ProbeFacts?[]> ProbeAllAsync(List<Match> matches, CancellationToken ct)
    {
        var facts = new ProbeFacts?[matches.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, matches.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) =>
            {
                try
                {
                    var json = await _probeCache.GetStreamsJsonAsync(matches[i].Path, token);
                    if (json is not null)
                        facts[i] = ParseProbe(json, matches[i].Kind == FileKind.Audio ? "audio" : "subtitle");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "External-track probe failed for {Path}", matches[i].Path);
                }
            });
        return facts;
    }

    private static ProbeFacts? ParseProbe(string json, string codecType)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var type) || type.GetString() != codecType) continue;
            string? codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
            int? channels = s.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var c) ? c : null;
            double? duration = ReadSeconds(s, "duration")
                ?? (root.TryGetProperty("format", out var format) ? ReadSeconds(format, "duration") : null);
            string? language = null;
            if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                foreach (var tag in tags.EnumerateObject())
                    if (string.Equals(tag.Name, "language", StringComparison.OrdinalIgnoreCase))
                        language = tag.Value.GetString();
            return new ProbeFacts(codec, channels, duration is { } d ? Math.Round(d, 3) : null, language);
        }
        return null;
    }

    private static double? ReadSeconds(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? d : null;

    // ─── Episode resolution (mirrors MediaFileResolver) ───────────────────────

    private (int? season, int? episode) ResolveEpisode(
        string filePath, IReadOnlyList<RenamePattern> patterns, string root,
        bool defaultToSeasonOne, Dictionary<string, int?> seasonByDir)
    {
        var fileName = Path.GetFileName(filePath);
        var fileDir  = Path.GetDirectoryName(filePath) ?? root;
        var parse    = _patterns.ParseFileName(fileName, patterns);

        int? season = parse.Season;
        if (season is null)
        {
            lock (seasonByDir)
            {
                if (!seasonByDir.TryGetValue(fileDir, out season))
                    seasonByDir[fileDir] = season = _patterns.DetectSeasonFromPath(fileDir, root);
            }
        }
        int? episode = parse.IsMatched ? parse.Episode : TryParseBareNumericEpisode(fileName);

        if (episode is not null && season is null && defaultToSeasonOne)
            season = 1;

        return (season, episode);
    }

    /// <summary>What the cached parses depend on besides the path: the pattern
    /// texts in order and the single-season defaulting.</summary>
    private static string ParseSignature(IReadOnlyList<RenamePattern> patterns, bool defaultToSeasonOne)
        => (defaultToSeasonOne ? "1|" : "0|")
           + string.Join('\n', patterns.Select(p => p.Priority.ToString(CultureInfo.InvariantCulture) + ":" + p.Pattern));

    private static bool SeasonMatches(int? a, int? b, bool defaultToSeasonOne)
    {
        // Treat "no season" as season 1 for single-season series so a bare
        // numeric dub file lines up with an S01-tagged video (and vice versa).
        if (defaultToSeasonOne)
        {
            a ??= 1;
            b ??= 1;
        }
        return a == b;
    }

    /// <summary>For a same-name match in a sub-folder: only a season the
    /// folder path states explicitly, and that differs, rules it out.</summary>
    private static bool SeasonMatchesLoose(int? candidate, int? video, bool defaultToSeasonOne)
        => candidate is null || video is null || SeasonMatches(candidate, video, defaultToSeasonOne);

    private async Task<List<RenamePattern>> BuildEffectivePatternsAsync(
        AppDbContext db, Guid? folderId, bool isMovie, CancellationToken ct)
    {
        var globalPatterns = await db.RenamePatterns.AsNoTracking()
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);

        var folderPatterns = folderId is Guid fid
            ? await db.RenamePatterns.AsNoTracking().Where(p => p.FolderId == fid).ToListAsync(ct)
            : new List<RenamePattern>();
        var excludedIds = folderPatterns
            .Where(p => p.IsExcluded && p.GlobalPatternId.HasValue)
            .Select(p => p.GlobalPatternId!.Value)
            .ToHashSet();

        return globalPatterns
            .Where(p => !excludedIds.Contains(p.Id))
            .Concat(folderPatterns.Where(p => !p.IsExcluded))
            .Where(p => p.ApplicableTo is null ||
                       (isMovie ? p.ApplicableTo == FolderType.Movie
                                : p.ApplicableTo != FolderType.Movie))
            .OrderBy(p => p.Priority)
            .ToList();
    }

    // ─── Shared small helpers (kept local to avoid touching MediaFileResolver) ─

    [GeneratedRegex(@"^(?:ep?)?(\d{1,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex BareNumericEpisodeRegex();

    private static int? TryParseBareNumericEpisode(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = BareNumericEpisodeRegex().Match(stem);
        return m.Success && int.TryParse(m.Groups[1].ValueSpan, out var n) ? n : null;
    }

    private static int CountSeasonsInJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.GetArrayLength();
        }
        catch { /* malformed — treat as zero */ }
        return 0;
    }

    /// <summary>
    /// The folder that owns <paramref name="videoFull"/> (already
    /// <c>Path.GetFullPath</c>'d): the flat-section entry registered for exactly
    /// this file, else the most specific folder containing it — a real media
    /// folder before a section, then the deepest root. Flat-section siblings
    /// share their section's directory as Path, so they only ever match by
    /// their own file.
    /// </summary>
    internal static OwnerCandidate? PickOwner(IReadOnlyList<OwnerCandidate> folders, string videoFull)
    {
        foreach (var f in folders)
        {
            if (f.SingleFilePath is null) continue;
            string single;
            try { single = Path.GetFullPath(f.SingleFilePath); }
            catch { continue; }
            if (string.Equals(single, videoFull, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return folders
            .Where(f => f.SingleFilePath is null && PathIsUnder(videoFull, f.Path))
            .OrderBy(f => f.IsSection ? 1 : 0)        // prefer a real media folder over a section
            .ThenByDescending(f => f.Path.Length)     // then the deepest matching root
            .FirstOrDefault();
    }

    /// <summary><paramref name="normalizedPath"/> must already be
    /// <c>Path.GetFullPath</c>'d (done once per call, not once per folder).</summary>
    private static bool PathIsUnder(string normalizedPath, string root)
    {
        try
        {
            var normalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal sealed record OwnerCandidate(Guid Id, string Path, bool IsSection, string? SingleFilePath);

    /// <summary>The directory that is the IMMEDIATE child of <paramref name="ancestor"/>
    /// on the path down to <paramref name="descendantFile"/> — i.e. the series
    /// folder sitting directly under a section. Returns null if the file isn't a
    /// descendant or lives directly in the ancestor.</summary>
    private static string? ImmediateChildUnder(string ancestor, string descendantFile)
    {
        try
        {
            var a = Path.GetFullPath(ancestor)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var startDir = Path.GetDirectoryName(Path.GetFullPath(descendantFile));
            for (var dir = startDir is null ? null : new DirectoryInfo(startDir);
                 dir?.Parent is not null;
                 dir = dir.Parent)
            {
                var parentFull = dir.Parent.FullName
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(parentFull, a, StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
            }
            return null;
        }
        catch { return null; }
    }
}
