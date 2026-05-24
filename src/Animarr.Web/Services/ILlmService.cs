namespace Animarr.Web.Services;

/// <summary>Result of LLM folder identification.</summary>
public class LlmIdentifyResult
{
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    /// <summary>
    /// Canonical English title when the source name is in pinyin/romaji/Cyrillic
    /// transliteration. Used as an extra TMDB search query when the primary
    /// title returns nothing.
    /// </summary>
    public string? EnglishTitle { get; set; }
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

/// <summary>Lightweight telemetry surface for the AI/LLM settings page hero card.
/// Counters live in-memory in the singleton implementation; reset on restart.</summary>
public class LlmTelemetry
{
    /// <summary>Last 50 IdentifyFolder latencies in ms. Average computed on read.</summary>
    public int AverageLatencyMs { get; init; }
    public int CallCount        { get; init; }
    /// <summary>Probe state: did the last IsAvailable check pass?</summary>
    public bool LastProbeOk     { get; init; }
    public DateTime? LastProbeAt { get; init; }
}

public interface ILlmService
{
    /// <summary>Ask the LLM to identify a media folder from its path/name.</summary>
    /// <param name="folderPath">Path to the folder or single file to identify.</param>
    /// <param name="sectionLabel">User-defined label of the parent section
    /// (e.g. "Anime", "Movies"); a category hint that biases classification.</param>
    Task<LlmIdentifyResult?> IdentifyFolderAsync(string folderPath, string? sectionLabel = null, CancellationToken ct = default);

    /// <summary>Telemetry snapshot for the settings page. Cheap; safe to call on every render.</summary>
    LlmTelemetry GetTelemetry();

    /// <summary>Ask the LLM to suggest a rename regex for an unmatched filename.</summary>
    Task<LlmRegexResult?> SuggestRegexAsync(string fileName, CancellationToken ct = default);

    /// <summary>
    /// Given a list of search candidates, ask the LLM to pick the best index for the folder.
    /// Returns null when selection is not possible or LLM is unavailable.
    /// </summary>
    Task<int?> SelectCandidateAsync(string folderName, List<LlmCandidateItem> candidates, CancellationToken ct = default);

    /// <summary>Returns true if the configured LLM server is reachable.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 3.3: given a list of un-matched video files and a list of episode
    /// titles for a season, ask the LLM to suggest a file → episode mapping.
    /// Used only when pattern matching and fuzzy ordering both failed.
    /// Returns null on failure; otherwise a list of pairs (fileIndex, episodeNumber).
    /// </summary>
    Task<List<(int FileIndex, int EpisodeNumber)>?> MapFilesToEpisodesAsync(
        IReadOnlyList<string> fileNames,
        IReadOnlyList<(int Number, string Name)> episodes,
        CancellationToken ct = default);
}
