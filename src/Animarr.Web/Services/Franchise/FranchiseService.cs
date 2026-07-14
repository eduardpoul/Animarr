using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Franchise;

/// <summary>
/// Franchise graph: builds it (a bounded BFS over AniList relations, one
/// request per node) and reads it back as a watch-order rail.
///
/// The graph is stored in AniList-id space (nodes are snapshots — most
/// franchise members are NOT in the library); matching onto MediaItems
/// happens at read time via AniListId/MalId, with consecutive nodes of the
/// same library item collapsed into one card ("S1–S3") — AniList models each
/// season as its own entry while our items span the whole TMDB series.
///
/// Watch order = release order: a topological pass over SEQUEL/PREQUEL edges
/// with (year, title) tie-breaks. Right for ~95% of franchises; the exotic
/// ones (Monogatari…) get an LLM-curated order in a later iteration.
/// </summary>
public sealed class FranchiseService(
    IDbContextFactory<AppDbContext> dbFactory,
    AniListClient aniList,
    TmdbClient tmdb,
    Segments.MalIdResolver malResolver,
    ILogger<FranchiseService> logger)
{
    private const int MaxDepth = 6;
    private const int MaxNodes = 30;

    // TMDB collections change rarely; a short process cache spares a movie its
    // detail+collection round-trip on every rail open. (movie tmdbId → collId,
    // collId → collection). No eviction needed — bounded by library size.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int?> _collectionIdCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TmdbCollectionDetail?> _collectionCache = new();

    /// <summary>Edge types the BFS FOLLOWS outward. ALTERNATIVE/CHARACTER/
    /// SUMMARY/OTHER nodes are recorded when seen but not expanded — following
    /// ALTERNATIVE recursively would swallow half of AniList on franchises
    /// like Fate.</summary>
    private static readonly HashSet<string> ExpandTypes = new(StringComparer.OrdinalIgnoreCase)
        { "SEQUEL", "PREQUEL", "PARENT", "SIDE_STORY", "SPIN_OFF" };

    /// <summary>Types stored as graph members at all (the rest — mostly
    /// CHARACTER cameos — are noise on a watch-order rail).</summary>
    private static readonly HashSet<string> KeepTypes = new(StringComparer.OrdinalIgnoreCase)
        { "SEQUEL", "PREQUEL", "PARENT", "SIDE_STORY", "SPIN_OFF", "ALTERNATIVE", "SUMMARY" };

    // ── build ────────────────────────────────────────────────────────────────

    /// <summary>Walk the franchise around one library title and persist the
    /// nodes/edges. Returns the number of nodes seen (0 = no ids / no graph).</summary>
    public async Task<int> RefreshForItemAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return 0;

        try
        {
            // Id-less title (fresh import, beyond the airing resolver's quota):
            // bridge by title now — the resolver fills item ids in place and
            // persists them. A miss still stamps below, so the 30-day rescan is
            // the retry cadence, not every page open.
            if (item.AniListId is null && item.MalId is null)
                await malResolver.ResolveAsync(item, ct);
            if (item.AniListId is null && item.MalId is null) return 0;

            var visited = new Dictionary<int, AniListClient.AniListNode>();
            var edges   = new HashSet<(int From, int To, string Type)>();
            var queue   = new Queue<(int? AniId, int? MalId, int Depth)>();
            queue.Enqueue((item.AniListId, item.MalId, 0));

            while (queue.Count > 0 && visited.Count < MaxNodes)
            {
                ct.ThrowIfCancellationRequested();
                var (aniId, malId, depth) = queue.Dequeue();
                if (aniId is int known && visited.ContainsKey(known)) continue;

                var rel = await aniList.GetRelationsAsync(aniId, malId, ct);
                if (rel is null) continue;
                if (visited.ContainsKey(rel.Node.AniListId)) continue;
                visited[rel.Node.AniListId] = rel.Node;

                // First fetch resolves the item's own AniList id when we only
                // had MAL — persist it for every other AniList-keyed feature.
                item.AniListId ??= rel.Node.AniListId;

                foreach (var (type, node) in rel.Edges)
                {
                    if (!KeepTypes.Contains(type)) continue;
                    edges.Add((rel.Node.AniListId, node.AniListId, type.ToUpperInvariant()));
                    if (!visited.ContainsKey(node.AniListId))
                    {
                        // Record the neighbour snapshot even when we won't
                        // expand it (ALTERNATIVE/SUMMARY leaves).
                        visited.TryAdd(node.AniListId, node);
                        if (ExpandTypes.Contains(type) && depth + 1 <= MaxDepth)
                        {
                            visited.Remove(node.AniListId);   // re-fetch for ITS edges
                            queue.Enqueue((node.AniListId, node.IdMal, depth + 1));
                        }
                    }
                }
            }

            if (visited.Count > 0)
            {
                var ids = visited.Keys.ToList();
                var existingNodes = await db.FranchiseNodes
                    .Where(n => ids.Contains(n.AniListId))
                    .ToDictionaryAsync(n => n.AniListId, ct);
                foreach (var (id, n) in visited)
                {
                    if (existingNodes.TryGetValue(id, out var row))
                    {
                        row.MalId = n.IdMal;  row.Title = n.Title;   row.Format = n.Format;
                        row.Year  = n.Year;   row.Episodes = n.Episodes;
                        row.CoverUrl = n.CoverUrl; row.Status = n.Status;
                        row.FetchedAtUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        db.FranchiseNodes.Add(new FranchiseNode
                        {
                            Id = Guid.NewGuid(), AniListId = id, MalId = n.IdMal,
                            Title = n.Title, Format = n.Format, Year = n.Year,
                            Episodes = n.Episodes, CoverUrl = n.CoverUrl, Status = n.Status,
                            FetchedAtUtc = DateTime.UtcNow,
                        });
                    }
                }

                var fromIds = edges.Select(e => e.From).Distinct().ToList();
                var existingEdges = (await db.FranchiseEdges
                        .Where(e => fromIds.Contains(e.FromAniListId))
                        .ToListAsync(ct))
                    .Select(e => (e.FromAniListId, e.ToAniListId, e.RelationType))
                    .ToHashSet();
                foreach (var e in edges.Where(e => !existingEdges.Contains(e)))
                    db.FranchiseEdges.Add(new FranchiseEdge
                    {
                        Id = Guid.NewGuid(),
                        FromAniListId = e.From, ToAniListId = e.To, RelationType = e.Type,
                    });
            }

            if (visited.Count > 1)
                logger.LogInformation("[Franchise] {Title}: graph of {Nodes} node(s)", item.Title, visited.Count);
            return visited.Count;
        }
        finally
        {
            // Stamp even on failure so one broken title can't wedge the queue.
            item.LastRelationsCheckAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // ── read ─────────────────────────────────────────────────────────────────

    /// <summary>The watch-order rail for one title, or null when nothing useful
    /// is known (fewer than two members). Merges two sources — the AniList
    /// relations graph (anime) and the TMDB collection (live-action / film
    /// franchises AniList doesn't cover) — so either can contribute members the
    /// other misses.</summary>
    public async Task<FranchiseDto?> GetFranchiseAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return null;

        var aniCards  = await BuildAniListCardsAsync(db, item, mediaItemId, ct);
        var tmdbCards = await BuildTmdbCollectionCardsAsync(db, item, mediaItemId, ct);

        var cards = MergeFranchiseCards(aniCards, tmdbCards);
        if (cards.Count <= 1) return null;

        // Franchise title = the first card's title (root of the AniList
        // release-order, or earliest film in a TMDB collection).
        var title = cards[0].Title;
        return new FranchiseDto(
            title,
            cards.Count,
            cards.Count(c => c.Watched),
            cards.Count(c => c.InLibrary),
            cards);
    }

    /// <summary>Cards from the stored AniList relations graph (empty when the
    /// title has no AniList id / no graph). Same logic as before: connected
    /// component → release-order toposort → library match → season collapse.</summary>
    private async Task<List<FranchiseCardDto>> BuildAniListCardsAsync(
        AppDbContext db, MediaItem item, Guid mediaItemId, CancellationToken ct)
    {
        if (item.AniListId is not int rootId) return new();

        // Connected component around the root (undirected reachability).
        var component = new HashSet<int> { rootId };
        while (true)
        {
            var frontier = component.ToList();
            var grown = await db.FranchiseEdges.AsNoTracking()
                .Where(e => frontier.Contains(e.FromAniListId) || frontier.Contains(e.ToAniListId))
                .Select(e => new { e.FromAniListId, e.ToAniListId })
                .ToListAsync(ct);
            var before = component.Count;
            foreach (var e in grown) { component.Add(e.FromAniListId); component.Add(e.ToAniListId); }
            if (component.Count == before || component.Count > MaxNodes * 2) break;
        }
        if (component.Count <= 1) return new();

        var nodes = await db.FranchiseNodes.AsNoTracking()
            .Where(n => component.Contains(n.AniListId))
            .ToListAsync(ct);
        if (nodes.Count <= 1) return new();
        var edges = await db.FranchiseEdges.AsNoTracking()
            .Where(e => component.Contains(e.FromAniListId) && component.Contains(e.ToAniListId))
            .ToListAsync(ct);

        // ── release-order sort: topo over SEQUEL chains, year tie-break ─────
        var ordered = OrderNodes(nodes, edges);

        // ── match onto library items (AniListId first, MalId fallback) ──────
        var aniIds = nodes.Select(n => n.AniListId).ToList();
        var malIds = nodes.Where(n => n.MalId != null).Select(n => n.MalId!.Value).ToList();
        var libItems = await db.MediaItems.AsNoTracking()
            .Where(m => (m.AniListId != null && aniIds.Contains(m.AniListId.Value))
                     || (m.MalId != null && malIds.Contains(m.MalId.Value)))
            .ToListAsync(ct);

        var claims = new Dictionary<int, MediaItem>();
        foreach (var n in nodes)
        {
            var m = libItems.FirstOrDefault(x => x.AniListId == n.AniListId)
                 ?? libItems.FirstOrDefault(x => n.MalId != null && x.MalId == n.MalId);
            if (m is not null) claims[n.AniListId] = m;
        }
        PropagateSeasonClaims(claims, ordered, edges);
        MediaItem? MatchOf(FranchiseNode n) => claims.GetValueOrDefault(n.AniListId);

        // Watched = the matched item has at least one watched episode (any user
        // scoping is deliberately ignored here — the rail is a shared surface).
        var libIds = libItems.Select(m => m.Id).ToList();
        var watchedIds = (await db.WatchStates.AsNoTracking()
                .Where(w => libIds.Contains(w.MediaItemId) && w.IsWatched)
                .Select(w => w.MediaItemId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        // ── collapse consecutive nodes of the same library item ─────────────
        var cards = new List<FranchiseCardDto>();
        foreach (var n in ordered)
        {
            var match = MatchOf(n);
            if (match is not null && cards.Count > 0 && cards[^1].MediaItemId == match.Id)
            {
                // Same library item as the previous card — extend its span.
                var last = cards[^1];
                cards[^1] = last with
                {
                    SpanCount = last.SpanCount + 1,
                    Episodes  = (last.Episodes ?? 0) + (n.Episodes ?? 0),
                };
                continue;
            }
            cards.Add(new FranchiseCardDto(
                n.AniListId,
                match?.Id,
                match is not null ? MediaTitles.DisplayTitle(match) : n.Title,
                n.Year, n.Format, n.Episodes,
                match is not null && !string.IsNullOrEmpty(match.PosterPath)
                    ? $"/api/image?path={Uri.EscapeDataString(match.PosterPath)}"
                    : n.CoverUrl,
                RelationOf(n.AniListId, edges),
                InLibrary: match is not null,
                IsCurrent: match?.Id == mediaItemId,
                Watched:   match is not null && watchedIds.Contains(match.Id),
                SpanCount: 1));
        }
        return cards;
    }

    /// <summary>Cards from the title's TMDB movie collection (empty for
    /// non-movies, titles with no TmdbId, or films not in a collection). The
    /// franchise source for live-action / film series AniList doesn't model.
    /// Parts match the library by TmdbId; order is by release year.</summary>
    private async Task<List<FranchiseCardDto>> BuildTmdbCollectionCardsAsync(
        AppDbContext db, MediaItem item, Guid currentId, CancellationToken ct)
    {
        if (item.TmdbId is not int tmdbId || item.MediaType != MediaItemType.Movie)
            return new();   // belongs_to_collection is a movie-only TMDB concept

        var collId = await ResolveCollectionIdAsync(tmdbId, ct);
        if (collId is null) return new();
        var coll = await GetCollectionCachedAsync(collId.Value, ct);
        if (coll?.Parts is not { Count: > 1 }) return new();

        var partIds = coll.Parts.Select(p => p.Id).ToList();
        var libItems = await db.MediaItems.AsNoTracking()
            .Where(m => m.TmdbId != null && partIds.Contains(m.TmdbId.Value))
            .ToListAsync(ct);
        var libIds = libItems.Select(m => m.Id).ToList();
        var watchedIds = (await db.WatchStates.AsNoTracking()
                .Where(w => libIds.Contains(w.MediaItemId) && w.IsWatched)
                .Select(w => w.MediaItemId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var cards = new List<FranchiseCardDto>();
        foreach (var p in coll.Parts.OrderBy(p => p.Year ?? int.MaxValue))
        {
            if (p.Id <= 0 || string.IsNullOrWhiteSpace(p.Title)) continue;
            var match = libItems.FirstOrDefault(m => m.TmdbId == p.Id);
            cards.Add(new FranchiseCardDto(
                AniListId: 0,
                MediaItemId: match?.Id,
                Title: match is not null ? MediaTitles.DisplayTitle(match) : p.Title,
                Year: p.Year,
                Format: "MOVIE",
                Episodes: null,
                CoverUrl: match is not null && !string.IsNullOrEmpty(match.PosterPath)
                    ? $"/api/image?path={Uri.EscapeDataString(match.PosterPath)}"
                    : (!string.IsNullOrEmpty(p.PosterPath) ? $"https://image.tmdb.org/t/p/w342{p.PosterPath}" : null),
                Relation: null,
                InLibrary: match is not null,
                IsCurrent: match?.Id == currentId,
                Watched: match is not null && watchedIds.Contains(match.Id),
                SpanCount: 1,
                TmdbId: p.Id));
        }
        return cards;
    }

    private async Task<int?> ResolveCollectionIdAsync(int movieTmdbId, CancellationToken ct)
    {
        if (_collectionIdCache.TryGetValue(movieTmdbId, out var cached)) return cached;
        int? collId = null;
        try
        {
            var detail = await tmdb.GetMovieDetailAsync(movieTmdbId, ct: ct);
            collId = detail?.BelongsToCollection?.Id;
        }
        catch { /* leave null; cache the miss so we don't retry every open */ }
        _collectionIdCache[movieTmdbId] = collId;
        return collId;
    }

    private async Task<TmdbCollectionDetail?> GetCollectionCachedAsync(int collectionId, CancellationToken ct)
    {
        if (_collectionCache.TryGetValue(collectionId, out var cached)) return cached;
        TmdbCollectionDetail? coll = null;
        try { coll = await tmdb.GetCollectionAsync(collectionId, ct: ct); }
        catch { /* leave null */ }
        _collectionCache[collectionId] = coll;
        return coll;
    }

    /// <summary>Merge the two sources into one rail. When only one source has
    /// cards, keep it verbatim — AniList's release-order toposort and TMDB's
    /// year order are each already correct, and re-sorting AniList by year
    /// would drop the SEQUEL chain. Only the (rare) hybrid — an anime that ALSO
    /// has a TMDB movie collection — needs a common order, done by year after
    /// deduping TMDB films already present as AniList nodes.</summary>
    private static List<FranchiseCardDto> MergeFranchiseCards(
        List<FranchiseCardDto> ani, List<FranchiseCardDto> tmdb)
    {
        if (tmdb.Count == 0) return ani;
        if (ani.Count == 0) return tmdb;
        var result = new List<FranchiseCardDto>(ani);
        foreach (var t in tmdb)
            if (!result.Any(a => SameTitleYear(a, t)))
                result.Add(t);
        return result
            .OrderBy(c => c.Year ?? int.MaxValue)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SameTitleYear(FranchiseCardDto a, FranchiseCardDto b)
    {
        if (a.Year is int ay && b.Year is int by && Math.Abs(ay - by) > 1) return false;
        return NormalizeTitle(a.Title) == NormalizeTitle(b.Title)
            && NormalizeTitle(a.Title).Length > 0;
    }

    private static string NormalizeTitle(string t) =>
        new string((t ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>A TMDB series spans several AniList season-entries but its ids
    /// only pin the first one, so "Season 2" nodes would render as
    /// not-in-library. Claim TV-ish SEQUEL successors for the same item while
    /// the AniList episode sum still fits the item's TMDB episode total — the
    /// budget is what stops true successor SERIES from being swallowed
    /// (Naruto's one AniList entry already spends its 220 episodes, so
    /// Shippuuden never fits). Movies/OVA between cours are walked through
    /// without being claimed; a node exactly matched to another library item
    /// ends the chain.</summary>
    private static void PropagateSeasonClaims(
        Dictionary<int, MediaItem> claims, List<FranchiseNode> ordered, List<FranchiseEdge> edges)
    {
        var byId = ordered.ToDictionary(n => n.AniListId);
        var nextOf = new Dictionary<int, int>();
        foreach (var e in edges)
        {
            if (e.RelationType.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase))
                nextOf.TryAdd(e.FromAniListId, e.ToAniListId);
            else if (e.RelationType.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase))
                nextOf.TryAdd(e.ToAniListId, e.FromAniListId);
        }

        static bool SeasonishFormat(string? f) =>
            f is null || f.ToUpperInvariant() is "TV" or "ONA" or "TV_SHORT";

        foreach (var start in ordered.Where(n => claims.ContainsKey(n.AniListId)).ToList())
        {
            var item = claims[start.AniListId];
            if (item.MediaType == MediaItemType.Movie) continue;   // only episodic items span seasons
            if (TmdbEpisodeTotal(item) is not int cap) continue;

            var cum = start.Episodes ?? 0;
            var cur = start.AniListId;
            for (var steps = 0; steps < MaxNodes && cum < cap; steps++)
            {
                if (!nextOf.TryGetValue(cur, out var nxId) || !byId.TryGetValue(nxId, out var nx)) break;
                if (claims.TryGetValue(nxId, out var owner))
                {
                    if (!ReferenceEquals(owner, item)) break;
                    cur = nxId; continue;
                }
                if (!SeasonishFormat(nx.Format)) { cur = nxId; continue; }
                if (cum + (nx.Episodes ?? 0) > cap + 1) break;   // +1: recap-episode slack
                claims[nxId] = item;
                cum += nx.Episodes ?? 0;
                cur = nxId;
            }
        }
    }

    /// <summary>Episode total across the item's real TMDB seasons (specials
    /// excluded), or null when season metadata is absent.</summary>
    private static int? TmdbEpisodeTotal(MediaItem item)
    {
        if (string.IsNullOrEmpty(item.SeasonsJson)) return null;
        try
        {
            var seasons = System.Text.Json.JsonSerializer.Deserialize<List<SeasonMeta>>(item.SeasonsJson);
            var total = seasons?.Where(s => s.Number > 0).Sum(s => s.EpisodeCount) ?? 0;
            return total > 0 ? total : null;
        }
        catch { return null; }
    }

    /// <summary>Kahn's topological sort over SEQUEL edges (PREQUEL inverted),
    /// ties broken by (year, title) — i.e. release order stabilised by the
    /// explicit chains AniList knows about.</summary>
    private static List<FranchiseNode> OrderNodes(List<FranchiseNode> nodes, List<FranchiseEdge> edges)
    {
        var byId = nodes.ToDictionary(n => n.AniListId);
        var after = new Dictionary<int, List<int>>();   // a → must come after a
        var indeg = nodes.ToDictionary(n => n.AniListId, _ => 0);
        void AddDep(int earlier, int later)
        {
            if (!byId.ContainsKey(earlier) || !byId.ContainsKey(later) || earlier == later) return;
            if (!after.TryGetValue(earlier, out var list)) after[earlier] = list = new();
            if (list.Contains(later)) return;
            list.Add(later);
            indeg[later]++;
        }
        foreach (var e in edges)
        {
            if (e.RelationType.Equals("SEQUEL", StringComparison.OrdinalIgnoreCase))
                AddDep(e.FromAniListId, e.ToAniListId);
            else if (e.RelationType.Equals("PREQUEL", StringComparison.OrdinalIgnoreCase))
                AddDep(e.ToAniListId, e.FromAniListId);
        }

        var ready = nodes.Where(n => indeg[n.AniListId] == 0).ToList();
        var result = new List<FranchiseNode>(nodes.Count);
        while (ready.Count > 0)
        {
            ready.Sort((a, b) =>
            {
                var y = (a.Year ?? int.MaxValue).CompareTo(b.Year ?? int.MaxValue);
                return y != 0 ? y : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            var n = ready[0];
            ready.RemoveAt(0);
            result.Add(n);
            foreach (var later in after.GetValueOrDefault(n.AniListId) ?? [])
                if (--indeg[later] == 0)
                    ready.Add(byId[later]);
        }
        // Cycles (bad data) — append whatever's left by year.
        foreach (var n in nodes.Where(n => !result.Contains(n)).OrderBy(n => n.Year ?? int.MaxValue))
            result.Add(n);
        return result;
    }

    /// <summary>Label for the card's relation badge: the most descriptive
    /// incoming/outgoing edge type touching this node, main-chain types first.</summary>
    private static string? RelationOf(int aniListId, List<FranchiseEdge> edges)
    {
        static int Rank(string t) => t.ToUpperInvariant() switch
        {
            "SIDE_STORY"  => 0,
            "SPIN_OFF"    => 1,
            "ALTERNATIVE" => 2,
            "SUMMARY"     => 3,
            _             => 9,
        };
        // Only INCOMING branch edges badge a node (A —SIDE_STORY→ B means B is
        // the side story; A is main chain and stays badge-free).
        var touching = edges
            .Where(e => e.ToAniListId == aniListId)
            .Select(e => e.RelationType)
            .OrderBy(Rank)
            .FirstOrDefault();
        // Main-chain (SEQUEL/PREQUEL/PARENT) members get no badge — the order
        // number already says it; badges are for the branches.
        return touching is not null && Rank(touching) < 9 ? touching.ToUpperInvariant() : null;
    }
}
