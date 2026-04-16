using System.Text.RegularExpressions;
using Animarr.Web.Configuration;
using Animarr.Web.Data.Models;
using Microsoft.Extensions.Options;

namespace Animarr.Web.Services;

public partial class PatternMatchService(IOptions<AppSettings> appOptions) : IPatternMatchService
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
            catch { continue; } // Bad regex in DB — skip

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

    public int? DetectSeasonFromPath(string folderPath)
    {
        // Walk up max 2 levels: immediate folder, then its parent
        var dir = new DirectoryInfo(folderPath);

        for (int i = 0; i < 2 && dir != null; i++, dir = dir.Parent!)
        {
            var name = dir.Name;

            var m = SeasonWordRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sw)) return sw;

            m = SCodeRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sc)) return sc;

            m = PartRegex().Match(name);
            if (m.Success && int.TryParse(m.Groups["s"].Value, out var sp)) return sp;
        }

        return null;
    }

    // ─── Build target name ────────────────────────────────────────────────────

    public string? BuildTargetName(ParseResult parse, int? seasonFromPath, FileKind kind, string extension)
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

            return effectiveSeason.HasValue
                ? $"S{effectiveSeason.Value:D2}E{ep}{ext}"
                : $"{ep}{ext}";
        }

        return null;
    }

    // ─── Evaluate single file (for preview) ───────────────────────────────────

    public RenamePreviewItem EvaluateFile(
        string filePath,
        IEnumerable<RenamePattern> patterns,
        IEnumerable<IgnoreRule> ignoreRules,
        FolderType folderType = FolderType.Auto,
        bool isSection = false,
        string? folderRoot = null)
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

                // Always derive title from the filename — most reliable for torrent releases
                // (folder names can be quality tags like "1080" or buried sub-paths).
                // Fall back to the parent sub-folder name only if the filename gives nothing
                // (e.g. a generic "movie.mkv" inside a properly named folder).
                var fnParse = ParseFileName(fileName, patterns);
                if (fnParse.IsMatched && fnParse.Episode >= 1900 && fnParse.Episode <= 2099)
                {
                    year = fnParse.Episode;
                    appendYear = true;
                }
                movieTitle = CleanMovieTitle(Path.GetFileNameWithoutExtension(fileName), year);

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
            seasonFromPath = DetectSeasonFromPath(dir);

        // 5. Build target name
        var newName = BuildTargetName(parse2, seasonFromPath, kind, ext);
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
