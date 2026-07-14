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

        public HlsSession(string token, string source, string dir, Process proc, double seekSec, int segCount,
            string? videoCodec, HlsPlan plan, double audioOffsetSec, double totalDurationSec,
            int audioTrackIndex, int maxHeight, string? externalAudioPath = null, int maxBitrate = 0)
        {
            Token       = token;
            SourcePath  = source;
            OutputDir   = dir;
            Process     = proc;
            SeekSec     = seekSec;
            SegmentCount = segCount;
            VideoCodec  = videoCodec;
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
        /// process must already be terminated; we dispose it here.</summary>
        public void SwapProcess(Process newProc)
        {
            var old = Process;
            Process = newProc;
            try { old.Dispose(); } catch { }
        }
    }
}
