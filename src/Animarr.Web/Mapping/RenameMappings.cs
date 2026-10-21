using System.Linq.Expressions;
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

    /// <summary>Queue row for lists and hub pushes: everything but
    /// <c>LogDetails</c>. That's the per-job scan log (1.5-5 KB a row) and no
    /// client reads it from the list — the AI popup only needs label, status and
    /// times — yet the 200-row list shipped it on every refetch. Projected in SQL
    /// so the column isn't even read.</summary>
    public static readonly Expression<Func<EntityIdQueue, IdentificationQueueEntryDto>> ToListEntryDto =
        q => new IdentificationQueueEntryDto(
            q.Id,
            q.FolderId,
            q.Folder.Label,
            (Animarr.Shared.IdentificationQueueStatus)(int)q.Status,
            q.RetryCount,
            q.ErrorMessage,
            q.ForceRefresh,
            null,
            q.QueuedAt,
            q.ProcessedAt);
}
