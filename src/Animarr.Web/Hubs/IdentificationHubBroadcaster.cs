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
///   • Queue depth / status change → <c>QueueChanged</c> (snapshot of queue)
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
                    await _hub.Clients.Group(IdentificationHub.AdminGroup).SendAsync(
                        IdentificationHubMethods.QueueChanged,
                        new IdentificationProgressEnvelope(entry.ToDto()));
                }
                // Non-admins only use QueueChanged as a "refetch" trigger — they
                // get the ping without the entry (folder labels of every section).
                await _hub.Clients.Group(IdentificationHub.ViewerGroup).SendAsync(
                    IdentificationHubMethods.QueueChanged,
                    new IdentificationProgressEnvelope(null!));

                // Clients refetch their snapshot on ANY QueueChanged — and only
                // then. Once the last job finishes no Queued/Processing row is
                // left, so nothing was sent and the NavPane counter / AI popup
                // froze on "N-1/N, busy" until a reload (same for pause/resume on
                // an empty queue). Ping once with the latest finished job.
                if (processing.Count == 0)
                {
                    var last = await db.IdentificationQueues
                        .Include(q => q.Folder)
                        .OrderByDescending(q => q.ProcessedAt)
                        .FirstOrDefaultAsync();
                    await _hub.Clients.Group(IdentificationHub.AdminGroup).SendAsync(
                        IdentificationHubMethods.QueueChanged,
                        new IdentificationProgressEnvelope(last?.ToDto()!));
                }

                // Per role: the count of NeedsReview items that role can see —
                // the same number its GET /api/media/needs-review returns.
                foreach (var role in await db.Roles.AsNoTracking().ToListAsync())
                {
                    var needsReview = await db.MediaItems
                        .ApplyRoleFolderFilter(role)
                        .CountAsync(m => m.IdentificationStatus
                            == Animarr.Web.Data.Models.IdentificationStatus.NeedsReview);
                    await _hub.Clients.Group(IdentificationHub.RoleGroup(role.Id)).SendAsync(
                        IdentificationHubMethods.NeedsReviewCountChanged,
                        new NeedsReviewCountEnvelope(needsReview));
                }
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
