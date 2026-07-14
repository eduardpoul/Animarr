using System.Net.Http.Json;
using System.Text.Json;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Native detail page matching the Blazor MediaDetail: hero (backdrop + poster +
/// title + pills + meta + a Continue/Watch CTA + favorite star) → 3-column body
/// (Synopsis / Details / Identification) → season tabs → episode grid with
/// per-episode watched ticks and resume bars. Data via the shared authenticated
/// HttpClient + IAnimarrApiClient; images / episode thumbs / playback URLs are
/// built against the live server origin.
/// </summary>
public partial class NativeDetailPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private readonly IAnimarrApiClient _api;
    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');

    private readonly string _id;
    private readonly Guid   _guid;
    private DetailDto? _item;
    private FileDto[]  _files = Array.Empty<FileDto>();
    private ContinueWatchDto? _continue;
    private WatchStateDto[] _watch = Array.Empty<WatchStateDto>();
    private bool _isFav;
    private int  _activeSeason = 1;
    private List<SeasonTabVm> _tabs = new();

    public static readonly BindableProperty PillsProperty =
        BindableProperty.Create(nameof(Pills), typeof(System.Collections.IList), typeof(NativeDetailPage));
    public System.Collections.IList? Pills { get => (System.Collections.IList?)GetValue(PillsProperty); set => SetValue(PillsProperty, value); }

    public static readonly BindableProperty DetailRowsProperty =
        BindableProperty.Create(nameof(DetailRows), typeof(System.Collections.IList), typeof(NativeDetailPage));
    public System.Collections.IList? DetailRows { get => (System.Collections.IList?)GetValue(DetailRowsProperty); set => SetValue(DetailRowsProperty, value); }

    public static readonly BindableProperty IdentRowsProperty =
        BindableProperty.Create(nameof(IdentRows), typeof(System.Collections.IList), typeof(NativeDetailPage));
    public System.Collections.IList? IdentRows { get => (System.Collections.IList?)GetValue(IdentRowsProperty); set => SetValue(IdentRowsProperty, value); }

    public static readonly BindableProperty SeasonTabsProperty =
        BindableProperty.Create(nameof(SeasonTabs), typeof(System.Collections.IList), typeof(NativeDetailPage));
    public System.Collections.IList? SeasonTabs { get => (System.Collections.IList?)GetValue(SeasonTabsProperty); set => SetValue(SeasonTabsProperty, value); }

    public static readonly BindableProperty EpisodesProperty =
        BindableProperty.Create(nameof(Episodes), typeof(System.Collections.IList), typeof(NativeDetailPage));
    public System.Collections.IList? Episodes { get => (System.Collections.IList?)GetValue(EpisodesProperty); set => SetValue(EpisodesProperty, value); }

    public Command<int> SeasonCommand   { get; }
    public Command      BackCommand     { get; }
    public Command      FavoriteCommand { get; }
    public Command      PlayHeroCommand { get; }

    public NativeDetailPage(string id, string? title, string? backdropUrl)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();
        _api  = services.GetRequiredService<IAnimarrApiClient>();

        _id = id;
        Guid.TryParse(id, out _guid);

        SeasonCommand   = new Command<int>(SwitchSeason);
        BackCommand     = new Command(() => Navigation.PopAsync());
        FavoriteCommand = new Command(async () => await ToggleFavoriteAsync());
        PlayHeroCommand = new Command(PlayCta);

        TitleLabel.Text = (title ?? "").ToUpperInvariant();
        if (!string.IsNullOrEmpty(backdropUrl)) BackdropImage.Source = backdropUrl;
        EpisodesView.SelectionChanged += OnEpisodeSelected;
        TvFocus.Attach(EpisodesView);
        _ = LoadAsync();
    }

    private void OnEpisodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is EpisodeVm ep && ep.Have)
            PlayFile(ep.Season, ep.EpisodeNum, ep.FilePath, ep.ResumeMs);
        EpisodesView.SelectedItem = null;
    }

    // Hero CTA: resume / next / first / rewatch, computed server-side.
    private void PlayCta()
    {
        if (_continue?.FilePath is { Length: > 0 } cf)
            PlayFile(_continue.Season, _continue.Episode, cf, _continue.ProgressMs ?? 0);
        else
        {
            var f = _files.FirstOrDefault(x => !string.IsNullOrEmpty(x.FilePath));
            PlayFile(f?.Season, f?.Episode, f?.FilePath, 0);
        }
    }

    // Open the in-app ExoPlayer for one file, carrying resume + episode coords
    // so the player reports progress back to /api/watch-states.
    private async void PlayFile(int? season, int? episode, string? filePath, long resumeMs)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        var title = _item?.Title ?? TitleLabel.Text;
        await Navigation.PushAsync(new PlayerPage(_guid, season, episode, filePath, title, resumeMs));
    }

    private async Task LoadAsync()
    {
        try
        {
            var itemTask  = _http.GetFromJsonAsync<DetailDto>($"/api/media/{_id}", Json);
            var filesTask = _http.GetFromJsonAsync<FileDto[]>($"/api/media/{_id}/files", Json);
            _item  = await itemTask;
            _files = await filesTask ?? Array.Empty<FileDto>();
            if (_item is null) return;

            ApplyHero();
            ApplyBody();
            BuildSeasonTabs();

            // Personalised bits — best-effort, don't block the page on them.
            if (_guid != Guid.Empty)
            {
                _continue = await SafeAsync(_api.GetContinueAsync(_guid));
                _watch    = await SafeAsync(_api.GetWatchStatesAsync(_guid)) ?? Array.Empty<WatchStateDto>();
                var favs  = await SafeAsync(_api.GetFavoriteIdsAsync());
                _isFav    = favs?.Contains(_guid) ?? false;

                ApplyCta();
                ApplyFavorite();
                BuildEpisodes();   // re-render with watched ticks / resume bars
            }
        }
        catch { /* leave partial UI on failure */ }
    }

    private static async Task<T?> SafeAsync<T>(Task<T> task)
    {
        try { return await task; } catch { return default; }
    }

    private void ApplyCta()
    {
        PlayLabel.Text = _continue?.Kind switch
        {
            "continue" => $"▶   Продолжить {EpLabel(_continue)}",
            "next"     => $"▶   Следующая {EpLabel(_continue)}",
            "rewatch"  => "↺   Пересмотреть",
            _          => "▶   Смотреть",
        };
    }

    private static string EpLabel(ContinueWatchDto? c)
    {
        if (c?.Episode is null) return "";
        return c.Season is > 0 ? $"S{c.Season}·E{c.Episode}" : $"E{c.Episode}";
    }

    private async Task ToggleFavoriteAsync()
    {
        if (_guid == Guid.Empty) return;
        try
        {
            if (_isFav) await _api.RemoveFavoriteAsync(_guid);
            else        await _api.AddFavoriteAsync(_guid);
            _isFav = !_isFav;
            ApplyFavorite();
        }
        catch { }
    }

    private void ApplyFavorite()
    {
        FavLabel.Text = _isFav ? "★" : "☆";
        FavLabel.TextColor = _isFav ? Color.FromArgb("#e8b34d") : Colors.White;
    }

    private void ApplyHero()
    {
        var it = _item!;
        TitleLabel.Text = (it.Title ?? "").ToUpperInvariant();

        var alt = (it.EnglishTitle is not null && it.EnglishTitle != it.Title) ? it.EnglishTitle : null;
        AltLabel.Text = alt ?? "";
        AltLabel.IsVisible = !string.IsNullOrEmpty(alt);

        CjkLabel.Text = it.CjkTitle ?? "";

        if (!string.IsNullOrEmpty(it.FanartPath))
            BackdropImage.Source = $"{ImageBase}/api/image?path={Uri.EscapeDataString(it.FanartPath)}&w=1280";
        if (!string.IsNullOrEmpty(it.PosterPath))
            PosterImage.Source = $"{ImageBase}/api/image?path={Uri.EscapeDataString(it.PosterPath)}&w=330";

        var meta = new List<string>();
        if (it.Rating is > 0)              meta.Add($"★ {it.Rating:F1}");
        if (it.Year is not null)           meta.Add(it.Year.ToString()!);
        if (!string.IsNullOrEmpty(it.Studio))   meta.Add(it.Studio);
        if (it.Runtime is > 0)             meta.Add($"{it.Runtime}m");
        if (it.EpisodeCount is > 0)        meta.Add($"{it.EpisodeCount} ep");
        if (!string.IsNullOrEmpty(it.Language)) meta.Add(it.Language);
        MetaLabel.Text = string.Join("   ·   ", meta);

        var pills = new List<string> { TypeLabel(it.MediaType) };
        if (it.Tags is { Length: > 0 }) pills.AddRange(it.Tags.Take(3));
        Pills = pills;
    }

    private void ApplyBody()
    {
        var it = _item!;
        SynopsisLabel.Text = string.IsNullOrEmpty(it.Description) ? "Нет описания." : it.Description;

        var kv = new List<KvVm>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) kv.Add(new KvVm(k, v!)); }
        Add("Студия", it.Studio);
        Add("Язык", it.Language);
        if (it.Runtime is > 0)      Add("Хроно", $"{it.Runtime} м");
        if (it.EpisodeCount is > 0) Add("Эпизодов", it.EpisodeCount.ToString());
        Add("Сезон", it.SeasonLabel);
        Add("Статус", it.Status);
        Add("Рейтинг", it.ContentRating);
        if (it.Genres is { Length: > 0 }) Add("Жанры", string.Join(" · ", it.Genres));
        DetailRows = kv;

        var ids = new List<IdVm>();
        if (it.TmdbId is not null)          ids.Add(new IdVm("TMDB", it.TmdbId.ToString()!));
        if (it.MalId is not null)           ids.Add(new IdVm("MAL", it.MalId.ToString()!));
        if (!string.IsNullOrEmpty(it.ImdbId)) ids.Add(new IdVm("IMDb", it.ImdbId!));
        IdentRows = ids;
        IdentTitle.IsVisible = ids.Count > 0;
    }

    private void BuildSeasonTabs()
    {
        var it = _item!;
        var hasMeta = (it.TmdbId is not null || it.MalId is not null) && it.Seasons is { Length: > 0 };
        var fileSeasons = _files.Where(f => f.Season is not null || f.Episode is not null)
                                .Select(f => f.Season ?? 1).Distinct().OrderBy(n => n).ToArray();

        var tabs = new List<SeasonTabVm>();
        if (hasMeta)
        {
            foreach (var s in it.Seasons!.OrderBy(s => s.Number))
                tabs.Add(new SeasonTabVm(s.Number, $"Сезон {s.Number}", s.EpisodeCount));
            foreach (var n in fileSeasons.Where(fs => it.Seasons!.All(s => s.Number != fs)))
                tabs.Add(new SeasonTabVm(n, n == 0 ? "Спецвыпуски" : $"Сезон {n}", _files.Count(f => (f.Season ?? 1) == n)));
        }
        else
        {
            foreach (var n in fileSeasons)
                tabs.Add(new SeasonTabVm(n, n == 0 ? "Спецвыпуски" : $"Сезон {n}", _files.Count(f => (f.Season ?? 1) == n)));
        }

        _tabs = tabs;
        SeasonsSection.IsVisible = tabs.Count > 0;
        _activeSeason = tabs.Count > 0 ? tabs[0].Number : 1;
        RenderTabs();
        BuildEpisodes();
    }

    private void RenderTabs()
        => SeasonTabs = _tabs.Select(t => new SeasonTabVm(t.Number, t.Label, t.RawCount, t.Number == _activeSeason)).ToList();

    private void SwitchSeason(int n)
    {
        _activeSeason = n;
        RenderTabs();
        BuildEpisodes();
    }

    private void BuildEpisodes()
    {
        var it = _item!;

        if (it.MediaType == "Movie" || (_files.Length == 1 && _files[0].Episode is null))
        {
            var mf = _files.FirstOrDefault();
            var ws = _watch.FirstOrDefault();
            Episodes = new List<EpisodeVm>
            {
                new()
                {
                    Number   = "▶",
                    Title    = "Смотреть фильм",
                    Meta     = it.Runtime is > 0 ? $"{it.Runtime}m" : "Фильм",
                    ThumbUrl = !string.IsNullOrEmpty(it.FanartPath)
                        ? $"{ImageBase}/api/image?path={Uri.EscapeDataString(it.FanartPath)}&w=640" : "",
                    Have     = mf is not null,
                    FilePath = mf?.FilePath,
                    Season   = mf?.Season,
                    EpisodeNum = mf?.Episode,
                    ResumeMs = ws?.ProgressMs ?? 0,
                    IsWatched   = ws?.IsWatched ?? false,
                    WatchFraction = Frac(ws),
                },
            };
            return;
        }

        var active    = it.Seasons?.FirstOrDefault(s => s.Number == _activeSeason);
        var maxFileEp = _files.Where(f => (f.Season ?? 1) == _activeSeason)
                              .Select(f => f.Episode ?? 0).DefaultIfEmpty(0).Max();
        var count = Math.Max(active?.EpisodeCount ?? 0, maxFileEp);
        if (count == 0) count = 1;

        var eps = new List<EpisodeVm>();
        for (int i = 1; i <= count; i++)
        {
            var f    = _files.FirstOrDefault(x => (x.Season ?? 1) == _activeSeason && x.Episode == i);
            var have = f is not null;
            var ws   = _watch.FirstOrDefault(w => (w.Season ?? 1) == _activeSeason && w.Episode == i);
            var title = _activeSeason == 0
                ? $"Спецвыпуск {i}"
                : (f?.AbsoluteEpisode is int ab ? $"Эпизод {i}  ·  TMDB #{ab}" : $"Эпизод {i}");
            eps.Add(new EpisodeVm
            {
                Number   = i.ToString("00"),
                Title    = title,
                Meta     = have ? (it.Runtime is > 0 ? $"{it.Runtime}m" : "На диске") : "Нет файла",
                ThumbUrl = have ? $"{ImageBase}/api/media/{_id}/episode-thumb?season={_activeSeason}&episode={i}" : "",
                Have     = have,
                FilePath = f?.FilePath,
                Season   = _activeSeason,
                EpisodeNum = i,
                ResumeMs = ws?.ProgressMs ?? 0,
                IsWatched   = ws?.IsWatched ?? false,
                WatchFraction = Frac(ws),
            });
        }
        Episodes = eps;
    }

    private static double Frac(WatchStateDto? ws)
    {
        if (ws is null || ws.ProgressMs is not > 0 || ws.RuntimeMs is not > 0) return 0;
        return Math.Clamp((double)ws.ProgressMs.Value / ws.RuntimeMs.Value, 0, 1);
    }

    private static string TypeLabel(string? t) => t switch
    {
        "Anime"       => "ANIME",
        "Movie"       => "MOVIE",
        "Series"      => "SERIES",
        "Multserials" => "MULTI",
        _             => "MEDIA",
    };

    private sealed record DetailDto(
        string? Title, string? EnglishTitle, string? CjkTitle, int? Year, double? Rating,
        string? Studio, string? Language, int? Runtime, int? EpisodeCount, string? SeasonLabel,
        string? Status, string? ContentRating, string? Description, string[]? Genres, string[]? Tags,
        int? TmdbId, int? MalId, string? ImdbId, string? MediaType,
        string? PosterPath, string? FanartPath, SeasonDto[]? Seasons);

    private sealed record SeasonDto(int Number, int EpisodeCount, string? PosterPath, string? Overview);
    private sealed record FileDto(int? Season, int? Episode, int? AbsoluteEpisode, string? FilePath);

    public sealed record KvVm(string Key, string Value);
    public sealed record IdVm(string Src, string Id);

    public sealed class SeasonTabVm
    {
        public int    Number   { get; }
        public string Label    { get; }
        public string Count    { get; }
        public int    RawCount { get; }
        public Color  Bg       { get; }
        public Color  Fg       { get; }
        public Color  CountFg  { get; }

        public SeasonTabVm(int number, string label, int count, bool active = false)
        {
            Number   = number;
            Label    = label;
            Count    = count.ToString();
            RawCount = count;
            Bg      = active ? Color.FromArgb("#e8772e")  : Color.FromArgb("#1a1d24");
            Fg      = active ? Colors.White               : Color.FromArgb("#c7ccd4");
            CountFg = active ? Color.FromArgb("#ffe0c8")  : Color.FromArgb("#6b7280");
        }
    }

    public sealed class EpisodeVm
    {
        public string  Number     { get; init; } = "";
        public string  Title      { get; init; } = "";
        public string  Meta       { get; init; } = "";
        public string  ThumbUrl   { get; init; } = "";
        public bool    Have       { get; init; }
        public string? FilePath   { get; init; }
        public int?    Season     { get; init; }
        public int?    EpisodeNum { get; init; }
        public long    ResumeMs   { get; init; }
        public bool    IsWatched     { get; init; }
        public double  WatchFraction { get; init; }   // 0..1 resume position

        public Color  Strip    => Have ? Color.FromArgb("#56c596") : Color.FromArgb("#e8a33d");
        public string ChipText => IsWatched ? "✓" : Have ? "•" : "!";
        public Color  ChipFg   => IsWatched ? Color.FromArgb("#56c596")
                                : Have ? Color.FromArgb("#c7ccd4") : Color.FromArgb("#e8a33d");
        public Color  TitleFg  => Have ? Color.FromArgb("#e8eaed") : Color.FromArgb("#a8a097");
        public Color  MetaFg   => Have ? Color.FromArgb("#5d564f") : Color.FromArgb("#e8a33d");
        public double ArtOpacity => Have ? (IsWatched ? 0.82 : 1.0) : 0.72;

        // Resume bar: full green when watched, partial orange mid-episode.
        public bool   HasBar     => IsWatched || WatchFraction > 0.01;
        public double BarValue   => IsWatched ? 1.0 : WatchFraction;
        public Color  BarColor   => IsWatched ? Color.FromArgb("#56c596") : Color.FromArgb("#e8772e");
    }
}
