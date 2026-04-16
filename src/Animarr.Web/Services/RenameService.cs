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
        var excludedGlobalPatternIds = folderPatterns
            .Where(p => p.IsExcluded && p.GlobalPatternId.HasValue)
            .Select(p => p.GlobalPatternId!.Value)
            .ToHashSet();

        var effectivePatterns = globalPatterns
            .Where(p => !excludedGlobalPatternIds.Contains(p.Id))
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
            .OrderBy(f => f, NaturalStringComparer.Ordinal);

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
        var toProcess = approved.Where(i => i.Status == PreviewStatus.WillRename && i.IsSelected).ToList();

        // Write all intentions as Pending before touching any file.
        // If the process crashes mid-run, SeedDataService.RecoverPendingHistoryAsync will resolve them on restart.
        var histories = toProcess.Select(item => new RenameHistory
        {
            Id           = Guid.NewGuid(),
            FolderId     = folderId,
            OriginalPath = item.OriginalPath,
            NewPath      = item.NewPath!,
            Status       = RenameStatus.Pending,
            ProcessedAt  = DateTime.UtcNow,
        }).ToList();
        db.RenameHistories.AddRange(histories);
        await db.SaveChangesAsync(ct);

        foreach (var (item, history) in toProcess.Zip(histories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(item.OriginalPath))
                {
                    history.Status = RenameStatus.Error;
                    history.ErrorMessage = "Source file no longer exists.";
                }
                else if (File.Exists(item.NewPath))
                {
                    history.Status = RenameStatus.Skipped;
                    history.ErrorMessage = "Target filename already exists.";
                }
                else
                {
                    var moved = await torrentEngine.MoveFileAsync(item.OriginalPath, item.NewPath!);
                    if (!moved) File.Move(item.OriginalPath, item.NewPath!);
                    history.Status = RenameStatus.Renamed;
                    logger.LogInformation("Renamed: {Old} → {New}", item.OriginalName, item.NewName);
                }
            }
            catch (Exception ex)
            {
                history.Status = RenameStatus.Error;
                history.ErrorMessage = ex.Message;
                logger.LogError(ex, "Failed to rename {File}", item.OriginalPath);
            }
            await db.SaveChangesAsync(ct);
        }
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

        var excludedGlobalIds = folder.Patterns
            .Where(p => p.IsExcluded && p.GlobalPatternId.HasValue)
            .Select(p => p.GlobalPatternId!.Value)
            .ToHashSet();

        var isMovieFolder = folder.FolderType == FolderType.Movie;

        var effectivePatterns = globalPatterns
            .Where(p => !excludedGlobalIds.Contains(p.Id))
            .Concat(folder.Patterns.Where(p => !p.IsExcluded))
            .OrderBy(p => p.Priority)
            .Where(p => isMovieFolder
                ? p.ApplicableTo == FolderType.Movie
                : p.ApplicableTo != FolderType.Movie)
            .ToList();

        var globalIgnoreRules = await db.IgnoreRules
            .Where(r => r.Scope == RuleScope.Global && r.FolderId == null)
            .ToListAsync(ct);

        var effectiveIgnoreRules = globalIgnoreRules.Concat(folder.IgnoreRules).ToList();

        var item = matcher.EvaluateFile(filePath, effectivePatterns, effectiveIgnoreRules,
            folder.FolderType, folder.IsSection, folder.Path);

        // Skip if the file is still being downloaded (not yet 100%)
        var incompleteFiles = torrentEngine.GetIncompleteFilePaths();
        if (incompleteFiles.Contains(filePath))
        {
            logger.LogDebug("[Watcher] Skipping rename — file not fully downloaded: {Path}", filePath);
            return;
        }

        if (item.Status != PreviewStatus.WillRename)
        {
            // Not going to rename — only write history for non-Skip outcomes to avoid noise
            return;
        }

        // Write Pending intent before touching the file
        var history = new RenameHistory
        {
            Id           = Guid.NewGuid(),
            FolderId     = folderId,
            OriginalPath = filePath,
            NewPath      = item.NewPath!,
            Status       = RenameStatus.Pending,
            ProcessedAt  = DateTime.UtcNow,
        };
        db.RenameHistories.Add(history);
        await db.SaveChangesAsync(ct);

        try
        {
            if (File.Exists(item.NewPath))
            {
                history.Status = RenameStatus.Skipped;
                history.ErrorMessage = "Target filename already exists.";
            }
            else
            {
                var moved = await torrentEngine.MoveFileAsync(item.OriginalPath, item.NewPath!);
                if (!moved) File.Move(item.OriginalPath, item.NewPath!);
                history.Status = RenameStatus.Renamed;
                logger.LogInformation("[Watcher] Renamed: {Old} → {New}", item.OriginalName, item.NewName);
            }
        }
        catch (Exception ex)
        {
            history.Status = RenameStatus.Error;
            history.ErrorMessage = ex.Message;
            logger.LogError(ex, "[Watcher] Failed to rename {File}", filePath);
        }

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
