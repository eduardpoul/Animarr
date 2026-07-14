using System.Collections.Concurrent;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

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
/// A successful bridge also persists MalId + AniListId back onto the MediaItem
/// row — the AniList id in particular is what the airing-schedule / relations
/// features key off, and it exists for most donghua even when IdMal is null.
///
/// Note: this only yields the title's primary MAL id, which is correct for
/// single-season titles. Per-season / per-cour mapping (e.g. Bleach TYBW split
/// into "Season 3") would be added here later.
/// </summary>
public sealed class MalIdResolver(
    AniListClient aniList,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<MalIdResolver> logger)
{
    // Process-wide: a title's MAL id doesn't change, and we want to reuse it
    // across the background pass and lazy player lookups without re-querying.
    private static readonly ConcurrentDictionary<Guid, (int? Mal, int? AniList)> _cache = new();

    public async Task<int?> ResolveAsync(MediaItem item, CancellationToken ct = default)
    {
        if (item.MalId is > 0) return item.MalId;
        if (_cache.TryGetValue(item.Id, out var cached)) return cached.Mal;

        // Try several title forms in order. AniList matches English/romaji far
        // better than a CJK original title — e.g. Gundam 00's OriginalTitle is
        // kanji ("機動戦士ガンダム00"), which AniList misses, while its English
        // title resolves fine.
        var candidates = new[] { item.Title, item.EnglishTitle, item.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int? mal = null, aniListId = null;
        foreach (var query in candidates)
        {
            try
            {
                var match = await aniList.ResolveAsync(query, ct);
                // Keep the first AniList id even when IdMal is missing — donghua
                // usually exist on AniList but carry no MAL cross-reference.
                if (match is not null) aniListId ??= match.AniListId;
                if (match?.IdMal is > 0)
                {
                    mal = match.IdMal;
                    aniListId = match.AniListId;
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

        _cache[item.Id] = (mal, aniListId);
        await PersistIdsAsync(item, mal, aniListId, ct);
        return mal;
    }

    /// <summary>Write freshly-bridged ids onto the MediaItem row (only filling
    /// blanks — never overwriting an existing id). Targeted UPDATE by id: the
    /// item instance we received may be tracked by another context or detached.</summary>
    private async Task PersistIdsAsync(MediaItem item, int? mal, int? aniListId, CancellationToken ct)
    {
        var setMal = mal is > 0 && item.MalId is not > 0;
        var setAniList = aniListId is > 0 && item.AniListId is not > 0;
        if (!setMal && !setAniList) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.MediaItems
                .Where(m => m.Id == item.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.MalId,     m => setMal     ? mal       : m.MalId)
                    .SetProperty(m => m.AniListId, m => setAniList ? aniListId : m.AniListId), ct);
            // Mirror onto the instance so same-request callers see the ids.
            if (setMal)     item.MalId     = mal;
            if (setAniList) item.AniListId = aniListId;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[MalId] failed to persist ids for {Title}", item.Title);
        }
    }
}
