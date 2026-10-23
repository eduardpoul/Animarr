using System.Globalization;
using Animarr.Shared;
using Animarr.Shared.Models;

namespace Animarr.App;

/// <summary>
/// The phone player's two layouts, YouTube's: held upright the picture sits
/// in a 16:9 box at the top and the title's details fill the rest — the
/// episode and its synopsis, previous / next, the season's episodes (tap one
/// to play it); held sideways it fills the screen with the system bars
/// hidden, as the player always did. The HUD's ⛶ and Back swap them; with
/// the system's auto-rotate on, turning the phone does too — ⛶ holds its
/// orientation until the phone is turned that way, then lets the sensor
/// decide again. The layout follows the page's shape (OnSizeAllocated), so
/// picture-in-picture's small window gets the bare picture as well.
/// </summary>
public partial class PlayerPage
{
    private bool _phoneFullscreen = true;
    private (bool Full, double W, double H) _phoneLayout;
    private BoxView? _videoBox;

    private ScrollView? _details;
    private Label? _dEyebrow, _dTitle, _dSubtitle, _dMeta, _dDesc, _dPrevSub, _dNextSub;
    private Border? _dPrev, _dNext;
    private Label? _dPrevTitle, _dNextTitle, _dEpisodesHead;
    private Task? _phoneLang;
    private View? _dNav, _dEpisodesBlock;
    private ScrollView? _dSeasonsScroll;
    private HorizontalStackLayout? _dSeasons;
    private VerticalStackLayout? _dEpisodes;
    private bool _dDescExpanded;
    private int? _dSeasonShown;
    private MediaItemDto? _dItem;
    private bool _dItemAsked;
    private int _dGen;

    private const string DetailsBg   = "#101114";
    private const string RowBg       = "#1b1d21";
    private const string RowBgActive = "#2a2420";
    private const string Accent      = "#f49157";
    private const string TextDim     = "#a8abb3";

    /// <summary>SetUpPhone part: the two rows and the details pane.</summary>
    private void SetUpPhoneLayout()
    {
        RootGrid.RowDefinitions = new RowDefinitionCollection(
            new RowDefinition(GridLength.Star), new RowDefinition(new GridLength(0)));
        // Row 0's bounds for the native surface / subtitles (raw native
        // children of the grid, laid out by hand — see PhoneVideoBox).
        // No InputTransparent: it wraps the view in a container (Left / Top
        // would read 0); the touch layer above it takes the taps anyway.
        _videoBox = new BoxView { Color = Colors.Transparent };
        RootGrid.Children.Insert(0, _videoBox);
        _details = BuildDetails();
        _details.IsVisible = false;
        Grid.SetRow(_details, 1);
        RootGrid.Children.Add(_details);
        // The sheets (⚙, audio, subtitles…) cover the whole screen.
        Grid.SetRowSpan(SheetOverlay, 2);
        FullscreenButton.IsVisible = true;
        FullscreenFocus.Command = new Command(ToggleFullscreen);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (Phone && width > 0 && height > 0) ApplyPhoneLayout(width > height, width, height);
    }

