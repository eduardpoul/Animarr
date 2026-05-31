using Animarr.Shared.Models;
using EntityRenamePattern = Animarr.Web.Data.Models.RenamePattern;
using EntityIdQueue       = Animarr.Web.Data.Models.IdentificationQueue;

namespace Animarr.Web.Mapping;

internal static class RenameMappings
{
    public static RenamePatternDto ToDto(this EntityRenamePattern p)
        => new()
        {
            Id              = p.Id,
            Name            = p.Name,
            Pattern         = p.Pattern,
            Scope           = (Animarr.Shared.PatternScope)(int)p.Scope,
            IsExcluded      = p.IsExcluded,
            GlobalPatternId = p.GlobalPatternId,
            Priority        = p.Priority,
            IsBuiltIn       = p.IsBuiltIn,
            ApplicableTo    = p.ApplicableTo is null
                ? null
                : (Animarr.Shared.FolderType)(int)p.ApplicableTo.Value,
            FolderId        = p.FolderId,
        };

    public static IdentificationQueueEntryDto ToDto(this EntityIdQueue q)
        => new(
            q.Id,
            q.FolderId,
            q.Folder?.Label ?? string.Empty,
            (Animarr.Shared.IdentificationQueueStatus)(int)q.Status,
            q.RetryCount,
            q.ErrorMessage,
            q.ForceRefresh,
            q.LogDetails,
            q.QueuedAt,
            q.ProcessedAt);
}
