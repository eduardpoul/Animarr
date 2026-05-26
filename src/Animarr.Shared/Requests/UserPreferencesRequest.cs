namespace Animarr.Shared.Requests;

/// <summary>PATCH body for <c>/api/me/preferences</c>. Every property is
/// optional — server applies whichever ones are non-null and keeps the
/// rest as they were. Pattern: tab in ProfilePanel patches just the
/// 3-4 fields it controls.</summary>
public sealed record UpdatePreferencesRequest(
    int?    AccentHue,
    bool?   BackdropEnabled,
    int?    BackdropBlurPx,
    int?    BackdropBrightness,
    int?    BackdropIntervalSec,
    bool?   TvMode,
    string? AudioPreferredLanguage,
    string? SubtitlePreferredLanguage,
    int?    SubtitleSize,
    int?    DefaultVolume,
    bool?   AudioPassthrough,
    bool?   NormalizeVolume,
    string? Language);

/// <summary>POST body for <c>/api/me/password</c>. Requires the current
/// password as an anti-CSRF measure on top of the auth cookie.</summary>
public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