    private void ApplyPhoneLayout(bool full, double w, double h)
    {
        if (_phoneLayout == (full, w, h)) return;
        _phoneLayout = (full, w, h);
        _phoneFullscreen = full;
        RootGrid.RowDefinitions[0].Height = full ? GridLength.Star : new GridLength(Math.Round(w * 9 / 16));
        RootGrid.RowDefinitions[1].Height = full ? new GridLength(0) : GridLength.Star;
        if (_details is not null) _details.IsVisible = !full;
        FullscreenIcon.Text = full ? FluentGlyphs.ExitFullScreen : FluentGlyphs.FullScreen;

        // A sideways phone keeps the chrome it had; the upright 16:9 box (~200 dp
        // tall) gets a compact one — the title moves to the details below.
        TopBar.HeightRequest        = full ? 100 : 52;
        TitleBlock.IsVisible        = full;
        BackButton.Margin           = full ? new Thickness(12, 14, 0, 0) : new Thickness(4, 4, 0, 0);
        BottomBar.HeightRequest     = full ? 150 : 92;
        BottomPanel.Padding         = full ? new Thickness(16, 0, 16, 8) : new Thickness(8, 0, 8, 0);
        CenterControls.Spacing      = full ? 48 : 32;
        SizeRound(CenterPlayButton, full ? 76 : 58);
        SizeRound(CenterPrevButton, full ? 56 : 44);
        SizeRound(CenterNextButton, full ? 56 : 44);
        CenterPlayIcon.FontSize     = full ? 36 : 30;
        InfoLine.Margin             = new Thickness(0, 0, 0, full ? 120 : 80);
        UpNextCard.WidthRequest     = full ? 360 : Math.Max(240, w - 24);
        UpNextCard.Margin           = full ? new Thickness(0, 0, 16, 112) : new Thickness(12, 0, 12, 76);
        SeekHintLeft.Margin         = new Thickness(full ? 64 : 24, 0, 0, 0);
        SeekHintRight.Margin        = new Thickness(0, 0, full ? 64 : 24, 0);
        // The sheets are centred on the whole screen; upright it is narrow.
        SheetCard.MinimumWidthRequest = Math.Min(380, w - 24);
        SheetScroll.WidthRequest      = Math.Min(460, w - 64);
        SheetScroll.MaximumHeightRequest = full ? 380 : Math.Max(240, h * 0.6);
#if ANDROID
        // Upright the HUD fills the small box: subtitles wait under it (YouTube's).
        Services.NativePlayerService.SubtitleLift = full ? 0.22f : 0f;
#endif

        if (_details is not null && !full) _ = RefreshPhoneDetailsAsync();
        if (!_inPip) SetPhoneImmersive(full);
    }

#if ANDROID
    /// <summary>Upright: the 16:9 box at the top (px, in the grid's native
    /// view) — the picture and its subtitles go there, not mid-screen under
    /// the details. Full screen: null, the whole page.</summary>
    private (int L, int T, int W, int H)? PhoneVideoBox() =>
        !_phoneFullscreen && (_videoBox?.Handler?.ContainerView ?? _videoBox?.Handler?.PlatformView) is Android.Views.View v
        && v.Width > 0 && v.Height > 0
            ? (v.Left, v.Top, v.Width, v.Height)
            : null;
#endif

    private static void SizeRound(Border b, double size)
    {
        b.WidthRequest = b.HeightRequest = size;
        b.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = size / 2 };
    }

    /// <summary>Back on a phone: full screen first goes back to the upright
    /// layout (YouTube's), then Back leaves the player.</summary>
    private bool PhoneBack()
    {
        if (!_phoneFullscreen || _inPip || !Phone) return false;
        // A tablet / a phone with no upright layout to go to is already there.
        if (_phoneLayout.W > 0 && _phoneLayout.W <= _phoneLayout.H) return false;
        ToggleFullscreen();
        return true;
    }

    // ── ⛶ and turning the phone ────────────────────────────────────────────

    /// <summary>"landscape" / "portrait" — the orientation ⛶ forced, held until
    /// the phone is actually turned that way (then the sensor decides again,
    /// when the system's auto-rotate is on). Null: nothing held.</summary>
    private string? _orientationHold;

    private void ToggleFullscreen()
    {
        _orientationHold = _phoneFullscreen ? "portrait" : "landscape";
        JsInterop.PlatformBridge.SetOrientation(_orientationHold);
        ArmAutoHide();
    }

