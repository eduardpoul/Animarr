using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Animarr.Web.Services;

/// <summary>
/// The stream a DLNA renderer gets when the raw file won't do (plan 6.6):
/// a progressive MPEG-TS — the container every renderer takes — with the
/// chosen audio (an embedded track other than the first, or an external dub
/// as a second input), since AVTransport has no track selection. Video is
/// copied when TS can carry it (H.264 / HEVC / MPEG-2), else — or on request —
/// re-encoded to H.264 (software, ≤ 1080p). Audio is copied when TVs decode
/// it in TS (AAC / AC-3 / E-AC-3 / MP3 / MP2), else AC-3 (keeps 5.1). Seeking
/// is by time: the renderer's <c>TimeSeekRange.dlna.org</c> restarts ffmpeg
/// there. Independent of the HLS sessions; a few at a time.
/// </summary>
public sealed partial class DlnaStreamService
{
    private const int MaxConcurrent = 3;
    private static readonly SemaphoreSlim Gate = new(MaxConcurrent, MaxConcurrent);

    private static readonly HashSet<string> TsVideo = new(StringComparer.OrdinalIgnoreCase)
        { "h264", "hevc", "mpeg2video", "mpeg1video" };
    private static readonly HashSet<string> TsAudio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "ac3", "eac3", "mp3", "mp2" };

    /// <summary>contentFeatures of the stream: time-seek only, converted,
    /// streaming transfer, DLNA 1.5.</summary>
    public const string ProtocolFlags = "DLNA.ORG_OP=10;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01100000000000000000000000000000";
    public const string ContentType = "video/mpeg";

    private readonly MediaProbeCache _probeCache;
    private readonly ILogger<DlnaStreamService> _logger;

    public DlnaStreamService(MediaProbeCache probeCache, ILogger<DlnaStreamService> logger)
    {
        _probeCache = probeCache;
        _logger     = logger;
    }

    /// <summary>The ffmpeg command for one request, or why there is none.</summary>
    public sealed record Plan(IReadOnlyList<string> Args, double? DurationSec, bool VideoCopied, bool AudioCopied);

    /// <param name="audioOffsetMs">Sync offset of the audio (+ = the sound
    /// later): the audio comes from its own input (the dub, or the video file
    /// opened a second time) shifted by <c>-itsoffset</c> — copied audio stays
    /// copied.</param>
    public async Task<(Plan? Plan, string? Error)> BuildAsync(string videoPath, int audioIndex, string? externalAudio,
        bool transcodeVideo, double seekSec, CancellationToken ct, int audioOffsetMs = 0)
    {
        var json = await _probeCache.GetStreamsJsonAsync(videoPath, ct);
        var video = CropDetector.MainVideo(json);
        if (video is null) return (null, "No video stream.");
        var (videoCodec, audioCodecs) = Codecs(json);

        string? audioCodec;
        // A dub shorter than the video ends the TS's audio early — renderers
        // stall or stop there. One that doesn't cover the video (or of unknown
        // length) is transcoded and padded with silence to the video's end.
        bool padDub = false;
        if (externalAudio is not null)
        {
            var dubJson = await _probeCache.GetStreamsJsonAsync(externalAudio, ct);
            audioCodec = Codecs(dubJson).Audio.FirstOrDefault();
            var videoDur = MediaFactsService.FormatDuration(json) ?? video.DurationSec;
            padDub = !(MediaFactsService.FormatDuration(dubJson) is double dubDur && videoDur is double vd && dubDur >= vd - 0.5);
        }
        else if (audioIndex < 0 || (audioCodecs.Count > 0 && audioIndex >= audioCodecs.Count))
            return (null, $"audio must be 0..{Math.Max(0, audioCodecs.Count - 1)} for this file.");
        else
            audioCodec = audioCodecs.Count > 0 ? audioCodecs[audioIndex] : null;

        bool copyVideo = !transcodeVideo && videoCodec is not null && TsVideo.Contains(videoCodec);
        bool copyAudio = !padDub && audioCodec is not null && TsAudio.Contains(audioCodec);
        var ss = seekSec > 0 ? seekSec.ToString("0.###", CultureInfo.InvariantCulture) : null;

        // An offset needs the audio on an input of its own: -itsoffset shifts
        // every stream of the input it precedes.
        bool shifted = audioOffsetMs != 0 && (externalAudio is not null || audioCodecs.Count > 0);
        string? audioInput = externalAudio ?? (shifted ? videoPath : null);
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (ss is not null) a.AddRange(["-ss", ss]);
        a.AddRange(["-i", videoPath]);
        if (audioInput is not null)
        {
            if (ss is not null) a.AddRange(["-ss", ss]);
            if (shifted) a.AddRange(["-itsoffset", (audioOffsetMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)]);
            a.AddRange(["-i", audioInput]);
        }
        a.AddRange(["-map", $"0:{video.StreamIndex.ToString(CultureInfo.InvariantCulture)}"]);
        if (externalAudio is not null) a.AddRange(["-map", "1:a:0?"]);
        else if (audioCodecs.Count > 0)
            a.AddRange(["-map", $"{(shifted ? 1 : 0)}:a:{audioIndex.ToString(CultureInfo.InvariantCulture)}"]);

        if (copyVideo) a.AddRange(["-c:v", "copy"]);
        else a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "21", "-pix_fmt", "yuv420p",
                         "-vf", "scale='min(1920,iw)':-2", "-g", "48"]);
        if (copyAudio) a.AddRange(["-c:a", "copy"]);
        else a.AddRange(["-c:a", "ac3", "-b:a", "448k"]);
        if (padDub)
        {
            // Silence up to the video's end (finite on its own), and the output
            // stops with the video.
            var rest = (video.DurationSec ?? MediaFactsService.FormatDuration(json)) is double total
                ? Math.Max(1, total - seekSec + 1) : (double?)null;
            a.AddRange(["-af", rest is double w ? "apad=whole_dur=" + w.ToString("0.###", CultureInfo.InvariantCulture) : "apad",
                        "-shortest"]);
        }
        a.AddRange(["-sn", "-dn"]);
        // A negative offset puts the first sound before 0: lift both streams.
        if (shifted && audioOffsetMs < 0) a.AddRange(["-avoid_negative_ts", "make_zero"]);
        a.AddRange(["-f", "mpegts", "-mpegts_flags", "+resend_headers",
                    "-muxdelay", "0", "-muxpreload", "0", "pipe:1"]);
        return (new Plan(a, video.DurationSec, copyVideo, copyAudio), null);
    }

    private static (string? Video, List<string> Audio) Codecs(string? probeJson)
    {
        string? video = null;
        var audio = new List<string>();
        if (string.IsNullOrWhiteSpace(probeJson)) return (video, audio);
        using var doc = JsonDocument.Parse(probeJson);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return (video, audio);
        foreach (var s in streams.EnumerateArray())
        {
            var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
            var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
            if (type == "audio") audio.Add(codec);
            else if (type == "video" && video is null
                     && !(s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap)
                          && ap.TryGetInt32(out var v) && v == 1))
                video = codec;
        }
        return (video, audio);
    }

    /// <summary>Run <paramref name="plan"/> and copy its output to the
    /// response until the renderer hangs up (ffmpeg is killed then). False
    /// when all slots are busy.</summary>
    public async Task<bool> StreamAsync(Plan plan, HttpResponse response, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct)) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var arg in plan.Args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await response.StartAsync(ct);
                await p.StandardOutput.BaseStream.CopyToAsync(response.Body, 64 * 1024, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The renderer hung up (stop, seek → a new request) — normal.
            }
            finally
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
            }
            var err = (await stderr.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Trim();
            if (err.Length > 0 && !ct.IsCancellationRequested)
                _logger.LogWarning("DLNA stream ffmpeg: {Err}", err.Length > 2000 ? err[^2000..] : err);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Start of a <c>TimeSeekRange.dlna.org: npt=…-…</c> header in
    /// seconds ("123.4" or "0:02:03.4"); null when absent / unparseable.</summary>
    public static double? ParseTimeSeekStart(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var m = NptRegex().Match(header);
        if (!m.Success) return null;
        var start = m.Groups["start"].Value;
        if (start.Contains(':'))
        {
            var parts = start.Split(':');
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mi)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return null;
            return h * 3600 + mi * 60 + s;
        }
        return double.TryParse(start, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : null;
    }

    /// <summary><c>npt=123.400-</c> / <c>npt=123.400-1440.000/1440.000</c>.</summary>
    public static string FormatNpt(double start, double? duration)
    {
        static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        return duration is { } d ? $"npt={F(start)}-{F(d)}/{F(d)}" : $"npt={F(start)}-";
    }

    [GeneratedRegex(@"npt\s*=\s*(?<start>[0-9:.]+)\s*-", RegexOptions.IgnoreCase)]
    private static partial Regex NptRegex();
}
