using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using Animarr.App.Services;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

namespace Animarr.App;

/// <summary>
/// Full-screen native player. ExoPlayer (via NativePlayerService) renders into a
/// TextureView added to the root grid; the XAML HUD sits on top. Two control
/// modes, like a web/YouTube-TV player:
///
///   • HUD hidden (video playing): the remote drives playback directly —
///     OK = play/pause, ◀/▶ = seek ±10s. ▲/▼ reveals the HUD.
///   • HUD shown: focus lands on the on-screen buttons; arrows move focus
///     between them and OK activates the focused one (normal D-pad nav). The
///     HUD auto-hides after a few seconds of no input while playing.
///
/// Keys are delivered from MainActivity.OnKeyDown via <see cref="HandleKey"/>,
/// which only intercepts them while the HUD is hidden; once it's up, keys fall
/// through to MAUI focus navigation.
/// </summary>
public partial class PlayerPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PlayerPage? Current { get; private set; }
    public static bool HandleGlobalKey(int keyCode) => Current?.HandleKey(keyCode) ?? false;

    private readonly HttpClient _http;
    private readonly ServerAddressProvider _addr;
    private readonly IAnimarrApiClient _api;
    private string ImageBase => _addr.Current!.ToString().TrimEnd('/');

    private readonly Guid   _mediaItemId;
    private int?   _season;
    private int?   _episode;
    private string _filePath;
    private readonly long   _resumeMs;

    // Playback timeline. A downscaled quality (maxHeight > 0) plays a server-side
    // HLS transcode whose manifest is already seeked, so ExoPlayer position 0 maps
    // to _streamBaseMs on the real runtime. Direct Play (original quality) has base
    // 0 and ExoPlayer's own absolute position. _knownDurationMs is the full runtime
    // from the start response (a shortened HLS playlist can't report it).
    private int  _maxHeight;
    private long _streamBaseMs;
    private long _knownDurationMs;
    private List<MediaFileDto>? _episodes;

    private IDispatcherTimer? _timer;
    private IDispatcherTimer? _hudTimer;
    private int  _ticks;
    private long _durationMs;
    private bool _started;
    private bool _attached;

    private double? _introStart, _introEnd, _creditsStart, _creditsEnd;
    private double  _skipTarget;
    private bool    _skipVisible;

    public PlayerPage(Guid mediaItemId, int? season, int? episode,
                      string filePath, string? title, long resumeMs = 0)
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);

        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI DI container not ready.");
        _http = services.GetRequiredService<HttpClient>();
        _addr = services.GetRequiredService<ServerAddressProvider>();
        _api  = services.GetRequiredService<IAnimarrApiClient>();

        _mediaItemId = mediaItemId;
        _season = season;
        _episode = episode;
        _filePath = filePath;
        _resumeMs = resumeMs;

        TitleLabel.Text = (title ?? "").ToUpperInvariant();
        EyebrowLabel.Text = episode is not null
            ? $"NOW PLAYING · S{season ?? 1} · EP {episode}"
            : "NOW PLAYING";

        BackFocus.Command      = new Command(async () => await Navigation.PopAsync());
        SeekBackFocus.Command  = new Command(() => SeekBy(-10_000));
        PrevFocus.Command      = new Command(() => GoAdjacentEpisode(-1));
        PlayPauseFocus.Command = new Command(TogglePlay);
        NextFocus.Command      = new Command(() => GoAdjacentEpisode(+1));
        SeekFwdFocus.Command   = new Command(() => SeekBy(+10_000));
        SkipFocus.Command      = new Command(DoSkip);
        VolumeFocus.Command    = new Command(OpenVolumeSheet);
        AudioFocus.Command     = new Command(OpenAudioSheet);
        SubsFocus.Command      = new Command(OpenSubsSheet);
        QualityFocus.Command   = new Command(OpenQualitySheet);
        AspectFocus.Command    = new Command(OpenAspectSheet);
        InfoFocus.Command      = new Command(ShowInfo);
        VolumeSliderFocus.Command = new Command(CloseSheet);

        // Re-apply the remembered aspect mode ("default" = fit/letterbox) —
        // the raw TextureView stretches non-16:9 sources without it.
        _aspect = Preferences.Default.Get("player_aspect", "default");
        _ = NativePlayerService.Instance?.SetAspectRatioAsync(_aspect);

        RootGrid.Loaded += (_, _) => AttachAndStart();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Current = this;
        RevealHud();  // show the HUD + focus the play button on entry
    }

    // ── Remote control (simple) ─────────────────────────────────────────────
    // HUD hidden → any key opens it and focus lands on the buttons.
    // HUD shown → keys fall through to normal MAUI focus navigation (arrows move
    // between buttons, OK activates). Back hides the HUD (handled via OnBack).
    public bool HandleKey(int keyCode)
    {
#if ANDROID
        var kc = (Android.Views.Keycode)keyCode;

        // A submenu is open → it owns the keys. The volume slider has no
        // focusable rows, so ◀/▶ are captured here (a native key-listener on the
        // slider is the primary path; this is the fallback when nothing's
        // focused). List sheets let focus navigation drive the rows.
        if (_sheetMode == SheetMode.Volume)
        {
            switch (kc)
            {
                case Android.Views.Keycode.DpadLeft:  AdjustVolume(-0.05f); return true;
                case Android.Views.Keycode.DpadRight: AdjustVolume(+0.05f); return true;
                case Android.Views.Keycode.DpadCenter:
                case Android.Views.Keycode.Enter:     CloseSheet();          return true;
                case Android.Views.Keycode.Back:
                case Android.Views.Keycode.Escape:    return false;   // OnBack closes it
                default:                              return true;    // swallow the rest
            }
        }
        if (_sheetMode == SheetMode.List)
            return false;   // arrows move between rows, OK selects, Back → OnBack

        if (kc is Android.Views.Keycode.Back or Android.Views.Keycode.Escape)
            return false;   // let the activity handle Back

        if (!Hud.IsVisible)
        {
            RevealHud();
            return true;    // swallow the key that opened the menu
        }
        ArmAutoHide();      // keep it up while the user navigates
#endif
        return false;       // focus navigation drives the buttons
    }

    public static bool HandleGlobalBack() => Current?.OnBack() ?? false;
    private bool OnBack()
    {
        if (_sheetMode != SheetMode.None) { CloseSheet(); return true; }
        if (Hud.IsVisible) { HideHud(); return true; }
        return false;       // let navigation pop the page
    }

    private void RevealHud()
    {
        Hud.IsVisible = true;
        try { PlayPauseButton.Focus(); } catch { }
        ArmAutoHide();
        // Bridge the wide gap between the transport cluster and the settings
        // cluster with explicit focus links — D-pad focus search won't reliably
        // jump that far on its own, leaving VOL/AT/CC/HD/ⓘ unreachable.
        Dispatcher.Dispatch(EnsureFocusChain);
    }

    // Explicit D-pad focus links so the remote can cross from the left transport
    // row (+10 / Skip) to the right settings row (VOL) and back — a plain
    // horizontal gap that big isn't bridged by Android's geometric focus search.
    private void EnsureFocusChain()
    {
#if ANDROID
        if (_focusChainWired) return;
        _nvSeekFwd = SeekFwdButton.Handler?.PlatformView as Android.Views.View;
        _nvVol     = VolumeButton.Handler?.PlatformView as Android.Views.View;
        _nvSkip    = SkipButton.Handler?.PlatformView as Android.Views.View;
        if (_nvSeekFwd is null || _nvVol is null) return;   // handlers not ready yet — retry next reveal

        if (_nvSeekFwd.Id == Android.Views.View.NoId) _nvSeekFwd.Id = Android.Views.View.GenerateViewId();
        if (_nvVol.Id     == Android.Views.View.NoId) _nvVol.Id     = Android.Views.View.GenerateViewId();
        if (_nvSkip is not null && _nvSkip.Id == Android.Views.View.NoId)
            _nvSkip.Id = Android.Views.View.GenerateViewId();

        _nvVol.NextFocusLeftId = _nvSeekFwd.Id;
        if (_nvSkip is not null)
        {
            _nvSkip.NextFocusRightId = _nvVol.Id;
            _nvSkip.NextFocusLeftId  = _nvSeekFwd.Id;
        }
        UpdateSkipFocusLink();
        _focusChainWired = true;
#endif
    }

    // +10 goes right to the Skip button when it's showing, otherwise straight
    // across to VOL. Called from EnsureFocusChain and each tick as Skip toggles.
    private void UpdateSkipFocusLink()
    {
#if ANDROID
        if (_nvSeekFwd is null || _nvVol is null) return;
        bool skipVis = _nvSkip is not null && SkipButton.IsVisible;
        _nvSeekFwd.NextFocusRightId = skipVis ? _nvSkip!.Id : _nvVol.Id;
#endif
    }

    private void ArmAutoHide()
    {
        _hudTimer?.Stop();
        _hudTimer = Dispatcher.CreateTimer();
        _hudTimer.Interval = TimeSpan.FromSeconds(5);
        _hudTimer.IsRepeating = false;
        _hudTimer.Tick += (_, _) =>
        {
            // Auto-hide only while actually playing; keep it up when paused.
            if (NativePlayerService.Instance?.GetState()?.Playing == true)
                HideHud();
        };
        _hudTimer.Start();
    }

    private void HideHud() => Hud.IsVisible = false;

    private void AttachAndStart()
    {
        if (_started) return;
        _started = true;
        AttachSurface();
        _ = StartAsync();
        StartTimer();
    }

    private void AttachSurface()
    {
#if ANDROID
        if (_attached) return;
        if (RootGrid.Handler?.PlatformView is Android.Views.ViewGroup vg)
        {
            var m = vg.Resources?.DisplayMetrics;
            int w = m?.WidthPixels  ?? 1920;
            int h = m?.HeightPixels ?? 1080;
            var tv = new Android.Views.TextureView(vg.Context!)
            {
                LayoutParameters = new Android.Views.ViewGroup.LayoutParams(w, h),
            };
            vg.AddView(tv, 0);   // behind the XAML HUD

            // MAUI's layout only arranges its own cross-platform children, so a
            // raw native child stays 0×0 (video decodes but renders to nothing —
            // black screen, audio only). Force measure+layout to display size.
            _textureView = tv;
            LayoutNative(tv, w, h);
            RootGrid.SizeChanged += (_, _) => { if (_textureView is { } t) LayoutNative(t, w, h); };

            NativePlayerService.RegisterTextureView(tv);
            _attached = true;
        }
#endif
    }

