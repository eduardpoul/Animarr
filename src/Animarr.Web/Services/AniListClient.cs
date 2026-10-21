using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// Minimal AniList GraphQL client (https://graphql.anilist.co) — free, no auth.
/// Used only to bridge a title → MyAnimeList id, so AnimeThemes (which keys on
/// MAL/AniList ids) can be queried for anime we identified via TMDB (which
/// carries no MAL id).
/// </summary>
public class AniListClient(IHttpClientFactory httpFactory, ILogger<AniListClient> logger)
{
    private const string ClientName = "anilist";

    // AniList rate-limits per IP (~30 req/min while degraded) and the budget
    // is shared by the franchise walk, MAL-id bridging and the airing refresh.
    // After a 429 the relations walk and title resolves hold off until the
    // server's Retry-After instead of spending more requests into the wall.
    // Static: the client itself is scoped.
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRetryAfter     = TimeSpan.FromMinutes(15);
    private static long _rateLimitedUntilTicks;

    /// <summary>What is left of the Retry-After window of the last 429
    /// (zero when AniList may be called).</summary>
    public static TimeSpan RateLimitRemaining
    {
        get
        {
            var left = Interlocked.Read(ref _rateLimitedUntilTicks) - DateTime.UtcNow.Ticks;
            return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
        }
    }

