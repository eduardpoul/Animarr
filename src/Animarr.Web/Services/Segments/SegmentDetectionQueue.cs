using System.Threading.Channels;
using Animarr.Web.Data.Models;

namespace Animarr.Web.Services.Segments;

/// <summary>
/// Hand-off from the player's segment lookup (GET /api/media/{id}/segments) to
/// <see cref="SegmentDetectionBackgroundService"/>: an episode that the cheap
/// providers left without intro/credits gets one heavy (chromaprint) attempt,
/// but not at the moment the user presses Play — that used to start up to 8
/// full-priority ffmpeg decodes (~GBs of reads) racing the player's first
/// segments. The background service drains this queue ahead of its title pass,
/// once nobody is watching, at nice/idle-I/O priority.
///
/// Process-wide and in-memory on purpose (like the endpoint's per-process
/// debounce): a restart just loses a best-effort retry. Bounded, so a scripted
/// burst of opens can't grow it; a refused enqueue lets the caller retry later.
/// </summary>
public static class SegmentDetectionQueue
{
    /// <summary>One episode to detect. <paramref name="SeasonFiles"/> are its
    /// season peers (episode, path), captured by the endpoint's file resolve so
    /// the background pass doesn't redo it.</summary>
    public sealed record Request(
        MediaItem Item,
        int Season,
        int Episode,
        string FilePath,
        IReadOnlyList<(int Episode, string Path)> SeasonFiles);

    private const int Capacity = 64;

    // FullMode.Wait so TryWrite reports "full" (DropWrite would claim success).
    private static readonly Channel<Request> _channel = Channel.CreateBounded<Request>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    /// <summary>Queue an episode; false when the queue is full.</summary>
    public static bool TryEnqueue(Request request) => _channel.Writer.TryWrite(request);

    internal static ChannelReader<Request> Reader => _channel.Reader;
}
