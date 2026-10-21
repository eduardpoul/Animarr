using Microsoft.Extensions.Caching.Memory;

namespace Animarr.Web.Services;

/// <summary>
/// Remembers, for a while, that an episode thumbnail fell through to the show's
/// own art (no TMDB still, no ffmpeg frame), so the next views of that card
/// skip the TMDB lookup and the ffmpeg spawn instead of repeating the whole
/// chain on every render. Keyed on the episode's resolved file: when a file for
/// it lands on disk (or a different one is mapped), the chain runs again at once.
/// The fallback is deliberately not written as the episode's cached thumb — a
/// real still or frame may turn up later.
/// </summary>
public static class EpisodeThumbMisses
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private sealed record Key(Guid MediaItemId, int Season, int Episode);
    private sealed record Miss(string? FilePath);

    public static bool IsKnown(IMemoryCache cache, Guid mediaItemId, int season, int episode, string? filePath)
        => cache.TryGetValue(new Key(mediaItemId, season, episode), out Miss? miss)
           && miss is not null
           && string.Equals(miss.FilePath, filePath, StringComparison.Ordinal);

    public static void Remember(IMemoryCache cache, Guid mediaItemId, int season, int episode, string? filePath)
        => cache.Set(new Key(mediaItemId, season, episode), new Miss(filePath), Ttl);
}
