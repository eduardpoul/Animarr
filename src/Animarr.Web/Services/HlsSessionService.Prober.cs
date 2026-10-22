using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

// ffprobe: source stream inspection + playback-plan decision + output metadata.
public sealed partial class HlsSessionService
{
    // ─── ffprobe ────────────────────────────────────────────────────────────

    private async Task<ProbeInfo?> ProbeMediaAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            // Shared with /api/probe and /api/video (MediaProbeCache): one
            // ffprobe per file version instead of one per caller.
            var json = await _probeCache.GetStreamsJsonAsync(fullPath, ct);
            if (json is null) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double duration = 0;
            long bitRate = 0;
            double startTime = 0;
            if (root.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("duration", out var durEl)
                    && double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    duration = d;
                bitRate = ParseBitRate(fmt);
                if (fmt.TryGetProperty("start_time", out var stEl)
                    && double.TryParse(stEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var st0))
                    startTime = st0;
            }
            long videoBitRate = 0;

            string? vCodec = null;
            string? aCodec = null;
            int width = 1280, height = 720;
            bool hasDv = false;
            var dovi = DoviInfo.None;
            string? videoTag = null;      // MP4 sample entry (hvc1 / hev1 / dvh1 / dvhe …)
            string? sar = null;           // pixel aspect ratio, container's first (ProbeInfo.SampleAspectRatio)
            bool interlaced = false;      // explicit field order (ProbeInfo.Interlaced)
            var subtitleCodecs = new List<string?>();   // every subtitle stream, 0:s:N order
            bool is10Bit = false;
            string? colorTransfer = null;   // smpte2084 → HDR10, arib-std-b67 → HLG
            int audioChannels = 0;
            string? audioLanguage = null;
            string? videoCodecsAttr = null;
            string? audioCodecsAttr = null;
            double reorderDelaySec = 0;   // B-frame reorder delay → audio itsoffset for stream-copy
            int videoStreamIndex = -1;    // absolute stream index of the main video (the `-map 0:N`)
            var audioStreams = new List<AudioStreamInfo>();   // every audio stream, 0:a:N order
            double videoEnd = 0;          // main video's own end (ProbeInfo.VideoEndSec)
            string? audioProfile = null;  // first audio track's profile (ProbeInfo.AudioProfile)

