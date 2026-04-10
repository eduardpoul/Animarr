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
    private readonly Dictionary<Guid, FolderWatcherEntry> _watchers = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

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
            if (_watchers.TryGetValue(folderId, out var entry))
            {
                entry.Dispose();
                _watchers.Remove(folderId);
                logger.LogInformation("Watcher stopped for folder {Id}", folderId);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsWatching(Guid folderId)
    {
        lock (_watchers) return _watchers.ContainsKey(folderId);
    }

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

        // Track in-flight files to debounce rapid events
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingLock = new object();

        watcher.Created += (_, e) => OnFileCreated(e.FullPath, folderId, pending, pendingLock);
        watcher.Renamed += (_, e) => OnFileCreated(e.FullPath, folderId, pending, pendingLock);
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

    private void OnFileCreated(string filePath, Guid folderId, HashSet<string> pending, object pendingLock)
    {
        lock (pendingLock)
        {
            if (!pending.Add(filePath)) return; // already processing
        }

        // Fire and forget with delay to let the file finish copying
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delayMs);

                if (!File.Exists(filePath))
                {
                    logger.LogDebug("Watcher: file disappeared before processing: {Path}", filePath);
                    return;
                }

                var originalName = Path.GetFileName(filePath);

                using var scope = scopeFactory.CreateScope();
                var renameService = scope.ServiceProvider.GetRequiredService<IRenameService>();
                await renameService.ProcessSingleFileAsync(filePath, folderId);

                // Re-read what it was renamed to (if renamed, path changed)
                var newName = GetLatestNameFromHistory(folderId, filePath);
                FileRenamed?.Invoke(folderId, originalName, newName ?? originalName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing file {Path}", filePath);
            }
            finally
            {
                lock (pendingLock) pending.Remove(filePath);
            }
        });
    }

    private string? GetLatestNameFromHistory(Guid folderId, string originalPath)
    {
        // Best effort — synchronous quick lookup
        try
        {
            using var db = dbFactory.CreateDbContext();
            var record = db.RenameHistories
                .Where(h => h.FolderId == folderId && h.OriginalPath == originalPath)
                .OrderByDescending(h => h.ProcessedAt)
                .Select(h => h.NewPath)
                .FirstOrDefault();
            return record is not null ? Path.GetFileName(record) : null;
        }
        catch
        {
            return null;
        }
    }

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
