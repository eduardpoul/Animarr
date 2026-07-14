using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Input;
using Animarr.App.Services;
using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Shared.Requests;
using Animarr.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Animarr.App;

/// <summary>
/// Full-screen native player. Hosts ExoPlayer (via NativePlayerService) in a
/// SurfaceView added to <c>VideoHost</c>, with a XAML HUD overlaid on top — the
/// hole-punch surface sits behind the MAUI content so the transport controls
/// stay visible. Playback URL comes from POST /api/hls/start (server picks
/// Direct Play / Direct Stream / HLS); we tell it the client can decode HEVC so
/// it stream-copies rather than transcodes on the weak TV box. Resume position
/// is seeked on start and progress is reported to /api/watch-states every 5s.
/// </summary>
public partial class PlayerPage : ContentPage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
    private int  _ticks;
    private long _durationMs;
    private bool _started;
    private bool _attached;

    // Skip segments (seconds), loaded lazily.
    private double? _introStart, _introEnd, _creditsStart, _creditsEnd;
    private double  _skipTarget;

    public Command BackCommand      { get; }
    public Command PlayPauseCommand { get; }
    public Command SeekBackCommand  { get; }
    public Command SeekFwdCommand   { get; }
    public Command SkipCommand      { get; }

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

        BackCommand      = new Command(async () => await Navigation.PopAsync());
        PlayPauseCommand = new Command(TogglePlay);
        SeekBackCommand  = new Command(() => SeekBy(-10_000));
        SeekFwdCommand   = new Command(() => SeekBy(+10_000));
        SkipCommand      = new Command(DoSkip);

        VideoHost.Loaded += (_, _) => AttachAndStart();
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
        if (VideoHost.Handler?.PlatformView is Android.Views.ViewGroup vg)
        {
            var sv = new Android.Views.SurfaceView(vg.Context!)
            {
                LayoutParameters = new Android.Views.ViewGroup.LayoutParams(
                    Android.Views.ViewGroup.LayoutParams.MatchParent,
                    Android.Views.ViewGroup.LayoutParams.MatchParent),
            };
            vg.AddView(sv);
            NativePlayerService.RegisterSurfaceView(sv);
            _attached = true;
        }
#endif
    }

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
            {
                // Fall back to a raw file stream so playback still starts.
                mediaUrl = $"{ImageBase}/api/file?path={Uri.EscapeDataString(_filePath)}";
            }

            NativePlayerService.Instance?.PlayAsync(mediaUrl, _resumeMs);
            _ = LoadSegmentsAsync();
        }
        catch { /* leave HUD; user can back out */ }
    }

    // Resolve a possibly-relative server URL (e.g. "/api/file?…") to absolute.
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
        PlayPauseLabel.Text = st.Playing ? "⏸" : "▶";

        if (st.DurationMs > 0)
        {
            Scrubber.Progress = Math.Clamp((double)st.PositionMs / st.DurationMs, 0, 1);
            PositionLabel.Text = Fmt(st.PositionMs);
            DurationLabel.Text = Fmt(st.DurationMs);
        }

        UpdateSkip(st.PositionMs / 1000.0);

        // End of file → leave the player.
        if (st.Ended)
        {
            RecordProgress(st.PositionMs, st.DurationMs);
            _ = Navigation.PopAsync();
            return;
        }

        // Report progress every ~5s.
        if (++_ticks % 5 == 0 && st.PositionMs > 0)
            RecordProgress(st.PositionMs, st.DurationMs);
    }

    private void UpdateSkip(double posSec)
    {
        // Inside intro?
        if (_introStart is double is0 && _introEnd is double ie && posSec >= is0 && posSec < ie)
        {
            _skipTarget = ie;
            SkipLabel.Text = "Пропустить заставку";
            SkipButton.IsVisible = true;
            return;
        }
        // Inside credits?
        if (_creditsStart is double cs && posSec >= cs)
        {
            _skipTarget = _creditsEnd ?? (_durationMs / 1000.0);
            SkipLabel.Text = "Пропустить титры";
            SkipButton.IsVisible = true;
            return;
        }
        SkipButton.IsVisible = false;
    }

    private void DoSkip()
    {
        if (_skipTarget > 0)
            NativePlayerService.Instance?.SeekAsync((long)(_skipTarget * 1000));
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
            _ = _api.RecordProgressAsync(new RecordProgressRequest(
                _mediaItemId, _season, _episode, _filePath, positionMs,
                durationMs > 0 ? durationMs : null));
        }
        catch { }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
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
