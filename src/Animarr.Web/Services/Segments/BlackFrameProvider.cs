using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Cascade level 3 (opt-in): black-frame video analysis as a last-resort credits
/// approximation. Anime usually fades to black before the ending, so the first
/// solid black in the tail is a rough credits-start guess. Heuristic and
/// expensive (decodes video), so it's gated behind segments.blackframe_enabled
/// and only runs when nothing earlier produced a credits segment (it's last in
/// the cascade, and the orchestrator stops once credits are found).
/// </summary>
public sealed partial class BlackFrameProvider(ILogger<BlackFrameProvider> logger) : ISegmentProvider
{
    public SegmentSource Source => SegmentSource.BlackFrame;
    public int Order => 30;
    public bool Cheap => false;   // decodes video — background pass only, and opt-in

    /// <summary>A stuck ffmpeg (dead mount, corrupt file) is killed after this.
    /// Generous: a 7-min 4K HEVC tail decoded on 2 threads at nice 10 can take
    /// several minutes on a NAS CPU.</summary>
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(10);

    public bool CanRun(SegmentEpisodeContext ctx) => ctx.DurationSec > 60 && File.Exists(ctx.FilePath);

    public async Task<IReadOnlyList<DetectedSegment>> DetectAsync(SegmentEpisodeContext ctx, CancellationToken ct)
    {
        // Scan the tail only (decoding the whole file would be far too costly).
        var tailWindow = Math.Min(420, ctx.DurationSec * 0.4);   // last ~7 min / 40%
        var tailStart  = ctx.DurationSec - tailWindow;

        // Background pass: don't start a video decode while someone is watching.
        if (ctx.WaitForIdleAsync is { } waitForIdle) await waitForIdle(ct);
        var blacks = await DetectBlackAsync(ctx.FilePath, tailStart, tailWindow, ct);
        if (blacks.Count == 0) return Array.Empty<DetectedSegment>();

        // First fade-to-black starting in the last 40% → credits boundary guess.
        var minStart = ctx.DurationSec * 0.6;
        var pick = blacks.FirstOrDefault(b => b >= minStart);
        if (pick <= 0) return Array.Empty<DetectedSegment>();

        logger.LogInformation("[BlackFrame] {File} → credits ~{Start:F0}s", Path.GetFileName(ctx.FilePath), pick);
        return new[] { new DetectedSegment(SegmentKind.Credits, pick, ctx.DurationSec) };
    }

    /// <summary>Absolute (file-relative) start times of black segments found in
    /// the window via ffmpeg's blackdetect filter. Empty on any failure. Runs
    /// deprioritised (nice 10 + idle I/O on Linux, BelowNormal elsewhere) with a
    /// 2-thread decoder cap, like the other background decodes.</summary>
    private async Task<List<double>> DetectBlackAsync(string file, double ssSec, double durSec, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var starts = new List<double>();
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
            foreach (var a in new[]
            {
                // -nostats: no per-frame progress spam in the drained stderr
                // (blackdetect's own lines are info-level and unaffected).
                "-nostdin", "-hide_banner", "-nostats",
                "-threads", "2",
                "-ss", ssSec.ToString("0.###", inv),
                "-i", file,
                "-t", durSec.ToString("0.###", inv),
                "-an",
                "-vf", "blackdetect=d=0.10:pix_th=0.10",
                "-f", "null", "-",
            }) psi.ArgumentList.Add(a);

            using var p = LowPriorityProcess.Start(psi);
            if (p is null) return starts;
            // blackdetect reports on stderr; input-seek makes its timestamps
            // window-relative, so add ssSec back to get a file-relative time.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FfmpegTimeout);
            string stderr;
            try
            {
                var stdoutTask = p.StandardOutput.ReadToEndAsync(timeout.Token);   // "-f null" writes nothing, but keep the pipe drained
                stderr = await p.StandardError.ReadToEndAsync(timeout.Token);
                await stdoutTask;
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("[BlackFrame] ffmpeg timed out on {File}", Path.GetFileName(file));
                return starts;
            }

            foreach (Match m in BlackStartRegex().Matches(stderr))
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, inv, out var rel))
                    starts.Add(ssSec + rel);
            starts.Sort();
            return starts;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[BlackFrame] blackdetect failed for {File}", Path.GetFileName(file));
            return starts;
        }
    }

    [GeneratedRegex(@"black_start:([\d.]+)")]
    private static partial Regex BlackStartRegex();
}
