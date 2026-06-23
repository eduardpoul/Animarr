using Animarr.Shared.Models;
using Animarr.Shared.Requests;

namespace Animarr.Shared;

/// <summary>
/// One contract every UI consumer (Animarr.UI pages, the WASM client, the
/// MAUI hybrid) calls into. Two concrete implementations exist:
///
///   • <c>HttpAnimarrApiClient</c> — talks to <see cref="ApiRoutes"/> over
///     HttpClient. Used by Animarr.Web.Client (WASM) and Animarr.App (MAUI).
///
///   • <c>ServerSideAnimarrApiClient</c> — lives inside Animarr.Web during the
///     transition from Blazor Server to API-only. Forwards every method to
///     the underlying EF / domain services directly. Will be deleted in
///     Phase 5 once Razor Server is removed.
///
/// Methods deliberately accept primitive args (no <c>HttpRequestMessage</c>
/// in the signature) so platform-specific concerns stay in the impl.
/// </summary>
public interface IAnimarrApiClient
{
    // ─── Media catalog ────────────────────────────────────────────────────
    Task<MediaItemDto[]>  GetMediaItemsAsync(MediaListQuery query, CancellationToken ct = default);
    Task<MediaItemDto?>   GetMediaItemAsync(Guid id, CancellationToken ct = default);
    Task<MediaItemDto>    UpdateMediaItemAsync(Guid id, UpdateMediaItemRequest request, CancellationToken ct = default);
    Task                  DeleteMediaItemAsync(Guid id, CancellationToken ct = default);
    Task<IdentificationCandidateDto[]> GetMediaCandidatesAsync(Guid id, CancellationToken ct = default);
    Task<MediaItemDto>    ResolveMediaCandidateAsync(Guid id, ResolveCandidateRequest request, CancellationToken ct = default);
    Task<MediaItemDto>    RefreshMetadataAsync(Guid id, CancellationToken ct = default);
    Task<ImageCandidateDto[]> GetPosterAlternativesAsync(Guid id, CancellationToken ct = default);
    Task<ImageCandidateDto[]> GetBackdropAlternativesAsync(Guid id, CancellationToken ct = default);
    Task<string[]>            GetBackdropRotationAsync(CancellationToken ct = default);
    Task<MediaItemDto[]>  GetNeedsReviewAsync(CancellationToken ct = default);
    Task<ContinueWatchDto?> GetContinueAsync(Guid mediaItemId, CancellationToken ct = default);
    Task<MediaFileDto[]> GetMediaFilesAsync(Guid mediaItemId, CancellationToken ct = default);
    /// <summary>Tier 2 — set a manual (season, episode) override for one file of
    /// this item. Null season/episode keeps the deterministic value for that
    /// field. The override survives re-scans and AI re-resolution.</summary>
    Task SetEpisodeMappingAsync(Guid mediaItemId, EpisodeMappingRequest request, CancellationToken ct = default);
    /// <summary>Clear a stored override for <paramref name="filePath"/> — revert
    /// that file to the deterministic parse.</summary>
    Task ClearEpisodeMappingAsync(Guid mediaItemId, string filePath, CancellationToken ct = default);
    /// <summary>Tier 1 — ask the LLM to place files the deterministic parser
    /// couldn't. Returns the number of files newly assigned an episode (0 when
    /// the LLM is disabled/unreachable).</summary>
    Task<int> ResolveEpisodesWithLlmAsync(Guid mediaItemId, CancellationToken ct = default);
    /// <summary>Compute &amp; store per-season absolute offsets (donghua: TMDB
    /// one season, disk split). Returns the diskSeason→offset map.</summary>
    Task<Dictionary<int, int>> ResolveSeasonOffsetsAsync(Guid mediaItemId, CancellationToken ct = default);
    Task ApplyImageAsync(Guid mediaItemId, ApplyImageRequest request, CancellationToken ct = default);

    // ─── Folder browsing (server-side filesystem) ────────────────────────
    Task<FolderBrowseEntryDto[]> BrowseFolderAsync(string? path, CancellationToken ct = default);

    // ─── Folder watchers ──────────────────────────────────────────────────
    Task<FolderWatcherDto[]> GetFoldersAsync(CancellationToken ct = default);
    Task<FolderWatcherDto?>  GetFolderAsync(Guid id, CancellationToken ct = default);
    Task<FolderWatcherDto>   CreateFolderAsync(CreateFolderRequest request, CancellationToken ct = default);
    Task<FolderWatcherDto>   UpdateFolderAsync(Guid id, UpdateFolderRequest request, CancellationToken ct = default);
    Task                     DeleteFolderAsync(Guid id, CancellationToken ct = default);
    Task                     ScanFolderAsync(Guid id, CancellationToken ct = default);
    Task<FolderWatcherDto[]> GetFolderChildrenAsync(Guid id, CancellationToken ct = default);
    Task<FolderWatcherDto[]> GetSectionFoldersAsync(CancellationToken ct = default);

