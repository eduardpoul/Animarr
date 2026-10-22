using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Animarr.Web.Services;

// Bounded session dirs: app-side pruning of played segments + SIGSTOP/SIGCONT
// look-ahead throttling of ffmpeg.
//
// Why: ffmpeg never deleted anything — `-hls_playlist_type vod` forces
// hls_list_size to 0, so `delete_segments` had nothing to drop — and nothing
// paced it, so a stream-copy wrote the WHOLE remaining file into the session
// dir at disk speed (~10× realtime and up). /tmp is an 8 GB RAM tmpfs on prod:
// every HLS session held its entire output in RAM until Stop / the idle GC, a
// 4K remux could hit ENOSPC mid-movie, and a paused viewer's re-encode burned
// GPU/CPU to EOF for content nobody watched. Two halves:
//   • Pruning — delete segments more than PruneKeepBehindSegments behind the
//     furthest segment the player has requested. A scrub back past that window
//     asks for a missing segment → WaitForFileAsync's seek-restart re-produces
//     it (the path that already served scrubs before a resume point).
//   • Throttle — SIGSTOP ffmpeg once it is ThrottleHighWaterSegments ahead of
//     the player's furthest request, SIGCONT when the lead drops to
//     ThrottleLowWaterSegments (or the player asks for anything further).
// Not `-readrate`: a rate cap only slows the run-away (at 2× a paused viewer
// still fills tmpfs at 2× realtime — the keepalive keeps paused sessions
// alive), while a lead cap bounds it; and SIGSTOP never slows startup or a
// seek-restart, which run at full speed until they are a high-water mark ahead.
// Simply dropping `vod` (so delete_segments fires) wasn't an option either:
// unpaced, ffmpeg outruns the player by the list size within seconds and deletes
// segments the player hasn't fetched yet → seek-restart loops.
public sealed partial class HlsSessionService
{
    /// <summary>Segments kept on disk behind the furthest segment the player has
    /// requested (30 × 4 s = 2 min). Players request AHEAD of the playhead —
    /// hls.js up to its 90 s forward buffer, ExoPlayer (TV) ~50 s — so this
    /// leaves ~30 s (hls.js, plus its own 60 s back buffer in memory) to ~70 s
    /// (ExoPlayer, no back buffer: every −10 s press re-fetches) of instant
    /// back-scrub. Further back goes through the seek-restart.</summary>
    private const int PruneKeepBehindSegments = 30;

    /// <summary>Pause ffmpeg once it is this many segments (80 s) ahead of the
    /// player's furthest request. With PruneKeepBehindSegments that bounds a
    /// session dir to ~50 segments (+ one throttle tick of overshoot) — the
    /// "keep the last 50 segments" the muxer flags were meant to give.</summary>
    private const int ThrottleHighWaterSegments = 20;

    /// <summary>Resume ffmpeg once its lead drops to this many segments (40 s).
    /// The hysteresis keeps a stream-copy from flapping STOP/CONT on every
    /// request; 40 s of produced-but-unrequested media is plenty of cushion for
    /// a re-encode to get going again.</summary>
    private const int ThrottleLowWaterSegments = 10;

    /// <summary>How far past this run's newest segment a request may land and
    /// still be waited for; anything further restarts ffmpeg at the target
    /// (<see cref="WaitForFileAsync"/>).</summary>
    private const int RestartThresholdSegments = 12;

    /// <summary>Throttle/prune tick. Pausing is the only part that can't be
    /// driven by segment requests (ffmpeg races ahead between them), so the
    /// interval bounds the overshoot past the high-water mark to ~0.1 s of
    /// stream-copy — measured: a page-cached copy ran ~85 segments past it with
    /// a 500 ms tick. A tick is a semaphore try + one stat() per session.</summary>
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(100);

    // Job-control signal numbers: Linux vs the BSD family (macOS).
    private static readonly int SigStop = OperatingSystem.IsLinux() ? 19 : 17;
    private static readonly int SigCont = OperatingSystem.IsLinux() ? 18 : 19;

    /// <summary>SIGSTOP/SIGCONT need POSIX kill(2). On Windows (dev box) the
    /// throttle is off — ffmpeg runs to EOF as before; pruning still applies.
    /// Flipped off for good if libc can't be bound.</summary>
    private static bool _canSuspend = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD();

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    private int _throttleTickBusy;

    private void ThrottleTick()
    {
        if (_sessions.IsEmpty) return;
        // Timer callbacks can overlap if one runs long (slow disk) — skip instead.
        if (Interlocked.Exchange(ref _throttleTickBusy, 1) == 1) return;
        try
        {
            foreach (var (_, session) in _sessions)
            {
                try { ThrottleAndPrune(session); }
                catch (ObjectDisposedException) { /* stopped mid-tick */ }
                catch (Exception ex) { _logger.LogDebug(ex, "HLS {Token}: throttle tick failed", session.Token); }
            }
        }
        finally { Volatile.Write(ref _throttleTickBusy, 0); }
    }

