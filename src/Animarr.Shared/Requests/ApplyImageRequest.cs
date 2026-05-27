namespace Animarr.Shared.Requests;

/// <summary>
/// Pin a remote image URL (TMDB CDN-hosted typically) as the new poster /
/// fanart / logo for a MediaItem. The server downloads the full-res
/// version, writes it into the image cache, and updates the corresponding
/// path column.
///
/// <see cref="ImageType"/> is one of: "poster", "fanart", "logo".
/// </summary>
public sealed record ApplyImageRequest(string ImageType, string Url);
