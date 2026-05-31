namespace Animarr.Web.Data.Models;

/// <summary>
/// A persisted override for a single video file's (season, episode).
///
/// Deterministic parsing stays ON-THE-FLY in <c>MediaFileResolver</c> — it is
/// re-derived from the filename on every read, so it is self-healing (rename a
/// pattern, move a file, and the next scan reflects it without any stored state
/// to invalidate). This table therefore stores ONLY the decisions that cannot
/// be recomputed from the name:
///   • a user's manual correction  (Source = "manual")
///   • an LLM's verdict for a file the parser could not place (Source = "llm")
///
/// Overlay model: the resolver computes the deterministic answer first, then
/// applies the matching row here if one exists. "manual" outranks "llm", and
/// neither is ever overwritten by the deterministic pass. Rows are keyed by the
/// absolute <see cref="FilePath"/>; an orphaned row (file moved or deleted) is
/// simply never matched and can be cleaned up lazily.
/// </summary>
public class EpisodeFileMapping
{
    public Guid Id { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Absolute path of the video file this override applies to. Must
    /// match exactly what <c>MediaFileResolver</c> enumerates from disk.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Override season. Null = keep the deterministic season.</summary>
    public int? Season { get; set; }

    /// <summary>Override episode. Null = keep the deterministic episode.</summary>
    public int? Episode { get; set; }

    /// <summary>Who set this row — see <see cref="MappingSource"/>. Manual
    /// outranks LLM; the deterministic pass never overwrites either.</summary>
    public string Source { get; set; } = MappingSource.Manual;

    /// <summary>0..1 confidence for LLM rows; manual rows are 1.0.</summary>
    public double Confidence { get; set; } = 1.0;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Well-known values for <see cref="EpisodeFileMapping.Source"/>.
/// Mirrors the Source-precedence convention already used by
/// <c>MediaItemCategory</c> (manual rows the classifier won't clobber).</summary>
public static class MappingSource
{
    public const string Manual = "manual";
    public const string Llm    = "llm";
}