    private void ThrottleAndPrune(HlsSession session)
    {
        // Probe-less fallback (no pre-written playlist): the player follows
        // ffmpeg's own growing playlist like a live stream, and a restart can't
        // rebuild it (RegenerateMediaPlaylist needs a segment count) — so keep
        // the legacy behaviour there: no pausing, no pruning.
        if (session.SegmentCount <= 0) return;

        // Hold the restart lock for the tick: a seek-restart swaps the process
        // and deletes/re-creates segment files, and pruning with a floor computed
        // before a backward restart could delete the new run's first segments.
        // Busy = a restart is running → just catch the next tick.
        if (!session.RestartLock.Wait(0)) return;
        try
        {
            FinalizeKeyframeSegments(session);
            int floor;
            lock (session.ThrottleLock)
            {
                var lead = RefreshProducedTop(session) - session.PlayFrontier;
                if (!session.Paused && lead >= ThrottleHighWaterSegments)
                {
                    if (TrySignal(session.Process, SigStop))
                    {
                        session.Paused = true;
                        _logger.LogDebug("HLS {Token}: ffmpeg paused at seg-{Top} (player at seg-{Frontier})",
                            session.Token, session.ProducedTop, session.PlayFrontier);
                    }
                }
                else if (session.Paused && lead <= ThrottleLowWaterSegments)
                {
                    // Safety net — a segment request normally resumes it first.
                    ResumeLocked(session);
                }

                floor = session.PlayFrontier - PruneKeepBehindSegments;
                if (floor <= session.LastPruneFloor) return;
            }

            if (PruneBelow(session.OutputDir, floor))
                lock (session.ThrottleLock) session.LastPruneFloor = floor;
        }
        finally
        {
            try { session.RestartLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Called for every segment request BEFORE it is served or waited
    /// on: advances the play frontier and wakes a paused ffmpeg as soon as the
    /// player gets within the low-water mark (or asks past what exists).</summary>
    private void NotePlayerRequest(HlsSession session, int segment)
    {
        // Past the playlist's end isn't a real position — it must not drag the
        // frontier (and with it the prune floor) beyond everything on disk.
        if (session.SegmentCount <= 0 || segment >= session.SegmentCount) return;
        lock (session.ThrottleLock)
        {
            var top = RefreshProducedTop(session);
            // A request this far ahead is served by a restart at the target,
            // whose SwapProcess sets the frontier. If that restart never happens
            // (request aborted while queued on RestartLock, ffmpeg failed to
            // start), a frontier moved here would prune this run's upcoming
            // segments and wedge it — leave it to the restart.
            if (segment > top + RestartThresholdSegments) return;
            if (segment > session.PlayFrontier) session.PlayFrontier = segment;
            if (!session.Paused) return;
            if (top - session.PlayFrontier <= ThrottleLowWaterSegments)
                ResumeLocked(session);
        }
    }

    /// <summary>Highest contiguous segment of the current run on disk. Walks
    /// forward from the last known top, so it's one or two stat calls per call
    /// instead of listing the whole session dir. Caller holds ThrottleLock.</summary>
    private static int RefreshProducedTop(HlsSession session)
    {
        var ext = SegmentExtension(session.Plan);
        var n = session.ProducedTop;
        while (File.Exists(Path.Combine(session.OutputDir, $"seg-{n + 1:D5}.{ext}"))) n++;
        session.ProducedTop = n;
        return n;
    }

    /// <summary><see cref="RefreshProducedTop"/> for callers outside the lock.</summary>
    private static int CurrentProducedTop(HlsSession session)
    {
        lock (session.ThrottleLock) return RefreshProducedTop(session);
    }

    private void ResumeLocked(HlsSession session)
    {
        // Clear the flag even if the signal fails: the process is gone then,
        // and a stale "paused" would only block the next pause.
        TrySignal(session.Process, SigCont);
        session.Paused = false;
        _logger.LogDebug("HLS {Token}: ffmpeg resumed at seg-{Top} (player at seg-{Frontier})",
            session.Token, session.ProducedTop, session.PlayFrontier);
    }

    private bool TrySignal(Process proc, int signal)
    {
        if (!_canSuspend) return false;
        try
        {
            // An exited process's pid may already be reused — never signal it.
            if (proc.HasExited) return false;
            return SysKill(proc.Id, signal) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _canSuspend = false;
            _logger.LogWarning(ex, "HLS: kill(2) unavailable — ffmpeg look-ahead throttling disabled");
            return false;
        }
        catch (InvalidOperationException) { return false; }   // disposed / never started
    }

    /// <summary>Delete every finished segment numbered below <paramref name="floor"/>.
    /// init.mp4 / _init_restart.mp4 / playlists / *.tmp partials never match.
    /// Returns false when something couldn't be deleted (Windows: a response
    /// still has it open) so the next tick retries.</summary>
    private static bool PruneBelow(string dir, int floor)
    {
        var complete = true;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "seg-*"))
            {
                var n = ParseSegmentNumber(Path.GetFileName(path));
                if (n < 0 || n >= floor) continue;
                try { File.Delete(path); }
                catch { complete = false; }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch { complete = false; }
        return complete;
    }

    /// <summary>Seek-restart helper: drop every segment numbered at or above
    /// the restart target. The new run re-creates them in order; leaving the old
    /// run's files in place let the player splice a different encoder run in
    /// without a discontinuity marker, and made produced-top detection jump.</summary>
    private static void DeleteSegmentsFrom(string dir, int fromSegment)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "seg-*"))
            {
                var n = ParseSegmentNumber(Path.GetFileName(path));
                if (n < fromSegment) continue;
                try { File.Delete(path); } catch { }
            }
            // A key-frame run's unfinalized files (killed mid-segment, or never
            // listed) — the new run writes them again.
            foreach (var path in Directory.EnumerateFiles(dir, "raw-*"))
            {
                var n = ParseRawNumber(Path.GetFileName(path));
                if (n >= 0 && n >= fromSegment) try { File.Delete(path); } catch { }
            }
        }
        catch { /* best-effort */ }
    }
}
