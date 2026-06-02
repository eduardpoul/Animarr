using System.Net.Http.Json;
using System.Text.Json;

namespace Animarr.App;

/// <summary>
/// Native detail page (POC): backdrop hero + a native episode grid. Episodes
/// come from /api/media/{id}/files; each thumbnail from the per-episode
/// endpoint built earlier (/api/media/{id}/episode-thumb). Proves the episode
/// grid — the other scroll-heavy screen — is smooth + D-pad-navigable natively.
/// </summary>
public partial class NativeDetailPage : ContentPage
{
    private const string ServerBase = "http://192.168.11.200:8080";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _id;

    public NativeDetailPage(string id, string title, string? backdropUrl)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        _id = id;
        TitleLabel.Text = (title ?? "").ToUpperInvariant();
        if (!string.IsNullOrEmpty(backdropUrl)) BackdropImage.Source = backdropUrl;
        TvFocus.Attach(EpisodesView);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var files = await http.GetFromJsonAsync<FileDto[]>(
                            $"{ServerBase}/api/media/{_id}/files", Json)
                        ?? Array.Empty<FileDto>();

            var eps = files
                .Where(f => f.Episode is > 0)
                .OrderBy(f => f.Season ?? 1).ThenBy(f => f.Episode)
                .Select(f => new EpisodeVm
                {
                    Label    = $"S{f.Season ?? 1}  ·  E{f.Episode}",
                    ThumbUrl = $"{ServerBase}/api/media/{_id}/episode-thumb?season={f.Season ?? 1}&episode={f.Episode}",
                })
                .ToList();

            EpisodesView.ItemsSource = eps;
#if ANDROID
            try
            {
                var rm = Bumptech.Glide.Glide.With(Android.App.Application.Context);
                foreach (var ep in eps) { try { rm.Load(ep.ThumbUrl).Preload(); } catch { } }
            }
            catch { }
#endif
        }
        catch { /* POC: leave empty on failure */ }
    }

    private sealed record FileDto(int? Season, int? Episode);

    public sealed class EpisodeVm
    {
        public string Label    { get; init; } = "";
        public string ThumbUrl { get; init; } = "";
    }
}
