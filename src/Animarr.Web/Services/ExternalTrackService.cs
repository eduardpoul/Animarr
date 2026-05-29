using System.Text.Json;
using System.Text.RegularExpressions;
using Animarr.Shared.Models;
using Animarr.Web.Configuration;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Animarr.Web.Services;

/// <summary>
/// Finds external (sideload) audio / subtitle files that belong to a given
/// video — fandub tracks and sidecar subtitles that live next to the video or
/// in a sibling sub-folder rather than muxed inside the container.
///
/// Two deterministic tiers, no LLM:
///   • <b>Tier 0 — sidecar.</b> Same directory, same stem (optionally with a
///     trailing language/tag suffix): <c>Ep.S01E05.mkv</c> ↔
///     <c>Ep.S01E05.mka</c> / <c>Ep.S01E05.rus.mka</c> / <c>Ep.S01E05.srt</c>.
///     The classic Plex/Jellyfin convention; highest confidence.
///   • <b>Tier 1 — episode bucket.</b> Any audio/subtitle file ANYWHERE in the
///     media folder tree whose parsed (season, episode) equals the video's —
///     e.g. <c>/Russian Dub/05.mka</c> for <c>… S01E05 …</c>. Reuses the exact
///     same <see cref="IPatternMatchService"/> machinery (episode regexes +
///     <see cref="IPatternMatchService.DetectSeasonFromPath"/>) that the library
///     scanner uses for video files, so the sub-folder case the user worried
///     about is handled with zero new matching logic.
///
/// The genuinely ambiguous residue (unparseable names, multiple competing dubs,
/// title disambiguation) is intentionally NOT handled here — that's where an
/// optional LLM pass would slot in later. This service stays deterministic,
/// offline-safe, and cheap so it can run on every player open.
/// </summary>
public sealed class ExternalTrackService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IPatternMatchService _patterns;
    private readonly AppSettings _settings;
    private readonly ILogger<ExternalTrackService> _logger;

    public ExternalTrackService(
        IDbContextFactory<AppDbContext> dbFactory,
        IPatternMatchService patterns,
        IOptions<AppSettings> appOptions,
        ILogger<ExternalTrackService> logger)
    {
        _dbFactory = dbFactory;
        _patterns  = patterns;
        _settings  = appOptions.Value;
        _logger    = logger;
    }

    /// <summary>
    /// Discover external audio + subtitle tracks for <paramref name="videoFullPath"/>.
    /// Returns an empty list (never throws) when nothing matches or the path is
    /// outside any watched folder.
    /// </summary>
    public async Task<IReadOnlyList<ExternalTrackDto>> FindForVideoAsync(
        string videoFullPath, CancellationToken ct = default)
    {
        try
        {
            return await FindCoreAsync(videoFullPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External-track discovery failed for {Path}", videoFullPath);
            return Array.Empty<ExternalTrackDto>();
        }
    }

    private async Task<IReadOnlyList<ExternalTrackDto>> FindCoreAsync(
        string videoFullPath, CancellationToken ct)
    {
        var videoDir  = Path.GetDirectoryName(videoFullPath);
        if (string.IsNullOrEmpty(videoDir)) return Array.Empty<ExternalTrackDto>();
        var videoStem = Path.GetFileNameWithoutExtension(videoFullPath);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ── Locate the owning FolderWatcher (most-specific matching root) ──────
        var folders = await db.FolderWatchers
            .Include(f => f.Patterns)
            .Where(f => f.Path != null && f.Path != "")
            .ToListAsync(ct);

        FolderWatcher? folder = folders
            .Where(f => PathIsUnder(videoFullPath, f.Path!))
            .OrderBy(f => f.IsSection ? 1 : 0)        // prefer a real media folder over a section
            .ThenByDescending(f => f.Path!.Length)    // then the deepest matching root
            .FirstOrDefault();

        // Decide how wide to sweep. The hard constraint is NOT to cross into a
        // sibling series — otherwise an identically-numbered "05.mka" from
        // another show would false-match. So:
        //   • per-series / per-movie folder  → its whole subtree (dubs may sit
        //     in a /Dub or /Russian sub-folder of the series).
        //   • broad SECTION watcher          → narrow to the series sub-folder
        //     (the section's immediate child on the path to the video).
        //   • single loose file / no owner   → the video's own directory only.
        string searchRoot;
        SearchOption searchOption;
        if (folder is null || folder.SingleFilePath is not null)
        {
            searchRoot   = videoDir;
            searchOption = SearchOption.TopDirectoryOnly;
        }
        else if (folder.IsSection)
        {
            var seriesRoot = ImmediateChildUnder(folder.Path!, videoFullPath);
            searchRoot   = seriesRoot ?? videoDir;
            searchOption = seriesRoot is not null ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        }
        else
        {
            searchRoot   = folder.Path!;
            searchOption = SearchOption.AllDirectories;
        }

        var mediaItem = folder is null
            ? null
            : await db.MediaItems.FirstOrDefaultAsync(m => m.FolderId == folder.Id, ct);
        bool isMovie = mediaItem?.MediaType == MediaItemType.Movie;
        int declaredSeasons = CountSeasonsInJson(mediaItem?.SeasonsJson);
        bool defaultToSeasonOne = !isMovie && declaredSeasons <= 1;

        var patterns = await BuildEffectivePatternsAsync(db, folder, isMovie, ct);

        // ── Resolve the video's own (season, episode) ─────────────────────────
        var (videoSeason, videoEpisode) = ResolveEpisode(
            videoFullPath, patterns, searchRoot, defaultToSeasonOne, isMovie);

        // ── Sweep candidate audio / subtitle files ────────────────────────────
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ExternalTrackDto>();

        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateFiles(searchRoot, "*", searchOption); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External-track sweep could not enumerate {Root}", searchRoot);
            return Array.Empty<ExternalTrackDto>();
        }

        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var ext  = Path.GetExtension(path);
            var kind = _patterns.DetermineFileKind(ext);
            if (kind is not (FileKind.Audio or FileKind.Subtitle)) continue;
            if (seen.Contains(path)) continue;

            var candDir  = Path.GetDirectoryName(path) ?? searchRoot;
            var candStem = Path.GetFileNameWithoutExtension(path);

            // ── Tier 0 — sidecar (same dir, stem == video stem ± suffix) ──────
            string? tier   = null;
            string? suffix = null;
            bool sameDir   = string.Equals(
                candDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                videoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

            if (sameDir && TryMatchSidecar(candStem, videoStem, out suffix))
            {
                tier = "sidecar";
            }
            // ── Tier 1 — same (season, episode) bucket anywhere in the tree ──
            else if (videoEpisode is not null)
            {
                var (cSeason, cEpisode) = ResolveEpisode(
                    path, patterns, searchRoot, defaultToSeasonOne, isMovie: false);
                if (cEpisode == videoEpisode && SeasonMatches(cSeason, videoSeason, defaultToSeasonOne))
                    tier = "episode";
            }

            if (tier is null) continue;

            var extBare = ext.TrimStart('.').ToLowerInvariant();
            var lang    = ExtractLanguage(suffix, Path.GetFileName(candDir), candStem);
            var label   = BuildLabel(kind, sameDir, suffix, candDir, lang);

            results.Add(new ExternalTrackDto(
                Path:      path,
                FileName:  Path.GetFileName(path),
                Ext:       extBare,
                Kind:      kind == FileKind.Audio ? "audio" : "subtitle",
                Label:     label,
                Language:  lang?.code,
                MatchTier: tier));
            seen.Add(path);
        }

        // Audio first, sidecars before tree matches, then alphabetic for stability.
        return results
            .OrderBy(t => t.Kind == "audio" ? 0 : 1)
            .ThenBy(t => t.MatchTier == "sidecar" ? 0 : 1)
            .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ─── Episode resolution (mirrors MediaFileResolver) ───────────────────────

    private (int? season, int? episode) ResolveEpisode(
        string filePath, IReadOnlyList<RenamePattern> patterns, string root,
        bool defaultToSeasonOne, bool isMovie)
    {
        if (isMovie) return (null, null);

        var fileName = Path.GetFileName(filePath);
        var fileDir  = Path.GetDirectoryName(filePath) ?? root;
        var parse    = _patterns.ParseFileName(fileName, patterns);

        int? season  = parse.Season ?? _patterns.DetectSeasonFromPath(fileDir, root);
        int? episode = parse.IsMatched ? parse.Episode : TryParseBareNumericEpisode(fileName);

        if (episode is not null && season is null && defaultToSeasonOne)
            season = 1;

        return (season, episode);
    }

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

    /// <summary>Tier-0 test: a candidate stem is a sidecar of the video stem when
    /// it equals it, or extends it with a separator-delimited suffix
    /// (<c>video</c> → <c>video.rus</c> / <c>video - eng</c>). The captured
    /// suffix feeds the language/label heuristics.</summary>
    private static bool TryMatchSidecar(string candStem, string videoStem, out string? suffix)
    {
        suffix = null;
        if (string.Equals(candStem, videoStem, StringComparison.OrdinalIgnoreCase))
            return true;
        if (candStem.Length > videoStem.Length
            && candStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            var sep = candStem[videoStem.Length];
            if (sep is '.' or ' ' or '_' or '-')
            {
                suffix = candStem[(videoStem.Length + 1)..].Trim(' ', '.', '_', '-');
                return true;
            }
        }
        return false;
    }

    private async Task<List<RenamePattern>> BuildEffectivePatternsAsync(
        AppDbContext db, FolderWatcher? folder, bool isMovie, CancellationToken ct)
    {
        var globalPatterns = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);

        var folderPatterns = folder?.Patterns ?? new List<RenamePattern>();
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

    // ─── Label + language heuristics ──────────────────────────────────────────

    /// <summary>Common language tokens seen in fandub / sub filenames and folder
    /// names (Latin + Cyrillic). Maps to (iso-ish code, display name).</summary>
    private static readonly Dictionary<string, (string code, string name)> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rus"]=("rus","Russian"), ["ru"]=("rus","Russian"), ["russian"]=("rus","Russian"), ["рус"]=("rus","Russian"), ["русский"]=("rus","Russian"),
        ["eng"]=("eng","English"), ["en"]=("eng","English"), ["english"]=("eng","English"),
        ["jpn"]=("jpn","Japanese"), ["jp"]=("jpn","Japanese"), ["ja"]=("jpn","Japanese"), ["jap"]=("jpn","Japanese"), ["japanese"]=("jpn","Japanese"),
        ["ukr"]=("ukr","Ukrainian"), ["ua"]=("ukr","Ukrainian"), ["uk"]=("ukr","Ukrainian"), ["ukrainian"]=("ukr","Ukrainian"), ["укр"]=("ukr","Ukrainian"),
        ["ger"]=("ger","German"), ["de"]=("ger","German"), ["german"]=("ger","German"),
        ["fre"]=("fre","French"), ["fra"]=("fre","French"), ["fr"]=("fre","French"), ["french"]=("fre","French"),
        ["spa"]=("spa","Spanish"), ["es"]=("spa","Spanish"), ["spanish"]=("spa","Spanish"),
        ["kor"]=("kor","Korean"), ["ko"]=("kor","Korean"), ["korean"]=("kor","Korean"),
        ["chi"]=("chi","Chinese"), ["zh"]=("chi","Chinese"), ["chinese"]=("chi","Chinese"),
    };

    private static (string code, string name)? ExtractLanguage(params string?[] sources)
    {
        foreach (var src in sources)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            foreach (var token in Regex.Split(src.ToLowerInvariant(), @"[^\p{L}]+"))
            {
                if (token.Length == 0) continue;
                if (LangMap.TryGetValue(token, out var v)) return v;
            }
        }
        return null;
    }

    private static string BuildLabel(
        FileKind kind, bool sameDir, string? suffix, string candDir,
        (string code, string name)? lang)
    {
        // Choose the most descriptive label source:
        //   • file in its own sub-folder → the folder name ("Russian Dub").
        //   • sidecar with a suffix      → the suffix ("rus", "eng dub").
        //   • bare sidecar (same stem)   → nothing; fall back to language/kind.
        string core = !sameDir
            ? CleanLabel(Path.GetFileName(candDir))
            : CleanLabel(suffix ?? "");

        var kindFallback = kind == FileKind.Audio ? "External audio" : "External subtitle";

        // Core is empty or is itself just a language word → use the language name.
        if (string.IsNullOrWhiteSpace(core) || IsBareLanguageWord(core))
            return lang?.name ?? kindFallback;

        // Prepend the language when known and not already implied by the core.
        if (lang is { } l && !core.Contains(l.name, StringComparison.OrdinalIgnoreCase))
            return $"{l.name} · {core}";

        return core;
    }

    private static bool IsBareLanguageWord(string core)
        => !core.Contains(' ') && LangMap.ContainsKey(core.Trim());

    private static string CleanLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('.', ' ').Replace('_', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Trim('[', ']', '(', ')', '-', ' ');
    }

    // ─── Shared small helpers (kept local to avoid touching MediaFileResolver) ─

    private static int? TryParseBareNumericEpisode(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(stem, @"^(?:ep?)?(\d{1,4})$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
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

    private static bool PathIsUnder(string fullPath, string root)
    {
        try
        {
            var normalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(fullPath)
                .StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

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