#if ANDROID
    private DeviceTurnListener? _turnListener;

    /// <summary>OnPhoneAttached part: let the phone turn (auto-rotate permitting).</summary>
    private void StartOrientationFollow()
    {
        var ctx = Platform.CurrentActivity ?? Platform.AppContext;
        JsInterop.PlatformBridge.SetOrientation("auto");
        try
        {
            _turnListener ??= new DeviceTurnListener(ctx, this);
            if (_turnListener.CanDetectOrientation()) _turnListener.Enable();
        }
        catch (Exception ex) { Log($"orientation listener: {ex.Message}"); }
    }

    private void StopOrientationFollow()
    {
        try { _turnListener?.Disable(); _turnListener?.Dispose(); } catch { }
        _turnListener = null;
        _orientationHold = null;
    }

    /// <summary>The device's angle (0 upright, 90 / 270 sideways): a held
    /// orientation lets go once the phone is held that way.</summary>
    private void OnDeviceTurned(int degrees)
    {
        if (_orientationHold is not { } hold) return;
        bool sideways = degrees is > 60 and < 120 or > 240 and < 300;
        bool upright  = degrees is < 25 or > 335;
        if (!(hold == "landscape" ? sideways : upright)) return;
        _orientationHold = null;
        // Auto-rotate off: the forced orientation stays until ⛶ / Back.
        if (AutoRotateOn()) JsInterop.PlatformBridge.SetOrientation("auto");
    }

    private static bool AutoRotateOn()
    {
        try
        {
            var cr = (Platform.CurrentActivity ?? Platform.AppContext).ContentResolver;
            return Android.Provider.Settings.System.GetInt(cr, Android.Provider.Settings.System.AccelerometerRotation, 0) == 1;
        }
        catch { return false; }
    }

    private sealed class DeviceTurnListener : Android.Views.OrientationEventListener
    {
        private readonly WeakReference<PlayerPage> _page;
        public DeviceTurnListener(Android.Content.Context ctx, PlayerPage page) : base(ctx) => _page = new(page);

        public override void OnOrientationChanged(int orientation)
        {
            if (orientation == OrientationUnknown || !_page.TryGetTarget(out var p)) return;
            p.OnDeviceTurned(orientation);
        }
    }
