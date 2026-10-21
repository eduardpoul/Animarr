using System.Net.Http.Json;
using System.Text.Json;
using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App.Tv;

/// <summary>Data access + view models for the native TV screens — the same
/// endpoints the old XAML pages used, kept in one place.</summary>
internal sealed class TvData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpClient Http { get; }
    public IAnimarrApiClient Api { get; }
    private readonly ServerAddressProvider _addr;
    public string Base => _addr.Current!.ToString().TrimEnd('/');

    public TvData()
    {
        var s = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        Http = s.GetRequiredService<HttpClient>();
        Api = s.GetRequiredService<IAnimarrApiClient>();
        _addr = s.GetRequiredService<ServerAddressProvider>();
    }

    public async Task<T?> Get<T>(string url, CancellationToken ct = default)
    {
        try { return await Http.GetFromJsonAsync<T>(url, Json, ct); }
        catch { return default; }
    }

    public static async Task<T?> Safe<T>(Task<T> t)
    {
        try { return await t; } catch { return default; }
    }

    /// <summary>Catalog lists ask for the lighter card view (24 fields); older
    /// servers ignore the param and send the full DTO.</summary>
    public const string CardView = "&view=card";

    // artwork path → LastMetadataRefreshedAt ticks, from every catalog / detail
    // response. Img() puts it on the URL as t= wherever that path is shown (the
    // server then caches it immutably, Glide gets a new key when art changes).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> s_ver = new();

    public static void Remember(string? poster, string? fanart, DateTime? refreshedAt)
    {
        if (refreshedAt is not { } at) return;
        if (!string.IsNullOrEmpty(poster)) s_ver[poster] = at.Ticks;
        if (!string.IsNullOrEmpty(fanart)) s_ver[fanart] = at.Ticks;
    }

    public static MediaItem[] Remember(MediaItem[]? items)
    {
        items ??= Array.Empty<MediaItem>();
        foreach (var i in items) Remember(i.PosterPath, i.FanartPath, i.LastMetadataRefreshedAt);
        return items;
    }

    public string Img(string? path, int w)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var t = s_ver.TryGetValue(path, out var v) ? $"&t={v}" : "";
        return TvMedia.Url($"{Base}/api/image?path={Uri.EscapeDataString(path)}{t}&w={w}");
    }

    public string EpisodeThumb(string mediaId, int season, int episode)
        => TvMedia.Url($"{Base}/api/media/{mediaId}/episode-thumb?season={season}&episode={episode}");

    // ── DTO shapes (only the fields the TV uses) ─────────────────────────────
    public sealed record MediaItem(
        string? Id, string? Title, string? PosterPath, string? FanartPath,
        int? Year, string? MediaType, double? Rating, int? EpisodeCount, string? FolderId,
        DateTime? LastMetadataRefreshedAt);

    public sealed record ContinueItem(
        string MediaItemId, string? Title, string? PosterPath, string? FanartPath,
        int? Year, int? Season, int? Episode, double Progress, bool IsNew);

    public sealed record CalendarEvent(
        string MediaItemId, string? Title, string? PosterUrl, string? BackdropUrl,
        int Episode, DateTime AiringAtUtc, string? Status);

    public sealed record Prefs(string? Language, string? EpisodeListView, int? AccentHue);
    public sealed record ConfigValue(string? Value);

    public sealed record Detail(
        string? Title, string? EnglishTitle, string? CjkTitle, int? Year, double? Rating,
        string? Studio, string? Language, int? Runtime, int? EpisodeCount, string? SeasonLabel,
        string? Status, string? ContentRating, string? Description, string[]? Genres, string[]? Tags,
        int? TmdbId, int? MalId, string? ImdbId, string? MediaType,
        string? PosterPath, string? FanartPath, Season[]? Seasons, DateTime? LastMetadataRefreshedAt);

    public sealed record Season(int Number, int EpisodeCount, string? PosterPath, string? Overview);
    public sealed record FileRow(int? Season, int? Episode, int? AbsoluteEpisode, string? FilePath);

    public static string TypeLabel(string? t) => t switch
    {
        "Anime" => "Аниме", "Movie" => "Фильм", "Series" => "Сериал", "Multserials" => "Мультсериал", _ => "",
    };

    /// <summary>"2022 · 24 эп. · ★ 8.1" for a poster card.</summary>
    public static string PosterMeta(MediaItem i)
    {
        var p = new List<string>();
        if (i.Year is > 0) p.Add(i.Year.Value.ToString());
        if (i.MediaType != "Movie" && i.EpisodeCount is > 0) p.Add($"{i.EpisodeCount} эп.");
        if (i.Rating is > 0) p.Add($"★ {i.Rating:0.0}");
        return string.Join(" · ", p);
    }
}
