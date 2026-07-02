namespace Animarr.Shared.Models;

/// <summary>
/// Watch-order franchise rail for a title. Cards are pre-sorted (release
/// order via AniList SEQUEL/PREQUEL chains) and consecutive AniList seasons
/// matched to the SAME library item come collapsed into one card
/// (SpanCount > 1 → "xN seasons").
/// </summary>
public sealed record FranchiseDto(
    string Title,
    int    Total,
    int    WatchedCount,
    int    InLibraryCount,
    IReadOnlyList<FranchiseCardDto> Cards);

/// <summary>One franchise member. InLibrary cards navigate to the catalog;
/// external ones show a Want button (watchlist by AniListId). Relation is the
/// AniList branch type (SIDE_STORY / SPIN_OFF / ALTERNATIVE / SUMMARY) or
/// null for main-chain entries.</summary>
public sealed record FranchiseCardDto(
    int     AniListId,
    Guid?   MediaItemId,
    string  Title,
    int?    Year,
    string? Format,
    int?    Episodes,
    string? CoverUrl,
    string? Relation,
    bool    InLibrary,
    bool    IsCurrent,
    bool    Watched,
    int     SpanCount);
