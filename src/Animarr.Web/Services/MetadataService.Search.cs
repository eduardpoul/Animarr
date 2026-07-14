using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using HueHash = Animarr.Shared.HueHash;
using LanguageNameMap = Animarr.Shared.LanguageNameMap;

namespace Animarr.Web.Services;

// Candidate search across TMDB TV/Movie, MAL and IMDb → scored MetadataCandidate list.
public partial class MetadataService
{
    // ── Candidate gathering ───────────────────────────────────────────────────

    private async Task<List<MetadataCandidate>> GatherCandidatesAsync(
        string searchTitle,
        FolderType typeHint,
        string? tmdbKey,
        string? malKey,
        int? folderYear,
        Action<string>? log,
        CancellationToken ct)
    {
        // Load source order config: [{id:"tmdb_tv",enabled:true},{id:"tmdb_movie",enabled:true}]
        var sourceOrderJson = await appConfig.GetAsync(AppConfigKeys.SearchSourceOrder, ct);
        var sourceOrder = ParseSourceOrder(sourceOrderJson);

        var tasks = new List<Task<List<MetadataCandidate>>>();
        var sourceWeights = new Dictionary<string, double>();

        for (int i = 0; i < sourceOrder.Count; i++)
        {
            var src = sourceOrder[i];
            if (!src.Enabled) continue;
            // weight: first source = 1.0, each subsequent = -0.05
            double weight = 1.0 - i * 0.05;
            sourceWeights[src.Id] = weight;

            if (src.Id == "tmdb_tv" && typeHint != FolderType.Movie)
            {
                if (!string.IsNullOrWhiteSpace(tmdbKey))
                {
                    log?.Invoke($"[TMDB] Searching TV for \"{searchTitle}\"");
                    tasks.Add(SearchTmdbTvCandidatesAsync(searchTitle, folderYear, ct));
                }
                else log?.Invoke("[TMDB TV] Skipped — API key not configured.");
            }
            else if (src.Id == "tmdb_movie" && typeHint != FolderType.Series)
            {
                if (!string.IsNullOrWhiteSpace(tmdbKey))
                {
                    log?.Invoke($"[TMDB] Searching Movies for \"{searchTitle}\"");
                    tasks.Add(SearchTmdbMovieCandidatesAsync(searchTitle, folderYear, ct));
                }
                else log?.Invoke("[TMDB Movie] Skipped — API key not configured.");
            }
            else if (src.Id == "mal")
            {
                if (!string.IsNullOrWhiteSpace(malKey))
                {
                    log?.Invoke($"[MAL] Searching for \"{searchTitle}\"");
                    tasks.Add(SearchMalCandidatesAsync(searchTitle, folderYear, ct));
                }
                else log?.Invoke("[MAL] Skipped — client ID not configured.");
            }
            else if (src.Id == "imdb_search")
            {
                log?.Invoke($"[IMDb] Searching for \"{searchTitle}\"");
                tasks.Add(SearchImdbCandidatesAsync(searchTitle, folderYear, ct, log));
            }
        }

        if (tasks.Count == 0) return [];

        var results = await Task.WhenAll(tasks);
        var all = results.SelectMany(r => r).ToList();

        // Apply source weight to score
        if (sourceWeights.Count > 0)
        {
            all = all.Select(c =>
            {
                double w = sourceWeights.TryGetValue(c.Source, out var wv) ? wv : 1.0;
                return c with { Score = c.Score * w };
            }).ToList();
        }

        // Cross-validation: when two different sources independently return the same
        // work (matched on normalised title + year), boost every candidate in that
        // group by +0.25 — agreement between independent indexes is a strong signal.
        if (all.Count > 1)
        {
            var groups = all
                .GroupBy(c => (Key: NormaliseTitleForMatch(c.Title), c.Year))
                .Where(g => g.Select(c => c.Source).Distinct().Count() >= 2)
                .ToList();

            if (groups.Count > 0)
            {
                var boosted = new HashSet<MetadataCandidate>(ReferenceEqualityComparer.Instance);
                foreach (var g in groups)
                    foreach (var c in g)
                        boosted.Add(c);
                all = all.Select(c => boosted.Contains(c)
                    ? c with { Score = c.Score + 0.25 }
                    : c).ToList();
                log?.Invoke($"[Cross-validation] {groups.Count} cross-source match group(s) boosted (+0.25)");
            }
        }

        log?.Invoke($"[Search] {all.Count} total candidates");
        return all;
    }

