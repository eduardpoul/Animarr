using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;

namespace Animarr.Shared;

/// <summary>
/// HttpClient-backed <see cref="IAnimarrApiClient"/> shared by every
/// non-server runtime (Animarr.Web.Client WASM, Animarr.App MAUI Hybrid).
/// Each method is a straight call into <see cref="ApiRoutes"/> with the
/// matching DTO serialised over JSON.
///
/// 404 / 204 are surfaced as <c>null</c> / no-op rather than thrown
/// exceptions so the UI doesn't have to wrap every call in try/catch.
/// Real failures (5xx, network) still throw via
/// <see cref="HttpClient.SendAsync"/>'s default error handling.
/// </summary>
public sealed class HttpAnimarrApiClient : IAnimarrApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public HttpAnimarrApiClient(HttpClient http) { _http = http; }

    // ─── Media catalog ────────────────────────────────────────────────────

    public async Task<MediaItemDto[]> GetMediaItemsAsync(MediaListQuery query, CancellationToken ct = default)
    {
        var url = BuildMediaListUrl(query);
        return await _http.GetFromJsonAsync<MediaItemDto[]>(url, JsonOpts, ct)
            ?? Array.Empty<MediaItemDto>();
    }

    public Task<MediaItemDto?> GetMediaItemAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<MediaItemDto>(ApiRoutes.MediaItem(id), ct);

    public async Task<MediaItemDto> UpdateMediaItemAsync(Guid id, UpdateMediaItemRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(ApiRoutes.MediaItem(id), request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MediaItemDto>(JsonOpts, ct))!;
    }

    public async Task DeleteMediaItemAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(ApiRoutes.MediaItem(id), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IdentificationCandidateDto[]> GetMediaCandidatesAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IdentificationCandidateDto[]>(ApiRoutes.MediaItemCandidates(id), JsonOpts, ct)
            ?? Array.Empty<IdentificationCandidateDto>();

    public async Task<MediaItemDto> ResolveMediaCandidateAsync(Guid id, ResolveCandidateRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.MediaItemResolve(id), request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MediaItemDto>(JsonOpts, ct))!;
    }

    public async Task<MediaItemDto> RefreshMetadataAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(ApiRoutes.MediaItemRefresh(id), null, ct);
        resp.EnsureSuccessStatusCode();
        // The endpoint replies 202 Accepted without a body — the caller should
        // refetch via GetMediaItemAsync to see the updated state after the
        // background identification finishes. Return a placeholder shell.
        return new MediaItemDto { Id = id };
    }

    public async Task<string[]> GetPosterAlternativesAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string[]>(ApiRoutes.MediaPosterAlternatives(id), JsonOpts, ct)
            ?? Array.Empty<string>();

    public async Task<string[]> GetBackdropAlternativesAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string[]>(ApiRoutes.MediaBackdropAlternatives(id), JsonOpts, ct)
            ?? Array.Empty<string>();

    public async Task<string[]> GetBackdropRotationAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<string[]>(ApiRoutes.MediaBackdropList, JsonOpts, ct)
            ?? Array.Empty<string>();

    public async Task<MediaItemDto[]> GetNeedsReviewAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<MediaItemDto[]>(ApiRoutes.MediaNeedsReview, JsonOpts, ct)
            ?? Array.Empty<MediaItemDto>();

    public Task<ContinueWatchDto?> GetContinueAsync(Guid mediaItemId, CancellationToken ct = default)
        => GetOrNullAsync<ContinueWatchDto>(ApiRoutes.MediaContinueFor(mediaItemId), ct);

    public async Task<MediaFileDto[]> GetMediaFilesAsync(Guid mediaItemId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<MediaFileDto[]>(ApiRoutes.MediaFilesFor(mediaItemId), JsonOpts, ct)
            ?? Array.Empty<MediaFileDto>();

    public async Task ApplyImageAsync(Guid mediaItemId, ApplyImageRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.MediaApplyImageFor(mediaItemId), request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<FolderBrowseEntryDto[]> BrowseFolderAsync(string? path, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(path)
            ? ApiRoutes.FoldersBrowse
            : $"{ApiRoutes.FoldersBrowse}?path={Uri.EscapeDataString(path)}";
        return await _http.GetFromJsonAsync<FolderBrowseEntryDto[]>(url, JsonOpts, ct)
            ?? Array.Empty<FolderBrowseEntryDto>();
    }

    // ─── Folder watchers ──────────────────────────────────────────────────

    public async Task<FolderWatcherDto[]> GetFoldersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<FolderWatcherDto[]>(ApiRoutes.Folders, JsonOpts, ct)
            ?? Array.Empty<FolderWatcherDto>();

    public Task<FolderWatcherDto?> GetFolderAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<FolderWatcherDto>(ApiRoutes.Folder(id), ct);

    public async Task<FolderWatcherDto> CreateFolderAsync(CreateFolderRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.Folders, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FolderWatcherDto>(JsonOpts, ct))!;
    }

    public async Task<FolderWatcherDto> UpdateFolderAsync(Guid id, UpdateFolderRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(ApiRoutes.Folder(id), request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FolderWatcherDto>(JsonOpts, ct))!;
    }

    public async Task DeleteFolderAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(ApiRoutes.Folder(id), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ScanFolderAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(ApiRoutes.FolderScanFor(id), null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<FolderWatcherDto[]> GetFolderChildrenAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<FolderWatcherDto[]>(ApiRoutes.FolderChildrenFor(id), JsonOpts, ct)
            ?? Array.Empty<FolderWatcherDto>();

    public async Task<FolderWatcherDto[]> GetSectionFoldersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<FolderWatcherDto[]>(ApiRoutes.SectionFolders, JsonOpts, ct)
            ?? Array.Empty<FolderWatcherDto>();

    // ─── Torrents ─────────────────────────────────────────────────────────

    public async Task<TorrentRecordDto[]> GetTorrentsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<TorrentRecordDto[]>(ApiRoutes.Torrents, JsonOpts, ct)
            ?? Array.Empty<TorrentRecordDto>();

    public Task<TorrentRecordDto?> GetTorrentAsync(Guid id, CancellationToken ct = default)
        => GetOrNullAsync<TorrentRecordDto>(ApiRoutes.Torrent(id), ct);

    public async Task<TorrentRecordDto> AddMagnetAsync(AddMagnetRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.TorrentAddMagnet, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TorrentRecordDto>(JsonOpts, ct)
            ?? new TorrentRecordDto();
    }

    public async Task<TorrentRecordDto> AddTorrentFileAsync(AddTorrentFileRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.TorrentAddFile, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TorrentRecordDto>(JsonOpts, ct)
            ?? new TorrentRecordDto();
    }

    public async Task<TorrentRecordDto> UpdateTorrentAsync(Guid id, UpdateTorrentRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(ApiRoutes.Torrent(id), request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TorrentRecordDto>(JsonOpts, ct))!;
    }

    public async Task DeleteTorrentAsync(Guid id, bool deleteFiles, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"{ApiRoutes.Torrent(id)}?deleteFiles={deleteFiles}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task PauseTorrentAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(ApiRoutes.TorrentPauseFor(id), null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ResumeTorrentAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(ApiRoutes.TorrentResumeFor(id), null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<TorrentFileNodeDto> GetTorrentFileTreeAsync(Guid id, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<TorrentFileNodeDto>(ApiRoutes.TorrentFileTreeFor(id), JsonOpts, ct)
            ?? new TorrentFileNodeDto();

    public async Task UpdateTorrentFileSelectionsAsync(Guid id, UpdateFileSelectionsRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(ApiRoutes.TorrentFileSelectionsFor(id), request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<TorrentConfigDto> GetTorrentConfigAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<TorrentConfigDto>(ApiRoutes.TorrentConfig, JsonOpts, ct)
            ?? new TorrentConfigDto();

    public async Task<TorrentConfigDto> UpdateTorrentConfigAsync(TorrentConfigDto config, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(ApiRoutes.TorrentConfig, config, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TorrentConfigDto>(JsonOpts, ct))!;
    }

    // ─── Watch state ──────────────────────────────────────────────────────

    public async Task<WatchStateDto[]> GetWatchStatesAsync(Guid mediaItemId, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<WatchStateDto[]>(ApiRoutes.WatchStatesFor(mediaItemId), JsonOpts, ct)
            ?? Array.Empty<WatchStateDto>();

    public async Task<WatchStateDto> RecordProgressAsync(RecordProgressRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.WatchStateProgress, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WatchStateDto>(JsonOpts, ct)
            ?? new WatchStateDto();
    }

    public async Task<WatchStateDto> ToggleWatchedAsync(ToggleWatchedRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.WatchStateToggle, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<WatchStateDto>(JsonOpts, ct)
            ?? new WatchStateDto();
    }

    public async Task ResetProgressAsync(ResetProgressRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.WatchStateReset, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ─── Rename + identification queues ──────────────────────────────────

    public async Task<RenameQueueEntryDto[]> GetRenameQueueAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<RenameQueueEntryDto[]>(ApiRoutes.RenameQueue, JsonOpts, ct)
            ?? Array.Empty<RenameQueueEntryDto>();

    public async Task<RenameHistoryEntryDto[]> GetRenameHistoryAsync(int? take, CancellationToken ct = default)
    {
        var url = take is null ? ApiRoutes.RenameHistory : $"{ApiRoutes.RenameHistory}?take={take}";
        var page = await _http.GetFromJsonAsync<PagedResult<RenameHistoryEntryDto>>(url, JsonOpts, ct);
        return page?.Items ?? Array.Empty<RenameHistoryEntryDto>();
    }

    public async Task<PagedResult<RenameHistoryEntryDto>> GetRenameHistoryPageAsync(
        int skip, int take, Guid? folderId, RenameStatus? status, CancellationToken ct = default)
    {
        var args = new List<string> { $"skip={skip}", $"take={take}" };
        if (folderId is not null) args.Add($"folderId={folderId}");
        if (status is not null)   args.Add($"status={status}");
        var url = $"{ApiRoutes.RenameHistory}?{string.Join('&', args)}";
        return await _http.GetFromJsonAsync<PagedResult<RenameHistoryEntryDto>>(url, JsonOpts, ct)
            ?? new PagedResult<RenameHistoryEntryDto>(Array.Empty<RenameHistoryEntryDto>(), 0);
    }

    public async Task RevertRenameAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(ApiRoutes.RenameHistoryRevertFor(id), null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IdentificationQueueEntryDto[]> GetIdentificationQueueAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IdentificationQueueEntryDto[]>(ApiRoutes.IdentificationQueue, JsonOpts, ct)
            ?? Array.Empty<IdentificationQueueEntryDto>();

    public async Task EnqueueIdentificationAsync(Guid folderId, bool forceRefresh, CancellationToken ct = default)
    {
        var url = $"{ApiRoutes.IdentificationEnqueue}?folderId={folderId}&forceRefresh={forceRefresh}";
        using var resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CancelIdentificationAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(ApiRoutes.IdentificationCancelFor(id), null, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ─── Patterns / ignore rules / tags ──────────────────────────────────

    public async Task<RenamePatternDto[]> GetPatternsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<RenamePatternDto[]>(ApiRoutes.Patterns, JsonOpts, ct)
            ?? Array.Empty<RenamePatternDto>();

    public async Task<RenamePatternDto> UpsertPatternAsync(Guid? id, UpsertPatternRequest request, CancellationToken ct = default)
    {
        var url = id is null ? ApiRoutes.Patterns : ApiRoutes.Pattern(id.Value);
        using var resp = id is null
            ? await _http.PostAsJsonAsync(url, request, JsonOpts, ct)
            : await _http.PutAsJsonAsync(url, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<RenamePatternDto>(JsonOpts, ct))!;
    }

    public async Task DeletePatternAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(ApiRoutes.Pattern(id), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IgnoreRuleDto[]> GetIgnoreRulesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<IgnoreRuleDto[]>(ApiRoutes.IgnoreRules, JsonOpts, ct)
            ?? Array.Empty<IgnoreRuleDto>();

    public async Task<IgnoreRuleDto> UpsertIgnoreRuleAsync(Guid? id, UpsertIgnoreRuleRequest request, CancellationToken ct = default)
    {
        var url = id is null ? ApiRoutes.IgnoreRules : ApiRoutes.IgnoreRule(id.Value);
        using var resp = id is null
            ? await _http.PostAsJsonAsync(url, request, JsonOpts, ct)
            : await _http.PutAsJsonAsync(url, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<IgnoreRuleDto>(JsonOpts, ct))!;
    }

    public async Task DeleteIgnoreRuleAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(ApiRoutes.IgnoreRule(id), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<MediaTagDto[]> GetMediaTagsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<MediaTagDto[]>(ApiRoutes.MediaTags, JsonOpts, ct)
            ?? Array.Empty<MediaTagDto>();

    public async Task<MediaTagDto> UpsertMediaTagAsync(Guid? id, UpsertMediaTagRequest request, CancellationToken ct = default)
    {
        var url = id is null ? ApiRoutes.MediaTags : ApiRoutes.MediaTag(id.Value);
        using var resp = id is null
            ? await _http.PostAsJsonAsync(url, request, JsonOpts, ct)
            : await _http.PutAsJsonAsync(url, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<MediaTagDto>(JsonOpts, ct))!;
    }

    public async Task DeleteMediaTagAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(ApiRoutes.MediaTag(id), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AssignTagAsync(Guid tagId, Guid mediaItemId, CancellationToken ct = default)
    {
        var url = ApiRoutes.MediaTagAssign
            .Replace("{tagId}", tagId.ToString())
            .Replace("{mediaItemId}", mediaItemId.ToString());
        using var resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task UnassignTagAsync(Guid tagId, Guid mediaItemId, CancellationToken ct = default)
    {
        var url = ApiRoutes.MediaTagAssign
            .Replace("{tagId}", tagId.ToString())
            .Replace("{mediaItemId}", mediaItemId.ToString());
        using var resp = await _http.DeleteAsync(url, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ─── App config + hardware ───────────────────────────────────────────

    public async Task<AppConfigEntryDto[]> GetAppConfigAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<AppConfigEntryDto[]>(ApiRoutes.AppConfig, JsonOpts, ct)
            ?? Array.Empty<AppConfigEntryDto>();

    public Task<AppConfigEntryDto?> GetAppConfigValueAsync(string key, CancellationToken ct = default)
        => GetOrNullAsync<AppConfigEntryDto>(ApiRoutes.AppConfigKey(key), ct);

    public async Task SetAppConfigValueAsync(string key, string? value, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(
            ApiRoutes.AppConfigKey(key),
            new AppConfigEntryDto(key, value),
            JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<HardwareReportDto> GetHardwareReportAsync(bool rescan, CancellationToken ct = default)
    {
        var url = rescan ? $"{ApiRoutes.HardwareInfo}?rescan=true" : ApiRoutes.HardwareInfo;
        return await _http.GetFromJsonAsync<HardwareReportDto>(url, JsonOpts, ct)
            ?? throw new InvalidOperationException("Hardware probe returned no data.");
    }

    // ─── Search ──────────────────────────────────────────────────────────

    public async Task<SearchResponse> SearchTmdbAsync(SearchRequest request, CancellationToken ct = default)
        => await PostForJsonAsync<SearchRequest, SearchResponse>(ApiRoutes.SearchTmdb, request, ct);

    public async Task<SearchResponse> SearchMalAsync(SearchRequest request, CancellationToken ct = default)
        => await PostForJsonAsync<SearchRequest, SearchResponse>(ApiRoutes.SearchMal, request, ct);

    public async Task<SearchResponse> SearchImdbAsync(SearchRequest request, CancellationToken ct = default)
        => await PostForJsonAsync<SearchRequest, SearchResponse>(ApiRoutes.SearchImdb, request, ct);

    // ─── DLNA ────────────────────────────────────────────────────────────

    public async Task<DlnaRendererDto[]> GetDlnaRenderersAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<DlnaRendererDto[]>(ApiRoutes.DlnaRenderers, JsonOpts, ct)
            ?? Array.Empty<DlnaRendererDto>();

    public async Task DlnaPlayAsync(DlnaPlayRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(ApiRoutes.DlnaPlay, request, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static string BuildMediaListUrl(MediaListQuery q)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(q.Tag))      args.Add($"tag={Uri.EscapeDataString(q.Tag)}");
        if (!string.IsNullOrEmpty(q.Search))   args.Add($"search={Uri.EscapeDataString(q.Search)}");
        if (q.Type is not null)                args.Add($"type={q.Type}");
        if (q.FolderId is not null)            args.Add($"folderId={q.FolderId}");
        if (q.Skip is not null)                args.Add($"skip={q.Skip}");
        if (q.Take is not null)                args.Add($"take={q.Take}");
        if (!string.IsNullOrEmpty(q.Sort))     args.Add($"sort={Uri.EscapeDataString(q.Sort)}");
        return args.Count == 0
            ? ApiRoutes.Media
            : $"{ApiRoutes.Media}?{string.Join('&', args)}";
    }

    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }

    private async Task<TResp> PostForJsonAsync<TReq, TResp>(string url, TReq body, CancellationToken ct)
        where TResp : class
    {
        using var resp = await _http.PostAsJsonAsync(url, body, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TResp>(JsonOpts, ct))!;
    }
}
