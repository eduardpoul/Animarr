namespace Animarr.Shared.Models;

/// <summary>
/// Per-user appearance + audio + language preferences. Returned by
/// <c>GET /api/me/preferences</c>; written piecewise by <c>PATCH</c> with
/// the same shape (any unset field on PATCH keeps its previous value).
/// </summary>
public sealed record UserPreferencesDto(
    int    AccentHue,
    bool   BackdropEnabled,
    int    BackdropBlurPx,
    int    BackdropBrightness,
    int    BackdropIntervalSec,
    bool   TvMode,
    string AudioPreferredLanguage,
    string SubtitlePreferredLanguage,
    int    SubtitleSize,
    int    DefaultVolume,
    bool   AudioPassthrough,
    bool   NormalizeVolume,
    string Language);
