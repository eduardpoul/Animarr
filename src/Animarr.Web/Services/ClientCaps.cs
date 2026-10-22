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
///   • display — <c>hdr</c> (shows HDR10/HLG as HDR), <c>dv</c> (decodes Dolby Vision itself).
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

    private ClientCaps(HashSet<string> tokens, int maxAudioChannels, bool hevc, bool hevc10)
    {
        _tokens = tokens;
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
    /// Parses the start request's caps. Unknown tokens are kept (harmless);
    /// <paramref name="maxAudioChannels"/> outside 1..8 means "not reported" (2).
    /// </summary>
    public static ClientCaps Parse(string? nativeCaps, int? maxAudioChannels, string? clientHevc = null,
        string? clientHevc10 = null)
    {
        var tokens = string.IsNullOrWhiteSpace(nativeCaps)
            ? new HashSet<string>()
            : nativeCaps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.ToLowerInvariant())
                        .Where(t => t.Length <= 16)
                        .Take(64)
                        .ToHashSet();
        var channels = maxAudioChannels is >= 1 and <= 8 ? maxAudioChannels.Value : DefaultAudioChannels;
        return new ClientCaps(tokens, channels, Flag(clientHevc), Flag(clientHevc10));
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
