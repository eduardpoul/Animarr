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
        /// <summary>Where each announced seg-N starts — the seek-restart seeks
        /// ffmpeg to <c>Layout.Start(N)</c>, and playlist rewrites reuse it.</summary>
        public SegmentLayout Layout { get; }
        public int     SegmentCount => Layout.Count;
        /// <summary>The start's probe (audio fields = the mapped track's, see
        /// ProbeInfo.ForAudioTrack). The seek-restart builds its ffmpeg run from
        /// it, so it gets the same source height / bitrate / codec facts as the
        /// first run — its old hand-made minimal probe had no height, and the
        /// software re-encode fell back to 12 Mbps after every scrub.</summary>
        public ProbeInfo? Probe { get; init; }
        /// <summary>What the start decided for the audio (AudioPolicy) — a
        /// seek-restart must keep it, or a scrub would switch codec mid-stream
        /// under the CODECS the playlist advertised.</summary>
        public AudioDecision? Audio { get; init; }
        /// <summary>Dolby Vision treatment of the fMP4 copy (tag / RPU strip),
        /// kept for the restart like the audio decision.</summary>
        public DoviHandling Dovi { get; init; }
        /// <summary>The re-encode's output format (H.264 / HEVC Main10).</summary>
        public VideoTarget Target { get; init; }
        /// <summary>Subtitle burned into the picture (null = none) — the restart
        /// draws it too.</summary>
        public SubtitleBurn? Burn { get; init; }

        // ── Key-frame-exact copy (HlsSessionService.Keyframes.cs) ──
        /// <summary>The copy runs use the segment muxer on Layout.Cuts.</summary>
        public bool KeyframeMode { get; init; }
        /// <summary>Guards the finalize pass (raw-N → seg-N) and its list state.</summary>
        public object FinalizeLock { get; } = new();
        /// <summary>The current run's CSV segment list.</summary>
        public string? KeyframeListPath;
        /// <summary>Bytes of that list already finalized.</summary>
        public long KeyframeListRead;
        /// <summary>First segment past the current run's -segment_times window
        /// (SegmentCount = the whole rest of the file).</summary>
        public int RunWindowEnd = int.MaxValue;
        /// <summary>Runs started so far (names each run's list).</summary>
        public int RunCount;

        /// <summary>Set by Stop before anything is killed: a rendition run
        /// being (re)started concurrently — a request, the prefetch — must not
        /// launch an ffmpeg (or recreate the dir) for a session that is gone.</summary>
        public volatile bool Stopped;

        // ── Audio renditions (HlsSessionService.Renditions.cs) ──
        /// <summary>The video runs carry no audio: every audio option is its
        /// own rendition with its own ffmpeg runs.</summary>
        public bool VideoOnly { get; init; }
        /// <summary>The session's audio renditions (null = audio muxed in).</summary>
        public List<AudioRendition>? Renditions { get; init; }
        public string? VideoCodec { get; }
        // Codec of the MAPPED audio track (ProbeInfo.ForAudioTrack) from the
        // start probe — the seek-restart has no probe and needs it for the TS
        // passthrough decision.
        public string? AudioCodec { get; }
        // Source bit depth, likewise for the restart (Hi10P must not hw-decode).
        public bool    Is10Bit    { get; }
        // Absolute index of the probed main video stream (-1 = unknown) — the
        // restart maps the same stream, never a cover picture in front of it.
        public int     VideoStreamIndex { get; init; } = -1;
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
        /// <summary>HDR → SDR mapper chosen at start — a seek-restart only has a
        /// minimal probe (no colour transfer), so it reuses this.</summary>
        public HdrToneMapper ToneMapper { get; }
        /// <summary>Account + client device that started the session — the
        /// dedup / eviction key next to the source path.</summary>
        public SessionOwner Owner { get; }
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

        public HlsSession(string token, string source, string dir, Process proc, double seekSec, SegmentLayout layout,
            string? videoCodec, HlsPlan plan, double audioOffsetSec, double totalDurationSec,
            int audioTrackIndex, int maxHeight, string? externalAudioPath = null, int maxBitrate = 0,
            string? audioCodec = null, bool is10Bit = false, int startSegment = 0,
            HdrToneMapper toneMapper = HdrToneMapper.None, SessionOwner owner = default)
        {
            Owner           = owner;
            ToneMapper      = toneMapper;
            RunStartSegment = startSegment;
            ProducedTop     = startSegment - 1;
            PlayFrontier    = startSegment;
            Token       = token;
            SourcePath  = source;
            OutputDir   = dir;
            Process     = proc;
            SeekSec     = seekSec;
            Layout      = layout;
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
