namespace Animarr.Shared.Models;

/// <summary>
/// One row in the poster / backdrop / logo alternative gallery served by
/// <c>/api/media/{id}/poster-alternatives</c> and
/// <c>/api/media/{id}/backdrop-alternatives</c>.
///
/// Previous shape was a bare <c>string[]</c> of URLs — readable enough for
/// the gallery grid, but the user had no way to compare candidates by
/// resolution before picking. TMDB returns the actual pixel size of each
/// asset in its <c>file_path</c> response, so we now surface those numbers
/// as a "1280x720" badge on each card.
///
/// <see cref="Width"/> / <see cref="Height"/> are 0 for sources that didn't
/// report a size (MAL pictures) — UI hides the badge in that case.
/// </summary>
public sealed record ImageCandidateDto(string Url, int Width, int Height);
