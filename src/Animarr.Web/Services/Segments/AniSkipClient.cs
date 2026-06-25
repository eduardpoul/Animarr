using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Client for the AniSkip v2 API (https://api.aniskip.com) — crowd-sourced anime
/// opening/ending timestamps keyed by MyAnimeList id + episode number. Free, no
/// auth. A 404 / <c>found:false</c> means nobody has contributed times for that
/// episode yet — a miss, not an error.
///
/// Contract (verified against the live API):
///   GET /v2/skip-times/{malId}/{ep}?types=op&amp;types=ed&amp;types=mixed-op
///       &amp;types=mixed-ed&amp;types=recap&amp;episodeLength={sec}
///   → { found, results:[ { interval:{startTime,endTime}, skipType, episodeLength } ] }
/// Note <c>types</c> repeats WITHOUT the <c>[]</c> suffix.
/// </summary>
public class AniSkipClient(IHttpClientFactory httpFactory, ILogger<AniSkipClient> logger)
{
    public const string ClientName = "aniskip";

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>One contributed interval: type ("op"/"ed"/"recap"/"mixed-*"),
    /// start/end seconds, and the episode length AniSkip recorded it against.</summary>
    public sealed record SkipInterval(string SkipType, double StartTime, double EndTime, double EpisodeLength);

    public async Task<IReadOnlyList<SkipInterval>> GetSkipTimesAsync(
        int malId, int episodeNumber, double episodeLengthSec, CancellationToken ct = default)
    {
        if (malId <= 0 || episodeNumber <= 0) return Array.Empty<SkipInterval>();
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            var len  = (episodeLengthSec > 0 ? episodeLengthSec : 0).ToString("0.###", CultureInfo.InvariantCulture);
            var url  = $"/v2/skip-times/{malId}/{episodeNumber}"
                     + "?types=op&types=ed&types=mixed-op&types=mixed-ed&types=recap"
                     + $"&episodeLength={len}";

            using var resp = await http.GetAsync(url, ct);
            // 404 = no contributed times for this episode — an expected miss.
            if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<SkipInterval>();
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<AniSkipResponse>(_json, ct);
            if (body is not { Found: true, Results: not null }) return Array.Empty<SkipInterval>();

            return body.Results
                .Where(r => r.Interval is not null && !string.IsNullOrEmpty(r.SkipType))
                .Select(r => new SkipInterval(r.SkipType!, r.Interval!.StartTime, r.Interval.EndTime, r.EpisodeLength))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AniSkip] lookup failed for mal={Mal} ep={Ep}", malId, episodeNumber);
            return Array.Empty<SkipInterval>();
        }
    }
}

// ── DTOs (subset of the AniSkip JSON response) ───────────────────────────────

public sealed class AniSkipResponse
{
    [JsonPropertyName("found")]   public bool Found { get; set; }
    [JsonPropertyName("results")] public List<AniSkipResult>? Results { get; set; }
}

public sealed class AniSkipResult
{
    [JsonPropertyName("interval")]      public AniSkipInterval? Interval { get; set; }
    [JsonPropertyName("skipType")]      public string? SkipType { get; set; }
    [JsonPropertyName("episodeLength")] public double EpisodeLength { get; set; }
}

public sealed class AniSkipInterval
{
    [JsonPropertyName("startTime")] public double StartTime { get; set; }
    [JsonPropertyName("endTime")]   public double EndTime { get; set; }
}