            if (root.TryGetProperty("streams", out var streams))
            {
                // The main video is picked up front, not "the first video
                // stream": cover art often comes first and picking it sent the
                // plan to a re-encode of one still image (see PickMainVideo).
                var mainVideo = PickMainVideo(streams);
                int pos = -1;
                foreach (var s in streams.EnumerateArray())
                {
                    pos++;
                    if (!s.TryGetProperty("codec_type", out var ct2)) continue;
                    var type = ct2.GetString();
                    if (type == "video" && pos == mainVideo)
                    {
                        videoStreamIndex = s.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : -1;
                        if (s.TryGetProperty("codec_name", out var cn)) vCodec = cn.GetString();
                        if (s.TryGetProperty("width",  out var w) && w.TryGetInt32(out var wi)) width  = wi;
                        if (s.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi)) height = hi;
                        // Capture color transfer characteristic so BuildOutputInfo
                        // can mark the stream as HDR10 / HLG. Distinct from
                        // bit-depth — 10-bit alone isn't HDR.
                        if (s.TryGetProperty("color_transfer", out var ctEl))
                            colorTransfer = ctEl.GetString();
                        // pix_fmt yuv420p10le / yuv444p10le etc. — anything containing
                        // "10le" or "10be" is 10-bit. Profile strings like "Main 10"
                        // are also a tell.
                        if (s.TryGetProperty("pix_fmt", out var pf))
                        {
                            var pix = pf.GetString() ?? "";
                            if (pix.Contains("10le", StringComparison.OrdinalIgnoreCase)
                             || pix.Contains("10be", StringComparison.OrdinalIgnoreCase))
                                is10Bit = true;
                        }
                        if (s.TryGetProperty("profile", out var pr))
                        {
                            var prof = pr.GetString() ?? "";
                            if (prof.Contains("Main 10", StringComparison.OrdinalIgnoreCase))
                                is10Bit = true;
                        }
                        // Dolby Vision profile / compatibility (DoviInfo) — the
                        // copy paths treat P5, P7 and P8 differently.
                        dovi = DoviInfo.FromStream(s);
                        hasDv = dovi.Present;
                        videoTag = s.TryGetProperty("codec_tag_string", out var tagEl) ? tagEl.GetString() : null;
                        sar = s.TryGetProperty("sample_aspect_ratio", out var sarEl) ? sarEl.GetString() : null;
                        // field_order: progressive / tt / bb / tb / bt / unknown.
                        // "unknown" (or absent) counts as progressive — only an
                        // explicit field order triggers deinterlacing (idet is a
                        // full decode pass; not run here).
                        interlaced = s.TryGetProperty("field_order", out var foEl)
                            && (foEl.GetString() ?? "") is "tt" or "bb" or "tb" or "bt";
                        // B-frame reorder delay → default audio itsoffset for the
                        // fMP4 stream-copy path. has_b_frames is the decoder's
                        // reorder depth; × frame duration = how far video
                        // presentation lags decode (and thus audio runs ahead).
                        int hasB = 0;
                        if (s.TryGetProperty("has_b_frames", out var hbEl) && hbEl.TryGetInt32(out var hbV)) hasB = hbV;
                        double fps = 0;
                        if (s.TryGetProperty("avg_frame_rate", out var afrEl)) fps = ParseFrameRate(afrEl.GetString());
                        if (fps <= 0 && s.TryGetProperty("r_frame_rate", out var rfrEl)) fps = ParseFrameRate(rfrEl.GetString());
                        // +1 frame safety bias: has_b_frames under-counts the
                        // effective reorder on deep B-pyramids (UHD remuxes),
                        // leaving audio slightly ahead. Audio-ahead (~45ms
                        // perception threshold) is more noticeable than audio-
                        // behind (~125ms), so bias toward a touch behind. Files
                        // with no B-frames stay at 0 (no reorder, no bias).
                        if (hasB > 0 && fps > 0) reorderDelaySec = (hasB + 1) / fps;

                        videoCodecsAttr = BuildVideoCodecsAttribute(vCodec, s);
                        videoEnd = StreamEndSec(s);
                        videoBitRate = ParseBitRate(s);
                    }
                    else if (type == "subtitle")
                    {
                        subtitleCodecs.Add(s.TryGetProperty("codec_name", out var scn) ? scn.GetString() : null);
                    }
                    else if (type == "audio")
                    {
                        // EVERY audio stream, in order — index N here is ffmpeg's
                        // 0:a:N, so the session can describe (and pass through)
                        // the track the user actually picked, not just track 0.
                        string? codec = s.TryGetProperty("codec_name", out var acn) ? acn.GetString() : null;
                        int channels = s.TryGetProperty("channels", out var ach) && ach.TryGetInt32(out var ci) ? ci : 0;
                        // Language tag: ffprobe puts it under tags.language, but
                        // MKV-from-Matroska sometimes uses LANGUAGE (uppercase).
                        string? language = null;
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            if (tags.TryGetProperty("language", out var langEl))
                                language = langEl.GetString();
                            else if (tags.TryGetProperty("LANGUAGE", out var langElU))
                                language = langElU.GetString();
                        }
                        string? aProfile = s.TryGetProperty("profile", out var apr) ? apr.GetString() : null;
                        audioStreams.Add(new AudioStreamInfo(codec, channels, language, aProfile));

                        if (aCodec is null)
                        {
                            // The FIRST audio stream fills the top-level fields:
                            // what Direct Play / Direct Stream (and a start with
                            // the default track) play. StartAsync swaps in the
                            // selected track (ForAudioTrack). We still default the
                            // MSE CODECS=… string to mp4a.40.2 because the fMP4
                            // path always transcodes; the TS branch builds its own.
                            aCodec = codec;
                            audioChannels = channels;
                            audioLanguage = language;
                            audioProfile = aProfile;
                            audioCodecsAttr = "mp4a.40.2";
                        }
                    }
                }
            }

            var combined = (videoCodecsAttr, audioCodecsAttr) switch
            {
                (string v, string a) => $"{v},{a}",
                (string v, null)     => v,
                (null,     string a) => a,
                _                    => null,
            };

            // reorderDelaySec stays the has_b_frames estimate here. The packet
            // measurement that overrides it (MeasureReorderDelayAsync) is a
            // second ffprobe only the fMP4 stream-copy plan needs, so StartAsync
            // runs it for that plan alone instead of on every probe.
            return new ProbeInfo(duration, vCodec, aCodec, width, height, combined, hasDv, is10Bit,
                colorTransfer, audioChannels, audioLanguage, reorderDelaySec)
            {
                VideoStreamIndex = videoStreamIndex,
                AudioStreams     = audioStreams,
                VideoEndSec      = videoEnd,
                BitRate          = bitRate,
                VideoBitRate     = videoBitRate,
                AudioProfile     = audioProfile,
                VideoCodecsAttribute = videoCodecsAttr,
                Dovi             = dovi,
                VideoCodecTag    = videoTag,
                SampleAspectRatio = sar,
                Interlaced       = interlaced,
                SubtitleCodecs   = subtitleCodecs,
                StartTimeSec     = startTime,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller (HLS start request) went away — let the endpoint drop
            // it instead of carrying on with "probe failed" and spawning ffmpeg.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", fullPath);
            return null;
        }
    }

    /// <summary>
    /// Position (in ffprobe's <c>streams</c> array) of the stream to play as THE
    /// video, or -1 when there is none.
    ///
    /// Not simply the first video stream: a cover picture can come first. As
    /// an <c>attached_pic</c> (MP4 covr, Matroska image attachment) it is
    /// skipped outright; muxed as a real one-frame video track (a PNG/MJPEG
    /// track in an MKV — the case ffmpeg's <c>V</c> specifier can't see, as
    /// the disposition is 0) a non-still-image codec wins over it. A file whose
    /// only video is MJPEG (old camcorder AVIs) still gets it.
    /// </summary>
    internal static int PickMainVideo(JsonElement streams)
    {
        if (streams.ValueKind != JsonValueKind.Array) return -1;
        int pos = -1, firstStill = -1;
        foreach (var s in streams.EnumerateArray())
        {
            pos++;
            if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "video") continue;
            if (IsAttachedPicture(s)) continue;
            var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
            if (!IsStillImageCodec(codec)) return pos;
            if (firstStill < 0) firstStill = pos;
        }
        return firstStill;
    }

    /// <summary>The main video's absolute stream index (<c>-map 0:N</c>), or -1.</summary>
    internal static int MainVideoStreamIndex(JsonElement streams)
    {
        var pos = PickMainVideo(streams);
        if (pos < 0) return -1;
        var s = streams[pos];
        return s.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var v) ? v : -1;
    }

    /// <summary>ffprobe <c>disposition.attached_pic</c> = 1: an embedded cover
    /// picture, not a video track.</summary>
    internal static bool IsAttachedPicture(JsonElement stream)
        => stream.TryGetProperty("disposition", out var disp)
           && disp.ValueKind == JsonValueKind.Object
           && disp.TryGetProperty("attached_pic", out var ap)
           && ap.ValueKind == JsonValueKind.Number && ap.TryGetInt32(out var v) && v != 0;

    /// <summary>Codecs that carry pictures rather than movies (as a "video"
    /// stream they're a cover / thumbnail track).</summary>
    private static bool IsStillImageCodec(string? codec) => (codec ?? "").ToLowerInvariant() is
        "mjpeg" or "png" or "apng" or "bmp" or "gif" or "webp" or "tiff" or "jpegls" or "jpeg2000" or "qoi";

    /// <summary>End of one ffprobe stream in seconds (start_time + duration),
    /// from <c>duration</c> or, for Matroska (no per-stream duration), the
    /// <c>DURATION</c> tag ("00:00:20.019000000"). 0 = unknown.</summary>
    private static double StreamEndSec(JsonElement stream)
    {
        var inv = CultureInfo.InvariantCulture;
        double start = stream.TryGetProperty("start_time", out var st)
            && double.TryParse(st.GetString(), NumberStyles.Float, inv, out var sv) && sv > 0 ? sv : 0;
        if (stream.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, inv, out var dv) && dv > 0)
            return start + dv;
        if (stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.StartsWith("DURATION", StringComparison.OrdinalIgnoreCase)
                    && ParseClock(tag.Value.GetString()) is double sec && sec > 0)
                    return start + sec;
        return 0;
    }

    /// <summary><c>bit_rate</c> of an ffprobe format / stream object, or the
    /// Matroska <c>BPS</c> tag mkvmerge writes per track (bits/s; 0 = unknown).</summary>
    private static long ParseBitRate(JsonElement el)
    {
        if (el.TryGetProperty("bit_rate", out var br)
            && long.TryParse(br.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
            return v;
        if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            foreach (var tag in tags.EnumerateObject())
                if ((tag.Name.Equals("BPS", StringComparison.OrdinalIgnoreCase)
                     || tag.Name.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase))
                    && long.TryParse(tag.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)
                    && t > 0)
                    return t;
        return 0;
    }

    /// <summary>"HH:MM:SS.fffffffff" → seconds (null when it isn't one).</summary>
    private static double? ParseClock(string? text)
    {
        var parts = (text ?? "").Split(':');
        if (parts.Length != 3) return null;
        var inv = CultureInfo.InvariantCulture;
        return int.TryParse(parts[0], NumberStyles.Integer, inv, out var h)
            && int.TryParse(parts[1], NumberStyles.Integer, inv, out var m)
            && double.TryParse(parts[2], NumberStyles.Float, inv, out var s)
            ? h * 3600 + m * 60 + s
            : null;
    }

    /// <summary>Parse an ffprobe frame-rate string ("24000/1001", "25", "0/0")
    /// into fps. Returns 0 when unknown/zero so callers can skip it.</summary>
    private static double ParseFrameRate(string? rate)
    {
        if (string.IsNullOrWhiteSpace(rate)) return 0;
        var slash = rate.IndexOf('/');
        if (slash < 0)
            return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
        if (double.TryParse(rate.AsSpan(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(rate.AsSpan(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
            return num / den;
        return 0;
    }

    /// <summary>How far past the resume point <see cref="FindCopyAnchorAsync"/>
    /// looks for the next key frame. Bounds the read to one GOP plus this.</summary>
    private const double CopyAnchorWindowSec = 10.0;

    /// <summary>
    /// For a stream copy that resumes at <paramref name="seekSec"/>: the last
    /// key frame at or before it (where the copy has to start — the demuxer
    /// seek lands there, exactly as ffmpeg's <c>-ss</c> does) and the first
    /// key frame after it within <see cref="CopyAnchorWindowSec"/> (null = none
    /// in the window). One ffprobe over ~one GOP of packets; null on failure.
    /// </summary>
    private async Task<(double Keyframe, double? Next)?> FindCopyAnchorAsync(string fullPath, int videoStreamIndex,
        double seekSec, CancellationToken ct)
    {
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var r = await FfprobeRunner.RunAsync(
            [
                "-v", "error",
                "-select_streams", videoStreamIndex >= 0 ? videoStreamIndex.ToString(inv) : "V:0",
                "-read_intervals", $"{seekSec.ToString("F3", inv)}%{(seekSec + CopyAnchorWindowSec).ToString("F3", inv)}",
                "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", fullPath,
            ], TimeSpan.FromSeconds(15), ct);
            if (r.ExitCode != 0) return null;

            double? keyframe = null, next = null;
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var c = line.IndexOf(',');
                if (c <= 0 || line.IndexOf('K', c) < 0) continue;
                if (!double.TryParse(line.AsSpan(0, c), NumberStyles.Float, inv, out var pts)) continue;
                if (pts <= seekSec + 0.001) keyframe = pts;
                else { next = pts; break; }
            }
            return keyframe is double kf ? (kf, next) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HLS: key frame lookup failed for {Path} @ {Seek}", fullPath, seekSec);
            return null;
        }
    }

    /// <summary>Measure the real B-pyramid composition delay by probing the
    /// opening video packets and taking max(pts − dts). On a stream-copy this
    /// is how far video presentation lags decode — i.e. how far audio runs
    /// ahead — and unlike has_b_frames it captures deep, irregular pyramids
    /// (UHD remuxes). Returns the delay in seconds, or null on probe failure
    /// (caller keeps the has_b_frames estimate). 0 = no reorder (no B-frames).</summary>
    private async Task<double?> MeasureReorderDelayAsync(string fullPath, int videoStreamIndex, CancellationToken ct)
    {
        try
        {
            // First ~48 packets' pts,dts of the main video stream
            // (MediaProbeCache, cached per file).
            var outp = await _probeCache.GetReorderPacketsCsvAsync(fullPath, videoStreamIndex, ct);
            if (outp is null) return null;

            double max = 0;
            bool any = false;
            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = line.IndexOf(',');
                if (c <= 0) continue;
                if (double.TryParse(line.AsSpan(0, c), NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                 && double.TryParse(line.AsSpan(c + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var dts))
                {
                    any = true;
                    var d = pts - dts;
                    if (d > max) max = d;
                }
            }
            return any ? max : (double?)null;
        }
        catch { return null; }
    }

    private static string? BuildVideoCodecsAttribute(string? codecName, JsonElement stream)
    {
        // Build a `CODECS=…` string MSE can match. For HEVC we always re-tag
        // to hvc1 in ffmpeg args, so the attribute must use hvc1 too.
        if (string.Equals(codecName, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            // hvc1.{profile}.{compat}.L{level}.{constraints}
            // Defaults match Main10 L5.0 — the most common 4K profile.
            int profile = 2; // 1=Main, 2=Main10
            if (stream.TryGetProperty("profile", out var p))
            {
                var pn = p.GetString() ?? "";
                if (pn.Contains("Main 10", StringComparison.OrdinalIgnoreCase)) profile = 2;
                else if (pn.Contains("Main", StringComparison.OrdinalIgnoreCase)) profile = 1;
            }
            int level = 150;
            if (stream.TryGetProperty("level", out var l) && l.TryGetInt32(out var lvl))
                level = lvl;
            return $"hvc1.{profile}.4.L{level}.B0";
        }
        if (string.Equals(codecName, "h264", StringComparison.OrdinalIgnoreCase))
        {
            // avc1.{profile_idc:x2}{constraint:x2}{level_idc:x2}
            int profileIdc = 0x64;  // 100 = High
            int levelIdc   = 0x28;  // 40 = level 4.0
            if (stream.TryGetProperty("level", out var l) && l.TryGetInt32(out var lvl))
                levelIdc = lvl;
            return $"avc1.{profileIdc:X2}00{levelIdc:X2}".ToLowerInvariant();
        }
        if (string.Equals(codecName, "av1", StringComparison.OrdinalIgnoreCase))
            return "av01.0.05M.08"; // Main profile, level 5.0, 8-bit
        if (string.Equals(codecName, "vp9", StringComparison.OrdinalIgnoreCase))
            return "vp09.00.50.08";
        return null;
    }

}
