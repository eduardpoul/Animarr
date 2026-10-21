using Microsoft.Extensions.Caching.Memory;

namespace Animarr.Web.Services;

/// <summary>
/// Episode number → TMDB still path for one TMDB season, memoized for the
/// episode-thumbnail endpoint. Each thumbnail cache miss used to download the
/// whole season detail (with images) again — one identical TMDB call per card,
/// so a season grid's first open meant N calls and quickly hit TMDB's 429
/// back-off. Now one call per (show, season) serves every card:
///   • found: kept 6 h (stills of aired episodes don't move; a late still is
///     picked up after that);
///   • TMDB answered nothing (404 / failed request → null): kept 15 min;
///   • exceptions (bad API key, cancellation) are not cached.
/// Concurrent misses share one in-flight request. Only the still paths are
/// kept, not the season payload.
/// </summary>
public static class TmdbSeasonStills
{
    private static readonly TimeSpan FoundTtl    = TimeSpan.FromHours(6);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(15);

    private sealed record Key(int TmdbId, int Season);

    /// <summary>Null when TMDB has no such season (or didn't answer). Same
    /// first-wins semantics as <c>Episodes.FirstOrDefault(e =&gt; e.EpisodeNumber == n)</c>.</summary>
    public static Task<IReadOnlyDictionary<int, string?>?> GetAsync(
        IMemoryCache cache, TmdbClient tmdb, int tmdbId, int season, CancellationToken ct)
    {
        var key = new Key(tmdbId, season);
        var lazy = cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = FoundTtl;
            return new Lazy<Task<IReadOnlyDictionary<int, string?>?>>(() => FetchAsync(cache, key, tmdb));
        })!;
        // The fetch is shared, so it runs without any one request's token;
        // a caller that goes away just stops waiting.
        return lazy.Value.WaitAsync(ct);
    }

    private static async Task<IReadOnlyDictionary<int, string?>?> FetchAsync(IMemoryCache cache, Key key, TmdbClient tmdb)
    {
        TmdbSeasonDetail? detail;
        try { detail = await tmdb.GetSeasonDetailAsync(key.TmdbId, key.Season, ct: CancellationToken.None); }
        catch
        {
            cache.Remove(key);
            throw;
        }

        if (detail is null)
        {
            cache.Set(key, new Lazy<Task<IReadOnlyDictionary<int, string?>?>>(
                Task.FromResult<IReadOnlyDictionary<int, string?>?>(null)), NotFoundTtl);
            return null;
        }

        var stills = new Dictionary<int, string?>();
        foreach (var ep in detail.Episodes ?? [])
            stills.TryAdd(ep.EpisodeNumber, ep.StillPath);
        return stills;
    }
}
