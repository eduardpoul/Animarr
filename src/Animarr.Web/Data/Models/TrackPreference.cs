namespace Animarr.Web.Data.Models;

/// <summary>
/// Per-profile track memory of one title: which audio and subtitles this
/// profile picked, the subtitle offset, the per-dub audio offsets and the
/// aspect preset. One row per (user, title) — <see cref="Season"/> null —
/// plus optional per-season rows for series whose seasons carry different
/// dubs; a season row's facets win over the title row's.
///
/// Choices are stored BY MEANING, not by index: the track indices and the
/// external file names differ from episode to episode, so the next episode
/// looks for the track with the same language + title (embedded) or the dub
/// with the same group key (external, see
/// <see cref="Animarr.Shared.Models.ExternalTrackDto.GroupKey"/>).
/// <see cref="Services.TrackPreferenceService"/> does the matching.
/// </summary>
public class TrackPreference
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Null = the whole title (a movie, or every season of a
    /// series); a number = that season only (0 = specials).</summary>
    public int? Season { get; set; }

    // ─── Audio ───────────────────────────────────────────────────────────────

    /// <summary>"embedded" | "external"; null = nothing remembered (the
    /// profile's defaults apply).</summary>
    public string? AudioKind { get; set; }
    /// <summary>Canonical ISO 639-2 code ("rus", "jpn") of the picked track;
    /// null when the track had no (known) language tag.</summary>
    public string? AudioLanguage { get; set; }
    /// <summary>Embedded: the stream's title tag ("Дубляж", "Commentary").</summary>
    public string? AudioTitle { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    /// <summary>External: the dub's group key (<c>dir:russian dub</c>,
    /// <c>sidecar:ukr</c>).</summary>
    public string? AudioGroupKey { get; set; }

    // ─── Subtitles ───────────────────────────────────────────────────────────

    /// <summary>"embedded" | "external" | "off"; null = nothing remembered.</summary>
    public string? SubtitleKind { get; set; }
    public string? SubtitleLanguage { get; set; }
    /// <summary>"full" | "forced" | "signs".</summary>
    public string? SubtitleType { get; set; }
    public string? SubtitleTitle { get; set; }
    public string? SubtitleGroupKey { get; set; }

    // ─── Offsets / picture ───────────────────────────────────────────────────

    /// <summary>Subtitle delay in ms (positive = later); null = 0.</summary>
    public int? SubtitleOffsetMs { get; set; }

    /// <summary>Audio sync offset per dub as JSON {"&lt;audio key&gt;": ms} —
    /// key <c>ext:&lt;group key&gt;</c> for an external dub, <c>emb:&lt;language&gt;</c>
    /// for an embedded track. Null / empty = no offsets.</summary>
    public string? AudioOffsetsJson { get; set; }

    /// <summary>Player aspect / zoom preset as the client names it; the web's
    /// form is <c>&lt;mode&gt;[_z&lt;zoom&gt;][_p&lt;pan&gt;]</c> ("fit",
    /// "crop:2.35:1_z1.2"). Null = the player's default.</summary>
    public string? Aspect { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
