using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Franchise;

/// <summary>
/// Backfills franchise graphs one title at a time — the segment/trickplay
/// serial-pass shape, but on a slower cadence: one BFS costs several AniList
/// requests, and the shared 30 req/min budget also feeds the airing refresh
/// and MalId bridging. <see cref="MediaItem.LastRelationsCheckAt"/> gates the
/// queue (franchises change rarely → long TTL); the detail page additionally
/// lazy-kicks a refresh for never-checked titles so the rail a user is
/// actually looking at fills first.
/// </summary>
public sealed class FranchiseBackgroundService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<FranchiseBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BusyDelay    = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan IdleDelay    = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RescanAfter  = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork = false;
            try { didWork = await ProcessOneAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[Franchise] background pass failed"); }

            try { await Task.Delay(didWork ? BusyDelay : IdleDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        Guid itemId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff = DateTime.UtcNow - RescanAfter;
            var next = await db.MediaItems
                .Where(m => m.IdentificationStatus == IdentificationStatus.Identified ||
                            m.IdentificationStatus == IdentificationStatus.Manual)
                .Where(m => m.AniListId != null || m.MalId != null)
                .Where(m => m.LastRelationsCheckAt == null || m.LastRelationsCheckAt < cutoff)
                .OrderBy(m => m.LastRelationsCheckAt)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);
            if (next is not Guid g) return false;
            itemId = g;
        }

        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<FranchiseService>();
        await svc.RefreshForItemAsync(itemId, ct);
        return true;
    }
}
