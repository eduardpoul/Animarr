using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

// ffmpeg argv construction per plan (TS stream-copy / VAAPI re-encode / fMP4 copy).
public sealed partial class HlsSessionService
{
    // ─── ffmpeg argv ─────────────────────────────────────────────────────────

    /// <summary>Build the ffmpeg command line for a session/restart. The plan
    /// determines container (TS vs fMP4), encoding (stream-copy vs VAAPI), and
    /// audio handling (passthrough vs AAC re-encode + itsoffset).</summary>
    private static List<string> BuildFfmpegArgs(
        string fullPath, double seekSec, string sessionDir, string mediaPlaylist,
        string masterPlaylistName, ProbeInfo? probe, HlsPlan plan,
        int startNumber = 0, bool reuseInit = false,
        int targetHeight = 0,
        double audioOffsetSec = 0.04,
        // Audio stream index inside the source (0 = first). Threaded through
        // to each Build*Args branch which substitutes it in the `-map` arg.
        // When externalAudioPath is set, this is the index WITHIN the external
        // file instead (almost always 0 — dub files carry a single stream).
        int audioTrackIndex = 0,
        // External sideload audio file. When non-null, every plan takes it as a
        // SECOND ffmpeg input and maps its audio (`-map 1:a:{audioTrackIndex}`)
        // — re-encoded to browser-safe AAC — instead of the source's own audio.
        // The video pipeline (TS copy / fMP4 / re-encode) is unchanged.
        string? externalAudioPath = null,
        // Output bitrate cap in Mbps (0 = use the resolution ladder). Threaded
        // to the re-encode builders, which feed it to RateFor.
        int maxBitrate = 0)
    {
        var args = new List<string>();
        var videoCodec = probe?.VideoCodec;
        var audioCodec = probe?.AudioCodec;

        // PTS positioning rules (applies to fMP4 paths; TS doesn't need
        // -copyts because PCR carries absolute time inside each segment):
        //   • Initial run (startNumber=0): default rebase-to-0 is correct.
        //   • Restart (startNumber=K): we need output PTS to start at
        //     K*segDur or fMP4's TFDT box claims time 0 while the playlist
        //     says seg-K starts at K*6s, and hls.js drops the segment as a
        //     duplicate/discontinuity. -copyts is the only flag the HLS
        //     muxer actually respects for this.

        switch (plan)
        {
            case HlsPlan.TsStreamCopy:
                BuildTsStreamCopyArgs(args, fullPath, seekSec, audioCodec, startNumber, audioTrackIndex,
                    externalAudioPath, audioOffsetSec);
                break;
            case HlsPlan.Fmp4VaapiReencode:
                BuildFmp4VaapiArgs(args, fullPath, seekSec, audioOffsetSec, startNumber, targetHeight, audioTrackIndex,
                    externalAudioPath, maxBitrate);
                break;
            case HlsPlan.Fmp4NvencReencode:
                BuildFmp4NvencArgs(args, fullPath, seekSec, audioOffsetSec, startNumber, targetHeight, audioTrackIndex,
                    externalAudioPath, maxBitrate);
                break;
            case HlsPlan.Fmp4StreamCopy:
                BuildFmp4StreamCopyArgs(args, fullPath, seekSec, startNumber, videoCodec, audioOffsetSec, audioTrackIndex,
                    externalAudioPath);
                break;
            case HlsPlan.Fmp4SoftwareReencode:
                BuildFmp4SoftwareArgs(args, fullPath, seekSec, audioOffsetSec, startNumber,
                    targetHeight, probe?.Height ?? 0, audioTrackIndex, externalAudioPath, maxBitrate);
                break;
        }

        // ── HLS muxer options ────────────────────────────────────────────
        args.AddRange(new[]
        {
            "-f", "hls",
            "-hls_time", SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture),
            "-hls_segment_type", plan == HlsPlan.TsStreamCopy ? "mpegts" : "fmp4",
            // Sliding window: keep the last 50 segments physically on disk
            // (= ~5 min of content). Old ones get deleted via the
            // delete_segments flag below. WITHOUT this every UHD HEVC
            // session would fill the 8GB tmpfs in ~7 min and choke ffmpeg.
            // Scrub-back past the deleted window triggers our seek-restart
            // (RestartFfmpegAtSegmentAsync), which re-produces the needed
            // range from source.
            "-hls_list_size", "50",
            "-hls_playlist_type", "vod",
        });
        if (startNumber > 0)
        {
            args.Add("-start_number");
            args.Add(startNumber.ToString(CultureInfo.InvariantCulture));
        }
        var segExt = SegmentExtension(plan);
        args.AddRange(new[]
        {
            // delete_segments: physically remove segment files that fall off
            // the sliding window. Without this `-hls_list_size 50` only
            // trims the m3u8 (which we ignore — we have our own playlist),
            // leaving disk usage unbounded. WITH it, ffmpeg actively reclaims
            // tmpfs as it goes.
            "-hls_flags", "temp_file+delete_segments",
            "-hls_segment_filename", Path.Combine(sessionDir, $"seg-%05d.{segExt}"),
        });
        // fMP4 needs an init segment carrying decoder config; MPEG-TS is
        // self-bootstrapping (PAT/PMT/SPS in every segment).
        if (plan != HlsPlan.TsStreamCopy)
        {
            // For a restart, init.mp4 already exists from the original run.
            // Writing it again creates a race window where the file briefly
            // disappears (temp_file rename) and any concurrent fetch sees a
            // 0-byte response. Use a per-run sentinel filename to avoid
            // stomping the working init.
            var initName = reuseInit ? "_init_restart.mp4" : "init.mp4";
            args.AddRange(new[] { "-hls_fmp4_init_filename", initName });
        }
        args.AddRange(new[]
        {
            "-master_pl_name", masterPlaylistName,
            mediaPlaylist,
        });
        return args;
    }

    /// <summary>MPEG-TS HLS with stream-copy video and audio passthrough where
    /// possible. This is the Jellyfin/Emby/Plex Direct Stream path — works for
    /// every H.264 file (the most common case by far) with zero GPU usage and
    /// rock-solid A/V sync because the PCR field in each TS packet carries
    /// absolute clock-time the player can chase. Audio gets AAC re-encoded
    /// when the source codec isn't browser-friendly (DTS, TrueHD, etc.).</summary>
    private static void BuildTsStreamCopyArgs(List<string> args, string fullPath, double seekSec,
        string? audioCodec, int startNumber, int audioTrackIndex,
        string? externalAudioPath = null, double audioOffsetSec = 0.0)
    {
        bool ext = externalAudioPath is not null;

        // Input #0 — source. Video always; its own audio too unless an external
        // dub replaces it.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — external dub audio (a second input), sync-shifted via
        // -itsoffset exactly like the fMP4 paths. The TS path is normally
        // single-input, so this is only wired in when a dub is selected.
        if (ext)
        {
            if (seekSec > 0)
            {
                args.Add("-ss");
                args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
            }
            args.Add("-itsoffset");
            args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(externalAudioPath!);
        }

        // -copyts so the output PCR carries the SOURCE position (= K*segDur
        // for a seg-K restart) instead of being rebased to 0. Without this,
        // a resume/scrub-restart writes seg-K with PCR=0 while the playlist
        // claims seg-K is at K*6s timeline — hls.js sees the mismatch and
        // either drops the segment or stalls. Only needed when startNumber>0
        // (we're not starting from seg-0); for fresh playback PCR=0 is
        // already correct.
        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", ext ? $"1:a:{audioTrackIndex}?" : $"0:a:{audioTrackIndex}?",
            "-c:v", "copy",
        });
        // Audio: an external dub is always AAC-transcoded — its codec is
        // unknown here (.mka can hold FLAC/AC3/DTS/…) so we can't risk a copy.
        // For the source's own audio, passthrough when the browser can decode
        // it inside MPEG-TS (AAC/MP3 only across all browsers; AC3/E-AC3 work
        // in Chrome but break Firefox/Safari, so those re-encode to AAC).
        if (!ext && IsBrowserCompatibleAudioInTs(audioCodec))
        {
            args.AddRange(new[] { "-c:a", "copy" });
        }
        else
        {
            args.AddRange(new[] { "-c:a", "aac", "-ac", "2", "-b:a", "192k" });
        }
    }

    /// <summary>fMP4 HLS with VAAPI re-encode to H.264 + B-frames disabled.
    /// Used for HEVC 8-bit on hosts with /dev/dri available. Combined with
    /// -itsoffset audio-sync compensation this is our pixel-perfect path.</summary>
    private static void BuildFmp4VaapiArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int audioTrackIndex,
        string? externalAudioPath = null, int maxBitrate = 0)
    {
        // Re-encoding the video stream eliminates the B-frame display reorder
        // that puts the first reorderable frame ~83ms past the segment
        // boundary in stream-copy output. With -bf 0 + IDR-per-segment GOP we
        // get genuinely byte-aligned A/V at every segment edge.
        //
        // We additionally nudge audio later by `audioOffsetSec` (default 40ms,
        // overrideable per-client via the calibration result the browser
        // computes once on first play). Humans tolerate audio behind the
        // picture (~125ms threshold) much better than ahead (~45ms), so we
        // always err on the late side. Implementation: a second ffmpeg input
        // with -itsoffset — the only PTS-shift mechanism the HLS fMP4 muxer
        // respects (filter-graph adelay/aresample get re-aligned away).
        args.Add("-vaapi_device");
        args.Add("/dev/dri/renderD128");
        args.Add("-hwaccel"); args.Add("vaapi");
        args.Add("-hwaccel_output_format"); args.Add("vaapi");

        // Input #0 — video (GPU-decoded).
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio only, sync-offset baked in via -itsoffset. Software
        // demuxes audio so we don't fight VAAPI's audio path. When an external
        // dub is selected this input points at THAT file instead of the source
        // (its audio is mapped below); the same -ss/-itsoffset apply.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.Add("-itsoffset");
        args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(externalAudioPath ?? fullPath);

        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        var vf = "format=nv12|vaapi,hwupload";
        if (targetHeight > 0)
            vf = $"scale_vaapi=w=-2:h={targetHeight}:format=nv12,{vf}";
        var rate = RateFor(targetHeight, maxBitrate);
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "h264_vaapi",
            "-profile:v", "main",
            "-bf",     "0",
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
    }

    /// <summary>fMP4 HLS with NVIDIA NVENC re-encode to H.264 + B-frames
    /// disabled. Used when NVENC is available (any RTX / GTX 10-series+).
    /// Unlike VAAPI on Vega, NVENC can decode AND encode HEVC Main10 →
    /// this plan is the preferred re-encode path for HDR/DV content on
    /// NVIDIA hosts. -bf 0 still applied so B-frame reorder doesn't
    /// reintroduce the fMP4 TFDT sync wobble.</summary>
    private static void BuildFmp4NvencArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int audioTrackIndex,
        string? externalAudioPath = null, int maxBitrate = 0)
    {
        // CUDA-backed decode + NVENC encode. `-hwaccel_output_format cuda`
        // keeps frames on the GPU between decode and encode, avoiding a
        // CPU↔GPU bounce. `auto` lets ffmpeg pick the right NVDEC for the
        // source codec (h264_nvdec, hevc_nvdec, etc.).
        args.Add("-hwaccel"); args.Add("cuda");
        args.Add("-hwaccel_output_format"); args.Add("cuda");

        // Input #0 — video, GPU-decoded.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio with -itsoffset (same trick as VAAPI/SW paths).
        // Points at the external dub file when one is selected.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.Add("-itsoffset");
        args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(externalAudioPath ?? fullPath);

        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        // Optional scale on GPU via scale_cuda. nv12 output format for
        // NVENC compatibility.
        var vf = targetHeight > 0
            ? $"scale_cuda=w=-2:h={targetHeight}:format=nv12"
            : "scale_cuda=format=nv12";
        var rate = RateFor(targetHeight, maxBitrate);
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "h264_nvenc",
            "-preset", "p4",          // p1=fastest, p7=highest quality; p4 is balanced
            "-tune", "hq",
            "-profile:v", "main",
            "-bf",     "0",           // no B-frames → tight A/V sync per segment
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
    }

    /// <summary>fMP4 HLS via SOFTWARE libx264 re-encode + optional downscale.
    /// The CPU fallback for the quality-ladder when no GPU encoder is present.
    /// Software-decodes the source (so it handles any codec incl. HEVC 10-bit),
    /// scales with the CPU `scale` filter, and encodes H.264 8-bit (yuv420p) at
    /// the ladder bitrate. -bf 0 + IDR-per-segment matches the GPU paths' A/V
    /// sync. veryfast preset to give the best shot at real-time on a CPU; if the
    /// box still can't keep up, that's on the operator (don't pick a lower rung
    /// than the source). HDR is flattened to SDR (no tonemap) — fine for a
    /// downscaled rung on a small screen.</summary>
    private static void BuildFmp4SoftwareArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int sourceHeight, int audioTrackIndex,
        string? externalAudioPath = null, int maxBitrate = 0)
    {
        // Input #0 — video (software decode).
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio with -itsoffset (same A/V-sync trick as the GPU paths).
        // Points at the external dub file when one is selected.
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.Add("-itsoffset");
        args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(externalAudioPath ?? fullPath);

        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        var outH = targetHeight > 0 ? targetHeight : sourceHeight;
        var rate = RateFor(outH, maxBitrate);
        var vf = targetHeight > 0 ? $"scale=-2:{targetHeight}" : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-profile:v", "main",
            "-pix_fmt", "yuv420p",    // 10-bit/HDR source → 8-bit SDR H.264
            "-bf",     "0",
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
    }

    /// <summary>fMP4 HLS with full stream-copy. Used for 10-bit HEVC HDR / DV
    /// where Vega 11 can't encode Main10. B-frames stay in the bitstream
    /// → first decoded sample's TFDT in fMP4 is the IDR's decode time but
    /// its display time is later, so audio reaches the user ~50ms before
    /// video. Compensate by shifting audio via -itsoffset on a separate
    /// input — same trick the VAAPI path uses. Without this knob the user
    /// has to rely on DLNA cast / mpv to avoid the wobble.</summary>
    private static void BuildFmp4StreamCopyArgs(List<string> args, string fullPath, double seekSec,
        int startNumber, string? videoCodec, double audioOffsetSec, int audioTrackIndex,
        string? externalAudioPath = null)
    {
        // A second input is needed either to shift the source's own audio
        // (-itsoffset) OR to pull audio from an external dub file. The external
        // case forces dual-input regardless of offset.
        bool ext          = externalAudioPath is not null;
        bool useDualInput = audioOffsetSec != 0.0 || ext;

        // Input #0 — video (and audio when not using a second input)
        if (seekSec > 0)
        {
            args.Add("-ss");
            args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-i", fullPath });

        // Input #1 — audio with -itsoffset. The source file itself for a pure
        // sync nudge, or the external dub file when one is selected.
        if (useDualInput)
        {
            if (seekSec > 0)
            {
                args.Add("-ss");
                args.Add(seekSec.ToString("F3", CultureInfo.InvariantCulture));
            }
            args.Add("-itsoffset");
            args.Add(audioOffsetSec.ToString("F3", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(externalAudioPath ?? fullPath);
        }
        if (startNumber > 0)
        {
            args.Add("-copyts");
        }
        args.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", useDualInput ? $"1:a:{audioTrackIndex}?" : $"0:a:{audioTrackIndex}?",
            "-c:v", "copy",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
        if (string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-tag:v"); args.Add("hvc1");
        }
    }

}
