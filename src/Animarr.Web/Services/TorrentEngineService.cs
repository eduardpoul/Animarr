using System.Collections.Concurrent;
using System.Net;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using MonoTorrent;
using MonoTorrent.Client;

namespace Animarr.Web.Services;

public class TorrentEngineService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TorrentEngineService> _logger;

    private ClientEngine _engine = null!;
    private readonly ConcurrentDictionary<string, TorrentManager> _managers = new();
    private readonly ConcurrentDictionary<string, TorrentLiveStats> _liveStats = new();
    /// <summary>Display name cache for torrents (magnet names before metadata arrives).</summary>
    private readonly ConcurrentDictionary<string, string> _names = new();
    /// <summary>FolderWatcherId per torrent (populated on add, available in stats).</summary>
    private readonly ConcurrentDictionary<string, Guid?> _folderWatchers = new();
    private readonly ConcurrentDictionary<string, bool> _autoRename = new();

    /// <summary>Fires every 500 ms and on any torrent state change.</summary>
    public event Action? StateChanged;

    public TorrentEngineService(
        IServiceScopeFactory scopeFactory,
        ILogger<TorrentEngineService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // BackgroundService lifecycle
    // -------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var cfg = await LoadConfigAsync();
            await StartEngineAsync(cfg);
            await RestoreActiveTorrentsAsync(cfg);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            int tickCount = 0;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                UpdateLiveStats();
                StateChanged?.Invoke();

                // Every 4 ticks (~2s): auto-mark 100% downloaded files as DoNotDownload
                if (++tickCount % 4 == 0)
                    await AutoMarkCompletedFilesAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TorrentEngineService crashed");
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    private async Task<TorrentConfig> LoadConfigAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        var cfg = await ctx.TorrentConfig.FirstOrDefaultAsync(c => c.Id == 1);
        if (cfg is null)
        {
            cfg = new TorrentConfig();
            ctx.TorrentConfig.Add(cfg);
            await ctx.SaveChangesAsync();
        }
        return cfg;
    }

    private Task StartEngineAsync(TorrentConfig cfg)
    {
        Directory.CreateDirectory(cfg.CacheDirectory);

        var builder = new EngineSettingsBuilder
        {
            CacheDirectory          = cfg.CacheDirectory,
            MaximumDownloadRate     = cfg.GlobalDownloadLimit,
            MaximumUploadRate       = cfg.GlobalUploadLimit,
            MaximumConnections      = cfg.MaxConnections,
            AllowPortForwarding     = cfg.EnableUPnP,
            AllowLocalPeerDiscovery = cfg.EnableLSD,
        };
        builder.ListenEndPoints["ipv4"] = new IPEndPoint(IPAddress.Any, cfg.ListenPort);

        _engine = new ClientEngine(builder.ToSettings());
        return Task.CompletedTask;
    }

    private async Task RestoreActiveTorrentsAsync(TorrentConfig cfg)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        var records = await ctx.TorrentRecords
            .Where(r => r.State != TorrentRecordState.Stopped && r.State != TorrentRecordState.Error)
            .Include(r => r.FileSelections)
            .Include(r => r.FolderWatcher)
            .ToListAsync();

        foreach (var record in records)
        {
            try
            {
                var torrentSettings = BuildTorrentSettings(record.DownloadLimit, record.UploadLimit);
                TorrentManager mgr;

                if (record.MagnetLink is not null)
                {
                    mgr = await _engine.AddAsync(MagnetLink.Parse(record.MagnetLink), record.SavePath, torrentSettings);
                }
                else if (record.TorrentFilePath is not null && File.Exists(record.TorrentFilePath))
                {
                    var torrent = await Torrent.LoadAsync(record.TorrentFilePath);
                    mgr = await _engine.AddAsync(torrent, record.SavePath, torrentSettings);
                }
                else
                {
                    _logger.LogWarning("Cannot restore torrent {Name}: no magnet or file", record.Name);
                    continue;
                }

                _names[record.InfoHash] = record.Name;
                _folderWatchers[record.InfoHash] = record.FolderWatcherId;
                _autoRename[record.InfoHash] = record.AutoRename;

                await ApplyFileSelectionsAsync(mgr, record.FileSelections);
                SubscribeEvents(mgr, record.InfoHash, cfg);
                _managers[record.InfoHash] = mgr;

                if (record.State != TorrentRecordState.Paused)
                    await mgr.StartAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore torrent {Name}", record.Name);
            }
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await PersistStatsAsync();
            await _engine.StopAllAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during shutdown");
        }
    }

    // -------------------------------------------------------------------------
    // Event subscriptions
    // -------------------------------------------------------------------------

    private void SubscribeEvents(TorrentManager mgr, string infoHash, TorrentConfig cfg)
    {
        mgr.TorrentStateChanged += (_, e) =>
        {
            // When a magnet torrent transitions from Metadata state, metadata has just arrived
            if (e.OldState == TorrentState.Metadata)
                Task.Run(async () => await OnMetadataReceivedAsync(mgr, infoHash));

            Task.Run(async () => await OnStateChangedAsync(mgr, infoHash, cfg));
        };
    }

    private async Task OnStateChangedAsync(TorrentManager mgr, string infoHash, TorrentConfig cfg)
    {
        UpdateLiveStats();
        StateChanged?.Invoke();

        try
        {
        if (mgr.State == TorrentState.Seeding)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var ctx = await db.CreateDbContextAsync();

            var record = await ctx.TorrentRecords
                .Include(r => r.FolderWatcher)
                .FirstOrDefaultAsync(r => r.InfoHash == infoHash);

            if (record is null) return;

            if (record.CompletedAt is null)
            {
                record.CompletedAt = DateTime.UtcNow;
                record.State = TorrentRecordState.Seeding;
                await ctx.SaveChangesAsync();

                if (cfg.AutoRenameAfterDownload && record.FolderWatcher?.RenameEnabled == true)
                {
                    try
                    {
                        var renameService = scope.ServiceProvider.GetRequiredService<IRenameService>();
                        var items = await renameService.ScanFolderAsync(record.FolderWatcher.Id);
                        var toApply = items.Where(i => i.Status == PreviewStatus.WillRename && i.IsSelected).ToList();
                        if (toApply.Count > 0)
                            await renameService.ApplyRenamesAsync(record.FolderWatcher.Id, toApply);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto-rename failed for {Path}", record.SavePath);
                    }
                }

                if (record.FlattenSubfolders)
                {
                    try { FlattenToRoot(record.SavePath); }
                    catch (Exception ex) { _logger.LogWarning(ex, "FlattenSubfolders failed for {Path}", record.SavePath); }
                }
            }

            // Stop seeding check
            var ratio = record.StopSeedingRatio ?? cfg.StopSeedingRatio;
            var totalSize = mgr.Torrent?.Size ?? 0;
            bool ratioReached = ratio > 0 && totalSize > 0 &&
                (record.Uploaded + mgr.Monitor.DataBytesSent) >= (long)(totalSize * ratio);

            if (record.StopAfterDownload || cfg.StopSeedingAfterDone || ratioReached)
                await mgr.StopAsync();
        }
        else if (mgr.State == TorrentState.Error)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var ctx = await db.CreateDbContextAsync();
            var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
            if (record is not null) { record.State = TorrentRecordState.Error; await ctx.SaveChangesAsync(); }
        }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict in OnStateChangedAsync for {InfoHash}; ignoring.", infoHash);
        }
    }

    private async Task OnMetadataReceivedAsync(TorrentManager mgr, string infoHash)
    {
        if (mgr.Torrent is null) return;

        _names[infoHash] = mgr.Torrent.Name;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var ctx = await db.CreateDbContextAsync();
            var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
            if (record is null) return;

            record.Name      = mgr.Torrent.Name;
            record.TotalSize = mgr.Torrent.Size;
            record.State     = TorrentRecordState.Downloading;
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrency conflict in OnMetadataReceivedAsync for {InfoHash}; ignoring.", infoHash);
        }

        UpdateLiveStats();
        StateChanged?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Live stats
    // -------------------------------------------------------------------------

    /// <summary>
    /// Automatically sets Priority=DoNotDownload for any file that has reached 100%
    /// so the renamer can safely rename it without the torrent re-verifying the old path.
    /// </summary>
    private async Task AutoMarkCompletedFilesAsync()
    {
        foreach (var mgr in _managers.Values)
        {
            if (mgr.Torrent is null) continue; // metadata not yet received
            foreach (var file in mgr.Files)
            {
                if (file.Priority == Priority.DoNotDownload) continue;
                if (file.BitField.Length > 0 && file.BitField.TrueCount == file.BitField.Length)
                    await mgr.SetFilePriorityAsync(file, Priority.DoNotDownload);
            }
        }
    }

    private void UpdateLiveStats()
    {
        foreach (var (hash, mgr) in _managers)
        {
            _liveStats[hash] = new TorrentLiveStats(
                InfoHash:         hash,
                Name:             mgr.Torrent?.Name ?? _names.GetValueOrDefault(hash, hash[..Math.Min(8, hash.Length)]),
                SavePath:         mgr.SavePath,
                State:            mgr.State,
                Progress:         mgr.Progress,
                DownloadRate:     mgr.Monitor.DownloadRate,
                UploadRate:       mgr.Monitor.UploadRate,
                Downloaded:       mgr.Monitor.DataBytesReceived,
                Uploaded:         mgr.Monitor.DataBytesSent,
                TotalSize:        mgr.Torrent?.Size ?? 0,
                Seeds:            mgr.Peers.Seeds,
                Peers:            mgr.Peers.Leechs,
                DownloadLimit:    mgr.Settings.MaximumDownloadRate,
                UploadLimit:      mgr.Settings.MaximumUploadRate,
                MetadataReceived: mgr.Torrent is not null,
                FolderWatcherId:  _folderWatchers.GetValueOrDefault(hash),
                AutoRename:       _autoRename.GetValueOrDefault(hash)
            );
        }

        foreach (var hash in _liveStats.Keys.Except(_managers.Keys).ToList())
            _liveStats.TryRemove(hash, out _);
    }

    private async Task PersistStatsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        foreach (var (hash, stats) in _liveStats)
        {
            var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == hash);
            if (record is null) continue;
            record.Downloaded = stats.Downloaded;
            record.Uploaded   = stats.Uploaded;
            record.State      = MapState(stats.State);
        }
        await ctx.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public IReadOnlyList<TorrentLiveStats> GetAll() => [.. _liveStats.Values];

    /// <summary>
    /// Returns the set of absolute file paths currently being actively downloaded
    /// (i.e. the torrent is in Downloading or Metadata state and priority > 0).
    /// RenameService uses this to skip in-progress files.
    /// </summary>
    public HashSet<string> GetActiveDownloadFilePaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hash, mgr) in _managers)
        {
            // Only exclude files from torrents that are actively downloading
            if (mgr.State != TorrentState.Downloading && mgr.State != TorrentState.Metadata)
                continue;
            foreach (var file in mgr.Files)
            {
                if (file.Priority == Priority.DoNotDownload) continue;
                // file.Path is relative inside the save path
                var abs = System.IO.Path.Combine(mgr.SavePath, file.Path);
                result.Add(abs);
                // Also add partial file variant (MonoTorrent may use .!bt suffix)
                result.Add(abs + ".!bt");
            }
            // If metadata not yet received, block the whole save path directory
            if (mgr.Torrent is null)
                result.Add(mgr.SavePath);
        }
        return result;
    }

    public TorrentLiveStats? Get(string infoHash) => _liveStats.GetValueOrDefault(infoHash);

    public IList<ITorrentManagerFile>? GetFiles(string infoHash)
        => _managers.TryGetValue(infoHash, out var mgr) ? mgr.Files : null;

    /// <summary>
    /// After a file is renamed by the renamer, mark it DoNotDownload so MonoTorrent
    /// stops tracking it and won't try to re-download the (now missing) original path.
    /// Matches by the absolute path on disk (ITorrentManagerFile.FullPath).
    /// </summary>
    public async Task SetFileDoNotDownloadByAbsPathAsync(string originalAbsPath)
    {
        foreach (var mgr in _managers.Values)
        {
            var file = mgr.Files.FirstOrDefault(f =>
                string.Equals(f.FullPath, originalAbsPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.DownloadIncompleteFullPath, originalAbsPath, StringComparison.OrdinalIgnoreCase));
            if (file is not null)
            {
                await mgr.SetFilePriorityAsync(file, Priority.DoNotDownload);
                return;
            }
        }
    }

    /// <summary>Returns per-file download progress 0–100. Key = normalized path.</summary>
    public Dictionary<string, double>? GetFileProgress(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return null;
        return mgr.Files.ToDictionary(
            f => f.Path.Replace('\\', '/'),
            f => f.BitField.Length > 0 ? f.BitField.TrueCount * 100.0 / f.BitField.Length : 0.0);
    }

    /// <summary>Parse a .torrent file and return its file list without adding to the engine.</summary>
    public static async Task<List<TorrentFileEntry>> ParseTorrentFilesAsync(byte[] data)
    {
        var torrent = await Torrent.LoadAsync(data);
        return [.. torrent.Files.Select(f => new TorrentFileEntry(f.Path, f.Length))];
    }

    /// <summary>Returns current file priorities for an active torrent. Key = file path, Value = Priority as int.</summary>
    public Dictionary<string, int>? GetFilePriorities(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return null;
        return mgr.Files.ToDictionary(f => f.Path, f => (int)f.Priority);
    }

    /// <summary>Set multiple file priorities atomically and persist to DB.</summary>
    public async Task SetFilePrioritiesBatchAsync(string infoHash, Dictionary<string, int> priorities)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        foreach (var (path, prio) in priorities)
        {
            var file = mgr.Files.FirstOrDefault(f => f.Path.Replace('\\', '/') == path);
            if (file is not null)
                await mgr.SetFilePriorityAsync(file, (Priority)prio);
        }
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var record = await ctx.TorrentRecords.Include(r => r.FileSelections)
            .FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is null) return;
        foreach (var (path, prio) in priorities)
        {
            var sel = record.FileSelections.FirstOrDefault(s => s.FilePath == path);
            if (sel is null)
                record.FileSelections.Add(new TorrentFileSelection { Id = Guid.NewGuid(), TorrentId = record.Id, FilePath = path, Priority = prio });
            else
                sel.Priority = prio;
        }
        await ctx.SaveChangesAsync();
    }

    /// <summary>Persist the StopAfterDownload flag for a torrent.</summary>
    public async Task SetStopAfterDownloadAsync(string infoHash, bool value)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is not null) { record.StopAfterDownload = value; await ctx.SaveChangesAsync(); }
    }

    public async Task<string> AddMagnetAsync(
        string magnetUri, string savePath, Guid? folderWatcherId,
        int dlLimit, int ulLimit, bool autoRename, bool startPaused, bool stopAfterDownload = false,
        bool flattenSubfolders = false)
    {
        var magnetLink = MagnetLink.Parse(magnetUri);
        var infoHash = magnetLink.InfoHashes.V1OrV2.ToHex();

        if (_managers.ContainsKey(infoHash))
            throw new InvalidOperationException("Торрент уже добавлен");

        var cfg = await LoadConfigAsync();
        var torrentSettings = BuildTorrentSettings(dlLimit, ulLimit);
        var mgr = await _engine.AddAsync(magnetLink, savePath, torrentSettings);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        var name = magnetLink.Name ?? infoHash[..Math.Min(8, infoHash.Length)];
        var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is null)
        {
            record = new TorrentRecord { Id = Guid.NewGuid(), InfoHash = infoHash, AddedAt = DateTime.UtcNow };
            ctx.TorrentRecords.Add(record);
        }
        record.Name            = name;
        record.MagnetLink      = magnetUri;
        record.SavePath        = savePath;
        record.FolderWatcherId = folderWatcherId;
        record.State           = startPaused ? TorrentRecordState.Paused : TorrentRecordState.Metadata;
        record.DownloadLimit   = dlLimit;
        record.UploadLimit     = ulLimit;
        record.AutoRename        = autoRename;
        record.StopAfterDownload  = stopAfterDownload;
        record.FlattenSubfolders  = flattenSubfolders;
        record.CompletedAt        = null;
        record.ErrorMessage       = null;
        await ctx.SaveChangesAsync();

        _names[infoHash] = name;
        _folderWatchers[infoHash] = folderWatcherId;
        _autoRename[infoHash] = autoRename;

        SubscribeEvents(mgr, infoHash, cfg);
        _managers[infoHash] = mgr;

        if (!startPaused) await mgr.StartAsync();

        UpdateLiveStats();
        StateChanged?.Invoke();
        return infoHash;
    }

    public async Task<string> AddTorrentFileAsync(
        byte[] torrentData, string savePath, Guid? folderWatcherId,
        int dlLimit, int ulLimit, bool autoRename, bool startPaused, bool stopAfterDownload = false,
        Dictionary<string, int>? initialPriorities = null, bool flattenSubfolders = false)
    {
        var torrent = await Torrent.LoadAsync(torrentData);
        var infoHash = torrent.InfoHashes.V1OrV2.ToHex();

        if (_managers.ContainsKey(infoHash))
            throw new InvalidOperationException("Торрент уже добавлен");

        var cfg = await LoadConfigAsync();
        Directory.CreateDirectory(cfg.CacheDirectory);
        var torrentPath = Path.Combine(cfg.CacheDirectory, $"{infoHash}.torrent");
        await File.WriteAllBytesAsync(torrentPath, torrentData);

        var torrentSettings = BuildTorrentSettings(dlLimit, ulLimit);
        var mgr = await _engine.AddAsync(torrent, savePath, torrentSettings);

        // Apply initial priorities BEFORE starting so skipped files aren't downloaded
        if (initialPriorities is not null)
        {
            foreach (var (path, prio) in initialPriorities)
            {
                var file = mgr.Files.FirstOrDefault(f => f.Path == path);
                if (file is not null)
                    await mgr.SetFilePriorityAsync(file, (Priority)prio);
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();

        var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is null)
        {
            record = new TorrentRecord { Id = Guid.NewGuid(), InfoHash = infoHash, AddedAt = DateTime.UtcNow };
            ctx.TorrentRecords.Add(record);
        }
        record.Name            = torrent.Name;
        record.TorrentFilePath = torrentPath;
        record.SavePath        = savePath;
        record.FolderWatcherId = folderWatcherId;
        record.State           = startPaused ? TorrentRecordState.Paused : TorrentRecordState.Downloading;
        record.TotalSize       = torrent.Size;
        record.DownloadLimit   = dlLimit;
        record.UploadLimit     = ulLimit;
        record.AutoRename        = autoRename;
        record.StopAfterDownload  = stopAfterDownload;
        record.FlattenSubfolders  = flattenSubfolders;
        record.CompletedAt        = null;
        record.ErrorMessage       = null;
        // Persist initial file priorities — use ExecuteDeleteAsync so EF change tracking
        // never issues individual DELETE statements that could return 0 rows on a re-add.
        if (initialPriorities is not null)
        {
            await ctx.TorrentFileSelections
                .Where(s => s.TorrentId == record.Id)
                .ExecuteDeleteAsync();
            ctx.TorrentFileSelections.AddRange(initialPriorities.Select(kvp =>
                new TorrentFileSelection { Id = Guid.NewGuid(), TorrentId = record.Id, FilePath = kvp.Key, Priority = kvp.Value }));
        }
        await ctx.SaveChangesAsync();

        _names[infoHash] = torrent.Name;
        _folderWatchers[infoHash] = folderWatcherId;
        _autoRename[infoHash] = autoRename;

        SubscribeEvents(mgr, infoHash, cfg);
        _managers[infoHash] = mgr;

        if (!startPaused) await mgr.StartAsync();

        UpdateLiveStats();
        StateChanged?.Invoke();
        return infoHash;
    }

    public async Task PauseAsync(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        await mgr.PauseAsync();
        await SetRecordStateAsync(infoHash, TorrentRecordState.Paused);
        UpdateLiveStats(); StateChanged?.Invoke();
    }

    public async Task ResumeAsync(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        await mgr.StartAsync();
        await SetRecordStateAsync(infoHash, TorrentRecordState.Downloading);
        UpdateLiveStats(); StateChanged?.Invoke();
    }

    public async Task StopAsync(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        await mgr.StopAsync();
        await SetRecordStateAsync(infoHash, TorrentRecordState.Stopped);
        UpdateLiveStats(); StateChanged?.Invoke();
    }

    public async Task RemoveAsync(string infoHash, bool deleteFiles)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        if (mgr.State != TorrentState.Stopped)
            await mgr.StopAsync();

        await _engine.RemoveAsync(mgr,
            deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);

        _managers.TryRemove(infoHash, out _);
        _liveStats.TryRemove(infoHash, out _);
        _names.TryRemove(infoHash, out _);
        _folderWatchers.TryRemove(infoHash, out _);
        _autoRename.TryRemove(infoHash, out _);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is not null) { record.State = TorrentRecordState.Stopped; await ctx.SaveChangesAsync(); }

        StateChanged?.Invoke();
    }

    public async Task SetFilePriorityAsync(string infoHash, string filePath, int priority)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        var file = mgr.Files.FirstOrDefault(f => f.Path == filePath);
        if (file is null) return;
        await mgr.SetFilePriorityAsync(file, (Priority)priority);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var record = await ctx.TorrentRecords.Include(r => r.FileSelections)
            .FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is null) return;
        var sel = record.FileSelections.FirstOrDefault(s => s.FilePath == filePath);
        if (sel is null)
            record.FileSelections.Add(new TorrentFileSelection { Id = Guid.NewGuid(), TorrentId = record.Id, FilePath = filePath, Priority = priority });
        else
            sel.Priority = priority;
        await ctx.SaveChangesAsync();
    }

    public async Task SetSpeedLimitsAsync(string infoHash, int dlLimit, int ulLimit)
    {
        if (!_managers.TryGetValue(infoHash, out var mgr)) return;
        var newSettings = new TorrentSettingsBuilder(mgr.Settings)
        {
            MaximumDownloadRate = dlLimit,
            MaximumUploadRate   = ulLimit,
        }.ToSettings();
        await mgr.UpdateSettingsAsync(newSettings);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is not null) { record.DownloadLimit = dlLimit; record.UploadLimit = ulLimit; await ctx.SaveChangesAsync(); }

        UpdateLiveStats(); StateChanged?.Invoke();
    }

    public async Task UpdateGlobalSettingsAsync(TorrentConfig cfg)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var existing = await ctx.TorrentConfig.FirstOrDefaultAsync(c => c.Id == 1);
        if (existing is null) { ctx.TorrentConfig.Add(cfg); }
        else
        {
            existing.GlobalDownloadLimit    = cfg.GlobalDownloadLimit;
            existing.GlobalUploadLimit      = cfg.GlobalUploadLimit;
            existing.MaxConnections         = cfg.MaxConnections;
            existing.EnableUPnP             = cfg.EnableUPnP;
            existing.EnableLSD              = cfg.EnableLSD;
            existing.EnableDHT              = cfg.EnableDHT;
            existing.ListenPort             = cfg.ListenPort;
            existing.StopSeedingAfterDone   = cfg.StopSeedingAfterDone;
            existing.StopSeedingRatio       = cfg.StopSeedingRatio;
            existing.AutoRenameAfterDownload = cfg.AutoRenameAfterDownload;
        }
        await ctx.SaveChangesAsync();

        var builder = new EngineSettingsBuilder(_engine.Settings)
        {
            MaximumDownloadRate     = cfg.GlobalDownloadLimit,
            MaximumUploadRate       = cfg.GlobalUploadLimit,
            MaximumConnections      = cfg.MaxConnections,
            AllowPortForwarding     = cfg.EnableUPnP,
            AllowLocalPeerDiscovery = cfg.EnableLSD,
        };
        await _engine.UpdateSettingsAsync(builder.ToSettings());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static TorrentSettings BuildTorrentSettings(int dl, int ul)
        => new TorrentSettingsBuilder { MaximumDownloadRate = dl, MaximumUploadRate = ul }.ToSettings();

    private async Task SetRecordStateAsync(string infoHash, TorrentRecordState state)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        var record = await ctx.TorrentRecords.FirstOrDefaultAsync(r => r.InfoHash == infoHash);
        if (record is null) return;
        record.State = state;
        await ctx.SaveChangesAsync();
    }

    private static async Task ApplyFileSelectionsAsync(TorrentManager mgr, ICollection<TorrentFileSelection> selections)
    {
        if (mgr.Files.Count == 0 || selections.Count == 0) return;
        foreach (var sel in selections)
        {
            var file = mgr.Files.FirstOrDefault(f => f.Path == sel.FilePath);
            if (file is not null)
                await mgr.SetFilePriorityAsync(file, (Priority)sel.Priority);
        }
    }

    private static TorrentRecordState MapState(TorrentState state) => state switch
    {
        TorrentState.Seeding  => TorrentRecordState.Seeding,
        TorrentState.Paused   => TorrentRecordState.Paused,
        TorrentState.Stopped  => TorrentRecordState.Stopped,
        TorrentState.Error    => TorrentRecordState.Error,
        TorrentState.Metadata => TorrentRecordState.Metadata,
        TorrentState.Hashing  => TorrentRecordState.Hashing,
        _                     => TorrentRecordState.Downloading,
    };

    private static void FlattenToRoot(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return;
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToList())
        {
            var dest = Path.Combine(rootPath, Path.GetFileName(file));
            if (string.Equals(file, dest, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(dest))
                File.Move(file, dest);
        }
        foreach (var dir in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }
}
