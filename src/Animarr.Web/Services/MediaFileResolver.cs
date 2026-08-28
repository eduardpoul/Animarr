using System.Text.Json;
using System.Text.RegularExpressions;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Server-side equivalent of the episode-to-file mapping that the original
/// MediaDetail.razor did inline. Hands the API the same answer the Razor
/// page used to compute locally — file paths bucketed by (season, episode)
/// when a pattern matches, plus unmatched leftovers so the user can pick.
///
/// Lighter than the original (no LLM fallback, no fuzzy season-name
/// matching against TMDB) — those refinements stay in the Razor server
/// page until the API surface grows. Pattern-based extraction covers the
/// 95% case.
/// </summary>
public sealed class MediaFileResolver
{
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts",
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IPatternMatchService _patterns;

    public MediaFileResolver(IDbContextFactory<AppDbContext> dbFactory, IPatternMatchService patterns)
    {
        _dbFactory = dbFactory;
        _patterns  = patterns;
    }

    public async Task<MediaFileDto[]> ResolveAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems
            .Include(m => m.Folder)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return Array.Empty<MediaFileDto>();

        var folder = item.Folder;
        if (folder is null) return Array.Empty<MediaFileDto>();

        // Flat-section single-file rows store one absolute path directly.
        if (!string.IsNullOrEmpty(folder.SingleFilePath))
        {
            if (!File.Exists(folder.SingleFilePath)) return Array.Empty<MediaFileDto>();
            var fi = new FileInfo(folder.SingleFilePath);
            return new[] { new MediaFileDto(fi.FullName, fi.Name, null, null, fi.Length) };
        }

