namespace Animarr.Shared;

/// <summary>
/// Centralised list of API routes — keeps the server (endpoint mapping) and
/// the client (HTTP calls in <see cref="IAnimarrApiClient"/>) in lockstep.
/// Any new endpoint should appear here first.
///
/// Naming: lower-kebab segments, plural collection names, "{id}" placeholder
/// for path params. Helper methods build concrete URLs without manual string
/// concatenation, so a typo is caught at compile-time on the consumer side.
/// </summary>
public static class ApiRoutes
{
    // ─── Media catalog ────────────────────────────────────────────────────
    public const string Media             = "/api/media";
    public const string MediaById         = "/api/media/{id}";
    public const string MediaCandidates   = "/api/media/{id}/candidates";
    public const string MediaResolve      = "/api/media/{id}/resolve";
    public const string MediaRefresh      = "/api/media/{id}/refresh";
    public const string MediaPosterAlts   = "/api/media/{id}/poster-alternatives";
    public const string MediaBackdropAlts = "/api/media/{id}/backdrop-alternatives";
    public const string MediaBackdropList = "/api/media/backdrops";
    public const string MediaNeedsReview  = "/api/media/needs-review";
    public const string MediaContinue     = "/api/media/{id}/continue";

    // ─── Folder watchers ──────────────────────────────────────────────────
    public const string Folders        = "/api/folders";
    public const string FolderById     = "/api/folders/{id}";
    public const string FolderScan     = "/api/folders/{id}/scan";
    public const string FolderChildren = "/api/folders/{id}/children";
    public const string SectionFolders = "/api/folders/sections";

    // ─── Torrents ─────────────────────────────────────────────────────────
    public const string Torrents               = "/api/torrents";
    public const string TorrentById            = "/api/torrents/{id}";
    public const string TorrentAddMagnet       = "/api/torrents/add-magnet";
    public const string TorrentAddFile         = "/api/torrents/add-file";
    public const string TorrentPause           = "/api/torrents/{id}/pause";
    public const string TorrentResume          = "/api/torrents/{id}/resume";
    public const string TorrentFileTree        = "/api/torrents/{id}/file-tree";
    public const string TorrentFileSelections  = "/api/torrents/{id}/file-selections";
    public const string TorrentConfig          = "/api/torrent-config";

    // ─── Watch state ──────────────────────────────────────────────────────
    public const string WatchStatesForMedia = "/api/watch-states/{mediaItemId}";
    public const string WatchStateProgress  = "/api/watch-states/progress";
    public const string WatchStateToggle    = "/api/watch-states/toggle";
    public const string WatchStateReset     = "/api/watch-states/reset";

    // ─── Rename queue / history / patterns / ignore rules ────────────────
    public const string RenameQueue        = "/api/rename-queue";
    public const string RenameHistory      = "/api/rename-history";
    public const string RenameHistoryRevert= "/api/rename-history/{id}/revert";
    public const string Patterns           = "/api/patterns";
    public const string PatternById        = "/api/patterns/{id}";
    public const string IgnoreRules        = "/api/ignore-rules";
    public const string IgnoreRuleById     = "/api/ignore-rules/{id}";

    // ─── Identification queue ────────────────────────────────────────────
    public const string IdentificationQueue       = "/api/identification-queue";
    public const string IdentificationEnqueue     = "/api/identification-queue/enqueue";
    public const string IdentificationCancel      = "/api/identification-queue/{id}/cancel";

    // ─── Tags ────────────────────────────────────────────────────────────
    public const string MediaTags            = "/api/media-tags";
    public const string MediaTagById         = "/api/media-tags/{id}";
    public const string MediaTagAssign       = "/api/media-tags/{tagId}/items/{mediaItemId}";

    // ─── App config + hardware probe ─────────────────────────────────────
    public const string AppConfig          = "/api/app-config";
    public const string AppConfigByKey     = "/api/app-config/{key}";
    public const string HardwareInfo       = "/api/hardware-info";

    // ─── Metadata / search ───────────────────────────────────────────────
    public const string SearchTmdb   = "/api/search/tmdb";
    public const string SearchMal    = "/api/search/mal";
    public const string SearchImdb   = "/api/search/imdb";

    // ─── DLNA ────────────────────────────────────────────────────────────
    public const string DlnaRenderers = "/api/dlna/renderers";
    public const string DlnaPlay      = "/api/dlna/play";

    // ─── Media playback ──────────────────────────────────────────────────
    // These already exist in Animarr.Web and are referenced from the player
    // JS / Razor markup directly. Listed here for symmetry / discoverability.
    public const string Image          = "/api/image";
    public const string Video          = "/api/video";
    public const string File           = "/api/file";
    public const string PlaylistM3u    = "/api/playlist.m3u";
    public const string HlsStart       = "/api/hls/start";
    public const string HlsSegment     = "/api/hls/{token}/{file}";
    public const string HlsKeepalive   = "/api/hls/keepalive";
    public const string HlsStop        = "/api/hls/{token}";
    public const string HlsSessions    = "/api/hls/sessions";
    public const string Probe          = "/api/probe";
    public const string Subtitle       = "/api/subtitle";

