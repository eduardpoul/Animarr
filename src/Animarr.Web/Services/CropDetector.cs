using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Animarr.Web.Services;

/// <summary>
/// Finds black bars baked into the picture (a 2.39:1 film in a 16:9 frame —
/// letterbox; a 4:3 show in a 16:9 frame — pillarbox) with ffmpeg's
/// <c>cropdetect</c> on a handful of frames sampled across the file. The
/// samples are combined as their union, so a dark scene that looks like extra
/// bars never crops real picture away: the result is the smallest rectangle
/// that holds everything any sample showed. Run once per file version (see
/// <see cref="FileFactsStore"/>), not per playback.
/// </summary>
public static partial class CropDetector
{
    private static readonly double[] SampleAt = [0.15, 0.3, 0.45, 0.6, 0.75, 0.9];
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>What was measured. <see cref="Crop"/> null = no bars (or not
    /// determinable — then <see cref="ValidSamples"/> is 0).</summary>
    public sealed record Result(int Width, int Height, CropRect? Crop, int Samples, int ValidSamples);

    public sealed record CropRect(int X, int Y, int Width, int Height);

    /// <summary>The main video stream of a probe (not a cover picture).</summary>
    public sealed record VideoStream(int StreamIndex, int Width, int Height, string? SampleAspect, double? DurationSec);

    public static VideoStream? MainVideo(string? probeJson)
    {
        if (string.IsNullOrWhiteSpace(probeJson)) return null;
        using var doc = JsonDocument.Parse(probeJson);
        var root = doc.RootElement;
        double? duration = root.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d)
                           && d.ValueKind == JsonValueKind.String
                           && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds) && ds > 0
            ? ds : null;
        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array) return null;
        foreach (var s in streams.EnumerateArray())
        {
            if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "video") continue;
            if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap)
                && ap.TryGetInt32(out var apv) && apv == 1) continue;
            if (!s.TryGetProperty("index", out var ix) || !ix.TryGetInt32(out var index)) continue;
            int w = s.TryGetProperty("width", out var wv) && wv.TryGetInt32(out var wi) ? wi : 0;
            int h = s.TryGetProperty("height", out var hv) && hv.TryGetInt32(out var hi) ? hi : 0;
            if (w <= 0 || h <= 0) continue;
            var sar = s.TryGetProperty("sample_aspect_ratio", out var sv) ? sv.GetString() : null;
            return new VideoStream(index, w, h, sar, duration);
        }
        return null;
    }

    public static async Task<Result?> DetectAsync(string fullPath, VideoStream video, CancellationToken ct)
    {
        var times = video.DurationSec is { } d && d > 4
            ? SampleAt.Select(f => Math.Round(d * f, 3)).ToArray()
            : [0d];

        var crops = new List<CropRect>();
        foreach (var t in times)
        {
            ct.ThrowIfCancellationRequested();
            var c = await SampleAsync(fullPath, video.StreamIndex, t, ct);
            // A (near-)black frame reports a tiny or negative box — no evidence.
            if (c is { Width: > 0, Height: > 0 } && c.Width * (long)c.Height >= video.Width * (long)video.Height / 5)
                crops.Add(c);
        }
        if (crops.Count == 0) return new Result(video.Width, video.Height, null, times.Length, 0);

        int x1 = crops.Min(c => c.X), y1 = crops.Min(c => c.Y);
        int x2 = crops.Max(c => c.X + c.Width), y2 = crops.Max(c => c.Y + c.Height);
        x1 = Math.Clamp(x1, 0, video.Width);  y1 = Math.Clamp(y1, 0, video.Height);
        x2 = Math.Clamp(x2, x1, video.Width); y2 = Math.Clamp(y2, y1, video.Height);
        int w = x2 - x1, h = y2 - y1;

        // Bars thinner than 2 % (or 8 px) are encoder edge noise, not bars.
        bool letterbox = video.Height - h >= Math.Max(8, video.Height / 50);
        bool pillarbox = video.Width - w >= Math.Max(8, video.Width / 50);
        var crop = letterbox || pillarbox
            ? new CropRect(pillarbox ? x1 : 0, letterbox ? y1 : 0, pillarbox ? w : video.Width, letterbox ? h : video.Height)
            : null;
        return new Result(video.Width, video.Height, crop, times.Length, crops.Count);
    }

    /// <summary>cropdetect over ~10 frames from <paramref name="atSec"/>: the
    /// last (accumulated) box it reports.</summary>
    private static async Task<CropRect?> SampleAsync(string fullPath, int streamIndex, double atSec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        // limit as a fraction (≈ 24/255) works for 8- and 10-bit sources alike.
        foreach (var a in new[]
                 {
                     "-hide_banner", "-nostats", "-v", "info",
                     "-ss", atSec.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i", fullPath,
                     "-map", $"0:{streamIndex.ToString(CultureInfo.InvariantCulture)}",
                     "-frames:v", "12", "-an", "-sn", "-dn",
                     "-vf", "cropdetect=limit=0.094:round=2:reset=0",
                     "-f", "null", "-",
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SampleTimeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            ct.ThrowIfCancellationRequested();
            return null;   // this sample timed out — the others still count
        }
        CropRect? last = null;
        foreach (Match m in CropRegex().Matches(await stderr))
            last = new CropRect(
                int.Parse(m.Groups["x"].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["y"].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["w"].ValueSpan, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["h"].ValueSpan, CultureInfo.InvariantCulture));
        return last;
    }

    [GeneratedRegex(@"crop=(?<w>-?\d+):(?<h>-?\d+):(?<x>-?\d+):(?<y>-?\d+)")]
    private static partial Regex CropRegex();
}
