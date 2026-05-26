using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Server-side equivalent of the episode-to-file mapping that the original
/// MediaDetail.razor did inline. Hands the API the same answer the Razor
/// page used to compute locally — file paths bucketed by (season, episode)
/// when a pattern matches, plus unmatched leftovers so the user can pick.
///
/// Lighter than the original (no LLM fallback, no fuzzy season-name
/// matching against TMDB) — those refinements stay in the Razor server
/// page until the API surface grows. Pattern-based extraction covers the
/// 95% case.
/// </summary>
public sealed class MediaFileResolver
{
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts",
    };

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IPatternMatchService _patterns;

    public MediaFileResolver(IDbContextFactory<AppDbContext> dbFactory, IPatternMatchService patterns)
    {
        _dbFactory = dbFactory;
        _patterns  = patterns;
    }

    public async Task<MediaFileDto[]> ResolveAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems
            .Include(m => m.Folder)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return Array.Empty<MediaFileDto>();

        var folder = item.Folder;
        if (folder is null) return Array.Empty<MediaFileDto>();

        // Flat-section single-file rows store one absolute path directly.
        if (!string.IsNullOrEmpty(folder.SingleFilePath))
        {
            if (!File.Exists(folder.SingleFilePath)) return Array.Empty<MediaFileDto>();
            var fi = new FileInfo(folder.SingleFilePath);
            return new[] { new MediaFileDto(fi.FullName, fi.Name, null, null, fi.Length) };
        }

        if (string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path))
            return Array.Empty<MediaFileDto>();

        // Effective pattern set: globals minus folder-excluded + folder-local.
        var globalPatterns = await db.RenamePatterns
            .Where(p => p.Scope == PatternScope.Global)
            .ToListAsync(ct);
        var folderWithPatterns = await db.FolderWatchers
            .Include(f => f.Patterns)
            .FirstAsync(f => f.Id == folder.Id, ct);

        var excludedIds = folderWithPatterns.Patterns
            .Where(p => p.IsExcluded && p.GlobalPatternId.HasValue)
            .Select(p => p.GlobalPatternId!.Value)
            .ToHashSet();
        bool isMovie = folder.FolderType == FolderType.Movie;
        var effectivePatterns = globalPatterns
            .Where(p => !excludedIds.Contains(p.Id))
            .Concat(folderWithPatterns.Patterns.Where(p => !p.IsExcluded))
            .Where(p => p.ApplicableTo is null ||
                       (isMovie ? p.ApplicableTo == FolderType.Movie : p.ApplicableTo != FolderType.Movie))
            .OrderBy(p => p.Priority)
            .ToList();

        var results = new List<MediaFileDto>();
        foreach (var filePath in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
        {
            if (!VideoExts.Contains(Path.GetExtension(filePath))) continue;
            var fileName = Path.GetFileName(filePath);
            var parse    = _patterns.ParseFileName(fileName, effectivePatterns);
            var fileDir  = Path.GetDirectoryName(filePath) ?? folder.Path;

            int? season = parse.Season
                ?? _patterns.DetectSeasonFromPath(fileDir, folder.Path);
            int? episode = parse.IsMatched ? parse.Episode : null;

            // Movies have no season/episode bucket — leave both null.
            if (isMovie) { season = null; episode = null; }

            FileInfo fi;
            try { fi = new FileInfo(filePath); } catch { continue; }
            results.Add(new MediaFileDto(filePath, fileName, season, episode, fi.Length));
        }

        // Sort: by season → episode → filename so the catalog renders in a
        // predictable order regardless of how the OS returned directory entries.
        return results
            .OrderBy(f => f.Season ?? int.MaxValue)
            .ThenBy(f => f.Episode ?? int.MaxValue)
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
