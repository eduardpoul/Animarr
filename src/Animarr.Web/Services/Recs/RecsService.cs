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
    /// <summary>Candidate pool size the For-you rail rotates over.</summary>
    private const int PoolSize      = 30;
    /// <summary>Recency half-life for the core taste profile.</summary>
    private const double HalfLifeDays = 14;
    private static readonly TimeSpan PoolTtl     = TimeSpan.FromHours(3);
    private static readonly TimeSpan RotateEvery = TimeSpan.FromHours(2);

    // TMDB related feeds barely move day to day — cache per (id, kind) so a
    // detail page reopen or a Home reload costs zero external calls.
    private static readonly ConcurrentDictionary<(int Id, bool Movie), (DateTime At, List<TmdbSearchResult> Items)> _relatedCache = new();
    private static readonly TimeSpan RelatedTtl = TimeSpan.FromHours(24);
    /// <summary>Per-user For-you pools (see <see cref="BuildForYouPoolAsync"/>).</summary>
    private static readonly ConcurrentDictionary<Guid, (DateTime At, List<RecCardDto> Pool)> _forYouPool = new();

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

    /// <summary>Serve the "For you" rail: a ~30-card pool is (re)built lazily
    /// every <see cref="PoolTtl"/> and cached per user; the visible window of
    /// <see cref="ForYouBudget"/> rotates every <see cref="RotateEvery"/> so
    /// the rail doesn't flicker between Home visits but does feel alive over
    /// the day. Dismissals / watchlist adds apply at serve time — instantly,
    /// without waiting for a rebuild.</summary>
    public async Task<List<RecCardDto>> GetForYouAsync(Guid userId, CancellationToken ct = default)
    {
        List<RecCardDto> pool;
        if (_forYouPool.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < PoolTtl)
        {
            pool = hit.Pool;
        }
        else
        {
            pool = await BuildForYouPoolAsync(userId, ct);
            _forYouPool[userId] = (DateTime.UtcNow, pool);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (dismissed, dismissedTmdb, wlTmdb) = await LoadExclusionsAsync(db, userId, ct);
        var visible = pool
            .Where(c => c.MediaItemId is not Guid mid || !dismissed.Contains(mid))
            .Where(c => c.MediaItemId is not null ||
                        (c.TmdbId is int t && !dismissedTmdb.Contains(t) && !wlTmdb.Contains(t)))
            .ToList();
        if (visible.Count <= ForYouBudget) return visible;

        // Deterministic rotation: same window for RotateEvery, then it slides.
        var epoch  = DateTime.UtcNow.Ticks / RotateEvery.Ticks;
        var offset = (int)((uint)HashCode.Combine(userId, epoch) % visible.Count);
        return visible.Skip(offset).Concat(visible.Take(offset)).Take(ForYouBudget).ToList();
    }

    /// <summary>Build the layered candidate pool:
    ///   • CORE (~60%) — the CURRENT taste: per-title engagement decays with a
    ///     14-day half-life (day-bucketed WatchEvents, falling back to the
    ///     LastSeenAt-decayed aggregate for pre-journal history); externals are
    ///     picked by CONSENSUS VOTING across the recent seeds' TMDB related
    ///     lists (a title two recent shows both point at beats a merely
    ///     popular one).
    ///   • MEMORY (~25%) — the OLD taste: seeds/genres from all-time history
    ///     that the current core doesn't cover, so a phase of watching donghua
    ///     doesn't erase the sci-fi you loved before.
    ///   • EXPLORE (~15%) — high-rated titles OUTSIDE both profiles, an
    ///     anti-filter-bubble wildcard.
    /// The layers are interleaved (memory/explore every ~4th slot).</summary>
    private async Task<List<RecCardDto>> BuildForYouPoolAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await LoadCatalogAsync(db, ct);
        if (all.Count == 0) return [];
        var byId = all.ToDictionary(m => m.Id);
        var now  = DateTime.UtcNow;

        // ── engagement: recency-decayed (core) + flat all-time (memory) ─────
        var states = await db.WatchStates.AsNoTracking()
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.MediaItemId)
            .Select(g => new
            {
                MediaItemId = g.Key,
                Seconds     = g.Sum(w => w.TotalWatchTimeSec),
                LastSeen    = g.Max(w => w.LastSeenAt),
                Touched     = g.Any(w => w.IsWatched || (w.ProgressMs ?? 0) > 0),
            })
            .ToListAsync(ct);
        var events = await db.WatchEvents.AsNoTracking()
            .Where(e => e.UserId == userId)
            .Select(e => new { e.MediaItemId, e.Date, e.SecondsWatched })
            .ToListAsync(ct);

        double DecayDays(double days) => Math.Pow(0.5, Math.Max(0, days) / HalfLifeDays);
        var recentWeight = events
            .GroupBy(e => e.MediaItemId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.SecondsWatched * DecayDays((now - e.Date).TotalDays)));
        foreach (var s in states.Where(x => x.Seconds > 0 && x.LastSeen is not null))
        {
            // Pre-journal fallback: the aggregate decayed by its last activity.
            var fb = s.Seconds * DecayDays((now - s.LastSeen!.Value).TotalDays);
            if (fb > recentWeight.GetValueOrDefault(s.MediaItemId))
                recentWeight[s.MediaItemId] = fb;
        }
        var flatWeight = states.Where(s => s.Seconds > 0)
            .ToDictionary(s => s.MediaItemId, s => (double)s.Seconds);
        var touched = states.Where(s => s.Touched).Select(s => s.MediaItemId).ToHashSet();

        List<MediaItem> SeedsOf(Dictionary<Guid, double> w, HashSet<Guid>? not = null, int take = 3) => w
            .Where(kv => kv.Value > 0 && byId.ContainsKey(kv.Key) && (not is null || !not.Contains(kv.Key)))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => byId[kv.Key])
            .Take(take)
            .ToList();
        var recentSeeds   = SeedsOf(recentWeight, take: 3);
        var recentSeedIds = recentSeeds.Select(s => s.Id).ToHashSet();
        var memorySeeds   = SeedsOf(flatWeight, not: recentSeedIds, take: 2);

        Dictionary<string, double> GenreProfile(Dictionary<Guid, double> w)
        {
            var p = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, weight) in w)
            {
                if (weight <= 0 || !byId.TryGetValue(id, out var m)) continue;
                foreach (var l in LabelsOf(m)) p[l] = p.GetValueOrDefault(l) + weight;
            }
            return p;
        }
        var coreProfile   = GenreProfile(recentWeight);
        var flatProfile   = GenreProfile(flatWeight);
        var coreTopGenres = coreProfile.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── local candidate scoring against a profile ────────────────────────
        List<(MediaItem M, double Score, string? RTitle, string? RTag)> ScoreLocals(
            Dictionary<string, double> profile, List<MediaItem> anchorSeeds, Func<MediaItem, List<string>, bool>? gate)
        {
            var maxW = profile.Count > 0 ? profile.Values.Max() : 0;
            var anchors = anchorSeeds.Select(a => (Item: a, Labels: LabelsOf(a))).ToList();
            var list = new List<(MediaItem, double, string?, string?)>();
            foreach (var m in all)
            {
                if (touched.Contains(m.Id)) continue;
                var labels = LabelsOf(m);
                if (gate is not null && !gate(m, labels)) continue;
                double score = (m.Rating ?? 0) * 0.15;
                string? rTitle = null, rTag = null;
                if (maxW > 0)
                {
                    double overlap = 0;
                    foreach (var l in labels) overlap += profile.GetValueOrDefault(l) / maxW;
                    if (overlap <= 0) continue;
                    score += overlap * 2.0;
                    var best = anchors
                        .Select(a => (a.Item, Shared: labels.Intersect(a.Labels, StringComparer.OrdinalIgnoreCase).Count()))
                        .Where(x => x.Shared > 0)
                        .OrderByDescending(x => x.Shared)
                        .ThenBy(x => (m.Id.GetHashCode() ^ x.Item.Id.GetHashCode()) & 0x7fffffff)
                        .FirstOrDefault();
                    if (best.Item is not null) rTitle = best.Item.Title;
                    else rTag = PickDisplayLabel(m, labels.OrderByDescending(l => profile.GetValueOrDefault(l)).Take(1).ToList());
                }
                list.Add((m, score, rTitle, rTag));
            }
            return list;
        }

        var coreBudget    = (int)(PoolSize * 0.6);
        var memoryBudget  = (int)(PoolSize * 0.25);
        var exploreBudget = PoolSize - coreBudget - memoryBudget;

        // CORE — current-taste locals (cold start: profile empty → top-rated).
        var core = ScoreLocals(coreProfile, recentSeeds, gate: null)
            .OrderByDescending(x => x.Score).Take(coreBudget)
            .Select(x => LocalCard(x.M, x.RTitle, x.RTag)).ToList();
        var used = core.Where(c => c.MediaItemId is not null).Select(c => c.MediaItemId!.Value).ToHashSet();

        // MEMORY — old-taste locals whose strongest genre is OUTSIDE the
        // current core's top genres (that difference is the whole point).
        var memory = ScoreLocals(flatProfile, memorySeeds,
                gate: (m, labels) => !used.Contains(m.Id) && !labels.Any(coreTopGenres.Contains))
            .OrderByDescending(x => x.Score).Take(memoryBudget)
            .Select(x => LocalCard(x.M, x.RTitle, x.RTag)).ToList();
        foreach (var c in memory.Where(c => c.MediaItemId is not null)) used.Add(c.MediaItemId!.Value);

        // EXPLORE — high-rated untouched titles outside BOTH profiles.
        var knownGenres = coreProfile.Keys.Concat(flatProfile.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explore = all
            .Where(m => !touched.Contains(m.Id) && !used.Contains(m.Id)
                        && (knownGenres.Count == 0 || !LabelsOf(m).Any(knownGenres.Contains)))
            .OrderByDescending(m => m.Rating ?? 0)
            .Take(exploreBudget)
            .Select(m => LocalCard(m))
            .ToList();

        // ── external backfill by consensus voting ────────────────────────────
        if (await ScopeAllowsExternalAsync(db, userId, ct))
        {
            var libTmdb = LibraryTmdbIds(all);
            async Task<List<RecCardDto>> VoteAsync(List<MediaItem> seeds, Dictionary<Guid, double> weights, int take)
            {
                if (take <= 0 || seeds.Count == 0) return [];
                var maxSeedW = seeds.Max(s => weights.GetValueOrDefault(s.Id));
                var votes = new Dictionary<int, (double Score, TmdbSearchResult R, bool Movie, string Seed)>();
                foreach (var seed in seeds.Where(s => s.TmdbId is int))
                {
                    var isMovie = seed.MediaType == MediaItemType.Movie;
                    var seedW = maxSeedW > 0 ? weights.GetValueOrDefault(seed.Id) / maxSeedW : 1;
                    var related = await RelatedCachedAsync(seed.TmdbId!.Value, isMovie, ct);
                    for (var i = 0; i < related.Count; i++)
                    {
                        var r = related[i];
                        if (libTmdb.Contains(r.Id)) continue;
                        // Vote = seed recency-weight + a small list-position bonus;
                        // a title present in SEVERAL seeds' lists accumulates.
                        var v = seedW * (1.0 + 0.2 * (related.Count - i) / (double)related.Count);
                        if (votes.TryGetValue(r.Id, out var cur))
                            votes[r.Id] = (cur.Score + v, cur.R, cur.Movie, cur.Seed);
                        else
                            votes[r.Id] = (v, r, isMovie, seed.Title);
                    }
                }
                return votes.Values
                    .OrderByDescending(v => v.Score)
                    .ThenByDescending(v => v.R.VoteCount)
                    .Take(take)
                    .Select(v => ExternalCard(v.R, v.Movie, reasonTitle: v.Seed))
                    .ToList();
            }

            core.AddRange(await VoteAsync(recentSeeds, recentWeight, coreBudget - core.Count));
            memory.AddRange(await VoteAsync(memorySeeds, flatWeight, memoryBudget - memory.Count));
        }

        // ── interleave: core is the spine, memory/explore every ~4th slot ────
        var extras = new Queue<RecCardDto>(memory.Concat(explore));
        var spine  = new Queue<RecCardDto>(core);
        var pool   = new List<RecCardDto>(PoolSize);
        while (pool.Count < PoolSize && (spine.Count > 0 || extras.Count > 0))
        {
            var takeExtra = (pool.Count % 4 == 3 && extras.Count > 0) || spine.Count == 0;
            pool.Add(takeExtra ? extras.Dequeue() : spine.Dequeue());
        }
        return pool;
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
