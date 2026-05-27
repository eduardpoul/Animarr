using System.Text.Json;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

public class RenameService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPatternMatchService matcher,
    TorrentEngineService torrentEngine,
    IAppConfigService appConfig,
    ILogger<RenameService> logger) : IRenameService
{
    // ─── B-1: shared effective-pattern/ignore-rule resolution ─────────────────

    private sealed record EffectiveRules(
        FolderWatcher Folder,
        List<RenamePattern> Patterns,
        List<IgnoreRule> IgnoreRules,
        IReadOnlyDictionary<(int s, int ep), string>? EpisodeNames);

    private sealed record SeasonMetaDto(int Number, string? Name, List<EpisodeMetaDto>? Episodes);
    private sealed record EpisodeMetaDto(int Number, string? Name);

    private async Task<EffectiveRules?> LoadEffectiveRulesAsync(
        AppDbContext db, Guid folderId, string? filePath, CancellationToken ct)
    {
        var folder = await db.FolderWatchers
            .Include(f => f.Patterns)
            .Include(f => f.IgnoreRules)
            .FirstOrDefaultAsync(f => f.Id == folderId, ct);

        if (folder is null) return null;

        // ─── Flat-section override ────────────────────────────────────────────
        // A section's FSW also fires for video files dropped directly in its
        // root. Those files are registered as SingleFilePath child watchers
        // (FolderType=Movie) by OnVideoFileCreated, but the rename queue is
        // populated by OnFileCreated which always uses the SECTION's folderId.
        // Without this swap, movie files inherit the section's FolderType=Auto
        // and get matched against TV-episode patterns (e.g. "Universal Episode
        // fallback" captures the year and renames Inception.2010.mkv → 2010.mkv).
        // Resolution: if we know the file path AND a SingleFilePath child of
        // this section matches it, evaluate using that child's rules.
        if (folder.IsSection && !string.IsNullOrEmpty(filePath))
        {
            var child = await db.FolderWatchers
                .Include(f => f.Patterns)
                .Include(f => f.IgnoreRules)
                .FirstOrDefaultAsync(f => f.ParentSectionId == folder.Id
                                       && f.SingleFilePath == filePath, ct);
            if (child is not null)
            {
                logger.LogDebug("Flat-section file routed to child watcher: {Path} (child {ChildId}, FolderType={Type})",
                    filePath, child.Id, child.FolderType);
                folder = child;
            }
        }

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
            .Where(p => isMovieFolder
                ? p.ApplicableTo == FolderType.Movie
                : p.ApplicableTo != FolderType.Movie)
            .OrderBy(p => p.Priority)
            .ToList();

        var globalIgnoreRules = await db.IgnoreRules
            .Where(r => r.Scope == RuleScope.Global && r.FolderId == null)
            .ToListAsync(ct);

        var effectiveIgnoreRules = globalIgnoreRules.Concat(folder.IgnoreRules).ToList();

        // Phase 1.3: episode-name map from MediaItem.SeasonsJson, if enabled and identified.
        IReadOnlyDictionary<(int, int), string>? episodeNames = null;
        var includeEpName = await appConfig.GetAsync<bool>(AppConfigKeys.IncludeEpisodeNameInFile, true, ct);
        if (includeEpName)
        {
            var media = await db.MediaItems
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.FolderId == folder.Id, ct);
            if (media is not null && !string.IsNullOrWhiteSpace(media.SeasonsJson))
                episodeNames = BuildEpisodeNameMap(media.SeasonsJson);
        }

        return new EffectiveRules(folder, effectivePatterns, effectiveIgnoreRules, episodeNames);
    }

    private static Dictionary<(int, int), string>? BuildEpisodeNameMap(string seasonsJson)
    {
        try
        {
            var seasons = JsonSerializer.Deserialize<List<SeasonMetaDto>>(seasonsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (seasons is null) return null;

            var map = new Dictionary<(int, int), string>();
            foreach (var s in seasons)
            {
                if (s.Episodes is null) continue;
                foreach (var ep in s.Episodes)
                {
                    if (!string.IsNullOrWhiteSpace(ep.Name))
                        map[(s.Number, ep.Number)] = ep.Name!;
                }
            }
            return map.Count > 0 ? map : null;
        }
        catch
        {
            return null;
        }
    }

    // ─── Scan (dry-run) ───────────────────────────────────────────────────────

    public async Task<List<RenamePreviewItem>> ScanFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rules = await LoadEffectiveRulesAsync(db, folderId, filePath: null, ct)
                    ?? throw new InvalidOperationException($"Folder {folderId} not found.");

        if (!Directory.Exists(rules.Folder.Path))
            throw new DirectoryNotFoundException($"Directory not found: {rules.Folder.Path}");

        // If we're scanning a section, preload SingleFilePath children so each flat
        // movie file is evaluated under its own (FolderType=Movie) rules instead
        // of inheriting the section's auto-rules.
        Dictionary<string, EffectiveRules>? flatChildRules = null;
        if (rules.Folder.IsSection)
        {
            var childIds = await db.FolderWatchers
                .Where(f => f.ParentSectionId == folderId && f.SingleFilePath != null)
                .Select(f => new { f.Id, f.SingleFilePath })
                .ToListAsync(ct);
            if (childIds.Count > 0)
            {
                flatChildRules = new Dictionary<string, EffectiveRules>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in childIds)
                {
                    if (string.IsNullOrEmpty(c.SingleFilePath)) continue;
                    var cr = await LoadEffectiveRulesAsync(db, c.Id, c.SingleFilePath, ct);
                    if (cr is not null) flatChildRules[c.SingleFilePath] = cr;
                }
            }
        }

        // Enumerate all files recursively (M-11: yields control to keep UI responsive)
        var activeDownloads = torrentEngine.GetActiveDownloadFilePaths();
        var incompleteFiles  = torrentEngine.GetIncompleteFilePaths();
        var files = Directory
            .EnumerateFiles(rules.Folder.Path, "*", SearchOption.AllDirectories)
            .Where(f => !activeDownloads.Contains(f) && !activeDownloads.Contains(f.TrimEnd('/'))
                     && !incompleteFiles.Contains(f)  && !incompleteFiles.Contains(f.TrimEnd('/')))
            .OrderBy(f => f, NaturalStringComparer.Ordinal);

        var results = new List<RenamePreviewItem>();
        int counter = 0;
        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var effective = (flatChildRules != null && flatChildRules.TryGetValue(filePath, out var cr))
                ? cr : rules;
            var item = matcher.EvaluateFile(filePath, effective.Patterns, effective.IgnoreRules,
                effective.Folder.FolderType, effective.Folder.IsSection, effective.Folder.Path,
                effective.EpisodeNames);
            results.Add(item);

            // M-11: yield every 200 items so a huge tree doesn't peg the request thread
            if ((++counter & 0xFF) == 0) await Task.Yield();
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

        // H-5: pair item + history explicitly so future filtering can't desync indices.
        var pairs = approved
            .Where(i => i.Status == PreviewStatus.WillRename && i.IsSelected)
            .Select(item => (item, history: new RenameHistory
            {
                Id           = Guid.NewGuid(),
                FolderId     = folderId,
                OriginalPath = item.OriginalPath,
                NewPath      = item.NewPath!,
                Status       = RenameStatus.Pending,
                ProcessedAt  = DateTime.UtcNow,
            }))
            .ToList();

        // Write all intentions as Pending before touching any file.
        // If the process crashes mid-run, SeedDataService.RecoverPendingHistoryAsync will resolve them on restart.
        db.RenameHistories.AddRange(pairs.Select(p => p.history));
        await db.SaveChangesAsync(ct);

        foreach (var (item, history) in pairs)
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

                    // Phase 3.5: rename paired subtitles / sidecar files that
                    // share the original baseName (e.g. video.eng.srt → newname.eng.srt).
                    RenamePairedSidecars(item.OriginalPath, item.NewPath!);
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

    /// <summary>
    /// Phase 3.5: when a video file is renamed, also rename any sidecar files
    /// (subtitles, NFO, JPG thumbs) in the same directory that share the
    /// same baseName — preserving language/track suffixes like ".eng.srt".
    /// </summary>
    private void RenamePairedSidecars(string originalPath, string newPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(originalPath);
            var newDir = Path.GetDirectoryName(newPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(newDir)) return;
            if (!Directory.Exists(dir)) return;

            // Use newDir if it differs (RenameService may not move across dirs, but stay safe).
            var oldBase = Path.GetFileNameWithoutExtension(originalPath);
            var newBase = Path.GetFileNameWithoutExtension(newPath);
            if (string.IsNullOrEmpty(oldBase) || string.IsNullOrEmpty(newBase) || oldBase == newBase)
                return;

            // We only touch sidecar extensions — never re-rename video files (they go through
            // the rename pipeline themselves).
            var sidecarExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".nfo", ".sup" };

            foreach (var sibling in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(sibling, originalPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var ext = Path.GetExtension(sibling);
                if (!sidecarExts.Contains(ext)) continue;

                var siblingBase = Path.GetFileNameWithoutExtension(sibling);
                if (string.IsNullOrEmpty(siblingBase)) continue;

                // Match: same baseName (video.srt) OR same baseName with a track suffix (video.eng.srt).
                string? suffix = null;
                if (string.Equals(siblingBase, oldBase, StringComparison.OrdinalIgnoreCase))
                {
                    suffix = "";
                }
                else if (siblingBase.StartsWith(oldBase + ".", StringComparison.OrdinalIgnoreCase))
                {
                    suffix = siblingBase[(oldBase.Length)..]; // includes leading dot
                }

                if (suffix is null) continue;

                var targetPath = Path.Combine(newDir, newBase + suffix + ext);
                if (string.Equals(sibling, targetPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(targetPath))
                {
                    logger.LogDebug("Sidecar target exists, skipping: {Path}", targetPath);
                    continue;
                }

                try
                {
                    File.Move(sibling, targetPath);
                    logger.LogInformation("Sidecar renamed: {Old} → {New}",
                        Path.GetFileName(sibling), Path.GetFileName(targetPath));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to rename sidecar {Path}", sibling);
                }
            }
        }
        catch (Exception ex)
        {
            // Sidecar renaming is best-effort — never fail the main rename because of it.
            logger.LogWarning(ex, "Sidecar pairing failed for {Path}", originalPath);
        }
    }

    // ─── Process single file (Watcher) ────────────────────────────────────────

    public async Task ProcessSingleFileAsync(string filePath, Guid folderId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rules = await LoadEffectiveRulesAsync(db, folderId, filePath, ct);
        if (rules is null) return;

        var item = matcher.EvaluateFile(filePath, rules.Patterns, rules.IgnoreRules,
            rules.Folder.FolderType, rules.Folder.IsSection, rules.Folder.Path,
            rules.EpisodeNames);

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

                // Phase 3.5: rename paired subtitles / sidecars alongside.
                RenamePairedSidecars(item.OriginalPath, item.NewPath!);
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
