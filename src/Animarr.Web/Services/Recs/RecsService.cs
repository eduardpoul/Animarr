using System.Collections.Concurrent;
using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Recs;

/// <summary>
/// Heuristic recommendation engine — iteration 1, no LLM in the loop yet.
///
/// Both rails are LOCAL-FIRST WITH EXTERNAL BACKFILL: the slot budget is
/// filled from the user's own library first (clickable, watchable now), and
/// only the remainder comes from TMDB — so a big library barely sees external
/// cards while a small/fully-watched one still gets a full rail. The
/// per-user <c>UserPreferences.RecsScope</c> ("library") turns the backfill
/// off entirely.
///
///   • "More like this": genre/tag overlap + same-type bonus among identified
///     titles; backfill = TMDB recommendations+similar for THIS title.
///   • "For you": profile = watch-seconds per genre from the user's
///     WatchStates; candidates = untouched titles scored by profile overlap;
///     backfill = TMDB related of the user's top-watched titles.
///
/// Exclusions everywhere: the title itself, already-in-library matches (for
/// external), the user's dismissals and watchlist entries.
/// </summary>
public sealed class RecsService(
    IDbContextFactory<AppDbContext> dbFactory,
    TmdbClient tmdb,
    ILogger<RecsService> logger)
{
    private const int SimilarBudget = 8;
    private const int ForYouBudget  = 12;

    // TMDB related feeds barely move day to day — cache per (id, kind) so a
    // detail page reopen or a Home reload costs zero external calls.
    private static readonly ConcurrentDictionary<(int Id, bool Movie), (DateTime At, List<TmdbSearchResult> Items)> _relatedCache = new();
    private static readonly TimeSpan RelatedTtl = TimeSpan.FromHours(24);

    // ── "More like this" ────────────────────────────────────────────────────

    public async Task<List<RecCardDto>> GetSimilarAsync(Guid mediaItemId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return [];

        var all = await LoadCatalogAsync(db, ct);
        var (dismissed, dismissedTmdb, wlTmdb) = await LoadExclusionsAsync(db, userId, ct);

        var itemLabels = LabelsOf(item);
        var scored = new List<(MediaItem M, double Score, string? SharedTag)>();
        foreach (var m in all)
        {
            if (m.Id == item.Id || dismissed.Contains(m.Id)) continue;
            var labels = LabelsOf(m);
            var shared = itemLabels.Intersect(labels, StringComparer.OrdinalIgnoreCase).ToList();
            if (shared.Count == 0) continue;
            var score = shared.Count * 2.0
                        + (m.MediaType == item.MediaType ? 0.6 : 0)
                        + (m.Rating ?? 0) * 0.1;
            scored.Add((m, score, PickDisplayLabel(m, shared)));
        }

        var cards = scored
            .OrderByDescending(s => s.Score)
            .Take(SimilarBudget)
            .Select(s => LocalCard(s.M, reasonTag: s.SharedTag))
            .ToList();

        // External backfill for the remaining slots.
        var need = SimilarBudget - cards.Count;
        if (need > 0 && item.TmdbId is int tmdbId
            && await ScopeAllowsExternalAsync(db, userId, ct))
        {
            var libTmdb = LibraryTmdbIds(all);
            var related = await RelatedCachedAsync(tmdbId, item.MediaType == MediaItemType.Movie, ct);
            cards.AddRange(related
                .Where(r => !libTmdb.Contains(r.Id) && !dismissedTmdb.Contains(r.Id) && !wlTmdb.Contains(r.Id))
                .Take(need)
                .Select(r => ExternalCard(r, item.MediaType == MediaItemType.Movie, reasonTitle: null)));
        }
        return cards;
    }

    // ── "For you" ───────────────────────────────────────────────────────────

    public async Task<List<RecCardDto>> GetForYouAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await LoadCatalogAsync(db, ct);
        if (all.Count == 0) return [];
        var (dismissed, dismissedTmdb, wlTmdb) = await LoadExclusionsAsync(db, userId, ct);

        // Engagement per title: watch seconds + "touched at all".
        var states = await db.WatchStates.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.MediaItemId)
            .Select(g => new
            {
                MediaItemId = g.Key,
                Seconds     = g.Sum(w => w.TotalWatchTimeSec),
                Touched     = g.Any(w => w.IsWatched || (w.ProgressMs ?? 0) > 0),
            })
            .ToListAsync(ct);
        var byId      = all.ToDictionary(m => m.Id);
        var touched   = states.Where(s => s.Touched).Select(s => s.MediaItemId).ToHashSet();
        var topWatched = states
            .Where(s => s.Seconds > 0 && byId.ContainsKey(s.MediaItemId))
            .OrderByDescending(s => s.Seconds)
            .Select(s => byId[s.MediaItemId])
            .Take(5)
            .ToList();

        // Genre profile: seconds spent per canonical genre/tag label.
        var genreWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in states.Where(x => x.Seconds > 0))
        {
            if (!byId.TryGetValue(s.MediaItemId, out var m)) continue;
            foreach (var g in LabelsOf(m))
                genreWeight[g] = genreWeight.GetValueOrDefault(g) + s.Seconds;
        }
        var maxWeight = genreWeight.Count > 0 ? genreWeight.Values.Max() : 0;

        // Local candidates: untouched, not dismissed. Cold start (no history)
        // falls back to top-rated so the rail isn't empty on day one.
        // Reason anchors: each card picks its OWN best-overlapping title from
        // the user's top-watched — a rail where every reason reads "because
        // you watch Bleach" is monotone even when true.
        var anchors = topWatched.Take(4)
            .Select(a => (Item: a, Labels: LabelsOf(a)))
            .ToList();
        var locals = new List<(MediaItem M, double Score, string? ReasonTitle, string? ReasonTag)>();
        foreach (var m in all)
        {
            if (touched.Contains(m.Id) || dismissed.Contains(m.Id)) continue;
            var labels = LabelsOf(m);
            double score = (m.Rating ?? 0) * 0.15;
            string? reasonTitle = null, reasonTag = null;
            if (maxWeight > 0)
            {
                double overlap = 0;
                foreach (var l in labels) overlap += genreWeight.GetValueOrDefault(l) / maxWeight;
                if (overlap <= 0) continue;   // with history, only suggest things the profile supports
                score += overlap * 2.0;
                // Tie-break by a per-candidate hash: with a genre-homogeneous
                // library every candidate ties across anchors, and a plain
                // "first wins" cites the single most-watched show on every
                // card. The hash spreads ties across the top-watched titles
                // deterministically (stable between reloads).
                var best = anchors
                    .Select(a => (a.Item, Shared: labels.Intersect(a.Labels, StringComparer.OrdinalIgnoreCase).Count()))
                    .Where(x => x.Shared > 0)
                    .OrderByDescending(x => x.Shared)
                    .ThenBy(x => (m.Id.GetHashCode() ^ x.Item.Id.GetHashCode()) & 0x7fffffff)
                    .FirstOrDefault();
                if (best.Item is not null)
                    reasonTitle = best.Item.Title;
                else
                    reasonTag = PickDisplayLabel(m,
                        labels.OrderByDescending(l => genreWeight.GetValueOrDefault(l)).Take(1).ToList());
            }
            locals.Add((m, score, reasonTitle, reasonTag));
        }

        var cards = locals
            .OrderByDescending(s => s.Score)
            .Take(ForYouBudget)
            .Select(s => LocalCard(s.M, s.ReasonTitle, s.ReasonTag))
            .ToList();

        // External backfill: TMDB related of the top-watched titles.
        var need = ForYouBudget - cards.Count;
        if (need > 0 && topWatched.Count > 0 && await ScopeAllowsExternalAsync(db, userId, ct))
        {
            var libTmdb = LibraryTmdbIds(all);
            var seen = new HashSet<int>();
            var pool = new List<(TmdbSearchResult R, bool Movie, string Seed)>();
            foreach (var seed in topWatched.Take(3).Where(s => s.TmdbId is int))
            {
                var isMovie = seed.MediaType == MediaItemType.Movie;
                foreach (var r in await RelatedCachedAsync(seed.TmdbId!.Value, isMovie, ct))
                    if (seen.Add(r.Id)) pool.Add((r, isMovie, seed.Title));
            }
            cards.AddRange(pool
                .Where(p => !libTmdb.Contains(p.R.Id) && !dismissedTmdb.Contains(p.R.Id) && !wlTmdb.Contains(p.R.Id))
                .OrderByDescending(p => p.R.VoteCount)
                .Take(need)
                .Select(p => ExternalCard(p.R, p.Movie, reasonTitle: p.Seed)));
        }
        return cards;
    }

    // ── shared bits ─────────────────────────────────────────────────────────

    private static Task<List<MediaItem>> LoadCatalogAsync(AppDbContext db, CancellationToken ct)
        => db.MediaItems.AsNoTracking()
            .Where(m => m.IdentificationStatus == IdentificationStatus.Identified ||
                        m.IdentificationStatus == IdentificationStatus.Manual)
            .ToListAsync(ct);

    private async Task<(HashSet<Guid> Local, HashSet<int> Tmdb, HashSet<int> WatchlistTmdb)>
        LoadExclusionsAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var dism = await db.RecDismissals.AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => new { d.MediaItemId, d.TmdbId })
            .ToListAsync(ct);
        var wl = await db.WatchlistItems.AsNoTracking()
            .Where(w => w.UserId == userId && w.TmdbId != null)
            .Select(w => w.TmdbId!.Value)
            .ToListAsync(ct);
        return (
            dism.Where(d => d.MediaItemId is not null).Select(d => d.MediaItemId!.Value).ToHashSet(),
            dism.Where(d => d.TmdbId is not null).Select(d => d.TmdbId!.Value).ToHashSet(),
            wl.ToHashSet());
    }

    private async Task<bool> ScopeAllowsExternalAsync(AppDbContext db, Guid userId, CancellationToken ct)
    {
        var scope = await db.UserPreferences.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.RecsScope)
            .FirstOrDefaultAsync(ct);
        return scope != "library";
    }

    private async Task<List<TmdbSearchResult>> RelatedCachedAsync(int tmdbId, bool isMovie, CancellationToken ct)
    {
        var key = (tmdbId, isMovie);
        if (_relatedCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < RelatedTtl)
            return hit.Items;
        try
        {
            var items = await tmdb.GetRelatedAsync(tmdbId, isMovie, ct);
            _relatedCache[key] = (DateTime.UtcNow, items);
            return items;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Recs] TMDB related fetch failed for {Id}", tmdbId);
            return [];
        }
    }

    /// <summary>Canonical genre + tag labels of a title (the language-independent
    /// EN forms — scoring keys off these).</summary>
    private static List<string> LabelsOf(MediaItem m)
    {
        var list = new List<string>();
        list.AddRange(ParseJsonArray(m.GenresJson));
        list.AddRange(ParseJsonArray(m.TagsJson));
        return list;
    }

    /// <summary>Pick the reason label to SHOW: prefer the localized form of the
    /// first shared canonical genre when the item carries one.</summary>
    private static string? PickDisplayLabel(MediaItem m, List<string> shared)
    {
        if (shared.Count == 0) return null;
        var canon = ParseJsonArray(m.GenresJson);
        var local = ParseJsonArray(m.GenresLocalizedJson);
        var idx = canon.FindIndex(g => string.Equals(g, shared[0], StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx < local.Count ? local[idx] : shared[0];
    }

    private static List<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static HashSet<int> LibraryTmdbIds(List<MediaItem> all)
        => all.Where(m => m.TmdbId is not null).Select(m => m.TmdbId!.Value).ToHashSet();

    private static RecCardDto LocalCard(MediaItem m, string? reasonTitle = null, string? reasonTag = null) => new(
        m.Id, m.TmdbId, m.Title, m.Year,
        string.IsNullOrEmpty(m.PosterPath) ? null : $"/api/image?path={Uri.EscapeDataString(m.PosterPath)}",
        m.Rating,
        m.MediaType == MediaItemType.Movie ? "movie" : "tv",
        reasonTitle, reasonTag,
        InLibrary: true);

    private static RecCardDto ExternalCard(TmdbSearchResult r, bool seedIsMovie, string? reasonTitle)
    {
        // /similar of a TV title returns TV entries (no media_type field) —
        // fall back to the seed's kind when the result doesn't say.
        var isMovie = r.MediaType == "movie" || (r.MediaType is null && seedIsMovie);
        return new(
            null, r.Id, r.DisplayTitle, r.Year,
            string.IsNullOrEmpty(r.PosterPath) ? null : TmdbClient.PosterUrl(r.PosterPath, "w342"),
            r.VoteAverage > 0 ? r.VoteAverage : null,
            isMovie ? "movie" : "tv",
            reasonTitle, null,
            InLibrary: false);
    }
}
