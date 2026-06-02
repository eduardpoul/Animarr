using System.Net.Http.Json;
using System.Text.Json;

namespace Animarr.App;

/// <summary>
/// Native detail page matching the Blazor MediaDetail: hero (backdrop + poster +
/// title + pills + meta + Play) → 3-column body (Synopsis / Details /
/// Identification) → season tabs → rich episode grid (16:9 thumb + big number +
/// OK/MISS chip + title + meta). Episodes from /api/media/{id}/files, thumbs
/// from /api/media/{id}/episode-thumb. Self-contained (hard-coded LAN server).
/// </summary>
public partial class NativeDetailPage : ContentPage
{
    private const string ServerBase = "http://192.168.11.200:8080";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _id;
    private DetailDto? _item;
    private FileDto[]  _files = Array.Empty<FileDto>();
    private int        _activeSeason = 1;
    private List<SeasonTabVm> _tabs = new();

    // Dynamic lists bound from XAML.
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

    // Commands (not Tapped events) so TvFocusBehavior can run them from D-pad OK.
    public Command<int> SeasonCommand   { get; }
    public Command      BackCommand     { get; }
    public Command      EditCommand     { get; }
    public Command      PlayHeroCommand { get; }

    public NativeDetailPage(string id, string? title, string? backdropUrl)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        _id = id;
        SeasonCommand   = new Command<int>(SwitchSeason);
        BackCommand     = new Command(() => Navigation.PopAsync());
        // Edit metadata isn't rebuilt natively — open the full Blazor detail
        // (which carries the edit drawer) for this item in a pushed WebView host.
        EditCommand     = new Command(() => Navigation.PushAsync(new BlazorHostPage($"/catalog/{_id}")));
        PlayHeroCommand = new Command(() =>
            Play(_files.FirstOrDefault(f => !string.IsNullOrEmpty(f.FilePath))?.FilePath));
        TitleLabel.Text = (title ?? "").ToUpperInvariant();
        if (!string.IsNullOrEmpty(backdropUrl)) BackdropImage.Source = backdropUrl;
        EpisodesView.SelectionChanged += OnEpisodeSelected;
        TvFocus.Attach(EpisodesView);
        _ = LoadAsync();
    }

    // D-pad OK on an episode (CollectionView selection) → play it.
    private void OnEpisodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is EpisodeVm ep && ep.Have) Play(ep.FilePath);
        EpisodesView.SelectedItem = null;
    }

    // Raw passthrough (/api/file, designed for external native players) launched
    // via ACTION_VIEW — the device's video player streams it with range support.
    private void Play(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
#if ANDROID
        try
        {
            var url = $"{ServerBase}/api/file?path={Uri.EscapeDataString(filePath)}";
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
            intent.SetDataAndType(Android.Net.Uri.Parse(url), "video/*");
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context!.StartActivity(intent);
        }
        catch { }
#endif
    }

    private async Task LoadAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            var itemTask  = http.GetFromJsonAsync<DetailDto>($"{ServerBase}/api/media/{_id}", Json);
            var filesTask = http.GetFromJsonAsync<FileDto[]>($"{ServerBase}/api/media/{_id}/files", Json);
            _item  = await itemTask;
            _files = await filesTask ?? Array.Empty<FileDto>();
            if (_item is null) return;

            ApplyHero();
            ApplyBody();
            BuildSeasonTabs();
        }
        catch { /* POC: leave partial UI on failure */ }
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
            BackdropImage.Source = $"{ServerBase}/api/image?path={Uri.EscapeDataString(it.FanartPath)}&w=1280";
        if (!string.IsNullOrEmpty(it.PosterPath))
            PosterImage.Source = $"{ServerBase}/api/image?path={Uri.EscapeDataString(it.PosterPath)}&w=330";

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
        SynopsisLabel.Text = string.IsNullOrEmpty(it.Description) ? "No synopsis available." : it.Description;

        var kv = new List<KvVm>();
        void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) kv.Add(new KvVm(k, v!)); }
        Add("Studio", it.Studio);
        Add("Language", it.Language);
        if (it.Runtime is > 0)      Add("Runtime", $"{it.Runtime} m");
        if (it.EpisodeCount is > 0) Add("Episodes", it.EpisodeCount.ToString());
        Add("Season", it.SeasonLabel);
        Add("Status", it.Status);
        Add("Rating", it.ContentRating);
        if (it.Genres is { Length: > 0 }) Add("Tags", string.Join(" · ", it.Genres));
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
                tabs.Add(new SeasonTabVm(s.Number, $"Season {s.Number}", s.EpisodeCount));
            foreach (var n in fileSeasons.Where(fs => it.Seasons!.All(s => s.Number != fs)))
                tabs.Add(new SeasonTabVm(n, n == 0 ? "Specials" : $"Season {n}", _files.Count(f => (f.Season ?? 1) == n)));
        }
        else
        {
            foreach (var n in fileSeasons)
                tabs.Add(new SeasonTabVm(n, n == 0 ? "Specials" : $"Season {n}", _files.Count(f => (f.Season ?? 1) == n)));
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

        // Movie / single flat file → one "Play movie" card (no episode grid).
        if (it.MediaType == "Movie" || (_files.Length == 1 && _files[0].Episode is null))
        {
            var mf = _files.FirstOrDefault();
            Episodes = new List<EpisodeVm>
            {
                new()
                {
                    Number   = "▶",
                    Title    = "Play movie",
                    Meta     = it.Runtime is > 0 ? $"{it.Runtime}m" : "Movie",
                    ThumbUrl = !string.IsNullOrEmpty(it.FanartPath)
                        ? $"{ServerBase}/api/image?path={Uri.EscapeDataString(it.FanartPath)}&w=640" : "",
                    Have     = mf is not null,
                    FilePath = mf?.FilePath,
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
            var title = _activeSeason == 0
                ? $"Special {i}"
                : (f?.AbsoluteEpisode is int ab ? $"Episode {i}  ·  TMDB #{ab}" : $"Episode {i}");
            eps.Add(new EpisodeVm
            {
                Number   = i.ToString("00"),
                Title    = title,
                Meta     = have ? (it.Runtime is > 0 ? $"{it.Runtime}m" : "On disk") : "Not downloaded",
                ThumbUrl = have ? $"{ServerBase}/api/media/{_id}/episode-thumb?season={_activeSeason}&episode={i}" : "",
                Have     = have,
                FilePath = f?.FilePath,
            });
        }
        Episodes = eps;

#if ANDROID
        try
        {
            var rm = Bumptech.Glide.Glide.With(Android.App.Application.Context);
            foreach (var ep in eps) if (ep.Have) { try { rm.Load(ep.ThumbUrl).Preload(); } catch { } }
        }
        catch { }
#endif
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
        public string Number   { get; init; } = "";
        public string Title    { get; init; } = "";
        public string Meta     { get; init; } = "";
        public string ThumbUrl { get; init; } = "";
        public bool   Have     { get; init; }
        public string? FilePath { get; init; }

        public Color  Strip    => Have ? Color.FromArgb("#56c596") : Color.FromArgb("#e8a33d");
        public string ChipText => Have ? "✓" : "!";
        public Color  ChipFg   => Have ? Color.FromArgb("#56c596") : Color.FromArgb("#e8a33d");
        public Color  TitleFg  => Have ? Color.FromArgb("#e8eaed") : Color.FromArgb("#a8a097");
        public Color  MetaFg   => Have ? Color.FromArgb("#5d564f") : Color.FromArgb("#e8a33d");
        public double ArtOpacity => Have ? 1.0 : 0.72;
    }
}
