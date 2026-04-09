using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

public class RenameService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPatternMatchService matcher,
    TorrentEngineService torrentEngine,
    ILogger<RenameService> logger) : IRenameService
{
    // ─── Scan (dry-run) ───────────────────────────────────────────────────────

    public async Task<List<RenamePreviewItem>> ScanFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var folder = await db.FolderWatchers
            .Include(f => f.Patterns)
            .Include(f => f.IgnoreRules)
            .FirstOrDefaultAsync(f => f.Id == folderId, ct)
            ?? throw new InvalidOperationException($"Folder {folderId} not found.");

        if (!Directory.Exists(folder.Path))
            throw new DirectoryNotFoundException($"Directory not found: {folder.Path}");

        // Merge global + folder-specific patterns (folder exclusions override globals)
        var globalPatterns = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);

        var folderPatterns = folder.Patterns;
        var excludedPatternIds = folderPatterns
            .Where(p => p.IsExcluded)
            .Select(p => p.Id)
            .ToHashSet();

        var effectivePatterns = globalPatterns
            .Where(p => !excludedPatternIds.Contains(p.Id))
            .Concat(folderPatterns.Where(p => !p.IsExcluded))
            .OrderBy(p => p.Priority)
            .ToList();

        // Filter patterns by folder type:
        // - Movie patterns (ApplicableTo == Movie) only run in Movie folders
        // - All other patterns (ApplicableTo == null/Series) don't run in Movie folders
        var isMovieFolder = folder.FolderType == FolderType.Movie;
        effectivePatterns = effectivePatterns
            .Where(p => isMovieFolder
                ? p.ApplicableTo == FolderType.Movie
                : p.ApplicableTo != FolderType.Movie)
            .ToList();

        // Global + folder-specific ignore rules
        var globalIgnoreRules = await db.IgnoreRules
            .Where(r => r.Scope == RuleScope.Global && r.FolderId == null)
            .ToListAsync(ct);

        var effectiveIgnoreRules = globalIgnoreRules
            .Concat(folder.IgnoreRules)
            .ToList();

        // Enumerate all files recursively
        var activeDownloads = torrentEngine.GetActiveDownloadFilePaths();
        var incompleteFiles  = torrentEngine.GetIncompleteFilePaths();
        var files = Directory
            .EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
            .Where(f => !activeDownloads.Contains(f) && !activeDownloads.Contains(f.TrimEnd('/'))
                     && !incompleteFiles.Contains(f)  && !incompleteFiles.Contains(f.TrimEnd('/')))
            .OrderBy(f => f);

        var results = new List<RenamePreviewItem>();
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
        var item = matcher.EvaluateFile(filePath, effectivePatterns, effectiveIgnoreRules,
            folder.FolderType, folder.IsSection, folder.Path);
            results.Add(item);
        }

        // Update LastScannedAt
        var tracked = await db.FolderWatchers.FindAsync([folderId], ct);
        if (tracked != null)
        {
            tracked.LastScannedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return results;
    }

    // ─── Apply renames ────────────────────────────────────────────────────────

    public async Task ApplyRenamesAsync(
        Guid folderId,
        IEnumerable<RenamePreviewItem> approved,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        foreach (var item in approved.Where(i => i.Status == PreviewStatus.WillRename && i.IsSelected))
        {
            ct.ThrowIfCancellationRequested();

            var historyEntry = new RenameHistory
            {
                Id = Guid.NewGuid(),
                FolderId = folderId,
                OriginalPath = item.OriginalPath,
                NewPath = item.NewPath!,
                ProcessedAt = DateTime.UtcNow,
            };

            try
            {
                if (!File.Exists(item.OriginalPath))
                {
                    historyEntry.Status = RenameStatus.Error;
                    historyEntry.ErrorMessage = "Source file no longer exists.";
                    db.RenameHistories.Add(historyEntry);
                    continue;
                }

                if (File.Exists(item.NewPath))
                {
                    historyEntry.Status = RenameStatus.Skipped;
                    historyEntry.ErrorMessage = "Target filename already exists.";
                    db.RenameHistories.Add(historyEntry);
                    continue;
                }

                File.Move(item.OriginalPath, item.NewPath!);
                historyEntry.Status = RenameStatus.Renamed;
                logger.LogInformation("Renamed: {Old} → {New}", item.OriginalName, item.NewName);                await torrentEngine.SetFileDoNotDownloadByAbsPathAsync(item.OriginalPath);            }
            catch (Exception ex)
            {
                historyEntry.Status = RenameStatus.Error;
                historyEntry.ErrorMessage = ex.Message;
                logger.LogError(ex, "Failed to rename {File}", item.OriginalPath);
            }

            db.RenameHistories.Add(historyEntry);
        }

        await db.SaveChangesAsync(ct);
    }

    // ─── Process single file (Watcher) ────────────────────────────────────────

    public async Task ProcessSingleFileAsync(string filePath, Guid folderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var folder = await db.FolderWatchers
            .Include(f => f.Patterns)
            .Include(f => f.IgnoreRules)
            .FirstOrDefaultAsync(f => f.Id == folderId, ct);

        if (folder is null) return;

        var globalPatterns = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);

        var excludedIds = folder.Patterns
            .Where(p => p.IsExcluded)
            .Select(p => p.Id)
            .ToHashSet();

        var effectivePatterns = globalPatterns
            .Where(p => !excludedIds.Contains(p.Id))
            .Concat(folder.Patterns.Where(p => !p.IsExcluded))
            .OrderBy(p => p.Priority)
            .ToList();

        var globalIgnoreRules = await db.IgnoreRules
            .Where(r => r.Scope == RuleScope.Global && r.FolderId == null)
            .ToListAsync(ct);

        var effectiveIgnoreRules = globalIgnoreRules.Concat(folder.IgnoreRules).ToList();

        var item = matcher.EvaluateFile(filePath, effectivePatterns, effectiveIgnoreRules);

        // Skip if the file is still being downloaded (not yet 100%)
        var incompleteFiles = torrentEngine.GetIncompleteFilePaths();
        if (incompleteFiles.Contains(filePath))
        {
            logger.LogDebug("[Watcher] Skipping rename — file not fully downloaded: {Path}", filePath);
            return;
        }

        var history = new RenameHistory
        {
            Id = Guid.NewGuid(),
            FolderId = folderId,
            OriginalPath = filePath,
            NewPath = item.NewPath ?? filePath,
            ProcessedAt = DateTime.UtcNow,
        };

        if (item.Status == PreviewStatus.WillRename)
        {
            try
            {
                if (File.Exists(item.NewPath))
                {
                    history.Status = RenameStatus.Skipped;
                    history.ErrorMessage = "Target filename already exists.";
                }
                else
                {
                    File.Move(item.OriginalPath, item.NewPath!);
                    history.Status = RenameStatus.Renamed;
                    logger.LogInformation("[Watcher] Renamed: {Old} → {New}", item.OriginalName, item.NewName);                    await torrentEngine.SetFileDoNotDownloadByAbsPathAsync(item.OriginalPath);                }
            }
            catch (Exception ex)
            {
                history.Status = RenameStatus.Error;
                history.ErrorMessage = ex.Message;
                logger.LogError(ex, "[Watcher] Failed to rename {File}", filePath);
            }
        }
        else
        {
            history.Status = RenameStatus.Skipped;
            history.ErrorMessage = item.Reason;
        }

        db.RenameHistories.Add(history);
        await db.SaveChangesAsync(ct);
    }

    // ─── Revert ───────────────────────────────────────────────────────────────

    public async Task<bool> RevertAsync(Guid historyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var record = await db.RenameHistories.FindAsync([historyId], ct);
        if (record is null || record.IsReverted || record.Status != RenameStatus.Renamed)
            return false;

        try
        {
            if (!File.Exists(record.NewPath))
            {
                logger.LogWarning("Revert failed — file not found: {Path}", record.NewPath);
                return false;
            }

            if (File.Exists(record.OriginalPath))
            {
                logger.LogWarning("Revert failed — original path already occupied: {Path}", record.OriginalPath);
                return false;
            }

            File.Move(record.NewPath, record.OriginalPath);

            record.IsReverted = true;
            record.Status = RenameStatus.Reverted;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Reverted: {New} → {Old}", record.NewPath, record.OriginalPath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Revert failed for history {Id}", historyId);
            return false;
        }
    }
}