#endif

    // ── The details pane (upright) ───────────────────────────────────────────

    private ScrollView BuildDetails()
    {
        _dEyebrow  = new Label { TextColor = Color.FromArgb(Accent), FontFamily = "sans-serif-medium", FontSize = 12, CharacterSpacing = 1 };
        _dTitle    = new Label { TextColor = Colors.White, FontFamily = "sans-serif-medium", FontAttributes = FontAttributes.Bold,
                                 FontSize = 19, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
        _dSubtitle = new Label { TextColor = Color.FromArgb(TextDim), FontFamily = "sans-serif", FontSize = 14,
                                 MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
        _dMeta     = new Label { TextColor = Color.FromArgb(TextDim), FontFamily = "sans-serif", FontSize = 13 };

        (_dPrev, _dPrevTitle, _dPrevSub) = NavButton(FluentGlyphs.Previous, -1);
        (_dNext, _dNextTitle, _dNextSub) = NavButton(FluentGlyphs.Next, +1);
        var nav = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)),
                             ColumnSpacing = 10 };
        nav.Add(_dPrev, 0, 0);
        nav.Add(_dNext, 1, 0);
        _dNav = nav;

        _dDesc = new Label { TextColor = Color.FromArgb("#d8dae0"), FontFamily = "sans-serif", FontSize = 14,
                             LineHeight = 1.25, MaxLines = 4, LineBreakMode = LineBreakMode.TailTruncation };
        var expand = new TapGestureRecognizer();
        expand.Tapped += (_, _) =>
        {
            _dDescExpanded = !_dDescExpanded;
            _dDesc.MaxLines = _dDescExpanded ? int.MaxValue : 4;
        };
        _dDesc.GestureRecognizers.Add(expand);

        _dSeasons = new HorizontalStackLayout { Spacing = 8 };
        _dSeasonsScroll = new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = _dSeasons,
                                           HorizontalScrollBarVisibility = ScrollBarVisibility.Never, IsVisible = false };
        _dEpisodes = new VerticalStackLayout { Spacing = 6 };
        _dEpisodesBlock = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                (_dEpisodesHead = SectionHead()),
                _dSeasonsScroll,
                _dEpisodes,
            },
        };

        ApplyDetailsStrings();
        return new ScrollView
        {
            BackgroundColor = Color.FromArgb(DetailsBg),
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 14, 16, 28),
                Spacing = 12,
                Children =
                {
                    new VerticalStackLayout { Spacing = 3, Children = { _dEyebrow, _dTitle, _dSubtitle, _dMeta } },
                    _dNav,
                    _dDesc,
                    _dEpisodesBlock,
                },
            },
        };
    }

    private static Label SectionHead() => new()
    {
        TextColor = Color.FromArgb(TextDim), FontFamily = "sans-serif-medium",
        FontSize = 12, CharacterSpacing = 1.5, Margin = new Thickness(0, 6, 0, 0),
    };

    /// <summary>The pane's fixed texts (again once the lang pack is in).</summary>
    private void ApplyDetailsStrings()
    {
        _dPrevTitle!.Text = TvL.T("player.hud_prev", "Предыдущая серия", "Previous episode");
        _dNextTitle!.Text = TvL.T("player.hud_next", "Следующая серия", "Next episode");
        _dEpisodesHead!.Text = TvL.T("media_detail.overline_episodes", "Эпизоды", "Episodes").ToUpperInvariant();
    }

    /// <summary>A phone has no TV sign-in to load the lang pack (TvL would
    /// stay on its Russian fallbacks): the account's language, else the
    /// device's, else English — the web UI's rule.</summary>
    private async Task LoadPhoneLanguageAsync()
    {
        string? code = null;
        try { code = (await _api.GetMyPreferencesAsync()).Language; }
        catch (Exception ex) { Log($"details: preferences {ex.Message}"); }
        if (!IsPackLanguage(code)) code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (!IsPackLanguage(code)) code = "en";
        await TvL.LoadAsync(_http, code!.ToLowerInvariant());
        if (_dPrevTitle is not null) ApplyDetailsStrings();
        EyebrowLabel.Text = Eyebrow(_season, _episode);
    }

    private static bool IsPackLanguage(string? code) =>
        code?.ToLowerInvariant() is "en" or "ru" or "uk" or "de" or "es";

    private (Border Button, Label Title, Label Sub) NavButton(string glyph, int delta)
    {
        var sub = new Label { TextColor = Color.FromArgb(TextDim), FontFamily = "sans-serif", FontSize = 12,
                              MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
        var icon = new Label { Style = (Style)Resources["HudIcon"], Text = glyph, FontSize = 20 };
        var title = new Label { TextColor = Colors.White, FontFamily = "sans-serif-medium", FontSize = 13,
                                MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
        var text = new VerticalStackLayout
        {
            Spacing = 1, VerticalOptions = LayoutOptions.Center,
            Children = { title, sub },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)),
                             ColumnSpacing = 10 };
        row.Add(icon, 0, 0);
        row.Add(text, 1, 0);
        var b = new Border { Content = row, Padding = new Thickness(12, 10), StrokeThickness = 0,
                             StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                             BackgroundColor = Color.FromArgb(RowBg) };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => { if (b.Opacity >= 1) GoAdjacentEpisode(delta); };
        b.GestureRecognizers.Add(tap);
        return (b, title, sub);
    }

    /// <summary>Fill the pane for the current file: the title's facts once, the
    /// episode's text, previous / next, the season's episodes with what was
    /// watched. Cheap to call again — everything but the watch states is cached.</summary>
    private async Task RefreshPhoneDetailsAsync()
    {
        if (_details is null || _phoneFullscreen) return;
        int gen = ++_dGen;
        var file = _filePath;
        await (_phoneLang ??= LoadPhoneLanguageAsync());

        if (!_dItemAsked)
        {
            _dItemAsked = true;
            try { _dItem = await _api.GetMediaItemAsync(_mediaItemId); }
            catch (Exception ex) { Log($"details: item {ex.Message}"); _dItemAsked = false; }
        }
        var eps   = await GetEpisodesAsync();
        var metas = await EpisodeMetadataAsync();
        IReadOnlyList<WatchStateDto> states = Array.Empty<WatchStateDto>();
        try
        {
            var ask = _api.GetWatchStatesAsync(_mediaItemId);
            if (await Task.WhenAny(ask, Task.Delay(3000)) == ask) states = await ask;
        }
        catch (Exception ex) { Log($"details: watch states {ex.Message}"); }
        if (gen != _dGen || file != _filePath || _closed) return;

        bool series = _episode is not null && eps.Count > 1;
        var cur  = eps.FirstOrDefault(f => string.Equals(f.FilePath, _filePath, StringComparison.OrdinalIgnoreCase));
        var meta = cur is not null ? MetaFor(metas, cur) : _episode is int e ? MetaFor(metas, _season, e, null) : null;

        // Head: the episode (its name, else "Эп. N") over the title; a film: its name.
        _dEyebrow!.Text = series
            ? $"{TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", _season ?? 1)} · {TvL.F("common.episode_short_fmt", "Эп. {0}", "Ep {0}", _episode!)}".ToUpperInvariant()
            : string.Join(" · ", new[] { _dItem?.Year?.ToString(CultureInfo.InvariantCulture), _dItem?.Studio }
                  .Where(s => !string.IsNullOrWhiteSpace(s))).ToUpperInvariant();
        _dEyebrow.IsVisible = _dEyebrow.Text.Length > 0;
        _dTitle!.Text = series ? meta?.Title ?? TvL.F("common.episode_short_fmt", "Эп. {0}", "Ep {0}", _episode!) : _title;
        _dSubtitle!.Text = series ? _title : "";
        _dSubtitle.IsVisible = series;
        _dMeta!.Text = MetaLine(meta, series);
        _dMeta.IsVisible = _dMeta.Text.Length > 0;
        _dDesc!.Text = FirstText(meta?.Overview, _dItem?.Description)
                       ?? TvL.T("media_detail.no_synopsis", "Описание отсутствует.", "No synopsis.");
        // A film has the pane to itself: its synopsis whole.
        _dDescExpanded = !series;
        _dDesc.MaxLines = series ? 4 : int.MaxValue;

        // Previous / next.
        _dNav!.IsVisible = series;
        SetNav(_dPrev!, _dPrevSub!, _prevEpisode, metas);
        SetNav(_dNext!, _dNextSub!, _nextEpisode, metas);

        // The season's episodes (the one playing first in view).
        _dEpisodesBlock!.IsVisible = series;
        if (!series) return;
        var seasons = eps.Select(f => f.Season ?? 1).Distinct().Order().ToList();
        if (_dSeasonShown is not int shown || !seasons.Contains(shown)) _dSeasonShown = _season ?? 1;
        BuildSeasonChips(seasons, eps, metas, states);
        BuildEpisodeRows(eps, metas, states);
    }

    private void SetNav(Border b, Label sub, MediaFileDto? f, List<EpisodeMetaDto> metas)
    {
        b.Opacity = f is null ? 0.4 : 1;
        sub.Text = f is null ? "—" : EpisodeName(f, metas);
    }

    private void BuildSeasonChips(List<int> seasons, List<MediaFileDto> eps, List<EpisodeMetaDto> metas,
        IReadOnlyList<WatchStateDto> states)
    {
        _dSeasons!.Children.Clear();
        _dSeasonsScroll!.IsVisible = seasons.Count > 1;
        if (seasons.Count <= 1) return;
        foreach (var s in seasons)
        {
            bool on = s == _dSeasonShown;
            var chip = new Border
            {
                Padding = new Thickness(14, 7), StrokeThickness = on ? 0 : 1, Stroke = Color.FromArgb("#3a3d44"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
                BackgroundColor = on ? Color.FromArgb(Accent) : Colors.Transparent,
                Content = new Label { Text = TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", s),
                                      TextColor = on ? Color.FromArgb("#0f1115") : Colors.White,
                                      FontFamily = "sans-serif-medium", FontSize = 13 },
            };
            int season = s;
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (_dSeasonShown == season) return;
                _dSeasonShown = season;
                BuildSeasonChips(seasons, eps, metas, states);
                BuildEpisodeRows(eps, metas, states);
            };
            chip.GestureRecognizers.Add(tap);
            _dSeasons.Children.Add(chip);
        }
    }

    private void BuildEpisodeRows(List<MediaFileDto> eps, List<EpisodeMetaDto> metas, IReadOnlyList<WatchStateDto> states)
    {
        _dEpisodes!.Children.Clear();
        foreach (var f in eps.Where(f => (f.Season ?? 1) == _dSeasonShown))
        {
            bool current = string.Equals(f.FilePath, _filePath, StringComparison.OrdinalIgnoreCase);
            var m = MetaFor(metas, f);
            var ws = states.FirstOrDefault(w => string.Equals(w.FilePath, f.FilePath, StringComparison.OrdinalIgnoreCase))
                     ?? states.FirstOrDefault(w => (w.Season ?? 1) == (f.Season ?? 1) && w.Episode == f.Episode);
            var sub = string.Join(" · ", new[]
            {
                m?.RuntimeMin is int rt and > 0 ? TvL.F("common.duration_m_fmt", "{0} мин", "{0} min", rt) : null,
                current ? TvL.T("player.now_playing_short", "Сейчас", "Playing")
                : ws is { IsWatched: true } ? "✓ " + TvL.T("player.watched", "Просмотрено", "Watched")
                : ws is { ProgressMs: > 0 } ? TvL.F("player.stopped_at", "Остановились на {0}", "Stopped at {0}", Fmt(ws.ProgressMs!.Value))
                : null,
            }.Where(s => !string.IsNullOrEmpty(s)));

            var number = new Label { Text = (f.Episode ?? 0).ToString("00", CultureInfo.InvariantCulture),
                                     TextColor = current ? Color.FromArgb(Accent) : Colors.White,
                                     FontFamily = "sans-serif-medium", FontAttributes = FontAttributes.Bold, FontSize = 16,
                                     VerticalOptions = LayoutOptions.Center, WidthRequest = 30 };
            var text = new VerticalStackLayout
            {
                Spacing = 2, VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label { Text = m?.Title ?? TvL.F("common.episode_short_fmt", "Эп. {0}", "Ep {0}", f.Episode ?? 0),
                                TextColor = Colors.White, FontFamily = "sans-serif-medium", FontSize = 14,
                                MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation },
                    new Label { Text = sub, IsVisible = sub.Length > 0, FontFamily = "sans-serif", FontSize = 12,
                                TextColor = current ? Color.FromArgb(Accent) : Color.FromArgb(TextDim) },
                },
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)),
                                  ColumnSpacing = 12 };
            grid.Add(number, 0, 0);
            grid.Add(text, 1, 0);
            var row = new Border
            {
                Content = grid, Padding = new Thickness(12, 10), StrokeThickness = current ? 1 : 0,
                Stroke = Color.FromArgb(Accent), Opacity = ws is { IsWatched: true } && !current ? 0.7 : 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                BackgroundColor = Color.FromArgb(current ? RowBgActive : RowBg),
            };
            if (!current)
            {
                var file = f;
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => SwitchToEpisode(file);
                row.GestureRecognizers.Add(tap);
            }
            _dEpisodes.Children.Add(row);
        }
    }

    /// <summary>"24 мин · 4 июля 2025 · ★ 8.3" — the episode's, else the title's.</summary>
    private string MetaLine(EpisodeMetaDto? m, bool series)
    {
        var parts = new List<string>();
        int? runtime = m?.RuntimeMin ?? (series ? null : _dItem?.Runtime);
        if (runtime is int rt and > 0) parts.Add(TvL.F("common.duration_m_fmt", "{0} мин", "{0} min", rt));
        if (m?.AirDate is { } ad && DateTime.TryParse(ad, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            parts.Add(d.ToString("d MMMM yyyy", UiCulture()));
        else if (!series && _dItem?.Year is int y) { /* the year is in the eyebrow */ }
        double? rating = m?.Rating ?? (series ? null : _dItem?.Rating);
        if (rating is double r and > 0) parts.Add("★ " + r.ToString("0.0", UiCulture()));
        return string.Join(" · ", parts);
    }

    private static CultureInfo UiCulture()
    {
        try { return CultureInfo.GetCultureInfo(TvL.Lang); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private static string? FirstText(params string?[] texts) => texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    private string EpisodeName(MediaFileDto f, List<EpisodeMetaDto> metas)
    {
        var ep = TvL.F("common.episode_short_fmt", "Эп. {0}", "Ep {0}", f.Episode ?? 0);
        if (f.Season is int s && s != (_season ?? 1)) ep = $"{TvL.F("media_detail.season_default", "Сезон {0}", "Season {0}", s)} · {ep}";
        return MetaFor(metas, f)?.Title is { } t ? $"{ep} · {t}" : ep;
    }

    private static EpisodeMetaDto? MetaFor(List<EpisodeMetaDto> metas, MediaFileDto f) =>
        MetaFor(metas, f.Season, f.Episode ?? 0, f.AbsoluteEpisode);

    /// <summary>The episode's TMDB texts: its own (season, episode) — the
    /// server also lists a split season's rows that way — else, for an
    /// absolutely numbered file, TMDB season 1 / its absolute number.</summary>
    private static EpisodeMetaDto? MetaFor(List<EpisodeMetaDto> metas, int? season, int episode, int? absolute) =>
        metas.FirstOrDefault(m => m.Season == (season ?? 1) && m.Episode == episode)
        ?? (absolute is int abs ? metas.FirstOrDefault(m => m.Season == 1 && m.Episode == abs) : null);
}
