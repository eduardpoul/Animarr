using Animarr.Shared.Realtime;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace Animarr.Web.Hubs;

/// <summary>
/// Pushes a <see cref="TorrentSnapshotEnvelope"/> to every connected client
/// over <see cref="TorrentHub"/> — but only when there is something to say:
///   • the engine raised <see cref="TorrentEngineService.StateChanged"/> (its
///     500 ms tick hashes the visible stats and fires only on a change; every
///     add / pause / remove / metadata / relink fires it too);
///   • at most one snapshot per <see cref="MinInterval"/> — a busy download
///     changes rates every tick, the UI doesn't need 2 Hz;
///   • a slow <see cref="Heartbeat"/> resync while torrents exist;
///   • nothing at all while no client is connected (connections are counted
///     by <see cref="TorrentHub"/>, which also hands every new connection the
///     current snapshot, so skipping here loses nothing).
/// It used to send the full list to Clients.All every 500 ms whenever a single
/// torrent existed — seeding/stopped ones included, changed or not, listened to
/// or not: ~30-120 KB/s per open tab for a library that just seeds.
///
/// Method name and payload shape (LiveSnapshot + TorrentSnapshotEnvelope) are
/// unchanged, so older web/phone builds keep working against this server.
///
/// One hosted service per app, runs for the app's lifetime.
/// </summary>
public sealed class TorrentHubBroadcaster : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Heartbeat   = TimeSpan.FromSeconds(15);

    private readonly TorrentEngineService _engine;
    private readonly IHubContext<TorrentHub> _hub;
    private readonly ILogger<TorrentHubBroadcaster> _logger;

    /// <summary>Released when the engine reports a change; the loop sleeps on it
    /// (with the heartbeat as timeout) instead of polling.</summary>
    private readonly SemaphoreSlim _changed = new(0);
    /// <summary>1 = the engine changed since the last snapshot went out. Also
    /// keeps a burst of StateChanged calls to a single semaphore release.</summary>
    private int _dirty;
    /// <summary>Live <see cref="TorrentHub"/> connections.</summary>
    private int _connections;

    public TorrentHubBroadcaster(
        TorrentEngineService engine,
        IHubContext<TorrentHub> hub,
        ILogger<TorrentHubBroadcaster> logger)
    {
        _engine  = engine;
        _hub     = hub;
        _logger  = logger;
    }

    internal void OnClientConnected()    => Interlocked.Increment(ref _connections);
    internal void OnClientDisconnected() => Interlocked.Decrement(ref _connections);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _engine.StateChanged += OnEngineChanged;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.StateChanged -= OnEngineChanged;
        return base.StopAsync(cancellationToken);
    }

    // Raised synchronously on the engine's tick / handler threads — only flag it.
    private void OnEngineChanged()
    {
        if (Interlocked.Exchange(ref _dirty, 1) == 0)
            _changed.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a touch on startup so the engine can come up first.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var lastCount  = 0;
        var lastSentAt = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Sleep until the engine reports a change or the heartbeat is due.
            try { await _changed.WaitAsync(Heartbeat, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                var changed       = Interlocked.Exchange(ref _dirty, 0) == 1;
                var heartbeatDue  = DateTime.UtcNow - lastSentAt >= Heartbeat;
                if ((changed || heartbeatDue) && Volatile.Read(ref _connections) > 0)
                {
                    var stats = _engine.GetAll();
                    // Idle (no torrents) stays quiet, but the transition TO empty is
                    // sent once: clients only learn the list changed from a snapshot,
                    // so deleting the last torrent left the downloads badge on its
                    // old count until a reload.
                    var send = stats.Count > 0 || lastCount > 0;
                    lastCount = stats.Count;
                    if (send)
                    {
                        var envelope = new TorrentSnapshotEnvelope(
                            stats.Select(s => s.ToDto()).ToArray(),
                            DateTime.UtcNow);
                        await _hub.Clients.All.SendAsync(
                            TorrentHubMethods.LiveSnapshot, envelope, stoppingToken);
                        lastSentAt = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TorrentHubBroadcaster tick failed");
            }

            // Rate cap: changes that land meanwhile stay flagged (_dirty) and go
            // out together in the next snapshot.
            try { await Task.Delay(MinInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
