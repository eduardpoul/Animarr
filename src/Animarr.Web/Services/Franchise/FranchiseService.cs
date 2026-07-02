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
    Segments.MalIdResolver malResolver,
    ILogger<FranchiseService> logger)
{
    private const int MaxDepth = 6;
    private const int MaxNodes = 30;

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

    /// <summary>The watch-order rail for one library title, or null when the
    /// graph is unknown / trivial (single node).</summary>
    public async Task<FranchiseDto?> GetFranchiseAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item?.AniListId is not int rootId) return null;

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
        if (component.Count <= 1) return null;

        var nodes = await db.FranchiseNodes.AsNoTracking()
            .Where(n => component.Contains(n.AniListId))
            .ToListAsync(ct);
        if (nodes.Count <= 1) return null;
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
        MediaItem? MatchOf(FranchiseNode n) =>
            libItems.FirstOrDefault(m => m.AniListId == n.AniListId)
            ?? libItems.FirstOrDefault(m => n.MalId != null && m.MalId == n.MalId);

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
        if (cards.Count <= 1) return null;

        var title = cards.FirstOrDefault(c => c.Year == cards.Min(x => x.Year))?.Title ?? cards[0].Title;
        return new FranchiseDto(
            title,
            cards.Count,
            cards.Count(c => c.Watched),
            cards.Count(c => c.InLibrary),
            cards);
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
