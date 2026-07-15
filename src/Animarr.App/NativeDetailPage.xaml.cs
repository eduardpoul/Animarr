using System.Net.Http.Json;
using System.Text.Json;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

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

    // ── Episode grid: chunked code-built cards + a lazy thumb queue ─────────
    // The full season renders (175+ episodes), but only EpChunk cards at a time:
    // OnPageScrolled appends the next chunk as the view nears the end, so the
    // initial layout stays fast on the weak TV GPU. Episode frames download
    // through the authenticated HttpClient with ThumbGate limiting concurrency
    // (each uncached frame costs the server an ffmpeg grab).
    private const int EpChunk = 24;
    private static readonly SemaphoreSlim ThumbGate = new(3);
    private List<EpisodeVm> _seasonEps = new();
    private int  _builtCount;
    private bool _appending;
    private CancellationTokenSource? _thumbCts;
    /// <summary>"grid" | "list" — seeded from the EpisodeListView preference
    /// (same one the web grid⇄list toggle writes).</summary>
    private string _episodeView = "grid";

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

        // D-pad OK on the hero buttons — set on the behavior directly (reliable).
        BackFocus.Command = BackCommand;
        FavFocus.Command  = FavoriteCommand;
        PlayFocus.Command = PlayHeroCommand;
        GridViewFocus.Command = new Command(() => SwitchEpisodeView("grid"));
        ListViewFocus.Command = new Command(() => SwitchEpisodeView("list"));

        TitleLabel.Text = (title ?? "").ToUpperInvariant();
        if (!string.IsNullOrEmpty(backdropUrl)) BackdropImage.Source = backdropUrl;
        _ = LoadAsync();
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Two core calls together.
            var itemTask  = _http.GetFromJsonAsync<DetailDto>($"/api/media/{_id}", Json);
            var filesTask = _http.GetFromJsonAsync<FileDto[]>($"/api/media/{_id}/files", Json);
            _item  = await itemTask;  LogT(sw, "item");
            _files = await filesTask ?? Array.Empty<FileDto>();  LogT(sw, "files");
            if (_item is null) return;

            ApplyHero();
            ApplyBody();
            BuildSeasonTabs();
            Loader.IsVisible = false;
            LogT(sw, "core-ui");

            // Personalised bits — run in parallel, don't serialize them.
            if (_guid != Guid.Empty)
            {
                var contT  = SafeAsync(_api.GetContinueAsync(_guid));
                var watchT = SafeAsync(_api.GetWatchStatesAsync(_guid));
                var favT   = SafeAsync(_api.GetFavoriteIdsAsync());
                var prefT  = SafeAsync(_api.GetMyPreferencesAsync());
                _continue = await contT;  LogT(sw, "continue");
                _watch    = await watchT ?? Array.Empty<WatchStateDto>();  LogT(sw, "watch");
                var favs  = await favT;   _isFav = favs?.Contains(_guid) ?? false;  LogT(sw, "favs");
                _episodeView = (await prefT)?.EpisodeListView == "list" ? "list" : "grid";

                ApplyCta();
                ApplyFavorite();
            }
            ApplyViewToggleVisual();

            // Build the episode grid LAST, after a yield so the hero/body paint
            // first — rendering the cards is the slow part on the weak TV GPU.
            await Task.Yield();
            BuildEpisodes();
            LogT(sw, "episodes");
        }
        catch { /* leave partial UI on failure */ }
        finally { Loader.IsVisible = false; }
    }

    private static void LogT(System.Diagnostics.Stopwatch sw, string stage)
    {
#if ANDROID
        Android.Util.Log.Info("AnimarrDetail", $"{stage} @ {sw.ElapsedMilliseconds}ms");
#endif
    }

    private static async Task<T?> SafeAsync<T>(Task<T> task)
    {
        try { return await task; } catch { return default; }
    }

    private void ApplyCta()
    {
        PlayLabel.Text = _continue?.Kind switch
        {
            "continue" => $"▶   {TvL.T("continue.resume", "Продолжить", "Resume")} {EpLabel(_continue)}",
            "next"     => $"▶   {TvL.T("detail.next", "Следующая", "Next")} {EpLabel(_continue)}",
            "rewatch"  => "↺   " + TvL.T("detail.rewatch", "Пересмотреть", "Rewatch"),
            _          => "▶   " + TvL.T("home.watch", "Смотреть", "Watch"),
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
        Add(TvL.T("media_detail.detail_studio", "Студия", "Studio"), it.Studio);
        Add(TvL.T("media_detail.detail_language", "Язык", "Language"), it.Language);
        if (it.Runtime is > 0)
            Add(TvL.T("media_detail.detail_runtime", "Хроно", "Runtime"), $"{it.Runtime} m");
        if (it.EpisodeCount is > 0)
            Add(TvL.T("media_detail.detail_episodes", "Эпизодов", "Episodes"), it.EpisodeCount.ToString());
        Add(TvL.T("media_detail.detail_season", "Сезон", "Season"), it.SeasonLabel);
        Add(TvL.T("media_detail.detail_status", "Статус", "Status"), it.Status);
        Add(TvL.T("media_detail.detail_rating", "Рейтинг", "Rating"), it.ContentRating);
        if (it.Genres is { Length: > 0 })
            Add(TvL.T("media_detail.detail_genres", "Жанры", "Genres"), string.Join(" · ", it.Genres));
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
                tabs.Add(new SeasonTabVm(s.Number, TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", s.Number), s.EpisodeCount));
            foreach (var n in fileSeasons.Where(fs => it.Seasons!.All(s => s.Number != fs)))
                tabs.Add(new SeasonTabVm(n, n == 0 ? TvL.T("media_detail.specials", "Спецвыпуски", "Specials") : TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", n), _files.Count(f => (f.Season ?? 1) == n)));
        }
        else
        {
            foreach (var n in fileSeasons)
                tabs.Add(new SeasonTabVm(n, n == 0 ? TvL.T("media_detail.specials", "Спецвыпуски", "Specials") : TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", n), _files.Count(f => (f.Season ?? 1) == n)));
        }

        _tabs = tabs;
        SeasonsSection.IsVisible = tabs.Count > 0;
        _activeSeason = tabs.Count > 0 ? tabs[0].Number : 1;
        RenderTabs();
        // Episodes are built separately (after a yield) so the tabs/hero paint
        // first — see LoadAsync.
    }

    private void RenderTabs()
        => SeasonTabs = _tabs.Select(t =>
        {
            var vm = new SeasonTabVm(t.Number, t.Label, t.RawCount, t.Number == _activeSeason);
            vm.Select = new Command(() => SwitchSeason(vm.Number));
            return vm;
        }).ToList();

    private void SwitchSeason(int n)
    {
        _activeSeason = n;
        RenderTabs();
        BuildEpisodes();
    }

    private void BuildEpisodes()
    {
        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        EpisodesHost.Children.Clear();
        _builtCount = 0;
        _seasonEps  = ComputeSeasonEpisodes();
        AppendEpisodeChunk();
        UpdateCounters();
    }

    /// <summary>The FULL episode list for the active season (no cap — cards are
    /// appended in chunks as the page scrolls, see AppendEpisodeChunk).</summary>
    private List<EpisodeVm> ComputeSeasonEpisodes()
    {
        var it = _item!;

        if (it.MediaType == "Movie" || (_files.Length == 1 && _files[0].Episode is null))
        {
            var mf = _files.FirstOrDefault();
            var ws = _watch.FirstOrDefault();
            var movieEp = new EpisodeVm
            {
                Number   = "▶",
                Title    = TvL.T("media_detail.movie_play_default", "Смотреть фильм", "Watch movie"),
                ThumbUrl = !string.IsNullOrEmpty(it.FanartPath)
                    ? $"{ImageBase}/api/image?path={Uri.EscapeDataString(it.FanartPath)}&w=640" : "",
                Have     = mf is not null,
                FilePath = mf?.FilePath,
                Season   = mf?.Season,
                EpisodeNum = mf?.Episode,
                ResumeMs = ws?.ProgressMs ?? 0,
                IsWatched   = ws?.IsWatched ?? false,
                WatchFraction = Frac(ws),
            };
            movieEp.Play = new Command(() => PlayFile(movieEp.Season, movieEp.EpisodeNum, movieEp.FilePath, movieEp.ResumeMs));
            movieEp.Menu = new Command(() => ShowEpisodeMenu(movieEp));
            return new List<EpisodeVm> { movieEp };
        }

        var active    = it.Seasons?.FirstOrDefault(s => s.Number == _activeSeason);
        var maxFileEp = _files.Where(f => (f.Season ?? 1) == _activeSeason)
                              .Select(f => f.Episode ?? 0).DefaultIfEmpty(0).Max();
        var count = Math.Max(active?.EpisodeCount ?? 0, maxFileEp);
        if (count == 0) count = 1;

        var eps = new List<EpisodeVm>(count);
        for (int i = 1; i <= count; i++)
        {
            var f    = _files.FirstOrDefault(x => (x.Season ?? 1) == _activeSeason && x.Episode == i);
            var have = f is not null;
            var ws   = _watch.FirstOrDefault(w => (w.Season ?? 1) == _activeSeason && w.Episode == i);
            var ep = new EpisodeVm
            {
                Number   = i.ToString("00"),
                Title    = _activeSeason == 0 ? TvL.F("detail.special_fmt", "Спецвыпуск {0}", "Special {0}", i) : TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", i),
                Have     = have,
                FilePath = f?.FilePath,
                Season   = _activeSeason,
                EpisodeNum = i,
                ResumeMs = ws?.ProgressMs ?? 0,
                IsWatched   = ws?.IsWatched ?? false,
                WatchFraction = Frac(ws),
            };
            ep.Play = new Command(() => PlayFile(ep.Season, ep.EpisodeNum, ep.FilePath, ep.ResumeMs));
            ep.Menu = new Command(() => ShowEpisodeMenu(ep));
            eps.Add(ep);
        }
        return eps;
    }

    private void AppendEpisodeChunk()
    {
        if (_appending || _thumbCts is null) return;
        _appending = true;
        try
        {
            var end = Math.Min(_builtCount + EpChunk, _seasonEps.Count);
            for (var i = _builtCount; i < end; i++)
                EpisodesHost.Children.Add(_episodeView == "list"
                    ? BuildEpisodeRow(_seasonEps[i], _thumbCts.Token)
                    : BuildEpisodeCard(_seasonEps[i], _thumbCts.Token));
            _builtCount = end;
        }
        finally { _appending = false; }
    }

    /// <summary>Grid⇄list flip: rebuild the episode area and persist the choice
    /// to the shared EpisodeListView preference (the web toggle reads it too).</summary>
    private void SwitchEpisodeView(string view)
    {
        if (_episodeView == view) return;
        _episodeView = view;
        ApplyViewToggleVisual();
        BuildEpisodes();
        _ = SafeAsync(_api.UpdateMyPreferencesAsync(new UpdatePreferencesRequest(
            AccentHue: null, BackdropEnabled: null, BackdropBlurPx: null,
            BackdropBrightness: null, BackdropIntervalSec: null, TvMode: null,
            AudioPreferredLanguage: null, SubtitlePreferredLanguage: null,
            SubtitleSize: null, DefaultVolume: null, AudioPassthrough: null,
            NormalizeVolume: null, Language: null, EpisodeListView: view)));
    }

    private void ApplyViewToggleVisual()
    {
        var grid = _episodeView != "list";
        GridViewBtn.BackgroundColor = grid ? Color.FromArgb("#e8772e") : Color.FromArgb("#1a1d24");
        GridViewIcon.TextColor      = grid ? Colors.White : Color.FromArgb("#c7ccd4");
        ListViewBtn.BackgroundColor = grid ? Color.FromArgb("#1a1d24") : Color.FromArgb("#e8772e");
        ListViewIcon.TextColor      = grid ? Color.FromArgb("#c7ccd4") : Colors.White;
    }

    // D-pad focus auto-scrolls the page, so this fires for both touch and
    // remote: top up the grid when the view nears the built end (~3 rows out).
    private void OnPageScrolled(object? sender, ScrolledEventArgs e)
    {
        if (_builtCount >= _seasonEps.Count) return;
        var remaining = PageScroll.ContentSize.Height - PageScroll.Height - e.ScrollY;
        if (remaining < 900) AppendEpisodeChunk();
    }

    // ── Episode card (web MediaDetail grid look) ────────────────────────────
    // 143dp card, 6 per row in the 922dp strip: big number top-left on the
    // frame, green ✓ chip top-right when watched (amber ! when the file is
    // missing), green on-disk dot bottom-right, resume bar, "Эпизод N" below.
    private const int EpCardW = 143;

    private View BuildEpisodeCard(EpisodeVm ep, CancellationToken ct)
    {
        var art = new Grid { HeightRequest = 80 };
        var image = new Image { Aspect = Aspect.AspectFill };
        art.Add(image);
        if (!string.IsNullOrEmpty(ep.ThumbUrl)) image.Source = ep.ThumbUrl;   // movie fanart
        else if (ep.Have && ep.Season is int s && ep.EpisodeNum is int e)
            EnqueueThumb(image, s, e, ct);

        art.Add(new BoxView
        {
            InputTransparent = true,
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0.25f),
                    new GradientStop(Color.FromArgb("#C7000000"), 1f),
                },
                new Point(0, 0), new Point(0, 1)),
        });
        art.Add(new Label
        {
            Text = ep.Number, InputTransparent = true,
            FontFamily = "ArchivoBlack", FontAttributes = FontAttributes.Bold, FontSize = 15, TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(8, 5, 0, 0),
        });

        // Top-right: watched ✓ (green) / missing file ! (amber).
        var watchChip = MakeEpChip("✓", "#56c596");
        watchChip.IsVisible = ep.IsWatched;
        art.Add(watchChip);
        if (!ep.Have)
        {
            var missing = MakeEpChip("!", "#e8a33d");
            missing.IsVisible = !ep.IsWatched;
            art.Add(missing);
        }

        // Bottom-right: on-disk dot (web's disk badge).
        if (ep.Have)
        {
            art.Add(new Border
            {
                InputTransparent = true,
                HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 0, 6, 6),
                WidthRequest = 16, HeightRequest = 16, StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                BackgroundColor = Color.FromArgb("#A6101720"),
                Content = new Label
                {
                    Text = "●", TextColor = Color.FromArgb("#56c596"), FontSize = 7,
                    HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
                },
            });
        }

        // Resume bar (hand-rolled — the native ProgressBar track renders fat).
        var barFill = new BoxView
        {
            Color = ep.IsWatched ? Color.FromArgb("#56c596") : Color.FromArgb("#e8772e"),
            HorizontalOptions = LayoutOptions.Start,
            WidthRequest = ep.IsWatched ? EpCardW : Math.Round(EpCardW * ep.WatchFraction),
        };
        var bar = new Grid
        {
            HeightRequest = 3, VerticalOptions = LayoutOptions.End, InputTransparent = true,
            IsVisible = ep.IsWatched || ep.WatchFraction > 0.01,
        };
        bar.Add(new BoxView { Color = Color.FromArgb("#66000000") });
        bar.Add(barFill);
        art.Add(bar);

        var rows = new Grid
        {
            RowDefinitions = { new RowDefinition(80), new RowDefinition(GridLength.Auto) },
        };
        rows.Add(art, 0, 0);
        var title = new Label
        {
            Text = ep.Title, FontFamily = "Geist", FontSize = 10,
            TextColor = Color.FromArgb(ep.Have ? "#c7ccd4" : "#7d7972"),
            MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation,
            Padding = new Thickness(8, 6, 8, 8),
        };
        rows.Add(title, 0, 1);

        var card = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Color.FromArgb("#15171d"),
            WidthRequest = EpCardW,
            Margin = new Thickness(0, 0, TvCards.CardGap, TvCards.CardGap),
            Opacity = ep.Have ? 1.0 : 0.72,
        };
        card.Content = rows;
        card.Behaviors.Add(new TvFocusBehavior { Radius = 8, Command = ep.Play, LongCommand = ep.Menu });

        // Toggle-watched updates this card in place (no grid rebuild).
        ep.Refresh = () =>
        {
            watchChip.IsVisible = ep.IsWatched;
            bar.IsVisible = ep.IsWatched || ep.WatchFraction > 0.01;
            barFill.WidthRequest = ep.IsWatched ? EpCardW : Math.Round(EpCardW * ep.WatchFraction);
            barFill.Color = ep.IsWatched ? Color.FromArgb("#56c596") : Color.FromArgb("#e8772e");
        };
        return card;
    }

    /// <summary>List-mode episode row (web d-ep--list): small 16:9 thumb with the
    /// number + resume bar, then "EP NN · title" and a meta line, watched chip at
    /// the right. Same focus/click/long-press semantics as the grid card.</summary>
    private View BuildEpisodeRow(EpisodeVm ep, CancellationToken ct)
    {
        const int rowW = 912, thumbW = 100, thumbH = 56;

        var art = new Grid();
        var image = new Image { Aspect = Aspect.AspectFill };
        art.Add(image);
        if (!string.IsNullOrEmpty(ep.ThumbUrl)) image.Source = ep.ThumbUrl;
        else if (ep.Have && ep.Season is int s && ep.EpisodeNum is int e)
            EnqueueThumb(image, s, e, ct);
        var barFill = new BoxView
        {
            Color = ep.IsWatched ? Color.FromArgb("#56c596") : Color.FromArgb("#e8772e"),
            HorizontalOptions = LayoutOptions.Start,
            WidthRequest = ep.IsWatched ? thumbW : Math.Round(thumbW * ep.WatchFraction),
        };
        var bar = new Grid
        {
            HeightRequest = 2.5, VerticalOptions = LayoutOptions.End, InputTransparent = true,
            IsVisible = ep.IsWatched || ep.WatchFraction > 0.01,
        };
        bar.Add(new BoxView { Color = Color.FromArgb("#66000000") });
        bar.Add(barFill);
        art.Add(bar);
        var thumb = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#10141a"),
            WidthRequest = thumbW, HeightRequest = thumbH,
            VerticalOptions = LayoutOptions.Center,
            Content = art,
        };

        var titleLine = new HorizontalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label
                {
                    Text = $"EP {ep.Number}", FontFamily = "GeistMono", FontSize = 9.5,
                    FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#f5934f"),
                    VerticalOptions = LayoutOptions.Center,
                },
                new Label
                {
                    Text = ep.Title, FontFamily = "Geist", FontSize = 11.5,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb(ep.Have ? "#e8eaed" : "#7d7972"),
                    MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation,
                    VerticalOptions = LayoutOptions.Center,
                },
            },
        };
        var metaLabel = new Label
        {
            Text = ep.Have
                ? (_item?.Runtime is > 0 ? $"{_item.Runtime}m" : TvL.T("episode_card.tooltip_on_disk", "На диске", "On disk"))
                : TvL.T("episode_card.meta_missing", "Не скачано", "Not downloaded"),
            FontFamily = "Geist", FontSize = 9,
            TextColor = Color.FromArgb(ep.Have ? "#8a91a0" : "#e8a33d"),
        };
        var main = new VerticalStackLayout
        {
            Spacing = 2, VerticalOptions = LayoutOptions.Center,
            Children = { titleLine, metaLabel },
        };

        var watchChip = MakeEpChip("✓", "#56c596");
        watchChip.IsVisible = ep.IsWatched;
        watchChip.VerticalOptions = LayoutOptions.Center;
        watchChip.Margin = new Thickness(0);

        var cols = new Grid
        {
            Padding = new Thickness(8, 6),
            ColumnSpacing = 14,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        cols.Add(thumb, 0, 0);
        cols.Add(main, 1, 0);
        cols.Add(watchChip, 2, 0);
        if (!ep.Have)
        {
            var missing = MakeEpChip("!", "#e8a33d");
            missing.VerticalOptions = LayoutOptions.Center;
            missing.Margin = new Thickness(0);
            cols.Add(missing, 2, 0);
        }

        var row = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Color.FromArgb("#15171d"),
            WidthRequest = rowW,
            Margin = new Thickness(0, 0, TvCards.CardGap, TvCards.CardGap),
            Opacity = ep.Have ? 1.0 : 0.72,
            Content = cols,
        };
        row.Behaviors.Add(new TvFocusBehavior { Radius = 8, Command = ep.Play, LongCommand = ep.Menu });

        ep.Refresh = () =>
        {
            watchChip.IsVisible = ep.IsWatched;
            bar.IsVisible = ep.IsWatched || ep.WatchFraction > 0.01;
            barFill.WidthRequest = ep.IsWatched ? thumbW : Math.Round(thumbW * ep.WatchFraction);
            barFill.Color = ep.IsWatched ? Color.FromArgb("#56c596") : Color.FromArgb("#e8772e");
        };
        return row;
    }

    private static Border MakeEpChip(string glyph, string color) => new()
    {
        InputTransparent = true,
        HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start,
        Margin = new Thickness(0, 5, 6, 0),
        WidthRequest = 18, HeightRequest = 18,
        StrokeThickness = 1, Stroke = Color.FromArgb(color),
        StrokeShape = new RoundRectangle { CornerRadius = 5 },
        BackgroundColor = Color.FromArgb("#8C10141a"),
        Content = new Label
        {
            Text = glyph, TextColor = Color.FromArgb(color), FontSize = 10,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
        },
    };

    /// <summary>Episode frame via the cookie-carrying HttpClient, throttled by
    /// ThumbGate — an uncached frame costs the server an ffmpeg grab, so the
    /// queue keeps a page-load from firing 175 of them at once.</summary>
    private void EnqueueThumb(Image img, int season, int episode, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ThumbGate.WaitAsync(ct);
                try
                {
                    var bytes = await _http.GetByteArrayAsync(
                        $"/api/media/{_id}/episode-thumb?season={season}&episode={episode}", ct);
                    if (bytes.Length == 0 || ct.IsCancellationRequested) return;
                    MainThread.BeginInvokeOnMainThread(() =>
                        img.Source = ImageSource.FromStream(() => new MemoryStream(bytes)));
                }
                finally { ThumbGate.Release(); }
            }
            catch { /* number-only card is fine */ }
        }, ct);
    }

    // ── Long-press: watched toggle (the TV counterpart of the web checkbox) ──
    private async void ShowEpisodeMenu(EpisodeVm ep)
    {
        if (_guid == Guid.Empty) return;
        var mark  = ep.IsWatched ? TvL.T("movie_file_card.btn_mark_unwatched", "Отметить непросмотренным", "Mark as unwatched") : TvL.T("movie_file_card.btn_mark_watched", "Отметить просмотренным", "Mark as watched");
        var title = ep.EpisodeNum is int n
            ? (_activeSeason > 0
                ? $"{TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", _activeSeason)} · {TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", n)}"
                : TvL.F("detail.episode_fmt", "Эпизод {0}", "Episode {0}", n))
            : (_item?.Title ?? "");
        var choice = await DisplayActionSheet(title, TvL.T("common.btn_cancel", "Отмена", "Cancel"), null, mark);
        if (choice != mark) return;

        try
        {
            var dto = await _api.ToggleWatchedAsync(new Animarr.Shared.Requests.ToggleWatchedRequest(
                _guid, ep.Season, ep.EpisodeNum, ep.FilePath, !ep.IsWatched));
            ep.IsWatched     = dto?.IsWatched ?? !ep.IsWatched;
            ep.WatchFraction = ep.IsWatched ? 1 : 0;
            ep.Refresh?.Invoke();
            UpdateCounters();
            // Keep the cache fresh so a season switch shows the new state.
            _watch = await SafeAsync(_api.GetWatchStatesAsync(_guid)) ?? _watch;
        }
        catch { }
    }

    /// <summary>Web's "164 of 175 watched · 171 of 175 on disk" line.</summary>
    private void UpdateCounters()
    {
        if (_item?.MediaType == "Movie" || _seasonEps.Count <= 1)
        {
            WatchedCounter.IsVisible = false;
            return;
        }
        var total   = _seasonEps.Count;
        var watched = _seasonEps.Count(e => e.IsWatched);
        var disk    = _seasonEps.Count(e => e.Have);
        WatchedCounter.Text = TvL.F("detail.watched_counter_fmt", "{0} из {1} просмотрено · {2} из {1} на диске", "{0} of {1} watched · {2} of {1} on disk", watched, total, disk);
        WatchedCounter.IsVisible = true;
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
        /// <summary>D-pad OK action for this tab (bound to TvFocusBehavior.Command).</summary>
        public System.Windows.Input.ICommand? Select { get; set; }

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
        /// <summary>Direct image URL (movie fanart); episode frames are queued
        /// through EnqueueThumb instead.</summary>
        public string  ThumbUrl   { get; init; } = "";
        public bool    Have       { get; init; }
        public string? FilePath   { get; init; }
        public int?    Season     { get; init; }
        public int?    EpisodeNum { get; init; }
        public long    ResumeMs   { get; init; }
        // Mutable: the long-press watched toggle updates these in place.
        public bool    IsWatched     { get; set; }
        public double  WatchFraction { get; set; }   // 0..1 resume position
        public System.Windows.Input.ICommand? Play { get; set; }
        public System.Windows.Input.ICommand? Menu { get; set; }
        /// <summary>Repaints this card's watched chip / resume bar after a toggle
        /// (set by BuildEpisodeCard — the cards are code-built, not data-bound).</summary>
        public Action? Refresh { get; set; }
    }
}
