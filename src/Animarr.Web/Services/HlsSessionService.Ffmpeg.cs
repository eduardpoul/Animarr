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
        int maxBitrate = 0,
        // HDR → SDR tone mapping for the re-encode plans (None = SDR source or
        // off). Chosen once per session by ToneMapperFor.
        HdrToneMapper toneMapper = HdrToneMapper.None)
    {
        var args = new List<string>();
        var videoCodec = probe?.VideoCodec;
        var audioCodec = probe?.AudioCodec;
        // NVDEC and VAAPI have no H.264 High10 decoder: hand Hi10P to the GPU
        // re-encoders as a non-hw-decodable input so it takes their software-
        // decode + upload path (as legacy codecs do) instead of failing in
        // -hwaccel init.
        var hwDecodeCodec = probe?.Is10Bit == true
            && string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase)
            ? null : videoCodec;
        // The exact stream the probe picked as the video (never a cover
        // picture in front of it); "0:V:0?" when there was no probe.
        var videoMap = probe?.VideoMap ?? "0:V:0?";

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
                    externalAudioPath, audioOffsetSec, videoMap);
                break;
            case HlsPlan.Fmp4VaapiReencode:
                BuildFmp4VaapiArgs(args, fullPath, seekSec, audioOffsetSec, startNumber, targetHeight, audioTrackIndex,
                    externalAudioPath, maxBitrate, hwDecodeCodec, toneMapper, videoMap);
                break;
            case HlsPlan.Fmp4NvencReencode:
                BuildFmp4NvencArgs(args, fullPath, seekSec, audioOffsetSec, startNumber, targetHeight, audioTrackIndex,
                    externalAudioPath, maxBitrate, hwDecodeCodec, toneMapper, videoMap);
                break;
            case HlsPlan.Fmp4StreamCopy:
                BuildFmp4StreamCopyArgs(args, fullPath, seekSec, startNumber, videoCodec, audioOffsetSec, audioTrackIndex,
                    externalAudioPath, videoMap);
                break;
            case HlsPlan.Fmp4SoftwareReencode:
                BuildFmp4SoftwareArgs(args, fullPath, seekSec, audioOffsetSec, startNumber,
                    targetHeight, probe?.Height ?? 0, audioTrackIndex, externalAudioPath, maxBitrate, toneMapper, videoMap);
                break;
        }

        // ── HLS muxer options ────────────────────────────────────────────
        args.AddRange(new[]
        {
            "-f", "hls",
            "-hls_time", SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture),
            "-hls_segment_type", plan == HlsPlan.TsStreamCopy ? "mpegts" : "fmp4",
            // ffmpeg's own playlist is a scratch file we ignore, except in the
            // probe-less fallback where the player reads it — keep it VOD-typed
            // there. NB: vod forces hls_list_size to 0, so ffmpeg never deletes
            // segments (the old `-hls_list_size 50` + `delete_segments` pair was
            // inert — measured: every segment stayed on disk). Disk usage is
            // bounded app-side instead: played segments are pruned and ffmpeg
            // is paused while far enough ahead (HlsSessionService.Throttle.cs);
            // a scrub back past the pruned window goes through the seek-restart.
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
            // temp_file: segments are written as *.tmp and renamed when complete,
            // so "seg-N exists" means "seg-N is finished" — the segment endpoint,
            // the produced-top walk and the pruner all rely on that.
            "-hls_flags", "temp_file",
            "-hls_segment_filename", FfmpegOutputPath(Path.Combine(sessionDir, $"seg-%05d.{segExt}")),
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
            // The name stays RELATIVE: hlsenc joins it onto the directory of the
            // media playlist path below (an absolute init name would be appended
            // to that directory verbatim).
            var initName = reuseInit ? "_init_restart.mp4" : "init.mp4";
            args.AddRange(new[] { "-hls_fmp4_init_filename", initName });
        }
        args.AddRange(new[]
        {
            "-master_pl_name", masterPlaylistName,
            FfmpegOutputPath(mediaPlaylist),
        });
        return args;
    }

    /// <summary>
    /// Key frame on every segment boundary of a re-encode, so the HLS muxer can
    /// cut exactly where the playlist says a segment starts. ffmpeg evaluates
    /// <c>t</c> RELATIVE to the first frame it encodes (fftools' ref_pts —
    /// measured on BtbN N-124716 and jellyfin-ffmpeg 8.1.3: <c>-ss 41</c> +
    /// <c>-copyts</c> gives key frames at 41, 45, 49 …; an absolute
    /// <c>gte(t,40+4*n)</c> fired nothing until 80 s), and hlsenc measures its
    /// cuts from the run's first packet too. Both therefore line up with the
    /// announced grid as long as the run STARTS on a segment boundary — which
    /// is why StartAsync and the seek-restart seek re-encodes to
    /// <c>SegmentLayout.Start(K)</c> = K*4, not to the resume point (B18: a
    /// resume at 41 s cut at 41/45/49 under a playlist saying 40/44/48).
    /// </summary>
    private static readonly string ForceKeyFramesExpr =
        $"expr:gte(t,n_forced*{SegmentDurationSec.ToString("F1", CultureInfo.InvariantCulture)})";

    /// <summary>
    /// An output path in the form ffmpeg's HLS muxer can split. hlsenc finds
    /// the directory the init segment goes into with <c>strrchr(path, '/')</c>
    /// — forward slash only — so on Windows a <c>C:\…\_ffmpeg.m3u8</c> had "no
    /// directory", <c>init.mp4</c> landed in the server's working directory,
    /// and every fMP4 plan failed to start. Windows ffmpeg takes '/' as a
    /// separator everywhere, so outputs are normalised to it. Linux is left
    /// alone: there '\' is a legal file-name character, not a separator.
    /// </summary>
    private static string FfmpegOutputPath(string path)
        => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    /// <summary>argv as one copy-pasteable line for the Debug log.</summary>
    private static string FormatArgv(IEnumerable<string> args)
        => "ffmpeg " + string.Join(' ', args.Select(a =>
            a.Length > 0 && a.IndexOfAny([' ', '"', '\'', '(', ')', ',', ';']) < 0
                ? a
                : "\"" + a.Replace("\"", "\\\"") + "\""));

    /// <summary>The last lines ffmpeg wrote to stderr (it runs with
    /// <c>-loglevel warning</c>, so this is the warnings/errors). Kept per run
    /// so a run that dies before its first segment can be explained in the log
    /// and in the start error instead of a bare exit code.</summary>
    private sealed class StderrTail(int capacity = 30)
    {
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_lines)
            {
                if (_lines.Count == capacity) _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public string Text { get { lock (_lines) return string.Join('\n', _lines); } }

        /// <summary>Last non-blank line, trimmed to a size fit for an error message.</summary>
        public string? LastLine
        {
            get
            {
                string? last;
                lock (_lines) last = _lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (last is null) return null;
                last = last.Trim();
                return last.Length <= 300 ? last : last[..300] + "…";
            }
        }
    }

    /// <summary>
    /// Start one ffmpeg run for a session and drain its stderr (at Warning, so
    /// encoder problems show up in <c>docker logs</c>; an undrained pipe would
    /// block ffmpeg). Returns the process and the drain task, which completes
    /// once ffmpeg closed stderr — i.e. after its last line was read.
    ///
    /// stdout is not redirected: nobody reads it, and redirecting stdout
    /// without draining it fills the 64 KB pipe and BLOCKS ffmpeg mid-segment
    /// once anything prints there (some libavformat versions put a banner on
    /// stdout even with -hide_banner).
    ///
    /// WorkingDirectory = the session dir: every output path we pass is
    /// absolute, so this only matters for anything ffmpeg derives relative to
    /// its CWD — which then stays inside the session dir (deleted with the
    /// session) instead of littering the server's content root, as init.mp4 did
    /// on Windows. Removing that dir while ffmpeg runs would fail on Windows
    /// (a process's CWD is locked), but every teardown path kills ffmpeg and
    /// waits for it before deleting.
    /// </summary>
    private (Process Proc, Task Drain) LaunchFfmpeg(List<string> args, string sessionDir, string token,
        int? restartSegment, StderrTail? tail)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = sessionDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HLS {Token}{Run}: {Argv}", token,
                restartSegment is int r ? $" +seg{r}" : "", FormatArgv(args));

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var drain = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                {
                    tail?.Add(line);
                    if (restartSegment is int seg)
                        _logger.LogWarning("[hls {Token} +seg{Seg}] {Line}", token, seg, line);
                    else
                        _logger.LogWarning("[hls {Token}] {Line}", token, line);
                }
            }
            catch { /* process exited */ }
            try { await proc.WaitForExitAsync(); } catch { }
            if (restartSegment is null)
                _logger.LogInformation("HLS session {Token} ffmpeg exited (code {Code})", token,
                    SafeExitCode(proc));
        });
        return (proc, drain);
    }

    /// <summary>Exit code, or -1 when the process is gone/disposed (a session
    /// stopped meanwhile disposes its Process).</summary>
    private static int SafeExitCode(Process proc)
    {
        try { return proc.HasExited ? proc.ExitCode : -1; }
        catch { return -1; }
    }

    /// <summary>MPEG-TS HLS with stream-copy video and audio passthrough where
    /// possible. This is the Jellyfin/Emby/Plex Direct Stream path — works for
    /// every H.264 file (the most common case by far) with zero GPU usage and
    /// rock-solid A/V sync because the PCR field in each TS packet carries
    /// absolute clock-time the player can chase. Audio gets AAC re-encoded
    /// when the source codec isn't browser-friendly (DTS, TrueHD, etc.).</summary>
    private static void BuildTsStreamCopyArgs(List<string> args, string fullPath, double seekSec,
        string? audioCodec, int startNumber, int audioTrackIndex,
        string? externalAudioPath = null, double audioOffsetSec = 0.0, string videoMap = "0:V:0?")
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
            "-map", videoMap,     // the probed main video, never a cover picture
            "-map", ext ? $"1:a:{audioTrackIndex}?" : $"0:a:{audioTrackIndex}?",
            "-c:v", "copy",
        });
        // Audio: an external dub is always AAC-transcoded — its codec is
        // unknown here (.mka can hold FLAC/AC3/DTS/…) so we can't risk a copy.
        // For the source's own audio, passthrough when the browser can decode
        // it inside MPEG-TS (AAC/MP3 only across all browsers; AC3/E-AC3 work
        // in Chrome but break Firefox/Safari, so those re-encode to AAC).
        // audioCodec is the mapped track's (ProbeInfo.ForAudioTrack).
        if (TsAudioPassthrough(audioCodec, ext))
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
        string? externalAudioPath = null, int maxBitrate = 0, string? videoCodec = null,
        HdrToneMapper toneMapper = HdrToneMapper.None, string videoMap = "0:V:0?")
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
        //
        // Decode side: only H.264/HEVC go through the VAAPI DECODER. Legacy
        // codecs (mpeg4/XviD, MPEG-2, VC-1 …) are decoded in software — cheap —
        // and the frames are hwupload'ed so the ENCODE still runs on the GPU.
        var vc = (videoCodec ?? "").ToLowerInvariant();
        bool hwDecode = vc is "h264" or "hevc";
        bool placebo = toneMapper == HdrToneMapper.Libplacebo;
        if (placebo)
        {
            // libplacebo needs a Vulkan device next to the VAAPI one. Derived
            // from it (vulkan=vk@va) so the final hwupload can derive its way
            // back to VAAPI for the encoder; the filter device is Vulkan.
            args.AddRange(new[]
            {
                "-init_hw_device", "vaapi=va:/dev/dri/renderD128",
                "-init_hw_device", "vulkan=vk@va",
                "-filter_hw_device", "vk",
            });
        }
        else
        {
            args.Add("-vaapi_device");
            args.Add("/dev/dri/renderD128");
        }
        if (hwDecode)
        {
            args.Add("-hwaccel"); args.Add("vaapi");
            if (placebo) { args.Add("-hwaccel_device"); args.Add("va"); }
            args.Add("-hwaccel_output_format"); args.Add("vaapi");
        }

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
        var size = targetHeight > 0 ? $"w=-2:h={targetHeight}:" : "";
        string vf = (hwDecode, toneMapper) switch
        {
            // HDR on the GPU path. Downscale FIRST on the VAAPI surfaces (cheap
            // fixed-function), then download the p010 frames for tone mapping:
            // Vega/Raven can't map VAAPI surfaces into Vulkan zero-copy
            // (VK_ERROR_INVALID_DRM_FORMAT_MODIFIER_PLANE_LAYOUT_EXT), and
            // letting libplacebo scale at 4K made it 0.5× realtime vs 1.4×.
            (true, HdrToneMapper.Libplacebo) =>
                $"scale_vaapi={size}format=p010,hwdownload,format=p010,hwupload,"
                + $"{HdrToneMap.LibplaceboFilter},hwdownload,format=nv12,hwupload=derive_device=vaapi",
            (true, HdrToneMapper.Tonemapx) =>
                $"scale_vaapi={size}format=p010,hwdownload,format=p010,{HdrToneMap.TonemapxFilter},hwupload",
            // SDR (or tone mapping off). Always convert the surface to nv12 —
            // h264_vaapi only takes 8-bit, and a 10-bit HEVC source decodes
            // to p010 surfaces (the old no-downscale `format=nv12|vaapi` chain
            // handed p010 straight to the encoder and failed).
            (true, _) => $"scale_vaapi={size}format=nv12",
            // Software-decoded HDR (e.g. 10-bit AV1 / VP9 without a GPU decoder).
            (false, HdrToneMapper.Libplacebo) =>
                $"hwupload,{HdrToneMap.LibplaceboScaled(targetHeight)},hwdownload,format=nv12,hwupload=derive_device=vaapi",
            (false, HdrToneMapper.Tonemapx) =>
                (targetHeight > 0 ? $"scale=-2:{targetHeight}," : "") + $"{HdrToneMap.TonemapxFilter},hwupload",
            // Software-decoded frames: upload first, then scale ON the GPU.
            _ => "format=nv12,hwupload" + (targetHeight > 0 ? $",scale_vaapi=w=-2:h={targetHeight}" : ""),
        };
        var rate = RateFor(targetHeight, maxBitrate);
        args.AddRange(new[]
        {
            "-map", videoMap,     // the probed main video, never a cover picture
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "h264_vaapi",
            "-profile:v", "main",
            "-bf",     "0",
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", ForceKeyFramesExpr,
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
        if (toneMapper != HdrToneMapper.None) args.AddRange(HdrToneMap.Bt709OutputTags);
    }

    /// <summary>fMP4 HLS with NVIDIA NVENC re-encode to H.264 + B-frames
    /// disabled. Used when NVENC is available (any RTX / GTX 10-series+).
    /// Unlike VAAPI on Vega, NVENC can decode AND encode HEVC Main10 →
    /// this plan is the preferred re-encode path for HDR/DV content on
    /// NVIDIA hosts. -bf 0 still applied so B-frame reorder doesn't
    /// reintroduce the fMP4 TFDT sync wobble.</summary>
    private static void BuildFmp4NvencArgs(List<string> args, string fullPath, double seekSec,
        double audioOffsetSec, int startNumber, int targetHeight, int audioTrackIndex,
        string? externalAudioPath = null, int maxBitrate = 0, string? videoCodec = null,
        HdrToneMapper toneMapper = HdrToneMapper.None, string videoMap = "0:V:0?")
    {
        // CUDA-backed decode + NVENC encode. `-hwaccel_output_format cuda`
        // keeps frames on the GPU between decode and encode, avoiding a
        // CPU↔GPU bounce. Legacy codecs NVDEC can't decode (mpeg4/XviD, VC-1 …)
        // are decoded in software; h264_nvenc happily encodes system-memory
        // frames, so the expensive half still runs on the GPU.
        var vc = (videoCodec ?? "").ToLowerInvariant();
        bool hwDecode = vc is "h264" or "hevc";
        if (hwDecode)
        {
            args.Add("-hwaccel"); args.Add("cuda");
            args.Add("-hwaccel_output_format"); args.Add("cuda");
        }

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
        // Optional scale: on-GPU via scale_cuda for GPU-decoded frames;
        // plain CPU scale for software-decoded legacy codecs (NVENC takes
        // system-memory nv12/yuv420p frames directly).
        var size = targetHeight > 0 ? $"w=-2:h={targetHeight}:" : "";
        var vf = (hwDecode, toneMapper) switch
        {
            // HDR: scale on the GPU in 10-bit, then tone-map — on the GPU with
            // tonemap_cuda, or downloaded to the CPU tonemapx (NVENC takes
            // system-memory nv12 directly).
            (true, HdrToneMapper.TonemapCuda) => $"scale_cuda={size}format=p010,{HdrToneMap.TonemapCudaFilter}",
            (true, HdrToneMapper.Tonemapx)    => $"scale_cuda={size}format=p010,hwdownload,format=p010,{HdrToneMap.TonemapxFilter}",
            (true, _)                         => $"scale_cuda={size}format=nv12",
            (false, HdrToneMapper.None)       => (targetHeight > 0 ? $"scale=-2:{targetHeight}," : "") + "format=nv12",
            // Software-decoded HDR: CPU tone mapping (tonemap_cuda would need an upload first).
            (false, _)                        => (targetHeight > 0 ? $"scale=-2:{targetHeight}," : "") + HdrToneMap.TonemapxFilter,
        };
        var rate = RateFor(targetHeight, maxBitrate);
        args.AddRange(new[]
        {
            "-map", videoMap,     // the probed main video, never a cover picture
            "-map", $"1:a:{audioTrackIndex}?",
            "-vf", vf,
            "-c:v", "h264_nvenc",
            "-preset", "p4",          // p1=fastest, p7=highest quality; p4 is balanced
            "-tune", "hq",
            "-profile:v", "main",
            "-bf",     "0",           // no B-frames → tight A/V sync per segment
            // A forced key frame must be an IDR: NVENC makes it a plain I frame
            // by default, which isn't flagged as a key frame, so the HLS muxer
            // never cut there — measured: 10.4 s segments (NVENC's 250-frame
            // GOP) behind a playlist announcing 4 s ones, i.e. seg-1 held
            // 10.4–20.8 s while the player placed it at 4 s.
            "-forced-idr", "1",
            "-b:v",    rate.V,
            "-maxrate",rate.Max,
            "-bufsize",rate.Buf,
            "-force_key_frames", ForceKeyFramesExpr,
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
        if (toneMapper != HdrToneMapper.None) args.AddRange(HdrToneMap.Bt709OutputTags);
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
        string? externalAudioPath = null, int maxBitrate = 0, HdrToneMapper toneMapper = HdrToneMapper.None,
        string videoMap = "0:V:0?")
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
        // HDR: CPU tone mapping after the (cheaper, smaller) downscale.
        if (toneMapper != HdrToneMapper.None) vf += "," + HdrToneMap.TonemapxFilter;
        args.AddRange(new[]
        {
            "-map", videoMap,     // the probed main video, never a cover picture
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
            "-force_key_frames", ForceKeyFramesExpr,
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
        });
        if (toneMapper != HdrToneMapper.None) args.AddRange(HdrToneMap.Bt709OutputTags);
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
        string? externalAudioPath = null, string videoMap = "0:V:0?")
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
            "-map", videoMap,     // the probed main video, never a cover picture
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
