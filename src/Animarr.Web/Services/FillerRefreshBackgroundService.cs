using Animarr.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services;

/// <summary>
/// Background sweep that fetches per-episode filler/recap flags from Jikan
/// for every MAL-id-carrying title. One item per tick (Jikan's rate limits
/// are strict and a long-runner is a dozen paginated requests by itself).
/// Finished shows re-check on a long TTL; RELEASING ones sooner — a fresh
/// episode of a long-runner can itself be filler.
/// </summary>
public sealed class FillerRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<FillerRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay     = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BusyDelay        = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleDelay        = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RescanAfter      = TimeSpan.FromDays(30);
    private static readonly TimeSpan ReleasingRescan  = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(StartupDelay, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            bool didWork = false;
            try { didWork = await ProcessOneAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[Filler] sweep tick failed"); }

            try { await Task.Delay(didWork ? BusyDelay : IdleDelay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var jikan     = scope.ServiceProvider.GetRequiredService<JikanClient>();

        var now             = DateTime.UtcNow;
        var staleCutoff     = now - RescanAfter;
        var releasingCutoff = now - ReleasingRescan;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.MediaItems
            .Where(m => m.MalId != null)
            .Where(m => m.LastFillerCheckAt == null
                     || m.LastFillerCheckAt < staleCutoff
                     || (m.AiringStatus == "RELEASING" && m.LastFillerCheckAt < releasingCutoff))
            .OrderBy(m => m.LastFillerCheckAt == null ? 0 : 1)   // never-checked first
            .ThenBy(m => m.LastFillerCheckAt)
            .FirstOrDefaultAsync(ct);
        if (item is null) return false;

        var flags = await jikan.GetEpisodeFlagsAsync(item.MalId!.Value, ct);
        if (flags is null)
        {
            // Transient (rate limit / network) — push the stamp forward a day
            // instead of a full TTL so the retry is soon but the queue moves on.
            item.LastFillerCheckAt = now - RescanAfter + TimeSpan.FromDays(1);
            await db.SaveChangesAsync(ct);
            return true;
        }

        item.EpisodeFlagsJson = flags.Filler.Count > 0 || flags.Recap.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(new EpisodeFlagsData
            {
                Filler = flags.Filler.OrderBy(x => x).ToArray(),
                Recap  = flags.Recap.OrderBy(x => x).ToArray(),
            })
            : null;
        item.LastFillerCheckAt = now;
        await db.SaveChangesAsync(ct);

        if (flags.Filler.Count > 0 || flags.Recap.Count > 0)
            logger.LogInformation("[Filler] {Title}: {Filler} filler / {Recap} recap episode(s)",
                item.Title, flags.Filler.Count, flags.Recap.Count);
        return true;
    }
}
