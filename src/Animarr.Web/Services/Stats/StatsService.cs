using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Stats;

/// <summary>
/// Builds the personal stats dashboard for one user. Two data sources:
/// WatchStates answer "what" (watched episodes/titles, by genre/studio/type,
/// estimated minutes), the WatchEvents journal answers "when" (heatmap,
/// streaks, monthly hours). Everything is scoped to the requesting user.
/// </summary>
public sealed class StatsService(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int HeatmapDays = 182;   // ~26 weeks — a half-year wall

    public async Task<StatsDto> BuildAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ── "what" — watched WatchStates joined to their items ──────────────
        var watched = await (
            from w in db.WatchStates.AsNoTracking()
            join m in db.MediaItems.AsNoTracking() on w.MediaItemId equals m.Id
            where w.UserId == userId && w.IsWatched
            select new WatchRow(
                m.Id, m.MediaType, m.Runtime, w.RuntimeMs, m.Studio,
                m.GenresLocalizedJson, m.GenresJson, m.Title, m.EnglishTitle,
                m.OriginalTitle, m.CjkTitle, m.PosterPath))
            .ToListAsync(ct);

        // Per-title rollup: episodes watched + estimated minutes.
        var byTitle = watched
            .GroupBy(r => r.Id)
            .Select(g =>
            {
                var r0 = g.First();
                return new TitleAgg(
                    r0, Episodes: g.Count(), Minutes: g.Sum(MinutesOf));
            })
            .ToList();

        var summaryEpisodes = watched.Count;
        var summaryTitles   = byTitle.Count;
        var totalMinutes    = byTitle.Sum(t => t.Minutes);

        // Top genres (localized labels if present) — by minutes, tie by titles.
        var genreAcc = new Dictionary<string, (int Titles, long Minutes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in byTitle)
            foreach (var g in Genres(t.Row))
            {
                var cur = genreAcc.GetValueOrDefault(g);
                genreAcc[g] = (cur.Titles + 1, cur.Minutes + t.Minutes);
            }
        var topGenres = genreAcc
            .OrderByDescending(kv => kv.Value.Minutes).ThenByDescending(kv => kv.Value.Titles)
            .Take(12)
            .Select(kv => new StatBar(kv.Key, kv.Value.Titles, kv.Value.Minutes))
            .ToList();

        // Top studios — by minutes.
        var topStudios = byTitle
            .Where(t => !string.IsNullOrWhiteSpace(t.Row.Studio))
            .GroupBy(t => t.Row.Studio!.Trim())
            .Select(g => new StatBar(g.Key, g.Count(), g.Sum(t => t.Minutes)))
            .OrderByDescending(s => s.Minutes).ThenByDescending(s => s.Titles)
            .Take(10)
            .ToList();

        // By media type.
        var byType = byTitle
            .GroupBy(t => TypeKey(t.Row.MediaType))
            .Select(g => new TypeSlice(g.Key, g.Count(), g.Sum(t => t.Episodes), g.Sum(t => t.Minutes)))
            .OrderByDescending(s => s.Minutes)
            .ToList();

        // Most-watched titles.
        var topTitles = byTitle
            .OrderByDescending(t => t.Minutes).ThenByDescending(t => t.Episodes)
            .Take(10)
            .Select(t => new TopTitleStat(
                t.Row.Id, DisplayTitle(t.Row),
                string.IsNullOrEmpty(t.Row.PosterPath) ? null
                    : $"/api/image?path={Uri.EscapeDataString(t.Row.PosterPath)}",
                t.Episodes, t.Minutes))
            .ToList();

        // ── "when" — WatchEvents journal ────────────────────────────────────
        var events = await db.WatchEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new { e.Date, e.SecondsWatched })
            .ToListAsync(ct);

        var perDay = events
            .GroupBy(e => e.Date.Date)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.SecondsWatched) / 60);

        var heatmap = perDay
            .OrderBy(kv => kv.Key)
            .Select(kv => new HeatCell(kv.Key.ToString("yyyy-MM-dd"), kv.Value))
            .ToList();

        var byMonth = events
            .GroupBy(e => new DateTime(e.Date.Year, e.Date.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g => new MonthStat(
                g.Key.ToString("yyyy-MM"), g.Sum(e => e.SecondsWatched) / 60, g.Count()))
            .ToList();

        var (current, longest) = Streaks(perDay.Keys);

        var summary = new StatsSummary(
            summaryEpisodes, summaryTitles, totalMinutes,
            ActiveDays: perDay.Count, current, longest);

        return new StatsDto(summary, topGenres, topStudios, byType, topTitles, heatmap, byMonth);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    /// <summary>Estimated minutes for one watched episode: the actually-played
    /// runtime where playback recorded it, else the title's metadata runtime,
    /// else a per-type default so id-poor donghua still count.</summary>
    private static long MinutesOf(WatchRow r) =>
        r.RuntimeMs is > 0 ? r.RuntimeMs.Value / 60000
        : r.Runtime  is > 0 ? r.Runtime.Value
        : r.MediaType == MediaItemType.Movie ? 100 : 24;

    private static IEnumerable<string> Genres(WatchRow r)
    {
        var json = !string.IsNullOrWhiteSpace(r.GenresLocalizedJson) ? r.GenresLocalizedJson : r.GenresJson;
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return (JsonSerializer.Deserialize<string[]>(json, JsonOpts) ?? []).Where(g => !string.IsNullOrWhiteSpace(g)); }
        catch { return []; }
    }

    private static string TypeKey(MediaItemType t) => t switch
    {
        MediaItemType.Anime       => "anime",
        MediaItemType.Movie       => "movie",
        MediaItemType.Multserials => "cartoon",
        _                         => "series",
    };

    /// <summary>Display title — the human-readable name, avoiding a raw CJK
    /// original when a Latin title exists (mirrors MediaTitles.DisplayTitle
    /// without pulling the whole entity).</summary>
    private static string DisplayTitle(WatchRow r)
    {
        if (!string.IsNullOrWhiteSpace(r.Title) && !IsMostlyCjk(r.Title)) return r.Title;
        if (!string.IsNullOrWhiteSpace(r.EnglishTitle)) return r.EnglishTitle!;
        if (!string.IsNullOrWhiteSpace(r.Title)) return r.Title;
        return r.OriginalTitle ?? r.CjkTitle ?? "—";
    }

    private static bool IsMostlyCjk(string s)
    {
        int cjk = 0, letters = 0;
        foreach (var c in s)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (c is (>= '぀' and <= 'ヿ') or (>= '㐀' and <= '鿿')
                  or (>= '가' and <= '힯')) cjk++;
        }
        return letters > 0 && cjk * 2 > letters;
    }

    /// <summary>Current streak (consecutive active days ending today or
    /// yesterday) and the longest run ever, from the set of active days.</summary>
    private static (int Current, int Longest) Streaks(IEnumerable<DateTime> days)
    {
        var set = days.Select(d => d.Date).ToHashSet();
        if (set.Count == 0) return (0, 0);

        int longest = 0;
        foreach (var d in set)
        {
            if (set.Contains(d.AddDays(-1))) continue;   // not a run start
            int len = 1;
            var n = d.AddDays(1);
            while (set.Contains(n)) { len++; n = n.AddDays(1); }
            longest = Math.Max(longest, len);
        }

        // Current: walk back from today; allow it to have started yesterday.
        var today = DateTime.UtcNow.Date;
        var anchor = set.Contains(today) ? today : set.Contains(today.AddDays(-1)) ? today.AddDays(-1) : (DateTime?)null;
        int current = 0;
        if (anchor is DateTime a)
        {
            current = 1;
            var p = a.AddDays(-1);
            while (set.Contains(p)) { current++; p = p.AddDays(-1); }
        }
        return (current, longest);
    }

    private sealed record WatchRow(
        Guid Id, MediaItemType MediaType, int? Runtime, long? RuntimeMs, string? Studio,
        string? GenresLocalizedJson, string? GenresJson,
        string Title, string? EnglishTitle, string? OriginalTitle, string? CjkTitle, string? PosterPath);

    private sealed record TitleAgg(WatchRow Row, int Episodes, long Minutes);
}
