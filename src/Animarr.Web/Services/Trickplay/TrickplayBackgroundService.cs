using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Services.Trickplay;

/// <summary>
/// Background pass that pre-generates seek-preview sprites one title at a
/// time — the same serial low-cadence shape as
/// <see cref="Segments.SegmentDetectionBackgroundService"/>.
///
/// Ordering: titles someone touched in the last week come first (their
/// scrubber benefits tonight), then the rest of the library oldest-scan
/// first. <see cref="MediaItem.LastTrickplayScanAt"/> gates re-scans; the
/// torrent-completion hook clears it so fresh episodes get sprites promptly.
///
/// Politeness: the whole tick is skipped while any HLS transcode session is
/// alive — sprite decoding is cheap but a weak NAS shouldn't split cores with
/// a live transcode. (Direct Play/Stream cost little; ffmpeg also runs at
/// BelowNormal priority as a second belt.)
/// </summary>
public sealed class TrickplayBackgroundService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    HlsSessionService hls,
    ILogger<TrickplayBackgroundService> logger) : BackgroundService
{
    // Staggered ~25s after the segment pass's 20s so boot work doesn't pile up.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan BusyDelay    = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleDelay    = TimeSpan.FromSeconds(60);
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
            catch (Exception ex) { logger.LogWarning(ex, "[Trickplay] background pass failed"); }

            try { await Task.Delay(didWork ? BusyDelay : IdleDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        // Yield fully to live transcodes — retry on the idle cadence.
        if (hls.ActiveSessionCount > 0) return false;

        Guid itemId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff       = DateTime.UtcNow - RescanAfter;
            var recentCutoff = DateTime.UtcNow.AddDays(-7);
            var next = await db.MediaItems
                .Where(m => (m.IdentificationStatus == IdentificationStatus.Identified ||
                             m.IdentificationStatus == IdentificationStatus.Manual) &&
                            (m.LastTrickplayScanAt == null || m.LastTrickplayScanAt < cutoff))
                // Actively-watched titles first, then never-scanned (SQLite sorts
                // NULL before values), then the stalest.
                .OrderByDescending(m => db.WatchStates.Any(w => w.MediaItemId == m.Id && w.LastSeenAt > recentCutoff))
                .ThenBy(m => m.LastTrickplayScanAt)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);
            if (next is not Guid g) return false;
            itemId = g;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<TrickplayService>();
            await svc.GenerateForItemAsync(itemId, ct);
        }
        finally
        {
            // Stamp even on failure so one broken title can't wedge the queue.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.MediaItems
                .Where(m => m.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastTrickplayScanAt, DateTime.UtcNow), ct);
        }
        return true;
    }
}
