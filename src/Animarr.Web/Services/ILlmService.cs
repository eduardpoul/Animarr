namespace Animarr.Web.Services;

/// <summary>Result of LLM folder identification.</summary>
public class LlmIdentifyResult
{
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    /// <summary>anime | series | movie | unknown</summary>
    public string Type { get; set; } = "unknown";
    public int? Season { get; set; }
    public double Confidence { get; set; }
    public string? SuggestedRegex { get; set; }
}

/// <summary>Result of LLM regex suggestion for an unmatched filename.</summary>
public class LlmRegexResult
{
    public string Pattern { get; set; } = "";
    public string Explanation { get; set; } = "";
    public double Confidence { get; set; }
}

/// <summary>One candidate entry sent to the LLM for selection.</summary>
public class LlmCandidateItem
{
    public int     Index    { get; init; }
    public string  Source   { get; init; } = "";   // "tmdb_tv" | "tmdb_movie" | "mal"
    public string  Title    { get; init; } = "";
    public int?    Year     { get; init; }
    public string? Type     { get; init; }          // "tv" | "movie" | "anime"
    public string? Overview { get; init; }
}

public interface ILlmService
{
    /// <summary>Ask the LLM to identify a media folder from its path/name.</summary>
    Task<LlmIdentifyResult?> IdentifyFolderAsync(string folderPath, CancellationToken ct = default);

    /// <summary>Ask the LLM to suggest a rename regex for an unmatched filename.</summary>
    Task<LlmRegexResult?> SuggestRegexAsync(string fileName, CancellationToken ct = default);

    /// <summary>
    /// Given a list of search candidates, ask the LLM to pick the best index for the folder.
    /// Returns null when selection is not possible or LLM is unavailable.
    /// </summary>
    Task<int?> SelectCandidateAsync(string folderName, List<LlmCandidateItem> candidates, CancellationToken ct = default);

    /// <summary>Returns true if the configured LLM server is reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
