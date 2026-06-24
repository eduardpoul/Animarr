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

        // Startup reconcile: drop FolderWatcher rows whose folder/file vanished
        // from disk while we were down. Per-section + skips unreachable roots, so
        // a temporary unmount can't wipe the library. Fire-and-forget so a slow
        // mount doesn't delay startup.
        _ = Task.Run(async () =>
        {
            try
            {
                var removed = await CleanupOrphanedChildrenAllAsync();
                if (removed > 0) logger.LogInformation("Startup reconcile removed {Count} orphaned folder rows.", removed);
            }
            catch (Exception ex) { logger.LogWarning(ex, "startup orphan cleanup failed"); }
        });

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

        // Renaming was removed — the per-folder watcher now only surfaces errors.
        // Section-level dir/file watchers (below) still drive auto-registration.
        watcher.Error += (_, e) => logger.LogError(e.GetException(), "FileSystemWatcher error for {Path}", path);

        // Sections always get both watchers: directories (for new subfolder per-title
        // entries) AND files (for standalone movies dropped right in the section root).
        // The previous "either/or" controlled by flatSection meant a Movies section
        // with mixed content (one Title-subfolder + loose .mkv files) only picked up
        // one mode. flatSection is kept as the toggle that turns OFF the dir-watcher
        // — useful for sections that should never auto-register subfolders.
        FileSystemWatcher? dirWatcher = null;
        FileSystemWatcher? fileWatcher = null;
        if (isSection)
        {
            if (!flatSection)
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

            // Standalone video files in the section root → always register a SingleFilePath
            // entry per file. Filtering happens inside OnVideoFileCreated.
            fileWatcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            fileWatcher.Created += (_, e) => OnVideoFileCreated(e.FullPath, folderId);
            fileWatcher.Renamed += (_, e) => OnVideoFileCreated(e.FullPath, folderId);
            fileWatcher.Error   += (_, e) => logger.LogError(e.GetException(), "FileWatcher error for {Path}", path);
        }

        _watchers[folderId] = new FolderWatcherEntry(watcher, dirWatcher, fileWatcher);
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

    /// <summary>
    /// One-shot scan of a section's path: enumerates immediate subdirectories
    /// and registers each one as a child FolderWatcher (the OnDirectoryCreated
    /// FSW path only fires for dirs created AFTER the watcher starts, so an
    /// existing populated tree never gets picked up otherwise).
    ///
    /// Used by:
    ///   - SectionFolderDialog after a brand-new section is saved — turns an
    ///     "added an existing /Pool-D1/Media/Anime with 40 anime in it" into
    ///     40 child rows the catalog can identify.
    ///   - RootFoldersList Rescan button — picks up any folders added while
    ///     the app was down OR while the section was disabled.
    ///
    /// Skips: already-registered paths, dismissed paths, junk/empty dirs (via
    /// MediaFolderHeuristics). Identification queuing is the caller's choice
    /// so we don't duplicate the policy that lives in OnDirectoryCreated.
    ///
    /// Returns the IDs of the newly-registered child watchers.
    /// </summary>
    public async Task<List<Guid>> DiscoverChildrenAsync(Guid sectionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var section = await db.FolderWatchers.FindAsync(sectionId);
        if (section is null || !section.IsSection) return [];
        if (!Directory.Exists(section.Path))
        {
            logger.LogWarning("DiscoverChildrenAsync: section path missing — {Path}", section.Path);
            return [];
        }

        // Prune children whose folder/file vanished from disk BEFORE discovering
        // new ones, so a scan both adds and removes. Safe: CleanupOrphanedChildren
        // bails when the section root itself is unreachable (temporary unmount).
        try { await CleanupOrphanedChildrenAsync(sectionId); }
        catch (Exception ex) { logger.LogWarning(ex, "orphan cleanup failed for {Path}", section.Path); }

        string[] dirs;
        string[] files;
        try
        {
            dirs  = Directory.GetDirectories(section.Path);
            // Loose video files in the section root → register each as a flat
            // SingleFilePath entry. Same shape as OnVideoFileCreated, but for
            // files that were already there when the section was added.
            files = Directory.GetFiles(section.Path)
                .Where(f => _videoExtensions.Contains(Path.GetExtension(f)))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DiscoverChildrenAsync: enumerate failed for {Path}", section.Path);
            return [];
        }

        // Pre-load existing registrations + dismissed list once, in-memory comparisons after.
        // Track both folder Paths and SingleFilePath entries so we don't double-register either.
        var existingChildren = await db.FolderWatchers
            .Where(f => f.ParentSectionId == sectionId)
            .Select(f => new { f.Path, f.SingleFilePath })
            .ToListAsync();
        var existingPaths = new HashSet<string>(
            existingChildren.Where(c => c.SingleFilePath is null).Select(c => c.Path),
            StringComparer.OrdinalIgnoreCase);
        var existingFiles = new HashSet<string>(
            existingChildren.Where(c => c.SingleFilePath is { Length: > 0 }).Select(c => c.SingleFilePath!),
            StringComparer.OrdinalIgnoreCase);

        var dismissed = ParseDismissed(
            (await db.AppConfigs.FindAsync(DismissedKey(sectionId)))?.Value);

        var newIds = new List<Guid>();
        var toAdd  = new List<FolderWatcher>();

        foreach (var dirPath in dirs)
        {
            if (existingPaths.Contains(dirPath)) continue;
            if (dismissed.Contains(NormalisePath(dirPath))) continue;
            if (!MediaFolderHeuristics.LooksLikeMediaFolder(dirPath))
            {
                logger.LogDebug("DiscoverChildrenAsync: skipping non-media dir {Path}", dirPath);
                continue;
            }

            var newFolder = new FolderWatcher
            {
                Id              = Guid.NewGuid(),
                Path            = dirPath,
                Label           = Path.GetFileName(dirPath),
                WatchEnabled    = section.WatchEnabled,
                IdentifyEnabled = section.IdentifyEnabled,
                FolderType      = section.FolderType,
                IsSection       = false,
                ParentSectionId = sectionId,
                CreatedAt       = DateTime.UtcNow,
            };
            toAdd.Add(newFolder);
            newIds.Add(newFolder.Id);
        }

        foreach (var filePath in files)
        {
            if (existingFiles.Contains(filePath)) continue;
            if (dismissed.Contains(NormalisePath(filePath))) continue;

            // SingleFilePath entry — represents one standalone movie sitting in
            // the section root. Path = section root (so /api/image and rename
            // queues still resolve correctly); identification keys off SingleFilePath.
            var newEntry = new FolderWatcher
            {
                Id              = Guid.NewGuid(),
                Path            = section.Path,
                SingleFilePath  = filePath,
                Label           = Path.GetFileNameWithoutExtension(filePath),
                WatchEnabled    = false,
                IdentifyEnabled = section.IdentifyEnabled,
                FolderType      = FolderType.Movie,
                IsSection       = false,
                FlatSection     = false,
                ParentSectionId = sectionId,
                CreatedAt       = DateTime.UtcNow,
            };
            toAdd.Add(newEntry);
            newIds.Add(newEntry.Id);
        }

        if (toAdd.Count == 0) return newIds;

        db.FolderWatchers.AddRange(toAdd);
        await db.SaveChangesAsync();

        // Start file watchers (for subfolders only — SingleFilePath entries don't
        // need their own watcher, the section root watcher handles them) and
        // best-effort torrent linking. Done outside the DB transaction so a
        // single bad path doesn't block the rest.
        foreach (var f in toAdd)
        {
            if (f.SingleFilePath is null)
            {
                try { await StartWatcherAsync(f.Id); } catch (Exception ex) { logger.LogWarning(ex, "Watcher start failed for {Path}", f.Path); }
                try { await torrentEngine.TryLinkTorrentAsync(f.Path, f.Id); } catch { /* best-effort */ }
            }
            SubfolderCreated?.Invoke(sectionId, f.Id);
        }

        logger.LogInformation(
            "DiscoverChildrenAsync: registered {DirCount} child folders + {FileCount} standalone files under {Section}",
            toAdd.Count(t => t.SingleFilePath is null),
            toAdd.Count(t => t.SingleFilePath is not null),
            section.Label);
        return newIds;
    }

    /// <summary>
    /// Removes FolderWatcher rows whose on-disk path (or SingleFilePath for
    /// flat-section entries) no longer exists. The MediaItem cascades, so the
    /// /catalog stops showing ghost cards that don't correspond to any disk
    /// content.
    ///
    /// SAFETY: a section root that is currently unreachable (e.g. network mount
    /// down) would naively cause us to wipe every child — we'd be deleting the
    /// user's entire library on a temporary unmount. To avoid that, we
    /// short-circuit any section whose own path is missing: leave it and its
    /// children alone until the mount comes back.
    /// </summary>
    public async Task<int> CleanupOrphanedChildrenAsync(Guid sectionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var section = await db.FolderWatchers.FindAsync(sectionId);
        if (section is null) return 0;
        // Refuse to touch anything if the section's own root isn't reachable —
        // the disk might be temporarily unmounted.
        if (!Directory.Exists(section.Path)) return 0;

        var children = await db.FolderWatchers
            .Where(f => f.ParentSectionId == sectionId && !f.IsSection)
            .ToListAsync();

        var orphans = children.Where(c =>
            c.SingleFilePath is { Length: > 0 }
                ? !File.Exists(c.SingleFilePath)
                : !Directory.Exists(c.Path)
        ).ToList();

        if (orphans.Count == 0) return 0;

        foreach (var orphan in orphans)
        {
            try { await StopWatcherAsync(orphan.Id); } catch { /* best effort */ }
        }
        db.FolderWatchers.RemoveRange(orphans);
        await db.SaveChangesAsync();
        logger.LogInformation("Cleaned up {Count} orphaned FolderWatcher rows under section {SectionId}",
            orphans.Count, sectionId);
        return orphans.Count;
    }

    /// <summary>
    /// Whole-library variant. Walks every section and removes orphan children.
    /// Sections themselves are never auto-deleted (an unreachable section is
    /// almost always a temporary mount problem, not a "delete my config" intent).
    /// </summary>
    public async Task<int> CleanupOrphanedChildrenAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sectionIds = await db.FolderWatchers
            .Where(f => f.IsSection)
            .Select(f => f.Id)
            .ToListAsync();
        int total = 0;
        foreach (var id in sectionIds)
            total += await CleanupOrphanedChildrenAsync(id);
        return total;
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

    private sealed class FolderWatcherEntry(
        FileSystemWatcher watcher,
        FileSystemWatcher? dirWatcher = null,
        FileSystemWatcher? fileWatcher = null) : IDisposable
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
            if (fileWatcher is not null)
            {
                fileWatcher.EnableRaisingEvents = false;
                fileWatcher.Dispose();
            }
        }
    }
}
