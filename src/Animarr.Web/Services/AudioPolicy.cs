namespace Animarr.Web.Services;

/// <summary>Where the audio ends up — each container carries a different set
/// of codecs, and each kind of player demuxes a different set of them.</summary>
public enum AudioOutput
{
    /// <summary>MPEG-TS HLS segments (the H.264 stream-copy plan).</summary>
    HlsTs,
    /// <summary>fMP4 HLS segments (the HEVC copy and every re-encode plan).</summary>
    HlsFmp4,
    /// <summary>/api/video's progressive fragmented MP4 (Direct Stream).</summary>
    Mp4Remux,
}

/// <summary>
/// What a stream does with the selected audio track: copy it (optionally
/// through a bitstream filter) or transcode it to <see cref="Codec"/> with
/// <see cref="Channels"/> channels.
/// </summary>
public sealed record AudioDecision(bool Copy, string Codec, int Channels, string? Bitrate = null,
    string? Filter = null, string? Bsf = null)
{
    /// <summary>The ffmpeg output options for the mapped audio stream.</summary>
    public IEnumerable<string> Args()
    {
        if (Copy)
        {
            yield return "-c:a"; yield return "copy";
            if (Bsf is not null) { yield return "-bsf:a"; yield return Bsf; }
            yield break;
        }
        yield return "-c:a"; yield return Codec;
        if (Filter is not null) { yield return "-af"; yield return Filter; }
        else { yield return "-ac"; yield return Channels.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        if (Bitrate is not null) { yield return "-b:a"; yield return Bitrate; }
    }

    /// <summary>RFC 6381 codec string for the master playlist's CODECS.</summary>
    public string CodecsTag => Codec switch
    {
        "aac"           => "mp4a.40.2",
        "mp3"           => "mp4a.40.34",
        "mp2"           => "mp4a.40.33",
        "ac3"           => "ac-3",
        "eac3"          => "ec-3",
        "opus"          => "opus",
        "flac"          => "fLaC",
        "dts"           => "dtsc",
        _               => "mp4a.40.2",
    };

    /// <summary>Short form for logs: "copy ac3", "aac 2ch 192k (downmix)".</summary>
    public override string ToString() => Copy
        ? $"copy {Codec}" + (Bsf is null ? "" : $" +{Bsf}")
        : $"{Codec} {Channels}ch {Bitrate}" + (Filter is null ? "" : " (downmix)");
}

/// <summary>
/// The audio half of a playback plan (master plan §5.5): copy what the client
/// decodes and the container carries; otherwise transcode — 5.1 when the
/// client plays 5.1, else a real stereo downmix.
/// </summary>
public static class AudioPolicy
{
    /// <summary>
    /// Stereo downmix that keeps the centre (dialogue) and LFE — the
    /// "Dave750" matrix Jellyfin ships: fronts and surrounds at −3 dB, centre
    /// and LFE at −6 dB. Any layout is first folded to 5.1 by swresample (7.1's
    /// back pair into the sides, 4.0 / 5.0 leave the missing channels silent),
    /// so the pan always finds the channels it names. Measured against bare
    /// <c>-ac 2</c> (ffmpeg 8 swresample, normalised matrix): centre and fronts
    /// come out 1.8 dB louder, the LFE is kept at −6 dB instead of whatever
    /// the swresample build defaults to — older builds dropped it entirely.
    /// </summary>
    public const string StereoDownmix =
        "aformat=channel_layouts=5.1(side),"
        + "pan=stereo|FL=0.707*FL+0.5*FC+0.707*SL+0.5*LFE|FR=0.707*FR+0.5*FC+0.707*SR+0.5*LFE";

    private static readonly AudioDecision AacStereo = new(false, "aac", 2, "192k");

    /// <summary>
    /// Decide for one audio track.
    /// </summary>
    /// <param name="codec">ffprobe codec of the MAPPED track; null = unknown
    /// (an external dub whose probe failed) → always transcoded.</param>
    /// <param name="channels">Its channel count (0 = unknown → treated as stereo).</param>
    /// <param name="profile">ffprobe profile — "DTS-HD MA" etc. picks the core extract.</param>
    public static AudioDecision Decide(string? codec, int channels, string? profile, ClientCaps client,
        AudioOutput output)
    {
        var c = Normalize(codec);
        // DTS Express (LBR) has no core a "dts" decoder could play — transcode it.
        var lbr = c == "dts" && string.Equals(profile, "DTS Express", StringComparison.OrdinalIgnoreCase);
        if (c.Length > 0 && !lbr && ClientTakes(c, client, output) && Carries(c, output, client.NativePlayer))
        {
            // DTS-HD MA / HRA / DTS:X: a client that decodes "dts" decodes the
            // core — every DTS-HD stream embeds one; dca_core drops the
            // extension (lossless / high-res) layers, a plain core passes
            // through untouched.
            var bsf = c == "dts" && IsDtsExtension(profile) ? "dca_core" : null;
            return new AudioDecision(true, c, channels, Bsf: bsf);
        }

        // Transcode. Keep the surround when the source has it and the client
        // plays it: E-AC-3 5.1 at 640k where the client decodes E-AC-3 (TVs,
        // Edge, Safari) and the container carries it, else AAC 5.1 — decoded
        // everywhere AAC is. Opus is never a target: in TS no player takes it,
        // and in MP4 its multichannel mapping is the least supported of the
        // three, while AAC 5.1 plays wherever Opus 5.1 would.
        if (channels > 2 && client.MaxAudioChannels >= 6)
        {
            if (client.Has("eac3") && Carries("eac3", output, client.NativePlayer))
                return new AudioDecision(false, "eac3", 6, "640k");
            return new AudioDecision(false, "aac", 6, "384k");
        }
        return channels > 2 ? AacStereo with { Filter = StereoDownmix } : AacStereo;
    }

    /// <summary>Copy candidates for a client WITHOUT a token list (browser
    /// defaults): AAC everywhere, MP3 in TS (hls.js) and in a progressive MP4
    /// (&lt;video&gt;), not in MSE-fed fMP4.</summary>
    private static bool ClientTakes(string codec, ClientCaps client, AudioOutput output)
    {
        if (client.HasTokens) return client.DecodesAudio(codec);
        return codec == "aac" || (codec == "mp3" && output is AudioOutput.HlsTs or AudioOutput.Mp4Remux);
    }

    /// <summary>
    /// Whether the container — and the client's demuxer for it — carries the
    /// codec. MPEG-TS: a native player's demuxer (ExoPlayer) takes AAC, MPEG
    /// audio, AC-3, E-AC-3 and DTS; hls.js transmuxes TS itself and passes on
    /// only AAC and MP3 (MP2 decodes in no browser). (f)MP4: AAC, MP3, AC-3,
    /// E-AC-3, Opus, FLAC (measured: both ffmpeg builds mux them without
    /// -strict), DTS for native players; TrueHD in MP4 is experimental in
    /// ffmpeg, Vorbis / PCM aren't MP4 codecs here, FLAC isn't a TS codec at all
    /// (ffmpeg silently drops it).
    /// </summary>
    private static bool Carries(string codec, AudioOutput output, bool nativePlayer) => output switch
    {
        AudioOutput.HlsTs => codec is "aac" or "mp3"
                             || (nativePlayer && codec is "mp2" or "ac3" or "eac3" or "dts"),
        _                 => codec is "aac" or "mp3" or "ac3" or "eac3" or "opus" or "flac"
                             || (nativePlayer && codec == "dts"),
    };

    private static string Normalize(string? codec)
    {
        var c = (codec ?? "").ToLowerInvariant();
        return c switch
        {
            "mp4a"     => "aac",
            "eac3_joc" => "eac3",
            _          => c,
        };
    }

    /// <summary>ffprobe profiles of DTS streams that carry more than the core:
    /// "DTS-HD MA", "DTS-HD HRA", "DTS-HD MA + DTS:X", "DTS-ES", "DTS 96/24".
    /// Plain "DTS" (core only) and unknown → false.</summary>
    private static bool IsDtsExtension(string? profile)
        => !string.IsNullOrEmpty(profile) && !string.Equals(profile, "DTS", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(profile, "unknown", StringComparison.OrdinalIgnoreCase);
}
