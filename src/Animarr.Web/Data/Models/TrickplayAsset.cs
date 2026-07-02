namespace Animarr.Web.Data.Models;

/// <summary>
/// One generated seek-preview sprite sheet per media FILE: a JPEG grid of
/// tiny frames sampled every <see cref="IntervalSec"/> seconds, shown as the
/// hover/scrub thumbnail bubble over the player's progress bar (trickplay).
///
/// The sprite lives NEXT TO the media (the folder's <c>.animarr/&lt;folderId&gt;/trickplay/</c>
/// dir, same convention as theme music) so the Docker data volume doesn't
/// balloon; this row is the manifest the player needs to address tiles.
/// Keyed by (item, file path) — the file is the source of truth, so a
/// mapping change (manual episode override) just updates Season/Episode here.
/// </summary>
public class TrickplayAsset
{
    public Guid Id { get; set; }

    /// <summary>FK → MediaItem.Id. Cascade-deleted with the item.</summary>
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Resolved (season, episode) of the source file at generation
    /// time — the lookup key the player queries by. NULL/NULL for movies.</summary>
    public int? Season { get; set; }
    public int? Episode { get; set; }

    /// <summary>Absolute path of the source video file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Absolute path of the generated sprite JPEG.</summary>
    public string SpritePath { get; set; } = string.Empty;

    /// <summary>Seconds of playback each tile covers (tile N shows ~N*interval).</summary>
    public int IntervalSec { get; set; }

    public int TileWidth  { get; set; }
    public int TileHeight { get; set; }

    /// <summary>Grid geometry: tiles per row / row count in the sprite sheet.</summary>
    public int Cols { get; set; }
    public int Rows { get; set; }

    /// <summary>Number of real tiles (the last grid row may be black-padded).</summary>
    public int Count { get; set; }

    public double DurationSec { get; set; }

    /// <summary>LastWriteTimeUtc of the source file at generation — a mismatch
    /// on the next pass means the file changed and the sprite regenerates.</summary>
    public DateTime SourceWriteTimeUtc { get; set; }

    public DateTime GeneratedAtUtc { get; set; }
}
