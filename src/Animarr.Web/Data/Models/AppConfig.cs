namespace Animarr.Web.Data.Models;

/// <summary>
/// Key/value store for user-configurable application settings persisted in the database.
/// Used for API keys, feature toggles, and appearance settings that must survive restarts
/// and be editable via UI without touching appsettings.json.
/// </summary>
public class AppConfig
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
}

/// <summary>Well-known keys for AppConfig. Use these constants instead of raw strings.</summary>
public static class AppConfigKeys
{
    // ─── Metadata sources ─────────────────────────────────────────────────────
    public const string TmdbApiKey   = "metadata.tmdb_api_key";
    public const string MalClientId  = "metadata.mal_client_id";

    /// <summary>JSON: [{id:"tmdb_tv",enabled:true},{id:"tmdb_movie",enabled:true}] — ordered by priority.</summary>
    public const string SearchSourceOrder = "metadata.search_source_order";

    /// <summary>Preferred metadata language (UI codes en/ru/uk/de/es; default en).
    /// Drives the TMDB <c>language</c> parameter for title/overview/genres and
    /// localized-poster selection. Empty fields fall back to English per field.</summary>
    public const string MetadataLanguage = "metadata.language";

    // ─── Auto-identification ──────────────────────────────────────────────────
    public const string AutoIdentifyEnabled     = "metadata.auto_identify_enabled";
    public const string DownloadEpisodeThumbs   = "metadata.download_episode_thumbs";

    // Deprecated: container-folder auto-rename was removed after two catastrophic
    // data-corruption incidents. The constant is kept so any value still sitting
    // in the AppConfig table can be located/cleaned up; nothing reads or writes it.
    [System.Obsolete("Container-folder auto-rename is no longer supported.")]
    public const string AutoRenameContainerFolder = "metadata.auto_rename_container_folder";
    // Phase 1.3: append episode name to renamed files — "S01E03 - Honky Tonk Women.mkv".
    public const string IncludeEpisodeNameInFile  = "rename.include_episode_name";
    // Phase 2.3: confidence threshold above which identification is auto-applied (default 0.85).
    public const string AutoApplyConfidence       = "metadata.auto_apply_confidence";
    // Phase 2.3: confidence threshold above which we keep the result as NeedsReview (default 0.50).
    public const string NeedsReviewConfidence     = "metadata.needs_review_confidence";

    // ─── LLM / AI provider ────────────────────────────────────────────────────
    /// <summary>Master switch — enables AI features.</summary>
    public const string LlmEnabled  = "llm.enabled";
    /// <summary>"openai" = api.openai.com  |  "compatible" = custom OpenAI-compatible URL (Ollama, LM Studio, Groq, …)  |  "embedded" = built-in llama.cpp running in-container against a downloaded GGUF model</summary>
    public const string LlmProvider = "llm.provider";
    /// <summary>Base URL for "compatible" provider, e.g. http://ollama:11434/v1 or http://localhost:1234/v1</summary>
    public const string LlmBaseUrl  = "llm.base_url";
    /// <summary>API key. Leave empty for local/unauthenticated services (Ollama, LM Studio).</summary>
    public const string LlmApiKey   = "llm.api_key";
    /// <summary>Model name, e.g. qwen2.5:1.5b, gpt-4o-mini, gemma3:4b</summary>
    public const string LlmModel    = "llm.model";

    // Phase 3.3: opt-in — use the LLM as a last-resort fallback for
    // file→episode mapping when regex patterns and natural ordering both miss.
    public const string LlmEpisodeMapping = "llm.episode_mapping";

    // ─── Embedded llama.cpp provider (provider = "embedded") ──────────────────
    /// <summary>Catalog id of the selected built-in model, or "custom".</summary>
    public const string LlmEmbeddedModelId     = "llm.embedded_model_id";
    /// <summary>Resolved GGUF filename under the models dir (/app/data/models) that the embedded llama-server loads.</summary>
    public const string LlmEmbeddedModelFile   = "llm.embedded_model_file";
    /// <summary>For "custom": Hugging Face repo "Org/Name-GGUF" the model file was/should be fetched from.</summary>
    public const string LlmEmbeddedHfRepo      = "llm.embedded_hf_repo";
    /// <summary>Loopback port the embedded llama-server listens on. Default 8091.</summary>
    public const string LlmEmbeddedPort        = "llm.embedded_port";
    /// <summary>GPU layers to offload (-ngl). 0 = CPU. Default 999 when Vulkan is active.</summary>
    public const string LlmEmbeddedGpuLayers   = "llm.embedded_gpu_layers";
    /// <summary>Context size (-c) for the embedded server. Default 4096.</summary>
    public const string LlmEmbeddedContextSize = "llm.embedded_ctx";
    /// <summary>Auto-stop the embedded server after this many seconds of no LLM activity (0 = never). Default 120.</summary>
    public const string LlmEmbeddedIdleTimeout = "llm.embedded_idle_timeout_sec";

    // ─── Legacy Ollama keys (kept for backward-compat migration reference) ────
    [Obsolete("Use LlmEnabled instead")]  public const string OllamaEnabled = "llm.ollama_enabled";
    [Obsolete("Use LlmBaseUrl instead")]  public const string OllamaUrl     = "llm.ollama_url";
    [Obsolete("Use LlmModel instead")]    public const string OllamaModel   = "llm.ollama_model";

    // ─── Backdrop / appearance ────────────────────────────────────────────────
    public const string BackdropEnabled    = "appearance.backdrop_enabled";
    /// <summary>"catalog" = only on catalog/Home page (default) | "everywhere" = all pages via MainLayout</summary>
    public const string BackdropScope      = "appearance.backdrop_scope";
    public const string BackdropIntervalSec = "appearance.backdrop_interval_sec";
    public const string BackdropBlurPx     = "appearance.backdrop_blur_px";
    public const string BackdropBrightness = "appearance.backdrop_brightness";

    // ─── Language / theme ─────────────────────────────────────────────────────
    public const string Language    = "appearance.language";
    public const string ThemeMode   = "appearance.theme_mode";
    public const string AccentColor = "appearance.accent_color";

    // ─── External player handoff ──────────────────────────────────────────────
    /// <summary>Which external player the in-browser "external" icon hands
    /// the file off to. One of: "mpv" (default), "iina", "infuse",
    /// "vlc_ios", "m3u" (universal download fallback), "custom".</summary>
    public const string ExternalPlayer       = "playback.external_player";
    /// <summary>For ExternalPlayer="custom" — URI template with {url}
    /// placeholder, e.g. "potplayer://{url}" or "mxplayer://play?url={url}".</summary>
    public const string ExternalPlayerCustom = "playback.external_player_custom";

    // ─── Skip intro / credits (segment detection) ─────────────────────────────
    /// <summary>AniSkip crowd-sourced OP/ED lookup by MAL id. Default true.</summary>
    public const string SegmentsAniSkipEnabled     = "segments.aniskip_enabled";
    /// <summary>Map named embedded container chapters to segments. Default true.</summary>
    public const string SegmentsChaptersEnabled    = "segments.chapters_enabled";
    /// <summary>Audio-fingerprint (chromaprint) detection in the background pass. Default true.</summary>
    public const string SegmentsChromaprintEnabled = "segments.chromaprint_enabled";
    /// <summary>Black-frame video analysis to approximate a credits start —
    /// expensive (decodes video), so off by default.</summary>
    public const string SegmentsBlackFrameEnabled  = "segments.blackframe_enabled";
}
