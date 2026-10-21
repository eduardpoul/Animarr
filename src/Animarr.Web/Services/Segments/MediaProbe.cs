using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Animarr.Web.Services.Segments;

/// <summary>Tiny ffprobe helper for the segment pipeline — only the bits the
/// detection cascade needs. ffprobe is resolved from PATH, matching the rest of
/// the app's ffmpeg/ffprobe usage (HlsSessionService, /api/probe).</summary>
public static class MediaProbe
{
    /// <summary>ffprobe only reads container headers; anything slower than this
    /// is a dead mount or a pathological file — kill it rather than wedge the
    /// segment queue (or a player request) forever.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>File duration in seconds, or 0 when ffprobe is unavailable / the
    /// file can't be read.</summary>
    public static async Task<double> GetDurationAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var stdout = await RunFfprobeAsync(new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                filePath,
            }, ct);
            if (stdout is null) return 0;
            return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Run ffprobe and return its stdout, or null when it can't start or
    /// times out. stderr is drained concurrently (a corrupt file can make even
    /// <c>-v error</c> print past the pipe buffer, which used to block ffprobe
    /// forever since nobody read it), and a timeout/cancel kills the process.</summary>
    private static async Task<string?> RunFfprobeAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await stderr;
            await p.WaitForExitAsync(timeout.Token);
            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
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
            var stdout = await RunFfprobeAsync(
                new[] { "-v", "error", "-print_format", "json", "-show_chapters", filePath }, ct);
            if (string.IsNullOrWhiteSpace(stdout)) return Array.Empty<Chapter>();

            using var doc = JsonDocument.Parse(stdout);
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
        catch
        {
            return Array.Empty<Chapter>();
        }
    }

    private static double ParseTime(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String &&
           double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0;
}
