using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Animarr.Shared;
using Animarr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// "Optimize for TV" (plan Этап 7, offline jobs): an opt-in background remux —
/// no re-encode — of files a TV's player handles badly or not at all:
/// M2TS and WMV / ASF containers, and Matroska files without a Cues index
/// (Media3 can't seek them) or with zlib-compressed tracks (Media3 rejects
/// them). The file is rewritten IN PLACE as an MKV with Cues (<c>-c copy</c>,
/// every stream, in the same order — track indices stay valid): no copy is
/// kept, the disk isn't doubled. The new file replaces the original only once
/// it checks out (same duration, same streams); an .m2ts / .wmv becomes
/// .mkv next to where it was, and the title's episode mappings and trickplay
/// rows follow it.
///
/// Off unless the admin enables <see cref="AppConfigKeys.TvRemuxEnabled"/>.
/// Runs for a title when an admin starts it, and by itself for a finished
/// torrent's files (<see cref="TorrentEngineService"/> ends that torrent's
/// seeding first — the rewritten data no longer matches its pieces). One job
/// at a time, parked while anyone is watching (<see cref="PlaybackActivity"/>),
/// skipped when the volume can't hold the file twice for the switch
/// (+<see cref="FreeSpaceMarginBytes"/>).
/// </summary>
public sealed class TvRemuxService : BackgroundService
{
    /// <summary>Free space that must remain while the new file is written next
    /// to the original.</summary>
    public const long FreeSpaceMarginBytes = 512L * 1024 * 1024;

