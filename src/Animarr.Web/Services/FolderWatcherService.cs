using System.Collections.Concurrent;
using System.Text.Json;
using Animarr.Web.Configuration;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Animarr.Web.Services;

/// <summary>
/// Background service that manages FileSystemWatcher instances for all enabled folders.
/// Supports dynamic start/stop without app restart.
/// Notifies Blazor components via the FileRenamed event.
/// </summary>
public class FolderWatcherService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<AppSettings> appOptions,
    ILogger<FolderWatcherService> logger,
    TorrentEngineService torrentEngine) : IHostedService, IDisposable
{
    private readonly int _delayMs = appOptions.Value.WatcherDelayMs;

    // folderId → watcher
    private readonly ConcurrentDictionary<Guid, FolderWatcherEntry> _watchers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    /// <summary>Paths to skip in OnFileCreated — populated before intentional moves to avoid re-processing. Value = expiry TickCount64.</summary>
    private readonly ConcurrentDictionary<string, long> _suppressedPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>H-2: periodic GC of expired _suppressedPaths entries that never got matched by a watcher event.</summary>
    private Timer? _suppressedGcTimer;

    /// <summary>Raised when a file is auto-renamed. Payload: (folderId, originalName, newName).</summary>
    public event Action<Guid, string, string>? FileRenamed;

    /// <summary>Raised when a new subdirectory is auto-registered inside a section. Payload: (sectionId, newFolderId).</summary>
    public event Action<Guid, Guid>? SubfolderCreated;

    // ─── IHostedService ───────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("FolderWatcherService starting.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var enabledFolders = await db.FolderWatchers
            .Where(f => f.WatchEnabled)
            .ToListAsync(cancellationToken);

        foreach (var folder in enabledFolders)
        {
            StartWatcherInternal(folder.Id, folder.Path, folder.IsSection, folder.FlatSection);
        }

        logger.LogInformation("Started {Count} folder watchers.", _watchers.Count);

        // H-2: prune expired _suppressedPaths every 60s
        _suppressedGcTimer = new Timer(_ =>
        {
            var now = Environment.TickCount64;
            foreach (var kv in _suppressedPaths)
                if (kv.Value < now)
                    _suppressedPaths.TryRemove(kv.Key, out long _);
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("FolderWatcherService stopping.");
        Dispose();
        return Task.CompletedTask;
    }

    // ─── Public API (called from UI) ──────────────────────────────────────────

    public async Task StartWatcherAsync(Guid folderId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_watchers.ContainsKey(folderId)) return;

            await using var db = await dbFactory.CreateDbContextAsync();
            var folder = await db.FolderWatchers.FindAsync(folderId);
            if (folder is null) return;

            StartWatcherInternal(folderId, folder.Path, folder.IsSection, folder.FlatSection);
            logger.LogInformation("Watcher started for folder {Id} ({Path})", folderId, folder.Path);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopWatcherAsync(Guid folderId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_watchers.TryRemove(folderId, out var entry))
            {
                entry.Dispose();
                logger.LogInformation("Watcher stopped for folder {Id}", folderId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsWatching(Guid folderId) => _watchers.ContainsKey(folderId);

    private static readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v",
        ".ts", ".m2ts", ".webm", ".flv", ".ogv"
    };

    // ─── Internal watcher creation ────────────────────────────────────────────

    private void StartWatcherInternal(Guid folderId, string path, bool isSection = false, bool flatSection = false)
    {
        if (!Directory.Exists(path))
        {
            logger.LogWarning("Cannot start watcher — directory not found: {Path}", path);
            return;
        }

        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        watcher.Created += (_, e) => OnFileCreated(e.FullPath, folderId);
        watcher.Renamed += (_, e) => OnFileCreated(e.FullPath, folderId);
        watcher.Error += (_, e) => logger.LogError(e.GetException(), "FileSystemWatcher error for {Path}", path);

        FileSystemWatcher? dirWatcher = null;
        if (isSection)
        {
            if (flatSection)
            {
                // Flat section: watch for video files directly in the root
                dirWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                dirWatcher.Created += (_, e) => OnVideoFileCreated(e.FullPath, folderId);
                dirWatcher.Error   += (_, e) => logger.LogError(e.GetException(), "FlatWatcher error for {Path}", path);
            }
            else
            {
                // Normal section: watch for new subdirectories
                dirWatcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true,
                };
                dirWatcher.Created += (_, e) => OnDirectoryCreated(e.FullPath, folderId);
                dirWatcher.Error   += (_, e) => logger.LogError(e.GetException(), "DirWatcher error for {Path}", path);
            }
        }

        _watchers[folderId] = new FolderWatcherEntry(watcher, dirWatcher);
    }

    private void OnDirectoryCreated(string dirPath, Guid sectionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Wait long enough for whatever created the dir to populate it
                // (torrent download, file manager) before we decide whether to register.
                await Task.Delay(2000);

                // BUG-FIX "New Folder reappears": skip junk-named or empty
                // directories. They cannot be identified and re-appear forever
                // if the user removes them from the catalog while the physical
                // folder stays on disk.
                if (!MediaFolderHeuristics.LooksLikeMediaFolder(dirPath))
                {
                    logger.LogDebug("Skipping auto-register — empty or junk subfolder: {Path}", dirPath);
                    return;
                }

                await using var db = await dbFactory.CreateDbContextAsync();

                // Skip if this path is already registered
                if (await db.FolderWatchers.AnyAsync(f => f.Path == dirPath))
                    return;

                // Bug-fix: also skip paths the user has explicitly dismissed in the past.
                if (await IsDismissedAsync(db, sectionId, dirPath))
                {
                    logger.LogDebug("Skipping auto-register — path was dismissed by user: {Path}", dirPath);
                    return;
                }

                var section = await db.FolderWatchers.FindAsync(sectionId);
                if (section is null) return;

                var newFolder = new FolderWatcher
                {
                    Id              = Guid.NewGuid(),
                    Path            = dirPath,
                    Label           = Path.GetFileName(dirPath),
                    WatchEnabled    = section.WatchEnabled,
                    RenameEnabled   = section.RenameEnabled,
                    IdentifyEnabled = section.IdentifyEnabled,
                    FolderType      = section.FolderType,
                    IsSection       = false,
                    ParentSectionId = sectionId,
                    CreatedAt       = DateTime.UtcNow,
                };
                db.FolderWatchers.Add(newFolder);
                await db.SaveChangesAsync();

                logger.LogInformation("Auto-registered subfolder: {Path}", dirPath);

                // Start file watcher for the new subfolder
                await StartWatcherAsync(newFolder.Id);

                // Try to auto-link a torrent whose SavePath matches this folder
                await torrentEngine.TryLinkTorrentAsync(dirPath, newFolder.Id);

                // Enqueue identification if AutoIdentify is enabled AND folder allows it
                using (var scope = scopeFactory.CreateScope())
                {
                    var appCfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
                    var autoIdentify = await appCfg.GetAsync<bool>(AppConfigKeys.AutoIdentifyEnabled, true);
                    if (autoIdentify && newFolder.IdentifyEnabled)
                    {
                        await using var idDb = await dbFactory.CreateDbContextAsync();
                        idDb.IdentificationQueues.Add(new Data.Models.IdentificationQueue
                        {
                            Id       = Guid.NewGuid(),
                            FolderId = newFolder.Id,
                            QueuedAt = DateTime.UtcNow,
                        });
                        await idDb.SaveChangesAsync();
                        logger.LogDebug("Queued identification for new folder {Path}", dirPath);
                    }
                }

                SubfolderCreated?.Invoke(sectionId, newFolder.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error auto-registering subfolder {Path}", dirPath);
            }
        });
    }

    private void OnVideoFileCreated(string filePath, Guid sectionId)
    {
        // Only handle video files directly in the section root
        if (!_videoExtensions.Contains(Path.GetExtension(filePath))) return;
        if (!string.Equals(Path.GetDirectoryName(filePath), null, StringComparison.OrdinalIgnoreCase))
        {
            // Ensure the file is directly in the section root (not a subfolder)
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);

                await using var db = await dbFactory.CreateDbContextAsync();

                // Skip if already registered by SingleFilePath
                if (await db.FolderWatchers.AnyAsync(f => f.SingleFilePath == filePath))
                    return;

                var section = await db.FolderWatchers.FindAsync(sectionId);
                if (section is null) return;

                var newFolder = new FolderWatcher
                {
                    Id              = Guid.NewGuid(),
                    Path            = section.Path,
                    SingleFilePath  = filePath,
                    Label           = Path.GetFileNameWithoutExtension(filePath),
                    WatchEnabled    = false,   // flat entries don't need their own watcher
                    RenameEnabled   = section.RenameEnabled,
                    IdentifyEnabled = section.IdentifyEnabled,
                    FolderType      = FolderType.Movie,
                    IsSection       = false,
                    FlatSection     = false,
                    ParentSectionId = sectionId,
                    CreatedAt       = DateTime.UtcNow,
                };
                db.FolderWatchers.Add(newFolder);
                await db.SaveChangesAsync();

                logger.LogInformation("Auto-registered flat movie: {Path}", filePath);

                // Enqueue identification if AutoIdentify is enabled AND section allows it
                using (var scope = scopeFactory.CreateScope())
                {
                    var appCfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
                    var autoIdentify = await appCfg.GetAsync<bool>(AppConfigKeys.AutoIdentifyEnabled, true);
                    if (autoIdentify && newFolder.IdentifyEnabled)
                    {
                        await using var idDb = await dbFactory.CreateDbContextAsync();
                        idDb.IdentificationQueues.Add(new Data.Models.IdentificationQueue
                        {
                            Id       = Guid.NewGuid(),
                            FolderId = newFolder.Id,
                            QueuedAt = DateTime.UtcNow,
                        });
                        await idDb.SaveChangesAsync();
                        logger.LogDebug("Queued identification for flat movie {Path}", filePath);
                    }
                }

                SubfolderCreated?.Invoke(sectionId, newFolder.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error auto-registering flat movie {Path}", filePath);
            }
        });
    }

    private void OnFileCreated(string filePath, Guid folderId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Skip paths suppressed by intentional renames/flattens
                if (_suppressedPaths.TryGetValue(filePath, out var expiry))
                {
                    if (Environment.TickCount64 < expiry)
                    {
                        _suppressedPaths.TryRemove(filePath, out _);
                        return;
                    }
                    _suppressedPaths.TryRemove(filePath, out _);
                }

                await using var db = await dbFactory.CreateDbContextAsync();

                // Dedup: skip if this file is already queued or being processed
                var alreadyQueued = await db.RenameQueues.AnyAsync(q =>
                    q.FilePath == filePath &&
                    q.FolderId == folderId &&
                    q.Status < RenameQueueStatus.Done);

                if (alreadyQueued) return;

                db.RenameQueues.Add(new Data.Models.RenameQueue
                {
                    Id       = Guid.NewGuid(),
                    FolderId = folderId,
                    FilePath = filePath,
                    Source   = Data.Models.RenameQueueSource.Watcher,
                    QueuedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
                logger.LogDebug("Queued file for rename: {Path}", filePath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to enqueue file {Path}", filePath);
            }
        });
    }

    /// <summary>Called by RenameQueueProcessorService after a file has been processed.</summary>
    public void NotifyFileRenamed(Guid folderId, string originalName, string newName)
        => FileRenamed?.Invoke(folderId, originalName, newName);

    /// <summary>Suppresses the next watcher event for <paramref name="filePath"/> for up to 15 seconds.
    /// Call this before intentionally moving a file so the resulting FSW event is ignored.</summary>
    public void SuppressPath(string filePath)
        => _suppressedPaths[filePath] = Environment.TickCount64 + 15_000;

    /// <summary>
    /// Bug-fix "New Folder reappears": persistent per-section list of dismissed
    /// child paths. When the user removes a child from the catalog, the physical
    /// folder on disk usually remains — we remember the path here so the auto-
    /// discovery loop doesn't keep re-registering it.
    /// </summary>
    public async Task DismissChildPathAsync(Guid sectionId, string childPath)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = DismissedKey(sectionId);
        var raw = await db.AppConfigs.FindAsync(key);
        var list = ParseDismissed(raw?.Value);
        if (list.Add(NormalisePath(childPath)))
        {
            var json = JsonSerializer.Serialize(list.ToList());
            if (raw is null)
                db.AppConfigs.Add(new AppConfig { Key = key, Value = json });
            else
                raw.Value = json;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Removes <paramref name="childPath"/> from the section's dismissed list so
    /// the next DiscoverChildrenAsync (or FSW Created event) re-registers it.
    /// Used when the user explicitly wants to bring back an accidentally-deleted
    /// folder (e.g. one that was lost to an auto-rename catastrophe).
    /// </summary>
    public async Task UndismissChildPathAsync(Guid sectionId, string childPath)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = DismissedKey(sectionId);
        var raw = await db.AppConfigs.FindAsync(key);
        if (raw?.Value is null) return;
        var list = ParseDismissed(raw.Value);
        if (list.Remove(NormalisePath(childPath)))
        {
            raw.Value = JsonSerializer.Serialize(list.ToList());
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Wipes ALL dismissed paths for a section — used by the "forget
    /// dismissed folders" recovery action.</summary>
    public async Task ClearDismissedAsync(Guid sectionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = DismissedKey(sectionId);
        var raw = await db.AppConfigs.FindAsync(key);
        if (raw is null) return;
        db.AppConfigs.Remove(raw);
        await db.SaveChangesAsync();
    }

    /// <summary>Returns true if <paramref name="childPath"/> was previously dismissed for this section.</summary>
    private static async Task<bool> IsDismissedAsync(AppDbContext db, Guid sectionId, string childPath)
    {
        var key = DismissedKey(sectionId);
        var raw = await db.AppConfigs.FindAsync(key);
        if (raw?.Value is null) return false;
        var list = ParseDismissed(raw.Value);
        return list.Contains(NormalisePath(childPath));
    }

    private static string DismissedKey(Guid sectionId) => $"dismissed.section.{sectionId}";

    private static HashSet<string> ParseDismissed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            return arr is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(arr, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static string NormalisePath(string p)
        => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // ─── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _suppressedGcTimer?.Dispose();
        _suppressedGcTimer = null;
        foreach (var entry in _watchers.Values)
            entry.Dispose();
        _watchers.Clear();
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Entry wrapper ────────────────────────────────────────────────────────

    private sealed class FolderWatcherEntry(FileSystemWatcher watcher, FileSystemWatcher? dirWatcher = null) : IDisposable
    {
        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            if (dirWatcher is not null)
            {
                dirWatcher.EnableRaisingEvents = false;
                dirWatcher.Dispose();
            }
        }
    }
}
