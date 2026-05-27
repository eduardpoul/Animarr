namespace Animarr.Shared.Requests;

/// <summary>
/// POST body for <c>/api/categories</c> — create a custom (non built-in)
/// category. <see cref="Name"/> is required and must be unique;
/// other fields default to empty/0 when omitted.
/// </summary>
public sealed record CreateCategoryRequest(
    string  Name,
    string? Description,
    string? Hint,
    int?    SortOrder);

/// <summary>
/// PATCH body for <c>/api/categories/{id}</c>. Every field is optional — the
/// server applies only the non-null ones. For built-in categories, the server
/// rejects attempts to change <see cref="Name"/> with 409 Conflict but accepts
/// edits to the other fields.
/// </summary>
public sealed record UpdateCategoryRequest(
    string? Name,
    string? Description,
    string? Hint,
    int?    SortOrder,
    bool?   Enabled);

/// <summary>
/// PUT body for <c>/api/media/{id}/categories</c>. Overwrites the item's
/// manual category set (Source="manual") with the given list. The
/// classifier respects these rows on rescan — manual overrides win over
/// LLM auto-classification.
/// </summary>
public sealed record SetMediaCategoriesRequest(Guid[] CategoryIds);