    /// <summary>Record a 429's Retry-After (default 60 s, capped at 15 min;
    /// a later deadline already in place wins). Returns the wait.</summary>
    private TimeSpan NoteRateLimited(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        var wait = ra?.Delta
                   ?? (ra?.Date is DateTimeOffset at ? at - DateTimeOffset.UtcNow : DefaultRetryAfter);
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        if (wait > MaxRetryAfter) wait = MaxRetryAfter;

        var until = DateTime.UtcNow.Add(wait).Ticks;
        long cur;
        do
        {
            cur = Interlocked.Read(ref _rateLimitedUntilTicks);
            if (cur >= until) break;
        }
        while (Interlocked.CompareExchange(ref _rateLimitedUntilTicks, until, cur) != cur);

        logger.LogWarning("[AniList] 429 rate limit — holding off for {Seconds}s", (int)wait.TotalSeconds);
        return wait;
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record AniListMatch(int AniListId, int? IdMal, string? Title);

    /// <summary>Airing snapshot for one media: RELEASING/FINISHED/…, plus the
    /// next episode's number and unix air time when the show is releasing.</summary>
    public sealed record AniListAiring(
        int AniListId, int? IdMal, string? Status,
        int? NextEpisode, long? NextAiringAtUnix);

    /// <summary>One media node + its typed relation edges — a single BFS step
    /// of the franchise graph.</summary>
    public sealed record AniListRelations(
        AniListNode Node,
        List<(string RelationType, AniListNode Node)> Edges);

    public sealed record AniListNode(
        int AniListId, int? IdMal, string Title, string? Format,
        int? Year, int? Episodes, string? CoverUrl, string? Status);

    /// <summary>Resolve an anime by title (romaji/english). Returns the AniList id
    /// plus the MAL id when AniList has the cross-reference. Null when no match.</summary>
    public async Task<AniListMatch?> ResolveAsync(string title, CancellationToken ct = default)
        => (await TryResolveAsync(title, ct)).Match;

    /// <summary><see cref="ResolveAsync"/> that also says whether a null is an
    /// ANSWER (AniList has no such anime — 404 / empty result) or a transient
    /// failure (429 rate limit, 5xx, network, cancellation) that is worth
    /// retrying rather than caching.</summary>
    public async Task<(AniListMatch? Match, bool Definitive)> TryResolveAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return (null, true);
        // Inside a 429's Retry-After window: the same transient answer a 429
        // gives, without spending a request.
        if (RateLimitRemaining > TimeSpan.Zero) return (null, false);
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            const string query = "query($search:String){Media(search:$search,type:ANIME){id idMal title{romaji english}}}";
            var payload = new { query, variables = new { search = title } };

            using var resp = await http.PostAsJsonAsync("/", payload, _json, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                NoteRateLimited(resp);
                return (null, false);
            }
            // AniList answers a search miss with 404 "Not Found."
            if (!resp.IsSuccessStatusCode) return (null, resp.StatusCode == System.Net.HttpStatusCode.NotFound);

            var data = await resp.Content.ReadFromJsonAsync<AniListEnvelope>(_json, ct);
            var m = data?.Data?.Media;
            if (m is null) return (null, true);
            return (new AniListMatch(m.Id, m.IdMal, m.Title?.English ?? m.Title?.Romaji), true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AniList] resolve failed for '{Title}'", title);
            return (null, false);
        }
    }

    /// <summary>Fetch one media node with its typed relations (one franchise
    /// BFS step). Query by AniList id, or by MAL id when that's all we have.
    /// Non-anime related nodes (manga adaptations etc.) are filtered out.
    /// Null = no answer for this node (unknown id, other HTTP error, network).
    /// A 429 — or a call inside its Retry-After window — throws
    /// <see cref="AniListRateLimitedException"/> instead: "out of budget" is
    /// not "no relations", and the walk must not be recorded as complete.</summary>
    public async Task<AniListRelations?> GetRelationsAsync(int? aniListId, int? malId, CancellationToken ct = default)
    {
        if (aniListId is null && malId is null) return null;
        if (RateLimitRemaining is { Ticks: > 0 } wait) throw new AniListRateLimitedException(wait);
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            const string query = """
                query($id:Int,$malId:Int){ Media(id:$id, idMal:$malId, type:ANIME){
                  id idMal status format episodes startDate{year} title{romaji english} coverImage{large}
                  relations{ edges{ relationType node{
                    id idMal type status format episodes startDate{year} title{romaji english} coverImage{large}
                  } } }
                } }
                """;
            var payload = new { query, variables = new { id = aniListId, malId = aniListId is null ? malId : null } };

            using var resp = await http.PostAsJsonAsync("/", payload, _json, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new AniListRateLimitedException(NoteRateLimited(resp));
            if (!resp.IsSuccessStatusCode) return null;

            var data = await resp.Content.ReadFromJsonAsync<AniListRelationsEnvelope>(_json, ct);
            var m = data?.Data?.Media;
            if (m is null) return null;

            var edges = new List<(string, AniListNode)>();
            foreach (var e in m.Relations?.Edges ?? [])
            {
                if (e?.Node is not { } n || n.Type is not null && n.Type != "ANIME") continue;
                if (string.IsNullOrEmpty(e.RelationType)) continue;
                edges.Add((e.RelationType, ToNode(n)));
            }
            return new AniListRelations(ToNode(m), edges);
        }
        catch (Exception ex) when (ex is not AniListRateLimitedException)
        {
            logger.LogWarning(ex, "[AniList] relations fetch failed for id={Id}/mal={Mal}", aniListId, malId);
            return null;
        }

        static AniListNode ToNode(AniListRelMedia n) => new(
            n.Id, n.IdMal,
            n.Title?.English ?? n.Title?.Romaji ?? $"#{n.Id}",
            n.Format, n.StartDate?.Year, n.Episodes,
            n.CoverImage?.Large, n.Status);
    }

    /// <summary>Batch airing lookup — one Page query covers up to 50 titles, so
    /// a whole library of ongoings costs a single request per tick. Query by
    /// AniList ids and/or MAL ids (donghua usually exist on AniList WITHOUT a
    /// MAL cross-reference, so the id_in arm matters).</summary>
    public async Task<List<AniListAiring>> GetAiringBatchAsync(
        IReadOnlyCollection<int> aniListIds, IReadOnlyCollection<int> malIds, CancellationToken ct = default)
    {
        var result = new List<AniListAiring>();
        if (aniListIds.Count == 0 && malIds.Count == 0) return result;
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            const string query = """
                query($ids:[Int],$malIds:[Int]){
                  byId: Page(perPage:50){ media(id_in:$ids, type:ANIME){
                    id idMal status episodes nextAiringEpisode{ episode airingAt } } }
                  byMal: Page(perPage:50){ media(idMal_in:$malIds, type:ANIME){
                    id idMal status episodes nextAiringEpisode{ episode airingAt } } }
                }
                """;
            var payload = new
            {
                query,
                variables = new
                {
                    ids    = aniListIds.Count > 0 ? aniListIds.Take(50).ToArray() : null,
                    malIds = malIds.Count > 0 ? malIds.Take(50).ToArray() : null,
                },
            };

            using var resp = await http.PostAsJsonAsync("/", payload, _json, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Record a 429 so the relations walk / resolves back off too;
                // the batch itself keeps its old shape (empty answer).
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests) NoteRateLimited(resp);
                logger.LogWarning("[AniList] airing batch returned {Status}", resp.StatusCode);
                return result;
            }

            var data = await resp.Content.ReadFromJsonAsync<AniListAiringEnvelope>(_json, ct);
            var media = (data?.Data?.ById?.Media ?? [])
                .Concat(data?.Data?.ByMal?.Media ?? [])
                .GroupBy(m => m.Id)
                .Select(g => g.First());
            foreach (var m in media)
                result.Add(new AniListAiring(m.Id, m.IdMal, m.Status,
                    m.NextAiringEpisode?.Episode, m.NextAiringEpisode?.AiringAt));
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AniList] airing batch failed");
            return result;
        }
    }
}

