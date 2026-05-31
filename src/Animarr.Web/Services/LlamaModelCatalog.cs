namespace Animarr.Web.Services;

/// <summary>One curated GGUF model for the built-in ("embedded") llama.cpp provider.</summary>
public sealed record LlamaCatalogEntry(
    string Id,
    string DisplayName,
    string HfRepo,
    string FileName,
    long   ApproxBytes,
    int    RecommendedRamMb,
    bool   CpuFriendly,
    bool   GpuRecommended,
    string Notes);

/// <summary>
/// Static curated list of small, identification-grade GGUF models offered for
/// one-click download on the AI/LLM settings tab. Kept deliberately small — the
/// identification workload is short structured-JSON, not chat. Users who want
/// something bigger/different paste an arbitrary Hugging Face repo + file
/// ("custom" in the UI).
///
/// NOTE: Hugging Face file names occasionally change between quant re-uploads.
/// If a curated download 404s, the <see cref="LlamaCatalogEntry.FileName"/> here
/// is stale — update it or fall back to the custom repo/file field.
/// </summary>
public static class LlamaModelCatalog
{
    private const long MiB = 1024L * 1024;

    public static readonly IReadOnlyList<LlamaCatalogEntry> Entries = new[]
    {
        new LlamaCatalogEntry(
            Id: "qwen2.5-0.5b-instruct-q4",
            DisplayName: "Qwen2.5 0.5B Instruct (Q4_K_M)",
            HfRepo: "Qwen/Qwen2.5-0.5B-Instruct-GGUF",
            FileName: "qwen2.5-0.5b-instruct-q4_k_m.gguf",
            ApproxBytes: 491 * MiB,
            RecommendedRamMb: 1024,
            CpuFriendly: true,
            GpuRecommended: false,
            Notes: "Tiny & fast, lowest accuracy. Good for weak CPUs / low RAM."),
        new LlamaCatalogEntry(
            Id: "qwen2.5-1.5b-instruct-q4",
            DisplayName: "Qwen2.5 1.5B Instruct (Q4_K_M)",
            HfRepo: "Qwen/Qwen2.5-1.5B-Instruct-GGUF",
            FileName: "qwen2.5-1.5b-instruct-q4_k_m.gguf",
            ApproxBytes: 1024 * MiB,
            RecommendedRamMb: 2048,
            CpuFriendly: true,
            GpuRecommended: false,
            Notes: "Recommended default — matches the previous Ollama default. Good CPU balance."),
        new LlamaCatalogEntry(
            Id: "qwen2.5-3b-instruct-q4",
            DisplayName: "Qwen2.5 3B Instruct (Q4_K_M)",
            HfRepo: "Qwen/Qwen2.5-3B-Instruct-GGUF",
            FileName: "qwen2.5-3b-instruct-q4_k_m.gguf",
            ApproxBytes: 1900 * MiB,
            RecommendedRamMb: 4096,
            CpuFriendly: true,
            GpuRecommended: true,
            Notes: "More accurate identification, heavier on CPU — better with a GPU."),
        new LlamaCatalogEntry(
            Id: "llama-3.2-3b-instruct-q4",
            DisplayName: "Llama 3.2 3B Instruct (Q4_K_M)",
            HfRepo: "bartowski/Llama-3.2-3B-Instruct-GGUF",
            FileName: "Llama-3.2-3B-Instruct-Q4_K_M.gguf",
            ApproxBytes: 2020 * MiB,
            RecommendedRamMb: 4096,
            CpuFriendly: true,
            GpuRecommended: true,
            Notes: "Alternative 3B with strong multilingual handling."),
    };

    public static LlamaCatalogEntry? ById(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : Entries.FirstOrDefault(e => e.Id == id);
}
