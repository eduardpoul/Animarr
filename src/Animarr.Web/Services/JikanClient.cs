using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// Jikan v4 (unofficial MyAnimeList API) — the only free source of per-episode
/// filler/recap flags; the official MAL v2 API doesn't expose them. No auth,
/// but strict rate limits (3 req/s, 60/min), so page fetches are spaced out
/// and callers are expected to be background sweeps, not request paths.
/// </summary>
public sealed class JikanClient(IHttpClientFactory httpFactory, ILogger<JikanClient> logger)
{
    public const string ClientName = "jikan";

    /// <summary>Max pages (100 eps each) — One Piece is ~12; anything past 40
    /// is bad data, not a real show.</summary>
    private const int MaxPages = 40;
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(800);

    public sealed record EpisodeFlags(List<int> Filler, List<int> Recap);

    /// <summary>All filler/recap episode numbers for one MAL entry, walking
    /// the paginated episode list. Returns empty lists when MAL has no
    /// episodes for the id (fine — flags just don't exist), null on transient
    /// failure (rate limit / network) so the caller can retry later.</summary>
    public async Task<EpisodeFlags?> GetEpisodeFlagsAsync(int malId, CancellationToken ct = default)
    {
        var filler = new List<int>();
        var recap  = new List<int>();
        using var http = httpFactory.CreateClient(ClientName);

        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            using var resp = await http.GetAsync($"/v4/anime/{malId}/episodes?page={page}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new EpisodeFlags(filler, recap);   // entry has no episode list
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogDebug("[Jikan] episodes {MalId} p{Page} → {Status}", malId, page, (int)resp.StatusCode);
                return null;                              // 429 & friends — retry next sweep
            }

            var body = await resp.Content.ReadFromJsonAsync<EpisodesPage>(cancellationToken: ct);
            foreach (var ep in body?.Data ?? [])
            {
                if (ep.Filler) filler.Add(ep.MalId);
                if (ep.Recap)  recap.Add(ep.MalId);
            }

            if (body?.Pagination?.HasNextPage != true) break;
            await Task.Delay(PageDelay, ct);
        }
        return new EpisodeFlags(filler, recap);
    }

    private sealed class EpisodesPage
    {
        [JsonPropertyName("pagination")] public PageInfo? Pagination { get; set; }
        [JsonPropertyName("data")] public List<Episode>? Data { get; set; }
    }
    private sealed class PageInfo
    {
        [JsonPropertyName("has_next_page")] public bool HasNextPage { get; set; }
    }
    private sealed class Episode
    {
        [JsonPropertyName("mal_id")] public int  MalId  { get; set; }
        [JsonPropertyName("filler")] public bool Filler { get; set; }
        [JsonPropertyName("recap")]  public bool Recap  { get; set; }
    }
}

/// <summary>Shape of MediaItem.EpisodeFlagsJson.</summary>
public sealed class EpisodeFlagsData
{
    public int[] Filler { get; set; } = Array.Empty<int>();
    public int[] Recap  { get; set; } = Array.Empty<int>();
}
