using Animarr.Shared.Realtime;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace Animarr.Web.Hubs;

/// <summary>
/// Polls <see cref="TorrentEngineService.GetLiveStats"/> on the engine's
/// own 500 ms cadence and pushes a snapshot to every connected client
/// over <see cref="TorrentHub"/>. Skips broadcasts when no clients are
/// listening (HubContext just no-ops in that case, but we still skip the
/// snapshot build to save CPU).
///
/// One hosted service per app, runs for the app's lifetime.
/// </summary>
public sealed class TorrentHubBroadcaster : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromMilliseconds(500);

    private readonly TorrentEngineService _engine;
    private readonly IHubContext<TorrentHub> _hub;
    private readonly ILogger<TorrentHubBroadcaster> _logger;

    public TorrentHubBroadcaster(
        TorrentEngineService engine,
        IHubContext<TorrentHub> hub,
        ILogger<TorrentHubBroadcaster> logger)
    {
        _engine  = engine;
        _hub     = hub;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a touch on startup so the engine can come up first.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var stats = _engine.GetAll();
                if (stats.Count > 0)
                {
                    var envelope = new TorrentSnapshotEnvelope(
                        stats.Select(s => s.ToDto()).ToArray(),
                        DateTime.UtcNow);
                    await _hub.Clients.All.SendAsync(
                        TorrentHubMethods.LiveSnapshot, envelope, stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TorrentHubBroadcaster tick failed");
            }

            try { await Task.Delay(Cadence, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
