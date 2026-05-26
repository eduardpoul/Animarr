using Microsoft.AspNetCore.SignalR;

namespace Animarr.Web.Hubs;

/// <summary>
/// SignalR hub for the identification queue + media-item status updates.
/// Subscribers (NeedsReview chip, Settings → Identification log, MediaDetail's
/// in-flight indicator) get pushed events instead of polling.
///
/// Like <see cref="TorrentHub"/>, this is push-only — server forwards events
/// from <see cref="IdentificationQueueProcessorService"/> via the broadcaster.
/// </summary>
public sealed class IdentificationHub : Hub
{
}