        if (string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path))
            return Array.Empty<MediaFileDto>();

        // Effective pattern set: globals minus folder-excluded + folder-local.
        var globalPatterns = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);
        var folderWithPatterns = await db.FolderWatchers
            .Include(f => f.Patterns)
            .FirstAsync(f => f.Id == folder.Id, ct);

        var excludedIds = folderWithPatterns.Patterns
            .Where(p => p.IsExcluded && p.GlobalPatternId.HasValue)
            .Select(p => p.GlobalPatternId!.Value)
            .ToHashSet();

        // Trust the MediaItem's classification (set by TMDB/LLM at identify
        // time) over the folder's FolderType. Older installs have a bunch of
        // Series-typed MediaItems sitting in FolderType=Movie folders because
        // the auto-classifier guessed wrong from filenames like "7.mkv" —
        // Death's Game in the user's library is the canonical example. The
        // old code took FolderType as ground truth and force-nulled
        // season/episode for those, hiding all episodes from MediaDetail.
        bool isMovie = item.MediaType == MediaItemType.Movie;

        var effectivePatterns = globalPatterns
            .Where(p => !excludedIds.Contains(p.Id))
            .Concat(folderWithPatterns.Patterns.Where(p => !p.IsExcluded))
            .Where(p => p.ApplicableTo is null ||
                       (isMovie ? p.ApplicableTo == FolderType.Movie : p.ApplicableTo != FolderType.Movie))
            .OrderBy(p => p.Priority)
            .ToList();

        // For Series with ≤ 1 declared season, fall back to season=1 when
        // patterns extract an episode but no season. Typical k-drama /
        // single-cour release layout: title-named parent folder + bare
        // numeric filenames, no "Season 1" anywhere on disk.
        int declaredSeasons = CountSeasonsInJson(item.SeasonsJson);
        bool defaultToSeasonOne = !isMovie && declaredSeasons <= 1;

        // Overlay: stored overrides (manual corrections + LLM verdicts) for this
        // item, keyed by absolute path. Applied on top of the deterministic parse
        // in the loop below — see EpisodeFileMapping. Movies never bucket by
        // episode, so we skip the query for them.
        var overrides = isMovie
            ? new Dictionary<string, EpisodeFileMapping>()
            : await db.EpisodeFileMappings
                .Where(m => m.MediaItemId == item.Id)
                .ToDictionaryAsync(m => m.FilePath, ct);

        // Per-season absolute offsets (donghua: TMDB one season, disk multi).
        // diskSeason → offset, so AbsoluteEpisode = offset + episode. Empty when
        // not applicable. See SeasonOffsetResolver / MediaItem.SeasonOffsetsJson.
        var seasonOffsets = ParseSeasonOffsets(item.SeasonOffsetsJson);

        // TMDB season structure (number → episode count), for distributing a
        // flat absolute-numbered disk into seasons. See AbsoluteToSeasonEpisode.
        var seasonStructure = ParseSeasonList(item.SeasonsJson);

        // ── Pass 1: deterministic (season, episode) per file ──────────────────
        var raw = new List<(string FilePath, string FileName, int? Season, int? Episode, long Size, bool SeasonFromPath)>();
        foreach (var filePath in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
        {
            if (!VideoExts.Contains(Path.GetExtension(filePath))) continue;
            var fileName = Path.GetFileName(filePath);
            var parse    = _patterns.ParseFileName(fileName, effectivePatterns);
            var fileDir  = Path.GetDirectoryName(filePath) ?? folder.Path;

            // Two different claims, deliberately kept apart. A season baked into
            // the FILE NAME by a release tag ("[TV-4] - 01") labels that one file.
            // A season read from the PATH ("Season 4/…") says the DISK is laid out
            // per-season. Only the latter may veto absolute-numbering distribution
            // below: conflating them let a single tagged weekly release switch off
            // the distribution for an entire flat library, collapsing it into S1.
            int? seasonFromName = parse.Season;
            int? seasonFromPath = seasonFromName is null
                ? _patterns.DetectSeasonFromPath(fileDir, folder.Path)
                : null;
            int? season  = seasonFromName ?? seasonFromPath;
            int? episode = parse.IsMatched ? parse.Episode : null;

            // Bare-numeric filename fallback: `7.mkv`, `08.mkv`, `ep12.mkv`.
            if (episode is null)
                episode = TryParseBareNumericEpisode(fileName);

            // Season fallback for single-season series.
            if (episode is not null && season is null && defaultToSeasonOne)
                season = 1;

            // Movies have no season/episode bucket — leave both null.
            if (isMovie) { season = null; episode = null; }

            long size;
            try { size = new FileInfo(filePath).Length; } catch { continue; }
            // Season 0 (Specials) never counted as a numbered season folder, and
            // still doesn't — matching the old `Season is null or 0` test.
            raw.Add((filePath, fileName, season, episode, size, seasonFromPath is > 0));
        }

        // Absolute-numbering distribution applies ONLY to a genuinely FLAT disk —
        // no "Season N" folder (no file resolved to a season ≥ 1) — whose files
        // run straight past season 1. That separates a flat absolute release
        // (Stellar Transformation: 50 files numbered 1..50 → S1-S4) from a
        // season-foldered disk with a few stray un-parsed files, which must NOT
        // be redistributed.
        int s1Count = seasonStructure.Count > 0 ? seasonStructure[0].Count : 0;
        int maxFlatEp = raw.Where(r => r.Season is null && r.Episode is > 0)
                           .Select(r => r.Episode!.Value).DefaultIfEmpty(0).Max();
        // A multi-season show whose files all sit loose in one folder — the disk
        // is numbered absolutely, whatever any individual filename claims.
        bool flatDisk = !isMovie
            && seasonStructure.Count > 1
            && raw.All(r => !r.SeasonFromPath);      // no numbered season folders
        bool applyAbsolute = flatDisk
            && maxFlatEp > s1Count;                  // genuinely spans past S1

        // ── Pass 2: distribution → overrides → absolute number → DTO ──────────
        var results = new List<MediaFileDto>(raw.Count);
        foreach (var (filePath, fileName, season0, episode0, size, _) in raw)
        {
            int? season = season0, episode = episode0;
            string? source = null;
            int? absoluteEp = null;

            // Map a flat absolute number into the TMDB seasons (abs 13 → S2E1
            // when S1 has 12). Numbers within S1 just belong to season 1.
            if (applyAbsolute && season is null && episode is > 0)
            {
                var (s, e) = AbsoluteToSeasonEpisode(episode.Value, seasonStructure);
                if (s > 1)
                {
                    absoluteEp = episode;   // the bare number IS the TMDB absolute episode
                    season  = s;
                    episode = e;
                    source  = "absolute";
                }
                else
                {
                    season = 1;
                }
            }

            // Overlay a stored override (manual / LLM) — a null field keeps the
            // deterministic value, so a partial correction composes cleanly.
            if (!isMovie && overrides.TryGetValue(filePath, out var ov))
            {
                if (ov.Season.HasValue)  season  = ov.Season;
                if (ov.Episode.HasValue) episode = ov.Episode;
                source = ov.Source;
            }

            // Season-offset absolute number (the donghua case: TMDB one season,
            // disk split) — only when not already set by the distribution above.
            if (absoluteEp is null && season is not null && episode is not null &&
                seasonOffsets.TryGetValue(season.Value, out var off))
                absoluteEp = off + episode.Value;

            // No stored offsets, but we know (season, episode) and the disk is
            // flat-absolute: the TMDB season structure gives the same answer.
            // S4E01 with seasons [26,26,26,…] → absolute 79, which is what lines
            // a freshly-tagged weekly release up with the 78 bare-numbered files
            // already sitting next to it.
            if (absoluteEp is null && flatDisk && season is > 0 && episode is > 0)
                absoluteEp = SeasonEpisodeToAbsolute(season.Value, episode.Value, seasonStructure);

            results.Add(new MediaFileDto(filePath, fileName, season, episode, size, source, absoluteEp));
        }

        // Sort: by season → episode → filename so the catalog renders in a
        // predictable order regardless of how the OS returned directory entries.
        return results
            .OrderBy(f => f.Season ?? int.MaxValue)
            .ThenBy(f => f.Episode ?? int.MaxValue)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Counts top-level entries in MediaItem.SeasonsJson (a JSON array
    /// of SeasonMeta objects). Returns 0 on null/empty/malformed input — callers
    /// should treat that as "no season metadata" and apply the single-season
    /// default. Cheap one-shot parse; we only need the array length.</summary>
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

    /// <summary>Parse MediaItem.SeasonOffsetsJson (a JSON object {"3":106,…})
    /// into a diskSeason→offset map. Returns an empty map on null/malformed
    /// input. Keys are season numbers as strings; values are absolute offsets.</summary>
    private static Dictionary<int, int> ParseSeasonOffsets(string? json)
    {
        var map = new Dictionary<int, int>();
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (int.TryParse(p.Name, out var s) && p.Value.TryGetInt32(out var off))
                        map[s] = off;
        }
        catch { /* malformed — treat as no offsets */ }
        return map;
    }

    /// <summary>Parse MediaItem.SeasonsJson (TMDB season list) into ordered
    /// (number, episodeCount) pairs. Case-insensitive — stored casing varies.</summary>
    private static List<(int Number, int Count)> ParseSeasonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<SeasonListEntry>>(json, SeasonListOpts);
            return list?.Select(s => (s.Number, s.EpisodeCount)).OrderBy(x => x.Number).ToList() ?? [];
        }
        catch { return []; }
    }

    private sealed record SeasonListEntry(int Number, int EpisodeCount);
    private static readonly JsonSerializerOptions SeasonListOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Map an absolute episode number to (season, episode) using the
    /// cumulative TMDB season episode counts: abs 13 with seasons [12,12,…] →
    /// (2, 1). Numbers past the last known season stay in the last season.</summary>
    private static (int Season, int Episode) AbsoluteToSeasonEpisode(
        int abs, List<(int Number, int Count)> seasons)
    {
        int cum = 0;
        foreach (var (number, count) in seasons)
        {
            if (abs <= cum + count) return (number, abs - cum);
            cum += count;
        }
        var last = seasons[^1];
        return (last.Number, abs - (cum - last.Count));
    }

    /// <summary>Inverse of <see cref="AbsoluteToSeasonEpisode"/>: (season 4,
    /// episode 1) with seasons [26,26,26,1] → absolute 79. Specials (season 0)
    /// are skipped rather than counted — they carry no absolute number and would
    /// otherwise shift every real season. Returns null when the season isn't in
    /// the known structure, so a bogus release tag can't invent a number.</summary>
    private static int? SeasonEpisodeToAbsolute(
        int season, int episode, List<(int Number, int Count)> seasons)
    {
        int cum = 0;
        foreach (var (number, count) in seasons)
        {
            if (number <= 0) continue;
            if (number == season) return cum + episode;
            cum += count;
        }
        return null;
    }

    /// <summary>Last-resort episode extraction for filenames that no rename
    /// pattern caught: a name whose stem is purely digits, or "e"/"ep" + digits.
    /// Examples: "7.mkv" → 7, "08.mkv" → 8, "ep12.mp4" → 12. Returns null for
    /// anything fancier (so we don't accidentally pull "2160" out of
    /// "Title.2160p.mkv" — that's still the pattern set's job).</summary>
    private static int? TryParseBareNumericEpisode(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(stem, @"^(?:ep?)?(\d{1,4})$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }
}
