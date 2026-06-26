namespace Animarr.Shared.Models;

/// <summary>
/// Per-episode metadata fetched from TMDB (title, synopsis, air date, rating,
/// runtime) and cached server-side. Drives the "detailed list" episode view.
///
/// <see cref="Season"/>/<see cref="Episode"/> are TMDB coordinates (the season
/// + episode as TMDB numbers them). For donghua/anime laid out as a single
/// absolute TMDB season, the client maps a disk file to its TMDB episode via
/// <c>MediaFileDto.AbsoluteEpisode</c> before looking the metadata up — same
/// mapping the episode-thumb endpoint uses.
/// </summary>
public sealed record EpisodeMetaDto(
    int     Season,
    int     Episode,
    string? Title,
    string? Overview,
    string? AirDate,    // ISO yyyy-MM-dd, or null
    double? Rating,     // TMDB vote_average (> 0), or null
    int?    RuntimeMin);
