using System.Collections.Concurrent;
using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Resolves the MyAnimeList id AniSkip needs for a title. One place for all the
/// "how do we get a MAL id" logic so AniSkipProvider stays thin:
///   1. Use the stored <see cref="MediaItem.MalId"/> when present.
///   2. Otherwise bridge title → MAL id via AniList (free, no API key) — the same
///      bridge MetadataService already uses for theme music.
/// Results (including misses) are cached per item for the process lifetime, so a
/// season's episodes trigger at most one AniList call.
///
/// Note: this only yields the title's primary MAL id, which is correct for
/// single-season titles. Per-season / per-cour mapping (e.g. Bleach TYBW split
/// into "Season 3") would be added here later.
/// </summary>
public sealed class MalIdResolver(AniListClient aniList, ILogger<MalIdResolver> logger)
{
    // Process-wide: a title's MAL id doesn't change, and we want to reuse it
    // across the background pass and lazy player lookups without re-querying.
    private static readonly ConcurrentDictionary<Guid, int?> _cache = new();

    public async Task<int?> ResolveAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.MalId is > 0) return item.MalId;
        if (_cache.TryGetValue(item.Id, out var cached)) return cached;

        // Try several title forms in order. AniList matches English/romaji far
        // better than a CJK original title — e.g. Gundam 00's OriginalTitle is
        // kanji ("機動戦士ガンダム00"), which AniList misses, while its English
        // title resolves fine.
        var candidates = new[] { item.Title, item.EnglishTitle, item.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int? mal = null;
        foreach (var query in candidates)
        {
            try
            {
                var match = await aniList.ResolveAsync(query, ct);
                if (match?.IdMal is > 0)
                {
                    mal = match.IdMal;
                    logger.LogInformation("[MalId] {Title} → mal {Mal} (AniList query: {Query})", item.Title, mal, query);
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[MalId] AniList resolve failed for query '{Query}'", query);
            }
        }
        if (mal is null)
            logger.LogDebug("[MalId] no MAL id for {Title} (AniList miss / not an anime)", item.Title);

        _cache[item.Id] = mal;
        return mal;
    }
}
