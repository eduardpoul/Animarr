namespace Animarr.Shared.Models;

/// <summary>
/// One video file on disk that belongs to a <see cref="MediaItemDto"/>.
/// Maps server-side file enumeration + pattern parsing into a flat list
/// the client can render directly.
///
/// Season + Episode are nullable because the server can't always parse
/// them — movies live in a flat folder, and unmatched series files still
/// surface so the user can pick one manually.
///
/// <see cref="Source"/> tells the client WHERE the (season, episode) came
/// from: null = derived deterministically from the filename on the fly;
/// "manual" = a user correction stored in the DB; "llm" = an AI verdict.
/// Lets the UI badge AI/manual rows and offer "reset to auto".
///
/// <see cref="AbsoluteEpisode"/> is the TMDB single-season (absolute) episode
/// number for donghua/anime where TMDB lists the whole run as one season but
/// the disk is split (e.g. disk S3E1 → AbsoluteEpisode 107). Null when no
/// per-season offset applies. The disk Season/Episode stay as-is for grouping;
/// AbsoluteEpisode is what the UI uses to pull the correct TMDB episode metadata.
/// </summary>
public sealed record MediaFileDto(
    string FilePath,
    string FileName,
    int? Season,
    int? Episode,
    long SizeBytes,
    string? Source = null,
    int? AbsoluteEpisode = null);
