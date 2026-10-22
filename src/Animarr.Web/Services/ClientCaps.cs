using System.Globalization;

namespace Animarr.Web.Services;

/// <summary>
/// What the playing client demuxes and decodes by itself, parsed once from a
/// start request: the <c>nativeCaps</c> token list (the TV app's TvCodecCaps
/// and, since plan task 1.2, the web player's MediaCapabilities probe — one
/// vocabulary), <c>maxAudioChannels</c>, and the older MSE flags
/// <c>clientHevc</c> / <c>clientHevc10</c>.
///
/// Tokens (lower-case, comma-separated):
///   • containers — mp4 m4v mov webm (browser) + mkv ts m2ts avi flv ogg wav (native players);
///   • video — h264 hevc hevc10 av1 vp9 vp8 mpeg4 mpeg2;
///   • audio — aac mp3 opus vorbis flac ac3 eac3 dts truehd pcm;
///   • display — <c>hdr</c> (shows HDR10/HLG as HDR), <c>dv</c> (decodes Dolby Vision itself);
///   • decoder size limits — <c>codec@WxH</c> (<c>hevc@4096x2160</c>, <c>av1@1920x1080</c>):
///     the largest frame that codec's decoder takes. Implies the plain codec
///     token; a source beyond it is not copied (it is re-encoded, scaled down
///     to fit where the output codec has a limit too). <c>hevc</c>'s limit
///     also covers <c>hevc10</c> unless that has its own.
/// A request without tokens (an older web build, curl) is <see cref="IsLegacyBrowser"/>:
/// the server keeps its browser defaults for it.
/// </summary>
public sealed class ClientCaps
{
    /// <summary>No report at all — a browser that only said (or didn't say) clientHevc.</summary>
    public static readonly ClientCaps Legacy = new(new HashSet<string>(), DefaultAudioChannels, false, false);

    /// <summary>Channel count assumed when the client doesn't report one: stereo.</summary>
    public const int DefaultAudioChannels = 2;

    private readonly HashSet<string> _tokens;

    private readonly Dictionary<string, (int W, int H)> _sizes = new(StringComparer.Ordinal);

