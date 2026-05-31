namespace Animarr.Shared.Requests;

// DTOs for the built-in ("embedded") llama.cpp provider — model catalog,
// download progress, and runtime status surfaced on the AI/LLM settings tab.
// The server downloads a GGUF model from Hugging Face onto the data volume and
// runs llama-server in-container; these DTOs drive the picker + progress UI.

/// <summary>One curated GGUF model offered for one-click download, enriched
/// with whether it's already present on disk.</summary>
public sealed record LlamaCatalogEntryDto(
    string Id,
    string DisplayName,
    string HfRepo,
    string FileName,
    long   ApproxBytes,
    int    RecommendedRamMb,
    bool   CpuFriendly,
    bool   GpuRecommended,
    string Notes,
    bool   Installed,
    long?  InstalledBytes);

/// <summary>A GGUF file already present under the models dir.</summary>
public sealed record InstalledModelDto(string FileName, long Bytes, bool IsActive);

/// <summary>Response of <c>GET /api/llm/embedded/catalog</c>.</summary>
public sealed record LlamaCatalogResponse(
    LlamaCatalogEntryDto[] Curated,
    InstalledModelDto[]    Installed,
    long                   ModelsDirFreeBytes);

/// <summary>Body of <c>POST /api/llm/embedded/download</c>. Either
/// <paramref name="ModelId"/> references a curated entry, or it is
/// <c>"custom"</c> with <paramref name="CustomRepo"/> + <paramref name="CustomFile"/>.</summary>
public sealed record StartDownloadRequest(
    string  ModelId,
    string? CustomRepo,
    string? CustomFile);

/// <summary>Live progress of the single in-flight model download
/// (<c>GET /api/llm/embedded/download</c>). A null body / <c>Phase = "idle"</c>
/// means nothing is downloading.</summary>
public sealed record DownloadProgressDto(
    string? ModelId,
    string? FileName,
    long    BytesReceived,
    long?   TotalBytes,
    double  Percent,
    string  Phase,
    string? Error);

/// <summary>Runtime status of the embedded llama-server child process
/// (<c>GET /api/llm/embedded/status</c>).</summary>
public sealed record EmbeddedStatusDto(
    string    State,
    string?   ModelId,
    string?   ModelFile,
    int       Port,
    bool      GpuActive,
    string    GpuMode,
    string?   Error,
    DateTime? ReadyAt);
