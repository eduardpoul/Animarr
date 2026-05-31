using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for filename-parsing patterns + the identification queue.
/// These feed the Parsing settings tab and the AI identification popup.
/// </summary>
internal static class RenameEndpoints
{
    public static IEndpointRouteBuilder MapRenameEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Patterns ────────────────────────────────────────────────────
        app.MapGet(ApiRoutes.Patterns, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.RenamePatterns
                .OrderBy(p => p.Priority)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapPost(ApiRoutes.Patterns, async (
            UpsertPatternRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = new RenamePattern
            {
                Id              = Guid.NewGuid(),
                Name            = request.Name,
                Pattern         = request.Pattern,
                Scope           = (Animarr.Web.Data.Models.PatternScope)(int)request.Scope,
                IsExcluded      = request.IsExcluded,
                GlobalPatternId = request.GlobalPatternId,
                Priority        = request.Priority,
                ApplicableTo    = request.ApplicableTo is null
                    ? null
                    : (Animarr.Web.Data.Models.FolderType)(int)request.ApplicableTo.Value,
                FolderId        = request.FolderId,
            };
            db.RenamePatterns.Add(entity);
            await db.SaveChangesAsync(ct);
            return Results.Created(ApiRoutes.Pattern(entity.Id), entity.ToDto());
        });

        app.MapPut(ApiRoutes.PatternById, async (
            Guid id,
            UpsertPatternRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.RenamePatterns.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (entity is null) return Results.NotFound();
            if (entity.IsBuiltIn) return Results.Forbid();

            entity.Name            = request.Name;
            entity.Pattern         = request.Pattern;
            entity.Scope           = (Animarr.Web.Data.Models.PatternScope)(int)request.Scope;
            entity.IsExcluded      = request.IsExcluded;
            entity.GlobalPatternId = request.GlobalPatternId;
            entity.Priority        = request.Priority;
            entity.ApplicableTo    = request.ApplicableTo is null
                ? null
                : (Animarr.Web.Data.Models.FolderType)(int)request.ApplicableTo.Value;
            entity.FolderId        = request.FolderId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(entity.ToDto());
        });

        app.MapDelete(ApiRoutes.PatternById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.RenamePatterns.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (entity is null) return Results.NotFound();
            if (entity.IsBuiltIn) return Results.Forbid();
            db.RenamePatterns.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ─── Identification queue ────────────────────────────────────────
        app.MapGet(ApiRoutes.IdentificationQueue, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.IdentificationQueues
                .Include(q => q.Folder)
                .OrderByDescending(q => q.QueuedAt)
                .Take(200)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.ToDto()).ToArray());
        });

        app.MapPost(ApiRoutes.IdentificationEnqueue, async (
            Guid folderId,
            bool? forceRefresh,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (!await db.FolderWatchers.AnyAsync(f => f.Id == folderId, ct))
                return Results.NotFound();

            var entity = new IdentificationQueue
            {
                Id           = Guid.NewGuid(),
                FolderId     = folderId,
                Status       = Animarr.Web.Data.Models.IdentificationQueueStatus.Queued,
                ForceRefresh = forceRefresh ?? false,
                QueuedAt     = DateTime.UtcNow,
            };
            db.IdentificationQueues.Add(entity);
            await db.SaveChangesAsync(ct);

            // The processor polls every few seconds; no manual nudge needed.
            return Results.Created($"{ApiRoutes.IdentificationQueue}/{entity.Id}", entity.ToDto());
        });

        app.MapPost(ApiRoutes.IdentificationCancel, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.IdentificationQueues.FirstOrDefaultAsync(q => q.Id == id, ct);
            if (entity is null) return Results.NotFound();
            if (entity.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Processing)
                return Results.Conflict("Already processing — wait for completion.");
            db.IdentificationQueues.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ─── Pause / resume / status (AI popup controls) ─────────────────
        // The processor is a singleton hosted service, so we can flip its in-memory
        // pause flag directly. Pausing only blocks picking up the NEXT job.
        app.MapPost(ApiRoutes.IdentificationPause, (IdentificationQueueProcessorService proc) =>
        {
            proc.SetPaused(true);
            return Results.NoContent();
        });
        app.MapPost(ApiRoutes.IdentificationResume, (IdentificationQueueProcessorService proc) =>
        {
            proc.SetPaused(false);
            return Results.NoContent();
        });
        app.MapGet(ApiRoutes.IdentificationStatus, (IdentificationQueueProcessorService proc) =>
            Results.Ok(new Animarr.Shared.Models.IdentificationQueueStatusDto(
                proc.IsPaused, proc.QueueDepth, proc.QueueProcessedSinceStart, proc.HitRate)));

        return app;
    }
}
