namespace Animarr.App.Services;

/// <summary>
/// Player settings of THIS device — the display / HDMI chain (TV, projector,
/// AV receiver) ones and the playback speed — in the app's local
/// preferences rather than on the server.
/// </summary>
internal static class TvPlaybackPrefs
{
    private const string MatchFrameRateKey = "player_match_fps";
    private const string PassthroughKey = "player_passthrough";
    private const string SpeedKey = "player_speed";
    private const string SubSizeKey = "player_sub_size";
    private const string SubBgKey = "player_sub_bg";

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

    /// <summary>Subtitle text height as a fraction of the screen (Media3
    /// SubtitleView fractional size): 0.05 small … 0.09 huge, 0.06 default.</summary>
    public static float SubtitleSize
    {
        get => Math.Clamp(Preferences.Default.Get(SubSizeKey, 0.06f), 0.04f, 0.1f);
        set => Preferences.Default.Set(SubSizeKey, Math.Clamp(value, 0.04f, 0.1f));
    }

    /// <summary>Subtitle background: 0 none (outlined text), 1 translucent
    /// box, 2 black box.</summary>
    public static int SubtitleBackground
    {
        get => Math.Clamp(Preferences.Default.Get(SubBgKey, 0), 0, 2);
        set => Preferences.Default.Set(SubBgKey, Math.Clamp(value, 0, 2));
    }

    /// <summary>Playback speed, 0.5–2× (1 = normal); kept for the next video.</summary>
    public static float Speed
    {
        get => Math.Clamp(Preferences.Default.Get(SpeedKey, 1f), 0.5f, 2f);
        set => Preferences.Default.Set(SpeedKey, Math.Clamp(value, 0.5f, 2f));
    }
}