/// <summary>AniList answered 429 (or the client is still inside that
/// answer's Retry-After window): a transient "no answer", never "no data".</summary>
public sealed class AniListRateLimitedException(TimeSpan retryAfter)
    : Exception($"AniList rate limit — retry in {(int)Math.Ceiling(retryAfter.TotalSeconds)}s")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public sealed class AniListEnvelope
{
    [JsonPropertyName("data")] public AniListData? Data { get; set; }
}

public sealed class AniListData
{
    [JsonPropertyName("Media")] public AniListMedia? Media { get; set; }
}

public sealed class AniListMedia
{
    [JsonPropertyName("id")]    public int     Id    { get; set; }
    [JsonPropertyName("idMal")] public int?    IdMal { get; set; }
    [JsonPropertyName("title")] public AniListTitle? Title { get; set; }
}

public sealed class AniListTitle
{
    [JsonPropertyName("romaji")]  public string? Romaji  { get; set; }
    [JsonPropertyName("english")] public string? English { get; set; }
}

// ── airing batch DTOs ────────────────────────────────────────────────────────

public sealed class AniListAiringEnvelope
{
    [JsonPropertyName("data")] public AniListAiringData? Data { get; set; }
}

public sealed class AniListAiringData
{
    [JsonPropertyName("byId")]  public AniListPage? ById  { get; set; }
    [JsonPropertyName("byMal")] public AniListPage? ByMal { get; set; }
}

public sealed class AniListPage
{
    [JsonPropertyName("media")] public List<AniListAiringMedia>? Media { get; set; }
}

public sealed class AniListAiringMedia
{
    [JsonPropertyName("id")]       public int     Id       { get; set; }
    [JsonPropertyName("idMal")]    public int?    IdMal    { get; set; }
    [JsonPropertyName("status")]   public string? Status   { get; set; }
    [JsonPropertyName("episodes")] public int?    Episodes { get; set; }
    [JsonPropertyName("nextAiringEpisode")] public AniListNextAiring? NextAiringEpisode { get; set; }
}

public sealed class AniListNextAiring
{
    [JsonPropertyName("episode")]  public int  Episode  { get; set; }
    [JsonPropertyName("airingAt")] public long AiringAt { get; set; }
}

// ── relations DTOs ───────────────────────────────────────────────────────────

public sealed class AniListRelationsEnvelope
{
    [JsonPropertyName("data")] public AniListRelationsData? Data { get; set; }
}

public sealed class AniListRelationsData
{
    [JsonPropertyName("Media")] public AniListRelMedia? Media { get; set; }
}

public sealed class AniListRelMedia
{
    [JsonPropertyName("id")]        public int     Id       { get; set; }
    [JsonPropertyName("idMal")]     public int?    IdMal    { get; set; }
    [JsonPropertyName("type")]      public string? Type     { get; set; }
    [JsonPropertyName("status")]    public string? Status   { get; set; }
    [JsonPropertyName("format")]    public string? Format   { get; set; }
    [JsonPropertyName("episodes")]  public int?    Episodes { get; set; }
    [JsonPropertyName("startDate")] public AniListDate? StartDate { get; set; }
    [JsonPropertyName("title")]     public AniListTitle? Title { get; set; }
    [JsonPropertyName("coverImage")] public AniListCover? CoverImage { get; set; }
    [JsonPropertyName("relations")] public AniListRelationEdges? Relations { get; set; }
}

public sealed class AniListDate
{
    [JsonPropertyName("year")] public int? Year { get; set; }
}

public sealed class AniListCover
{
    [JsonPropertyName("large")] public string? Large { get; set; }
}

public sealed class AniListRelationEdges
{
    [JsonPropertyName("edges")] public List<AniListRelationEdge>? Edges { get; set; }
}

public sealed class AniListRelationEdge
{
    [JsonPropertyName("relationType")] public string? RelationType { get; set; }
    [JsonPropertyName("node")]         public AniListRelMedia? Node { get; set; }
}
