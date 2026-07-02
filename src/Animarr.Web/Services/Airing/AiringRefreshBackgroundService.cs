using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Airing;

/// <summary>
/// Keeps the airing-schedule fields on MediaItem fresh for the ongoing
/// calendar. Ticks every 30 minutes; a tick usually costs ONE AniList request:
/// the batch query covers up to 50 titles by AniList id + 50 by MAL id at
/// once, and a home library rarely has more than a few dozen ongoings.
///
/// Candidate = identified title whose airing data is stale (12h TTL), whose
/// announced next episode has already aired (roll it into LastAired*, fetch
/// the new next), or that has never been checked. FINISHED titles re-check on
/// a lazy weekly cadence — a finished show only changes state on a new-season
/// announcement.
///
/// Sources: AniList nextAiringEpisode (anime + donghua; minute precision) with
/// a TMDB next_episode_to_air fallback (live-action dorama/series; date-only —
/// stored as 12:00 UTC so it lands on the right calendar day). Titles with no
/// external ids get up to <see cref="ResolvesPerTick"/> AniList title-resolves
/// per tick, which also persists the discovered ids via MalIdResolver's flow.
/// </summary>
public sealed class AiringRefreshBackgroundService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<AiringRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay    = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan TickEvery       = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RecheckAfter    = TimeSpan.FromHours(12);
    private static readonly TimeSpan FinishedRecheck = TimeSpan.FromDays(7);
    private const int ResolvesPerTick = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TickEvery);
        do
        {
            try { await RefreshOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[Airing] refresh tick failed"); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.MediaItems
            .Where(m => m.IdentificationStatus == IdentificationStatus.Identified ||
                        m.IdentificationStatus == IdentificationStatus.Manual)
            .Where(m => m.LastAiringCheckAt == null
                     || (m.NextAirAtUtc != null && m.NextAirAtUtc < now)
                     || (m.AiringStatus == "FINISHED"
                            ? m.LastAiringCheckAt < now - FinishedRecheck
                            : m.LastAiringCheckAt < now - RecheckAfter))
            .ToListAsync(ct);
        if (candidates.Count == 0) return;

        // Announced episode has aired → remember it for the "released, got the
        // file yet?" calendar row before the fresh fetch overwrites Next*.
        foreach (var m in candidates.Where(m => m.NextAirAtUtc is not null && m.NextAirAtUtc < now))
        {
            m.LastAiredEpisode = m.NextEpisodeNumber;
            m.LastAiredAtUtc   = m.NextAirAtUtc;
        }

        // Titles with no external ids: bridge a handful per tick via the same
        // resolver AniSkip uses (it persists MalId/AniListId onto the row).
        var unresolved = candidates
            .Where(m => m.AniListId is null && m.MalId is null && m.MediaType != MediaItemType.Movie)
            .Take(ResolvesPerTick)
            .ToList();
        if (unresolved.Count > 0)
        {
            using var scope = scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<Segments.MalIdResolver>();
            foreach (var m in unresolved)
            {
                try { await resolver.ResolveAsync(m, ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* stays unresolved — retried next tick */ }
            }
        }

        // ── AniList batch: anime + donghua ───────────────────────────────────
        using var svcScope = scopeFactory.CreateScope();
        var aniList = svcScope.ServiceProvider.GetRequiredService<AniListClient>();
        var byAniId = candidates.Where(m => m.AniListId is > 0)
            .GroupBy(m => m.AniListId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var byMalId = candidates.Where(m => m.AniListId is null && m.MalId is > 0)
            .GroupBy(m => m.MalId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        var updated = 0;
        if (byAniId.Count > 0 || byMalId.Count > 0)
        {
            var airing = await aniList.GetAiringBatchAsync(byAniId.Keys.ToList(), byMalId.Keys.ToList(), ct);
            foreach (var a in airing)
            {
                var targets = byAniId.GetValueOrDefault(a.AniListId)
                           ?? (a.IdMal is int mal ? byMalId.GetValueOrDefault(mal) : null);
                if (targets is null) continue;
                foreach (var m in targets)
                {
                    m.AiringStatus      = a.Status;
                    m.NextEpisodeNumber = a.NextEpisode;
                    m.NextAirAtUtc      = a.NextAiringAtUnix is long unix
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                        : null;
                    m.AniListId ??= a.AniListId;
                    m.LastAiringCheckAt = now;
                    updated++;
                }
            }
        }

        // ── TMDB fallback: live-action shows AniList doesn't cover ───────────
        // Only "Returning Series" (or never-checked) with a TmdbId and no
        // AniList data — a couple of detail requests per tick at most.
        var tmdb = svcScope.ServiceProvider.GetRequiredService<TmdbClient>();
        var tmdbCandidates = candidates
            .Where(m => m.LastAiringCheckAt != now                 // not covered by AniList above
                     && m.TmdbId is > 0
                     && m.AniListId is null && m.MalId is null
                     && m.MediaType != MediaItemType.Movie
                     && (m.Status is null || m.Status.Contains("Returning", StringComparison.OrdinalIgnoreCase)
                                          || m.Status.Contains("Production", StringComparison.OrdinalIgnoreCase)
                                          || m.AiringStatus is null or "RELEASING" or "NOT_YET_RELEASED"))
            .Take(5)
            .ToList();
        foreach (var m in tmdbCandidates)
        {
            try
            {
                var detail = await tmdb.GetTvDetailAsync(m.TmdbId!.Value, ct: ct);
                if (detail is null) { m.LastAiringCheckAt = now; continue; }
                var next = detail.NextEpisodeToAir;
                m.AiringStatus = next is not null || detail.InProduction ? "RELEASING" : "FINISHED";
                m.NextEpisodeNumber = next?.EpisodeNumber;
                // TMDB gives a date, not a time — noon UTC keeps it on the
                // right day in any nearby timezone.
                m.NextAirAtUtc = next?.AirDate is { Length: >= 10 } d
                                 && DateTime.TryParse(d, out var parsed)
                    ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc).AddHours(12)
                    : null;
                m.LastAiringCheckAt = now;
                updated++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[Airing] TMDB fallback failed for {Title}", m.Title);
                m.LastAiringCheckAt = now;
            }
        }

        // Whatever the sources didn't recognise still gets stamped so it can't
        // wedge the queue — a miss retries on the normal TTL.
        foreach (var m in candidates.Where(m => m.LastAiringCheckAt != now))
        {
            if (m.AniListId is not null || m.MalId is not null || m.TmdbId is not null)
                m.AiringStatus ??= "FINISHED";
            m.LastAiringCheckAt = now;
        }

        await db.SaveChangesAsync(ct);
        if (updated > 0)
            logger.LogInformation("[Airing] refreshed schedule for {Count} title(s)", updated);
    }
}
