using System.Collections.Concurrent;

namespace Animarr.Web.Services;

/// <summary>
/// One ffprobe per file version, shared by everything on the playback path.
///
/// Before: a single web playback start ran the identical
/// <c>ffprobe -show_format -show_streams</c> three times (HLS start's
/// ChoosePlaybackAsync, StartAsync again, and the player's parallel
/// /api/probe), each followed by a packet probe for the B-frame reorder delay
/// that only the fMP4 stream-copy plan uses; and every Direct-Stream
/// <c>/api/video?seek=</c> reload spawned another ffprobe just to re-learn the
/// video codec. Each spawn is ~40–100 ms warm and 150–400 ms on a cold HDD.
///
/// Keyed by (probe kind, full path, mtime, size) so a replaced or still-growing
/// file (torrent in progress) is simply a new key — entries never go stale, they
/// only age out of the LRU. Concurrent callers share the in-flight probe (the
/// player's /api/probe races /api/hls/start). The probe itself runs on its own
/// timeout, not a caller's token: one caller giving up must not cancel the probe
/// another is awaiting. Failures are not cached — the next call retries.
/// </summary>
public sealed class MediaProbeCache
{
    /// <summary>~5–30 KB of JSON per file → a few MB at most.</summary>
    private const int MaxEntries = 512;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] StreamsArgs =
        ["-v", "error", "-print_format", "json", "-show_format", "-show_streams"];

    // First ~48 video packets (a handful of GOPs) from the file start — enough
    // to see the steady-state B-pyramid depth, cheap to read. Selects the main
    // video stream the HLS plans map (by index), never a cover picture.
    private static string[] ReorderPacketsArgs(string streamSelector) =>
        ["-v", "error", "-select_streams", streamSelector, "-read_intervals", "%+#48",
         "-show_entries", "packet=pts_time,dts_time", "-of", "csv=p=0"];

    private readonly ILogger<MediaProbeCache> _logger;
    private readonly ConcurrentDictionary<Key, Slot> _slots = new();
    private long _clock;
    private int _evicting;

    private readonly record struct Key(string Kind, string Path, long MtimeTicks, long Length);

    private sealed class Slot
    {
        public Lazy<Task<string?>> Run = null!;
        public long LastUse;
    }

    public MediaProbeCache(ILogger<MediaProbeCache> logger) => _logger = logger;

    /// <summary><c>ffprobe -show_format -show_streams</c> JSON — exactly what
    /// /api/probe returns and what HLS start / /api/video parse. null when
    /// ffprobe failed.</summary>
    public Task<string?> GetStreamsJsonAsync(string fullPath, CancellationToken ct = default)
        => GetAsync("streams", StreamsArgs, fullPath, ct);

    /// <summary><c>ffprobe -show_chapters</c> JSON (container metadata only —
    /// no decoding); null when ffprobe failed. Feeds <c>/api/chapters</c>.</summary>
    public Task<string?> GetChaptersJsonAsync(string fullPath, CancellationToken ct = default)
        => GetAsync("chapters", ChaptersArgs, fullPath, ct);

    private static readonly string[] ChaptersArgs =
        ["-v", "error", "-print_format", "json", "-show_chapters"];

    /// <summary>pts,dts CSV of the first video packets — input for the fMP4
    /// stream-copy plan's reorder-delay measurement. null when ffprobe failed.
    /// <paramref name="videoStreamIndex"/> = absolute index of the main video
    /// (-1 = "V:0", the first video that isn't an attached picture).</summary>
    public Task<string?> GetReorderPacketsCsvAsync(string fullPath, int videoStreamIndex = -1, CancellationToken ct = default)
    {
        var selector = videoStreamIndex >= 0
            ? videoStreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "V:0";
        return GetAsync("reorder:" + selector, ReorderPacketsArgs(selector), fullPath, ct);
    }

    private Task<string?> GetAsync(string kind, string[] args, string fullPath, CancellationToken ct)
    {
        FileInfo? fi = null;
        try { fi = new FileInfo(fullPath); } catch { }
        // Not there (or vanished): nothing to key on — run uncached, ffprobe
        // reports the failure as before.
        if (fi is not { Exists: true }) return RunAsync(kind, args, fullPath).WaitAsync(ct);

        var key = new Key(kind, fullPath, fi.LastWriteTimeUtc.Ticks, fi.Length);
        // GetOrAdd may build a losing Slot under a race; its Lazy is never
        // evaluated, so only one ffprobe runs per key.
        var slot = _slots.GetOrAdd(key, k =>
        {
            var s = new Slot();
            s.Run = new Lazy<Task<string?>>(() => StartAsync(k, args, s));
            return s;
        });
        slot.LastUse = Interlocked.Increment(ref _clock);
        var task = slot.Run.Value;
        if (_slots.Count > MaxEntries) Evict();
        return task.WaitAsync(ct);
    }

    private async Task<string?> StartAsync(Key key, string[] args, Slot slot)
    {
        var result = await RunAsync(key.Kind, args, key.Path);
        // Drop failures so the next request retries instead of replaying them
        // (only this slot — a newer one may already sit under the key).
        if (result is null)
            _slots.TryRemove(new KeyValuePair<Key, Slot>(key, slot));
        return result;
    }

    private async Task<string?> RunAsync(string kind, string[] args, string fullPath)
    {
        try
        {
            var r = await FfprobeRunner.RunAsync([.. args, fullPath], ProbeTimeout, CancellationToken.None);
            if (r.ExitCode == 0) return r.StdOut;
            _logger.LogWarning("ffprobe ({Kind}) exit {Code} for {Path}: {Err}", kind, r.ExitCode, fullPath, r.StdErr.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe ({Kind}) failed for {Path}", kind, fullPath);
        }
        return null;
    }

    /// <summary>Drop the least-recently-used eighth once over capacity.</summary>
    private void Evict()
    {
        if (Interlocked.Exchange(ref _evicting, 1) == 1) return;
        try
        {
            var excess = _slots.Count - MaxEntries + MaxEntries / 8;
            if (excess <= 0) return;
            foreach (var kv in _slots.ToArray().OrderBy(kv => Volatile.Read(ref kv.Value.LastUse)).Take(excess))
                _slots.TryRemove(kv.Key, out _);
        }
        finally { Volatile.Write(ref _evicting, 0); }
    }
}
