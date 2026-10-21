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
/// playback/transcoding for CPU: the pass parks on <see cref="PlaybackActivity"/>
/// before each title, each episode and each ffmpeg run (HLS, Direct Play and
/// Direct Stream viewers all count), and the decodes themselves run at nice 10
/// / idle I/O with a 2-thread cap.
///
/// <see cref="MediaItem.LastSegmentScanAt"/> gates the queue: a title is scanned
/// once, then only re-scanned after <see cref="RescanAfter"/> (or
/// <see cref="FinishedRescanAfter"/> once it has finished airing). The work itself
/// is idempotent (already-covered episodes are skipped), so a missed/restarted
/// run just resumes on the next tick.
///
/// Episodes the player opened without segments arrive through
/// <see cref="SegmentDetectionQueue"/> and are drained ahead of the title pass
/// (the viewer is likely to open them again), under the same politeness.
/// </summary>
public sealed class SegmentDetectionBackgroundService(
    IDbContextFactory<AppDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    PlaybackActivity playback,
    ILogger<SegmentDetectionBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    // Short gap between titles so the initial library fill runs back-to-back;
    // long idle poll once the backlog is drained so we don't hammer the box.
    private static readonly TimeSpan BusyDelay    = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdleDelay    = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RescanAfter  = TimeSpan.FromDays(30);
    /// <summary>A finished show gets no new episodes, and re-fingerprinting the
    /// same files gives the same answer — yet every episode the cascade found
    /// nothing for (cold opens, finales, many western series) was fully
    /// re-decoded each month, with its peers. Back those titles off to ~6
    /// months. Nothing is lost: torrent completion clears the gate for a title
    /// that gains files, and an episode opened without segments still gets
    /// the cheap lookup inline plus a queued heavy attempt.</summary>
    private static readonly TimeSpan FinishedRescanAfter = TimeSpan.FromDays(180);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let identification settle at boot before competing for CPU.
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            bool didWork = false;
            try
            {
                // Nobody's watching → go; otherwise park here (cheap 15s poll).
                await playback.WaitForIdleAsync(stoppingToken);
                didWork = await ProcessQueuedAsync(stoppingToken)
                       || await ProcessOneAsync(stoppingToken);
            }
            // Only shutdown ends the loop — a stray cancellation from a provider
            // (HTTP timeout) is just a failed pass.
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "[Segments] background pass failed"); }

            // Drain the backlog fast (initial fill), then settle into a slow poll
            // that a player-queued episode cuts short.
            try
            {
                if (didWork) await Task.Delay(BusyDelay, stoppingToken);
                else await IdleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Wait out <see cref="IdleDelay"/>, or less if the player queues
    /// an episode meanwhile.</summary>
    private static async Task IdleAsync(CancellationToken ct)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay  = Task.Delay(IdleDelay, wake.Token);
        var queued = SegmentDetectionQueue.Reader.WaitToReadAsync(wake.Token).AsTask();
        await Task.WhenAny(delay, queued);
        wake.Cancel();   // retire the loser
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Run one player-queued episode (heavy cascade, low priority).
    /// Returns false when the queue is empty.</summary>
    private async Task<bool> ProcessQueuedAsync(CancellationToken ct)
    {
        if (!SegmentDetectionQueue.Reader.TryRead(out var req)) return false;

        // The title pass may have covered it since it was queued.
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var kinds = await db.EpisodeSegments
                .Where(x => x.MediaItemId == req.Item.Id && x.Season == req.Season && x.Episode == req.Episode &&
                            (x.Kind == SegmentKind.Intro || x.Kind == SegmentKind.Credits))
                .Select(x => x.Kind)
                .Distinct()
                .CountAsync(ct);
            if (kinds == 2) return true;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var detector = scope.ServiceProvider.GetRequiredService<SegmentDetectionService>();
            if (await detector.DetectForEpisodeAsync(req.Item, req.Season, req.Episode, req.FilePath,
                    req.SeasonFiles, cheapOnly: false, ct, playback.WaitForIdleAsync))
                logger.LogInformation("[Segments] player-queued detection wrote segments for {Title} S{Season}E{Episode}",
                    req.Item.Title, req.Season, req.Episode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Segments] player-queued detection failed for {File}", req.FilePath);
        }
        return true;
    }

    /// <summary>Process one title. Returns true when a title was picked up (so the
    /// loop keeps draining), false when the queue is empty (so it idles).</summary>
    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        Guid itemId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff         = DateTime.UtcNow - RescanAfter;
            var finishedCutoff = DateTime.UtcNow - FinishedRescanAfter;
            // Never-scanned first (SQLite orders NULLs before values), then the
            // oldest scan past the TTL.
            var next = await db.MediaItems
                .Where(m => (m.IdentificationStatus == IdentificationStatus.Identified ||
                             m.IdentificationStatus == IdentificationStatus.Manual) &&
                            (m.LastSegmentScanAt == null ||
                             (m.AiringStatus == "FINISHED"
                                 ? m.LastSegmentScanAt < finishedCutoff
                                 : m.LastSegmentScanAt < cutoff)))
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
            var n = await detector.DetectForItemAsync(itemId, cheapOnly: false, ct, playback.WaitForIdleAsync);
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
