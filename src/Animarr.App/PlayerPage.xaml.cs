using System.Net.Http.Json;
using System.Text.Json;
using Animarr.App.Services;
using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly int?   _season;
    private readonly int?   _episode;
    private readonly string _filePath;
    private readonly long   _resumeMs;

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
        PrevFocus.Command      = new Command(() => FlashInfo("Предыдущая серия — скоро"));
        PlayPauseFocus.Command = new Command(TogglePlay);
        NextFocus.Command      = new Command(() => FlashInfo("Следующая серия — скоро"));
        SeekFwdFocus.Command   = new Command(() => SeekBy(+10_000));
        SkipFocus.Command      = new Command(DoSkip);
        VolumeFocus.Command    = new Command(CycleVolume);
        AudioFocus.Command     = new Command(() => FlashInfo("Выбор аудиодорожки — скоро"));
        SubsFocus.Command      = new Command(() => FlashInfo("Субтитры — скоро"));
        QualityFocus.Command   = new Command(() => FlashInfo("Качество — скоро"));
        InfoFocus.Command      = new Command(ShowInfo);

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
        if (Hud.IsVisible) { HideHud(); return true; }
        return false;       // let navigation pop the page
    }

    private void RevealHud()
    {
        Hud.IsVisible = true;
        try { PlayPauseButton.Focus(); } catch { }
        ArmAutoHide();
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
    private static void LayoutNative(Android.Views.View v, int w, int h)
    {
        v.Measure(
            Android.Views.View.MeasureSpec.MakeMeasureSpec(w, Android.Views.MeasureSpecMode.Exactly),
            Android.Views.View.MeasureSpec.MakeMeasureSpec(h, Android.Views.MeasureSpecMode.Exactly));
        v.Layout(0, 0, w, h);
    }
#endif

    private async Task StartAsync()
    {
        try
        {
            var seekSec = _resumeMs / 1000;
            var url = $"/api/hls/start?path={Uri.EscapeDataString(_filePath)}" +
                      $"&seek={seekSec}&clientHevc=1&clientHevc10=1";
            var resp = await _http.PostAsync(url, null);
            StartResponse? body = null;
            if (resp.IsSuccessStatusCode)
                body = await resp.Content.ReadFromJsonAsync<StartResponse>(Json);

            var mediaUrl = Abs(body?.DirectPlayUrl ?? body?.DirectStreamUrl ?? body?.ManifestUrl);
            if (string.IsNullOrEmpty(mediaUrl))
                mediaUrl = $"{ImageBase}/api/file?path={Uri.EscapeDataString(_filePath)}";

            NativePlayerService.Instance?.PlayAsync(mediaUrl, _resumeMs);
            _ = LoadSegmentsAsync();
        }
        catch { }
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

        _durationMs = st.DurationMs;
        PlayPauseIcon.Text  = st.Playing ? "❚❚" : "▶";
        BufferSpinner.IsVisible = st.Buffering;

        if (st.DurationMs > 0)
        {
            Scrubber.Progress = Math.Clamp((double)st.PositionMs / st.DurationMs, 0, 1);
            PositionLabel.Text = Fmt(st.PositionMs);
            DurationLabel.Text = Fmt(st.DurationMs);
        }

        UpdateSkip(st.PositionMs / 1000.0);

        if (st.Ended)
        {
            RecordProgress(st.PositionMs, st.DurationMs);
            _ = Navigation.PopAsync();
            return;
        }

        if (++_ticks % 5 == 0 && st.PositionMs > 0)
            RecordProgress(st.PositionMs, st.DurationMs);
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
            NativePlayerService.Instance?.SeekAsync((long)(_skipTarget * 1000));
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

    private float _volume = 1f;
    private void CycleVolume()
    {
        _volume = _volume <= 0.01f ? 1f : Math.Max(0f, _volume - 0.25f);
        NativePlayerService.Instance?.SetVolumeAsync(_volume);
        FlashInfo(_volume <= 0.01f ? "Звук выключен" : $"Громкость {(int)(_volume * 100)}%");
    }

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
        var target = Math.Clamp(st.PositionMs + deltaMs, 0, st.DurationMs > 0 ? st.DurationMs : long.MaxValue);
        NativePlayerService.Instance?.SeekAsync(target);
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
        if (st is not null) RecordProgress(st.PositionMs, st.DurationMs);
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
