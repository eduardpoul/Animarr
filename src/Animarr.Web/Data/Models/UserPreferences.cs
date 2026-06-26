namespace Animarr.Web.Data.Models;

/// <summary>
/// Per-user appearance + audio + language preferences. Mirrors the four
/// <c>ProfilePanel</c> tabs (Identity / Appearance / Audio / Language).
/// One row per user, lazily created on first read with defaults inherited
/// from the legacy AppConfig keys so existing single-user installs upgrade
/// cleanly to v4.
///
/// Spec: design_handoff_animarr/CHANGELOG_3_FOR_CLAUDE_CODE.md §4.
/// </summary>
public class UserPreferences
{
    /// <summary>FK + PK → <see cref="User.Id"/>. One row per user.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    // ─── Appearance ──────────────────────────────────────────────────────────

    /// <summary>Accent hue 0..359 (oklch chroma stays in the design tokens).
    /// 5 swatches in ProfilePanel are: crimson 25, amber 50, green 145, blue 230,
    /// violet 295. Free entry via TweaksPanel for dev work.</summary>
    public int AccentHue { get; set; } = 25;

    /// <summary>Show the rotating fanart backdrop behind the catalog/detail shell.</summary>
    public bool BackdropEnabled { get; set; } = true;

    /// <summary>Backdrop blur in px (0..30 — anything higher costs noticeable GPU).</summary>
    public int BackdropBlurPx { get; set; } = 14;

    /// <summary>Backdrop brightness 10..80 (matches Tweaks slider range).</summary>
    public int BackdropBrightness { get; set; } = 38;

    /// <summary>Seconds between fanart cross-fades (5..120).</summary>
    public int BackdropIntervalSec { get; set; } = 30;

    /// <summary>TV mode toggle — adds <c>html.tv-mode</c> class for heavier focus
    /// rings + bigger hit targets. Activated by users connecting via TV browser
    /// or BlazorWebView on Android TV.</summary>
    public bool TvMode { get; set; }

    /// <summary>Personality theme (v5) — controls fonts, palette, and decorative
    /// layers via <c>html[data-theme="..."]</c>. One of:
    /// quietude (default) / cinematic / terminal / anime / ember-edge /
    /// mountain-sect / immortal-path / heavens-defiance / ink-and-paper / cyberpunk.
    /// Stored as a slug so adding a new theme is a CSS-only change.</summary>
    public string Theme { get; set; } = "quietude";

    /// <summary>Hero pager style — one of: f (transparent named pager),
    /// g (hover chevrons + dash pager, default), h (numbered pills + chevrons).
    /// Applies only on the Catalog hero. Spec: hero-nav-variants.html.</summary>
    public string HeroPagerStyle { get; set; } = "g";

    /// <summary>Default layout of the episode list on a title's detail page —
    /// "grid" (poster-card grid, default) or "list" (detailed horizontal rows
    /// with title/overview/meta). The grid⇄list toggle on the detail page
    /// writes back here, so it doubles as the per-account default.
    /// Spec: "Animarr — Episode View Modes".</summary>
    public string EpisodeListView { get; set; } = "grid";

    // ─── Audio (NEW in v4) ───────────────────────────────────────────────────

    /// <summary>Preferred audio language for new playbacks. ISO 639-1 lowercase or
    /// human-readable label (Japanese/Mandarin/English/Russian/Korean). Driver
    /// passes this to ffprobe stream selection.</summary>
    public string AudioPreferredLanguage { get; set; } = "Japanese";

    /// <summary>Preferred subtitle language. Same value set as
    /// <see cref="AudioPreferredLanguage"/>.</summary>
    public string SubtitlePreferredLanguage { get; set; } = "Russian";

    /// <summary>Subtitle font size override 12..32px. Stored as int, used by the
    /// player to set the <c>::cue { font-size }</c> at runtime.</summary>
    public int SubtitleSize { get; set; } = 18;

    /// <summary>Default volume 0..100 applied when the player starts.</summary>
    public int DefaultVolume { get; set; } = 78;

    /// <summary>Hand audio passthrough to an AV receiver (currently a flag we
    /// echo back in the player's audio-track selection logic).</summary>
    public bool AudioPassthrough { get; set; }

    /// <summary>Apply EBU R128 loudness normalisation. Reduces dynamic range —
    /// useful for late-night anime watching but loses cinematic mixing.</summary>
    public bool NormalizeVolume { get; set; } = true;

    /// <summary>Play the title's theme music (anime OP/ED) when you open its
    /// detail page. Default off — autoplaying audio is intrusive, and browsers
    /// gate sound behind a user gesture anyway. Never plays on Home/catalog.</summary>
    public bool ThemeMusicEnabled { get; set; }

    /// <summary>Theme music playback volume 0..100 — plays softly under the hero.</summary>
    public int ThemeMusicVolume { get; set; } = 40;

    // ─── Language ────────────────────────────────────────────────────────────

    /// <summary>Interface language code (en/ru/uk/de/es). UI fetches
    /// <c>/lang/{code}.json</c> at boot.</summary>
    public string Language { get; set; } = "en";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