    // ─── Torrents ─────────────────────────────────────────────────────────
    Task<TorrentRecordDto[]> GetTorrentsAsync(CancellationToken ct = default);
    Task<TorrentRecordDto?>  GetTorrentAsync(Guid id, CancellationToken ct = default);
    Task<TorrentRecordDto>   AddMagnetAsync(AddMagnetRequest request, CancellationToken ct = default);
    Task<TorrentRecordDto>   AddTorrentFileAsync(AddTorrentFileRequest request, CancellationToken ct = default);
    Task<TorrentRecordDto>   UpdateTorrentAsync(Guid id, UpdateTorrentRequest request, CancellationToken ct = default);
    Task                     DeleteTorrentAsync(Guid id, bool deleteFiles, CancellationToken ct = default);
    Task                     PauseTorrentAsync(Guid id, CancellationToken ct = default);
    Task                     ResumeTorrentAsync(Guid id, CancellationToken ct = default);
    Task<TorrentFileNodeDto> GetTorrentFileTreeAsync(Guid id, CancellationToken ct = default);
    Task                     UpdateTorrentFileSelectionsAsync(Guid id, UpdateFileSelectionsRequest request, CancellationToken ct = default);
    Task<TorrentConfigDto>   GetTorrentConfigAsync(CancellationToken ct = default);
    Task<TorrentConfigDto>   UpdateTorrentConfigAsync(TorrentConfigDto config, CancellationToken ct = default);
    /// <summary>Parse a .torrent file without touching the engine — used by
    /// the Add drawer to preview file list before the user commits.</summary>
    Task<ParsedTorrentDto?>  ParseTorrentAsync(ParseTorrentRequest request, CancellationToken ct = default);
    /// <summary>Drop arbitrary files into <paramref name="watcherId"/>'s folder
    /// (third tab of the Add download drawer — local files, no torrent).
    /// Returns the count of files actually written.</summary>
    Task<int>                UploadFilesToFolderAsync(Guid watcherId, IReadOnlyList<UploadFilePart> files, CancellationToken ct = default);

    // ─── Watch state ──────────────────────────────────────────────────────
    Task<WatchStateDto[]> GetWatchStatesAsync(Guid mediaItemId, CancellationToken ct = default);
    Task<WatchStateDto>   RecordProgressAsync(RecordProgressRequest request, CancellationToken ct = default);
    Task<WatchStateDto>   ToggleWatchedAsync(ToggleWatchedRequest request, CancellationToken ct = default);
    Task                  ResetProgressAsync(ResetProgressRequest request, CancellationToken ct = default);
    /// <summary>Bulk (un)mark a set of (season, episode) rows — the "mark
    /// earlier episodes too?" popup. Returns the affected rows.</summary>
    Task<WatchStateDto[]> MarkBulkWatchedAsync(MarkBulkWatchedRequest request, CancellationToken ct = default);

    // ─── Identification queue ────────────────────────────────────────────
    Task<IdentificationQueueEntryDto[]> GetIdentificationQueueAsync(CancellationToken ct = default);
    Task                          EnqueueIdentificationAsync(Guid folderId, bool forceRefresh, CancellationToken ct = default);
    Task                          CancelIdentificationAsync(Guid id, CancellationToken ct = default);
    Task                          PauseIdentificationAsync(CancellationToken ct = default);
    Task                          ResumeIdentificationAsync(CancellationToken ct = default);
    Task<IdentificationQueueStatusDto?> GetIdentificationStatusAsync(CancellationToken ct = default);
    Task<bool>                    RefreshThemeAsync(Guid mediaItemId, CancellationToken ct = default);
    Task<bool>                    SetThemeUrlAsync(Guid mediaItemId, string url, CancellationToken ct = default);

    // ─── Patterns / tags ─────────────────────────────────────────────────
    Task<RenamePatternDto[]>  GetPatternsAsync(CancellationToken ct = default);
    Task<RenamePatternDto>    UpsertPatternAsync(Guid? id, UpsertPatternRequest request, CancellationToken ct = default);
    Task                      DeletePatternAsync(Guid id, CancellationToken ct = default);
    Task<MediaTagDto[]>       GetMediaTagsAsync(CancellationToken ct = default);
    Task<MediaTagDto>         UpsertMediaTagAsync(Guid? id, UpsertMediaTagRequest request, CancellationToken ct = default);
    Task                      DeleteMediaTagAsync(Guid id, CancellationToken ct = default);
    Task                      AssignTagAsync(Guid tagId, Guid mediaItemId, CancellationToken ct = default);
    Task                      UnassignTagAsync(Guid tagId, Guid mediaItemId, CancellationToken ct = default);

