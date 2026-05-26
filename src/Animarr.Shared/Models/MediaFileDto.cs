namespace Animarr.Shared.Models;

/// <summary>
/// One video file on disk that belongs to a <see cref="MediaItemDto"/>.
/// Maps server-side file enumeration + pattern parsing into a flat list
/// the client can render directly.
///
/// Season + Episode are nullable because the server can't always parse
/// them — movies live in a flat folder, and unmatched series files still
/// surface so the user can pick one manually.
/// </summary>
public sealed record MediaFileDto(
    string FilePath,
    string FileName,
    int? Season,
    int? Episode,
    long SizeBytes);
