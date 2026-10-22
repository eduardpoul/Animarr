using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

// Synthetic HLS playlist (.m3u8) generation + VOD/live variants (ProbeInfo/HlsPlan live here).
public sealed partial class HlsSessionService
{
    // ─── Synthetic playlist generation ─────────────────────────────────────

    private sealed record ProbeInfo(double DurationSec, string? VideoCodec, string? AudioCodec,
        int Width, int Height, string? CodecsAttribute, bool HasDolbyVision, bool Is10Bit,
        // 2026-05-27: extended for PlayerOutputInfo. ColorTransfer drives
        // HDR10 / HLG detection (smpte2084 vs arib-std-b67), AudioChannels +
        // AudioLanguage feed the right-side meta plashka in the player HUD.
        string? ColorTransfer, int AudioChannels, string? AudioLanguage,
        // Estimated B-frame reorder delay (seconds) = has_b_frames / fps — used
        // as the default audio -itsoffset on the fMP4 stream-copy path so the
        // composition delay (audio-ahead) is corrected without re-encoding.
        double ReorderDelaySec = 0)
    {
        /// <summary>Absolute index of the main video stream (PickMainVideo) —
        /// mapped as <c>0:N</c>. -1 = unknown → <c>0:V:0?</c>.</summary>
        public int VideoStreamIndex { get; init; } = -1;

        /// <summary>The ffmpeg <c>-map</c> for the video: the exact stream the
        /// probe analysed, so a cover picture in front of it is never encoded.</summary>
        public string VideoMap => VideoStreamIndex >= 0
            ? "0:" + VideoStreamIndex.ToString(CultureInfo.InvariantCulture)
            : "0:V:0?";

        /// <summary>
        /// Where the main video stream ends (seconds; 0 = unknown), when the
        /// container says so — the stream duration, or Matroska's per-track
        /// DURATION tag. The format duration is the LONGEST stream's: a
        /// subtitle track running to 20.8 s behind video that ends at 20.02 s
        /// announced a 0.8 s seg-5 no ffmpeg run ever writes (no frame left to
        /// start it with) — 503 for the player until it gave up.
        /// </summary>
        public double VideoEndSec { get; init; }

        /// <summary>Dolby Vision profile / level / compatibility (None when
        /// <see cref="HasDolbyVision"/> is false).</summary>
        public DoviInfo Dovi { get; init; } = DoviInfo.None;

        /// <summary>ffprobe codec of every subtitle stream, index = ffmpeg's
        /// <c>s:N</c> (the burn-in's track number).</summary>
        public IReadOnlyList<string?> SubtitleCodecs { get; init; } = [];

        /// <summary>The video is interlaced (ffprobe field_order tt / bb / tb /
        /// bt). Browsers don't deinterlace — combing on every motion — so for
        /// them it is re-encoded, and every re-encode deinterlaces it (bwdif /
        /// deinterlace_vaapi / bwdif_cuda). Native players' decoders deinterlace
        /// themselves.</summary>
        public bool Interlaced { get; init; }

        /// <summary>Pixel aspect ratio as ffprobe reports it ("32:27"): the
        /// CONTAINER's when it has one (Matroska DisplayWidth/Height, MP4 pasp),
        /// else the bitstream's. null / "1:1" / "0:1" = square or unknown.</summary>
        public string? SampleAspectRatio { get; init; }

        /// <summary>ffprobe codec_tag_string of the video (MP4 sample entry:
        /// hvc1 / hev1 / dvh1 / dvhe; "[0][0][0][0]" for Matroska).</summary>
        public string? VideoCodecTag { get; init; }

        /// <summary>ffprobe profile of the mapped audio track ("DTS-HD MA",
        /// "LC" …) — AudioPolicy picks the DTS core extract from it.</summary>
        public string? AudioProfile { get; init; }

        /// <summary>The video half of <see cref="CodecsAttribute"/> (hvc1… /
        /// avc1…; null = unknown) — a copy plan pairs it with the CODECS of the
        /// audio it actually sends.</summary>
        public string? VideoCodecsAttribute { get; init; }

        /// <summary>Whole-file bitrate (bits/s, ffprobe <c>format.bit_rate</c>;
        /// 0 = unknown) — what a copy plan puts on the wire. A bitrate cap at or
        /// above it can't save anything and is ignored.</summary>
        public long BitRate { get; init; }

        /// <summary>The main video stream's own bitrate (bits/s; stream
        /// <c>bit_rate</c> or Matroska's <c>BPS</c> tag; 0 = unknown).</summary>
        public long VideoBitRate { get; init; }

        /// <summary>Every audio stream, index = ffmpeg's <c>a:N</c>. Empty for
        /// the seek-restart's minimal probe.</summary>
        public IReadOnlyList<AudioStreamInfo> AudioStreams { get; init; } = [];

        /// <summary>
        /// This probe with the top-level audio fields (codec / channels /
        /// language — first track by default) describing the track the session
        /// actually maps: source track <paramref name="audioTrackIndex"/>, or
        /// that track of the external dub (<paramref name="external"/>, probed
        /// separately). Everything downstream — TS passthrough, master-playlist
        /// CODECS, the Info block, the seek-restart — then talks about the
        /// SELECTED track: switching to the English track used to keep
        /// reporting "AC-3 5.1 (RUS)" from track 0.
        /// </summary>
        public ProbeInfo ForAudioTrack(int audioTrackIndex, ProbeInfo? external, bool hasExternal)
        {
            var list = hasExternal ? external?.AudioStreams : AudioStreams;
            if (list is null || audioTrackIndex < 0 || audioTrackIndex >= list.Count)
            {
                // External dub that couldn't be probed: its language is
                // unknown — don't claim the source's.
                return hasExternal ? this with { AudioLanguage = null } : this;
            }
            var a = list[audioTrackIndex];
            return this with
            {
                AudioCodec = a.Codec, AudioChannels = a.Channels, AudioLanguage = a.Language, AudioProfile = a.Profile,
            };
        }
    }

