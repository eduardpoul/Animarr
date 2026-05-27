using System.Text;
using System.Text.Json;
using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Assigns <see cref="Category"/> rows to a <see cref="MediaItem"/> after
/// identification finishes (or via an admin "rescan all" trigger). The pipeline
/// is:
///
///   1. Load the item + its current category assignments.
///   2. PRESERVE any rows with <c>Source="manual"</c> — those are user edits
///      and stay untouched no matter what the classifier says.
///   3. If the LLM is enabled, send the item's title/year/genres/origin/etc.
///      plus the enabled categories' Hints, and ask for JSON.
///   4. If the LLM is disabled OR the call fails, fall back to a deterministic
///      heuristic that looks at MediaType + origin language + genres.
///   5. Replace non-manual MediaItemCategory rows with the new picks,
///      tagging them <c>Source="llm"</c> or <c>"heuristic"</c>.
///
/// The service uses <see cref="IDbContextFactory{TContext}"/> so it can safely
/// be registered as a singleton — every call opens its own short-lived context.
/// </summary>
public sealed class CategoryClassifierService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<CategoryClassifierService> logger)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Classify a single MediaItem against enabled categories. Returns the
    /// Category Ids that were assigned (LLM or heuristic — manual overrides
    /// are NOT returned here because they were already in place).
    ///
    /// Failures are logged and yield an empty result — never thrown — so the
    /// identification pipeline isn't blocked by classifier outages.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ClassifyAsync(Guid mediaItemId, CancellationToken ct = default)
    {
        try
        {
            return await ClassifyCoreAsync(mediaItemId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Category classification failed for media item {Id}", mediaItemId);
            return Array.Empty<Guid>();
        }
    }

    private async Task<IReadOnlyList<Guid>> ClassifyCoreAsync(Guid mediaItemId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var item = await db.MediaItems
            .Include(m => m.Categories)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);
        if (item is null)
        {
            logger.LogDebug("Classify: media item {Id} not found.", mediaItemId);
            return Array.Empty<Guid>();
        }

        var categories = await db.Categories
            .Where(c => c.Enabled)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        if (categories.Count == 0)
        {
            logger.LogDebug("Classify: no enabled categories.");
            return Array.Empty<Guid>();
        }

        // Try LLM first when enabled. Fall back to heuristic on any failure.
        List<Guid>? picked = null;
        string source = "heuristic";

        bool llmEnabled = false;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var appCfg = scope.ServiceProvider.GetRequiredService<IAppConfigService>();
            llmEnabled = await appCfg.GetAsync<bool>(AppConfigKeys.LlmEnabled, false, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Classify: app config read failed, treating LLM as disabled.");
        }

        if (llmEnabled)
        {
            try
            {
                picked = await ClassifyWithLlmAsync(item, categories, ct);
                if (picked is { Count: > 0 })
                    source = "llm";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "LLM category classification failed for {Id} — falling back to heuristic.", mediaItemId);
                picked = null;
            }
        }

        if (picked is null || picked.Count == 0)
        {
            picked = ClassifyWithHeuristic(item, categories);
            source = "heuristic";
        }

        // PRESERVE manual rows — those are user overrides. Wipe everything else
        // and write the new picks (skipping any category that the user already
        // assigned manually, since that's still "in" the final set).
        var manualCatIds = item.Categories
            .Where(mc => string.Equals(mc.Source, "manual", StringComparison.OrdinalIgnoreCase))
            .Select(mc => mc.CategoryId)
            .ToHashSet();

        var toRemove = item.Categories
            .Where(mc => !manualCatIds.Contains(mc.CategoryId))
            .ToList();
        foreach (var r in toRemove)
            db.MediaItemCategories.Remove(r);

        var assigned = new List<Guid>();
        foreach (var catId in picked.Distinct())
        {
            if (manualCatIds.Contains(catId))
            {
                assigned.Add(catId);
                continue;
            }
            db.MediaItemCategories.Add(new MediaItemCategory
            {
                MediaItemId = item.Id,
                CategoryId  = catId,
                Source      = source,
                CreatedAt   = DateTime.UtcNow,
            });
            assigned.Add(catId);
        }

        await db.SaveChangesAsync(ct);
        logger.LogDebug("Classified {Id} ({Title}) → [{Cats}] via {Source}",
            item.Id, item.Title,
            string.Join(", ", categories.Where(c => assigned.Contains(c.Id)).Select(c => c.Name)),
            source);
        return assigned;
    }

    // ─── LLM path ─────────────────────────────────────────────────────────────

    private async Task<List<Guid>?> ClassifyWithLlmAsync(MediaItem item, List<Category> categories, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmService>();

        var prompt = BuildPrompt(item, categories);
        var rawJson = await llm.GetCompletionAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        return ParseLlmResponse(rawJson, categories);
    }

    private static string BuildPrompt(MediaItem item, IReadOnlyList<Category> categories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a media library categorisation assistant. Pick which category labels apply to the following media item.");
        sb.AppendLine();
        sb.AppendLine("Available categories (you may pick zero, one, or several):");
        foreach (var c in categories)
        {
            sb.Append("- \"").Append(c.Name).Append("\"");
            if (!string.IsNullOrWhiteSpace(c.Hint))
                sb.Append(" — ").Append(c.Hint);
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("Media item:");
        sb.Append("- title: ").AppendLine(item.Title);
        if (!string.IsNullOrWhiteSpace(item.OriginalTitle))
            sb.Append("- original_title: ").AppendLine(item.OriginalTitle);
        if (!string.IsNullOrWhiteSpace(item.EnglishTitle))
            sb.Append("- english_title: ").AppendLine(item.EnglishTitle);
        if (item.Year.HasValue)
            sb.Append("- year: ").AppendLine(item.Year.Value.ToString());
        sb.Append("- media_type: ").AppendLine(item.MediaType.ToString());
        if (!string.IsNullOrWhiteSpace(item.Language))
            sb.Append("- original_language: ").AppendLine(item.Language);
        if (!string.IsNullOrWhiteSpace(item.Studio))
            sb.Append("- studio: ").AppendLine(item.Studio);
        if (!string.IsNullOrWhiteSpace(item.ContentRating))
            sb.Append("- content_rating: ").AppendLine(item.ContentRating);

        var genres = SafeStringArray(item.GenresJson);
        if (genres.Length > 0)
            sb.Append("- genres: ").AppendLine(string.Join(", ", genres));
        var tags = SafeStringArray(item.TagsJson);
        if (tags.Length > 0)
            sb.Append("- tags: ").AppendLine(string.Join(", ", tags));
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            var desc = item.Description.Length > 400
                ? item.Description[..400] + "..."
                : item.Description;
            sb.Append("- description: ").AppendLine(desc);
        }

        sb.AppendLine();
        sb.AppendLine("Respond ONLY with valid JSON of the shape:");
        sb.AppendLine("{\"categories\": [\"Name1\", \"Name2\"]}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Use the EXACT category names listed above; do not invent new ones.");
        sb.AppendLine("- Pick zero or more. An empty array is fine if nothing fits.");
        sb.AppendLine("- Prefer Anime/Donghua/Dorama over generic Movies/Serials when both could apply.");
        sb.AppendLine("- Return ONLY the JSON object, no commentary.");
        return sb.ToString();
    }

    private static List<Guid>? ParseLlmResponse(string raw, IReadOnlyList<Category> categories)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("categories", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return null;

            var nameToId = categories.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
            var result = new List<Guid>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var name = el.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (nameToId.TryGetValue(name.Trim(), out var id) && !result.Contains(id))
                    result.Add(id);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    // ─── Heuristic fallback ───────────────────────────────────────────────────

    private static List<Guid> ClassifyWithHeuristic(MediaItem item, IReadOnlyList<Category> categories)
    {
        // Build lookup by canonical name so we can ignore casing in category names.
        // (Admins can disable specific categories — those aren't in `categories` so
        // the lookup returns null and we naturally skip them.)
        Guid? IdFor(string name) =>
            categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;

        var picked = new List<Guid>();
        var genres = SafeStringArray(item.GenresJson);
        var tags   = SafeStringArray(item.TagsJson);
        var lang   = (item.Language ?? "").Trim();
        var langL  = lang.ToLowerInvariant();
        var studio = (item.Studio ?? "").ToLowerInvariant();

        bool isAnimation = genres.Any(g => g.Contains("Animation", StringComparison.OrdinalIgnoreCase))
                        || tags.Any(t => t.Contains("Animation", StringComparison.OrdinalIgnoreCase)
                                      || t.Contains("Donghua",  StringComparison.OrdinalIgnoreCase)
                                      || t.Contains("Anime",    StringComparison.OrdinalIgnoreCase));
        bool isJp = langL is "japanese" or "ja" or "jp";
        bool isCn = langL is "mandarin" or "chinese" or "cantonese" or "zh" or "cn";
        bool isKr = langL is "korean" or "ko" or "kr";

        // MediaItemType.Anime already implies Japanese animation — bias accordingly.
        var anime = item.MediaType == MediaItemType.Anime;

        bool addedAnime    = false;
        bool addedDonghua  = false;
        bool addedDorama   = false;

        // ── Anime ──────────────────────────────────────────────────────────────
        if ((isAnimation && isJp) || anime)
        {
            if (IdFor("Anime") is Guid animeId) { picked.Add(animeId); addedAnime = true; }
        }

        // ── Donghua ────────────────────────────────────────────────────────────
        if (isAnimation && isCn)
        {
            if (IdFor("Donghua") is Guid dh1) { picked.Add(dh1); addedDonghua = true; }
        }
        // Studio-hint backup for Donghua (Bilibili / Tencent / etc. when language
        // field happens to be missing).
        if (!addedDonghua && isAnimation
            && (studio.Contains("bilibili") || studio.Contains("tencent")
                || studio.Contains("foch")  || studio.Contains("haoliners")))
        {
            if (IdFor("Donghua") is Guid dh2) { picked.Add(dh2); addedDonghua = true; }
        }

        // ── Dorama ─────────────────────────────────────────────────────────────
        if (isKr
            && item.MediaType is MediaItemType.Series or MediaItemType.Anime or MediaItemType.Unknown)
        {
            bool dramaOrRomance = genres.Any(g =>
                g.Equals("Drama",   StringComparison.OrdinalIgnoreCase) ||
                g.Equals("Romance", StringComparison.OrdinalIgnoreCase));
            if (dramaOrRomance || item.MediaType == MediaItemType.Series)
            {
                if (IdFor("Dorama") is Guid drid) { picked.Add(drid); addedDorama = true; }
            }
        }

        // ── Movies / Serials (default for live-action) ─────────────────────────
        switch (item.MediaType)
        {
            case MediaItemType.Movie:
                // Only fall back to "Movies" if we haven't already classified as
                // Anime/Donghua (the spec says LLM should prefer those; the
                // heuristic mirrors that).
                if (!addedAnime && !addedDonghua)
                {
                    if (IdFor("Movies") is Guid movId) picked.Add(movId);
                }
                break;
            case MediaItemType.Series:
                if (!addedAnime && !addedDonghua && !addedDorama)
                {
                    if (IdFor("Serials") is Guid serId) picked.Add(serId);
                }
                break;
            case MediaItemType.Anime:
                // already covered above
                break;
            case MediaItemType.Multserials:
                if (IdFor("Multi") is Guid mid) picked.Add(mid);
                if (IdFor("Serials") is Guid sid) picked.Add(sid);
                break;
        }

        // ── Kids (additive — can apply on top of anything above) ───────────────
        var kidsRatings = new[] { "G", "PG", "TV-Y", "TV-Y7", "TV-G", "TV-PG" };
        bool isKidsRating = !string.IsNullOrWhiteSpace(item.ContentRating)
                         && kidsRatings.Any(k => string.Equals(k, item.ContentRating, StringComparison.OrdinalIgnoreCase));
        bool isKidsGenre = genres.Any(g => g.Equals("Family", StringComparison.OrdinalIgnoreCase)
                                        || g.Equals("Kids",   StringComparison.OrdinalIgnoreCase)
                                        || g.Equals("Children", StringComparison.OrdinalIgnoreCase));
        if (isKidsRating || isKidsGenre)
        {
            if (IdFor("Kids") is Guid kidsId && !picked.Contains(kidsId)) picked.Add(kidsId);
        }

        return picked;
    }

    private static string[] SafeStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, _json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ─── Background rescan ────────────────────────────────────────────────────

    /// <summary>Reclassify every MediaItem. Used by the admin "Rescan" button —
    /// expected to be fired in the background; the caller does not await.</summary>
    public async Task RescanAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.MediaItems.Select(m => m.Id).ToListAsync(ct);
        logger.LogInformation("Category rescan starting — {Count} item(s).", ids.Count);

        int done = 0;
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            await ClassifyAsync(id, ct);
            done++;
            if (done % 50 == 0)
                logger.LogInformation("Category rescan progress: {Done}/{Total}", done, ids.Count);
        }

        logger.LogInformation("Category rescan finished — {Done}/{Total} item(s) processed.", done, ids.Count);
    }
}
