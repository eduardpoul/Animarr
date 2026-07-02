using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

// Idle/crashed-session reaping, tmp-dir cleanup and IDisposable teardown.
public sealed partial class HlsSessionService
{
    // ─── GC ──────────────────────────────────────────────────────────────────

    private void GarbageCollect()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (token, session) in _sessions)
        {
            if (session.LastActive < cutoff)
            {
                _logger.LogInformation("Reaping idle HLS session {Token} (last active {Age:F0}s ago)",
                    token, (DateTime.UtcNow - session.LastActive).TotalSeconds);
                Stop(token);
                continue;
            }

            // Crash detection: ffmpeg exited with non-zero. Player will get
            // 503s on every segment, so kill the session decisively rather
            // than letting it linger another 5 minutes.
            if (session.Process.HasExited && session.Process.ExitCode != 0)
            {
                _logger.LogInformation("Reaping crashed HLS session {Token} (ffmpeg exit {Code})", token, session.Process.ExitCode);
                Stop(token);
                continue;
            }

            // Incomplete-exit detection: ffmpeg exited successfully but only
            // produced part of the segments we expected. This shouldn't
            // happen with VOD encoding to completion, but can happen if our
            // probe under-counted duration vs ffmpeg's interpretation. Don't
            // tear it down (player can still seek into what exists) — just
            // note it for log-diving.
            if (session.Process.HasExited && session.Process.ExitCode == 0
                && session.SegmentCount > 0)
            {
                var produced = HighestProducedSegment(session.OutputDir) + 1;
                if (produced < session.SegmentCount)
                {
                    _logger.LogDebug("HLS {Token}: ffmpeg done at seg-{Produced}/{Total} — partial encoding",
                        token, produced, session.SegmentCount);
                }
            }
        }

        // Sweep orphan tmp dirs (a previous process or crash may have left
        // some behind). Anything under _rootDir not matching an active token
        // is fair game.
        try
        {
            var activeTokens = _sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(_rootDir))
            {
                var name = Path.GetFileName(dir);
                if (activeTokens.Contains(name)) continue;
                // Only delete dirs older than 2 min so we don't race a
                // freshly-created session whose token hasn't reached the
                // dictionary yet.
                try
                {
                    var info = new DirectoryInfo(dir);
                    if ((DateTime.UtcNow - info.CreationTimeUtc).TotalMinutes < 2) continue;
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("HLS: swept orphan session dir {Dir}", name);
                }
                catch { /* best-effort */ }
            }
        }
        catch { }
    }

    /// <summary>Diagnostic snapshot of every live session for /api/hls/sessions.</summary>
    public IReadOnlyList<HlsSessionStatus> Snapshot()
    {
        return _sessions.Select(kv =>
        {
            var s = kv.Value;
            var produced = HighestProducedSegment(s.OutputDir) + 1;
            var exited   = s.Process.HasExited;
            return new HlsSessionStatus(
                Token:           kv.Key,
                SourcePath:      s.SourcePath,
                StartSeekSec:    s.SeekSec,
                SegmentsTotal:   s.SegmentCount,
                SegmentsReady:   Math.Max(produced, 0),
                IdleSec:         (DateTime.UtcNow - s.LastActive).TotalSeconds,
                FfmpegExited:    exited,
                FfmpegExitCode:  exited ? s.Process.ExitCode : (int?)null
            );
        }).ToArray();
    }

    public sealed record HlsSessionStatus(
        string Token, string SourcePath, double StartSeekSec,
        int SegmentsTotal, int SegmentsReady, double IdleSec,
        bool FfmpegExited, int? FfmpegExitCode);

    private static void TryRemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _gcTimer.Dispose();
        foreach (var token in _sessions.Keys.ToList())
            Stop(token);
    }

}