    /// <summary>One audio stream of a probed file.</summary>
    private sealed record AudioStreamInfo(string? Codec, int Channels, string? Language, string? Profile = null);

    /// <summary>
    /// What ffmpeg invocation + segment shape this session needs. Decided once
    /// from the probe and stuck to for the session's lifetime.
    /// </summary>
    private enum HlsPlan
    {
        /// <summary>H.264 video → MPEG-TS HLS with stream-copy video + audio.
        /// PCR-based A/V sync inside TS solves the "audio ahead" wobble that
        /// our fMP4 path has. No GPU needed.</summary>
        TsStreamCopy,

        /// <summary>HEVC 8-bit → fMP4 HLS with VAAPI re-encode to H.264 +bf 0.
        /// AMD GCN+/Intel Gen9+ can encode 8-bit; re-encode flattens B-frame
        /// reorder.</summary>
        Fmp4VaapiReencode,

        /// <summary>HEVC 8/10-bit → fMP4 HLS with NVENC re-encode to H.264.
        /// NVIDIA NVENC can handle BOTH 8-bit and 10-bit HEVC decode + H.264
        /// encode (Pascal+), so this plan beats Fmp4StreamCopy for any HEVC
        /// content when an NVIDIA GPU is available.</summary>
        Fmp4NvencReencode,

        /// <summary>HEVC 10-bit HDR / DV → fMP4 HLS with stream-copy video.
        /// Used when neither VAAPI (Vega 11 can't encode Main10) nor NVENC
        /// is available. Some residual A/V wobble; recommend DLNA cast or
        /// mpv for sync-critical HDR/DV playback.</summary>
        Fmp4StreamCopy,

        /// <summary>Software (libx264) re-encode to H.264 + optional downscale.
        /// The universal CPU fallback for the quality-ladder downscale path when
        /// no usable GPU encoder is present (no NVENC, no VAAPI). CPU-heavy —
        /// especially decoding 4K HEVC 10-bit in software — but it's the only
        /// way to honour a "give me 1080p" request on a GPU-less host. Per the
        /// project rule: GPU if present, else CPU, and if the box can't keep up
        /// that's on the operator (don't downscale).</summary>
        Fmp4SoftwareReencode,
    }

    /// <summary>
    /// A subtitle track drawn into the picture (<c>burnSubtitle=N</c>): a
    /// bitmap track (PGS / VobSub / DVB) is overlaid from the source's own
    /// <c>0:s:N</c> stream; a text / ASS track is rendered by libass from its
    /// ASS conversion (<see cref="AssPath"/>) with the file's attached fonts
    /// (<see cref="FontsDir"/>, null = none).
    /// </summary>
    private sealed record SubtitleBurn(int Index, bool Image, string? AssPath, string? FontsDir);

