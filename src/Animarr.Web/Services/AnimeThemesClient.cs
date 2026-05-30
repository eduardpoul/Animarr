using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Animarr.Web.Services;

/// <summary>
/// Client for the AnimeThemes.moe API (https://api.animethemes.moe) — a free,
/// no-auth repository of anime opening/ending themes with direct audio/video
/// file links. We match an anime by an external id (MyAnimeList or AniList)
/// and pull the best OP/ED audio track (.ogg).
///
/// Coverage note: AnimeThemes is Japanese-anime focused. Chinese donghua are
/// mostly absent — a miss there is expected, not an error.
/// </summary>
public class AnimeThemesClient(IHttpClientFactory httpFactory, ILogger<AnimeThemesClient> logger)
{
    private const string ClientName = "animethemes";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One resolved theme: the audio file URL plus a display label.</summary>
    public sealed record ThemePick(string AudioUrl, string? Title);

    /// <summary>
    /// Finds the best theme for an anime identified by an external id. Prefers
    /// the first opening (OP1), falls back to the first ending, then to whatever
    /// exists. Returns null when the anime isn't mapped or has no audio track.
    /// </summary>
    /// <param name="site">"MyAnimeList" or "AniList" (AnimeThemes external-resource site name).</param>
    /// <param name="externalId">the MAL/AniList numeric id as a string.</param>
    public async Task<ThemePick?> GetBestThemeAsync(string site, string externalId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return null;
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            // filter[has]=resources + site + external_id maps the external id to an
            // AnimeThemes anime; the include pulls the nested audio links in one call.
            var url = $"/anime?filter[has]=resources&filter[site]={Uri.EscapeDataString(site)}"
                    + $"&filter[external_id]={Uri.EscapeDataString(externalId)}"
                    + "&include=animethemes.animethemeentries.videos.audio,animethemes.song";

            var resp = await http.GetFromJsonAsync<AnimeThemesResponse>(url, _json, ct);
            var theme = resp?.Anime
                .SelectMany(a => a.AnimeThemes)
                .Where(t => t.FirstAudioUrl is not null)
                .OrderBy(t => t.Rank)
                .FirstOrDefault();

            var audio = theme?.FirstAudioUrl;
            return string.IsNullOrEmpty(audio) ? null : new ThemePick(audio, theme!.DisplayTitle);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AnimeThemes] lookup failed for {Site} id={Id}", site, externalId);
            return null;
        }
    }

    /// <summary>Download a theme audio file to a local path. Returns false on failure
    /// (network error, or a read-only destination on a `:ro` media mount).</summary>
    public async Task<bool> DownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        try
        {
            var http = httpFactory.CreateClient(ClientName);
            var bytes = await http.GetByteArrayAsync(url, ct);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(destPath, bytes, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AnimeThemes] download failed: {Url} → {Path}", url, destPath);
            return false;
        }
    }
}

// ── DTOs (subset of the AnimeThemes JSON response) ───────────────────────────

public sealed class AnimeThemesResponse
{
    [JsonPropertyName("anime")] public List<AtAnime> Anime { get; set; } = [];
}

public sealed class AtAnime
{
    [JsonPropertyName("animethemes")] public List<AtTheme> AnimeThemes { get; set; } = [];
}

public sealed class AtTheme
{
    /// <summary>"OP" or "ED".</summary>
    [JsonPropertyName("type")]     public string? Type     { get; set; }
    [JsonPropertyName("sequence")] public int?    Sequence { get; set; }
    [JsonPropertyName("song")]     public AtSong? Song     { get; set; }
    [JsonPropertyName("animethemeentries")] public List<AtEntry> Entries { get; set; } = [];

    /// <summary>Sort key: openings before endings, then by sequence — OP1 ranks first.</summary>
    public int Rank => (Type == "OP" ? 0 : Type == "ED" ? 1000 : 2000) + (Sequence ?? 0);

    /// <summary>First non-empty audio link across this theme's entries/videos.</summary>
    public string? FirstAudioUrl => Entries
        .SelectMany(e => e.Videos)
        .Select(v => v.Audio?.Link)
        .FirstOrDefault(l => !string.IsNullOrEmpty(l));

    public string? DisplayTitle => Song?.Title;
}

public sealed class AtSong
{
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class AtEntry
{
    [JsonPropertyName("videos")] public List<AtVideo> Videos { get; set; } = [];
}

public sealed class AtVideo
{
    [JsonPropertyName("link")]  public string?  Link  { get; set; }
    [JsonPropertyName("audio")] public AtAudio? Audio { get; set; }
}

public sealed class AtAudio
{
    [JsonPropertyName("link")] public string? Link { get; set; }
}