    /// <summary>Strips non-alphanumeric characters and lower-cases — for cross-source matching.</summary>
    private static string NormaliseTitleForMatch(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var c in title)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>True when ≥70% of the letter characters are basic ASCII — used to decide
    /// whether to switch to the LLM's english_title for searching.</summary>
    private static bool IsMostlyAscii(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        int letters = 0, ascii = 0;
        foreach (var c in s)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c < 128) ascii++;
        }
        return letters == 0 || ascii * 10 >= letters * 7;
    }

    private static List<(string Id, bool Enabled)> ParseSourceOrder(string? json)
    {
        var defaults = new List<(string, bool)> { ("tmdb_tv", true), ("tmdb_movie", true), ("mal", false), ("imdb_search", true) };
        if (string.IsNullOrWhiteSpace(json)) return defaults;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<SearchSourceEntry>>(json);
            if (parsed is { Count: > 0 })
                return parsed.Select(e => (e.Id, e.Enabled)).ToList();
        }
        catch { /* ignore malformed config */ }
        return defaults;
    }

    private sealed record SearchSourceEntry(string Id, bool Enabled);

    // TMDB poster thumb size for the NeedsReview UI (≈ 60×90 rendered).
    private const string TmdbThumbBase = "https://image.tmdb.org/t/p/w154";

    private async Task<List<MetadataCandidate>> SearchTmdbTvCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct)
    {
        var results = await tmdb.SearchTvAsync(searchTitle, ct);
        return results.Take(5).Select(r => new MetadataCandidate(
            Source:        "tmdb_tv",
            Id:            r.Id,
            Title:         r.DisplayTitle,
            OriginalTitle: r.OriginalName,
            Year:          r.Year,
            Overview:      r.Overview,
            IsTv:          true,
            Score:         ScoreResult(r.DisplayTitle, r.OriginalName, r.Year, searchTitle, folderYear),
            PosterUrl:     !string.IsNullOrEmpty(r.PosterPath) ? TmdbThumbBase + r.PosterPath : null
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchTmdbMovieCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct)
    {
        var results = await tmdb.SearchMovieAsync(searchTitle, ct);
        return results.Take(5).Select(r => new MetadataCandidate(
            Source:        "tmdb_movie",
            Id:            r.Id,
            Title:         r.DisplayTitle,
            OriginalTitle: r.OriginalTitle,
            Year:          r.Year,
            Overview:      r.Overview,
            IsTv:          false,
            Score:         ScoreResult(r.DisplayTitle, r.OriginalTitle, r.Year, searchTitle, folderYear),
            PosterUrl:     !string.IsNullOrEmpty(r.PosterPath) ? TmdbThumbBase + r.PosterPath : null
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchMalCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct)
    {
        var results = await mal.SearchAsync(searchTitle, 5, ct);
        return results.Select(r => new MetadataCandidate(
            Source:        "mal",
            Id:            r.Id,
            Title:         r.EnglishTitle,
            OriginalTitle: r.AlternativeTitles?.Ja ?? r.Title,
            Year:          r.Year,
            Overview:      r.Synopsis,
            IsTv:          true,
            Score:         ScoreResult(r.EnglishTitle, r.AlternativeTitles?.Ja ?? r.Title, r.Year, searchTitle, folderYear),
            PosterUrl:     r.PosterUrl
        )).ToList();
    }

    private async Task<List<MetadataCandidate>> SearchImdbCandidatesAsync(
        string searchTitle, int? folderYear, CancellationToken ct, Action<string>? log = null)
    {
        var results = await imdbSearch.SearchTitlesAsync(searchTitle, 5, ct);
        if (results.Count == 0)
            log?.Invoke($"[IMDb] No results for \"{searchTitle}\" — ensure the title is in English.");
        // Note: IMDb's PrimaryImage is only on the detail endpoint, not the
        // search response — we leave PosterUrl null and rely on the external
        // link button in the NeedsReview UI to let the user preview manually.
        return results.Select(r =>
        {
            bool isTv = r.Type is "tvSeries" or "tvMiniSeries" or "tvSpecial" or "tvMovie";
            return new MetadataCandidate(
                Source:        "imdb_search",
                Id:            0,
                Title:         r.PrimaryTitle,
                OriginalTitle: r.OriginalTitle,
                Year:          r.StartYear,
                Overview:      null,
                IsTv:          isTv,
                Score:         ScoreResult(r.PrimaryTitle, r.OriginalTitle, r.StartYear, searchTitle, folderYear),
                StringId:      r.Id);
        }).ToList();
    }

    private static double ScoreResult(string title, string? altTitle, int? year, string searchTitle, int? folderYear)
    {
        double sim = StringSimilarity(title, searchTitle);
        if (!string.IsNullOrEmpty(altTitle))
            sim = Math.Max(sim, StringSimilarity(altTitle, searchTitle) * 0.95);
        double score = sim * 2.0;

        if (year.HasValue && folderYear.HasValue)
        {
            if (year == folderYear) score += 0.4;
            else if (Math.Abs(year.Value - folderYear.Value) <= 1) score += 0.15;
        }
        return score;
    }

}
