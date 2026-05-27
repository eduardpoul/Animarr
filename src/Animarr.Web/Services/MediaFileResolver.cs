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

        var results = new List<MediaFileDto>();
        foreach (var filePath in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
        {
            if (!VideoExts.Contains(Path.GetExtension(filePath))) continue;
            var fileName = Path.GetFileName(filePath);
            var parse    = _patterns.ParseFileName(fileName, effectivePatterns);
            var fileDir  = Path.GetDirectoryName(filePath) ?? folder.Path;

            int? season = parse.Season
                ?? _patterns.DetectSeasonFromPath(fileDir, folder.Path);
            int? episode = parse.IsMatched ? parse.Episode : null;

            // Bare-numeric filename fallback: `7.mkv`, `08.mkv`, `ep12.mkv`.
            // Triggered only when no pattern matched — the default global
            // patterns require ≥2 digits + a separator, which excludes the
            // single-digit naming common in Korean / Chinese releases.
            if (episode is null)
            {
                episode = TryParseBareNumericEpisode(fileName);
            }

            // Season fallback for single-season series — see note above.
            if (episode is not null && season is null && defaultToSeasonOne)
            {
                season = 1;
            }

            // Movies have no season/episode bucket — leave both null.
            if (isMovie) { season = null; episode = null; }

            FileInfo fi;
            try { fi = new FileInfo(filePath); } catch { continue; }
            results.Add(new MediaFileDto(filePath, fileName, season, episode, fi.Length));
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
