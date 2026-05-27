using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Seeds the seven built-in catalog categories (Movies, Serials, Anime, Donghua,
/// Dorama, Multi, Kids) on first run. Idempotent — safe to call on every startup;
/// existing rows (matched by <see cref="Category.Name"/>) are left alone so an
/// admin's edits to description/hint/enabled/sortOrder are preserved.
///
/// Seed GUIDs are deterministic so the same install never gets duplicate rows
/// even if it briefly ran against an older snapshot before this service existed.
/// </summary>
public sealed class CategorySeedService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<CategorySeedService> logger)
{
    private sealed record CategorySeed(
        Guid Id,
        string Name,
        string Description,
        string Hint,
        int SortOrder);

    // Hand-picked GUIDs — stable across installs so a re-seed is a no-op.
    // Generated once with `Guid.NewGuid()` and pasted in; do NOT regenerate.
    private static readonly CategorySeed[] BuiltIns =
    [
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e60"),
            Name:        "Movies",
            Description: "Feature-length theatrical films, any language or region.",
            Hint:        "feature films, theatrical movies, full-length cinema releases. NOT TV shows, NOT animation series. Includes anime films and donghua films IF the user has separate Anime/Donghua categories enabled (the LLM should prefer Anime/Donghua over Movies for animation).",
            SortOrder:   10),
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e61"),
            Name:        "Serials",
            Description: "Live-action TV shows that aren't anime, donghua or dorama.",
            Hint:        "live-action TV series and miniseries. Western shows, Russian shows, etc. NOT animation, NOT Korean dramas (those go to Dorama).",
            SortOrder:   20),
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e62"),
            Name:        "Anime",
            Description: "Japanese animation — TV series and films.",
            Hint:        "Japanese animation. origin_country=JP plus animated/anime genre. Studios like Studio Ghibli, MAPPA, Madhouse, Bones, ufotable.",
            SortOrder:   30),
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e63"),
            Name:        "Donghua",
            Description: "Chinese animation — TV and films.",
            Hint:        "Chinese animation from mainland China, Hong Kong or Taiwan. origin_country=CN/HK/TW plus animated genre. Studios like Bilibili, Tencent Penguin Pictures, Foch Films.",
            SortOrder:   40),
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e64"),
            Name:        "Dorama",
            Description: "Korean TV dramas and Korean drama films.",
            Hint:        "Korean live-action dramas (TV series). origin_country=KR plus drama/romance genres. NOT Korean action movies, NOT Korean horror — those belong to Movies/Serials.",
            SortOrder:   50),
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e65"),
            Name:        "Multi",
            Description: "Multi-series collections and franchises.",
            Hint:        "franchises that span multiple shows or films (Marvel Cinematic Universe, Star Wars, Star Trek, etc.). Apply alongside Movies/Serials when relevant.",
            SortOrder:   60),
        new(
            Id:          new Guid("8c4d1a3b-2e9f-4b6c-9d8e-1a2b3c4d5e66"),
            Name:        "Kids",
            Description: "Family / children content.",
            Hint:        "content aimed at children (G/PG rated, family-friendly). Cartoons, kids shows, educational content. Can apply alongside Anime/Donghua/Movies/Serials for kid-friendly items.",
            SortOrder:   70),
    ];

    public async Task EnsureSeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Categories
            .Where(c => c.BuiltIn)
            .Select(c => c.Name)
            .ToHashSetAsync(ct);

        var toAdd = BuiltIns
            .Where(s => !existing.Contains(s.Name))
            .Select(s => new Category
            {
                Id          = s.Id,
                Name        = s.Name,
                BuiltIn     = true,
                Enabled     = true,
                Description = s.Description,
                Hint        = s.Hint,
                SortOrder   = s.SortOrder,
                CreatedAt   = DateTime.UtcNow,
            })
            .ToList();

        if (toAdd.Count == 0)
        {
            logger.LogDebug("Built-in categories already up to date, skipping.");
            return;
        }

        db.Categories.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} new built-in categor{Suffix}: {Names}",
            toAdd.Count,
            toAdd.Count == 1 ? "y" : "ies",
            string.Join(", ", toAdd.Select(c => c.Name)));
    }
}
