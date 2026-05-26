using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for managing FolderWatchers — the user's library roots.
/// Mirrors the operations the Settings → Root folders panel performs, so
/// the UI can be moved to the WASM client without losing parity.
/// </summary>
internal static class FolderEndpoints
{
    public static IEndpointRouteBuilder MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Folders, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.FolderWatchers
                .OrderBy(f => f.Label)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapGet(ApiRoutes.FolderById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(row.ToDto());
        });

        // NOTE: the watcher service auto-discovers FolderWatcher rows via DB polling
        // / FileSystemWatcher events. Endpoint handlers persist the row and the
        // watcher picks it up on the next scan; we call StartWatcherAsync/StopWatcherAsync
        // for an immediate effect rather than waiting for the poll.
        app.MapPost(ApiRoutes.Folders, async (
            CreateFolderRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest("Path is required.");

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var entity = new FolderWatcher
            {
                Id              = Guid.NewGuid(),
                Path            = request.Path,
                Label           = string.IsNullOrWhiteSpace(request.Label) ? Path.GetFileName(request.Path.TrimEnd('/', '\\')) : request.Label,
                FolderType      = (Animarr.Web.Data.Models.FolderType)(int)request.FolderType,
                WatchEnabled    = request.WatchEnabled,
                RenameEnabled   = request.RenameEnabled,
                IdentifyEnabled = request.IdentifyEnabled,
                IsSection       = request.IsSection,
                FlatSection     = request.FlatSection,
                Hue             = request.Hue,
                BackdropPath    = request.BackdropPath,
                CreatedAt       = DateTime.UtcNow,
            };

            db.FolderWatchers.Add(entity);
            await db.SaveChangesAsync(ct);

            // Spin up the FileSystemWatcher right away so the user doesn't have to
            // wait for the next poll cycle to see new files appear.
            await watcher.StartWatcherAsync(entity.Id);

            return Results.Created(ApiRoutes.Folder(entity.Id), entity.ToDto());
        });

        app.MapPut(ApiRoutes.FolderById, async (
            Guid id,
            UpdateFolderRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (entity is null) return Results.NotFound();

            entity.Label           = request.Label;
            entity.FolderType      = (Animarr.Web.Data.Models.FolderType)(int)request.FolderType;
            entity.WatchEnabled    = request.WatchEnabled;
            entity.RenameEnabled   = request.RenameEnabled;
            entity.IdentifyEnabled = request.IdentifyEnabled;
            entity.Hue             = request.Hue;
            entity.BackdropPath    = request.BackdropPath;
            await db.SaveChangesAsync(ct);

            // Toggle the watcher to pick up the new WatchEnabled state immediately.
            if (request.WatchEnabled) await watcher.StartWatcherAsync(id);
            else                      await watcher.StopWatcherAsync(id);
            return Results.Ok(entity.ToDto());
        });

        app.MapDelete(ApiRoutes.FolderById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (entity is null) return Results.NotFound();

            // Cascade to any children of a section we owned.
            if (entity.IsSection)
            {
                var children = await db.FolderWatchers
                    .Where(f => f.ParentSectionId == id)
                    .ToListAsync(ct);
                db.FolderWatchers.RemoveRange(children);
            }
            db.FolderWatchers.Remove(entity);
            await db.SaveChangesAsync(ct);

            await watcher.StopWatcherAsync(id);
            return Results.NoContent();
        });

        app.MapPost(ApiRoutes.FolderScan, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            FolderWatcherService watcher,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.FolderWatchers.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (entity is null) return Results.NotFound();

            // Section roots re-discover children; ordinary folders just restart the watcher.
            if (entity.IsSection)
                await watcher.DiscoverChildrenAsync(id);
            else
                await watcher.StartWatcherAsync(id);
            return Results.Accepted();
        });

        app.MapGet(ApiRoutes.FolderChildren, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.FolderWatchers
                .Where(f => f.ParentSectionId == id)
                .OrderBy(f => f.Label)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapGet(ApiRoutes.SectionFolders, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.FolderWatchers
                .Where(f => f.IsSection)
                .OrderBy(f => f.Label)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        return app;
    }
}
