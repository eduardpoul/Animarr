using Animarr.Web.Data;
using Animarr.Web.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Background pass that runs the heavy detection providers (chromaprint) over
/// identified titles one at a time. Cheap providers (AniSkip) already ran
/// inline after identification; this fills the gaps for titles AniSkip didn't
/// cover. Serial + low cadence so a season's ffmpeg fingerprinting never fights
/// playback/transcoding for CPU.
///
/// <see cref="MediaItem.LastSegmentScanAt"/> gates the queue: a title is scanned
/// once, then only re-scanned after <see cref="RescanAfter"/>. The work itself
/// is idempotent (already-covered episodes are skipped), so a missed/restarted
/// run just resumes on the next tick.
/// </summary>
public sealed class SegmentDetectionBackgroundService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<SegmentDetectionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    // Short gap between titles so the initial library fill runs back-to-back;
    // long idle poll once the backlog is drained so we don't hammer the box.
    private static readonly TimeSpan BusyDelay    = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleDelay    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RescanAfter  = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let identification settle at boot before competing for CPU.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork = false;
            try { didWork = await ProcessOneAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[Segments] background pass failed"); }

            // Drain the backlog fast (initial fill), then settle into a slow poll.
            try { await Task.Delay(didWork ? BusyDelay : IdleDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Process one title. Returns true when a title was picked up (so the
    /// loop keeps draining), false when the queue is empty (so it idles).</summary>
    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        Guid itemId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff = DateTime.UtcNow - RescanAfter;
            // Never-scanned first (SQLite orders NULLs before values), then the
            // oldest scan past the TTL.
            var next = await db.MediaItems
                .Where(m => (m.IdentificationStatus == IdentificationStatus.Identified ||
                             m.IdentificationStatus == IdentificationStatus.Manual) &&
                            (m.LastSegmentScanAt == null || m.LastSegmentScanAt < cutoff))
                .OrderBy(m => m.LastSegmentScanAt)
                .Select(m => (Guid?)m.Id)
                .FirstOrDefaultAsync(ct);
            if (next is not Guid g) return false;
            itemId = g;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var detector = scope.ServiceProvider.GetRequiredService<SegmentDetectionService>();
            var n = await detector.DetectForItemAsync(itemId, cheapOnly: false, ct);
            if (n > 0)
                logger.LogInformation("[Segments] background pass wrote segments for {Count} episode(s) of {Id}", n, itemId);
        }
        finally
        {
            // Stamp even on a no-op/failure so a title can't wedge the queue —
            // it'll be retried after the TTL.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.MediaItems
                .Where(m => m.Id == itemId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastSegmentScanAt, DateTime.UtcNow), ct);
        }
        return true;
    }
}
