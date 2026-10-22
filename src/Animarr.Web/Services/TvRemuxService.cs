using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
/// them). The result is an MKV with Cues (<c>-c copy</c>, every stream, in
/// the same order — track indices stay valid) stored next to the media in the
/// title's asset dir <c>.animarr/&lt;folderId&gt;/tv/</c>, like trickplay and
/// theme music. Direct Play serves it in place of the source while it is
/// newer than the source (<see cref="FreshCopyOfAsync"/>).
///
/// Off unless the admin enables <see cref="AppConfigKeys.TvRemuxEnabled"/>;
/// started per title by an admin. One job at a time, parked while anyone is
/// watching (<see cref="PlaybackActivity"/>), and skipped when the volume
/// would keep less than <see cref="FreeSpaceMarginBytes"/> free.
/// </summary>
public sealed class TvRemuxService : BackgroundService
{
    /// <summary>Free space that must remain after the copy is written.</summary>
    public const long FreeSpaceMarginBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan FolderListTtl = TimeSpan.FromSeconds(30);

    public const string StatusNotNeeded = "not_needed";
    public const string StatusNeeded    = "needed";
    public const string StatusQueued    = "queued";
    public const string StatusRunning   = "running";
    public const string StatusDone      = "done";
    public const string StatusFailed    = "failed";
    public const string StatusSkipped   = "skipped";

