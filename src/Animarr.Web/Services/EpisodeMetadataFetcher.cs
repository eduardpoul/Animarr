using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Shared TMDB → <see cref="EpisodeMetadata"/> fetch, used both by the lazy
/// <c>/api/media/{id}/episodes</c> endpoint and by the identify-time warm in
/// <see cref="MetadataService"/>. Pure fetch: it builds freshly-stamped rows
/// (not attached to any DbContext) and leaves persistence + the stale check to
/// the caller, which owns the transaction.
/// </summary>
internal static class EpisodeMetadataFetcher
{
    /// <summary>Revision of the field-mapping below. Bump whenever the mapping
    /// changes so already-cached rows (stamped with an older version) are
    /// treated as stale by the endpoint and re-pulled on next view — no
    /// re-identify / language toggle needed. v2 added the English per-field
    /// fallback for episode name/overview.</summary>
    public const int CurrentVersion = 2;


    /// <summary>Fetch every season's episodes for <paramref name="item"/> from
    /// TMDB in <paramref name="lang"/> and build <see cref="EpisodeMetadata"/>
    /// rows keyed by TMDB (season, episode). Empty when the item has no TMDB id
    /// or no parsable seasons. A per-season TMDB failure is skipped, not fatal.
    ///
    /// Per-field English fallback: when <paramref name="lang"/> isn't English,
    /// episode names/overviews are often missing — TMDB returns a localized
    /// placeholder name ("Эпизод 12") and an empty overview. We pull the English
    /// season once and fill the gaps field-by-field (mirrors the title-level
    /// English fallback in <see cref="MetadataService"/>), so a localized title
    /// that genuinely exists is kept but a placeholder/empty one is replaced with
    /// the real English value.</summary>
    public static async Task<List<EpisodeMetadata>> FetchAsync(
        TmdbClient tmdb, MediaItem item, string lang, CancellationToken ct)
    {
        var rows = new List<EpisodeMetadata>();
        if (item.TmdbId is not int tmdbId || tmdbId <= 0) return rows;

        var now            = DateTime.UtcNow;
        var needsEnglish   = !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

        foreach (var seasonNumber in ParseSeasonNumbers(item.SeasonsJson))
        {
            TmdbSeasonDetail? detail;
            try { detail = await tmdb.GetSeasonDetailAsync(tmdbId, seasonNumber, language: lang, ct: ct); }
            catch { detail = null; }
            if (detail?.Episodes is null) continue;

            // English season (once per season) for the per-field fallback.
            Dictionary<int, TmdbEpisode>? en = null;
            if (needsEnglish)
            {
                try
                {
                    var enDetail = await tmdb.GetSeasonDetailAsync(tmdbId, seasonNumber, language: "en", ct: ct);
                    en = enDetail?.Episodes?
                        .GroupBy(e => e.EpisodeNumber)
                        .ToDictionary(g => g.Key, g => g.First());
                }
                catch { en = null; }
            }

            foreach (var ep in detail.Episodes)
            {
                TmdbEpisode? enEp = null;
                en?.TryGetValue(ep.EpisodeNumber, out enEp);

                // Title: keep a real localized name; otherwise the real English
                // name; otherwise null → the UI shows its "Episode N" fallback.
                var title = !IsPlaceholderName(ep.Name) ? ep.Name
                          : (enEp is not null && !IsPlaceholderName(enEp.Name)) ? enEp.Name
                          : null;
                // Overview: localized if present, else English.
                var overview = !string.IsNullOrWhiteSpace(ep.Overview) ? ep.Overview
                             : !string.IsNullOrWhiteSpace(enEp?.Overview) ? enEp!.Overview
                             : null;
                // Language-independent fields — fill from English if the localized
                // payload happened to omit them.
                var airDate = !string.IsNullOrWhiteSpace(ep.AirDate) ? ep.AirDate : enEp?.AirDate;
                var rating  = ep.VoteAverage > 0 ? ep.VoteAverage
                            : enEp is { VoteAverage: > 0 } ? enEp.VoteAverage
                            : (double?)null;
                var runtime = ep.Runtime ?? enEp?.Runtime;

                rows.Add(new EpisodeMetadata
                {
                    MediaItemId  = item.Id,
                    Season       = seasonNumber,
                    Episode      = ep.EpisodeNumber,
                    Title        = string.IsNullOrWhiteSpace(title)   ? null : title,
                    Overview     = string.IsNullOrWhiteSpace(overview) ? null : overview,
                    AirDate      = string.IsNullOrWhiteSpace(airDate)  ? null : airDate,
                    Rating       = rating,
                    RuntimeMin   = runtime,
                    Language     = lang,
                    FetchedAtUtc = now,
                    Version      = CurrentVersion,
                });
            }
        }
        return rows;
    }

    /// <summary>True when an episode name is missing or is TMDB's auto-generated
    /// placeholder for an untranslated title — "Episode 12" and its localized
    /// forms (ru "Эпизод/Серия", uk "Серія/Епізод", de "Folge", es "Episodio",
    /// fr "Épisode"). Those carry no information, so we treat them as "no title"
    /// and fall back to English.</summary>
    private static bool IsPlaceholderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(
            name.Trim(),
            @"^(episode|эпизод|серия|серія|епізод|folge|episodio|épisode|episódio)\s*\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>TMDB season numbers stored in <c>MediaItem.SeasonsJson</c> (a JSON
    /// array of <c>{number,episodeCount,…}</c>). Returns distinct, ordered numbers;
    /// empty on null/malformed input (movies, unidentified titles). Tolerant of
    /// camel/Pascal key casing so it survives either serialization.</summary>
    public static int[] ParseSeasonNumbers(string? seasonsJson)
    {
        if (string.IsNullOrWhiteSpace(seasonsJson)) return Array.Empty<int>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(seasonsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Array.Empty<int>();
            var nums = new SortedSet<int>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if ((el.TryGetProperty("number", out var n) || el.TryGetProperty("Number", out n))
                    && n.TryGetInt32(out var v) && v >= 0)
                    nums.Add(v);
            }
            return nums.ToArray();
        }
        catch { return Array.Empty<int>(); }
    }
}
