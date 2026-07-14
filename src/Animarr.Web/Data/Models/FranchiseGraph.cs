namespace Animarr.Web.Data.Models;

/// <summary>
/// One AniList media snapshot in the franchise graph. Keyed by AniList id —
/// deliberately NOT by MediaItemId, because most franchise members aren't in
/// the library (that's the point of the rail: show what's missing). Matching
/// onto library items happens at read time via AniListId/MalId.
/// </summary>
public class FranchiseNode
{
    public Guid Id { get; set; }

    public int AniListId { get; set; }
    public int? MalId { get; set; }

    public string  Title    { get; set; } = string.Empty;
    /// <summary>AniList format: TV / MOVIE / OVA / ONA / SPECIAL / TV_SHORT.</summary>
    public string? Format   { get; set; }
    public int?    Year     { get; set; }
    public int?    Episodes { get; set; }
    /// <summary>AniList CDN cover URL (hotlinked by the card).</summary>
    public string? CoverUrl { get; set; }
    /// <summary>RELEASING / FINISHED / NOT_YET_RELEASED / …</summary>
    public string? Status   { get; set; }

    public DateTime FetchedAtUtc { get; set; }
}

/// <summary>
/// A typed relation between two franchise nodes, as reported by AniList
/// (relationType of the edge From → To): SEQUEL, PREQUEL, PARENT, SIDE_STORY,
/// SPIN_OFF, ALTERNATIVE, SUMMARY, CHARACTER, OTHER.
/// </summary>
public class FranchiseEdge
{
    public Guid Id { get; set; }

    public int FromAniListId { get; set; }
    public int ToAniListId   { get; set; }
    public string RelationType { get; set; } = string.Empty;
}
