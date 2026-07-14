namespace Animarr.Shared.Models;

/// <summary>
/// Manifest for one episode/movie's seek-preview sprite sheet (trickplay).
/// The player addresses tile N (N = floor(t / IntervalSec), clamped to
/// Count-1) at grid cell (N % Cols, N / Cols) inside the sprite image.
/// </summary>
/// <param name="SpriteUrl">Relative URL of the sprite JPEG (served via /api/image).</param>
/// <param name="IntervalSec">Seconds of playback each tile covers.</param>
/// <param name="Count">Real tile count — the last grid row may be black-padded.</param>
public sealed record TrickplayDto(
    string SpriteUrl,
    int IntervalSec,
    int TileWidth,
    int TileHeight,
    int Cols,
    int Rows,
    int Count);
