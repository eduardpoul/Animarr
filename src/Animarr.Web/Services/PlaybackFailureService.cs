using System.Collections.Concurrent;
using System.Text.Json;

namespace Animarr.Web.Services;

/// <summary>
/// Decoder failures clients report (POST /api/playback/failure, task 6.1),
/// remembered per DEVICE so the next start for the same kind of stream steps
/// to the next plan instead of failing the same way again
/// (<c>transcodeReasonCode: device_failed_before</c>):
/// <list type="bullet">
///   <item>video — keyed by the source's decode signature (codec, profile,
///   level, bit depth, HDR / DV; <see cref="HlsSessionService.DecodeSignatureAsync"/>)
///   and the plan that failed: Direct Play → Direct Stream / HLS copy →
///   re-encode, and an HEVC re-encode → H.264;</item>
///   <item>audio — keyed by the codec the device failed to decode: that codec
///   is then treated as not decodable by the device (transcoded), on every
///   file.</item>
/// </list>
/// A device is the HLS session owner's client id (the sealed device cookie,
/// else an address + User-Agent hash). Entries expire after
/// <see cref="Expiry"/> — a failure can be transient, a firmware update can
/// fix a decoder — and a client can clear its own (DELETE).
/// Persisted next to the database (playback-failures.json): "next time"
/// includes after a restart.
/// </summary>
public sealed class PlaybackFailureService
{
    public const string TrackVideo = "video", TrackAudio = "audio";

    /// <summary>How long a reported failure keeps steering this device.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(30);

    private const int MaxEntries = 2000;

    /// <summary>One remembered failure.</summary>
    /// <param name="Key">"v:{signature}" (video) or "a:{codec}" (audio).</param>
    /// <param name="Plan">The plan the device failed on ("directplay",
    /// "directstream", "ts-copy", "fmp4-copy", "nvenc-reencode" …).</param>
    public sealed record Entry(string Device, string Key, string Plan, string Track, string? VideoCodec,
        string? AudioCodec, string? Error, int Count, DateTime FirstAt, DateTime LastAt);

