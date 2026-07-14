namespace Animarr.Shared.Models;

/// <summary>
/// One airing-calendar entry: episode N of a library title airs (or aired)
/// at AiringAtUtc. Status tells the card how to render:
///   "upcoming"      — not aired yet;
///   "aired-waiting" — aired, no file on disk yet (the release-hunt state);
///   "in-library"    — aired AND the episode file is already downloaded.
/// </summary>
public sealed record CalendarEventDto(
    Guid     MediaItemId,
    string   Title,
    string?  PosterUrl,
    string?  BackdropUrl,
    int      Episode,
    DateTime AiringAtUtc,
    string   Status);
