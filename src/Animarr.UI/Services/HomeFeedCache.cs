using Animarr.Shared.Models;

namespace Animarr.UI.Services;

/// <summary>
/// The last Home feed, kept for the app session so coming back to Home (from
/// Settings, a title page, …) paints at once while a fresh copy loads behind
/// it. Tied to the user and server it was loaded for — a profile or server
/// switch never shows the previous one's rows.
/// </summary>
public sealed class HomeFeedCache
{
    private HomeFeedSnapshot? _last;

    public HomeFeedSnapshot? TryGet(Guid? userId, string serverBase) =>
        _last is { } s && userId is not null && s.UserId == userId
        && string.Equals(s.ServerBase, serverBase, StringComparison.OrdinalIgnoreCase)
            ? s
            : null;

    /// <summary>Keeps <paramref name="snapshot"/> when it loaded fully for a
    /// known user; a partial (offline) load never replaces a good one.</summary>
    public void Store(HomeFeedSnapshot snapshot)
    {
        if (snapshot.Complete && snapshot.UserId is not null) _last = snapshot;
    }
}

/// <summary>Everything Home renders, as loaded. Treated as immutable — Home
/// derives its filtered slices into new collections.</summary>
public sealed record HomeFeedSnapshot(
    Guid? UserId,
    string ServerBase,
    bool Complete,
    ContinueWatchItemDto[] Continue,
    ContinueWatchItemDto[] NextUp,
    RecCardDto[] ForYou,
    CalendarEventDto[] ThisWeek,
    List<FolderWatcherDto> Sections,
    Dictionary<Guid, HashSet<Guid>> ChildFolderIdsBySection,
    List<MediaItemDto> AllItems,
    List<MediaItemDto> FeaturedPool,
    int HeroInterval);
