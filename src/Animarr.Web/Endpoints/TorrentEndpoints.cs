using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for the torrent engine. The engine identifies torrents by
/// info-hash internally; the API uses the DB-row Guid, so most endpoints
/// look up the record first and forward the InfoHash.
///
/// Add endpoints compute the savePath server-side from the chosen folder
/// (or a sensible default), keeping the request DTO minimal for clients.
/// </summary>
internal static class TorrentEndpoints
{
    public static IEndpointRouteBuilder MapTorrentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Torrents, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.TorrentRecords
                .Include(t => t.FileSelections)
                .OrderByDescending(t => t.AddedAt)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapGet(ApiRoutes.TorrentById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.TorrentRecords
                .Include(t => t.FileSelections)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(row.ToDto());
        });

        app.MapPost(ApiRoutes.TorrentAddMagnet, async (
            AddMagnetRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.MagnetLink))
                return Results.BadRequest("MagnetLink is required.");

            var savePath = await ResolveSavePathAsync(dbFactory, request.FolderWatcherId, ct);

            var infoHash = await engine.AddMagnetAsync(
                request.MagnetLink.Trim(),
                savePath,
                request.FolderWatcherId,
                // v5: clients can now cap the per-torrent rate from the Add
                // drawer. MonoTorrent treats 0 as "no limit". The engine
                // stores these on the record so they survive restart.
                dlLimit: Math.Max(0, request.DownloadLimitKBps) * 1024,
                ulLimit: Math.Max(0, request.UploadLimitKBps)   * 1024,
                startPaused: false,
                stopAfterDownload: request.StopAfterDownload,
                flattenSubfolders: request.SkipSubfolderStructure,
                suppressRootFolder: request.SuppressRootFolder,
                customRootFolderName: request.CustomRootFolderName);

            var record = await LoadByInfoHashAsync(dbFactory, infoHash, ct);
            return record is null
                ? Results.Accepted()
                : Results.Created(ApiRoutes.Torrent(record.Id), record.ToDto());
        });

        app.MapPost(ApiRoutes.TorrentAddFile, async (
            AddTorrentFileRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Base64Content))
                return Results.BadRequest("Base64Content is required.");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.Base64Content); }
            catch { return Results.BadRequest("Invalid Base64."); }

            var savePath = await ResolveSavePathAsync(dbFactory, request.FolderWatcherId, ct);

            // Map the drawer's unchecked files to DoNotDownload (priority 0).
            // Keys are the manifest paths (MonoTorrent Torrent.Files[].Path), so
            // they match the engine's mgr.Files[].Path lookup exactly. Applied
            // before StartAsync, so skipped files never download.
            Dictionary<string, int>? initialPriorities = null;
            if (request.ExcludedFiles is { Length: > 0 } excluded)
                initialPriorities = excluded
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct()
                    .ToDictionary(p => p, _ => 0);

            var infoHash = await engine.AddTorrentFileAsync(
                bytes,
                savePath,
                request.FolderWatcherId,
                // Same rate-limit plumbing as add-magnet (v5).
                dlLimit: Math.Max(0, request.DownloadLimitKBps) * 1024,
                ulLimit: Math.Max(0, request.UploadLimitKBps)   * 1024,
                startPaused: false,
                stopAfterDownload: request.StopAfterDownload,
                initialPriorities: initialPriorities,
                flattenSubfolders: request.SkipSubfolderStructure,
                suppressRootFolder: request.SuppressRootFolder,
                customRootFolderName: request.CustomRootFolderName);

            var record = await LoadByInfoHashAsync(dbFactory, infoHash, ct);
            return record is null
                ? Results.Accepted()
                : Results.Created(ApiRoutes.Torrent(record.Id), record.ToDto());
        });

        app.MapPut(ApiRoutes.TorrentById, async (
            Guid id,
            UpdateTorrentRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentRecords.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();

            entity.FolderWatcherId        = request.FolderWatcherId;
            entity.StopAfterDownload      = request.StopAfterDownload;
            entity.DownloadLimit          = request.DownloadLimit;
            entity.UploadLimit            = request.UploadLimit;
            entity.StopSeedingRatio       = request.StopSeedingRatio;
            entity.SkipSubfolderStructure = request.SkipSubfolderStructure;
            entity.SuppressRootFolder     = request.SuppressRootFolder;
            entity.CustomRootFolderName   = request.CustomRootFolderName;
            await db.SaveChangesAsync(ct);

            await engine.SetStopAfterDownloadAsync(entity.InfoHash, request.StopAfterDownload);
            await engine.SetSpeedLimitsAsync(entity.InfoHash, request.DownloadLimit, request.UploadLimit);
            return Results.Ok(entity.ToDto());
        });

        app.MapDelete(ApiRoutes.TorrentById, async (
            Guid id,
            bool? deleteFiles,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentRecords.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();

            // Engine cleanup (stop + drop from MonoTorrent, optionally delete the
            // downloaded files). For a torrent the engine isn't tracking anymore
            // — e.g. a "Stopped" row left over after a restart, the "broken
            // download" the user wants gone — RemoveAsync early-returns and, even
            // for live torrents, only marks the row Stopped. So the record never
            // left the list and Delete looked dead. HARD-delete the row here so
            // the trash button always removes it regardless of engine state.
            await engine.RemoveAsync(entity.InfoHash, deleteFiles ?? false);
            db.TorrentRecords.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost(ApiRoutes.TorrentPause, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentRecords.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();
            await engine.PauseAsync(entity.InfoHash);
            return Results.Accepted();
        });

        app.MapPost(ApiRoutes.TorrentResume, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentRecords.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();
            await engine.ResumeAsync(entity.InfoHash);
            return Results.Accepted();
        });

        // File tree is built from MonoTorrent's metadata cache held by the
        // engine; we re-shape it into the DTO recursively.
        app.MapGet(ApiRoutes.TorrentFileTree, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentRecords
                .Include(t => t.FileSelections)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();

            // Build a flat list of (path, size) — file tree shape is rebuilt
            // client-side from selections + priorities since the engine's
            // running-manager file list isn't exposed as a public API yet.
            var leaves = entity.FileSelections
                .Select(s => new TorrentFileNodeDto
                {
                    Path     = s.FilePath,
                    Name     = Path.GetFileName(s.FilePath),
                    IsDir    = false,
                    Size     = 0,
                    Depth    = s.FilePath.Count(c => c is '/' or '\\'),
                    Priority = s.Priority,
                })
                .ToArray();

            return Results.Ok(new TorrentFileNodeDto
            {
                Path = string.Empty,
                Name = entity.Name,
                IsDir = true,
                Children = leaves.ToList(),
            });
        });

        app.MapPut(ApiRoutes.TorrentFileSelections, async (
            Guid id,
            UpdateFileSelectionsRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentRecords.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();

            var priorities = request.Selections.ToDictionary(s => s.FilePath, s => s.Priority);
            await engine.SetFilePrioritiesBatchAsync(entity.InfoHash, priorities);
            return Results.NoContent();
        });

        app.MapGet(ApiRoutes.TorrentConfig, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var cfg = await db.TorrentConfig.FirstOrDefaultAsync(c => c.Id == 1, ct)
                ?? new TorrentConfig();
            return Results.Ok(cfg.ToDto());
        });

        app.MapPut(ApiRoutes.TorrentConfig, async (
            TorrentConfigDto cfg,
            IDbContextFactory<AppDbContext> dbFactory,
            TorrentEngineService engine,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.TorrentConfig.FirstOrDefaultAsync(c => c.Id == 1, ct);
            if (entity is null)
            {
                entity = new TorrentConfig { Id = 1 };
                db.TorrentConfig.Add(entity);
            }
            entity.GlobalDownloadLimit       = cfg.GlobalDownloadLimit;
            entity.GlobalUploadLimit         = cfg.GlobalUploadLimit;
            entity.ListenPort                = cfg.ListenPort;
            entity.MaxConnections            = cfg.MaxConnections;
            entity.EnableDHT                 = cfg.EnableDHT;
            entity.EnableLSD                 = cfg.EnableLSD;
            entity.EnableUPnP                = cfg.EnableUPnP;
            entity.StopSeedingAfterDone      = cfg.StopSeedingAfterDone;
            entity.StopSeedingRatio          = cfg.StopSeedingRatio;
            entity.DefaultStopAfterDownload  = cfg.DefaultStopAfterDownload;
            entity.CacheDirectory            = cfg.CacheDirectory;
            await db.SaveChangesAsync(ct);

            await engine.UpdateGlobalSettingsAsync(entity);
            return Results.Ok(entity.ToDto());
        });

        // v5: parse-only endpoint. Lets the Add drawer preview the file list
        // inside a .torrent before the user commits, without spinning up a
        // TorrentManager. Uses the static helper TorrentEngineService.
        // ParseTorrentFilesAsync so the engine doesn't get touched.
        app.MapPost(ApiRoutes.TorrentParse, async (
            ParseTorrentRequest request,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Base64Content))
                return Results.BadRequest("Base64Content is required.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(request.Base64Content); }
            catch { return Results.BadRequest("Invalid Base64."); }

            try
            {
                var torrent = await MonoTorrent.Torrent.LoadAsync(bytes);
                var files = torrent.Files
                    .Select(f => new ParsedTorrentFileDto(f.Path, f.Length))
                    .ToArray();
                return Results.Ok(new ParsedTorrentDto(
                    Name:      torrent.Name,
                    TotalSize: torrent.Size,
                    Files:     files));
            }
            catch (Exception ex)
            {
                return Results.BadRequest($"Could not parse .torrent: {ex.Message}");
            }
        });

        return app;

        // ─── Helpers ─────────────────────────────────────────────────────

        static async Task<string> ResolveSavePathAsync(
            IDbContextFactory<AppDbContext> dbFactory, Guid? folderId, CancellationToken ct)
        {
            if (folderId is null) return "/app/data/downloads";
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var folder = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == folderId.Value, ct);
            return folder?.Path ?? "/app/data/downloads";
        }

        static async Task<TorrentRecord?> LoadByInfoHashAsync(
            IDbContextFactory<AppDbContext> dbFactory, string infoHash, CancellationToken ct)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.TorrentRecords
                .Include(t => t.FileSelections)
                .FirstOrDefaultAsync(t => t.InfoHash == infoHash, ct);
        }
    }
}
