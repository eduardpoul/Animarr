using System.Collections.Concurrent;
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
            StartWatcherInternal(folder.Id, folder.Path, folder.IsSection);
        }

        logger.LogInformation("Started {Count} folder watchers.", _watchers.Count);
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

            StartWatcherInternal(folderId, folder.Path, folder.IsSection);
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

    // ─── Internal watcher creation ────────────────────────────────────────────

    private void StartWatcherInternal(Guid folderId, string path, bool isSection = false)
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
            dirWatcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            dirWatcher.Created += (_, e) => OnDirectoryCreated(e.FullPath, folderId);
            dirWatcher.Error   += (_, e) => logger.LogError(e.GetException(), "DirWatcher error for {Path}", path);
        }

        _watchers[folderId] = new FolderWatcherEntry(watcher, dirWatcher);
    }

    private void OnDirectoryCreated(string dirPath, Guid sectionId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500); // let the OS fully flush the directory creation

                await using var db = await dbFactory.CreateDbContextAsync();

                // Skip if this path is already registered
                if (await db.FolderWatchers.AnyAsync(f => f.Path == dirPath))
                    return;

                var section = await db.FolderWatchers.FindAsync(sectionId);
                if (section is null) return;

                var newFolder = new FolderWatcher
                {
                    Id              = Guid.NewGuid(),
                    Path            = dirPath,
                    Label           = Path.GetFileName(dirPath),
                    WatchEnabled    = section.WatchEnabled,
                    RenameEnabled   = section.RenameEnabled,
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

                SubfolderCreated?.Invoke(sectionId, newFolder.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error auto-registering subfolder {Path}", dirPath);
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

    // ─── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
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
