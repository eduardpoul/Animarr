using Animarr.Shared;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Mapping;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for the user-defined collection tags shown as catalog tabs.
/// </summary>
internal static class MediaTagEndpoints
{
    public static IEndpointRouteBuilder MapMediaTagEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.MediaTags, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.MediaTags
                .Select(t => new { Tag = t, Count = t.Items.Count })
                .OrderBy(x => x.Tag.SortOrder)
                .ThenBy(x => x.Tag.Name)
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => r.Tag.ToDto(r.Count)).ToArray());
        });

        app.MapPost(ApiRoutes.MediaTags, async (
            UpsertMediaTagRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = new MediaTag
            {
                Id        = Guid.NewGuid(),
                Name      = request.Name,
                Color     = request.Color,
                SortOrder = request.SortOrder,
                IsAutoTag = request.IsAutoTag,
            };
            db.MediaTags.Add(entity);
            await db.SaveChangesAsync(ct);
            return Results.Created(ApiRoutes.MediaTag(entity.Id), entity.ToDto(0));
        });

        app.MapPut(ApiRoutes.MediaTagById, async (
            Guid id,
            UpsertMediaTagRequest request,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaTags
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();

            entity.Name      = request.Name;
            entity.Color     = request.Color;
            entity.SortOrder = request.SortOrder;
            entity.IsAutoTag = request.IsAutoTag;
            await db.SaveChangesAsync(ct);
            return Results.Ok(entity.ToDto(entity.Items.Count));
        });

        app.MapDelete(ApiRoutes.MediaTagById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var entity = await db.MediaTags.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null) return Results.NotFound();
            db.MediaTags.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost(ApiRoutes.MediaTagAssign, async (
            Guid tagId,
            Guid mediaItemId,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var exists = await db.MediaItemTags
                .AnyAsync(x => x.MediaItemId == mediaItemId && x.MediaTagId == tagId, ct);
            if (exists) return Results.NoContent();

            db.MediaItemTags.Add(new MediaItemTag { MediaItemId = mediaItemId, MediaTagId = tagId });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapDelete(ApiRoutes.MediaTagAssign, async (
            Guid tagId,
            Guid mediaItemId,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var link = await db.MediaItemTags
                .FirstOrDefaultAsync(x => x.MediaItemId == mediaItemId && x.MediaTagId == tagId, ct);
            if (link is null) return Results.NotFound();
            db.MediaItemTags.Remove(link);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}