    private readonly IServiceScopeFactory _scopes;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MediaProbeCache _probeCache;
    private readonly PlaybackActivity _activity;
    private readonly ILogger<TvRemuxService> _logger;
    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime Until, List<ExternalTrackService.OwnerCandidate> Folders) _folders = (DateTime.MinValue, []);

    public TvRemuxService(IServiceScopeFactory scopes, IDbContextFactory<AppDbContext> dbFactory,
        MediaProbeCache probeCache, PlaybackActivity activity, ILogger<TvRemuxService> logger)
    {
        _scopes     = scopes;
        _dbFactory  = dbFactory;
        _probeCache = probeCache;
        _activity   = activity;
        _logger     = logger;
    }

    private sealed record Job(string Source, string Target);

    private sealed class JobState
    {
        public string Status = StatusQueued;
        public string? Error;
    }

    /// <summary>One video of a title and its "optimize for TV" state.</summary>
    /// <param name="Reasons">Why a TV needs the remux: "container_m2ts",
    /// "container_wmv", "no_cues", "compressed_tracks"; empty = it doesn't.</param>
    /// <param name="Status">"not_needed", "needed", "queued", "running", "done"
    /// (a fresh copy exists), "failed", "skipped" (see <paramref name="Error"/>).</param>
    public sealed record FileStatus(string Path, IReadOnlyList<string> Reasons, string Status,
        string? CopyPath, long? CopySizeBytes, string? Error);

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
        foreach (var (source, target) in files)
            result.Add(await DescribeAsync(source, target, ct));
        return result;
    }

    /// <summary>Queue every file of the title that needs the remux and has no
    /// fresh copy yet; the updated states. Null for an unknown title.</summary>
    public async Task<IReadOnlyList<FileStatus>?> QueueAsync(Guid mediaItemId, CancellationToken ct)
    {
        var files = await FilesAsync(mediaItemId, ct);
        if (files is null) return null;
        var result = new List<FileStatus>(files.Count);
        foreach (var (source, target) in files)
        {
            var s = await DescribeAsync(source, target, ct);
            if (s.Status is StatusNeeded or StatusFailed or StatusSkipped)
            {
                _jobs[source] = new JobState();
                await _queue.Writer.WriteAsync(new Job(source, target), ct);
                s = s with { Status = StatusQueued, Error = null };
            }
            result.Add(s);
        }
        return result;
    }

    /// <summary>Delete the title's copies; how many were removed.</summary>
    public async Task<int> DeleteCopiesAsync(Guid mediaItemId, CancellationToken ct)
    {
        var files = await FilesAsync(mediaItemId, ct);
        if (files is null) return 0;
        int n = 0;
        foreach (var (source, target) in files)
        {
            if (_jobs.TryGetValue(source, out var j) && j.Status is StatusQueued or StatusRunning) continue;
            try
            {
                if (File.Exists(target)) { File.Delete(target); n++; }
            }
            catch (IOException) { /* in use — left for a later delete */ }
            _jobs.TryRemove(source, out _);
        }
        return n;
    }

    /// <summary>The fresh TV copy of <paramref name="sourcePath"/> (exists, not
    /// empty, newer than the source) — what Direct Play serves instead; null
    /// when there is none or the feature is off.</summary>
    public async Task<string?> FreshCopyOfAsync(string sourcePath, CancellationToken ct)
    {
        if (!await IsEnabledAsync(ct)) return null;
        var target = TargetFor(await OwnerFolderAsync(sourcePath, ct), sourcePath);
        return target is not null && IsFresh(sourcePath, target) ? target : null;
    }

    private async Task<FileStatus> DescribeAsync(string source, string target, CancellationToken ct)
    {
        var reasons = await ReasonsAsync(source, ct);
        bool fresh = IsFresh(source, target);
        string status;
        string? error = null;
        if (_jobs.TryGetValue(source, out var job) && !(job.Status == StatusDone && !fresh))
            (status, error) = (job.Status, job.Error);
        else
            status = fresh ? StatusDone : reasons.Count > 0 ? StatusNeeded : StatusNotNeeded;
        long? size = fresh ? new FileInfo(target).Length : null;
        return new FileStatus(source, reasons, status, fresh ? target : null, size, error);
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

    private static bool IsFresh(string source, string target)
    {
        try
        {
            var t = new FileInfo(target);
            return t.Exists && t.Length > 0 && t.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(source);
        }
        catch { return false; }
    }

    // ─── Paths ────────────────────────────────────────────────────────────────

    /// <summary>The title's videos with the path of their TV copy.</summary>
    private async Task<List<(string Source, string Target)>?> FilesAsync(Guid mediaItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var folder = await db.MediaItems.AsNoTracking()
            .Where(m => m.Id == mediaItemId)
            .Select(m => new ExternalTrackService.OwnerCandidate(m.Folder.Id, m.Folder.Path, m.Folder.IsSection, m.Folder.SingleFilePath))
            .FirstOrDefaultAsync(ct);
        if (folder is null) return null;

        using var scope = _scopes.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<MediaFileResolver>();
        var list = new List<(string, string)>();
        foreach (var f in await resolver.ResolveAsync(mediaItemId, ct))
            if (TargetFor(folder, f.FilePath) is { } target)
                list.Add((f.FilePath, target));
        return list;
    }

    private async Task<ExternalTrackService.OwnerCandidate?> OwnerFolderAsync(string sourcePath, CancellationToken ct)
    {
        var (until, folders) = _folders;
        if (until < DateTime.UtcNow)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            folders = await db.FolderWatchers.AsNoTracking()
                .Where(f => f.Path != null && f.Path != "")
                .OrderBy(f => f.Id)
                .Select(f => new ExternalTrackService.OwnerCandidate(f.Id, f.Path, f.IsSection, f.SingleFilePath))
                .ToListAsync(ct);
            _folders = (DateTime.UtcNow + FolderListTtl, folders);
        }
        return ExternalTrackService.PickOwner(folders, Path.GetFullPath(sourcePath));
    }

    /// <summary><c>&lt;base&gt;/.animarr/&lt;folderId:N&gt;/tv/&lt;stem&gt;.&lt;hash&gt;.mkv</c>
    /// — the asset dir trickplay and theme music use; the hash of the source
    /// path keeps two files with one name (different seasons) apart.</summary>
    private static string? TargetFor(ExternalTrackService.OwnerCandidate? folder, string source)
    {
        if (folder is null) return null;
        var baseDir = folder.SingleFilePath is { Length: > 0 } single ? Path.GetDirectoryName(single) : folder.Path;
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToLowerInvariant())))[..8].ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(source);
        return Path.Combine(baseDir, ".animarr", folder.Id.ToString("N"), "tv", $"{stem}.{hash}.mkv");
    }

    // ─── The job ──────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_jobs.TryGetValue(job.Source, out var state)) continue;   // deleted meanwhile
            try
            {
                await _activity.WaitForIdleAsync(stoppingToken);
                await RunAsync(job, state, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                state.Status = StatusFailed;
                state.Error = ex.Message;
                _logger.LogWarning(ex, "TV remux failed for {Path}", job.Source);
            }
        }
    }

    private async Task RunAsync(Job job, JobState state, CancellationToken ct)
    {
        if (IsFresh(job.Source, job.Target)) { state.Status = StatusDone; return; }
        var sourceSize = new FileInfo(job.Source).Length;
        var dir = Path.GetDirectoryName(job.Target)!;
        Directory.CreateDirectory(dir);
        long? free = FreeBytes(dir);
        if (free is null || free.Value - sourceSize < FreeSpaceMarginBytes)
        {
            state.Status = StatusSkipped;
            state.Error = free is null
                ? "free space on the volume could not be determined"
                : $"not enough free space: {free.Value / 1048576} MB free, {sourceSize / 1048576} MB needed + {FreeSpaceMarginBytes / 1048576} MB margin";
            _logger.LogInformation("TV remux skipped for {Path}: {Why}", job.Source, state.Error);
            return;
        }

        state.Status = StatusRunning;
        var tmp = job.Target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var started = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        bool matroskaSource = Path.GetExtension(job.Source).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" };
        // M2TS / ASF may carry packets without timestamps; Matroska doesn't.
        if (!matroskaSource) args.AddRange(["-fflags", "+genpts"]);
        args.AddRange(["-i", job.Source,
            // Every stream (attachments = ASS fonts included), same order, no
            // re-encode; data streams have no Matroska mapping.
            "-map", "0", "-dn", "-c", "copy", "-map_metadata", "0", "-map_chapters", "0",
            // Cues up front: a TV seeks without reading to the end first.
            "-cues_to_front", "1", "-f", "matroska", tmp]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
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
                _logger.LogWarning("TV remux of {Path} failed (exit {Code}): {Err}", job.Source, p.ExitCode, state.Error);
                return;
            }
            File.Move(tmp, job.Target, overwrite: true);
            state.Status = StatusDone;
            state.Error = null;
            _logger.LogInformation("TV remux of {Path} → {Target} in {Sec:F1} s", job.Source, job.Target, started.Elapsed.TotalSeconds);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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
