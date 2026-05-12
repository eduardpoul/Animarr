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

    // ─── Auto-identification ──────────────────────────────────────────────────
    public const string AutoIdentifyEnabled     = "metadata.auto_identify_enabled";
    public const string DownloadEpisodeThumbs   = "metadata.download_episode_thumbs";

    // Phase 1.2/2.3: rename the watched folder to "Title (Year)" after successful identification.
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
    /// <summary>"openai" = api.openai.com  |  "compatible" = custom OpenAI-compatible URL (Ollama, LM Studio, Groq, …)</summary>
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
}
