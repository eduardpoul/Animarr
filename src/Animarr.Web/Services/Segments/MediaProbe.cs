using System.Globalization;
using System.Text.Json;

namespace Animarr.Web.Services.Segments;

/// <summary>Tiny ffprobe helper for the segment pipeline — only the bits the
/// detection cascade needs. ffprobe is resolved from PATH, matching the rest of
/// the app's ffmpeg/ffprobe usage (HlsSessionService, /api/probe). Runs through
/// <see cref="FfprobeRunner"/>: stderr drained, killed after
/// <see cref="ProbeTimeout"/> — the token is the app lifetime, so a dead mount
/// used to park a background pass forever.</summary>
public static class MediaProbe
{
    /// <summary>ffprobe only reads container headers; anything slower than this
    /// is a dead mount or a pathological file — kill it rather than wedge the
    /// segment queue forever.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>File duration in seconds, or 0 when ffprobe is unavailable / the
    /// file can't be read.</summary>
    public static async Task<double> GetDurationAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var r = await FfprobeRunner.RunAsync(
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                filePath,
            ], ProbeTimeout, ct);
            return double.TryParse(r.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>One embedded chapter: optional title + start/end seconds.</summary>
    public sealed record Chapter(string? Title, double StartSec, double EndSec);

    /// <summary>Embedded chapters via <c>ffprobe -show_chapters</c>, or an empty
    /// list when the file has none / ffprobe is unavailable.</summary>
    public static async Task<IReadOnlyList<Chapter>> GetChaptersAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var r = await FfprobeRunner.RunAsync(
                ["-v", "error", "-print_format", "json", "-show_chapters", filePath], ProbeTimeout, ct);
            return ParseChapters(r.StdOut);
        }
        catch
        {
            return Array.Empty<Chapter>();
        }
    }

    /// <summary>The chapters of <c>ffprobe -print_format json -show_chapters</c>
    /// output (also what <see cref="MediaProbeCache.GetChaptersJsonAsync"/>
    /// caches); empty for none / unparseable.</summary>
    public static IReadOnlyList<Chapter> ParseChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<Chapter>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("chapters", out var chapters) ||
                chapters.ValueKind != JsonValueKind.Array)
                return Array.Empty<Chapter>();

            var list = new List<Chapter>();
            foreach (var ch in chapters.EnumerateArray())
            {
                var start = ParseTime(ch, "start_time");
                var end   = ParseTime(ch, "end_time");
                string? title = null;
                if (ch.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object &&
                    tags.TryGetProperty("title", out var t))
                    title = t.GetString();
                list.Add(new Chapter(title, start, end));
            }
            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<Chapter>();
        }
    }

    private static double ParseTime(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String &&
           double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0;
}