    // ─── App config + hardware ───────────────────────────────────────────
    Task<AppConfigEntryDto[]> GetAppConfigAsync(CancellationToken ct = default);
    Task<AppConfigEntryDto?>  GetAppConfigValueAsync(string key, CancellationToken ct = default);
    Task                      SetAppConfigValueAsync(string key, string? value, CancellationToken ct = default);
    Task<HardwareReportDto>   GetHardwareReportAsync(bool rescan, CancellationToken ct = default);

    // ─── Search ──────────────────────────────────────────────────────────
    Task<SearchResponse> SearchTmdbAsync(SearchRequest request, CancellationToken ct = default);
    Task<SearchResponse> SearchMalAsync(SearchRequest request, CancellationToken ct = default);
    Task<SearchResponse> SearchImdbAsync(SearchRequest request, CancellationToken ct = default);

    // ─── DLNA ────────────────────────────────────────────────────────────
    Task<DlnaRendererDto[]> GetDlnaRenderersAsync(CancellationToken ct = default);
    Task                    DlnaPlayAsync(DlnaPlayRequest request, CancellationToken ct = default);

    // ─── Server identity (v5 multi-server) ───────────────────────────────
    /// <summary>Probe a candidate server URL for its <see cref="ServerInfoDto"/>.
    /// Used by the Discovery page + the server registry before any auth flow.
    /// The <paramref name="baseUrl"/> argument is mandatory because we're
    /// probing arbitrary other servers — not the one this client is bound to.</summary>
    Task<ServerInfoDto?>    GetServerInfoAsync(string baseUrl, CancellationToken ct = default);

    // ─── LLM diagnostics ─────────────────────────────────────────────────
    /// <summary>Pings the currently-configured LLM with a tiny prompt. Used by
    /// the AI settings tab's "Test connection" CTA so the admin can verify
    /// the provider / base URL / API key / model combo without queuing a real
    /// identification job. Saved AppConfig is what gets exercised — Save
    /// pending changes before calling this.</summary>
    Task<LlmTestResponse>   TestLlmAsync(CancellationToken ct = default);

    // ─── Embedded llama.cpp provider ─────────────────────────────────────
    /// <summary>Curated model catalog + installed files + free disk for the AI tab.</summary>
    Task<LlamaCatalogResponse> GetLlamaCatalogAsync(CancellationToken ct = default);
    /// <summary>Start downloading a GGUF model (curated id, or "custom" + repo/file).</summary>
    Task StartLlamaDownloadAsync(StartDownloadRequest request, CancellationToken ct = default);
    /// <summary>Progress of the single in-flight download (Phase="idle" when none).</summary>
    Task<DownloadProgressDto?> GetLlamaDownloadStatusAsync(CancellationToken ct = default);
    /// <summary>Cancel the in-flight download.</summary>
    Task CancelLlamaDownloadAsync(CancellationToken ct = default);
    /// <summary>Delete an installed model file (must not be the active one).</summary>
    Task DeleteLlamaModelAsync(string fileName, CancellationToken ct = default);
    /// <summary>Embedded llama-server runtime status (state/model/gpu/port).</summary>
    Task<EmbeddedStatusDto> GetEmbeddedStatusAsync(CancellationToken ct = default);
    /// <summary>Restart the embedded llama-server child; returns the new status.</summary>
    Task<EmbeddedStatusDto> RestartEmbeddedAsync(CancellationToken ct = default);

    // ─── Auth + per-user (v4) ────────────────────────────────────────────
    Task<AuthStatusDto>     GetAuthStatusAsync(CancellationToken ct = default);
    Task<UserDto?>          LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task                    LogoutAsync(CancellationToken ct = default);
    Task<UserDto?>          SetupInitialMasterAsync(SetupRequest request, CancellationToken ct = default);
    /// <summary>Minimal roster of every local profile for the switch-user /
    /// "who's watching" picker. Readable by ANY authenticated user (that's how
    /// non-admins switch profiles); exposes no username/email. v5.</summary>
    Task<RosterUserDto[]>   GetRosterAsync(CancellationToken ct = default);

