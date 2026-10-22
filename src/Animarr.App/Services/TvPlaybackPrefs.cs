namespace Animarr.App.Services;

/// <summary>
/// Player settings of THIS device — they depend on its display and HDMI
/// chain (TV, projector, AV receiver), not on the profile, so they live in
/// the app's local preferences rather than on the server.
/// </summary>
internal static class TvPlaybackPrefs
{
    private const string MatchFrameRateKey = "player_match_fps";
    private const string PassthroughKey = "player_passthrough";

    /// <summary>Switch the display to the video's frame rate (24 / 25 / 50 Hz…)
    /// for judder-free motion. Off by default: most TVs blank for a second or
    /// two on every switch (start, next episode).</summary>
    public static bool MatchFrameRate
    {
        get => Preferences.Default.Get(MatchFrameRateKey, false);
        set => Preferences.Default.Set(MatchFrameRateKey, value);
    }

    /// <summary>"auto" (default): AC-3 / E-AC-3 / DTS / TrueHD go to the HDMI
    /// sink as they are when it reports it takes them (Media3's
    /// AudioCapabilities); "off": always decoded to PCM on the device — for a
    /// receiver that claims formats it then plays as silence or noise.</summary>
    public static bool PassthroughOff
    {
        get => Preferences.Default.Get(PassthroughKey, "auto") == "off";
        set => Preferences.Default.Set(PassthroughKey, value ? "off" : "auto");
    }
}
