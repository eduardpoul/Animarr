namespace Animarr.Shared.Models;

/// <summary>
/// One card in a recommendation rail ("More like this" on a title page,
/// "For you" on Home). Local titles carry MediaItemId (clickable straight
/// into the catalog); external ones carry TmdbId + a TMDB poster URL and
/// render as "not in library" cards with a Want button.
///
/// Reasons come as PARTS (an anchor title and/or a shared genre) rather than
/// a composed sentence, so the client formats them through its own
/// localization ("Because you watch “{0}”" / "Тоже {0}").
/// </summary>
public sealed record RecCardDto(
    Guid?   MediaItemId,
    int?    TmdbId,
    string  Title,
    int?    Year,
    string? PosterUrl,
    double? Rating,
    string? MediaType,
    string? ReasonTitle,
    string? ReasonTag,
    bool    InLibrary);

public sealed record WatchlistItemDto(
    Guid     Id,
    Guid?    MediaItemId,
    int?     TmdbId,
    string   Title,
    int?     Year,
    string?  PosterUrl,
    string?  MediaType,
    DateTime CreatedAt,
    int?     AniListId = null);

/// <summary>POST body for /api/me/watchlist. Local add: MediaItemId only.
/// External add: TmdbId (recommendation rails) or AniListId (franchise rail)
/// + the snapshot fields from the card.</summary>
public sealed record WatchlistAddRequest(
    Guid?   MediaItemId,
    int?    TmdbId,
    string? Title,
    int?    Year,
    string? PosterUrl,
    string? MediaType,
    int?    AniListId = null);

/// <summary>POST body for /api/me/recs/dismiss — exactly one of the ids set.</summary>
public sealed record RecDismissRequest(Guid? MediaItemId, int? TmdbId);
