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
    private readonly AppSettings _settings = appOptions.Value;

    // ─── Regex for detecting season from folder name ─────────────────────────

    [GeneratedRegex(@"(?i)(?:season|s|сезон|serie[s]?)\s*0*(?<s>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonWordRegex();

    [GeneratedRegex(@"(?i)\bpart\s+0*(?<s>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();

    [GeneratedRegex(@"(?i)\bs0*(?<s>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SCodeRegex();

    // ─── FileKind ─────────────────────────────────────────────────────────────

    public FileKind DetermineFileKind(string extension)
    {
        var ext = extension.ToLowerInvariant();
        if (_settings.VideoExtensions.Contains(ext)) return FileKind.Video;
        if (_settings.SubtitleExtensions.Contains(ext)) return FileKind.Subtitle;
        if (_settings.AudioExtensions.Contains(ext)) return FileKind.Audio;
        if (_settings.ImageExtensions.Contains(ext)) return FileKind.Image;
        return FileKind.Unknown;
    }

    // ─── Ignore rules ─────────────────────────────────────────────────────────

    public bool IsIgnored(string fileName, IEnumerable<IgnoreRule> rules)
    {
        var lower = fileName.ToLowerInvariant();
        foreach (var rule in rules)
        {
            if (MatchesGlob(lower, rule.Mask.ToLowerInvariant()))
                return true;
        }
        return false;
    }

    // Glob matching for filename masks: * = any chars, ? = single char.
    private static bool MatchesGlob(string name, string mask)
    {
        if (mask == "*") return true;

        // Convert glob to regex: escape special chars, then map wildcards.
        var regexPattern = "^" + Regex.Escape(mask)
            .Replace(@"\*", ".*")   // * → any sequence
            .Replace(@"\?", ".")    // ? → any single char
            + "$";
        return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase);
    }

    // ─── Parse filename ───────────────────────────────────────────────────────

    public ParseResult ParseFileName(string fileName, IEnumerable<RenamePattern> patterns)
    {
        // Patterns are ordered by Priority ascending before being passed in.
        foreach (var p in patterns.OrderBy(x => x.Priority))
        {
            if (string.IsNullOrWhiteSpace(p.Pattern)) continue;

            Regex rx;
            try { rx = new Regex(p.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500)); }
            catch (Exception ex)
            {
                // L-7: surface invalid patterns instead of silently swallowing.
                logger.LogWarning(ex, "Skipping pattern «{Name}» (id={Id}): invalid regex `{Pattern}`",
                    p.Name, p.Id, p.Pattern);
                continue;
            }

            var m = rx.Match(fileName);
            if (!m.Success) continue;

            int? season = null;
            if (m.Groups["season"].Success && int.TryParse(m.Groups["season"].Value, out var s))
                season = s;

            if (!m.Groups["episode"].Success) continue;
            if (!int.TryParse(m.Groups["episode"].Value, out var episode)) continue;

            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            var isThumb = nameWithoutExt.EndsWith("-thumb") || nameWithoutExt.EndsWith("_thumb") || nameWithoutExt.EndsWith(".thumb");

            return new ParseResult(true, season, episode, isThumb);
        }

        // Fallback: filename without extension is a pure integer → treat as episode number.
        // e.g. "1.mp4", "01.mkv", "12.mp4" are already-named episode files.
        var fnLower = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var thumbOnly = fnLower.EndsWith("-thumb") || fnLower.EndsWith("_thumb");

        // Fallback for bare-number thumb images: "1-thumb.jpg", "01_thumb.jpg" → episode=1, isThumb=true
        var thumbNumMatch = System.Text.RegularExpressions.Regex.Match(fnLower, @"^0*(\d+)[_\-]thumb$");
        if (thumbNumMatch.Success && int.TryParse(thumbNumMatch.Groups[1].Value, out var thumbEp) && thumbEp > 0)
            return new ParseResult(true, null, thumbEp, true);

        if (int.TryParse(fnLower, out var bareEp) && bareEp > 0)
            return new ParseResult(true, null, bareEp, false);

        return new ParseResult(false, null, 0, thumbOnly);
    }

    // ─── Detect season from folder path ──────────────────────────────────────

    /// <summary>
    /// M-10: walks up the directory tree from <paramref name="folderPath"/> looking
    /// for a season marker. Stops at <paramref name="rootPath"/> (the FolderWatcher
    /// root) if provided, or after <c>maxDepth</c> levels otherwise.
    /// </summary>
    public int? DetectSeasonFromPath(string folderPath, string? rootPath = null, int maxDepth = 5)
    {
        var dir = new DirectoryInfo(folderPath);
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

        for (int i = 0; i < maxDepth && dir != null; i++, dir = dir.Parent!)
        {
            var name = dir.Name;

            var m = SeasonWordRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sw)) return sw;

            m = SCodeRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sc)) return sc;

            m = PartRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sp)) return sp;

            // Stop once we reach the FolderWatcher root — don't leak into parents above it.
            if (normalRoot != null)
            {
                var dirFull = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(dirFull, normalRoot, StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }

        return null;
    }

    // ─── Build target name ────────────────────────────────────────────────────

    public string? BuildTargetName(ParseResult parse, int? seasonFromPath, FileKind kind, string extension,
        string? episodeName = null)
    {
        var ext = extension.ToLowerInvariant();

        if (kind == FileKind.Image)
        {
            if (!parse.IsThumb) return null; // not an episode thumb — skip

            // Need episode number for thumb
            if (!parse.IsMatched || parse.Episode <= 0) return null;

            var ep = parse.Episode.ToString("D2");
            return parse.Season.HasValue
                ? $"S{parse.Season.Value:D2}E{ep}-thumb{ext}"
                : $"{ep}-thumb{ext}";
        }

        if (kind is FileKind.Video or FileKind.Subtitle)
        {
            if (!parse.IsMatched || parse.Episode <= 0) return null;

            var effectiveSeason = parse.Season ?? seasonFromPath;
            var ep = parse.Episode.ToString("D2");

            // Phase 1.3: optionally append episode name from TMDB metadata.
            var suffix = "";
            if (!string.IsNullOrWhiteSpace(episodeName))
            {
                var safe = SanitizeForFileName(episodeName);
                if (!string.IsNullOrWhiteSpace(safe))
                    suffix = $" - {safe}";
            }

            return effectiveSeason.HasValue
                ? $"S{effectiveSeason.Value:D2}E{ep}{suffix}{ext}"
                : $"{ep}{suffix}{ext}";
        }

        return null;
    }

    private static string SanitizeForFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? ' ' : c).ToArray();
        var cleaned = new string(chars).Trim().Trim('.');
        // Collapse multiple spaces.
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
    }

    // ─── Evaluate single file (for preview) ───────────────────────────────────

    public RenamePreviewItem EvaluateFile(
        string filePath,
        IEnumerable<RenamePattern> patterns,
        IEnumerable<IgnoreRule> ignoreRules,
        FolderType folderType = FolderType.Auto,
        bool isSection = false,
        string? folderRoot = null,
        IReadOnlyDictionary<(int s, int ep), string>? episodeNames = null)
    {
        var item = new RenamePreviewItem { OriginalPath = filePath };

        var fileName = Path.GetFileName(filePath);
        var ext = Path.GetExtension(filePath);
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;

        // 1. Check ignore rules
        if (IsIgnored(fileName, ignoreRules))
        {
            item.Status = PreviewStatus.WillSkip;
            item.Reason = "Matches ignore rule";
            item.IsSelected = false;
            return item;
        }

        // 2. Determine file type
        var kind = DetermineFileKind(ext);
        if (kind == FileKind.Unknown)
        {
            item.Status = PreviewStatus.WillSkip;
            item.Reason = "Unsupported file type";
            item.IsSelected = false;
            return item;
        }

        // ─── Movie-specific rename ────────────────────────────────────────────
        if (folderType == FolderType.Movie)
        {
            // Images inside movie folders are never thumbs — skip them
            if (kind == FileKind.Image)
            {
                item.Status = PreviewStatus.WillSkip;
                item.Reason = "Image file in movie folder — skipped";
                item.IsSelected = false;
                return item;
            }

            if (kind is FileKind.Video or FileKind.Subtitle)
            {
                var normRoot = folderRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normDir  = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var atRoot   = string.IsNullOrEmpty(normRoot) ||
                               string.Equals(normDir, normRoot, StringComparison.OrdinalIgnoreCase);

                string movieTitle;
                bool appendYear = false;
                int year = 0;

                // H-1: extract the year via a dedicated regex, not by abusing the
                // episode-pattern engine. Real-world filenames like
                // "Inception.2010.1080p.BluRay.mkv" don't match any rename pattern,
                // so the previous reliance on ParseFileName returning Episode∈[1900,2099]
                // silently dropped the year.
                var nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                var fnYearMatch = Regex.Match(nameNoExt, @"(?<![0-9])(19\d{2}|20\d{2})(?![0-9])");
                if (fnYearMatch.Success && int.TryParse(fnYearMatch.Value, out var fnYear))
                {
                    year = fnYear;
                    appendYear = true;
                }
                movieTitle = CleanMovieTitle(nameNoExt, year);

                if (string.IsNullOrWhiteSpace(movieTitle) && !atRoot)
                {
                    // Filename was generic → try cleaning the parent sub-folder name
                    var rawFolder = Path.GetFileName(normDir) ?? string.Empty;
                    var fyMatch = Regex.Match(rawFolder, @"\b(19\d\d|20\d\d)\b");
                    if (fyMatch.Success) { year = int.Parse(fyMatch.Value); appendYear = true; }
                    movieTitle = CleanMovieTitle(rawFolder, year);
                }

                if (string.IsNullOrWhiteSpace(movieTitle))
                {
                    item.Status = PreviewStatus.WillSkip;
                    item.Reason = "Could not determine movie title";
                    item.IsSelected = false;
                    return item;
                }

                var newMovieName = appendYear
                    ? $"{movieTitle} ({year}){ext.ToLowerInvariant()}"
                    : $"{movieTitle}{ext.ToLowerInvariant()}";

                if (string.Equals(fileName, newMovieName, StringComparison.OrdinalIgnoreCase))
                {
                    item.Status = PreviewStatus.AlreadyCorrect;
                    item.NewName = newMovieName;
                    item.NewPath = filePath;
                    item.Reason = "Already correctly named";
                    item.IsSelected = false;
                    return item;
                }

                item.Status = PreviewStatus.WillRename;
                item.NewName = newMovieName;
                item.NewPath = Path.Combine(dir, newMovieName);
                return item;
            }
        }

        // ─── Series / Auto rename (existing logic) ────────────────────────────

        // 3. Parse filename
        var parse2 = ParseFileName(fileName, patterns);

        // 4. Detect season from folder path if not found in filename
        int? seasonFromPath = null;
        if (!parse2.Season.HasValue)
            seasonFromPath = DetectSeasonFromPath(dir, folderRoot);

        // Phase 1.3: look up the episode title for the resolved (season, episode) if a map was supplied.
        string? episodeName = null;
        if (episodeNames is not null && parse2.IsMatched && parse2.Episode > 0)
        {
            var s = parse2.Season ?? seasonFromPath ?? 1;
            episodeNames.TryGetValue((s, parse2.Episode), out episodeName);
        }

        // 5. Build target name
        var newName = BuildTargetName(parse2, seasonFromPath, kind, ext, episodeName);
        if (newName is null)
        {
            item.Status = PreviewStatus.WillSkip;
            item.Reason = parse2.IsMatched
                ? "Could not build target name"
                : "No pattern matched — episode number not found";
            item.IsSelected = false;
            return item;
        }

        // 6. Check if already correct
        if (string.Equals(fileName, newName, StringComparison.OrdinalIgnoreCase))
        {
            item.Status = PreviewStatus.AlreadyCorrect;
            item.NewName = newName;
            item.NewPath = filePath;
            item.Reason = "Already correctly named";
            item.IsSelected = false;
            return item;
        }

        item.Status = PreviewStatus.WillRename;
        item.NewName = newName;
        item.NewPath = Path.Combine(dir, newName);
        return item;
    }

    // ─── Clean movie title from raw filename ─────────────────────────────────

    private static string CleanMovieTitle(string nameWithoutExt, int year)
    {
        // For dotted/underscore release names, truncate at first quality/technical token.
        // e.g. "Star.Wars.3.1080.Bublik"      → "Star Wars 3"
        //      "Inception.2010.1080p.BluRay"  → "Inception" (year stops the scan)
        if (nameWithoutExt.Contains('.') || nameWithoutExt.Contains('_'))
        {
            char sep = nameWithoutExt.Contains('.') ? '.' : '_';
            var parts = nameWithoutExt.Split(sep);
            var stopRx = new Regex(
                @"^\d{3,4}[ip]?$|^(?:BluRay|BDRip|BDRemux|WEB\.?DL|WEBRip|DVDRip|HDTV|Remux|PROPER|REPACK|HDR10?|x26[45]|HEVC|H26[45]|AVC|XviD|DivX|AAC|AC3|EAC3|DTS|TrueHD|Atmos|FLAC|MP3|UHD|SDR|10bit|8bit)$",
                RegexOptions.IgnoreCase);
            var titleParts = new List<string>();
            foreach (var p in parts)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (stopRx.IsMatch(p)) break;
                if (year > 0 && p == year.ToString()) break;
                titleParts.Add(p);
            }
            if (titleParts.Count > 0)
                return string.Join(" ", titleParts).Trim();
        }

        // Fallback: regex-based cleaning for space-separated or mixed filenames
        var s = nameWithoutExt;

        // Remove the year itself (with any surrounding brackets / parens / dots)
        if (year > 0)
            s = Regex.Replace(s, $@"[\s._\[(]?{Regex.Escape(year.ToString())}[\s._\])]?", " ");

        // Remove resolution tags
        s = Regex.Replace(s, @"\b(?:2160|1440|1080|720|480|360)[ip]?\b", " ", RegexOptions.IgnoreCase);

        // Remove source / release type
        s = Regex.Replace(s, @"\b(?:BluRay|BDRip|BDRemux|WEB[-.]?DL|WEBRip|DVDRip|HDTV|Remux|PROPER|REPACK|HDR(?:10)?)\b", " ", RegexOptions.IgnoreCase);

        // Remove video codec
        s = Regex.Replace(s, @"\b(?:x26[45]|HEVC|H\.?26[45]|AVC|XviD|DivX)\b", " ", RegexOptions.IgnoreCase);

        // Remove audio codec
        s = Regex.Replace(s, @"\b(?:AAC|AC3|EAC3|DTS(?:-HD)?|TrueHD|Atmos|FLAC|MP3)\b", " ", RegexOptions.IgnoreCase);

        // Remove any remaining [...] or (...) tokens
        s = Regex.Replace(s, @"\[.*?\]", " ");
        s = Regex.Replace(s, @"\(.*?\)", " ");

        // Remove trailing group name after a dash
        s = Regex.Replace(s, @"\s*-\s*\w+$", " ");

        // Dots and underscores → spaces
        s = s.Replace('.', ' ').Replace('_', ' ');

        // Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }
}
