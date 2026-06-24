using System.Diagnostics;
using System.Globalization;

namespace Animarr.Web.Services.Segments;

/// <summary>Tiny ffprobe helper for the segment pipeline — only the bits the
/// detection cascade needs. ffprobe is resolved from PATH, matching the rest of
/// the app's ffmpeg/ffprobe usage (HlsSessionService, /api/probe).</summary>
public static class MediaProbe
{
    /// <summary>File duration in seconds, or 0 when ffprobe is unavailable / the
    /// file can't be read.</summary>
    public static async Task<double> GetDurationAsync(string filePath, CancellationToken ct = default)
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
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                filePath,
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return 0;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch
        {
            return 0;
        }
    }
}
