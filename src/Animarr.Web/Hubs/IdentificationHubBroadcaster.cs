using Animarr.Shared.Realtime;
using Animarr.Web.Data;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Hubs;

/// <summary>
/// Bridges <see cref="IdentificationQueueProcessorService"/>'s events to
/// SignalR pushes on <see cref="IdentificationHub"/>. Wired up at startup;
/// stays subscribed for the app lifetime.
///
/// Three event flows:
///   • Queue depth / status change → <c>QueueChanged</c> (one ping per audience,
///     bursts coalesced over a short window; clients refetch on it)
///   • One item finished           → <c>MediaItemChanged</c> (single item)
///   • NeedsReview count change    → <c>NeedsReviewCountChanged</c>
///
/// Nothing is broadcast to every connection: queue entries (folder labels) only to admins,
/// a payload-less ping to everyone else, item / count events per role with
/// that role's folder filter applied (groups: see <see cref="IdentificationHub"/>).
/// </summary>
public sealed class IdentificationHubBroadcaster : IHostedService
{
    private readonly IdentificationQueueProcessorService _processor;
    private readonly IHubContext<IdentificationHub> _hub;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<IdentificationHubBroadcaster> _logger;
    private readonly CancellationTokenSource _stopping = new();

    public IdentificationHubBroadcaster(
        IdentificationQueueProcessorService processor,
        IHubContext<IdentificationHub> hub,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<IdentificationHubBroadcaster> logger)
    {
        _processor = processor;
        _hub       = hub;
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _processor.QueueChanged  += OnQueueChanged;
        _processor.ItemIdentified += OnItemIdentified;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _processor.QueueChanged  -= OnQueueChanged;
        _processor.ItemIdentified -= OnItemIdentified;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Trailing window that folds a burst of queue events into one
    /// broadcast. A job raises QueueChanged ~3 times (start, end, depth change)
    /// within a few hundred ms; every broadcast makes each admin client refetch
    /// the queue list.</summary>
    private static readonly TimeSpan QueueCoalesceWindow = TimeSpan.FromMilliseconds(400);

    /// <summary>1 = a QueueChanged arrived since the last broadcast started.</summary>
    private int _queueDirty;
    /// <summary>1 = the coalescing loop is running (at most one at a time).</summary>
    private int _queueLoopRunning;

    private void OnQueueChanged()
    {
        // Service events fire synchronously; flag it and let a single loop do
        // the DB load + send off-thread, once per coalescing window.
        Volatile.Write(ref _queueDirty, 1);
        if (Interlocked.CompareExchange(ref _queueLoopRunning, 1, 0) == 0)
            _ = Task.Run(QueueBroadcastLoopAsync);
    }

    private async Task QueueBroadcastLoopAsync()
    {
        while (true)
        {
            try
            {
                await Task.Delay(QueueCoalesceWindow, _stopping.Token);
                Volatile.Write(ref _queueDirty, 0);
                await BroadcastQueueAsync(_stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                Volatile.Write(ref _queueLoopRunning, 0);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OnQueueChanged broadcast failed");
            }

            // Leave unless an event landed while we were sending. Clear the flag
            // BEFORE re-checking: an event arriving after this point starts a
            // new loop itself (its CompareExchange now succeeds).
            Volatile.Write(ref _queueLoopRunning, 0);
            if (Volatile.Read(ref _queueDirty) == 0
                || Interlocked.CompareExchange(ref _queueLoopRunning, 1, 0) != 0)
                return;
        }
    }

    private async Task BroadcastQueueAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ONE message per audience per (coalesced) event. It used to send one
        // QueueChanged per Queued/Processing row (up to 20) and every client
        // refetches the whole queue on ANY QueueChanged — so each event became
        // up to 20 refetches of a 200-row list per admin tab. No client reads
        // the entry (all builds subscribe with a discard), so admins get the
        // head of the queue — the job in flight, i.e. the first row the old
        // fan-out sent — and the envelope shape stays the same.
        var head = await db.IdentificationQueues
            .Where(q => q.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Processing
                     || q.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Queued)
            .OrderBy(q => q.QueuedAt)
            .Select(RenameMappings.ToListEntryDto)
            .FirstOrDefaultAsync(ct);

        // Clients refetch their snapshot on ANY QueueChanged — and only then.
        // Once the last job finishes no Queued/Processing row is left, and the
        // NavPane counter / AI popup used to freeze on "N-1/N, busy" until a
        // reload (same for pause/resume on an empty queue) — so the ping still
        // goes out, carrying the latest finished job.
        head ??= await db.IdentificationQueues
            .OrderByDescending(q => q.ProcessedAt)
            .Select(RenameMappings.ToListEntryDto)
            .FirstOrDefaultAsync(ct);

        await _hub.Clients.Group(IdentificationHub.AdminGroup).SendAsync(
            IdentificationHubMethods.QueueChanged,
            new IdentificationProgressEnvelope(head!), ct);
        // Non-admins only use QueueChanged as a "refetch" trigger — they
        // get the ping without the entry (folder labels of every section).
        await _hub.Clients.Group(IdentificationHub.ViewerGroup).SendAsync(
            IdentificationHubMethods.QueueChanged,
            new IdentificationProgressEnvelope(null!), ct);

        // Per role: the count of NeedsReview items that role can see —
        // the same number its GET /api/media/needs-review returns.
        foreach (var role in await db.Roles.AsNoTracking().ToListAsync(ct))
        {
            var needsReview = await db.MediaItems
                .ApplyRoleFolderFilter(role)
                .CountAsync(m => m.IdentificationStatus
                    == Animarr.Web.Data.Models.IdentificationStatus.NeedsReview, ct);
            await _hub.Clients.Group(IdentificationHub.RoleGroup(role.Id)).SendAsync(
                IdentificationHubMethods.NeedsReviewCountChanged,
                new NeedsReviewCountEnvelope(needsReview), ct);
        }
    }

    private void OnItemIdentified(Guid folderId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var item = await db.MediaItems
                    .Include(m => m.Folder)
                    .FirstOrDefaultAsync(m => m.FolderId == folderId);
                if (item is null || item.Folder is null) return;

                // Only roles whose folder allowlist covers the item hear about it.
                var envelope = new MediaItemStatusEnvelope(
                    item.Id,
                    (Animarr.Shared.IdentificationStatus)(int)item.IdentificationStatus);
                foreach (var role in await db.Roles.AsNoTracking().ToListAsync())
                {
                    if (!role.AllowsFolder(item.Folder)) continue;
                    await _hub.Clients.Group(IdentificationHub.RoleGroup(role.Id)).SendAsync(
                        IdentificationHubMethods.MediaItemChanged, envelope);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OnItemIdentified broadcast failed");
            }
        });
    }
}
