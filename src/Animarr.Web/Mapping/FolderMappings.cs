using Animarr.Shared.Models;
using EntityFolderWatcher = Animarr.Web.Data.Models.FolderWatcher;

namespace Animarr.Web.Mapping;

internal static class FolderMappings
{
    public static FolderWatcherDto ToDto(this EntityFolderWatcher entity)
        => new()
        {
            Id              = entity.Id,
            Path            = entity.Path,
            Label           = entity.Label,
            WatchEnabled    = entity.WatchEnabled,
            FolderType      = (Animarr.Shared.FolderType)(int)entity.FolderType,
            RenameEnabled   = entity.RenameEnabled,
            IdentifyEnabled = entity.IdentifyEnabled,
            IsSection       = entity.IsSection,
            FlatSection     = entity.FlatSection,
            SingleFilePath  = entity.SingleFilePath,
            ParentSectionId = entity.ParentSectionId,
            CreatedAt       = entity.CreatedAt,
            LastScannedAt   = entity.LastScannedAt,
            Hue             = entity.Hue,
            BackdropPath    = entity.BackdropPath,
        };
}
