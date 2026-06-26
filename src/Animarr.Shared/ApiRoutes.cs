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
    public const string MediaFiles        = "/api/media/{id}/files";
    /// <summary>PUT a manual (season, episode) override for one file of this
    /// item; DELETE (?path=…) clears it back to deterministic. Tier 2 of the
    /// episode-resolution model — the server stores it as Source="manual".</summary>
    public const string MediaFileMapping  = "/api/media/{id}/files/mapping";
    /// <summary>POST — Tier 1: ask the LLM to place files the deterministic
    /// parser left without an episode. Stores Source="llm" overrides; returns
    /// the count placed. No-op when the LLM is disabled/unreachable.</summary>
    public const string MediaResolveEpisodes = "/api/media/{id}/resolve-episodes";
    /// <summary>POST — compute per-season absolute offsets from TMDB air-date
    /// gaps (donghua where TMDB is one season but the disk is split). Stores
    /// MediaItem.SeasonOffsetsJson; returns the map. No-op when not applicable.</summary>
    public const string MediaResolveSeasons = "/api/media/{id}/resolve-seasons";
    /// <summary>GET — per-episode thumbnail: TMDB episode still when available,
    /// else an ffmpeg frame grabbed from the local video. Lazy + cached.</summary>
    public const string MediaEpisodeThumb = "/api/media/{id}/episode-thumb";
    /// <summary>GET — per-episode TMDB metadata (title / synopsis / air date /
    /// rating / runtime) for the detailed-list episode view. Lazy-fetched +
    /// cached in EpisodeMetadata; re-fetched when the metadata language or the
    /// title's identification changes. Returns <c>EpisodeMetaDto[]</c>.</summary>
    public const string MediaEpisodes     = "/api/media/{id}/episodes";
    public const string MediaApplyImage   = "/api/media/{id}/apply-image";
    /// <summary>GET — skip-intro/credits segments. With <c>?season=&amp;episode=</c>
    /// returns one <c>EpisodeSegmentsDto</c> (lazy-detecting via AniSkip on a
    /// miss); without, returns all known <c>EpisodeSegmentsDto[]</c> for the item.
    /// PUT writes a manual override (Source=Manual, never clobbered by detection).</summary>
    public const string MediaSegments     = "/api/media/{id}/segments";
    /// <summary>GET — skip-intro/credits scan progress (<c>SegmentScanStatusDto</c>).</summary>
    public const string SegmentsStatus    = "/api/segments/status";
    /// <summary>POST — reset scan flags so the background pass reprocesses all titles.</summary>
    public const string SegmentsRescan    = "/api/segments/rescan";
    public const string MediaTheme        = "/api/media/{id}/theme";
    public const string MediaThemeRefresh = "/api/media/{id}/theme/refresh";
    public const string MediaThemeManual  = "/api/media/{id}/theme/manual";
    public const string FoldersBrowse     = "/api/folders/browse";

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
    /// <summary>POST a Base64-encoded .torrent and get back its parsed file
    /// list — used by the Add drawer to preview contents before committing.</summary>
    public const string TorrentParse           = "/api/torrents/parse";
    /// <summary>POST multipart files + a destination folder id. Drops the
    /// files into the folder as a plain copy (no torrent / magnet involved).
    /// Used by the "Upload files" tab of the Add download drawer.</summary>
    public const string FolderUpload           = "/api/folders/{watcherId}/upload";

    // ─── Watch state ──────────────────────────────────────────────────────
    public const string WatchStatesForMedia = "/api/watch-states/{mediaItemId}";
    public const string WatchStateProgress  = "/api/watch-states/progress";
    public const string WatchStateToggle    = "/api/watch-states/toggle";
    public const string WatchStateReset     = "/api/watch-states/reset";
    public const string WatchStateMarkBulk  = "/api/watch-states/mark-bulk";

    // ─── Patterns ────────────────────────────────────────────────────────
    public const string Patterns           = "/api/patterns";
    public const string PatternById        = "/api/patterns/{id}";

    // ─── Identification queue ────────────────────────────────────────────
    public const string IdentificationQueue       = "/api/identification-queue";
    public const string IdentificationEnqueue     = "/api/identification-queue/enqueue";
    public const string IdentificationCancel      = "/api/identification-queue/{id}/cancel";
    public const string IdentificationStatus      = "/api/identification-queue/status";
    public const string IdentificationPause       = "/api/identification-queue/pause";
    public const string IdentificationResume      = "/api/identification-queue/resume";

    // ─── Tags ────────────────────────────────────────────────────────────
    public const string MediaTags            = "/api/media-tags";
    public const string MediaTagById         = "/api/media-tags/{id}";
    public const string MediaTagAssign       = "/api/media-tags/{tagId}/items/{mediaItemId}";

    // ─── Categories ──────────────────────────────────────────────────────
    public const string Categories      = "/api/categories";
    public const string CategoryById    = "/api/categories/{id}";
    public const string CategoryRescan  = "/api/categories/rescan";
    public const string CategoryStats   = "/api/categories/stats";
    /// <summary>PUT to overwrite a media item's manual category set
    /// (Source="manual"). The classifier won't overwrite these on rescan.</summary>
    public const string MediaCategories = "/api/media/{id}/categories";

    // ─── App config + hardware probe ─────────────────────────────────────
    public const string AppConfig          = "/api/app-config";
    public const string AppConfigByKey     = "/api/app-config/{key}";

    /// <summary>GET — current metadata language + relocalisation progress
    /// (<c>MetadataLanguageStatusDto</c>). PUT <c>{ language }</c> — change the
    /// language and kick off re-fetching TMDB texts/posters for the library.</summary>
    public const string MetadataLanguage   = "/api/metadata/language";

    // ─── LLM diagnostics ──────────────────────────────────────────────────
    /// <summary>Live "ping" test against the configured LLM endpoint. Returns
    /// {ok, latencyMs, sample, error} so the AI tab can show success / failure
    /// without restarting identification. Saved config is used — Save before
    /// hitting Test.</summary>
    public const string LlmTest            = "/api/llm/test";

    // ─── Embedded llama.cpp provider (provider = "embedded") ──────────────
    /// <summary>GET — curated model catalog + installed files + free disk.</summary>
    public const string LlmEmbeddedCatalog  = "/api/llm/embedded/catalog";
    /// <summary>POST start / GET status / DELETE cancel the single in-flight download.</summary>
    public const string LlmEmbeddedDownload = "/api/llm/embedded/download";
    /// <summary>GET installed model files.</summary>
    public const string LlmEmbeddedModels   = "/api/llm/embedded/models";
    /// <summary>Route template for DELETE of one installed model file.</summary>
    public const string LlmEmbeddedModelByFile = "/api/llm/embedded/models/{file}";
    /// <summary>GET embedded runtime status (state/model/gpu/port).</summary>
    public const string LlmEmbeddedStatus   = "/api/llm/embedded/status";
    /// <summary>POST — restart the embedded llama-server child.</summary>
    public const string LlmEmbeddedRestart  = "/api/llm/embedded/restart";
    /// <summary>Builds the DELETE URL for a single installed model file.</summary>
    public static string LlmEmbeddedModel(string file) => $"/api/llm/embedded/models/{Uri.EscapeDataString(file)}";

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
    /// <summary>GET <c>?path=&lt;video&gt;</c> — external (sideload) audio + subtitle
    /// tracks discovered next to / under the video. Returns <c>ExternalTrackDto[]</c>.
    /// Drives the player's external-track entries in the Audio / Subtitles pickers.</summary>
    public const string ExternalTracks = "/api/external-tracks";

    // ─── Auth (v4) ───────────────────────────────────────────────────────
    public const string AuthLogin    = "/api/auth/login";
    public const string AuthLogout   = "/api/auth/logout";
    public const string AuthSetup    = "/api/auth/setup";
    public const string AuthStatus   = "/api/auth/status";
    /// <summary>POST — swap the auth cookie to another local user. Requires
    /// the target user's PIN when their <c>HasPin</c> is true. v5 per-user-
    /// per-device fast switch.</summary>
    public const string AuthSwitchUser = "/api/auth/switch-user";
    /// <summary>GET — minimal roster (<c>RosterUserDto[]</c>) of every local
    /// user for the switch-user / "who's watching" picker. Readable by ANY
    /// authenticated user (that's how non-admins switch profiles); carries no
    /// username/email. v5.</summary>
    public const string AuthRoster   = "/api/auth/roster";
    public const string Me           = "/api/me";
    public const string MePreferences = "/api/me/preferences";
    public const string MePassword   = "/api/me/password";
    /// <summary>PATCH — current user edits their own Name / Email / AvatarHue
    /// without the ManageUsers permission. v5.</summary>
    public const string MeProfile    = "/api/me/profile";
    /// <summary>POST sets/changes the current user's PIN, DELETE clears it.
    /// Both require the current password as anti-CSRF. v5.</summary>
    public const string MePin        = "/api/me/pin";

    /// <summary>POST adds, DELETE removes the user's favorite star for the
    /// given media item. v5 — was a global Set before.</summary>
    public const string MeFavorite   = "/api/me/favorites/{mediaItemId}";
    public const string MeFavorites  = "/api/me/favorites";

    /// <summary>GET — recent in-progress watch states for the current user,
    /// joined to their MediaItem rows. Drives the v5 Continue Watching
    /// hero on Home.</summary>
    public const string MeContinue   = "/api/me/continue";
    public const string MeNextUp     = "/api/me/next-up";

    // ─── Server identity (v5 multi-server) ───────────────────────────────
    /// <summary>Anonymous probe — returns <c>ServerInfoDto</c>. Hit by the
    /// Discovery page to populate server cards before login, and by the
    /// registry to refresh stale entries (title count / version drift).</summary>
    public const string ServerInfo   = "/api/server/info";

    // ─── TV pairing (v5 Phase 7) ─────────────────────────────────────────
    /// <summary>POST anonymous — TV asks for a fresh 6-char pair code +
    /// QR payload. Returns <c>PairInitDto</c>.</summary>
    public const string PairInit     = "/api/auth/pair/init";
    /// <summary>GET anonymous — TV polls every 2s with the code it got from
    /// init. Returns <c>PairPollDto</c>. When status="confirmed" the response
    /// also carries Set-Cookie so the next page navigation is authenticated.</summary>
    public const string PairPoll     = "/api/auth/pair/poll";
    /// <summary>POST authenticated — phone authorises the TV's pending code
    /// with its own identity. 204 on success, 404 unknown code, 410 already
    /// consumed / expired.</summary>
    public const string PairConfirm  = "/api/auth/pair/confirm";
    /// <summary>GET anonymous — server-rendered QR PNG for the given code.
    /// Decodes back to the same animarr:// payload init returned.</summary>
    public const string PairQr       = "/api/auth/pair/qr";

    // ─── User + Role admin (v4) ──────────────────────────────────────────
    public const string Users       = "/api/users";
    public const string UserById    = "/api/users/{id}";
    public const string Roles       = "/api/roles";
    public const string RoleById    = "/api/roles/{id}";

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
    public static string MediaThemeFor(Guid id)        => MediaTheme.Replace("{id}", id.ToString());
    public static string MediaThemeRefreshFor(Guid id) => MediaThemeRefresh.Replace("{id}", id.ToString());
    public static string MediaThemeManualFor(Guid id)  => MediaThemeManual.Replace("{id}", id.ToString());
    public static string MediaFilesFor(Guid id)        => MediaFiles.Replace("{id}", id.ToString());
    public static string MediaFileMappingFor(Guid id)  => MediaFileMapping.Replace("{id}", id.ToString());
    public static string MediaResolveEpisodesFor(Guid id) => MediaResolveEpisodes.Replace("{id}", id.ToString());
    public static string MediaResolveSeasonsFor(Guid id) => MediaResolveSeasons.Replace("{id}", id.ToString());
    public static string MediaEpisodeThumbFor(Guid id) => MediaEpisodeThumb.Replace("{id}", id.ToString());
    public static string MediaEpisodesFor(Guid id)     => MediaEpisodes.Replace("{id}", id.ToString());
    public static string MediaSegmentsFor(Guid id)     => MediaSegments.Replace("{id}", id.ToString());
    public static string MediaApplyImageFor(Guid id)   => MediaApplyImage.Replace("{id}", id.ToString());

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
    public static string MediaTag(Guid id)             => MediaTagById.Replace("{id}", id.ToString());
    public static string AppConfigKey(string key)      => AppConfigByKey.Replace("{key}", Uri.EscapeDataString(key));

    public static string IdentificationCancelFor(Guid id) => IdentificationCancel.Replace("{id}", id.ToString());

    public static string HlsSegmentUrl(string token, string file)
        => HlsSegment.Replace("{token}", token).Replace("{file}", file);

    public static string User(Guid id) => UserById.Replace("{id}", id.ToString());
    public static string Role(Guid id) => RoleById.Replace("{id}", id.ToString());
    public static string Category(Guid id) => CategoryById.Replace("{id}", id.ToString());
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

    /// <summary>JSON array of <c>{Id, Enabled}</c> entries that drives
    /// <c>MetadataService.GatherCandidatesAsync</c>. Source IDs the server
    /// honours: <c>tmdb_tv</c>, <c>tmdb_movie</c>, <c>mal</c>,
    /// <c>imdb_search</c>. The two TMDb variants share the same key but
    /// toggle independently (Movies vs TV); the Settings UI groups them
    /// under one "TMDb" toggle for clarity.</summary>
    public const string SearchSourceOrder  = "metadata.search_source_order";

    /// <summary>Preferred metadata language (UI codes en/ru/uk/de/es; default en).
    /// Server re-fetches TMDB title/overview/genres (+ localized posters) in this
    /// language; missing fields fall back to English.</summary>
    public const string MetadataLanguage   = "metadata.language";

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

    // Embedded llama.cpp provider (provider = "embedded")
    public const string LlmEmbeddedModelId     = "llm.embedded_model_id";
    public const string LlmEmbeddedModelFile   = "llm.embedded_model_file";
    public const string LlmEmbeddedHfRepo      = "llm.embedded_hf_repo";
    public const string LlmEmbeddedPort        = "llm.embedded_port";
    public const string LlmEmbeddedGpuLayers   = "llm.embedded_gpu_layers";
    public const string LlmEmbeddedContextSize = "llm.embedded_ctx";
    public const string LlmEmbeddedIdleTimeout = "llm.embedded_idle_timeout_sec";

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

    // ─── Skip intro / credits (segment detection) ─────────────────────────
    public const string SegmentsAniSkipEnabled     = "segments.aniskip_enabled";
    public const string SegmentsChaptersEnabled    = "segments.chapters_enabled";
    public const string SegmentsChromaprintEnabled = "segments.chromaprint_enabled";
    public const string SegmentsBlackFrameEnabled  = "segments.blackframe_enabled";
}
