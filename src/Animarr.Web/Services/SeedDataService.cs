using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Seeds built-in rename patterns and global ignore rules on first run.
/// Idempotent — safe to call on every startup.
/// </summary>
public class SeedDataService(IDbContextFactory<AppDbContext> dbFactory, ILogger<SeedDataService> logger)
{
    public async Task SeedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await SeedPatternsAsync(db);
        await SeedIgnoreRulesAsync(db);
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
            // [AniLibria] Attack on Titan - 05.mkv  /  [AniLibria.TV] Show - 12.mkv
            @"(?i)\[AniLibria(?:\.TV)?\]\s*[^\[\]-]+-\s*(?<episode>\d+)",
            Priority: 20
        ),
        new(
            "AwfulSubs",
            // [HorribleSubs] Show - 05 [1080p].mkv
            @"(?i)\[HorribleSubs\]\s*[^\[\]-]+-\s*(?<episode>\d+)\s*\[\d+p\]",
            Priority: 30
        ),
        new(
            "RawBox",
            // [Erai-raws] Show - 05 [1080p].mkv
            @"(?i)\[Erai-raws\]\s*[^\[\]-]+-\s*(?<episode>\d+)\s*\[",
            Priority: 40
        ),
        new(
            "SubsYes",
            // [SubsPlease] Show - 05 (1080p).mkv
            @"(?i)\[SubsPlease\]\s*[^\[\]-]+-\s*(?<episode>\d+)\s*\(",
            Priority: 50
        ),
        new(
            "Universal S##E##",
            // any_name.S01E05.mkv  /  Show.s2e12.mkv
            @"(?i)[._\s\-]s(?<season>\d{1,2})e(?<episode>\d{2,3})[._\s\-]",
            Priority: 60
        ),
        new(
            "Universal Episode fallback",
            // Fallback: extracts a 2–4 digit episode number surrounded by separators
            // e.g.  Show - 05.mkv  /  Show.05.mkv  /  Show_ep12.mkv
            @"(?:^|[._\s\-])(?:ep?)?(?<episode>\d{2,4})(?:[._\s\-]|$)",
            Priority: 999
        ),
        // ── Movie-specific patterns (ApplicableTo = Movie) ─────────────────
        new(
            "Movie - Year (Parentheses)",
            // [HorribleSubs] Kimi no Na wa (2016) [1080p].mkv  /  Your Name (2016).mkv
            @"(?i)\(\s*(?<episode>(?:19|20)\d{2})\s*\)",
            Priority: 5,
            ApplicableTo: FolderType.Movie
        ),
        new(
            "Movie - Year Dotted",
            // Inception.2010.1080p.BluRay.mkv  /  Your.Name.2016.mkv
            @"(?i)(?:^|[._ ])(?<episode>(?:19|20)\d{2})(?:[._ ]|$)",
            Priority: 6,
            ApplicableTo: FolderType.Movie
        ),
    ];

    private async Task SeedPatternsAsync(AppDbContext db)
    {
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

    // ─── Ignore rules ────────────────────────────────────────────────────────

    // These are Plex/Emby metadata files that must never be renamed.
    // Masks use simple glob syntax: * = any sequence of characters.
    private static readonly string[] BuiltInIgnoreMasks =
    [
        // Cover/artwork files
        "fanart*",
        "poster*",
        "backdrop*",
        "banner*",
        "logo*",
        "clearart*",
        "clearlogo*",
        "discart*",
        "landscape*",
        "keyart*",
        // Season-level images
        "season-poster*",
        "season-fanart*",
        "season-banner*",
        "season-landscape*",
        "season-specials*",
        // Metadata sidecar files
        "*.nfo",
        "*.xml",
        "*.srt.bak",
    ];

    private async Task SeedIgnoreRulesAsync(AppDbContext db)
    {
        if (await db.IgnoreRules.AnyAsync(r => r.Scope == RuleScope.Global && r.FolderId == null))
        {
            logger.LogDebug("Global ignore rules already seeded, skipping.");
            return;
        }

        var entities = BuiltInIgnoreMasks.Select(mask => new IgnoreRule
        {
            Id = Guid.NewGuid(),
            Mask = mask,
            Scope = RuleScope.Global,
        });

        db.IgnoreRules.AddRange(entities);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded {Count} global ignore rules.", BuiltInIgnoreMasks.Length);
    }

    // ─── Private record ──────────────────────────────────────────────────────

    private sealed record RenamePatternSeed(string Name, string Pattern, int Priority, FolderType? ApplicableTo = null);
}
