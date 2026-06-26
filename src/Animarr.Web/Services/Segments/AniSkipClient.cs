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

    // ── Reachability gate ─────────────────────────────────────────────────────
    // AniSkip is keyed by (MAL id, episode), so one long season fires one request
    // per episode. If the host is unreachable from this box — e.g. the TLS
    // handshake hangs behind a broken-MTU / DPI network while the API itself is
    // fine — every request would burn the full HTTP timeout. So we gate the whole
    // client on a cached verdict: probe once, and if AniSkip looks down, skip it
    // for a cooldown window and let the cascade fall through to chromaprint.
    // Static so the verdict is shared across all scoped instances in the process.
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTime _checkedUtc = DateTime.MinValue;
    private static bool _up = true;
    private static readonly TimeSpan Recheck      = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>True when AniSkip answered an HTTP request recently (even a 404 —
    /// that still proves the host is up). Cached for <see cref="Recheck"/>; a probe
    /// runs only when the verdict is stale. When false the providers skip AniSkip
    /// and rely on chromaprint. Call this before a batch of episode lookups.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _checkedUtc < Recheck) return _up;
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _checkedUtc < Recheck) return _up;   // re-check after waiting
            _up = await ProbeAsync(ct);
            _checkedUtc = DateTime.UtcNow;
            if (!_up)
                logger.LogWarning("[AniSkip] host unreachable — skipping AniSkip for {Min} min, falling back to chromaprint",
                    Recheck.TotalMinutes);
            return _up;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            // Cheap known-miss lookup — a 404 still proves the host is answering.
            using var resp = await http.GetAsync("/v2/skip-times/1/1?types=op&episodeLength=0", cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Trip the gate closed when a real lookup times out, so the rest of
    /// the season skips AniSkip immediately instead of each waiting out the timeout.</summary>
    private static void MarkDown()
    {
        _up = false;
        _checkedUtc = DateTime.UtcNow;
    }

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
            // A timeout / connection failure (not external cancellation) means the
            // host is unreachable from here — trip the gate so the rest of the
            // batch skips AniSkip fast instead of each waiting out the timeout.
            if (!ct.IsCancellationRequested && ex is TaskCanceledException or TimeoutException or HttpRequestException)
                MarkDown();
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
