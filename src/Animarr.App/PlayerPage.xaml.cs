using System.Net.Http.Json;
using System.Text.Json;
using Animarr.App.Services;
using Animarr.Shared;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Full-screen native player. ExoPlayer (via NativePlayerService) renders into a
/// TextureView added to the root grid; the XAML HUD sits on top. Controlled the
/// way a TV player should be — straight off the D-pad, not by focusing on-screen
/// buttons: OK = play/pause, ◀/▶ = seek ±10s, ▲ = skip intro/credits. The HUD
/// auto-hides during playback and stays up while paused. Keys are delivered from
/// MainActivity.OnKeyDown via <see cref="HandleKey"/>.
///
/// Playback URL comes from POST /api/hls/start (server picks Direct Play /
/// Direct Stream / HLS; we advertise HEVC so it stream-copies on the weak TV
/// box). Resume is seeked on start; progress is reported to /api/watch-states.
/// </summary>
public partial class PlayerPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The player currently on screen — MainActivity routes D-pad keys
    /// here so OK/seek work without focusing a button.</summary>
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

        RootGrid.Loaded += (_, _) => AttachAndStart();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Current = this;
        ShowHud();
    }

    // ── D-pad control (delivered from MainActivity.OnKeyDown) ────────────────
    public bool HandleKey(int keyCode)
    {
#if ANDROID
        switch ((Android.Views.Keycode)keyCode)
        {
            case Android.Views.Keycode.DpadCenter:
            case Android.Views.Keycode.Enter:
            case Android.Views.Keycode.NumpadEnter:
            case Android.Views.Keycode.ButtonA:
            case Android.Views.Keycode.MediaPlayPause:
                TogglePlay(); ShowHud(); return true;

            case Android.Views.Keycode.DpadLeft:
            case Android.Views.Keycode.MediaRewind:
                SeekBy(-10_000); ShowHud(); return true;

            case Android.Views.Keycode.DpadRight:
            case Android.Views.Keycode.MediaFastForward:
                SeekBy(+10_000); ShowHud(); return true;

            case Android.Views.Keycode.DpadUp:
                if (_skipVisible) DoSkip(); else ShowHud();
                return true;

            case Android.Views.Keycode.DpadDown:
                ShowHud(); return true;

            case Android.Views.Keycode.MediaPlay:
                NativePlayerService.Instance?.ResumeAsync(); ShowHud(); return true;
            case Android.Views.Keycode.MediaPause:
                NativePlayerService.Instance?.PauseAsync(); ShowHud(); return true;
        }
#endif
        return false;
    }

    // ── HUD show / auto-hide ────────────────────────────────────────────────
    private void ShowHud()
    {
        Hud.IsVisible = true;
        _hudTimer?.Stop();
        _hudTimer = Dispatcher.CreateTimer();
        _hudTimer.Interval = TimeSpan.FromSeconds(3.5);
        _hudTimer.IsRepeating = false;
        _hudTimer.Tick += (_, _) =>
        {
            // Hide only while actually playing — keep it up when paused.
            if (NativePlayerService.Instance?.GetState()?.Playing == true)
                Hud.IsVisible = false;
        };
        _hudTimer.Start();
    }

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
            // raw native child never gets measured/laid-out and stays 0×0 — the
            // video decodes but renders to nothing (black screen, audio only).
            // Force an explicit measure+layout to the display size, re-doing it
            // whenever MAUI re-arranges (which would otherwise reset it).
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
        catch { /* leave HUD; user can back out */ }
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
        CenterIcon.Text     = st.Playing ? "❚❚" : "▶";
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