    /// <summary>What a re-encode plan writes (decided once per session, see
    /// <see cref="ChooseVideoTarget"/>).</summary>
    private enum VideoTarget
    {
        /// <summary>H.264 8-bit SDR — decodes everywhere; HDR is tone-mapped.</summary>
        H264,
        /// <summary>HEVC Main10, SDR: a Hi10P (10-bit H.264) source for a client
        /// that decodes HEVC Main10 — the 10-bit gradients survive instead of
        /// banding in an 8-bit H.264.</summary>
        Hevc10,
        /// <summary>HEVC Main10 that STAYS HDR10 / HLG (BT.2020 + PQ / HLG tags,
        /// no tone mapping): an HDR source that must be re-encoded — a
        /// downscale / bitrate cap, a codec the client lacks — for a client
        /// that decodes HEVC Main10 and shows HDR.</summary>
        Hevc10Hdr,
    }

    /// <summary>
    /// The re-encode's output format. HEVC Main10 needs a client that decodes
    /// it (<c>hevc10</c> token / clientHevc10) AND an encoder for it on this
    /// plan: NVENC can (probed), VAAPI only where vainfo lists a Main10 encode
    /// entry point — the prod Vega 11 doesn't, so it stays on H.264 there —
    /// and the CPU plan never (x265 is far too slow for real time). Otherwise
    /// today's H.264 8-bit.
    /// </summary>
    private VideoTarget ChooseVideoTarget(HlsPlan plan, ProbeInfo? probe, ClientCaps client, bool burning = false)
    {
        if (probe is null || !client.Hevc10) return VideoTarget.H264;
        bool main10 = plan switch
        {
            HlsPlan.Fmp4NvencReencode => _hardware?.Current.NvencHevc10Encode == true,
            HlsPlan.Fmp4VaapiReencode => _hardware?.Current.VaapiHevc10Encode == true,
            _                         => false,
        };
        if (!main10) return VideoTarget.H264;
        // HDR10 / HLG — including DV P8 / P7, whose base layer is one — keeps
        // its HDR for an HDR display (`hdr` token) instead of being tone-mapped.
        // DV P5 has no such base (IPTPQc2): it is always tone-mapped.
        // (Not with a burned-in subtitle: libass / overlay draw SDR white,
        // which in a PQ picture is a 10 000-nit glare — those are tone-mapped.)
        if (client.Hdr && !burning && HdrTransferOf(probe) is not null) return VideoTarget.Hevc10Hdr;
        bool hi10p = string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) && probe.Is10Bit;
        return hi10p ? VideoTarget.Hevc10 : VideoTarget.H264;
    }

    /// <summary>The source's HDR transfer ("smpte2084" = PQ / HDR10,
    /// "arib-std-b67" = HLG) when a Main10 re-encode can keep it; null for SDR
    /// and for DV without a compatible base layer (P5).</summary>
    private static string? HdrTransferOf(ProbeInfo probe)
    {
        if (probe.Dovi.Present && !probe.Dovi.CompatibleBase) return null;
        var t = (probe.ColorTransfer ?? "").ToLowerInvariant();
        return t is "smpte2084" or "arib-std-b67" ? t : null;
    }

    /// <summary>Encoder colour tags of a <see cref="VideoTarget.Hevc10Hdr"/>
    /// output — BT.2020 with the source's transfer, so players switch to HDR
    /// (a scale filter or the encoder must not leave them unspecified).</summary>
    private static string[] HdrOutputTags(ProbeInfo? probe)
        => ["-color_primaries", "bt2020", "-color_trc", (probe is null ? null : HdrTransferOf(probe)) ?? "smpte2084",
            "-colorspace", "bt2020nc"];

    /// <summary>RFC 6381 string of a HEVC Main10 re-encode for the master
    /// playlist: Main10, level by output height (L3.1 / L4 / L5.1).</summary>
    private static string Hevc10CodecsTag(int height)
        => $"hvc1.2.4.L{(height <= 720 ? 93 : height <= 1080 ? 120 : 153)}.B0";

    /// <summary>H.264 video bitrate ladder by OUTPUT height (target kbps).
    /// Used by every re-encode plan so a 720p stream isn't shipped at 4K
    /// bitrate (and vice-versa). Tuned for LAN delivery — the old ladder
    /// (5M @ 1080p) blocked badly on anime via the VAAPI encoder. On a local
    /// network bitrate is ~free, so aim high; the player's Bitrate menu caps it
    /// when bandwidth actually matters.
    /// maxrate = 1.25×, VBV buffer = 2× the target (EncodeRate).</summary>
    private static long LadderKbps(int h) => h switch
    {
        <= 0    => 12_000,   // unknown → 1080-ish default
        <= 360  => 1_500,    // (360p used to fall on the 480p rung's 2.5 Mbps)
        <= 480  => 2_500,
        <= 576  => 3_500,
        <= 720  => 6_000,
        <= 1080 => 12_000,
        <= 1440 => 24_000,
        _       => 40_000,   // 2160p+
    };

    /// <summary>One encode's video bitrate: target / peak (maxrate) / VBV buffer.</summary>
    private readonly record struct EncodeRate(long Kbps)
    {
        public string V   => $"{Kbps}k";
        public string Max => $"{PeakKbps}k";
        public string Buf => $"{Kbps * 2}k";
        public long PeakKbps => Kbps * 5 / 4;
    }

    /// <summary>Lowest video bitrate an encode is ever given (kbps): below this
    /// even 480p falls apart.</summary>
    private const long MinEncodeKbps = 800;

    /// <summary>
    /// The bitrate a re-encode runs at. Starts from the ladder for the OUTPUT
    /// height (<paramref name="outHeight"/> — the downscale target, else the
    /// SOURCE height: no-downscale GPU re-encodes used to fall on the "unknown"
    /// rung, a fixed 12 Mbps for 480p and 4K alike, as did every software
    /// re-encode after a seek-restart, whose minimal probe had no height), then:
    ///   • never above what the source itself spends — re-encoding a 3 Mbps
    ///     file at 12 Mbps adds bytes, not quality. H.264 is given 1.5× the
    ///     budget of a HEVC / AV1 / VP9 source (same picture, older codec);
    ///   • the player's Bitrate menu (<paramref name="maxBitrateMbps"/>) is a
    ///     ceiling on top: "≤ N Mbps" — it no longer raises a 720p rung to N;
    ///   • never below <see cref="MinEncodeKbps"/>.
    /// </summary>
    private static EncodeRate RateFor(int outHeight, int maxBitrateMbps, ProbeInfo? source = null)
    {
        long kbps = LadderKbps(outHeight);
        var srcBps = source is null ? 0 : source.VideoBitRate > 0 ? source.VideoBitRate : source.BitRate;
        if (srcBps > 0)
        {
            var efficient = (source!.VideoCodec ?? "").ToLowerInvariant() is "hevc" or "av1" or "vp9";
            kbps = Math.Min(kbps, (long)(srcBps / 1000.0 * (efficient ? 1.5 : 1.0)));
        }
        if (maxBitrateMbps > 0) kbps = Math.Min(kbps, (long)maxBitrateMbps * 1000);
        return new EncodeRate(Math.Max(kbps, MinEncodeKbps));
    }

    /// <summary>
    /// The bitrate cap (Mbps) that actually applies: 0 when there is none or
    /// when it is at/above the source's own bitrate. Such a cap can't make the
    /// stream any smaller, so it must not force a re-encode either — it used
    /// to turn a stream copy into a full transcode for nothing.
    /// </summary>
    private static int EffectiveBitrateCap(int maxBitrateMbps, ProbeInfo? probe)
        => maxBitrateMbps > 0 && probe is { BitRate: > 0 } p && maxBitrateMbps * 1_000_000L >= p.BitRate
            ? 0
            : Math.Max(0, maxBitrateMbps);

    /// <summary>
    /// The main reason code (<see cref="ReasonCodes"/>) for the plan
    /// <see cref="ChoosePlan"/> picked — the same order of checks: a copy is a
    /// container remux; a re-encode is the user's cap, else an interlaced
    /// source for a browser, else Dolby Vision P5, else a codec the client
    /// can't decode.
    /// </summary>
    private static string PlanReason(HlsPlan plan, ProbeInfo probe, ClientCaps client, int maxHeight, int maxBitrate,
        bool burning = false)
    {
        if (plan is HlsPlan.TsStreamCopy or HlsPlan.Fmp4StreamCopy) return ReasonCodes.ContainerUnsupported;
        if (maxHeight > 0 && probe.Height > 0 && maxHeight < probe.Height) return ReasonCodes.UserQualityCap;
        if (maxBitrate > 0) return ReasonCodes.UserBitrateCap;
        if (burning) return ReasonCodes.BurnedSubtitle;
        if (probe.Interlaced && !client.NativePlayer) return ReasonCodes.Interlaced;
        if (DolbyVision.For(probe.Dovi, client) == DoviHandling.Reencode) return ReasonCodes.DolbyVision;
        return ReasonCodes.CodecUnsupported;
    }

    /// <summary>Pick the playback plan from the probe + detected hardware.
    ///   • Downscale OR bitrate cap requested → re-encode (GPU if available).
    ///   • H.264 (any bit depth) → MPEG-TS stream-copy. Works without any GPU.
    ///   • HEVC + client decodes HEVC → fMP4 stream-copy (Direct Stream): the
    ///     ORIGINAL bitstream, zero quality loss, fastest start. Covers 8-bit
    ///     AND 10-bit/HDR. Default once the browser reports HEVC via clientHevc.
    ///   • HEVC + client CAN'T decode HEVC → re-encode to H.264 (NVENC, else
    ///     VAAPI — 10-bit only where the GPU decodes Main10) for browser
    ///     compatibility, HDR tone-mapped to SDR; otherwise stream-copy (best
    ///     effort — needs a HEVC client or cast).
    /// clientHevc = MediaSource.isTypeSupported('…hvc1…') on the client.</summary>
    private HlsPlan ChoosePlan(ProbeInfo probe, ClientCaps client, int maxHeight = 0, int maxBitrate = 0,
        bool burning = false)
    {
        bool clientHevc = client.Hevc;
        // Quality-ladder downscale: the user picked a resolution below the
        // source. Downscaling REQUIRES a re-encode (a stream-copy can't be
        // shrunk), so pick the best available encoder regardless of source
        // codec/bit-depth:
        //   NVENC  — decodes H.264 + HEVC 8/10-bit, encodes H.264. Preferred.
        //   VAAPI  — Vega/Intel decode H.264 + HEVC (incl. 10-bit on VCN); we
        //            encode H.264 8-bit so the Main10-ENCODE limitation doesn't
        //            apply. HDR is tone-mapped to SDR (libplacebo / tonemapx).
        //   libx264 — CPU fallback when no GPU encoder is present.
        // A burned-in subtitle changes the picture: it needs a re-encode too.
        if ((maxHeight > 0 && probe.Height > 0 && maxHeight < probe.Height) || maxBitrate > 0 || burning)
        {
            if (_hardware?.Current.Nvenc.Available == true) return HlsPlan.Fmp4NvencReencode;
            if (_hardware?.Current.Vaapi.Available == true) return HlsPlan.Fmp4VaapiReencode;
            return HlsPlan.Fmp4SoftwareReencode;
        }

        // Interlaced video for a browser: hls.js / MSE show the fields woven
        // together (combing on every motion) — re-encode, which deinterlaces.
        // Native players (they declare mkv / ts / avi) deinterlace in their
        // decoders and keep the copy.
        if (probe.Interlaced && !client.NativePlayer)
        {
            if (_hardware?.Current.Nvenc.Available == true) return HlsPlan.Fmp4NvencReencode;
            if (_hardware?.Current.Vaapi.Available == true
                && (!probe.Is10Bit || _hardware.Current.VaapiHevc10Decode))
                return HlsPlan.Fmp4VaapiReencode;
            return HlsPlan.Fmp4SoftwareReencode;
        }

        // No downscale → original codec-driven choice. 8-bit H.264 only:
        // 10-bit "Hi10P" (old fansubs) decodes in no browser and on no TV SoC —
        // the Direct Play / Direct Stream / native gates already say so — and a
        // stream-copy of it was a black screen with no further fallback. It
        // re-encodes below like the legacy codecs.
        if (string.Equals(probe.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) && !probe.Is10Bit)
            return HlsPlan.TsStreamCopy;

        if (string.Equals(probe.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            // Dolby Vision P5 has no base layer a plain HEVC decoder shows
            // right (green / purple): for a client without DV decoding it is
            // never copied — it re-encodes below, tone-mapped (DolbyVision.For).
            bool mustReencode = DolbyVision.For(probe.Dovi, client) == DoviHandling.Reencode;

            // Browser decodes HEVC → ship the original bitstream untouched
            // (Direct Stream). No re-encode, no quality loss, fast start —
            // 8-bit and 10-bit/HDR alike. This is the path that makes Animarr
            // match a native client's quality instead of re-encoding to H.264.
            // 10-bit needs the Main10 capability (clientHevc10 / hevc10), not
            // just "decodes HEVC" — a Main-only decoder can't take it.
            if ((probe.Is10Bit ? client.Hevc10 : clientHevc) && !mustReencode)
                return HlsPlan.Fmp4StreamCopy;

            // Browser can't decode HEVC → re-encode to H.264 for compatibility
            // (10-bit HDR gets tone-mapped to SDR on the way, see ToneMapperFor).
            // NVENC first; then VAAPI — for 10-bit only when the GPU decodes
            // Main10 (vainfo VAProfileHEVCMain10/VLD; the Vega 11 host does,
            // older iGPUs don't); finally stream-copy fallback.
            if (_hardware?.Current.Nvenc.Available == true)
                return HlsPlan.Fmp4NvencReencode;
            if (_hardware?.Current.Vaapi.Available == true
                && (!probe.Is10Bit || _hardware.Current.VaapiHevc10Decode))
                return HlsPlan.Fmp4VaapiReencode;
            // HEVC: let the browser decode the bitstream — except DV P5, which
            // only a CPU re-encode can show correctly here.
            return mustReencode ? HlsPlan.Fmp4SoftwareReencode : HlsPlan.Fmp4StreamCopy;
        }

        // Anything else — mpeg4/XviD (AVI), MPEG-2, VC-1, MPEG-1, … — the browser
        // can't play it and it can't be stream-copied into fMP4 (AVI carries no
        // valid pts → "pts has no value"). Re-encode to H.264. DECODE stays in
        // software (GPU decoders rarely accept these legacy codecs — the arg
        // builders skip -hwaccel for them), but the ENCODE — the expensive
        // half — goes to the GPU when one is present: sw-decode → hwupload →
        // h264_vaapi (or h264_nvenc straight from system frames). "libx264 is
        // cheap for low-res" proved false in practice: a 704×396 XviD pinned a
        // Ryzen 2400GE at ~80% total.
        if (_hardware?.Current.Nvenc.Available == true) return HlsPlan.Fmp4NvencReencode;
        if (_hardware?.Current.Vaapi.Available == true) return HlsPlan.Fmp4VaapiReencode;
        return HlsPlan.Fmp4SoftwareReencode;
    }

    private static string BuildMasterPlaylist(ProbeInfo p, long bandwidthBps = 0)
    {
        // BANDWIDTH: the real peak when known (the encode's maxrate, a copy's
        // source bitrate), else a rough estimate from the resolution. hls.js
        // requires the attribute to exist; it doesn't have to be exact.
        var bandwidth = bandwidthBps > 0 ? bandwidthBps : EstimatedBandwidth(p.Width, p.Height);
        var codecs = p.CodecsAttribute ?? "avc1.640028,mp4a.40.2";
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:7\n");
        sb.Append($"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth},RESOLUTION={p.Width}x{p.Height},CODECS=\"{codecs}\"\n");
        sb.Append("media.m3u8\n");
        return sb.ToString();
    }

    private static string BuildMediaPlaylist(SegmentLayout layout, HlsPlan plan,
        double startOffsetSec = 0, IReadOnlyCollection<int>? restartBoundaries = null)
    {
        // TARGETDURATION must be >= every #EXTINF value (HLS spec). We round
        // up the segment length to be safe; an anchored layout can announce a
        // longer segment around its anchor.
        var target = Math.Max((int)Math.Ceiling(SegmentDurationSec) + 1, (int)Math.Ceiling(layout.MaxDuration));
        var segCount = layout.Count;

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        // VERSION 7 needed for fMP4 (#EXT-X-MAP). MPEG-TS works at 3 and is the
        // version Plex/Jellyfin/Emby emit — keeps maximum smart-TV compat.
        sb.Append(plan == HlsPlan.TsStreamCopy ? "#EXT-X-VERSION:3\n" : "#EXT-X-VERSION:7\n");
        sb.Append($"#EXT-X-TARGETDURATION:{target}\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-INDEPENDENT-SEGMENTS\n");
        if (startOffsetSec > 0)
            sb.Append($"#EXT-X-START:TIME-OFFSET={startOffsetSec.ToString("F3", CultureInfo.InvariantCulture)},PRECISE=YES\n");
        // MPEG-TS segments are self-contained (each carries its own PAT/PMT and
        // codec bootstrap data) — no init segment, no EXT-X-MAP. fMP4 needs
        // both. This is the whole reason MPEG-TS is so robust across decoders.
        if (plan != HlsPlan.TsStreamCopy)
            sb.Append("#EXT-X-MAP:URI=\"init.mp4\"\n");

        // EXT-X-DISCONTINUITY policy is plan-specific:
        //
        // • fMP4 (`Fmp4*`): emit BEFORE EVERY segment after seg-0. Reason:
        //   seek-restart spawns a fresh ffmpeg whose encoder may emit
        //   slightly different codec parameters (SPS/PPS, SAR bytes, etc.).
        //   The fMP4 TFDT box bug also means new-encoder segments may have
        //   the wrong fragment start time. hls.js detects mid-segment drift
        //   and reloading the decoder produces the ~200ms audio-ahead
        //   wobble. Pre-emptive markers tell hls.js to plan for a fresh
        //   decoder at every boundary — when codec params match (the
        //   common case within one run), the decoder doesn't actually
        //   reload, cost is well under 1 ms per segment.
        //
        // • MPEG-TS (`TsStreamCopy`): emit ONLY at actual restart points.
        //   Reason: TS carries absolute time via PCR in every packet, and
        //   stream-copy from a single ffmpeg run produces monotonically
        //   increasing PCR with no genuine discontinuity. Marking every
        //   segment as discontinuous makes hls.js EXPECT a PCR reset at
        //   each boundary, see that it doesn't happen, and stall trying to
        //   resync. This is what was causing the "plays 2 sec then freezes
        //   solid" bug. Plex/Jellyfin/Emby's TS playlists confirm: they
        //   only emit DISCONTINUITY at real codec/seek transitions.
        var boundarySet = restartBoundaries is { Count: > 0 }
            ? new HashSet<int>(restartBoundaries)
            : null;
        bool emitDiscontinuityEverywhere = plan != HlsPlan.TsStreamCopy;

        var ext = SegmentExtension(plan);
        for (int i = 0; i < segCount; i++)
        {
            bool emitMarker = i > 0 && (emitDiscontinuityEverywhere
                                     || (boundarySet?.Contains(i) ?? false));
            if (emitMarker) sb.Append("#EXT-X-DISCONTINUITY\n");
            var dur = layout.Duration(i);
            sb.Append($"#EXTINF:{dur.ToString("F3", CultureInfo.InvariantCulture)},\n");
            sb.Append($"seg-{i:D5}.{ext}\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// Segment layout of the synthetic VOD playlist — where each seg-N starts.
    ///
    /// <b>Uniform</b>: fixed-length segments with the remainder in the last one,
    /// except that a remainder shorter than <see cref="MinTailSec"/> is folded
    /// into the previous segment instead of being announced on its own. The
    /// format duration is usually a hair longer than the video (audio runs on
    /// for a few ms), e.g. 180.023 s → a 46th segment of 0.023 s. ffmpeg starts
    /// a segment only at a key frame past the boundary, so with no frame left
    /// after 180.0 it never wrote seg-45: the player got 503 for it, kept
    /// retrying, and the retries turned into seek-jump restarts. Folded, seg-44
    /// is announced as 4.023 s — what ffmpeg really writes there (up to EOF).
    ///
    /// <b>Anchored</b> (a stream-copy run that starts mid-file): segment J
    /// starts exactly at the key frame the copy has to start from, and its
    /// announced end lies inside what ffmpeg really writes into it. Why it must:
    /// hls.js (and ExoPlayer) place the FIRST fragment they load at its
    /// announced start — they compute the timestamp offset from it — so a copy
    /// that starts at a key frame 6 s before its announced start shifted the
    /// whole timeline by 6 s (the resume point, the external subtitles and the
    /// progress reports all off), and with sparse key frames the start
    /// position wasn't buffered at all. Segments before J keep the 4 s grid,
    /// with seg-(J-1) stretched to end at the anchor; segments after J continue
    /// the 4 s grid from J's end.
    /// </summary>
    public sealed class SegmentLayout
    {
        public const double MinTailSec = 0.5;

        private readonly double[] _starts;

        /// <summary>Full file duration the playlist spans.</summary>
        public double TotalSec { get; }

        /// <summary>First segment of the anchored part (0 for a uniform layout).</summary>
        public int AnchorIndex { get; }

        public int Count => _starts.Length;

        /// <summary>Announced start of segment <paramref name="index"/> (seconds).</summary>
        public double Start(int index) => _starts[index];

        /// <summary>EXTINF of segment <paramref name="index"/>.</summary>
        public double Duration(int index)
            => Math.Max((index + 1 < _starts.Length ? _starts[index + 1] : TotalSec) - _starts[index], 0);

        /// <summary>Longest EXTINF — TARGETDURATION must cover it.</summary>
        public double MaxDuration { get; }

        /// <summary>Segment whose announced range contains <paramref name="t"/>
        /// (clamped to the first / last segment).</summary>
        public int IndexOf(double t)
        {
            if (_starts.Length == 0) return 0;
            var i = Array.BinarySearch(_starts, t);
            if (i < 0) i = ~i - 1;
            return Math.Clamp(i, 0, _starts.Length - 1);
        }

        private SegmentLayout(double[] starts, double total, int anchorIndex)
        {
            _starts = starts;
            TotalSec = total;
            AnchorIndex = anchorIndex;
            double max = 0;
            for (int i = 0; i < starts.Length; i++) max = Math.Max(max, Duration(i));
            MaxDuration = max;
        }

        /// <summary>The plain 4 s grid (0 segments = unknown duration).
        /// <paramref name="contentEndSec"/> = where the video really ends when
        /// that is before <paramref name="totalSec"/> (0 = unknown): no segment
        /// is announced to START past it — see <see cref="ProbeInfo.VideoEndSec"/>.</summary>
        public static SegmentLayout Uniform(double totalSec, double segmentSec, double contentEndSec = 0)
            => Build(totalSec, segmentSec, prefix: 0, anchorStart: 0, firstEnd: segmentSec, contentEndSec);

        /// <summary>
        /// Layout for a stream copy whose first run starts at key frame
        /// <paramref name="keyframeSec"/> (the last one at or before the resume
        /// point). <paramref name="firstEndSec"/> = where segment J is announced
        /// to end — at most where ffmpeg will really cut it (see the caller),
        /// and past the resume point so the player asks for J first.
        /// </summary>
        public static SegmentLayout Anchored(double totalSec, double segmentSec, double keyframeSec, double firstEndSec,
            double contentEndSec = 0)
        {
            if (totalSec <= 0 || segmentSec <= 0) return Uniform(totalSec, segmentSec, contentEndSec);
            var j = (int)Math.Floor(keyframeSec / segmentSec + 1e-6);
            // A key frame in the first segment: start from the file's beginning
            // (it has a key frame at 0) — there's no segment before it to stretch.
            var anchor = j == 0 ? 0 : Math.Max(keyframeSec, j * segmentSec);
            return Build(totalSec, segmentSec, prefix: j, anchorStart: anchor,
                firstEnd: Math.Max(firstEndSec, anchor + segmentSec), contentEndSec);
        }

        private static SegmentLayout Build(double total, double seg, int prefix, double anchorStart, double firstEnd,
            double contentEnd)
        {
            if (total <= 0 || seg <= 0) return new SegmentLayout([], 0, 0);
            // Segments are only announced to START while there is still video
            // to put in them; the last one's EXTINF still runs to `total`.
            var end = contentEnd > 0 && contentEnd < total ? contentEnd : total;
            var starts = new List<double>();
            for (int i = 0; i < prefix && i * seg < end; i++) starts.Add(i * seg);
            if (anchorStart < end)
            {
                var anchorIndex = starts.Count;
                starts.Add(anchorStart);
                for (var s = firstEnd; s < end; s += seg) starts.Add(s);
                // Fold a tail shorter than MinTailSec into the segment before it
                // — never the anchor itself (a resume in the last second).
                if (starts.Count - 1 > anchorIndex && end - starts[^1] < MinTailSec)
                    starts.RemoveAt(starts.Count - 1);
                return new SegmentLayout(starts.ToArray(), total, anchorIndex);
            }
            if (starts.Count == 0) starts.Add(0);
            return new SegmentLayout(starts.ToArray(), total, 0);
        }
    }

    /// <summary>Atomically rewrite media.m3u8 with the current set of restart
    /// boundaries baked in as EXT-X-DISCONTINUITY markers. Atomic so a
    /// concurrent player fetch can never see a half-written playlist.</summary>
    private void RegenerateMediaPlaylist(HlsSession session)
    {
        var path = Path.Combine(session.OutputDir, "media.m3u8");
        var tmp  = path + ".tmp";
        try
        {
            var content = BuildMediaPlaylist(session.Layout,
                session.Plan,
                startOffsetSec: session.SeekSec,
                restartBoundaries: session.GetRestartBoundaries());
            File.WriteAllText(tmp, content, Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HLS: media.m3u8 regeneration failed for session {Token}", session.Token);
        }
    }

    private static int EstimatedBandwidth(int width, int height)
    {
        // VBR HEVC very rough table — exact numbers don't matter for single-
        // variant streams, hls.js just wants the field present.
        long pixels = (long)Math.Max(width, 1) * Math.Max(height, 1);
        if (pixels >= 3840L * 2160) return 25_000_000;
        if (pixels >= 1920L * 1080) return 8_000_000;
        if (pixels >= 1280L *  720) return 4_000_000;
        return 2_000_000;
    }

}