    // ─── TV pairing (v5 Phase 7) ─────────────────────────────────────────
    /// <summary>TV mints a fresh 6-char code + QR payload. Anonymous —
    /// the TV has no cookie yet.</summary>
    Task<PairInitDto>       InitPairAsync(PairInitRequest request, CancellationToken ct = default);
    /// <summary>TV polls every ~2s. Returns the current status; when it
    /// flips to "confirmed", the same response carries Set-Cookie issuing
    /// the TV's auth cookie for the user who authorised it.</summary>
    Task<PairPollDto>       PollPairAsync(string code, CancellationToken ct = default);
    /// <summary>Phone (signed in) authorises the TV's pending code. Returns
    /// true on 204 NoContent, false on 404/410 (unknown / expired).</summary>
    Task<bool>              ConfirmPairAsync(ConfirmPairRequest request, CancellationToken ct = default);
    Task<MeDto?>            GetMeAsync(CancellationToken ct = default);
    Task<UserPreferencesDto> GetMyPreferencesAsync(CancellationToken ct = default);
    Task<UserPreferencesDto> UpdateMyPreferencesAsync(UpdatePreferencesRequest request, CancellationToken ct = default);
    Task                    ChangeMyPasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);
    /// <summary>PATCH /api/me/profile — currently-signed-in user updates their
    /// own Name / Email / AvatarHue. No ManageUsers permission required.
    /// Returns the refreshed UserDto so the caller can re-paint the topbar chip
    /// + avatar without an extra <c>GET /api/me</c> round-trip.</summary>
    Task<UserDto>           UpdateMyProfileAsync(UpdateMyProfileRequest request, CancellationToken ct = default);

    // ─── PIN (v5 per-user-per-device fast switch) ─────────────────────────
    /// <summary>POST /api/me/pin. Set or change the PIN. The server validates
    /// 4-digit format + re-verifies the current password as anti-CSRF.</summary>
    Task                    SetMyPinAsync(SetPinRequest request, CancellationToken ct = default);
    /// <summary>DELETE /api/me/pin. Clear the PIN entirely — subsequent
    /// switch-user requests to this user no longer prompt for a keypad.</summary>
    Task                    ClearMyPinAsync(ClearPinRequest request, CancellationToken ct = default);
    /// <summary>POST /api/auth/switch-user. Swap the auth cookie to another
    /// user on this server. Returns null when the target has a PIN configured
    /// and the supplied PIN doesn't verify (401) so the UI can show an error
    /// without try/catch.</summary>
    Task<UserDto?>          SwitchUserAsync(SwitchUserRequest request, CancellationToken ct = default);

    // ─── Per-user favorites + continue watching (v5) ─────────────────────
    Task                    AddFavoriteAsync(Guid mediaItemId, CancellationToken ct = default);
    Task                    RemoveFavoriteAsync(Guid mediaItemId, CancellationToken ct = default);
    Task<Guid[]>            GetFavoriteIdsAsync(CancellationToken ct = default);
    Task<ContinueWatchItemDto[]> GetContinueWatchingAsync(int take = 8, CancellationToken ct = default);
    /// <summary>"Next Up" feed — the next episode to watch per engaged series
    /// (next-in-line or a freshly-landed episode flagged <c>IsNew</c>).</summary>
    Task<ContinueWatchItemDto[]> GetNextUpAsync(int take = 12, CancellationToken ct = default);

    // ─── User + Role admin (v4, manageUsers permission required) ────────
    Task<UserDto[]>         GetUsersAsync(CancellationToken ct = default);
    Task<UserDto>           CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserDto>           UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);
    Task                    DeleteUserAsync(Guid id, CancellationToken ct = default);
    Task<RoleDto[]>         GetRolesAsync(CancellationToken ct = default);
    Task<RoleDto>           CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default);
    Task<RoleDto>           UpdateRoleAsync(Guid id, UpdateRoleRequest request, CancellationToken ct = default);
    Task                    DeleteRoleAsync(Guid id, CancellationToken ct = default);

    // ─── Categories ──────────────────────────────────────────────────────
    Task<CategoryDto[]>     GetCategoriesAsync(CancellationToken ct = default);
    Task<CategoryDto>       CreateCategoryAsync(CreateCategoryRequest request, CancellationToken ct = default);
    Task<CategoryDto>       UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default);
    Task                    DeleteCategoryAsync(Guid id, CancellationToken ct = default);
    Task                    RescanCategoriesAsync(CancellationToken ct = default);
    /// <summary>Replace the item's manual category set. Manual rows survive
    /// future LLM rescans (the classifier preserves Source="manual").</summary>
    Task                    SetMediaCategoriesAsync(Guid mediaItemId, Guid[] categoryIds, CancellationToken ct = default);
}
