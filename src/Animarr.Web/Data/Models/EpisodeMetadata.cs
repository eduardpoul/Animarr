namespace Animarr.Web.Data.Models;

/// <summary>
/// Cached per-episode metadata from TMDB (title, synopsis, air date, rating,
/// runtime) backing the detailed-list episode view. One row per
/// (item, TMDB season, TMDB episode).
///
/// Populated lazily by <c>GET /api/media/{id}/episodes</c> on first view and
/// re-fetched when the metadata <see cref="Language"/> changes or the title's
/// identification is refreshed (compared against
/// <c>MediaItem.LastMetadataRefreshedAt</c> via <see cref="FetchedAtUtc"/>).
/// Mirrors the per-episode table shape of <see cref="EpisodeSegment"/>.
///
/// <see cref="Season"/>/<see cref="Episode"/> are TMDB coordinates; the UI maps
/// a disk file to its TMDB episode (via <c>MediaFileDto.AbsoluteEpisode</c>)
/// before looking a row up — the same mapping the episode-thumb endpoint uses.
/// </summary>
public class EpisodeMetadata
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>TMDB season number.</summary>
    public int Season { get; set; }

    /// <summary>TMDB episode number.</summary>
    public int Episode { get; set; }

    public string? Title { get; set; }
    public string? Overview { get; set; }

    /// <summary>Air date as the raw TMDB ISO string (yyyy-MM-dd), or null.</summary>
    public string? AirDate { get; set; }

    /// <summary>TMDB vote_average (&gt; 0), or null.</summary>
    public double? Rating { get; set; }

    /// <summary>Episode runtime in minutes, or null.</summary>
    public int? RuntimeMin { get; set; }

    /// <summary>Metadata language code (en/ru/uk/de/es) these texts were fetched
    /// in. A mismatch with the current setting triggers a re-fetch.</summary>
    public string Language { get; set; } = "en";

    /// <summary>When this row was last pulled from TMDB. Compared against the
    /// item's <c>LastMetadataRefreshedAt</c> to invalidate stale rows after a
    /// re-identify / re-localize.</summary>
    public DateTime FetchedAtUtc { get; set; }

    /// <summary>Revision of the fetch logic that produced this row
    /// (<see cref="Animarr.Web.Services.EpisodeMetadataFetcher.CurrentVersion"/>).
    /// Bumped when the fetcher's field-mapping changes (e.g. the English
    /// per-field fallback) so the endpoint can treat older rows as stale and
    /// re-pull them on next view — no re-identify or language toggle needed.
    /// Existing rows default to 0, which never matches the current version.</summary>
    public int Version { get; set; }
}
