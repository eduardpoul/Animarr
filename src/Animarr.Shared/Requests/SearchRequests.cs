using Animarr.Shared.Models;

namespace Animarr.Shared.Requests;

/// <summary>Free-text query against one source. Used by the candidate-picker
/// drawer when auto-identify produced no hit.</summary>
public sealed record SearchRequest(string Query, int? Year, MediaItemType? Type);

/// <summary>Server picks the source-specific endpoint and projects to a uniform
/// candidate list — UI just renders the cards.</summary>
public sealed record SearchResponse(IdentificationCandidateDto[] Candidates);
