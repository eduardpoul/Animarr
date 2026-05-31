using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Seeds built-in filename-parsing patterns on first run.
/// Idempotent — safe to call on every startup.
/// </summary>
public class SeedDataService(IDbContextFactory<AppDbContext> dbFactory, ILogger<SeedDataService> logger)
{
    public async Task SeedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SeedPatternsAsync(db);
    }

    // ─── Patterns ───────────────────────────────────────────────────────────

    private static readonly RenamePatternSeed[] BuiltInPatterns =
    [
        new(
            "Shizen",
            // Shizen-1080-s1-e5.mkv  /  Shizen-1080p-s02-e12.mkv
            @"(?i)[^.\s\[({-]+-\d+p?-s(?<season>\d+)-e(?<episode>\d+)",
            Priority: 10
        ),
        new(
            "Name-Resolution-Episode",
            // Anistar-1080-257.mp4  /  SomeShow-720-05.mkv  /  Title-1080p-12.mkv
            @"(?i)^.+-(?:2160|1440|1080|720|480|360)p?-(?<episode>\d+)\.",
            Priority: 15
        ),
        new(
            "AniVault",
            // [AniLilia] Attack on Titan - 05.mkv  /  [AniLilia.TV] Show - 12.mkv
            @"(?i)\[AniLilia(?:\.TV)?\]\s*[^\[\]-]+-\s*(?<episode>\d+)",
            Priority: 20
        ),
        new(
            "AwfulSubs",
            // [HorrorSubs] Show - 05 [1080p].mkv
            @"(?i)\[HorrorSubs\]\s*[^\[\]-]+-\s*(?<episode>\d+)\s*\[\d+p\]",
            Priority: 30
        ),
        new(
            "RawBox",
            // [Erao-raws] Show - 05 [1080p].mkv
            @"(?i)\[Erao-raws\]\s*[^\[\]-]+-\s*(?<episode>\d+)\s*\[",
            Priority: 40
        ),
        new(
            "SubsYes",
            // [SubsKindly] Show - 05 (1080p).mkv
            @"(?i)\[SubsKindly\]\s*[^\[\]-]+-\s*(?<episode>\d+)\s*\(",
            Priority: 50
        ),
        new(
            "Universal S##E##",
            // any_name.S01E05.mkv  /  Show.s2e12.mkv  /  S02E01.mkv
            // The leading (?:^|…) is load-bearing: a bare "S02E01.mkv" with no
            // show prefix is exactly what our own renamer emits, so the parser
            // MUST round-trip it. The old `[._\s\-]s…` required a separator
            // before `s` and silently dropped the episode for prefix-less names.
            @"(?i)(?:^|[._\s\-])s(?<season>\d{1,2})e(?<episode>\d{2,3})(?:[._\s\-]|$)",
            Priority: 60
        ),
        new(
            "Universal Episode fallback",
            // Fallback: extracts a 2–4 digit episode number surrounded by separators
            // e.g.  Show - 05.mkv  /  Show.05.mkv  /  Show_ep12.mkv
            @"(?:^|[._\s\-])(?:ep?)?(?<episode>\d{2,4})(?:[._\s\-]|$)",
            Priority: 999
        ),
        // ── Movie patterns are intentionally absent.
        //
        // Earlier seeds shipped "Movie - Year (Parentheses)" and "Movie - Year
        // Dotted" — both captured YEAR into a `(?<episode>)` group, which the
        // rename template then wrote out as `{episode}.{ext}`. Result: every
        // movie on disk got reduced to `2025.mkv` / `2018.mkv` and collisions
        // silently dropped files. Plain wrong by construction — year is not
        // an episode, and a movie filename should come from MediaItem.Title
        // (already filled by the LLM + TMDB identification pipeline).
        //
        // Cleanup of these stale rows is done in CleanupRemovedBuiltInsAsync
        // below — runs on every startup so existing installs heal themselves.
    ];

    /// <summary>Names of built-in patterns we used to ship but have since
    /// deleted. Existing installs may still carry them with IsBuiltIn=true;
    /// we remove them on every seed pass.</summary>
    private static readonly string[] RemovedBuiltInPatternNames =
    [
        "Movie - Year (Parentheses)",
        "Movie - Year Dotted",
    ];

    private async Task SeedPatternsAsync(AppDbContext db)
    {
        // First, remove any built-in patterns we used to ship but no longer do.
        // Targeted by Name so user-customised patterns with overlapping names
        // are NOT affected (they'd have IsBuiltIn=false).
        var removed = await db.RenamePatterns
            .Where(p => p.IsBuiltIn && RemovedBuiltInPatternNames.Contains(p.Name))
            .ExecuteDeleteAsync();
        if (removed > 0)
            logger.LogInformation("Removed {Count} stale built-in rename pattern(s): {Names}",
                removed, string.Join(", ", RemovedBuiltInPatternNames));

        // Keep built-in pattern rows in lockstep with the current seed text.
        // Built-in patterns are read-only at the API (pattern writes Forbid()
        // when IsBuiltIn), so existing rows can be force-synced by Name without
        // clobbering anyone — user customisations live as separate IsBuiltIn=
        // false rows. This heals older installs whose built-ins carry an
        // out-of-date regex (bug fixes, de-branding, …) and needs no real-name
        // match keys in source.
        var healed = 0;
        foreach (var seed in BuiltInPatterns)
        {
            healed += await db.RenamePatterns
                .Where(p => p.IsBuiltIn && p.Name == seed.Name && p.Pattern != seed.Pattern)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Pattern, seed.Pattern));
        }
        if (healed > 0)
            logger.LogInformation("Synced {Count} built-in pattern(s) to current seed regex.", healed);

        var existingNames = await db.RenamePatterns
            .Where(p => p.IsBuiltIn)
            .Select(p => p.Name)
            .ToHashSetAsync();

        var toAdd = BuiltInPatterns
            .Where(p => !existingNames.Contains(p.Name))
            .Select(p => new RenamePattern
            {
                Id = Guid.NewGuid(),
                Name = p.Name,
                Pattern = p.Pattern,
                Scope = PatternScope.Global,
                Priority = p.Priority,
                IsBuiltIn = true,
                ApplicableTo = p.ApplicableTo,
            })
            .ToList();

        if (toAdd.Count == 0)
        {
            logger.LogDebug("Built-in patterns already up to date, skipping.");
            return;
        }

        db.RenamePatterns.AddRange(toAdd);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} new built-in rename patterns.", toAdd.Count);
    }

    // ─── Private record ──────────────────────────────────────────────────────

    private sealed record RenamePatternSeed(string Name, string Pattern, int Priority, FolderType? ApplicableTo = null);
}
