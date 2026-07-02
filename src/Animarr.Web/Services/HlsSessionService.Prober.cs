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
            var psi = new ProcessStartInfo
            {
                FileName               = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in new[]
            {
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                fullPath,
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double duration = 0;
            if (root.TryGetProperty("format", out var fmt)
                && fmt.TryGetProperty("duration", out var durEl)
                && double.TryParse(durEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                duration = d;

            string? vCodec = null;
            string? aCodec = null;
            int width = 1280, height = 720;
            bool hasDv = false;
            bool is10Bit = false;
            string? colorTransfer = null;   // smpte2084 → HDR10, arib-std-b67 → HLG
            int audioChannels = 0;
            string? audioLanguage = null;
            string? videoCodecsAttr = null;
            string? audioCodecsAttr = null;
            double reorderDelaySec = 0;   // B-frame reorder delay → audio itsoffset for stream-copy

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    if (!s.TryGetProperty("codec_type", out var ct2)) continue;
                    var type = ct2.GetString();
                    if (type == "video" && vCodec is null)
                    {
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
                        if (s.TryGetProperty("side_data_list", out var sd))
                        {
                            foreach (var sdEl in sd.EnumerateArray())
                                if (sdEl.TryGetProperty("side_data_type", out var sdt)
                                    && (sdt.GetString() ?? "").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                                    hasDv = true;
                        }
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
                    }
                    else if (type == "audio" && aCodec is null)
                    {
                        // Capture the FIRST audio stream's codec name. ChoosePlan
                        // uses this to decide passthrough vs AAC re-encode on the
                        // MPEG-TS branch. We still default the MSE CODECS=… string
                        // to mp4a.40.2 because the fMP4 path always transcodes;
                        // the TS branch sidesteps the master playlist's codecs
                        // attribute entirely (browsers infer from segment bytes).
                        if (s.TryGetProperty("codec_name", out var acn)) aCodec = acn.GetString();
                        if (s.TryGetProperty("channels",   out var ach) && ach.TryGetInt32(out var ci))
                            audioChannels = ci;
                        // Language tag: ffprobe puts it under tags.language, but
                        // MKV-from-Matroska sometimes uses LANGUAGE (uppercase).
                        if (s.TryGetProperty("tags", out var tags))
                        {
                            if (tags.TryGetProperty("language", out var langEl))
                                audioLanguage = langEl.GetString();
                            else if (tags.TryGetProperty("LANGUAGE", out var langElU))
                                audioLanguage = langElU.GetString();
                        }
                        audioCodecsAttr = "mp4a.40.2";
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

            // Override the has_b_frames estimate with the ACTUAL max composition
            // offset measured from the opening packets — accurate for deep
            // B-pyramids (UHD remuxes) where has_b_frames badly under-counts.
            // Falls back to the estimate above only if the packet probe yields
            // nothing (returns null).
            if (await MeasureReorderDelayAsync(fullPath, ct) is double measuredDelay)
                reorderDelaySec = measuredDelay;

            return new ProbeInfo(duration, vCodec, aCodec, width, height, combined, hasDv, is10Bit,
                colorTransfer, audioChannels, audioLanguage, reorderDelaySec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", fullPath);
            return null;
        }
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

    /// <summary>Measure the real B-pyramid composition delay by probing the
    /// opening video packets and taking max(pts − dts). On a stream-copy this
    /// is how far video presentation lags decode — i.e. how far audio runs
    /// ahead — and unlike has_b_frames it captures deep, irregular pyramids
    /// (UHD remuxes). Returns the delay in seconds, or null on probe failure
    /// (caller keeps the has_b_frames estimate). 0 = no reorder (no B-frames).</summary>
    private async Task<double?> MeasureReorderDelayAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "ffprobe",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            // Read only the first ~48 packets (a handful of GOPs) from the file
            // start — enough to see the steady-state pyramid depth, cheap to read.
            foreach (var a in new[]
            {
                "-v", "error", "-select_streams", "v:0",
                "-read_intervals", "%+#48",
                "-show_entries", "packet=pts_time,dts_time",
                "-of", "csv=p=0",
                fullPath,
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;

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
