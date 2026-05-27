using Animarr.Shared.Realtime;
using Animarr.Web.Data;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Hubs;

/// <summary>
/// Bridges <see cref="IdentificationQueueProcessorService"/>'s events to
/// SignalR pushes on <see cref="IdentificationHub"/>. Wired up at startup;
/// stays subscribed for the app lifetime.
///
/// Three event flows:
///   • Queue depth / status change → <c>QueueChanged</c> (snapshot of queue)
///   • One item finished           → <c>MediaItemChanged</c> (single item)
///   • NeedsReview count change    → <c>NeedsReviewCountChanged</c>
/// </summary>
public sealed class IdentificationHubBroadcaster : IHostedService
{
    private readonly IdentificationQueueProcessorService _processor;
    private readonly IHubContext<IdentificationHub> _hub;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<IdentificationHubBroadcaster> _logger;

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
        return Task.CompletedTask;
    }

    private void OnQueueChanged()
    {
        // Service events fire synchronously; offload the DB load + send.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var processing = await db.IdentificationQueues
                    .Include(q => q.Folder)
                    .Where(q => q.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Processing
                             || q.Status == Animarr.Web.Data.Models.IdentificationQueueStatus.Queued)
                    .OrderBy(q => q.QueuedAt)
                    .Take(20)
                    .ToListAsync();

                foreach (var entry in processing)
                {
                    await _hub.Clients.All.SendAsync(
                        IdentificationHubMethods.QueueChanged,
                        new IdentificationProgressEnvelope(entry.ToDto()));
                }

                var needsReview = await db.MediaItems
                    .CountAsync(m => m.IdentificationStatus
                        == Animarr.Web.Data.Models.IdentificationStatus.NeedsReview);
                await _hub.Clients.All.SendAsync(
                    IdentificationHubMethods.NeedsReviewCountChanged,
                    new NeedsReviewCountEnvelope(needsReview));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OnQueueChanged broadcast failed");
            }
        });
    }

    private void OnItemIdentified(Guid folderId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var item = await db.MediaItems
                    .FirstOrDefaultAsync(m => m.FolderId == folderId);
                if (item is null) return;

                await _hub.Clients.All.SendAsync(
                    IdentificationHubMethods.MediaItemChanged,
                    new MediaItemStatusEnvelope(
                        item.Id,
                        (Animarr.Shared.IdentificationStatus)(int)item.IdentificationStatus));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "OnItemIdentified broadcast failed");
            }
        });
    }
}
