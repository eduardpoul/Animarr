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

    /// <summary>Non-numbered "season 0" buckets — a folder whose whole name is
    /// Specials/Extras/OVA/… is treated as season 0 (the Specials group), so its
    /// files surface as-is instead of vanishing with season=null.</summary>
    [GeneratedRegex(@"(?i)^(?:specials?|extras?|ova|ovas|oad|ncop|nced|sp|sps|pv|pvs|bonus)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialsFolderRegex();

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
                var dirFull = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(dirFull, normalRoot, StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }

        return null;
    }
}
