using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

// Per-session record: ffmpeg process handle, restart lock, discontinuity boundaries.
public sealed partial class HlsSessionService
{
    // ─── Per-session record ──────────────────────────────────────────────────

    private sealed class HlsSession
    {
        public string Token       { get; }
        public string SourcePath  { get; }
        public string OutputDir   { get; }
        public Process Process    { get; private set; }
        public double  SeekSec    { get; }
        public int     SegmentCount { get; }
        public string? VideoCodec { get; }
        // First audio stream's codec from the start probe — the seek-restart
        // has no probe and needs it for the TS passthrough decision.
        public string? AudioCodec { get; }
        // Source bit depth, likewise for the restart (Hi10P must not hw-decode).
        public bool    Is10Bit    { get; }
        public HlsPlan Plan       { get; }
        public double  AudioOffsetSec { get; }
        public double  TotalDurationSec { get; }
        // Which audio stream of the source we selected via `-map 0:a:{N}?`.
        // Set once at session creation; the session restart logic (used for
        // backward scrub jumps) needs to keep mapping the same audio track.
        public int     AudioTrackIndex { get; }
        // Output height cap (0 = native). Carried so the seek-restart re-runs
        // ffmpeg with the same downscale instead of reverting to source res.
        public int     MaxHeight  { get; }
        // Output bitrate cap in Mbps (0 = none). Carried like MaxHeight so a
        // seek-restart re-runs ffmpeg with the same cap.
        public int     MaxBitrate { get; }
        // External sideload audio file (null = use the source's own audio).
        // Carried so a backward-scrub restart keeps muxing the same dub track
        // instead of silently reverting to the source audio.
        public string? ExternalAudioPath { get; }
        public DateTime CreatedAt  { get; }
        public DateTime LastActive { get; private set; }

        // Segments at which a NEW ffmpeg process took over. Used by
        // RegenerateMediaPlaylist to insert EXT-X-DISCONTINUITY markers
        // so hls.js does a clean decoder reset instead of trying to
        // continue the previous run's pipeline through what's actually
        // a fresh encode (with potentially different SPS/PPS bytes).
        private readonly HashSet<int> _restartBoundaries = new();
        private readonly object _boundaryLock = new();

        public void AddRestartBoundary(int segmentIndex)
        {
            lock (_boundaryLock) { _restartBoundaries.Add(segmentIndex); }
        }
        public IReadOnlyCollection<int> GetRestartBoundaries()
        {
            lock (_boundaryLock) { return _restartBoundaries.ToArray(); }
        }

        // ── Look-ahead throttle + segment pruning (HlsSessionService.Throttle.cs) ──
        // All of these are read and written under ThrottleLock: segment requests,
        // the throttle tick and the seek-restart all touch them concurrently.

        /// <summary>Guards the throttle/prune fields below and the Process swap.</summary>
        public object ThrottleLock { get; } = new();

        /// <summary>First segment the CURRENT ffmpeg run writes: the start
        /// segment, or the target of the latest seek-restart. Segment numbers
        /// below it on disk belong to an earlier run.</summary>
        public int RunStartSegment;

        /// <summary>Highest segment of the current run known to be complete on
        /// disk, found by walking forward from <see cref="RunStartSegment"/>
        /// (<c>RunStartSegment - 1</c> = nothing yet). Contiguous on purpose: a
        /// plain "highest seg-N in the dir" also saw files a previous run left
        /// further ahead, so after a backward restart every not-yet-written
        /// segment looked "behind ffmpeg" and triggered another restart.</summary>
        public int ProducedTop;

        /// <summary>Furthest segment the player has requested since the current
        /// run began. Pruning keeps a window behind it; the throttle measures
        /// ffmpeg's lead against it.</summary>
        public int PlayFrontier;

        /// <summary>True while ffmpeg is SIGSTOPped by the throttle.</summary>
        public bool Paused;

        /// <summary>The prune floor of the last completed sweep, so the tick only
        /// lists the dir when the floor moved. Reset by a restart.</summary>
        public int LastPruneFloor = int.MinValue;

        public HlsSession(string token, string source, string dir, Process proc, double seekSec, int segCount,
            string? videoCodec, HlsPlan plan, double audioOffsetSec, double totalDurationSec,
            int audioTrackIndex, int maxHeight, string? externalAudioPath = null, int maxBitrate = 0,
            string? audioCodec = null, bool is10Bit = false, int startSegment = 0)
        {
            RunStartSegment = startSegment;
            ProducedTop     = startSegment - 1;
            PlayFrontier    = startSegment;
            Token       = token;
            SourcePath  = source;
            OutputDir   = dir;
            Process     = proc;
            SeekSec     = seekSec;
            SegmentCount = segCount;
            VideoCodec  = videoCodec;
            AudioCodec  = audioCodec;
            Is10Bit     = is10Bit;
            Plan        = plan;
            AudioOffsetSec = audioOffsetSec;
            TotalDurationSec = totalDurationSec;
            AudioTrackIndex  = audioTrackIndex;
            MaxHeight   = maxHeight;
            MaxBitrate  = maxBitrate;
            ExternalAudioPath = externalAudioPath;
            CreatedAt   = DateTime.UtcNow;
            LastActive  = DateTime.UtcNow;
        }

        /// <summary>Per-session restart serialiser — a backward seek kills and
        /// respawns this session's ffmpeg, and this stops overlapping seeks on
        /// the SAME session from racing. Disposed by <see cref="Stop"/>.</summary>
        public SemaphoreSlim RestartLock { get; } = new(1, 1);

        public void Touch() => LastActive = DateTime.UtcNow;

        /// <summary>Replace the ffmpeg process after a seek-restart. The old
        /// process must already be terminated; we dispose it here. The throttle
        /// state restarts with the new run (fresh process = not paused, nothing
        /// produced yet, player positioned at the restart target) — swapped under
        /// ThrottleLock so the throttle tick never pairs the new process with the
        /// old run's numbers.</summary>
        public void SwapProcess(Process newProc, int runStartSegment)
        {
            Process old;
            lock (ThrottleLock)
            {
                old = Process;
                Process         = newProc;
                RunStartSegment = runStartSegment;
                ProducedTop     = runStartSegment - 1;
                PlayFrontier    = runStartSegment;
                Paused          = false;
                LastPruneFloor  = int.MinValue;
            }
            try { old.Dispose(); } catch { }
        }
    }
}
