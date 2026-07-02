namespace Animarr.Web.Data.Models;

/// <summary>
/// One "Хочу посмотреть" entry for a user. Two flavours:
///   • local  — MediaItemId set (a library title starred for later);
///   • external — TmdbId set, with a metadata snapshot (Title/Year/PosterUrl)
///     so the card renders without re-querying TMDB. External entries are the
///     output of the recommendation rails' "Want" button and the future
///     franchise "+" — and the input for the future torrent-search hookup.
/// </summary>
public class WatchlistItem
{
    public Guid Id { get; set; }

    /// <summary>FK → User.Id. Cascade — the list is personal.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Set for library titles. Cascade-deleted with the item.</summary>
    public Guid? MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Set for external (not-in-library) titles.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Snapshot fields for external entries (local entries render
    /// from the MediaItem row instead).</summary>
    public string  Title     { get; set; } = string.Empty;
    public int?    Year      { get; set; }
    public string? PosterUrl { get; set; }
    /// <summary>"tv" | "movie" — which TMDB entity the TmdbId refers to.</summary>
    public string? MediaType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// "Don't suggest this" marks from the recommendation rails — one row per
/// (user, title). Local titles key on MediaItemId, external ones on TmdbId.
/// Read as an exclusion set by RecsService.
/// </summary>
public class RecDismissal
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid? MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public int? TmdbId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