    public const string StatusNotNeeded = "not_needed";
    public const string StatusNeeded    = "needed";
    public const string StatusQueued    = "queued";
    public const string StatusRunning   = "running";
    public const string StatusDone      = "done";
    public const string StatusFailed    = "failed";
    public const string StatusSkipped   = "skipped";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".m2ts", ".mts", ".wmv", ".asf" };

    private readonly IServiceScopeFactory _scopes;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MediaProbeCache _probeCache;
    private readonly PlaybackActivity _activity;
    private readonly ILogger<TvRemuxService> _logger;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    /// <summary>Job state by path — the source's while queued / running, the
    /// result's (the .mkv) once done.</summary>
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public TvRemuxService(IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory,
        MediaProbeCache probeCache, PlaybackActivity activity, ILogger<TvRemuxService> logger)
    {
        _scopes     = scopes;
        _dbFactory  = dbFactory;
        _probeCache = probeCache;
        _activity   = activity;
        _logger     = logger;
    }

    private sealed class JobState
    {
        public string Status = StatusQueued;
        public string? Error;
    }

    /// <summary>One video of a title and its "optimize for TV" state.</summary>
    /// <param name="Reasons">Why a TV needs the remux: "container_m2ts",
    /// "container_wmv", "no_cues", "compressed_tracks"; empty = it doesn't.</param>
    /// <param name="Status">"not_needed", "needed", "queued", "running", "done"
    /// (rewritten this session), "failed", "skipped" (see <paramref name="Error"/>).</param>
    public sealed record FileStatus(string Path, IReadOnlyList<string> Reasons, string Status, string? Error);

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
        return string.Equals(await cfg.GetAsync(AppConfigKeys.TvRemuxEnabled, ct), "true", StringComparison.OrdinalIgnoreCase);
    }

    // ─── What a title needs ───────────────────────────────────────────────────

    /// <summary>Every video of the title with its reasons and state; null for
    /// an unknown title.</summary>
    public async Task<IReadOnlyList<FileStatus>?> StatusAsync(Guid mediaItemId, CancellationToken ct)
    {
        var files = await FilesAsync(mediaItemId, ct);
        if (files is null) return null;
        var result = new List<FileStatus>(files.Count);
        foreach (var path in files)
            result.Add(await DescribeAsync(path, ct));
        return result;
    }

    /// <summary>Queue every file of the title that needs the remux; the updated
    /// states. Null for an unknown title.</summary>
    public async Task<IReadOnlyList<FileStatus>?> QueueAsync(Guid mediaItemId, CancellationToken ct)
    {
        var files = await FilesAsync(mediaItemId, ct);
        if (files is null) return null;
        var result = new List<FileStatus>(files.Count);
        foreach (var path in files)
        {
            var s = await DescribeAsync(path, ct);
            if (s.Status is StatusNeeded or StatusFailed or StatusSkipped)
            {
                await EnqueueAsync(path, ct);
                s = s with { Status = StatusQueued, Error = null };
            }
            result.Add(s);
        }
        return result;
    }

    /// <summary>Of <paramref name="paths"/>, the videos a TV needs remuxed — what
    /// a finished torrent would hand over.</summary>
    public async Task<List<string>> NeedingRemuxAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var needed = new List<string>();
        foreach (var p in paths)
            if (VideoExtensions.Contains(Path.GetExtension(p)) && File.Exists(p) && (await ReasonsAsync(p, ct)).Count > 0)
                needed.Add(p);
        return needed;
    }

    /// <summary>Queue these files (a finished torrent's, its seeding already
    /// ended) unless one is queued or running already.</summary>
    public async Task QueueFilesAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        foreach (var p in paths)
            if (!(_jobs.TryGetValue(p, out var j) && j.Status is StatusQueued or StatusRunning))
                await EnqueueAsync(p, ct);
    }

    private ValueTask EnqueueAsync(string path, CancellationToken ct)
    {
        _jobs[path] = new JobState();
        return _queue.Writer.WriteAsync(path, ct);
    }

    private async Task<FileStatus> DescribeAsync(string path, CancellationToken ct)
    {
        var reasons = await ReasonsAsync(path, ct);
        if (_jobs.TryGetValue(path, out var job))
            return new FileStatus(path, reasons, job.Status, job.Error);
        return new FileStatus(path, reasons, reasons.Count > 0 ? StatusNeeded : StatusNotNeeded, null);
    }

    /// <summary>Why a TV needs this file remuxed (empty: it doesn't).</summary>
    public async Task<IReadOnlyList<string>> ReasonsAsync(string path, CancellationToken ct)
    {
        var reasons = new List<string>();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        string? format = null;
        var json = await _probeCache.GetStreamsJsonAsync(path, ct);
        if (json is not null)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("format_name", out var fn))
                    format = fn.GetString();
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (ext is ".m2ts" or ".mts") reasons.Add("container_m2ts");
        else if (ext is ".wmv" or ".asf" || format == "asf") reasons.Add("container_wmv");
        else if (ext is ".mkv" || format?.StartsWith("matroska", StringComparison.Ordinal) == true)
        {
            if (MatroskaInspector.Inspect(path) is { } mkv)
            {
                if (!mkv.HasCues) reasons.Add("no_cues");
                if (mkv.CompressedTracks) reasons.Add("compressed_tracks");
            }
        }
        return reasons;
    }

    /// <summary>The title's videos.</summary>
    private async Task<List<string>?> FilesAsync(Guid mediaItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!await db.MediaItems.AnyAsync(m => m.Id == mediaItemId, ct)) return null;
        using var scope = _scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<MediaFileResolver>();
        return (await resolver.ResolveAsync(mediaItemId, ct)).Select(f => f.FilePath).ToList();
    }

    // ─── The job ──────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var source in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_jobs.TryGetValue(source, out var state) || state.Status != StatusQueued) continue;
            try
            {
                await _activity.WaitForIdleAsync(stoppingToken);
                await RunAsync(source, state, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                state.Status = StatusFailed;
                state.Error = ex.Message;
                _logger.LogWarning(ex, "TV remux failed for {Path}", source);
            }
        }
    }

    private async Task RunAsync(string source, JobState state, CancellationToken ct)
    {
        if (!File.Exists(source))
        {
            (state.Status, state.Error) = (StatusFailed, "the file is gone");
            return;
        }
        if ((await ReasonsAsync(source, ct)).Count == 0) { state.Status = StatusDone; return; }

        bool matroskaSource = Path.GetExtension(source).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        var final = matroskaSource ? source : Path.ChangeExtension(source, ".mkv");
        if (!matroskaSource && File.Exists(final))
        {
            (state.Status, state.Error) = (StatusSkipped, $"{Path.GetFileName(final)} already exists next to it");
            return;
        }

        var dir = Path.GetDirectoryName(source)!;
        var sourceSize = new FileInfo(source).Length;
        long? free = FreeBytes(dir);
        if (free is null || free.Value - sourceSize < FreeSpaceMarginBytes)
        {
            state.Status = StatusSkipped;
            state.Error = free is null
                ? "free space on the volume could not be determined"
                : $"not enough free space: {free.Value / 1048576} MB free, {sourceSize / 1048576} MB needed + {FreeSpaceMarginBytes / 1048576} MB margin";
            _logger.LogInformation("TV remux skipped for {Path}: {Why}", source, state.Error);
            return;
        }

        state.Status = StatusRunning;
        // Next to the source: the switch is a rename on the same volume.
        var tmp = Path.Combine(dir, "." + Path.GetFileNameWithoutExtension(source) + "." + Guid.NewGuid().ToString("N")[..8] + ".animarr-tmp.mkv");
        var started = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        // M2TS / ASF may carry packets without timestamps; Matroska doesn't.
        if (!matroskaSource) args.AddRange(["-fflags", "+genpts"]);
        args.AddRange(["-i", source,
            // Every stream (attachments = ASS fonts included), same order, no
            // re-encode; data streams have no Matroska mapping.
            "-map", "0", "-dn", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0",
            // Cues up front: a TV seeks without reading to the end first.
            "-cues_to_front", "1", "-f", "matroska", tmp]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using (var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start"))
            {
                var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
                try { await p.WaitForExitAsync(ct); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(true); } catch { }
                    throw;
                }
                var err = (await stderr).Trim();
                if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                {
                    state.Status = StatusFailed;
                    state.Error = err.Length > 500 ? err[^500..] : (err.Length > 0 ? err : $"ffmpeg exit {p.ExitCode}");
                    _logger.LogWarning("TV remux of {Path} failed (exit {Code}): {Err}", source, p.ExitCode, state.Error);
                    return;
                }
            }

            // The original goes only for a file that checks out.
            if (await MismatchAsync(source, tmp, ct) is { } why)
            {
                (state.Status, state.Error) = (StatusFailed, "the remuxed file doesn't match the original: " + why);
                _logger.LogWarning("TV remux of {Path} discarded: {Why}", source, why);
                return;
            }

            File.Move(tmp, final, overwrite: matroskaSource);
            if (!matroskaSource) File.Delete(source);
            await FollowRewriteAsync(source, final, ct);
            _jobs.TryRemove(source, out _);
            _jobs[final] = state;
            state.Status = StatusDone;
            state.Error = null;
            _logger.LogInformation("TV remux of {Path} → {Final} in place in {Sec:F1} s", source, final, started.Elapsed.TotalSeconds);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Why <paramref name="remuxed"/> can't replace <paramref name="source"/>
    /// (null: it can) — the same streams by type and the same duration
    /// (±max(1 s, 0.5 %)).</summary>
    private async Task<string?> MismatchAsync(string source, string remuxed, CancellationToken ct)
    {
        var a = Summarize(await _probeCache.GetStreamsJsonAsync(source, ct));
        var b = Summarize(await _probeCache.GetStreamsJsonAsync(remuxed, ct));
        if (a is null || b is null) return "it could not be probed";
        foreach (var kind in a.Value.Streams.Keys.Union(b.Value.Streams.Keys))
        {
            a.Value.Streams.TryGetValue(kind, out var na);
            b.Value.Streams.TryGetValue(kind, out var nb);
            if (na != nb) return $"{kind} streams {na} → {nb}";
        }
        if (a.Value.Duration > 0 && Math.Abs(a.Value.Duration - b.Value.Duration) > Math.Max(1.0, a.Value.Duration * 0.005))
            return $"duration {a.Value.Duration:F1} s → {b.Value.Duration:F1} s";
        return null;

        static (Dictionary<string, int> Streams, double Duration)? Summarize(string? json)
        {
            if (json is null) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var streams = new Dictionary<string, int>(StringComparer.Ordinal);
                if (doc.RootElement.TryGetProperty("streams", out var ss))
                    foreach (var s in ss.EnumerateArray())
                    {
                        var kind = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "?" : "?";
                        // -dn drops data streams on purpose.
                        if (kind == "data") continue;
                        streams[kind] = streams.GetValueOrDefault(kind) + 1;
                    }
                double duration = 0;
                if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
                    double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                return (streams, duration);
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }

    /// <summary>The file was rewritten (an .m2ts / .wmv became .mkv, or an .mkv
    /// in place): the title's episode mappings and trickplay rows (keyed by
    /// path) follow it. Same frames — the sprite's source time follows too, so
    /// it isn't regenerated.</summary>
    private async Task FollowRewriteAsync(string oldPath, string newPath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
            await db.EpisodeFileMappings.Where(m => m.FilePath == oldPath)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.FilePath, newPath), ct);
        var written = File.GetLastWriteTimeUtc(newPath);
        await db.TrickplayAssets.Where(t => t.FilePath == oldPath)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.FilePath, newPath)
                .SetProperty(t => t.SourceWriteTimeUtc, written), ct);
    }

    private static long? FreeBytes(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            // On Unix DriveInfo takes any path of the mount; on Windows the root.
            return new DriveInfo(OperatingSystem.IsWindows() ? root! : dir).AvailableFreeSpace;
        }
        catch { return null; }
    }
}