    private ClientCaps(HashSet<string> tokens, int maxAudioChannels, bool hevc, bool hevc10, bool hlsRemux = false)
    {
        // codec@WxH: the limit, and the codec itself as a plain token.
        foreach (var t in tokens.ToList())
        {
            var at = t.IndexOf('@');
            if (at <= 0) continue;
            var x = t.IndexOf('x', at + 1);
            if (x < 0
                || !int.TryParse(t.AsSpan(at + 1, x - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(t.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                || w <= 0 || h <= 0) continue;
            var codec = t[..at];
            tokens.Add(codec);
            _sizes[codec] = _sizes.TryGetValue(codec, out var old) ? (Math.Max(old.W, w), Math.Max(old.H, h)) : (w, h);
        }
        _tokens = tokens;
        HlsRemux = hlsRemux;
        MaxAudioChannels = maxAudioChannels;
        Hevc = hevc || tokens.Contains("hevc");
        Hevc10 = hevc10 || tokens.Contains("hevc10");
    }

    public IReadOnlySet<string> Tokens => _tokens;

    /// <summary>True when the client sent a token list.</summary>
    public bool HasTokens => _tokens.Count > 0;

    /// <summary>No token list: decisions follow the conservative browser rules.</summary>
    public bool IsLegacyBrowser => !HasTokens;

    public bool Has(string token) => _tokens.Contains(token);

    /// <summary>The decoder size limit of a codec token (hevc10 falls back to
    /// hevc's); null = none reported. A video codec without a limit of its own,
    /// from a client that reported limits for others, gets the largest of
    /// those: a device whose HEVC/AV1/VP9 decoders stop at 1080p doesn't take
    /// 4K AVC either — the projector (h264 sent without a size) failed a
    /// 3840×2160 H.264 re-encode with NO_EXCEEDS_CAPABILITIES.</summary>
    public (int W, int H)? SizeLimit(string codec)
    {
        if (_sizes.TryGetValue(codec, out var s)) return s;
        if (codec == "hevc10" && _sizes.TryGetValue("hevc", out var h)) return h;
        if (_sizes.Count == 0 || !VideoCodecTokens.Contains(codec)) return null;
        (int W, int H) largest = (0, 0);
        foreach (var v in _sizes.Values)
            if ((long)v.W * v.H > (long)largest.W * largest.H) largest = v;
        return largest;
    }

    private static readonly HashSet<string> VideoCodecTokens = new(StringComparer.Ordinal)
        { "h264", "hevc", "hevc10", "av1", "vp9", "vp8", "mpeg4", "mpeg2" };

    /// <summary>Whether a <paramref name="width"/>×<paramref name="height"/>
    /// frame fits the codec's reported decoder limit (either orientation —
    /// decoders report the long and the short side); true without a limit.</summary>
    public bool FitsDecoder(string codec, int width, int height)
    {
        if (SizeLimit(codec) is not { } s || width <= 0 || height <= 0) return true;
        return (width <= s.W && height <= s.H) || (width <= s.H && height <= s.W);
    }

    /// <summary>The largest even output height at which a frame of the source's
    /// aspect fits the codec's decoder limit; 0 = it fits as is (or no limit).</summary>
    public int FitHeight(string codec, int width, int height)
    {
        if (FitsDecoder(codec, width, height) || SizeLimit(codec) is not { } s) return 0;
        var (lw, lh) = width >= height ? (Math.Max(s.W, s.H), Math.Min(s.W, s.H)) : (Math.Min(s.W, s.H), Math.Max(s.W, s.H));
        var scale = Math.Min((double)lw / width, (double)lh / height);
        return Math.Max(2, (int)Math.Floor(height * scale) & ~1);
    }

    /// <summary>
    /// The client wants a needed remux (the video plays, the container or the
    /// audio doesn't) as a key-frame-exact HLS copy (<c>remux=hls</c>, task 6.2)
    /// rather than /api/video's progressive pipe: seeking is then a segment
    /// fetch instead of a reload, and resume / scrub land on the same key
    /// frames. Direct Play is unaffected. Off = the /api/video Direct Stream
    /// as before (DLNA and older clients).
    /// </summary>
    public bool HlsRemux { get; }

    /// <summary>Most output channels the client can play (2 when not reported).</summary>
    public int MaxAudioChannels { get; }

    /// <summary>Decodes 8-bit HEVC (MSE flag or token).</summary>
    public bool Hevc { get; }

    /// <summary>Decodes HEVC Main10 (MSE flag or token).</summary>
    public bool Hevc10 { get; }

    /// <summary>The display shows HDR10 / HLG as HDR (<c>hdr</c> token).</summary>
    public bool Hdr => Has("hdr");

    /// <summary>The client decodes Dolby Vision itself (<c>dv</c> token).</summary>
    public bool DolbyVision => Has("dv");

    /// <summary>
    /// A native player (ExoPlayer class) rather than a browser: it declared a
    /// container no browser opens. Such a client demuxes MPEG-TS itself — so a
    /// TS stream may carry AC-3 / E-AC-3 / DTS, which hls.js's TS transmuxer
    /// can't hand to MSE — and its hardware decoders deinterlace.
    /// </summary>
    public bool NativePlayer => Has("mkv") || Has("ts") || Has("m2ts") || Has("avi");

    /// <summary>
    /// Whether the client says it decodes this audio codec (ffprobe codec
    /// name, with the aliases the TV derives from Android mime types: mp2 →
    /// <c>mp3</c>, any pcm_* → <c>pcm</c>). Always false without tokens.
    /// </summary>
    public bool DecodesAudio(string? codec)
    {
        var c = (codec ?? "").ToLowerInvariant();
        return c switch
        {
            "aac" or "mp4a"      => Has("aac"),
            "mp3" or "mp2"       => Has("mp3"),
            "ac3"                => Has("ac3"),
            "eac3" or "eac3_joc" => Has("eac3"),
            "dts"                => Has("dts"),
            "truehd"             => Has("truehd"),
            "opus"               => Has("opus"),
            "vorbis"             => Has("vorbis"),
            "flac"               => Has("flac"),
            _ when c.StartsWith("pcm", StringComparison.Ordinal) => Has("pcm"),
            _                    => false,
        };
    }

    /// <summary>
    /// These caps without the audio codecs this device failed to decode
    /// (PlaybackFailureService, task 6.1): they then count as not decodable —
    /// transcoded, and no longer a reason for Direct Play. A client without a
    /// token list is returned as is.
    /// </summary>
    public ClientCaps WithoutAudio(IReadOnlySet<string> codecs)
    {
        if (codecs.Count == 0 || !HasTokens) return this;
        var drop = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in codecs)
            drop.Add(c switch { "mp2" => "mp3", _ when c.StartsWith("pcm", StringComparison.Ordinal) => "pcm", _ => c });
        var tokens = _tokens.Where(t => !drop.Contains(t)).ToHashSet();
        return tokens.Count == _tokens.Count ? this : new ClientCaps(tokens, MaxAudioChannels, Hevc, Hevc10, HlsRemux);
    }

    /// <summary>
    /// Parses the start request's caps. Unknown tokens are kept (harmless);
    /// <paramref name="maxAudioChannels"/> outside 1..8 means "not reported" (2).
    /// </summary>
    public static ClientCaps Parse(string? nativeCaps, int? maxAudioChannels, string? clientHevc = null,
        string? clientHevc10 = null, string? remux = null)
    {
        var tokens = string.IsNullOrWhiteSpace(nativeCaps)
            ? new HashSet<string>()
            : nativeCaps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.ToLowerInvariant())
                        .Where(t => t.Length <= 16)
                        .Take(64)
                        .ToHashSet();
        var channels = maxAudioChannels is >= 1 and <= 8 ? maxAudioChannels.Value : DefaultAudioChannels;
        return new ClientCaps(tokens, channels, Flag(clientHevc), Flag(clientHevc10),
            string.Equals(remux?.Trim(), "hls", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The caps as /api/video query parameters (empty for a legacy
    /// client), so a Direct Stream URL the server hands out — and every
    /// <c>&amp;seek=N</c> reload of it — is decided for the same client.</summary>
    public string ToQuery()
    {
        if (!HasTokens) return MaxAudioChannels != DefaultAudioChannels
            ? "&maxAudioChannels=" + MaxAudioChannels.ToString(CultureInfo.InvariantCulture) : "";
        return "&nativeCaps=" + Uri.EscapeDataString(string.Join(',', _tokens.Order(StringComparer.Ordinal)))
             + "&maxAudioChannels=" + MaxAudioChannels.ToString(CultureInfo.InvariantCulture);
    }

    public override string ToString()
        => HasTokens ? $"[{string.Join(' ', _tokens.Order(StringComparer.Ordinal))}] {MaxAudioChannels}ch"
                     : $"legacy hevc={Hevc} hevc10={Hevc10} {MaxAudioChannels}ch";

    // "1" / "true" — the browser sends "1" (minimal API's bool binder rejects it).
    private static bool Flag(string? v)
        => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
}
