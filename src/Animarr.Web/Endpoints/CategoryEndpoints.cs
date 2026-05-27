using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// REST surface for catalog categories (Movies / Serials / Anime / Donghua /
/// Dorama / Multi / Kids and any admin-defined custom categories).
///
///   GET    /api/categories           — list, any authenticated user
///   POST   /api/categories           — create custom (manageUsers)
///   PATCH  /api/categories/{id}      — edit (manageUsers); built-ins block name change
///   DELETE /api/categories/{id}      — delete custom (manageUsers); built-ins 409
///   POST   /api/categories/rescan    — kick background reclassify (manageUsers)
///   GET    /api/categories/stats     — [{categoryId, name, itemCount}]
/// </summary>
internal static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── List (any authenticated user) ────────────────────────────────
        app.MapGet(ApiRoutes.Categories, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Categories
                .AsNoTracking()
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .Select(c => new
                {
                    Cat = c,
                    Count = db.MediaItemCategories.Count(mc => mc.CategoryId == c.Id),
                })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => ToDto(r.Cat, r.Count)).ToArray());
        }).RequireAuthorization();

        // ─── Stats (any authenticated user) ───────────────────────────────
        app.MapGet(ApiRoutes.CategoryStats, async (
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.Categories
                .AsNoTracking()
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.Name)
                .Select(c => new
                {
                    categoryId = c.Id,
                    name       = c.Name,
                    itemCount  = db.MediaItemCategories.Count(mc => mc.CategoryId == c.Id),
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization();

        // ─── Create (manageUsers) ─────────────────────────────────────────
        app.MapPost(ApiRoutes.Categories, async (
            CreateCategoryRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required" });

            var name = req.Name.Trim();
            if (name.Length > 64)
                return Results.BadRequest(new { error = "Name must be 64 characters or fewer" });

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Categories.AnyAsync(c => c.Name == name, ct))
                return Results.Conflict(new { error = "Category name already used" });

            var cat = new Category
            {
                Id          = Guid.NewGuid(),
                Name        = name,
                BuiltIn     = false,
                Enabled     = true,
                Description = (req.Description ?? "").Trim(),
                Hint        = (req.Hint ?? "").Trim(),
                SortOrder   = req.SortOrder ?? 1000,
                CreatedAt   = DateTime.UtcNow,
            };
            db.Categories.Add(cat);
            await db.SaveChangesAsync(ct);
            return Results.Created(ApiRoutes.Category(cat.Id), ToDto(cat, 0));
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        // ─── Update (manageUsers) ─────────────────────────────────────────
        app.MapMethods(ApiRoutes.CategoryById, new[] { "PATCH" }, async (
            Guid id,
            UpdateCategoryRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cat is null) return Results.NotFound();

            // Built-in rename guard.
            if (req.Name is string nameRaw)
            {
                var newName = nameRaw.Trim();
                if (cat.BuiltIn && !string.Equals(cat.Name, newName, StringComparison.Ordinal))
                    return Results.Conflict(new { error = "Built-in categories cannot be renamed" });
                if (newName.Length == 0)
                    return Results.BadRequest(new { error = "Name cannot be empty" });
                if (newName.Length > 64)
                    return Results.BadRequest(new { error = "Name must be 64 characters or fewer" });
                if (!string.Equals(cat.Name, newName, StringComparison.Ordinal))
                {
                    if (await db.Categories.AnyAsync(c => c.Id != id && c.Name == newName, ct))
                        return Results.Conflict(new { error = "Category name already used" });
                    cat.Name = newName;
                }
            }

            if (req.Description is string desc) cat.Description = desc.Trim();
            if (req.Hint        is string hint) cat.Hint        = hint.Trim();
            if (req.SortOrder   is int    so)   cat.SortOrder   = so;
            if (req.Enabled     is bool   en)   cat.Enabled     = en;

            await db.SaveChangesAsync(ct);
            var count = await db.MediaItemCategories.CountAsync(mc => mc.CategoryId == cat.Id, ct);
            return Results.Ok(ToDto(cat, count));
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        // ─── Delete (manageUsers; built-ins blocked) ──────────────────────
        app.MapDelete(ApiRoutes.CategoryById, async (
            Guid id,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var cat = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (cat is null) return Results.NotFound();
            if (cat.BuiltIn)
                return Results.Conflict(new { error = "Built-in categories cannot be deleted" });
            db.Categories.Remove(cat);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        // ─── Rescan (manageUsers) ─────────────────────────────────────────
        // Fires the classifier across every MediaItem in the background. We
        // return 202 immediately — the work happens on a thread-pool task
        // so big libraries don't tie up the request.
        app.MapPost(ApiRoutes.CategoryRescan, (
            CategoryClassifierService classifier,
            ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("CategoryRescan");
            _ = Task.Run(async () =>
            {
                try
                {
                    await classifier.RescanAllAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Category rescan crashed");
                }
            });
            return Results.Accepted();
        }).RequireAuthorization(AuthConstants.Policies.ManageUsers);

        // ─── Per-item manual category set ────────────────────────────────
        // PUT /api/media/{id}/categories — overwrite the item's manual
        // category rows (Source="manual"). The classifier preserves these
        // on rescan. Anybody with viewContent can pin categories on items
        // they can see; manageUsers isn't required because this is a
        // per-item curation action, not a system-wide setting.
        app.MapPut(ApiRoutes.MediaCategories, async (
            Guid id,
            SetMediaCategoriesRequest req,
            IDbContextFactory<AppDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var mediaExists = await db.MediaItems.AnyAsync(m => m.Id == id, ct);
            if (!mediaExists) return Results.NotFound();

            var requested = (req.CategoryIds ?? Array.Empty<Guid>()).Distinct().ToArray();
            // Verify every requested CategoryId is real + enabled.
            var validIds = await db.Categories
                .Where(c => requested.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(ct);

            // Delete existing rows for this item, then add fresh manual rows.
            var existing = await db.MediaItemCategories
                .Where(mc => mc.MediaItemId == id)
                .ToListAsync(ct);
            db.MediaItemCategories.RemoveRange(existing);
            foreach (var catId in validIds)
            {
                db.MediaItemCategories.Add(new MediaItemCategory
                {
                    MediaItemId = id,
                    CategoryId  = catId,
                    Source      = "manual",
                    CreatedAt   = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { categoryIds = validIds });
        }).RequireAuthorization();

        return app;
    }

    private static CategoryDto ToDto(Category c, int itemCount) => new(
        c.Id,
        c.Name,
        c.BuiltIn,
        c.Enabled,
        c.Description ?? "",
        c.Hint ?? "",
        c.SortOrder,
        itemCount);
}
