using Animarr.Shared.Models;
using EntityWatchState = Animarr.Web.Data.Models.WatchState;

namespace Animarr.Web.Mapping;

internal static class WatchStateMappings
{
    public static WatchStateDto ToDto(this EntityWatchState entity)
        => new()
        {
            Id          = entity.Id,
            MediaItemId = entity.MediaItemId,
            Season      = entity.Season,
            Episode     = entity.Episode,
            FilePath    = entity.FilePath,
            IsWatched   = entity.IsWatched,
            ProgressMs  = entity.ProgressMs,
            RuntimeMs   = entity.RuntimeMs,
            TotalWatchTimeSec = entity.TotalWatchTimeSec,
            PlayCount   = entity.PlayCount,
            LastSeenAt  = entity.LastSeenAt,
            CreatedAt   = entity.CreatedAt,
        };
}
