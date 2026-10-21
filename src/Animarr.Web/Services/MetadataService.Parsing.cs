using System.Text.Json;
using System.Text.RegularExpressions;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using HueHash = Animarr.Shared.HueHash;
using LanguageNameMap = Animarr.Shared.LanguageNameMap;

namespace Animarr.Web.Services;

// Static filename/title parsing + fuzzy string-similarity helpers.
public partial class MetadataService
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Title/year parsing regexes. These used to be built inside the methods on
    // every call — `noise` with RegexOptions.Compiled, i.e. Reflection.Emit + JIT
    // of a ~90-branch alternation (~26 ms, ~190 KB) per identification, thrown
    // away right after. Source-generated once instead (~0.7 µs per call).

    [GeneratedRegex(@"[\[\(]\d{4}[\]\)]?\s*$")]
    private static partial Regex BracketYearSuffixRegex();

    [GeneratedRegex(@"\s*-?\s*S\d{1,2}(E\d{1,2})?(\s*-\s*S?\d{1,2}E\d{1,2})?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeSuffixRegex();

    [GeneratedRegex(@"\s+(Season|Series|Part)\s*\d+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonWordSuffixRegex();

    [GeneratedRegex(@"\s+\d+(st|nd|rd|th)\s+Season.*$", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSeasonSuffixRegex();

    [GeneratedRegex(@"[\[\(](1080p|720p|480p|2160p|4K|UHD|BluRay|BDRip|WEB-DL|WEBRip|HEVC|x265|x264|AVC|AAC|DTS|FLAC|HDR|SDR).*", RegexOptions.IgnoreCase)]
    private static partial Regex BracketReleaseTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"^(1080p|720p|480p|2160p|4K|UHD|BluRay|Blu-Ray|BDRip|BRRip|DVDRip|WEB-?DL|WEBRip|HEVC|x265|x264|H\.?265|H\.?264|AVC|AAC|AC3|DTS(?:-HD)?|TrueHD|FLAC|HDR|HDR10\+?|SDR|10bit|8bit|REMUX|Atmos|2CH|6CH|MA|Dolby|Hybrid|Extended|Director'?s?Cut|UNRATED|Theatrical|REPACK|PROPER|MULTi|DUAL|RUS|ENG|JAP|CHS|CHT|EN|RU|JA|ZH|KO|FR|DE|ES|IT|SUB|SUBS|DUB|DUBBED|FANSUB|YIFY|YTS|RARBG|FGT|EVO|CMRG|GalaxyRG|TGx|d3g|Telesync|TS|CAM|HDCAM|TC|TBS|VC-?1|10-?bit|HQ|HDTV|SDTV|BDR|BDRemux|REMASTERED|MAR-CAS|MeGusta|EPSiLON|SPARKS|NTb|FraMeSToR|DEFLATE|tigole|UTR|d3g)$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTokenRegex();

    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearTokenRegex();

    [GeneratedRegex(@"[\[\(](\d{4})[\]\)]")]
    private static partial Regex BracketedYearRegex();

    [GeneratedRegex(@"[.\s\-]((?:19|20)\d{2})(?:[.\s]|$)")]
    private static partial Regex TrailingYearRegex();

    private static string ParseTitleFromPath(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));

        // 1. Bracketed year — strip the bracket cluster only (year still extracted by ExtractYearFromPath)
        name = BracketYearSuffixRegex().Replace(name, "").Trim();

        // 2. Season/episode markers (TV files that slipped into a Movies section)
        name = SeasonEpisodeSuffixRegex().Replace(name, "").Trim();
        name = SeasonWordSuffixRegex().Replace(name, "").Trim();
        name = OrdinalSeasonSuffixRegex().Replace(name, "").Trim();

        // 3. Bracketed release-tag cluster (e.g. "(1080p BluRay x265)") — strip from the marker on
        name = BracketReleaseTagRegex().Replace(name, "").Trim();

        // 4. Normalise separators — dots/underscores → spaces, then collapse runs.
        name = name.Replace('.', ' ').Replace('_', ' ');
        name = WhitespaceRunRegex().Replace(name, " ").Trim();

        // 5. Release-noise word/year stripping. Many torrent-style filenames have the title +
        //    a dot-separated release-tag run: "Соник 2 в кино 2022 UHD Blu-Ray Remux 2160p"
        //    becomes "Соник 2 в кино" after this pass. Walk from the right, drop each token
        //    that looks like noise; stop at the first token that doesn't match.
        //    The earlier `\s(19|20)\d{2}\s*$` rule only caught the year if it was already
        //    the trailing token after the regexes above — for files with year + release tags
        //    after it, it missed.
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0)
        {
            var last = tokens[^1];
            if (NoiseTokenRegex().IsMatch(last) || YearTokenRegex().IsMatch(last))
            {
                tokens.RemoveAt(tokens.Count - 1);
                continue;
            }
            break;
        }
        name = string.Join(' ', tokens).Trim();

        // Trailing punctuation that survived the strip.
        name = name.Trim('-', '.', ' ', '_');
        return name;
    }

    private static int? ExtractYearFromPath(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, '/'));
        // Bracketed year: (2024) or [2024]
        var m = BracketedYearRegex().Match(name);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var y) && y is >= 1900 and <= 2099)
            return y;
        // Dot/space/dash-separated trailing year: Movie.Name.2024 or Movie Name - 2024
        var m2 = TrailingYearRegex().Match(name);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var y2) && y2 is >= 1900 and <= 2099)
            return y2;
        return null;
    }

    private static double StringSimilarity(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.8;

        static HashSet<string> Bigrams(string s) =>
            [.. Enumerable.Range(0, Math.Max(0, s.Length - 1)).Select(i => s.Substring(i, 2))];

        var ba = Bigrams(a);
        var bb = Bigrams(b);
        if (ba.Count == 0 || bb.Count == 0) return 0;
        double intersection = ba.Intersect(bb).Count();
        return 2.0 * intersection / (ba.Count + bb.Count);
    }
}
