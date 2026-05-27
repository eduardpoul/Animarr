using Animarr.Shared.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Animarr.Web.Hubs;

/// <summary>
/// SignalR hub for real-time torrent telemetry. Replaces what Blazor Server
/// used to do via its render-on-state-change mechanism — clients connect on
/// page load and receive snapshots pushed by <see cref="TorrentHubBroadcaster"/>
/// at the engine's existing 500 ms cadence.
///
/// The hub has no client-callable methods — telemetry only flows server →
/// client. Server-side commands still go through the REST endpoints.
/// </summary>
public sealed class TorrentHub : Hub
{
    // Intentionally empty — push-only.
}
