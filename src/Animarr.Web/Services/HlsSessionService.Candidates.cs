using System.Text;

namespace Animarr.Web.Services;

// Plan candidates + device failure stepping (task 6.1).
//
// A start answers with the full ordered candidate list — Direct Play, Direct
// Stream, the HLS copy, the re-encode — and for each why it was or wasn't
// taken, so a failing playback can be read off the start response. Failures a
// client reports (PlaybackFailureService) take a plan off the list for that
// device and that kind of stream; the next one down is then used, reported as
// transcodeReasonCode device_failed_before.
public sealed partial class HlsSessionService
{
    /// <summary>One plan of a start, in the order the server tries them.</summary>
    /// <param name="Plan">"directplay", "directstream", "ts-copy" / "fmp4-copy",
    /// "nvenc-reencode" / "vaapi-reencode" / "sw-reencode".</param>
    /// <param name="Status">"selected"; "skipped" (ruled out — see
    /// <paramref name="Reasons"/>); "fallback" (not needed, comes after the
    /// selected one; its reasons say whether it could have been used).</param>
    /// <param name="Reasons">ReasonCodes / CandidateReasons that rule it out.</param>
    public sealed record PlanCandidate(string Plan, string Status, IReadOnlyList<string> Reasons);

    /// <summary>Reasons only a candidate list carries (next to ReasonCodes).</summary>
    public static class CandidateReasons
    {
        /// <summary>The client asked for a needed remux as HLS (remux=hls).</summary>
        public const string RemuxHls = "remux_hls";
        /// <summary>The client doesn't take this tier's container (no "mp4" token for the remux).</summary>
        public const string RemuxUnsupported = "remux_unsupported";
        /// <summary>The tier doesn't apply to this file (a browser-native container needs no remux).</summary>
        public const string NotApplicable = "not_applicable";
        /// <summary>No probe — nothing could be judged.</summary>
        public const string ProbeFailed = "probe_failed";
        /// <summary>Skipped for a reason this list doesn't model.</summary>
        public const string NotChosen = "not_chosen";
    }

    /// <summary>
    /// The decode signature of a file's video — what a device's decoder
    /// failure is remembered by: codec + profile / level (the CODECS string
    /// where known), bit depth, HDR transfer, Dolby Vision profile, interlace.
    /// "v:hvc1.2.4.L150.B0.10.pq" etc.; null when the probe failed.
    /// </summary>
    public async Task<string?> DecodeSignatureAsync(string fullPath, CancellationToken ct)
    {
        var p = await ProbeMediaAsync(fullPath, ct);
        if (p is null || string.IsNullOrEmpty(p.VideoCodec)) return null;
        var sb = new StringBuilder("v:");
        sb.Append((p.VideoCodecsAttribute ?? p.VideoCodec).ToLowerInvariant());
        sb.Append(p.Is10Bit ? ".10" : ".8");
        var xfer = (p.ColorTransfer ?? "").ToLowerInvariant();
        if (xfer == "smpte2084") sb.Append(".pq");
        else if (xfer == "arib-std-b67") sb.Append(".hlg");
        if (p.Dovi.Profile >= 0) sb.Append(".dv").Append(p.Dovi.Profile);
        if (p.Interlaced) sb.Append(".i");
        return sb.ToString();
    }

    /// <summary>The probed audio codec of track <paramref name="audioTrackIndex"/> (null = unknown).</summary>
    public async Task<string?> AudioCodecAsync(string fullPath, int audioTrackIndex, CancellationToken ct)
    {
        var p = await ProbeMediaAsync(fullPath, ct);
        return p is not null && audioTrackIndex >= 0 && audioTrackIndex < p.AudioStreams.Count
            ? p.AudioStreams[audioTrackIndex].Codec : null;
    }

    /// <summary>The re-encode this host runs for a file (the encoder ladder).</summary>
    private HlsPlan ReencodePlanFor(ProbeInfo probe)
    {
        if (_hardware?.Current.Nvenc.Available == true) return HlsPlan.Fmp4NvencReencode;
        if (_hardware?.Current.Vaapi.Available == true && (!probe.Is10Bit || _hardware.Current.VaapiHevc10Decode))
            return HlsPlan.Fmp4VaapiReencode;
        return HlsPlan.Fmp4SoftwareReencode;
    }

