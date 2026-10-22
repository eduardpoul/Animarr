using System.Globalization;
using Animarr.Shared.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Measured per-file facts for the players (plan §6.2 "MediaFacts", 3.3):
/// the black-bar crop of the picture. Measured lazily on the first request
/// for a file version and kept by <see cref="FileFactsStore"/> — a second
/// open, a restart or another profile reads the stored answer.
/// </summary>
public sealed class MediaFactsService
{
    /// <summary>Bump to re-measure every file (e.g. after a detector change).</summary>
    private const string CropKind = "crop1";
    private static readonly TimeSpan CropTimeout = TimeSpan.FromSeconds(120);

    private readonly MediaProbeCache _probeCache;
    private readonly FileFactsStore _store;

    public MediaFactsService(MediaProbeCache probeCache, FileFactsStore store)
    {
        _probeCache = probeCache;
        _store      = store;
    }

    /// <summary>Crop detection in an AnalysisGate slot; a full queue = no
    /// result now (status "unknown", the next request tries again).</summary>
    private static async Task<CropDetector.Result?> DetectGatedAsync(string fullPath, CropDetector.VideoStream video,
        CancellationToken ct)
    {
        try { return await AnalysisGate.Default.RunAsync(t => CropDetector.DetectAsync(fullPath, video, t), park: false, ct); }
        catch (AnalysisBusyException) { return null; }
    }

    /// <summary>The facts of <paramref name="fullPath"/> (already validated).
    /// Waits up to <paramref name="wait"/> for a crop measurement still to be
    /// made; past that the answer says "pending" and the measurement finishes
    /// in the background.</summary>
    public async Task<MediaFactsDto> GetAsync(string fullPath, TimeSpan wait, CancellationToken ct)
    {
        var json  = await _probeCache.GetStreamsJsonAsync(fullPath, ct);
        var video = CropDetector.MainVideo(json);
        double? duration = video?.DurationSec ?? FormatDuration(json);
        if (video is null) return new MediaFactsDto(duration, null);

        var crop = _store.TryGet<CropDetector.Result>(CropKind, fullPath)
                   ?? await _store.GetOrComputeAsync(CropKind, fullPath,
                          token => DetectGatedAsync(fullPath, video, token), CropTimeout, wait, ct);
        string status = crop is null
            ? (_store.IsPending(CropKind, fullPath) ? "pending" : "unknown")
            : crop.Crop is not null ? "detected"
            : crop.ValidSamples > 0 ? "none" : "unknown";
        return new MediaFactsDto(duration, Describe(video, crop?.Crop, status));
    }

    internal static VideoFactsDto Describe(CropDetector.VideoStream v, CropDetector.CropRect? crop, string status)
    {
        double sar = ParseRatio(v.SampleAspect) ?? 1.0;
        double display = Math.Round(v.Width * sar / v.Height, 4);
        double content = crop is null ? display : Math.Round(crop.Width * sar / crop.Height, 4);
        bool letterbox = crop is not null && crop.Height < v.Height;
        bool pillarbox = crop is not null && crop.Width < v.Width;
        return new VideoFactsDto(v.Width, v.Height, ParseRatio(v.SampleAspect) is null ? null : v.SampleAspect,
            display, crop is null ? null : new CropRectDto(crop.X, crop.Y, crop.Width, crop.Height),
            content, letterbox, pillarbox, status);
    }

    /// <summary><c>format.duration</c> of a cached ffprobe streams JSON.</summary>
    internal static double? FormatDuration(string? probeJson)
    {
        if (string.IsNullOrWhiteSpace(probeJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(probeJson);
            return doc.RootElement.TryGetProperty("format", out var f)
                   && f.TryGetProperty("duration", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                   && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
                ? s : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>"4:3" → 1.333…; null for "0:1", "N/A", garbage.</summary>
    private static double? ParseRatio(string? r)
    {
        if (string.IsNullOrWhiteSpace(r)) return null;
        var parts = r.Split(':');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
               && a > 0 && b > 0
            ? a / b : null;
    }
}