    // ─── SignalR hubs ────────────────────────────────────────────────────
    /// <summary>Stream of <c>TorrentLiveStatsDto</c> snapshots (~500 ms cadence).</summary>
    public const string HubTorrents       = "/hubs/torrents";
    /// <summary>Stream of identification status / log updates.</summary>
    public const string HubIdentification = "/hubs/identification";

    // ─── URL builders ────────────────────────────────────────────────────

    public static string MediaItem(Guid id)            => MediaById.Replace("{id}", id.ToString());
    public static string MediaItemCandidates(Guid id)  => MediaCandidates.Replace("{id}", id.ToString());
    public static string MediaItemResolve(Guid id)     => MediaResolve.Replace("{id}", id.ToString());
    public static string MediaItemRefresh(Guid id)     => MediaRefresh.Replace("{id}", id.ToString());
    public static string MediaPosterAlternatives(Guid id)   => MediaPosterAlts.Replace("{id}", id.ToString());
    public static string MediaBackdropAlternatives(Guid id) => MediaBackdropAlts.Replace("{id}", id.ToString());
    public static string MediaContinueFor(Guid id)     => MediaContinue.Replace("{id}", id.ToString());

    public static string Folder(Guid id)               => FolderById.Replace("{id}", id.ToString());
    public static string FolderScanFor(Guid id)        => FolderScan.Replace("{id}", id.ToString());
    public static string FolderChildrenFor(Guid id)    => FolderChildren.Replace("{id}", id.ToString());

    public static string Torrent(Guid id)              => TorrentById.Replace("{id}", id.ToString());
    public static string TorrentPauseFor(Guid id)      => TorrentPause.Replace("{id}", id.ToString());
    public static string TorrentResumeFor(Guid id)     => TorrentResume.Replace("{id}", id.ToString());
    public static string TorrentFileTreeFor(Guid id)   => TorrentFileTree.Replace("{id}", id.ToString());
    public static string TorrentFileSelectionsFor(Guid id) => TorrentFileSelections.Replace("{id}", id.ToString());

    public static string WatchStatesFor(Guid mediaItemId) => WatchStatesForMedia.Replace("{mediaItemId}", mediaItemId.ToString());

    public static string Pattern(Guid id)              => PatternById.Replace("{id}", id.ToString());
    public static string IgnoreRule(Guid id)           => IgnoreRuleById.Replace("{id}", id.ToString());
    public static string MediaTag(Guid id)             => MediaTagById.Replace("{id}", id.ToString());
    public static string AppConfigKey(string key)      => AppConfigByKey.Replace("{key}", Uri.EscapeDataString(key));

    public static string RenameHistoryRevertFor(Guid id) => RenameHistoryRevert.Replace("{id}", id.ToString());
    public static string IdentificationCancelFor(Guid id) => IdentificationCancel.Replace("{id}", id.ToString());

    public static string HlsSegmentUrl(string token, string file)
        => HlsSegment.Replace("{token}", token).Replace("{file}", file);
}

/// <summary>
/// Well-known keys for the AppConfig key/value store. Mirror of the constants
/// in <c>Animarr.Web.Data.Models.AppConfigKeys</c> — kept in Animarr.Shared
/// so the UI can read/write them without taking a dependency on the server
/// data model.
/// </summary>
public static class AppConfigKeys
{
    // ─── Metadata sources ─────────────────────────────────────────────────
    public const string TmdbApiKey         = "metadata.tmdb_api_key";
    public const string MalClientId        = "metadata.mal_client_id";
    public const string SearchSourceOrder  = "metadata.search_source_order";

    // ─── Auto-identification ──────────────────────────────────────────────
    public const string AutoIdentifyEnabled   = "metadata.auto_identify_enabled";
    public const string DownloadEpisodeThumbs = "metadata.download_episode_thumbs";
    public const string IncludeEpisodeNameInFile = "rename.include_episode_name";
    public const string AutoApplyConfidence   = "metadata.auto_apply_confidence";
    public const string NeedsReviewConfidence = "metadata.needs_review_confidence";

    // ─── LLM ──────────────────────────────────────────────────────────────
    public const string LlmEnabled         = "llm.enabled";
    public const string LlmProvider        = "llm.provider";
    public const string LlmBaseUrl         = "llm.base_url";
    public const string LlmApiKey          = "llm.api_key";
    public const string LlmModel           = "llm.model";
    public const string LlmEpisodeMapping  = "llm.episode_mapping";

    // ─── Backdrop / appearance ────────────────────────────────────────────
    public const string BackdropEnabled    = "appearance.backdrop_enabled";
    public const string BackdropScope      = "appearance.backdrop_scope";
    public const string BackdropIntervalSec = "appearance.backdrop_interval_sec";
    public const string BackdropBlurPx     = "appearance.backdrop_blur_px";
    public const string BackdropBrightness = "appearance.backdrop_brightness";

    // ─── Language / theme ─────────────────────────────────────────────────
    public const string Language    = "appearance.language";
    public const string ThemeMode   = "appearance.theme_mode";
    public const string AccentColor = "appearance.accent_color";

    // ─── External player handoff ──────────────────────────────────────────
    public const string ExternalPlayer       = "playback.external_player";
    public const string ExternalPlayerCustom = "playback.external_player_custom";
}
