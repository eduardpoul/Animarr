namespace Animarr.Shared.Models;

/// <summary>
/// One catalog category — coarser than TMDB genres. Used by the Home chip
/// strip and the Server Settings admin row. <see cref="ItemCount"/> is the
/// number of MediaItems currently assigned to this category (counted
/// server-side on every list call; cheap with the (CategoryId) index).
///
/// Built-in categories (<see cref="BuiltIn"/>=true) cannot be deleted and
/// cannot be renamed. Their <see cref="Description"/>, <see cref="Hint"/>,
/// <see cref="SortOrder"/>, and <see cref="Enabled"/> can still be edited by
/// admins.
/// </summary>
public sealed record CategoryDto(
    Guid Id,
    string Name,
    bool BuiltIn,
    bool Enabled,
    string Description,
    string Hint,
    int SortOrder,
    int ItemCount);