#if ANDROID
    private Android.Views.TextureView? _textureView;

    // Native views for the explicit D-pad focus chain across the HUD's centre gap.
    private Android.Views.View? _nvSeekFwd, _nvVol, _nvSkip;
    private bool _focusChainWired;

    private static void LayoutNative(Android.Views.View v, int w, int h)
    {
        v.Measure(
            Android.Views.View.MeasureSpec.MakeMeasureSpec(w, Android.Views.MeasureSpecMode.Exactly),
            Android.Views.View.MeasureSpec.MakeMeasureSpec(h, Android.Views.MeasureSpecMode.Exactly));
        v.Layout(0, 0, w, h);
    }
#endif

    private Task StartAsync() => StartStreamAsync(_resumeMs, 0);

    /// <summary>True while the current stream is the /api/video progressive
    /// remux — a non-seekable ffmpeg pipe. SeekTo on it snaps to 0, so SeekBy
    /// restarts the stream at the target instead (ffmpeg's -output_ts_offset
    /// keeps the PTS absolute, so the timeline stays honest).</summary>
    private bool _isDirectStream;
    /// <summary>Set after a decoder failure — retry without nativeCaps so the
    /// server falls back to its transcode pipeline.</summary>
    private bool _forceTranscode;

    /// <summary>
    /// (Re)start the current file at <paramref name="resumeMs"/> (absolute time on
    /// the real runtime) with an optional height cap. Sends the device's decoder
    /// capabilities (nativeCaps) so the server Direct Plays anything ExoPlayer
    /// handles itself (MKV/AVI/MPEG4/AC3 …) instead of transcoding "for a
    /// browser". maxHeight &gt; 0 forces the HLS transcode path with a cap.
    /// </summary>
    private async Task StartStreamAsync(long resumeMs, int maxHeight)
    {
        try
        {
            _maxHeight = maxHeight;
            var seekSec = resumeMs / 1000;
            var url = $"/api/hls/start?path={Uri.EscapeDataString(_filePath)}" +
                      $"&seek={seekSec}&maxHeight={maxHeight}&clientHevc=1&clientHevc10=1";
            if (!_forceTranscode)
            {
                var caps = Services.TvCodecCaps.Get();
                if (!string.IsNullOrEmpty(caps))
                    url += $"&nativeCaps={Uri.EscapeDataString(caps)}";
            }
            var resp = await _http.PostAsync(url, null);
            StartResponse? body = null;
            if (resp.IsSuccessStatusCode)
                body = await resp.Content.ReadFromJsonAsync<StartResponse>(Json);

            _knownDurationMs = (long)((body?.TotalDuration ?? 0) * 1000);

            bool directPlay   = !string.IsNullOrEmpty(body?.DirectPlayUrl);
            bool directStream = !string.IsNullOrEmpty(body?.DirectStreamUrl) && !directPlay;
            var mediaUrl = Abs(body?.DirectPlayUrl ?? body?.DirectStreamUrl ?? body?.ManifestUrl);
            if (string.IsNullOrEmpty(mediaUrl))
            {
                mediaUrl = $"{ImageBase}/api/file?path={Uri.EscapeDataString(_filePath)}";
                directPlay = true;   // raw-file fallback → absolute timeline
            }
            _isDirectStream = directStream;

            if (directPlay)
            {
                // Range-seekable raw file — ExoPlayer owns an absolute timeline.
                _streamBaseMs = 0;
                NativePlayerService.Instance?.PlayAsync(mediaUrl, resumeMs);
            }
            else if (directStream)
            {
                // Progressive remux pipe: NOT seekable — bake the offset into the
                // request instead of SeekTo (which would snap to 0). The pipe's
                // PTS carry the absolute offset, so no base correction needed.
                if (seekSec > 0)
                    mediaUrl += (mediaUrl.Contains('?') ? "&" : "?") + $"seek={seekSec}";
                _streamBaseMs = 0;
                NativePlayerService.Instance?.PlayAsync(mediaUrl, 0);
            }
            else
            {
                // The HLS manifest is already seeked server-side → don't re-seek;
                // ExoPlayer position 0 == resumeMs on the real runtime.
                _streamBaseMs = resumeMs;
                NativePlayerService.Instance?.PlayAsync(mediaUrl, 0);
            }
            _ = LoadSegmentsAsync();
        }
        catch { }
    }

    // ── Episode navigation (⏮ / ⏭) ──────────────────────────────────────────
    private async Task<List<MediaFileDto>> GetEpisodesAsync()
    {
        if (_episodes is not null) return _episodes;
        try
        {
            var files = await _api.GetMediaFilesAsync(_mediaItemId);
            _episodes = files.Where(f => !string.IsNullOrEmpty(f.FilePath))
                             .OrderBy(f => f.Season ?? 0).ThenBy(f => f.Episode ?? 0)
                             .ToList();
        }
        catch { _episodes = new List<MediaFileDto>(); }
        return _episodes;
    }

    private async void GoAdjacentEpisode(int delta)
    {
        var eps = await GetEpisodesAsync();
        if (eps.Count <= 1) { FlashInfo("Других серий нет"); return; }
        int i = eps.FindIndex(f => string.Equals(f.FilePath, _filePath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = eps.FindIndex(f => f.Season == _season && f.Episode == _episode);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= eps.Count)
        {
            FlashInfo(delta > 0 ? "Это последняя серия" : "Это первая серия");
            return;
        }
        SwitchToEpisode(eps[j]);
    }

    private void SwitchToEpisode(MediaFileDto f)
    {
        _season = f.Season;
        _episode = f.Episode;
        _filePath = f.FilePath;
        // Fresh episode → back to original quality + absolute timeline, drop stale
        // skip segments (they're re-fetched for the new episode).
        _maxHeight = 0; _streamBaseMs = 0; _knownDurationMs = 0;
        _isDirectStream = false; _forceTranscode = false;   // per-file decisions
        _introStart = _introEnd = _creditsStart = _creditsEnd = null;
        _skipVisible = false; SkipButton.IsVisible = false;
        EyebrowLabel.Text = _episode is not null
            ? $"NOW PLAYING · S{_season ?? 1} · EP {_episode}"
            : "NOW PLAYING";
        FlashInfo(_episode is not null ? $"Серия {_episode}" : "Воспроизведение");
        _ = StartStreamAsync(0, 0);
    }

    private string? Abs(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
        return ImageBase + (url.StartsWith('/') ? url : "/" + url);
    }

    private async Task LoadSegmentsAsync()
    {
        if (_season is null || _episode is null) return;
        try
        {
            var seg = await _api.GetEpisodeSegmentsAsync(_mediaItemId, _season.Value, _episode.Value);
            if (seg is null) return;
            _introStart   = seg.IntroStart;
            _introEnd     = seg.IntroEnd;
            _creditsStart = seg.CreditsStart;
            _creditsEnd   = seg.CreditsEnd;
        }
        catch { }
    }

    private void StartTimer()
    {
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var st = NativePlayerService.Instance?.GetState();
        if (st is null) return;

        long dur    = _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs;
        long absPos = _streamBaseMs + st.PositionMs;
        _durationMs = dur;

        // Capability negotiation safety net: the device ADVERTISED a decoder
        // but it failed at runtime (rare vendor quirks) → retry once with
        // nativeCaps off, which sends the server down its transcode pipeline.
        if (!string.IsNullOrEmpty(st.ErrorMessage) && !_forceTranscode)
        {
            _forceTranscode = true;
            FlashInfo("Формат не пошёл — переключаюсь на транскод");
            _ = StartStreamAsync(absPos, _maxHeight);
            return;
        }

        PlayPauseIcon.Text  = st.Playing ? "❚❚" : "▶";
        BufferSpinner.IsVisible = st.Buffering;

        if (dur > 0)
        {
            Scrubber.Progress = Math.Clamp((double)absPos / dur, 0, 1);
            PositionLabel.Text = Fmt(absPos);
            DurationLabel.Text = Fmt(dur);
        }

        UpdateSkip(absPos / 1000.0);
        UpdateSkipFocusLink();

        if (st.Ended)
        {
            RecordProgress(absPos, dur);
            _ = Navigation.PopAsync();
            return;
        }

        if (++_ticks % 5 == 0 && absPos > 0)
            RecordProgress(absPos, dur);
    }

    private void UpdateSkip(double posSec)
    {
        if (_introStart is double is0 && _introEnd is double ie && posSec >= is0 && posSec < ie)
        {
            _skipTarget = ie; _skipVisible = true;
            SkipLabel.Text = "Пропустить заставку";
            SkipButton.IsVisible = true;
            return;
        }
        if (_creditsStart is double cs && posSec >= cs)
        {
            _skipTarget = _creditsEnd ?? (_durationMs / 1000.0);
            _skipVisible = true;
            SkipLabel.Text = "Пропустить титры";
            SkipButton.IsVisible = true;
            return;
        }
        _skipVisible = false;
        SkipButton.IsVisible = false;
    }

    private void DoSkip()
    {
        if (_skipTarget > 0)
        {
            long rel = (long)(_skipTarget * 1000) - _streamBaseMs;
            if (rel < 0) rel = 0;
            NativePlayerService.Instance?.SeekAsync(rel);
        }
        _skipVisible = false;
        SkipButton.IsVisible = false;
    }

    private void TogglePlay()
    {
        var st = NativePlayerService.Instance?.GetState();
        if (st is null) return;
        if (st.Playing) NativePlayerService.Instance?.PauseAsync();
        else            NativePlayerService.Instance?.ResumeAsync();
    }

    // ── Submenu sheets: volume slider + audio / subtitles / quality lists ────
    private enum SheetMode { None, Volume, List }
    private SheetMode _sheetMode = SheetMode.None;
    private View? _sheetOrigin;   // button to re-focus when the sheet closes

    private sealed record SheetRow(string Label, bool Selected, Action? OnSelect);

    // Volume ------------------------------------------------------------------
    private float _volume = 1f;

    private void OpenVolumeSheet()
    {
        _sheetOrigin = VolumeButton;
        _hudTimer?.Stop();
        SheetTitle.Text = "ГРОМКОСТЬ";
        VolumePanel.IsVisible = true;
        SheetItems.IsVisible = false;
        UpdateVolumeUi();
        _sheetMode = SheetMode.Volume;
        SheetOverlay.IsVisible = true;
        Dispatcher.Dispatch(() =>
        {
#if ANDROID
            WireVolumeKeys();
#endif
            FocusRow(VolumeSlider);
        });
    }

    private void AdjustVolume(float delta)
    {
        _volume = Math.Clamp(_volume + delta, 0f, 1f);
        NativePlayerService.Instance?.SetVolumeAsync(_volume);
        UpdateVolumeUi();
    }

    private void UpdateVolumeUi()
    {
        VolumeBar.Progress = _volume;
        VolumeValue.Text = _volume <= 0.001f ? "Выкл" : $"{(int)Math.Round(_volume * 100)}%";
    }

    // Track lists -------------------------------------------------------------
    private void OpenAudioSheet()
    {
        var tracks = NativePlayerService.Instance?.GetAudioTracks() ?? System.Array.Empty<TrackOption>();
        var rows = new List<SheetRow>();
        if (tracks.Count == 0)
            rows.Add(new SheetRow("Оригинал", true, null));
        else
            foreach (var t in tracks)
                rows.Add(new SheetRow(t.Label, t.Selected, () =>
                {
                    NativePlayerService.Instance?.SelectTrack(t.GroupIndex, t.TrackIndex);
                    FlashInfo($"Аудио: {t.Label}");
                }));
        OpenListSheet("АУДИОДОРОЖКА", rows, AudioButton);
    }

    private void OpenSubsSheet()
    {
        var tracks = NativePlayerService.Instance?.GetTextTracks() ?? System.Array.Empty<TrackOption>();
        var rows = new List<SheetRow>
        {
            new("Выключены", !tracks.Any(t => t.Selected), () =>
            {
                NativePlayerService.Instance?.DisableSubtitles();
                FlashInfo("Субтитры выключены");
            }),
        };
        foreach (var t in tracks)
            rows.Add(new SheetRow(t.Label, t.Selected, () =>
            {
                NativePlayerService.Instance?.SelectTrack(t.GroupIndex, t.TrackIndex);
                FlashInfo($"Субтитры: {t.Label}");
            }));
        OpenListSheet("СУБТИТРЫ", rows, SubsButton);
    }

    // Aspect ratio ------------------------------------------------------------
    private string _aspect = "default";

    private void OpenAspectSheet()
    {
        var rows = new List<SheetRow>
        {
            new("Авто (вписать)",        _aspect is "default" or "", () => ApplyAspect("default", "Авто")),
            new("Растянуть на экран",    _aspect == "stretch",       () => ApplyAspect("stretch", "Растянуть")),
            new("Заполнить (обрезать)",  _aspect == "zoom",          () => ApplyAspect("zoom", "Заполнить")),
            new("16:9",                  _aspect == "16:9",          () => ApplyAspect("16:9", "16:9")),
            new("4:3",                   _aspect == "4:3",           () => ApplyAspect("4:3", "4:3")),
        };
        OpenListSheet("СООТНОШЕНИЕ СТОРОН", rows, AspectButton);
    }

    private void ApplyAspect(string value, string label)
    {
        _aspect = value;
        Preferences.Default.Set("player_aspect", value);
        _ = NativePlayerService.Instance?.SetAspectRatioAsync(value);
        FlashInfo($"Формат: {label}");
    }

    private void OpenQualitySheet()
    {
        // Quality on TV mirrors the web: re-request the stream with a height cap
        // (maxHeight) so the server transcodes/downscales. The source is a single
        // video track, so there's nothing to pick from ExoPlayer directly — the
        // choices are downscale targets below the source resolution.
        var st = NativePlayerService.Instance?.GetState();
        int srcH = st?.ActualHeight ?? 0;
        if (srcH <= 0) srcH = 1080;

        var rows = new List<SheetRow>
        {
            new(srcH > 0 ? $"Оригинал · {srcH}p" : "Оригинал", _maxHeight == 0, () => ApplyQuality(0)),
        };
        foreach (var h in new[] { 1440, 1080, 720, 480 })
        {
            if (h >= srcH) continue;   // only offer caps below the source
            int cap = h;
            rows.Add(new SheetRow($"{h}p", _maxHeight == h, () => ApplyQuality(cap)));
        }
        OpenListSheet("КАЧЕСТВО", rows, QualityButton);
    }

    private void ApplyQuality(int maxHeight)
    {
        if (maxHeight == _maxHeight) { FlashInfo("Уже выбрано"); return; }
        var st = NativePlayerService.Instance?.GetState();
        long absPos = _streamBaseMs + (st?.PositionMs ?? 0);
        FlashInfo(maxHeight == 0 ? "Качество: оригинал" : $"Качество: {maxHeight}p");
        _ = StartStreamAsync(absPos, maxHeight);
    }

    private void OpenListSheet(string title, List<SheetRow> rows, View origin)
    {
        _sheetOrigin = origin;
        _hudTimer?.Stop();
        SheetTitle.Text = title;
        VolumePanel.IsVisible = false;
        SheetItems.Children.Clear();

        View? focusRow = null;
        foreach (var r in rows)
        {
            var row = BuildSheetRow(r);
            SheetItems.Children.Add(row);
            if (r.Selected && focusRow is null) focusRow = row;
        }
        focusRow ??= SheetItems.Children.Count > 0 ? SheetItems.Children[0] as View : null;

        SheetItems.IsVisible = true;
        _sheetMode = SheetMode.List;
        SheetOverlay.IsVisible = true;

        if (focusRow is not null)
            Dispatcher.Dispatch(() => FocusRow(focusRow));
    }

    private View BuildSheetRow(SheetRow r)
    {
        var label = new Label
        {
            Text = (r.Selected ? "●  " : "      ") + r.Label,
            TextColor = r.Selected ? Color.FromArgb("#e8a33d") : Colors.White,
            FontFamily = "Geist",
            FontSize = 15,
            VerticalOptions = LayoutOptions.Center,
        };
        var border = new Border
        {
            BackgroundColor = Colors.Transparent,
            StrokeThickness = 0,
            Padding = new Thickness(16, 10),
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = label,
        };
        var beh = new TvFocusBehavior { Radius = 8, FillOnFocus = true };
        beh.Command = new Command(() =>
        {
            r.OnSelect?.Invoke();
            CloseSheet();
        });
        border.Behaviors.Add(beh);
        return border;
    }

    private void CloseSheet()
    {
        SheetOverlay.IsVisible = false;
        SheetItems.Children.Clear();
        _sheetMode = SheetMode.None;
        var origin = _sheetOrigin;
        _sheetOrigin = null;
        if (origin is not null)
            Dispatcher.Dispatch(() => { try { origin.Focus(); } catch { } });
        ArmAutoHide();
    }

    private static void FocusRow(View v)
    {
#if ANDROID
        if (v.Handler?.PlatformView is Android.Views.View nv)
        {
            nv.Focusable = true;
            nv.FocusableInTouchMode = false;
            nv.RequestFocus();
        }
#endif
    }

#if ANDROID
    // Native key-listener on the volume slider: ◀/▶ change the level in place
    // (a focused view consumes arrows before Activity.OnKeyDown, so HandleKey
    // alone wouldn't see them). OK/Back fall through to the click / activity.
    private bool _volKeysWired;
    private void WireVolumeKeys()
    {
        if (_volKeysWired) return;
        if (VolumeSlider.Handler?.PlatformView is Android.Views.View nv)
        {
            nv.KeyPress += (_, e) =>
            {
                if (e.Event?.Action != Android.Views.KeyEventActions.Down) { e.Handled = false; return; }
                switch (e.KeyCode)
                {
                    case Android.Views.Keycode.DpadLeft:  AdjustVolume(-0.05f); e.Handled = true; break;
                    case Android.Views.Keycode.DpadRight: AdjustVolume(+0.05f); e.Handled = true; break;
                    default: e.Handled = false; break;   // OK → click (close); Back → activity
                }
            };
            _volKeysWired = true;
        }
    }
#endif

    private void ShowInfo()
    {
        var st = NativePlayerService.Instance?.GetState();
        if (st is null) { FlashInfo("Нет данных"); return; }
        var codec = string.IsNullOrEmpty(st.ActualCodec) ? "—" : st.ActualCodec.ToUpperInvariant();
        FlashInfo($"{codec} · {st.ActualWidth}×{st.ActualHeight} · {st.ActualBitDepth}-bit");
    }

    private IDispatcherTimer? _infoTimer;
    private void FlashInfo(string text)
    {
        InfoLine.Text = text;
        InfoLine.IsVisible = true;
        _infoTimer?.Stop();
        _infoTimer = Dispatcher.CreateTimer();
        _infoTimer.Interval = TimeSpan.FromSeconds(2.5);
        _infoTimer.IsRepeating = false;
        _infoTimer.Tick += (_, _) => InfoLine.IsVisible = false;
        _infoTimer.Start();
    }

    private void SeekBy(long deltaMs)
    {
        var st = NativePlayerService.Instance?.GetState();
        if (st is null) return;
        long dur = _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs;
        long absPos = _streamBaseMs + st.PositionMs;
        long target = Math.Clamp(absPos + deltaMs, 0, dur > 0 ? dur : long.MaxValue);
        // The /api/video remux pipe is NOT seekable — ExoPlayer.SeekTo snaps it
        // to 0 (the tester's "+10 resets to start"). Re-request the stream at
        // the target instead; PTS stay absolute via -output_ts_offset.
        if (_isDirectStream)
        {
            _ = StartStreamAsync(target, _maxHeight);
            return;
        }
        // Back to the stream's own timeline: 0 for Direct Play; a seeked HLS
        // transcode can't rewind before its base, so clamp there.
        long rel = target - _streamBaseMs;
        if (rel < 0) rel = 0;
        NativePlayerService.Instance?.SeekAsync(rel);
    }

    private void RecordProgress(long positionMs, long durationMs)
    {
        if (_mediaItemId == Guid.Empty || positionMs <= 0) return;
        try
        {
            _ = _api.RecordProgressAsync(new Animarr.Shared.Requests.RecordProgressRequest(
                _mediaItemId, _season, _episode, _filePath, positionMs,
                durationMs > 0 ? durationMs : null));
        }
        catch { }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (Current == this) Current = null;
        _timer?.Stop();
        _hudTimer?.Stop();
        var st = NativePlayerService.Instance?.GetState();
        if (st is not null)
            RecordProgress(_streamBaseMs + st.PositionMs,
                           _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs);
        _ = NativePlayerService.Instance?.DetachAsync();
    }

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private sealed record StartResponse(
        string? DirectPlayUrl, string? DirectStreamUrl, string? ManifestUrl,
        double? TotalDuration, double? ResumeSec, string? Token);
}