    /// <summary>An answer's output with <paramref name="code"/> as its main reason.</summary>
    public static PlayerOutputInfo? WithMainReason(PlayerOutputInfo? o, string code)
        => o is null ? null : o with
        {
            TranscodeReasonCode = code,
            TranscodeReasonCodes = [code, .. (o.TranscodeReasonCodes ?? []).Where(c => c != code)],
        };

    /// <summary>An answer's output with <paramref name="code"/> added to its reasons.</summary>
    public static PlayerOutputInfo WithExtraReason(PlayerOutputInfo o, string code)
        => (o.TranscodeReasonCodes ?? []).Contains(code) ? o
            : o with { TranscodeReasonCodes = [.. o.TranscodeReasonCodes ?? [], code] };

    /// <summary>
    /// The ordered candidate list of a start, with the tier that was actually
    /// served (<paramref name="selectedPlan"/> = output.plan) marked. Judged
    /// from the same probe and caps as the decision; <paramref name="requestReasons"/>
    /// are the start's own reasons that rule out both native paths (an
    /// external dub, a burn-in, a sync offset, audio renditions).
    /// </summary>
    public async Task<IReadOnlyList<PlanCandidate>> ExplainAsync(string fullPath, ClientCaps client, string selectedPlan,
        int audioTrackIndex, int maxHeight, int maxBitrate, bool burning, PlaybackFailureService.Steps steps,
        IReadOnlyList<string> requestReasons, CancellationToken ct)
    {
        var probe = await ProbeMediaAsync(fullPath, ct);
        if (probe is null)
            return [new PlanCandidate(selectedPlan, "selected", [CandidateReasons.ProbeFailed])];
        var track = probe.ForAudioTrack(audioTrackIndex, null, false);
        var container = Path.GetExtension(fullPath).ToLowerInvariant().TrimStart('.');
        var dovi = DolbyVision.For(probe.Dovi, client);
        bool interlacedForBrowser = probe.Interlaced && !client.NativePlayer;

        var common = new List<string>(requestReasons);
        if (maxHeight > 0 && probe.Height > 0 && maxHeight < probe.Height) common.Add(ReasonCodes.UserQualityCap);
        if (EffectiveBitrateCap(maxBitrate, probe) > 0) common.Add(ReasonCodes.UserBitrateCap);
        if (burning) common.Add(ReasonCodes.BurnedSubtitle);

        var dp = new List<string>(common);
        var ds = new List<string>(common);
        if (client.HasTokens)
        {
            bool videoOk = NativeVideoOk(probe, client);
            var videoWhy = !videoOk && client.Has(VideoToken(probe)) && !FitsClientDecoder(probe, client) ? ReasonCodes.DecoderLimit
                : !videoOk ? ReasonCodes.CodecUnsupported
                : dovi == DoviHandling.Reencode ? ReasonCodes.DolbyVision
                : interlacedForBrowser ? ReasonCodes.Interlaced : null;
            if (videoWhy is not null) { dp.Add(videoWhy); ds.Add(videoWhy); }
            bool rawOk = dovi switch
            {
                DoviHandling.None or DoviHandling.AsDolbyVision => true,
                DoviHandling.AsBaseLayer => client.NativePlayer || !IsDolbyVisionSampleEntry(probe.VideoCodecTag),
                _ => false,
            };
            if (!rawOk && videoWhy is null) dp.Add(ReasonCodes.DolbyVision);
            if (audioTrackIndex > 0) dp.Add(ReasonCodes.AudioTrack);
            if (probe.AudioStreams.Count > 0 && !client.DecodesAudio(track.AudioCodec)) dp.Add(ReasonCodes.AudioUnsupported);
            if (!client.Has(container)) dp.Add(ReasonCodes.ContainerUnsupported);
            if (!client.Has("mp4")) ds.Add(CandidateReasons.RemuxUnsupported);
        }
        else
        {
            var vc = (probe.VideoCodec ?? "").ToLowerInvariant();
            bool videoOk = vc switch
            {
                "h264" => !probe.Is10Bit,
                "hevc" => probe.Is10Bit ? client.Hevc10 : client.Hevc,
                _ => false,
            };
            var videoWhy = !videoOk ? ReasonCodes.CodecUnsupported
                : interlacedForBrowser ? ReasonCodes.Interlaced : null;
            if (videoWhy is not null) { dp.Add(videoWhy); ds.Add(videoWhy); }
            if (probe.HasDolbyVision) dp.Add(ReasonCodes.DolbyVision);
            if (dovi == DoviHandling.Reencode && videoWhy is null) ds.Add(ReasonCodes.DolbyVision);
            if (audioTrackIndex > 0) dp.Add(ReasonCodes.AudioTrack);
            var ac = (track.AudioCodec ?? "").ToLowerInvariant();
            if (ac is not ("aac" or "mp3" or "mp4a")) dp.Add(ReasonCodes.AudioUnsupported);
            bool nativeContainer = container is "mp4" or "m4v" or "mov";
            if (!nativeContainer) dp.Add(ReasonCodes.ContainerUnsupported);
            if (nativeContainer || container == "webm") ds.Add(CandidateReasons.NotApplicable);
        }
        if (client.HlsRemux) ds.Add(CandidateReasons.RemuxHls);
        if (steps.NoDirectPlay) dp.Add(ReasonCodes.DeviceFailedBefore);
        // The track's codec is one this device failed to decode (its caps here
        // already lack it — that is why the audio reason above is there).
        if (PlaybackFailureService.NormalizeAudioCodec(track.AudioCodec) is { } ac0 && steps.TranscodeAudio.Contains(ac0))
            dp.Add(ReasonCodes.DeviceFailedBefore);
        if (steps.NoDirectStream) ds.Add(ReasonCodes.DeviceFailedBefore);

        // The HLS copy: TS for 8-bit H.264, fMP4 for what the client decodes.
        var vcodec = (probe.VideoCodec ?? "").ToLowerInvariant();
        var copyName = vcodec == "h264" && !probe.Is10Bit ? "ts-copy" : "fmp4-copy";
        var copy = new List<string>();
        if (maxHeight > 0 && probe.Height > 0 && maxHeight < probe.Height) copy.Add(ReasonCodes.UserQualityCap);
        if (EffectiveBitrateCap(maxBitrate, probe) > 0) copy.Add(ReasonCodes.UserBitrateCap);
        if (burning) copy.Add(ReasonCodes.BurnedSubtitle);
        if (interlacedForBrowser) copy.Add(ReasonCodes.Interlaced);
        bool copyDecodes = vcodec switch
        {
            "h264" => !probe.Is10Bit,
            "hevc" => probe.Is10Bit ? client.Hevc10 : client.Hevc,
            "av1" or "vp9" => client.Has(vcodec),
            _ => false,
        };
        if (!copyDecodes) copy.Add(ReasonCodes.CodecUnsupported);
        else if (!FitsClientDecoder(probe, client)) copy.Add(ReasonCodes.DecoderLimit);
        else if (dovi == DoviHandling.Reencode) copy.Add(ReasonCodes.DolbyVision);
        if (steps.NoCopy) copy.Add(ReasonCodes.DeviceFailedBefore);

        var reencode = new List<string>();
        if (steps.NoHevcOutput) reencode.Add(ReasonCodes.DeviceFailedBefore);   // stepped to H.264

        var list = new List<(int Tier, string Plan, List<string> Reasons)>
        {
            (0, "directplay", dp), (1, "directstream", ds), (2, copyName, copy),
            (3, PlanName(ReencodePlanFor(probe)), reencode),
        };
        int selectedTier = selectedPlan switch
        {
            "directplay" => 0, "directstream" => 1, "ts-copy" or "fmp4-copy" => 2, _ => 3,
        };
        return list.Select(c => c.Tier == selectedTier
                ? new PlanCandidate(selectedPlan, "selected", c.Tier == 3 ? c.Reasons : [])
                : new PlanCandidate(c.Plan, c.Tier < selectedTier ? "skipped" : "fallback",
                    c.Tier < selectedTier && c.Reasons.Count == 0 ? [CandidateReasons.NotChosen] : c.Reasons.Distinct().ToList()))
            .ToList();
    }

    private static string PlanName(HlsPlan plan) => plan switch
    {
        HlsPlan.TsStreamCopy         => "ts-copy",
        HlsPlan.Fmp4VaapiReencode    => "vaapi-reencode",
        HlsPlan.Fmp4NvencReencode    => "nvenc-reencode",
        HlsPlan.Fmp4SoftwareReencode => "sw-reencode",
        HlsPlan.Fmp4StreamCopy       => "fmp4-copy",
        _                            => "unknown",
    };
}
