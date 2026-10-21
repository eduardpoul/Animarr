using Animarr.Shared.Realtime;
using Animarr.Web.Mapping;
using Animarr.Web.Services;
using Microsoft.AspNetCore.SignalR;

namespace Animarr.Web.Hubs;

/// <summary>
/// SignalR hub for real-time torrent telemetry. Replaces what Blazor Server
/// used to do via its render-on-state-change mechanism — clients connect on
/// page load and receive snapshots pushed by <see cref="TorrentHubBroadcaster"/>
/// whenever the engine reports a change (at most ~1 Hz, plus a slow heartbeat).
///
/// The hub has no client-callable methods — telemetry only flows server →
/// client. Server-side commands still go through the REST endpoints.
/// </summary>
public sealed class TorrentHub(
    TorrentHubBroadcaster broadcaster,
    TorrentEngineService engine) : Hub
{
    public override async Task OnConnectedAsync()
    {
        broadcaster.OnClientConnected();
        await base.OnConnectedAsync();

        // The broadcaster only pushes on change (or every heartbeat), so a
        // fresh connection — a new tab, the Downloads page's own connection, a
        // reconnect — gets the current snapshot right away instead of waiting
        // up to a heartbeat for live rates/progress. Idle (no torrents) stays
        // quiet, same as the broadcaster. Never throw from here: SignalR skips
        // OnDisconnectedAsync for a connection whose OnConnectedAsync failed,
        // which would leak the connection count above.
        try
        {
            var stats = engine.GetAll();
            if (stats.Count > 0)
            {
                await Clients.Caller.SendAsync(
                    TorrentHubMethods.LiveSnapshot,
                    new TorrentSnapshotEnvelope(stats.Select(s => s.ToDto()).ToArray(), DateTime.UtcNow),
                    Context.ConnectionAborted);
            }
        }
        catch { /* the next change / heartbeat delivers it */ }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        broadcaster.OnClientDisconnected();
        return base.OnDisconnectedAsync(exception);
    }
}
