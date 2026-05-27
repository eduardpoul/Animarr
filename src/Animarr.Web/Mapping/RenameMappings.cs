using Animarr.Shared.Models;
using EntityRenameQueue   = Animarr.Web.Data.Models.RenameQueue;
using EntityRenameHistory = Animarr.Web.Data.Models.RenameHistory;
using EntityRenamePattern = Animarr.Web.Data.Models.RenamePattern;
using EntityIgnoreRule    = Animarr.Web.Data.Models.IgnoreRule;
using EntityIdQueue       = Animarr.Web.Data.Models.IdentificationQueue;

namespace Animarr.Web.Mapping;

internal static class RenameMappings
{
    public static RenameQueueEntryDto ToDto(this EntityRenameQueue q)
        => new(
            q.Id,
            q.FolderId,
            q.Folder?.Label ?? string.Empty,
            q.FilePath,
            (Animarr.Shared.RenameQueueSource)(int)q.Source,
            q.QueuedAt,
            q.ProcessedAt,
            (Animarr.Shared.RenameQueueStatus)(int)q.Status,
            q.ErrorMessage,
            q.RetryCount);

    public static RenameHistoryEntryDto ToDto(this EntityRenameHistory h)
        => new(
            h.Id,
            h.FolderId,
            h.Folder?.Label ?? string.Empty,
            h.OriginalPath,
            h.NewPath,
            (Animarr.Shared.RenameStatus)(int)h.Status,
            h.ErrorMessage,
            h.ProcessedAt,
            h.IsReverted);

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

    public static IgnoreRuleDto ToDto(this EntityIgnoreRule r)
        => new(r.Id, r.Mask, (Animarr.Shared.RuleScope)(int)r.Scope, r.FolderId);

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