    /// <summary>What a start must avoid for this device and stream.</summary>
    public sealed record Steps(bool NoDirectPlay, bool NoDirectStream, bool NoCopy, bool NoHevcOutput,
        IReadOnlySet<string> TranscodeAudio)
    {
        public static readonly Steps None = new(false, false, false, false, new HashSet<string>());
        public bool Any => NoDirectPlay || NoDirectStream || NoCopy || NoHevcOutput || TranscodeAudio.Count > 0;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ILogger<PlaybackFailureService> _logger;
    private readonly string? _file;
    private readonly object _saveLock = new();

    public PlaybackFailureService(ILogger<PlaybackFailureService> logger, MediaCachePaths? paths = null)
    {
        _logger = logger;
        if (paths is not null)
        {
            var dataDir = Path.GetDirectoryName(paths.CacheRoot);
            if (!string.IsNullOrEmpty(dataDir)) _file = Path.Combine(dataDir, "playback-failures.json");
        }
        Load();
    }

    /// <summary>Plans a video failure can be reported for.</summary>
    public static bool IsKnownPlan(string? plan) => plan is "directplay" or "directstream" or "ts-copy" or "fmp4-copy"
        or "vaapi-reencode" or "nvenc-reencode" or "sw-reencode";

    /// <summary>The audio codec a failure names, normalized ("eac3_joc" → "eac3"); null = not one we know.</summary>
    public static string? NormalizeAudioCodec(string? codec)
    {
        var c = (codec ?? "").Trim().ToLowerInvariant();
        c = c switch { "mp4a" => "aac", "eac3_joc" or "ec-3" => "eac3", "ac-3" => "ac3", "mp2" => "mp3", _ => c };
        if (c.StartsWith("pcm", StringComparison.Ordinal)) return "pcm";
        return c is "aac" or "mp3" or "ac3" or "eac3" or "dts" or "truehd" or "opus" or "vorbis" or "flac" ? c : null;
    }

    public Entry Record(string device, string key, string plan, string track, string? videoCodec, string? audioCodec,
        string? error)
    {
        var now = DateTime.UtcNow;
        error = error is { Length: > 300 } ? error[..300] : error;
        var id = Id(device, key, plan);
        var entry = _entries.AddOrUpdate(id,
            _ => new Entry(device, key, plan, track, videoCodec, audioCodec, error, 1, now, now),
            (_, old) => old with { VideoCodec = videoCodec, AudioCodec = audioCodec, Error = error,
                                   Count = old.Count + 1, LastAt = now });
        _logger.LogInformation("Playback failure #{Count} on device {Device}: {Key} plan={Plan} ({Track}; {Video}/{Audio}) — {Error}",
            entry.Count, device, key, plan, track, videoCodec, audioCodec, error);
        Trim();
        Save();
        return entry;
    }

    /// <summary>The live entries of <paramref name="device"/> for these keys.</summary>
    public IReadOnlyList<Entry> For(string device, IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTime.UtcNow - Expiry;
        return _entries.Values.Where(e => e.Device == device && e.LastAt >= cutoff && set.Contains(e.Key))
            .OrderBy(e => e.FirstAt).ToList();
    }

    /// <summary>Every live entry of <paramref name="device"/>, oldest first.</summary>
    public IReadOnlyList<Entry> ForDevice(string device)
    {
        var cutoff = DateTime.UtcNow - Expiry;
        return _entries.Values.Where(e => e.Device == device && e.LastAt >= cutoff).OrderBy(e => e.FirstAt).ToList();
    }

    /// <summary>What to avoid for a stream with <paramref name="videoKey"/> on
    /// <paramref name="device"/>: the video steps for that signature, and
    /// every audio codec the device failed on.</summary>
    public Steps StepsFor(string device, string? videoKey)
    {
        var cutoff = DateTime.UtcNow - Expiry;
        bool dp = false, ds = false, copy = false, hevc = false;
        var audio = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _entries.Values)
        {
            if (e.Device != device || e.LastAt < cutoff) continue;
            if (e.Track == TrackAudio && e.Key.StartsWith("a:", StringComparison.Ordinal))
            {
                audio.Add(e.Key[2..]);
                continue;
            }
            if (videoKey is null || e.Key != videoKey) continue;
            switch (e.Plan)
            {
                case "directplay": dp = true; break;
                case "directstream": ds = true; break;
                case "ts-copy" or "fmp4-copy": copy = true; break;
                default:
                    if (string.Equals(e.VideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)) hevc = true;
                    break;
            }
        }
        return dp || ds || copy || hevc || audio.Count > 0 ? new Steps(dp, ds, copy, hevc, audio) : Steps.None;
    }

    /// <summary>Forgets <paramref name="device"/>'s failures (for these keys
    /// only, when given). Returns how many were removed.</summary>
    public int Clear(string device, IReadOnlyCollection<string>? keys = null)
    {
        int n = 0;
        foreach (var (id, e) in _entries)
            if (e.Device == device && (keys is null || keys.Contains(e.Key)) && _entries.TryRemove(id, out _)) n++;
        if (n > 0) Save();
        return n;
    }

    private static string Id(string device, string key, string plan) => device + "\n" + key + "\n" + plan;

    private void Trim()
    {
        var cutoff = DateTime.UtcNow - Expiry;
        foreach (var (id, e) in _entries)
            if (e.LastAt < cutoff) _entries.TryRemove(id, out _);
        if (_entries.Count <= MaxEntries) return;
        foreach (var (id, _) in _entries.OrderBy(kv => kv.Value.LastAt).Take(_entries.Count - MaxEntries).ToList())
            _entries.TryRemove(id, out _);
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file)) ?? [];
            var cutoff = DateTime.UtcNow - Expiry;
            foreach (var e in list.Where(e => e.LastAt >= cutoff && e.Device is { Length: > 0 } && e.Key is { Length: > 0 }))
                _entries[Id(e.Device, e.Key, e.Plan)] = e;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Playback failures: reading {File} failed — starting empty", _file); }
    }

    private void Save()
    {
        if (_file is null) return;
        lock (_saveLock)
        {
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.FirstAt).ToList()));
                File.Move(tmp, _file, overwrite: true);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Playback failures: writing {File} failed", _file); }
        }
    }
}
