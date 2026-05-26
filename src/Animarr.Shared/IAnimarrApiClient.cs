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
    Task<string[]>        GetPosterAlternativesAsync(Guid id, CancellationToken ct = default);
    Task<string[]>        GetBackdropAlternativesAsync(Guid id, CancellationToken ct = default);
    Task<string[]>        GetBackdropRotationAsync(CancellationToken ct = default);
    Task<MediaItemDto[]>  GetNeedsReviewAsync(CancellationToken ct = default);
    Task<ContinueWatchDto?> GetContinueAsync(Guid mediaItemId, CancellationToken ct = default);

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

    // ─── Watch state ──────────────────────────────────────────────────────
    Task<WatchStateDto[]> GetWatchStatesAsync(Guid mediaItemId, CancellationToken ct = default);
    Task<WatchStateDto>   RecordProgressAsync(RecordProgressRequest request, CancellationToken ct = default);
    Task<WatchStateDto>   ToggleWatchedAsync(ToggleWatchedRequest request, CancellationToken ct = default);
    Task                  ResetProgressAsync(ResetProgressRequest request, CancellationToken ct = default);

    // ─── Rename + identification queues ──────────────────────────────────
    Task<RenameQueueEntryDto[]>   GetRenameQueueAsync(CancellationToken ct = default);
    Task<RenameHistoryEntryDto[]> GetRenameHistoryAsync(int? take, CancellationToken ct = default);
    Task                          RevertRenameAsync(Guid id, CancellationToken ct = default);
    Task<IdentificationQueueEntryDto[]> GetIdentificationQueueAsync(CancellationToken ct = default);
    Task                          EnqueueIdentificationAsync(Guid folderId, bool forceRefresh, CancellationToken ct = default);
    Task                          CancelIdentificationAsync(Guid id, CancellationToken ct = default);

    // ─── Patterns / ignore rules / tags ──────────────────────────────────
    Task<RenamePatternDto[]>  GetPatternsAsync(CancellationToken ct = default);
    Task<RenamePatternDto>    UpsertPatternAsync(Guid? id, UpsertPatternRequest request, CancellationToken ct = default);
    Task                      DeletePatternAsync(Guid id, CancellationToken ct = default);
    Task<IgnoreRuleDto[]>     GetIgnoreRulesAsync(CancellationToken ct = default);
    Task<IgnoreRuleDto>       UpsertIgnoreRuleAsync(Guid? id, UpsertIgnoreRuleRequest request, CancellationToken ct = default);
    Task                      DeleteIgnoreRuleAsync(Guid id, CancellationToken ct = default);
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
}
