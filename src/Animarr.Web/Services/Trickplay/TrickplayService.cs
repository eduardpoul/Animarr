using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services.Segments;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Trickplay;

/// <summary>
/// Generates seek-preview sprite sheets (trickplay) for a title's files and
/// upserts their <see cref="TrickplayAsset"/> manifests. One ffmpeg pass per
/// file: keyframes only (<c>-skip_frame nokey</c>), one tile every
/// ~<see cref="MinIntervalSec"/>s letterboxed into a fixed
/// <see cref="TileWidth"/>×<see cref="TileHeight"/> cell, tiled into a single
/// JPEG — a whole 24-min episode costs seconds of decode and ~0.5 MB on disk.
///
/// Sprites land next to the media in <c>.animarr/&lt;folderId&gt;/trickplay/</c>
/// (the theme-music convention — keeps bulk assets off the Docker data volume)
/// and are served through the existing <c>/api/image</c> path whitelist.
/// </summary>
public sealed class TrickplayService(
    IDbContextFactory<AppDbContext> dbFactory,
    MediaFileResolver resolver,
    ILogger<TrickplayService> logger)
{
    public const int TileWidth  = 240;
    public const int TileHeight = 136;   // ~16:9 rounded to even for yuv420 JPEG
    public const int Cols       = 10;

    /// <summary>Sampling floor; long movies stretch the interval to stay under
    /// <see cref="MaxTiles"/> tiles so the sprite never grows past ~a few MB.</summary>
    private const int MinIntervalSec = 10;
    private const int MaxTiles       = 400;
    private const int MinDurationSec = 60;

    /// <summary>A stuck ffmpeg (dead NFS mount, corrupt file) is killed after
    /// this so it can't wedge the background queue.</summary>
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Generate (or refresh) sprites for every resolved on-disk file
    /// of the item. Skips files whose sprite is current (same source mtime).
    /// Returns the number of sprites actually generated.</summary>
    public async Task<int> GenerateForItemAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null) return 0;
        var folder = await db.FolderWatchers.AsNoTracking().FirstOrDefaultAsync(f => f.Id == item.FolderId, ct);
        if (folder is null) return 0;

        MediaFileDto[] files;
        try { files = await resolver.ResolveAsync(mediaItemId, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Trickplay] file resolve failed for {Id}", mediaItemId);
            return 0;
        }

        var existing = await db.TrickplayAssets
            .Where(a => a.MediaItemId == mediaItemId)
            .ToListAsync(ct);
        var byPath = existing.ToDictionary(a => a.FilePath, StringComparer.OrdinalIgnoreCase);

        int made = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(file.FilePath) || !File.Exists(file.FilePath)) continue;

            var writeTime = File.GetLastWriteTimeUtc(file.FilePath);
            byPath.TryGetValue(file.FilePath, out var row);
            if (row is not null && row.SourceWriteTimeUtc == writeTime && File.Exists(row.SpritePath))
            {
                // Sprite is current — just keep the (season, episode) lookup key
                // in step with the live mapping (manual overrides move files).
                if (row.Season != file.Season || row.Episode != file.Episode)
                {
                    row.Season  = file.Season;
                    row.Episode = file.Episode;
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            var fresh = await GenerateSpriteAsync(mediaItemId, folder, file, writeTime, ct);
            if (fresh is null) continue;

            if (row is null)
            {
                db.TrickplayAssets.Add(fresh);
                byPath[file.FilePath] = fresh;
            }
            else
            {
                row.Season             = fresh.Season;
                row.Episode            = fresh.Episode;
                row.SpritePath         = fresh.SpritePath;
                row.IntervalSec        = fresh.IntervalSec;
                row.TileWidth          = fresh.TileWidth;
                row.TileHeight         = fresh.TileHeight;
                row.Cols               = fresh.Cols;
                row.Rows               = fresh.Rows;
                row.Count              = fresh.Count;
                row.DurationSec        = fresh.DurationSec;
                row.SourceWriteTimeUtc = fresh.SourceWriteTimeUtc;
                row.GeneratedAtUtc     = fresh.GeneratedAtUtc;
            }
            made++;
            // Persist per file so a long season survives a mid-pass restart.
            await db.SaveChangesAsync(ct);
        }

        if (made > 0)
            logger.LogInformation("[Trickplay] generated {Count} sprite(s) for {Title}", made, item.Title);
        return made;
    }

    /// <summary>Run ffmpeg for one file and return its manifest row (unsaved),
    /// or null when the file is too short / unreadable / ffmpeg failed.</summary>
    private async Task<TrickplayAsset?> GenerateSpriteAsync(
        Guid mediaItemId, FolderWatcher folder, MediaFileDto file, DateTime writeTime, CancellationToken ct)
    {
        var duration = await MediaProbe.GetDurationAsync(file.FilePath, ct);
        if (duration < MinDurationSec) return null;

        var interval = Math.Max(MinIntervalSec, (int)Math.Ceiling(duration / MaxTiles));
        var count    = Math.Max(1, (int)Math.Ceiling(duration / interval));
        var rows     = (count + Cols - 1) / Cols;

        var dir = AssetDir(folder);
        if (dir is null) return null;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Trickplay] can't create {Dir} (read-only media mount?)", dir);
            return null;
        }
        var sprite = Path.Combine(dir, SpriteFileName(file));

        // Keyframes only: decode cost stays in seconds even on NAS CPUs. The
        // ±GOP timestamp jitter doesn't matter for a 240px preview thumb.
        // The quoted select expression keeps its comma out of the filtergraph
        // splitter; letterbox into a fixed even-sized cell so the client's
        // background-position math needs no per-file geometry.
        var vf = $"select='isnan(prev_selected_t)+gte(t-prev_selected_t,{interval})'," +
                 $"scale={TileWidth}:{TileHeight}:force_original_aspect_ratio=decrease," +
                 $"pad={TileWidth}:{TileHeight}:(ow-iw)/2:(oh-ih)/2,tile={Cols}x{rows}";
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[]
        {
            "-nostdin", "-v", "error",
            "-skip_frame", "nokey",
            "-i", file.FilePath,
            "-an", "-sn", "-dn",
            "-vf", vf,
            "-frames:v", "1",
            "-q:v", "4",
            "-y", sprite,
        }) psi.ArgumentList.Add(a);

        try
        {
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi);
            if (p is null) return null;
            // Never fight playback/transcode for CPU.
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* platform-dependent */ }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FfmpegTimeout);
            // Drain stderr so a chatty ffmpeg can't fill the pipe and deadlock.
            var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("[Trickplay] ffmpeg timed out on {File}", Path.GetFileName(file.FilePath));
                return null;
            }

            if (p.ExitCode != 0 || !File.Exists(sprite))
            {
                var stderr = await stderrTask;
                logger.LogDebug("[Trickplay] ffmpeg exit {Code} for {File}: {Err}",
                    p.ExitCode, Path.GetFileName(file.FilePath), Truncate(stderr, 400));
                return null;
            }

            logger.LogDebug("[Trickplay] {File} → {Tiles} tiles in {Ms} ms",
                Path.GetFileName(file.FilePath), count, sw.ElapsedMilliseconds);
            return new TrickplayAsset
            {
                Id                 = Guid.NewGuid(),
                MediaItemId        = mediaItemId,
                Season             = file.Season,
                Episode            = file.Episode,
                FilePath           = file.FilePath,
                SpritePath         = sprite,
                IntervalSec        = interval,
                TileWidth          = TileWidth,
                TileHeight         = TileHeight,
                Cols               = Cols,
                Rows               = rows,
                Count              = count,
                DurationSec        = duration,
                SourceWriteTimeUtc = writeTime,
                GeneratedAtUtc     = DateTime.UtcNow,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Trickplay] sprite generation failed for {File}", file.FilePath);
            return null;
        }
    }

    /// <summary>Media-adjacent asset dir — <c>.animarr/&lt;folderId&gt;/trickplay/</c>
    /// next to the media, mirroring MetadataService.ThemeDir.</summary>
    private static string? AssetDir(FolderWatcher folder)
    {
        var baseDir = folder.SingleFilePath is { Length: > 0 } file
            ? Path.GetDirectoryName(file)
            : folder.Path;
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        return Path.Combine(baseDir, ".animarr", folder.Id.ToString("N"), "trickplay");
    }

    /// <summary>Readable, collision-safe sprite name: episode hint for humans,
    /// path hash for uniqueness (alt versions of the same episode differ).</summary>
    private static string SpriteFileName(MediaFileDto file)
    {
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(file.FilePath.ToLowerInvariant())))[..10]
            .ToLowerInvariant();
        return file.Episode is int e
            ? $"s{file.Season ?? 1:00}e{e:000}-{hash}.jpg"
            : $"movie-{hash}.jpg";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
