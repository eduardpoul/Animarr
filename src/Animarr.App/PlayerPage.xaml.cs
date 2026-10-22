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
/// SurfaceView added to the root grid; the XAML HUD sits on top. Two control
/// modes, like a web/YouTube-TV player:
///
///   • HUD hidden (video playing): ◀/▶ seek ∓/±10 s (the target shows on
///     screen; held presses are coalesced), any other key reveals the HUD.
///     With the Up Next card on screen, OK starts the next episode and Back
///     hides the card.
///   • HUD shown: focus lands on the on-screen buttons; arrows move focus
///     between them and OK activates the focused one (normal D-pad nav). The
///     HUD auto-hides after a few seconds of no input while playing.
///   • Media keys (play/pause, ⏩/⏪, ⏭/⏮, stop) act directly in either mode,
///     and a MediaSession takes the same commands from the system (remotes
///     routed to the session, Google Assistant).
///
/// Keys are delivered from MainActivity.OnKeyDown via <see cref="HandleKey"/>,
/// which only intercepts media keys and keys while the HUD is hidden; once
/// it's up, keys fall through to MAUI focus navigation.
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
    private readonly string _title;
    /// <summary>The file the CURRENT stream plays. Differs from _filePath while
    /// an episode switch waits for its start: the old stream's position and
    /// end must not be saved against (or advance from) the new episode.</summary>
    private string? _streamFilePath;

    // Playback timeline. All three tiers expose ExoPlayer positions on the file's
    // absolute runtime — Direct Play natively, the Direct Stream remux via absolute
    // fragment times, HLS via a full-length playlist — so _streamBaseMs is 0
    // everywhere today; it stays as the hook for a relative stream.
    // _knownDurationMs is the full runtime from the start response.
    private int  _maxHeight;
    // Source height from an uncapped start response (a downscaled re-encode
    // reports its OUTPUT size there). The quality sheet labels "Оригинал" with
    // it: the decoded height is 0 when the decoder refused the source (4K on a
    // 1080p-only device) and the capped height after a quality switch.
    private int  _sourceHeight;
    private long _streamBaseMs;
    private long _knownDurationMs;
    private List<MediaFileDto>? _episodes;

    private IDispatcherTimer? _timer;

    // Server HLS session of the current stream (null on Direct Play / Stream).
    // The server reaps sessions idle for 5 min; ExoPlayer stops fetching while
    // paused on a full buffer, so — like the web player — ping it, and delete
    // it when the stream is replaced or the page closes (else the old transcode
    // ran on for 5 minutes after every episode switch).
    private string?  _hlsToken;
    private DateTime _lastKeepaliveUtc;
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
        _title = title ?? "";

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
        UpNextPlayFocus.Command = new Command(TriggerUpNext);
        UpNextSkipFocus.Command = new Command(SkipCredits);
        UpNextHideFocus.Command = new Command(DismissUpNext);

        // Re-apply the remembered aspect mode ("default" = fit/letterbox) —
        // the raw TextureView stretches non-16:9 sources without it.
        _aspect = Preferences.Default.Get("player_aspect", "default");
        _ = NativePlayerService.Instance?.SetAspectRatioAsync(_aspect);

        RootGrid.Loaded += (_, _) => AttachAndStart();

        if (NativePlayerService.Instance is { } nps) nps.TracksChanged += OnPlayerTracksChanged;
        // Subtitles move up while the HUD's bottom bar is on screen.
        Hud.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsVisible)) NativePlayerService.SetSubtitlesRaised(Hud.IsVisible);
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Current = this;
        RevealHud();  // show the HUD + focus the play button on entry
    }

    // ── Remote control ──────────────────────────────────────────────────────
    // Media keys → act directly, whatever is on screen.
    // HUD hidden → ◀/▶ seek, OK on the lone Up Next card plays the next
    // episode, any other key opens the HUD and focus lands on the buttons.
    // HUD shown → keys fall through to normal MAUI focus navigation (arrows move
    // between buttons, OK activates). Back hides the HUD (handled via OnBack).
    public bool HandleKey(int keyCode)
    {
#if ANDROID
        var kc = (Android.Views.Keycode)keyCode;

        // Transport keys never move focus: handled in every state, a sheet
        // included (play/pause over the audio list is still play/pause).
        if (HandleMediaKey(kc)) return true;

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
        {
            // Arrows move between rows, OK selects, Back → OnBack. A key only
            // gets here when no row took it, and Android's focus search would
            // then carry focus out of the sheet onto the HUD buttons hidden
            // under the overlay: ▲ on the first row closes the sheet (focus back
            // on its HUD button), ▼ on the last row and ◀/▶ stay put.
            int n = SheetItems.Children.Count;
            switch (kc)
            {
                case Android.Views.Keycode.DpadUp when n == 0 || IsRowFocused(0):
                    CloseSheet();
                    return true;
                case Android.Views.Keycode.DpadDown when n == 0 || IsRowFocused(n - 1):
                case Android.Views.Keycode.DpadLeft:
                case Android.Views.Keycode.DpadRight:
                    return true;
            }
            return false;
        }

        if (kc is Android.Views.Keycode.Back or Android.Views.Keycode.Escape)
            return false;   // let the activity handle Back

        if (!Hud.IsVisible)
        {
            switch (kc)
            {
                // ◀ / ▶ seek straight away (they used to only open the HUD);
                // a held key repeats and the repeats are coalesced.
                case Android.Views.Keycode.DpadLeft:  SeekBy(-10_000, feedback: true); return true;
                case Android.Views.Keycode.DpadRight: SeekBy(+10_000, feedback: true); return true;
                case Android.Views.Keycode.DpadCenter or Android.Views.Keycode.Enter
                    or Android.Views.Keycode.NumpadEnter when UpNextCard.IsVisible:
                    TriggerUpNext();
                    return true;
            }
            RevealHud();
            return true;    // swallow the key that opened the menu
        }
        ArmAutoHide();      // keep it up while the user navigates
#endif
        return false;       // focus navigation drives the buttons
    }

#if ANDROID
    private bool HandleMediaKey(Android.Views.Keycode kc)
    {
        switch (kc)
        {
            case Android.Views.Keycode.MediaPlayPause:
            case Android.Views.Keycode.Headsethook:      TogglePlay();                     return true;
            case Android.Views.Keycode.MediaPlay:        SetPlaying(true);                 return true;
            case Android.Views.Keycode.MediaPause:       SetPlaying(false);                return true;
            case Android.Views.Keycode.MediaStop:        Leave();                          return true;
            case Android.Views.Keycode.MediaFastForward:
            case Android.Views.Keycode.MediaSkipForward:
            case Android.Views.Keycode.MediaStepForward: SeekBy(+10_000, feedback: true);  return true;
            case Android.Views.Keycode.MediaRewind:
            case Android.Views.Keycode.MediaSkipBackward:
            case Android.Views.Keycode.MediaStepBackward: SeekBy(-10_000, feedback: true); return true;
            case Android.Views.Keycode.MediaNext:        GoAdjacentEpisode(+1);            return true;
            case Android.Views.Keycode.MediaPrevious:    GoAdjacentEpisode(-1);            return true;
        }
        return false;
    }
#endif

    /// <summary>Leave the player (media Stop / the system's stop command).</summary>
    private void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        _ = Navigation.PopAsync();
    }

    public static bool HandleGlobalBack() => Current?.OnBack() ?? false;

    // Android 13+ delivers Back through MAUI, not Activity.OnBackPressed:
    // close the submenu / HUD first, only then leave the player.
    private bool _leaving;
    protected override bool OnBackButtonPressed()
    {
        if (_leaving) return true;            // 2nd Back during the pop animation
        if (OnBack()) return true;
        _leaving = true;
        return base.OnBackButtonPressed();
    }
    private bool OnBack()
    {
        if (_sheetMode != SheetMode.None) { CloseSheet(); return true; }
        if (Hud.IsVisible) { HideHud(); return true; }
        if (UpNextCard.IsVisible) { DismissUpNext(); return true; }
        return false;       // let navigation pop the page
    }

    private void RevealHud()
    {
        Hud.IsVisible = true;
        // With Up Next armed the HUD brings its card along; focus goes to
        // "Смотреть", so OK means the same thing as on the lone card.
        UpdateUpNextVisibility();
        if (UpNextCard.IsVisible) FocusRow(UpNextPlayButton);
        else try { PlayPauseButton.Focus(); } catch { }
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

    private void HideHud()
    {
        Hud.IsVisible = false;
        UpdateUpNextVisibility();
    }

    private void AttachAndStart()
    {
        if (_started) return;
        _started = true;
        AttachSurface();
        StartMediaSession();
        _ = StartAsync();
        _ = ResolveAdjacentAsync();
        StartTimer();
    }

#if ANDROID
    private Services.TvMediaSession? _session;
#endif
    /// <summary>Playback speed (1 = normal), reported to the MediaSession.</summary>
    private float _speed = 1f;

    /// <summary>System-side control (session-routed media keys, Google
    /// Assistant): the same commands as the remote, through the same paths.</summary>
    private void StartMediaSession()
    {
#if ANDROID
        try
        {
            var ctx = Platform.CurrentActivity ?? Platform.AppContext;
            _session = new Services.TvMediaSession(ctx, new Services.TvMediaSession.Commands(
                Play: () => SetPlaying(true),
                Pause: () => SetPlaying(false),
                SeekTo: SeekToAbs,
                SeekBy: d => SeekBy(d, feedback: true),
                Next: () => GoAdjacentEpisode(+1),
                Previous: () => GoAdjacentEpisode(-1),
                Stop: Leave));
        }
        catch (Exception ex) { Log($"media session: {ex.Message}"); }
#endif
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
            // SurfaceView by default: video on its own compositor layer (no UI
            // redraw per video frame, 4K plane, HDR/DV, frame-rate switching).
            // The old TextureView path stays behind the bench flag "+texture".
            if (!Bench.BenchLog.Has("texture"))
            {
                var sv = new Android.Views.SurfaceView(vg.Context!) { LayoutParameters = new Android.Views.ViewGroup.LayoutParams(w, h) };
                vg.AddView(sv, 0);
                LayoutNative(sv, w, h);
                NativePlayerService.RegisterSurfaceView(sv);
                AttachSubtitleView(vg, w, h);
                RootGrid.SizeChanged += (_, _) => NativePlayerService.Instance?.ApplyAspectMatrix();
                _attached = true;
                return;
            }
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
            AttachSubtitleView(vg, w, h);
            _attached = true;
        }
#endif
    }

#if ANDROID
    /// <summary>Media3 SubtitleView right above the video surface (index 1),
    /// below the XAML HUD; full screen, so subtitles of a letterboxed film sit
    /// low on the screen rather than over the picture. Raw native child, so
    /// laid out by hand like the surface.</summary>
    private void AttachSubtitleView(Android.Views.ViewGroup vg, int w, int h)
    {
        var subs = new AndroidX.Media3.UI.SubtitleView(vg.Context!)
        {
            LayoutParameters = new Android.Views.ViewGroup.LayoutParams(w, h),
        };
        vg.AddView(subs, 1);
        LayoutNative(subs, w, h);
        NativePlayerService.RegisterSubtitleView(subs);
        NativePlayerService.SetSubtitlesRaised(Hud.IsVisible);
        RootGrid.SizeChanged += (_, _) => { LayoutNative(subs, w, h); NativePlayerService.RelayoutSubtitles(); };
    }
#endif

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

    private Task StartAsync() => StartStreamAsync(_resumeMs, 0, playWhenReady: true);

    private enum StreamKind { None, DirectPlay, DirectStream, Hls }
    /// <summary>Tier of the stream ExoPlayer is playing (None before the first
    /// successful start).</summary>
    private StreamKind _kind;
    /// <summary>Starts are serialised: each one takes a generation number and a
    /// newer start cancels the older one's request (the server stops an
    /// abandoned start's ffmpeg) and discards its late response — rapid quality
    /// / track / episode switches used to race, and the last RESPONSE (not the
    /// last choice) won.</summary>
    private int _startGen;
    private CancellationTokenSource? _startCts;
    private bool _closed;
    /// <summary>A start failed with nothing (alive) left playing: OK / play
    /// retries it.</summary>
    private (long ResumeMs, int MaxHeight)? _retryStart;
    /// <summary>Tokenised URL of the current Direct Play file / HLS manifest —
    /// re-prepared in place when a side-loaded subtitle changes.</summary>
    private string? _streamUrl;

    /// <summary>True while the current stream is the /api/video progressive
    /// remux — a non-seekable ffmpeg pipe. SeekTo on it snaps to 0, so SeekBy
    /// restarts the stream at the target instead (ffmpeg's -output_ts_offset
    /// keeps the PTS absolute, so the timeline stays honest).</summary>
    private bool _isDirectStream;
    /// <summary>Set after a decoder failure — retry without nativeCaps so the
    /// server falls back to its transcode pipeline.</summary>
    private bool _forceTranscode;
    /// <summary>Last rung after <see cref="_forceTranscode"/>: the no-caps retry
    /// still copies the original bitstream, which fails the same way when the
    /// decoder can't take its size/level — so cap to a 1080p H.264 re-encode.</summary>
    private bool _capFallback;
    /// <summary>A recovery restart is in flight: the failed player keeps
    /// reporting its error until the posted DoPlay re-prepares it, and that
    /// stale error must not trigger the next rung. Cleared on the first clean
    /// state, or after 8 s in case the new stream failed between two ticks.</summary>
    private DateTime? _recoveringSinceUtc;

    /// <summary>
    /// (Re)start the current file at <paramref name="resumeMs"/> (absolute time on
    /// the real runtime) with an optional height cap. Sends the device's decoder
    /// capabilities (nativeCaps) so the server Direct Plays anything ExoPlayer
    /// handles itself (MKV/AVI/MPEG4/AC3 …) instead of transcoding "for a
    /// browser". maxHeight &gt; 0 forces the HLS transcode path with a cap.
    /// <para>
    /// The current stream is only replaced once the server answered with a
    /// playable URL: a failed start (HTTP 500 from /api/hls/start, network) is
    /// retried once, then either leaves the previous stream playing with the
    /// previous settings (a quality / track switch is rolled back) or — when
    /// nothing is left to play — shows the error and waits for OK to retry. It
    /// never falls back to the raw /api/file silently: that played the original
    /// under a "720p" label and dropped the selected tracks.
    /// </para>
    /// <paramref name="playWhenReady"/> null keeps the current play/pause state;
    /// <paramref name="onFail"/> undoes a track choice made for this restart
    /// when it fails and the previous stream keeps playing.
    /// </summary>
    private async Task StartStreamAsync(long resumeMs, int maxHeight, bool? playWhenReady = null,
                                        Action? onFail = null)
    {
        int gen = ++_startGen;
        _startCts?.Cancel();
        var cts = _startCts = new CancellationTokenSource();
        var ct = cts.Token;
        // Track catalog (server probe + sidecar files) in parallel with the start.
        var catalog = EnsureCatalogAsync();
        // A restart (quality / track switch, remux seek, fallback rung) keeps a
        // pause: it used to force PlayWhenReady=true.
        bool play = playWhenReady
                    ?? (_kind == StreamKind.None || (NativePlayerService.Instance?.GetState()?.PlayWhenReady ?? true));
        _retryStart = null;
        string? failure = null;
        string trackParams = "";
        bool capsSent = false;
        bool superseded = false;
        StartResponse? body = null;
        try
        {
            // ExoPlayer fetches the stream with its own HTTP stack (no cookie):
            // the media URL carries the session token. HLS segment URLs are
            // relative to the playlist and authorised by the session id in
            // their path.
            await TvMedia.EnsureTokenAsync();
            // The previous episode's audio language is matched against this
            // file's tracks before the request, so the chosen track is in the
            // first stream rather than a restart.
            if (_audio is null && _audioCarry is not null)
            {
                if (!catalog.IsCompleted) await Task.WhenAny(catalog, Task.Delay(2000));
                DecideAudioIfNeeded();
            }
            var seekSec = resumeMs / 1000;
            trackParams = AudioStartParams(maxHeight);
            var url = $"/api/hls/start?path={Uri.EscapeDataString(_filePath)}" +
                      $"&seek={seekSec}&maxHeight={maxHeight}" + Services.TvCodecCaps.HevcParams() + trackParams;
            if (!_forceTranscode && !Bench.BenchLog.Has("nocaps"))
            {
                var caps = Services.TvCodecCaps.Get();
                if (!string.IsNullOrEmpty(caps))
                {
                    url += $"&nativeCaps={Uri.EscapeDataString(caps)}";
                    capsSent = true;
                }
            }

            Log($"start #{gen}: seek={seekSec} maxHeight={maxHeight} play={play}" +
                (trackParams.Length > 0 && _audio is { } sentAudio ? $" audio={DescribeAudio(sentAudio)}" : "") +
                $"{Services.TvCodecCaps.HevcParams().Replace('&', ' ')}{(capsSent ? " +caps" : "")}");
            // Two attempts, like the web player, but only where a second try
            // can help: a network error or a 5xx such as start_failed /
            // too_many_sessions (a start racing the cleanup of the previous
            // session). Never a 4xx, a 504 ffmpeg_timeout (the server already
            // waited 60 s) or ffmpeg_exited (the same plan fails the same way).
            for (int attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
            {
                try
                {
                    using var resp = await _http.PostAsync(url, null, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        body = await resp.Content.ReadFromJsonAsync<StartResponse>(Json, ct);
                        if (HasStreamUrl(body)) break;
                        failure = "сервер не вернул поток";
                    }
                    else
                    {
                        // RFC 7807 problem+json: `detail` is the server's one-line
                        // reason, the `code` extension says which failure it was.
                        int status = (int)resp.StatusCode;
                        StartProblem? pd = null;
                        try { pd = await resp.Content.ReadFromJsonAsync<StartProblem>(Json, ct); } catch { }
                        failure = $"{pd?.Detail ?? pd?.Title ?? "ошибка сервера"} (HTTP {status})";
                        Log($"start #{gen}: HTTP {status} code={pd?.Code} {pd?.Detail}");
                        // 409 superseded: a newer start for this file from this
                        // device replaced this one before it became playable —
                        // that one plays; nothing to report or retry.
                        if (status == 409 && pd?.Code == "superseded") { superseded = true; break; }
                        bool retryable = status >= 500 && status != 504
                                         && pd?.Code is not ("ffmpeg_exited" or "ffmpeg_timeout");
                        if (!retryable) break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { failure = ex.Message; }
                body = null;
                if (attempt == 0) await Task.Delay(800, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { failure ??= ex.Message; }

        // Superseded by a newer start (or the page closed) while waiting: drop
        // this answer and free the HLS session it created.
        if (gen != _startGen || _closed)
        {
            Log($"start #{gen}: superseded");
            if (body?.Token is { } stale) StopHlsSession(stale);
            return;
        }
        if (superseded)
        {
            // Not one of this page's starts (those are cancelled and caught
            // above) — e.g. a duplicate request. Silently: no message, no
            // retry, no fallback rung. A choice made for it is undone while
            // the current stream still plays, so the menus show what plays.
            Log($"start #{gen}: superseded on the server — ignored");
            if (_kind != StreamKind.None && string.IsNullOrEmpty(NativePlayerService.Instance?.GetState()?.ErrorMessage))
                onFail?.Invoke();
            return;
        }
        if (!HasStreamUrl(body))
        {
            OnStartFailed(failure ?? "нет ответа", resumeMs, maxHeight, onFail);
            return;
        }

        var b = body!;
        _knownDurationMs = (long)((b.TotalDuration ?? 0) * 1000);
        if (maxHeight == 0 && b.Output?.Height > 0) _sourceHeight = b.Output.Height;

        bool directPlay   = !string.IsNullOrEmpty(b.DirectPlayUrl);
        bool directStream = !directPlay && !string.IsNullOrEmpty(b.DirectStreamUrl);
        var newToken = !directPlay && !directStream ? b.Token : null;

        // Original bitstream bigger than any decoder on the device takes (4K
        // HEVC on a 1080p projector SoC): ExoPlayer rejects it with
        // NO_EXCEEDS_CAPABILITIES, and so would the no-caps retry (still a
        // copy). Ask for a capped re-encode straight away; an HDR source is
        // tone-mapped on the way. This answer's HLS session is not used.
        if (maxHeight == 0 && b.Output is { } o && !(o.Plan ?? "").EndsWith("reencode", StringComparison.Ordinal)
            && !Services.TvCodecCaps.CanDecodeSize(o.VideoCodec, o.Width, o.Height, o.BitDepth))
        {
            if (newToken is not null) StopHlsSession(newToken);
            int cap = o.Height > 1080 ? 1080 : 720;
            FlashInfo($"{o.Height}p не поддерживается устройством — {cap}p");
            _ = StartStreamAsync(resumeMs, cap, play, onFail);
            return;
        }

        // Sent without the chosen track to see whether the file Direct Plays —
        // it doesn't: restart with the track before anything plays (the wrong
        // language is never heard). This answer's HLS session is not used.
        if (NeedsTrackRestart(directPlay, trackParams))
        {
            if (newToken is not null) StopHlsSession(newToken);
            _directPlayable = false;   // → the restart sends audioTrackIndex
            Log($"start #{gen}: {(directStream ? "DirectStream" : "Hls")} carries a:0 only → restart with the track");
            _ = StartStreamAsync(resumeMs, maxHeight, play, onFail);
            return;
        }

        // A file's first start waits briefly for its track catalog, and any
        // start for the check of a subtitle it must side-load, so the subtitle
        // goes out with the stream instead of a second prepare right after.
        if (!catalog.IsCompleted)
            await Task.WhenAny(catalog, Task.Delay(2000));
        DecideSubIfNeeded();
        if (SideloadToCheck(directPlay) is { } check)
            await Task.WhenAny(CheckSubtitleAsync(check), Task.Delay(1500));
        if (gen != _startGen || _closed)
        {
            if (newToken is not null) StopHlsSession(newToken);
            return;
        }

        // Success — only now does the new stream replace the old one.
        _streamFilePath = _filePath;
        _maxHeight = maxHeight;
        _kind = directPlay ? StreamKind.DirectPlay : directStream ? StreamKind.DirectStream : StreamKind.Hls;
        Log($"start #{gen}: {_kind} plan={b.Output?.Plan}");
        // Only a start the server was free to Direct Play tells whether it can.
        if (maxHeight == 0 && trackParams.Length == 0 && capsSent) _directPlayable = directPlay;
        _isDirectStream = directStream;
        ClearError();

        var prevToken = _hlsToken;
        _hlsToken = newToken;
        _lastKeepaliveUtc = DateTime.UtcNow;
        if (prevToken is not null && prevToken != _hlsToken) StopHlsSession(prevToken);

        var mediaUrl = TvMedia.Url(Abs(b.DirectPlayUrl ?? b.DirectStreamUrl ?? b.ManifestUrl)!);
        _streamUrl = mediaUrl;
        _streamBaseMs = 0;
        DecideSubIfNeeded();
        var (sideload, textOn) = BuildSubPlan();
        _textApplied = false;
        _audioApplied = false;
        if (directStream)
        {
            // Progressive remux pipe: NOT seekable — bake the offset into the
            // request instead of SeekTo (which would snap to 0). The pipe's
            // PTS carry the absolute offset, so no base correction needed.
            var seekSec = resumeMs / 1000;
            if (seekSec > 0)
                mediaUrl += (mediaUrl.Contains('?') ? "&" : "?") + $"seek={seekSec}";
            NativePlayerService.Instance?.PlayAsync(mediaUrl, 0, play, sideload, textOn);
        }
        else
        {
            // Direct Play: range-seekable raw file — ExoPlayer owns an absolute
            // timeline. HLS: the server's media playlist lists the FULL run from
            // seg-0 (plus EXT-X-START at the seek point), so ExoPlayer's position
            // is already absolute. The old base = resumeMs counted the resume
            // twice — the HUD, saved progress, skip/Up-Next timing and quality/
            // error restarts all ran at 2× the real position. Seek explicitly on
            // that absolute timeline rather than relying on EXT-X-START handling.
            NativePlayerService.Instance?.PlayAsync(mediaUrl, resumeMs, play, sideload, textOn);
        }
        _ = LoadSegmentsAsync();
    }

    /// <summary>logcat tag "Animarr.Player": start requests, outcomes, track
    /// switches — the player-side half of the "Animarr.NativePlayer" trace.</summary>
    private static void Log(string msg)
    {
#if ANDROID
        global::Android.Util.Log.Info("Animarr.Player", msg);
#endif
    }

    private static bool HasStreamUrl(StartResponse? b) =>
        b is not null && (!string.IsNullOrEmpty(b.DirectPlayUrl) || !string.IsNullOrEmpty(b.DirectStreamUrl)
                          || !string.IsNullOrEmpty(b.ManifestUrl));

    /// <summary>/api/hls/start failed twice. If the previous stream still plays,
    /// it stays (with the settings it was started with — the menus read those
    /// back, so the switch is rolled back); otherwise the error stays on screen
    /// and OK / play retries the same start.
    /// <para>
    /// The server keeps this device's previous HLS session alive when a
    /// restart fails, so the old stream really does keep playing: _hlsToken is
    /// only replaced by a successful start, and Tick's keepalive goes on
    /// pinging the old token (an idle session is reaped after 5 min).
    /// </para></summary>
    private void OnStartFailed(string reason, long resumeMs, int maxHeight, Action? onFail)
    {
        Log($"start failed: {reason} (maxHeight={maxHeight}, kind={_kind})");
        var st = NativePlayerService.Instance?.GetState();
        bool oldAlive = _kind != StreamKind.None && st is not null && string.IsNullOrEmpty(st.ErrorMessage);
        if (oldAlive)
        {
            onFail?.Invoke();
            FlashInfo($"Не удалось переключить поток ({reason}) — остаётся текущий");
            return;
        }
        _retryStart = (resumeMs, maxHeight);
        ShowError($"Не удалось запустить воспроизведение ({reason}). OK — повторить");
    }

    // ── Track catalog: what the FILE carries, whatever the stream tier ──────
    // ExoPlayer only sees the tracks of the stream it plays: all of them on
    // Direct Play, one audio and no subtitles on Direct Stream / HLS. The
    // menus are built from the server's probe of the file (+ sidecar files
    // from /api/external-tracks) instead, so a choice is the same thing on
    // every tier and survives restarts: embedded audio #N / subtitle #N
    // (ffprobe order — the server's audioTrackIndex / /api/subtitle track=N)
    // or a sidecar path.

    private sealed record ProbeTrack(int Index, string? Lang, string? Title, string? Codec,
                                     int Channels, bool Default, bool Forced);

    private List<ProbeTrack> _probeAudio = new();
    private List<ProbeTrack> _probeSubs = new();
    private List<ExternalTrackDto> _extAudio = new();
    private List<ExternalTrackDto> _extSubs = new();
    private bool _probeOk;
    private string? _catalogPath;
    private Task _catalogTask = Task.CompletedTask;

    private Task EnsureCatalogAsync()
    {
        if (string.Equals(_catalogPath, _filePath, StringComparison.Ordinal)) return _catalogTask;
        _catalogPath = _filePath;
        _probeAudio = new(); _probeSubs = new(); _extAudio = new(); _extSubs = new();
        _probeOk = false;
        _subNeedsServer.Clear();
        _audioNeedsServer.Clear();
        _directPlayable = null;
        return _catalogTask = LoadCatalogAsync(_filePath);
    }

    private async Task LoadCatalogAsync(string path)
    {
        var p = Uri.EscapeDataString(path);
        var probeTask = _http.GetStringAsync($"/api/probe?path={p}");
        var extTask = _http.GetFromJsonAsync<List<ExternalTrackDto>>($"{ApiRoutes.ExternalTracks}?path={p}", Json);
        List<ProbeTrack> audio = new(), subs = new();
        bool ok = false;
        try
        {
            using var doc = JsonDocument.Parse(await probeTask);
            if (doc.RootElement.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    if (type is not ("audio" or "subtitle")) continue;
                    var tags = s.TryGetProperty("tags", out var t) ? t : default;
                    var disp = s.TryGetProperty("disposition", out var d) ? d : default;
                    var list = type == "audio" ? audio : subs;
                    list.Add(new ProbeTrack(list.Count,
                        Str(tags, "language") ?? Str(tags, "LANGUAGE"),
                        Str(tags, "title") ?? Str(tags, "TITLE"),
                        Str(s, "codec_name"),
                        s.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var n) ? n : 0,
                        Flag(disp, "default"), Flag(disp, "forced")));
                }
                ok = true;
            }
        }
        catch (Exception ex) { Log($"probe failed: {ex.Message}"); }
        List<ExternalTrackDto> ext = new();
        try { ext = await extTask ?? new(); }
        catch (Exception ex) { Log($"external-tracks failed: {ex.Message}"); }

        if (!string.Equals(path, _filePath, StringComparison.Ordinal) || _closed) return;   // episode changed
        _probeAudio = audio;
        _probeSubs = subs;
        _probeOk = ok;
        _extAudio = ext.Where(e => e.Kind == "audio").ToList();
        // /api/subtitle converts text formats only (a .sup / .idx can't become WebVTT).
        _extSubs = ext.Where(e => e.Kind == "subtitle" && e.Ext?.ToLowerInvariant()
                       is "srt" or "ass" or "ssa" or "vtt" or "webvtt" or "sub" or "smi" or "txt").ToList();
        Log($"catalog: {audio.Count} audio, {subs.Count} subs, {_extAudio.Count} ext audio, {_extSubs.Count} ext subs");

        // A stream that started before the catalog arrived: pick its default
        // subtitle now.
        if (_sub is null && _kind != StreamKind.None)
        {
            DecideSubIfNeeded();
            if (_sub is { IsOff: false }) ApplySubChoice();
        }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        static bool Flag(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) && i != 0;
    }

    // ── Audio ───────────────────────────────────────────────────────────────
    // Direct Play: ExoPlayer has every embedded track — switch in place. On
    // Direct Stream / HLS the stream carries ONE audio track (the /api/video
    // remux maps 0:a:0), so another track is a restart with audioTrackIndex
    // (the server then muxes it into HLS, transcoded to AAC); a sidecar dub
    // is a restart with externalAudio (a second ffmpeg input). A track the
    // device can't decode on Direct Play goes the same server way instead of
    // failing in the decoder.

    /// <summary>Audio choice: embedded #Embedded (ffprobe audio order = the
    /// server's audioTrackIndex) or a sidecar dub file.</summary>
    private readonly record struct AudioSel(int Embedded, string? External);

    /// <summary>null = the default (server: a:0; Direct Play: Media3's pick).</summary>
    private AudioSel? _audio;
    /// <summary>Previous episode's audio language, matched on the next file.</summary>
    private string? _audioCarry;
    private bool _audioApplied;
    /// <summary>This file Direct Plays on this device (learned from an uncapped
    /// start without track parameters) — then an embedded audio switch needs
    /// no audioTrackIndex (which would force HLS): it is selected locally.</summary>
    private bool? _directPlayable;
    /// <summary>Embedded audio tracks ExoPlayer can't decode on this device.</summary>
    private readonly HashSet<int> _audioNeedsServer = new();

    /// <summary>Track parameters for the next /api/hls/start. An embedded track
    /// goes out as audioTrackIndex only when the stream must carry it: the
    /// server skips Direct Play for any audioTrackIndex &gt; 0. On a file not
    /// known to Direct Play or not (the first start of a carried language)
    /// the start goes out without it — a Direct Play answer then selects the
    /// track locally, any other answer is restarted with it before it plays
    /// (<see cref="NeedsTrackRestart"/>).</summary>
    private string AudioStartParams(int maxHeight)
    {
        if (_audio is { External: { } ext })
            return $"&externalAudio={Uri.EscapeDataString(ext)}";
        if (_audio is { Embedded: > 0 } a)
        {
            bool canDirectPlay = maxHeight == 0 && !_forceTranscode && !Bench.BenchLog.Has("nocaps")
                                 && !_audioNeedsServer.Contains(a.Embedded);
            if (!canDirectPlay || _directPlayable == false) return $"&audioTrackIndex={a.Embedded}";
        }
        return "";
    }

    /// <summary>A start sent without the chosen embedded track (to learn whether
    /// the file Direct Plays) came back as a stream that carries a:0 only.
    /// <para>
    /// SERVER HOOK: once /api/video takes audioTrackIndex (server work in
    /// progress, not merged), a Direct Stream answer could keep its tier by
    /// appending &amp;audioTrackIndex=N to directStreamUrl instead of this
    /// restart onto HLS. Until then the restart sends the parameter and the
    /// server answers with whatever tier carries the track.
    /// </para></summary>
    private bool NeedsTrackRestart(bool directPlay, string trackParams) =>
        !directPlay && trackParams.Length == 0 && _audio is { External: null, Embedded: > 0 };

    private void DecideAudioIfNeeded()
    {
        if (_audio is not null || _audioCarry is null || !_catalogTask.IsCompleted) return;
        var lang = _audioCarry;
        _audioCarry = null;
        if (_probeAudio.FirstOrDefault(a => TrackLabels.NormLang(a.Lang) == lang) is { } e)
            _audio = new AudioSel(e.Index, null);
        else if (_extAudio.FirstOrDefault(x => TrackLabels.NormLang(x.Language) == lang) is { } x)
            _audio = new AudioSel(-1, x.Path);
        if (_audio is { } sel) Log($"audio carried: {DescribeAudio(sel)}");
    }

    /// <summary>Media3 audio track for embedded audio #k on Direct Play
    /// (container order; by rank within the language if Media3 skipped one).</summary>
    private TrackOption? MapEmbeddedAudio(int k, IReadOnlyList<TrackOption> media3Audio)
    {
        if (!_probeOk) return k >= 0 && k < media3Audio.Count ? media3Audio[k] : null;
        if (k < 0 || k >= _probeAudio.Count) return null;
        if (media3Audio.Count == _probeAudio.Count) return media3Audio[k];
        var lang = TrackLabels.NormLang(_probeAudio[k].Lang);
        int rank = _probeAudio.Take(k).Count(p => TrackLabels.NormLang(p.Lang) == lang);
        return media3Audio.Where(t => TrackLabels.NormLang(t.Language) == lang).Skip(rank).FirstOrDefault();
    }

    /// <summary>What actually plays: the dub, Media3's selected track on Direct
    /// Play, else the index the stream was started with.</summary>
    private AudioSel CurrentAudio()
    {
        if (_audio is { External: not null } ext) return ext;
        if (_kind == StreamKind.DirectPlay && NativePlayerService.Instance is { } nps)
        {
            var media3 = nps.GetAudioTracks();
            int count = _probeOk ? _probeAudio.Count : media3.Count;
            for (int k = 0; k < count; k++)
                if (MapEmbeddedAudio(k, media3) is { Selected: true }) return new AudioSel(k, null);
        }
        return _audio ?? new AudioSel(0, null);
    }

    private string AudioLabel(ProbeTrack a)
    {
        var parts = new List<string>();
        var lang = TrackLabels.LangName(a.Lang);
        if (lang.Length > 0) parts.Add(lang);
        if (!string.IsNullOrWhiteSpace(a.Title)) parts.Add(a.Title!);
        if (parts.Count == 0) parts.Add($"Дорожка {a.Index + 1}");
        if (TrackLabels.Codec(a.Codec) is { } c) parts.Add(c);
        if (TrackLabels.Channels(a.Channels) is { } ch) parts.Add(ch);
        return string.Join(" · ", parts);
    }

    private static string DescribeAudio(AudioSel s) =>
        s.External is { } p ? $"external {FileName(p)}" : $"embedded #{s.Embedded}";

    /// <summary>Last segment of a server path — '\' (a Windows server) or '/';
    /// Path.GetFileName on Android only knows '/'.</summary>
    private static string FileName(string path) => path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];

    private void ChooseAudio(AudioSel sel, string label)
    {
        if (CurrentAudio() == sel) { FlashInfo("Уже выбрано"); return; }
        var previous = _audio;
        Log($"audio chosen: {DescribeAudio(sel)} (tier {_kind})");
        if (sel.External is null && _kind == StreamKind.DirectPlay && !_audioNeedsServer.Contains(sel.Embedded)
            && NativePlayerService.Instance is { } nps
            && MapEmbeddedAudio(sel.Embedded, nps.GetAudioTracks()) is { } t)
        {
            if (t.Supported)
            {
                // Direct Play: in place, no restart.
                _audio = sel;
                nps.SelectTrack(t.GroupIndex, t.TrackIndex);
                FlashInfo($"Аудио: {label}");
                return;
            }
            // No decoder for it here: the server transcodes it (the restart
            // below then sends audioTrackIndex).
            _audioNeedsServer.Add(sel.Embedded);
        }
        // The stream carries one audio track: restart it with the new one at
        // the current position (play / pause kept). A failed start keeps the
        // old stream and the old choice.
        _audio = sel;
        FlashInfo($"Аудио: {label} — перезапуск потока…");
        var st = NativePlayerService.Instance?.GetState();
        _ = StartStreamAsync(_streamBaseMs + (st?.PositionMs ?? 0), _maxHeight, onFail: () => _audio = previous);
    }

    /// <summary>Direct Play prepared: put the chosen embedded track on (once
    /// per prepare). A track the device can't decode → the server transcodes it.</summary>
    private void ApplyAudioOnTracks(NativePlayerService nps)
    {
        if (_audioApplied) return;
        if (_kind != StreamKind.DirectPlay) { _audioApplied = true; return; }   // the server muxed it
        var media3 = nps.GetAudioTracks();
        if (media3.Count == 0) return;   // not prepared yet
        _audioApplied = true;
        if (_audio is not { External: null } a) return;   // default: Media3's pick
        var t = MapEmbeddedAudio(a.Embedded, media3);
        if (t is { Supported: true })
        {
            if (!t.Selected) nps.SelectTrack(t.GroupIndex, t.TrackIndex);
            Log($"audio: Media3 track {t.GroupIndex}/{t.TrackIndex} for embedded #{a.Embedded}");
            return;
        }
        _audioNeedsServer.Add(a.Embedded);
        Log($"audio: embedded #{a.Embedded} not decodable here → server transcode");
        FlashInfo("Дорожку не декодирует устройство — перекодирую на сервере");
        var st = nps.GetState();
        _ = StartStreamAsync(_streamBaseMs + st.PositionMs, _maxHeight);
    }

    // ── Subtitles ───────────────────────────────────────────────────────────
    // Direct Play: an embedded track Media3 can render is switched in place
    // (track selection, PGS included). Everything else — any embedded track
    // on Direct Stream / HLS (those streams carry no subtitles), a sidecar
    // file, an embedded track Media3 can't render — is the server's WebVTT
    // conversion side-loaded with the stream (/api/subtitle). That URL is
    // checked once before it is attached: a 422 (bitmap track, undecodable
    // charset) on a side-loaded source would fail the whole playback.

    /// <summary>Subtitle choice: embedded track #Embedded (ffprobe subtitle
    /// order), a sidecar file, or off.</summary>
    private readonly record struct SubSel(int Embedded, string? External)
    {
        public static SubSel Off => new(-1, null);
        public bool IsOff => Embedded < 0 && External is null;
    }

    /// <summary>null = not decided for this file yet (catalog not loaded).</summary>
    private SubSel? _sub;
    /// <summary>The previous episode's choice, carried to the next one by
    /// language (Off stays off).</summary>
    private (bool Off, string? Lang)? _subCarry;
    /// <summary>The current stream's text selection has been applied (set on
    /// the first OnTracksChanged after each prepare).</summary>
    private bool _textApplied;
    /// <summary>Embedded tracks Media3 can't render on this Direct Play file —
    /// they go through the server's conversion instead.</summary>
    private readonly HashSet<int> _subNeedsServer = new();
    /// <summary>/api/subtitle URLs (relative, no token) known to convert.</summary>
    private readonly HashSet<string> _goodSubs = new(StringComparer.Ordinal);

    private void DecideSubIfNeeded()
    {
        if (_sub is not null || !_catalogTask.IsCompleted) return;
        _sub = PickDefaultSub();
        Log($"subtitle default: {DescribeSub(_sub.Value)}");
    }

    private SubSel PickDefaultSub()
    {
        if (_subCarry is { } carry)
        {
            if (carry.Off || carry.Lang is null) return SubSel.Off;
            var same = _probeSubs.Where(s => TrackLabels.NormLang(s.Lang) == carry.Lang && SubListable(s)).ToList();
            if ((same.FirstOrDefault(s => !s.Forced) ?? same.FirstOrDefault()) is { } e) return new SubSel(e.Index, null);
            if (_extSubs.FirstOrDefault(x => TrackLabels.NormLang(x.Language) == carry.Lang) is { } x) return new SubSel(-1, x.Path);
            return SubSel.Off;
        }
        // Fresh start: the file's default-flagged track; else a forced track in
        // the audio's language (signs / foreign lines); else off — like Media3.
        if (_probeSubs.FirstOrDefault(s => s.Default && SubListable(s)) is { } def) return new SubSel(def.Index, null);
        var audioLang = TrackLabels.NormLang((_probeAudio.FirstOrDefault(a => a.Default) ?? _probeAudio.FirstOrDefault())?.Lang);
        if (audioLang is not null
            && _probeSubs.FirstOrDefault(s => s.Forced && SubListable(s) && TrackLabels.NormLang(s.Lang) == audioLang) is { } forced)
            return new SubSel(forced.Index, null);
        return SubSel.Off;
    }

    /// <summary>Can this embedded track be shown at all on the current tier?
    /// Text → always (Media3 or the server's WebVTT); bitmap → only when Media3
    /// renders it itself on Direct Play.</summary>
    private bool SubListable(ProbeTrack s, IReadOnlyList<TrackOption>? media3Text = null)
    {
        if (TrackLabels.IsTextSubtitle(s.Codec)) return true;
        if (_kind != StreamKind.DirectPlay) return false;
        media3Text ??= NativePlayerService.Instance?.GetTextTracks() ?? System.Array.Empty<TrackOption>();
        return MapEmbeddedSub(s.Index, media3Text) is { Supported: true };
    }

    /// <summary>Media3 text track for embedded subtitle #k on Direct Play. The
    /// Matroska / MP4 extractors expose the container's subtitle tracks in
    /// order; if Media3 skipped one (an unsupported codec), match by rank
    /// among the tracks of the same language.</summary>
    private TrackOption? MapEmbeddedSub(int k, IReadOnlyList<TrackOption> media3Text)
    {
        var emb = media3Text.Where(t => t.Id?.Contains(NativePlayerService.SideloadTrackId) != true).ToList();
        if (!_probeOk) return k >= 0 && k < emb.Count ? emb[k] : null;
        if (k < 0 || k >= _probeSubs.Count) return null;
        if (emb.Count == _probeSubs.Count) return emb[k];
        var lang = TrackLabels.NormLang(_probeSubs[k].Lang);
        int rank = _probeSubs.Take(k).Count(p => TrackLabels.NormLang(p.Lang) == lang);
        return emb.Where(t => TrackLabels.NormLang(t.Language) == lang).Skip(rank).FirstOrDefault();
    }

    /// <summary>/api/subtitle URL (relative, no token) of a choice; null for a
    /// track that can't become WebVTT.</summary>
    private string? SubtitleRelUrl(SubSel s)
    {
        if (s.External is { } ext)
            return $"/api/subtitle?path={Uri.EscapeDataString(ext)}&track=0&format=webvtt";
        if (s.Embedded < 0) return null;
        if (_probeOk && (s.Embedded >= _probeSubs.Count || !TrackLabels.IsTextSubtitle(_probeSubs[s.Embedded].Codec)))
            return null;
        return $"/api/subtitle?path={Uri.EscapeDataString(_filePath)}&track={s.Embedded}&format=webvtt";
    }

    /// <summary>How the current choice goes out with the next prepare of the
    /// current stream: a side-loaded WebVTT (only once its URL is known to
    /// convert — otherwise it's checked in the background and the stream
    /// reloaded), Media3's own track (Direct Play, text on), or nothing.</summary>
    private (SideloadSubtitle? Sideload, bool TextOn) BuildSubPlan()
    {
        if (_sub is not { } s || s.IsOff) return (null, false);
        if (_kind == StreamKind.DirectPlay && s.External is null && !_subNeedsServer.Contains(s.Embedded))
            return (null, true);   // OnTracksChanged selects the Media3 track
        var rel = SubtitleRelUrl(s);
        if (rel is null) return (null, false);
        if (!_goodSubs.Contains(rel))
        {
            _ = VerifySubThenReloadAsync(rel, s);
            return (null, false);
        }
        return (new SideloadSubtitle(TvMedia.Url(Abs(rel)!), SubLang(s), SubLabel(s)), true);
    }

    private readonly Dictionary<string, Task<bool>> _subChecks = new(StringComparer.Ordinal);

    /// <summary>One check per URL at a time (a start and a background reload
    /// may both ask); a failed check can be retried later.</summary>
    private Task<bool> CheckSubtitleAsync(string rel)
    {
        if (_goodSubs.Contains(rel)) return Task.FromResult(true);
        if (_subChecks.TryGetValue(rel, out var running)) return running;
        var t = DoCheckSubtitleAsync(rel);
        _subChecks[rel] = t;
        _ = t.ContinueWith(_ => _subChecks.Remove(rel), TaskScheduler.FromCurrentSynchronizationContext());
        return t;
    }

    /// <summary>The /api/subtitle URL the next stream (Direct Play or not) will
    /// side-load but that isn't known to convert yet; null if none.</summary>
    private string? SideloadToCheck(bool directPlay)
    {
        if (_sub is not { IsOff: false } s) return null;
        if (directPlay && s.External is null && !_subNeedsServer.Contains(s.Embedded)) return null;
        return SubtitleRelUrl(s) is { } rel && !_goodSubs.Contains(rel) ? rel : null;
    }

    private async Task<bool> DoCheckSubtitleAsync(string rel)
    {
        try
        {
            // The first request converts the track into the server's cache (a
            // full read of the container for an embedded one) — ExoPlayer's own
            // fetch then gets the cached file.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var resp = await _http.GetAsync(rel, cts.Token);
            if (!resp.IsSuccessStatusCode) { Log($"subtitle {rel}: HTTP {(int)resp.StatusCode}"); return false; }
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(body)) return false;
            _goodSubs.Add(rel);
            return true;
        }
        catch (Exception ex) { Log($"subtitle {rel}: {ex.Message}"); return false; }
    }

    private async Task VerifySubThenReloadAsync(string rel, SubSel forChoice, SubSel? previous = null)
    {
        var file = _filePath;
        if (!await CheckSubtitleAsync(rel))
        {
            if (file == _filePath && _sub == forChoice)
            {
                // Back to what is actually on screen, so the sheet doesn't mark
                // a track that never loaded.
                _sub = previous ?? SubSel.Off;
                FlashInfo("Эти субтитры не удалось загрузить");
            }
            return;
        }
        if (file != _filePath || _sub != forChoice || _closed) return;
        ReloadForSubtitles();
    }

    /// <summary>Re-prepare the current stream with the current subtitle
    /// choice at the current position and play state. Direct Play / HLS
    /// re-open the same URL; the Direct Stream pipe can't be re-opened
    /// mid-way, so it is restarted at the position.</summary>
    private void ReloadForSubtitles(Action? onFail = null)
    {
        var st = NativePlayerService.Instance?.GetState();
        long pos = _streamBaseMs + (st?.PositionMs ?? 0);
        if (_kind == StreamKind.DirectStream)
        {
            _ = StartStreamAsync(pos, _maxHeight, onFail: onFail);
            return;
        }
        if (_streamUrl is null) return;
        var (sideload, textOn) = BuildSubPlan();
        _textApplied = false;
        _audioApplied = false;
        Log($"subtitle reload: {DescribeSub(_sub ?? SubSel.Off)} at {pos} ms");
        NativePlayerService.Instance?.PlayAsync(_streamUrl, pos, st?.PlayWhenReady ?? true, sideload, textOn);
    }

    /// <summary>ExoPlayer listed / re-selected tracks: apply the subtitle
    /// choice to the freshly prepared stream (once per prepare).</summary>
    private void OnPlayerTracksChanged()
    {
        if (NativePlayerService.Instance is not { } nps) return;
        ApplyAudioOnTracks(nps);
        if (_textApplied || _sub is not { } s) return;
        if (s.IsOff) { _textApplied = true; return; }
        var text = nps.GetTextTracks();
        if (text.FirstOrDefault(t => t.Id?.Contains(NativePlayerService.SideloadTrackId) == true) is { } side)
        {
            _textApplied = true;
            if (!side.Selected) nps.SelectTrack(side.GroupIndex, side.TrackIndex);
            Log($"subtitle: side-loaded track selected ({DescribeSub(s)})");
            return;
        }
        if (_kind != StreamKind.DirectPlay || s.External is not null || _subNeedsServer.Contains(s.Embedded)) return;
        // Wait for the prepared track list (the first callback can be empty).
        if (nps.GetAudioTracks().Count == 0 && nps.GetVideoTracks().Count == 0) return;
        _textApplied = true;
        if (MapEmbeddedSub(s.Embedded, text) is { Supported: true } t)
        {
            nps.SelectTrack(t.GroupIndex, t.TrackIndex);
            Log($"subtitle: Media3 track {t.GroupIndex}/{t.TrackIndex} for embedded #{s.Embedded}");
            return;
        }
        // Media3 can't render it here → the server's WebVTT conversion.
        _subNeedsServer.Add(s.Embedded);
        Log($"subtitle: embedded #{s.Embedded} not renderable by Media3 → server WebVTT");
        if (SubtitleRelUrl(s) is { } rel) _ = VerifySubThenReloadAsync(rel, s);
    }

    private string? SubLang(SubSel s) =>
        s.External is { } p ? _extSubs.FirstOrDefault(x => x.Path == p)?.Language
        : s.Embedded >= 0 && s.Embedded < _probeSubs.Count ? _probeSubs[s.Embedded].Lang : null;

    private string SubLabel(SubSel s)
    {
        if (s.IsOff) return "Выключены";
        if (s.External is { } p)
            return _extSubs.FirstOrDefault(x => x.Path == p) is { } x ? ExternalLabel(x) : FileName(p);
        if (s.Embedded < _probeSubs.Count)
        {
            var t = _probeSubs[s.Embedded];
            var parts = new List<string>();
            var lang = TrackLabels.LangName(t.Lang);
            if (lang.Length > 0) parts.Add(lang);
            if (!string.IsNullOrWhiteSpace(t.Title)) parts.Add(t.Title!);
            if (parts.Count == 0) parts.Add($"Дорожка {t.Index + 1}");
            if (TrackLabels.Codec(t.Codec) is { } c) parts.Add(c);
            if (t.Forced) parts.Add("форсированные");
            return string.Join(" · ", parts);
        }
        return $"Дорожка {s.Embedded + 1}";
    }

    /// <summary>"Русский · SRT · файл", "AniLibria · MKA · файл": the full
    /// language name when the server's label is just the language word,
    /// else its label (dub studio / folder hint).</summary>
    private static string ExternalLabel(ExternalTrackDto x)
    {
        var lang = TrackLabels.LangName(x.Language);
        var name = lang.Length > 0 && !(x.Label ?? "").Trim().Contains(' ') ? lang : (x.Label ?? x.FileName);
        return $"{name} · {(x.Ext ?? "").ToUpperInvariant()} · файл";
    }

    private static string DescribeSub(SubSel s) =>
        s.IsOff ? "off" : s.External is { } p ? $"external {FileName(p)}" : $"embedded #{s.Embedded}";

    /// <summary>Apply the current choice to the playing stream (CC sheet, or
    /// a default picked after the stream started).</summary>
    private void ApplySubChoice(SubSel? previous = null)
    {
        if (_sub is not { } s || NativePlayerService.Instance is not { } nps) return;
        if (s.IsOff) { _textApplied = true; nps.DisableSubtitles(); return; }
        if (_kind == StreamKind.DirectPlay && s.External is null && !_subNeedsServer.Contains(s.Embedded)
            && MapEmbeddedSub(s.Embedded, nps.GetTextTracks()) is { Supported: true } t)
        {
            // In place, no re-prepare.
            _textApplied = true;
            nps.SelectTrack(t.GroupIndex, t.TrackIndex);
            return;
        }
        var rel = SubtitleRelUrl(s);
        if (rel is null) { _sub = previous ?? SubSel.Off; FlashInfo("Эти субтитры недоступны"); return; }
        if (_goodSubs.Contains(rel)) { ReloadForSubtitles(() => _sub = previous); return; }
        FlashInfo("Загрузка субтитров…");
        _ = VerifySubThenReloadAsync(rel, s, previous);
    }

    private void ChooseSub(SubSel sel)
    {
        if (_sub == sel) return;
        var previous = _sub;
        _sub = sel;
        Log($"subtitle chosen: {DescribeSub(sel)}");
        FlashInfo(sel.IsOff ? "Субтитры выключены" : $"Субтитры: {SubLabel(sel)}");
        ApplySubChoice(previous);
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
        // Not cached: a failed fetch used to read as "no other episodes" for
        // the rest of the session.
        catch (Exception ex) { Log($"episode list: {ex.Message}"); return new List<MediaFileDto>(); }
        return _episodes;
    }

    /// <summary>The episode <paramref name="delta"/> steps from the current
    /// one; null at either end (or for a film).</summary>
    private async Task<MediaFileDto?> AdjacentAsync(int delta)
    {
        var eps = await GetEpisodesAsync();
        int i = eps.FindIndex(f => string.Equals(f.FilePath, _filePath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = eps.FindIndex(f => f.Season == _season && f.Episode == _episode);
        int j = i + delta;
        return i < 0 || j < 0 || j >= eps.Count ? null : eps[j];
    }

    private async void GoAdjacentEpisode(int delta)
    {
        var eps = await GetEpisodesAsync();
        if (eps.Count <= 1) { FlashInfo("Других серий нет"); return; }
        if (await AdjacentAsync(delta) is not { } f)
        {
            FlashInfo(delta > 0 ? "Это последняя серия" : "Это первая серия");
            return;
        }
        SwitchToEpisode(f);
    }

    private int _switchGen;

    /// <summary>Where <paramref name="f"/> was left off: an unwatched episode's
    /// saved position (like the detail page), else the start. Asked fresh —
    /// another device may have moved it. A slow / failed answer starts at 0.</summary>
    private async Task<long> SavedPositionAsync(MediaFileDto f)
    {
        try
        {
            var ask = _api.GetWatchStatesAsync(_mediaItemId);
            if (await Task.WhenAny(ask, Task.Delay(3000)) != ask) return 0;
            var states = await ask;
            var ws = states.FirstOrDefault(w => string.Equals(w.FilePath, f.FilePath, StringComparison.OrdinalIgnoreCase))
                     ?? states.FirstOrDefault(w => (w.Season ?? 1) == (f.Season ?? 1) && w.Episode == f.Episode);
            return ws is { IsWatched: false, ProgressMs: > 0 } ? ws.ProgressMs!.Value : 0;
        }
        catch (Exception ex) { Log($"watch states: {ex.Message}"); return 0; }
    }

    private async void SwitchToEpisode(MediaFileDto f)
    {
        // The episode being left: its last seconds count too (90 % = watched).
        if (_streamFilePath == _filePath && NativePlayerService.Instance?.GetState() is { } st)
            RecordProgress(_streamBaseMs + st.PositionMs, _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs);
        _season = f.Season;
        _episode = f.Episode;
        _filePath = f.FilePath;
        // Fresh episode → back to original quality + absolute timeline, drop stale
        // skip segments (they're re-fetched for the new episode).
        _maxHeight = 0; _sourceHeight = 0; _streamBaseMs = 0; _knownDurationMs = 0;
        _isDirectStream = false; _forceTranscode = false; _capFallback = false;   // per-file decisions
        _recoveringSinceUtc = null;
        _pendingSeekMs = null; _seekTimer?.Stop();   // a seek pressed on the old episode
        // Tracks are per file: carry the subtitle choice by language (or off).
        if (_sub is { } sub) _subCarry = (sub.IsOff, sub.IsOff ? null : TrackLabels.NormLang(SubLang(sub)));
        _sub = null;
        if (_audio is { } aud)
            _audioCarry = TrackLabels.NormLang(aud.External is { } dub
                ? _extAudio.FirstOrDefault(x => x.Path == dub)?.Language
                : aud.Embedded < _probeAudio.Count ? _probeAudio[aud.Embedded].Lang : null);
        _audio = null;
        _introStart = _introEnd = _creditsStart = _creditsEnd = null;
        _skipVisible = false; SkipButton.IsVisible = false;
        ResetUpNext();
        EyebrowLabel.Text = _episode is not null
            ? $"NOW PLAYING · S{_season ?? 1} · EP {_episode}"
            : "NOW PLAYING";
        FlashInfo(_episode is not null ? $"Серия {_episode}" : "Воспроизведение");
        _ = ResolveAdjacentAsync();
        // ⏭ / ⏮ / Up Next continue an episode where it was left (it always
        // started at 0:00).
        int sw = ++_switchGen;
        long resume = await SavedPositionAsync(f);
        if (sw != _switchGen || _closed) return;
        if (resume > 0) Log($"episode switch: resume at {resume} ms");
        _ = StartStreamAsync(resume, 0, playWhenReady: true);
    }

    // ── Up Next ─────────────────────────────────────────────────────────────
    // Like the web player: the next episode is offered from the detected
    // credits start (else 95 % of the runtime). The card announces itself on
    // its own for 5 s, then shows with the HUD; in the last 10 s it counts
    // down on screen and the next episode starts at the end. "Скрыть" / Back
    // cancels that for this episode. Its "Пропустить титры" only shows when
    // something follows the credits (a preview) — otherwise "Смотреть" is the
    // skip.

    private const double UpNextFallbackFraction = 0.95;
    private const double UpNextCountdownSec = 10;
    private static readonly TimeSpan UpNextAnnounce = TimeSpan.FromSeconds(5);

    private MediaFileDto? _nextEpisode, _prevEpisode;
    private bool _upNextArmed, _upNextDismissed, _upNextDone, _creditsSkipUsed;
    private DateTime _upNextArmedUtc;
    private double _upNextRemainingSec = double.MaxValue;

    private async Task ResolveAdjacentAsync()
    {
        var file = _filePath;
        var next = await AdjacentAsync(+1);
        var prev = await AdjacentAsync(-1);
        if (file != _filePath) return;
        _nextEpisode = next;
        _prevEpisode = prev;
        UpNextName.Text = next is null ? ""
            : next.Episode is { } n ? (next.Season is { } s && s != _season ? $"Сезон {s} · Серия {n}" : $"Серия {n}")
            : System.IO.Path.GetFileNameWithoutExtension(FileName(next.FilePath));
    }

    private void ResetUpNext()
    {
        _nextEpisode = _prevEpisode = null;
        _upNextArmed = _upNextDismissed = _upNextDone = _creditsSkipUsed = false;
        _upNextRemainingSec = double.MaxValue;
        UpNextCard.IsVisible = false;
    }

    private double UpNextTriggerSec(double durSec) =>
        _creditsStart is double cs && cs > 0 && cs < durSec ? cs : durSec * UpNextFallbackFraction;

    /// <summary>Per tick: arm / count down / advance (only while playing — a
    /// pause in the last seconds holds the countdown, like the web player
    /// whose countdown runs on timeupdate).</summary>
    private void UpdateUpNext(double posSec, double durSec, bool playing)
    {
        bool armed = !_upNextDone && !_upNextDismissed && _nextEpisode is not null
                     && durSec > 0 && posSec >= UpNextTriggerSec(durSec);
        if (armed && !_upNextArmed)
        {
            _upNextArmedUtc = DateTime.UtcNow;
            Log($"up next armed at {posSec:0}s: {UpNextName.Text}");
        }
        _upNextArmed = armed;
        _upNextRemainingSec = armed ? durSec - posSec : double.MaxValue;
        if (armed)
        {
            UpNextPlayLabel.Text = _upNextRemainingSec <= UpNextCountdownSec
                ? $"▶  Смотреть · {Math.Max(0, (int)Math.Ceiling(_upNextRemainingSec))}"
                : "▶  Смотреть";
            UpNextSkipButton.IsVisible = !_creditsSkipUsed && _creditsEnd is double ce
                                         && durSec - ce > 5 && posSec < ce;
        }
        UpdateUpNextVisibility();
        if (armed && playing && _upNextRemainingSec <= 0.4) TriggerUpNext();
    }

    private void UpdateUpNextVisibility()
    {
        bool show = _upNextArmed && _sheetMode == SheetMode.None
                    && (Hud.IsVisible || DateTime.UtcNow - _upNextArmedUtc < UpNextAnnounce
                        || _upNextRemainingSec <= UpNextCountdownSec);
        if (show == UpNextCard.IsVisible) return;
        bool hadFocus = IsFocused(UpNextPlayButton) || IsFocused(UpNextSkipButton) || IsFocused(UpNextHideButton);
        UpNextCard.IsVisible = show;
        // Appearing over the HUD: focus "Смотреть" (OK = next episode, like the
        // lone card). Disappearing with focus inside: back to play / pause.
        if (show && Hud.IsVisible) Dispatcher.Dispatch(() => FocusRow(UpNextPlayButton));
        else if (!show && hadFocus && Hud.IsVisible) Dispatcher.Dispatch(() => FocusRow(PlayPauseButton));
    }

    private void TriggerUpNext()
    {
        if (_upNextDone || _nextEpisode is not { } next) return;
        _upNextDone = true;
        _upNextArmed = false;
        UpdateUpNextVisibility();
        Log($"up next → S{next.Season}E{next.Episode}");
        SwitchToEpisode(next);
    }

    private void DismissUpNext()
    {
        _upNextDismissed = true;
        _upNextArmed = false;
        UpdateUpNextVisibility();
        Log("up next dismissed");
    }

    private void SkipCredits()
    {
        if (_creditsEnd is not double ce) return;
        _creditsSkipUsed = true;
        UpNextSkipButton.IsVisible = false;
        FocusRow(UpNextPlayButton);
        SeekToAbs((long)(ce * 1000));
    }

    private static bool IsFocused(View v)
    {
#if ANDROID
        return v.Handler?.PlatformView is Android.Views.View nv && nv.IsFocused;
#else
        return false;
#endif
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
        // but it failed at runtime (vendor quirks, a level the SoC can't take)
        // → retry with nativeCaps off, which sends the server down its HLS
        // pipeline; if that copy fails too, a 1080p H.264 re-encode.
        if (string.IsNullOrEmpty(st.ErrorMessage)
            || DateTime.UtcNow - _recoveringSinceUtc > TimeSpan.FromSeconds(8))
            _recoveringSinceUtc = null;
        if (!string.IsNullOrEmpty(st.ErrorMessage) && _recoveringSinceUtc is null)
        {
            if (!_forceTranscode)
            {
                _forceTranscode = true;
                _recoveringSinceUtc = DateTime.UtcNow;
                FlashInfo("Формат не пошёл — переключаюсь на транскод");
                _ = StartStreamAsync(absPos, _maxHeight);
                return;
            }
            if (!_capFallback && _maxHeight is 0 or > 1080)
            {
                _capFallback = true;
                _recoveringSinceUtc = DateTime.UtcNow;
                FlashInfo("Оригинал не воспроизводится — переключаюсь на 1080p");
                _ = StartStreamAsync(absPos, 1080);
                return;
            }
            // Every rung used up: say so (it used to sit silently at the
            // failed frame) and let OK retry from here.
            if (!_errorShown)
            {
                _retryStart = (absPos, _maxHeight);
                ShowError("Не удалось воспроизвести файл. OK — повторить");
            }
        }

        if (_hlsToken is { } tok && (DateTime.UtcNow - _lastKeepaliveUtc).TotalSeconds >= 30)
        {
            _lastKeepaliveUtc = DateTime.UtcNow;
            _ = KeepAliveAsync(tok);
        }

        // The intent (PlayWhenReady), not Playing: that is false while
        // buffering, so the icon offered "play" and OK "resumed" instead of
        // pausing.
        PlayPauseIcon.Text  = st.PlayWhenReady ? "❚❚" : "▶";
        BufferSpinner.IsVisible = st.Buffering;

        if (dur > 0 && _pendingSeekMs is null)
        {
            Scrubber.Progress = Math.Clamp((double)absPos / dur, 0, 1);
            PositionLabel.Text = Fmt(absPos);
            DurationLabel.Text = Fmt(dur);
        }

#if ANDROID
        _session?.SetMetadata(_title, _episode is not null ? $"Сезон {_season ?? 1} · Серия {_episode}" : null, dur);
        _session?.SetState(st.PlayWhenReady, st.Buffering, st.Ended, _pendingSeekMs ?? absPos, _speed,
                           _nextEpisode is not null, _prevEpisode is not null);
#endif

        // An episode switch is waiting for its stream: the old one's position
        // and end belong to the episode that was left.
        if (_streamFilePath != _filePath) return;

        UpdateUpNext(absPos / 1000.0, dur / 1000.0, st.PlayWhenReady);
        // It may have switched episodes (and resolved the one after) — the
        // old stream's end below must not advance a second time.
        if (_streamFilePath != _filePath) return;
        UpdateSkip(absPos / 1000.0);
        UpdateSkipFocusLink();

        if (st.Ended)
        {
            RecordProgress(absPos, dur);
            // The next episode follows unless Up Next was dismissed (or the
            // user paused right at the end).
            if (_nextEpisode is not null && !_upNextDismissed && !_upNextDone)
            {
                if (st.PlayWhenReady) TriggerUpNext();
            }
            else if (!_upNextDone) Leave();
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
        // Credits: only inside the detected segment (it used to come back after
        // creditsEnd for the rest of the file), and not while the Up Next card
        // owns the credits — it has its own skip.
        if (_creditsStart is double cs && posSec >= cs && !_upNextArmed
            && (_creditsEnd is not double ce || posSec < ce))
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
        if (_skipTarget > 0) SeekToAbs((long)(_skipTarget * 1000));
        _skipVisible = false;
        SkipButton.IsVisible = false;
    }

    /// <summary>Seek to an absolute time on the file's timeline (skip buttons,
    /// the system's seek command).</summary>
    private void SeekToAbs(long ms)
    {
        ms = Math.Max(0, ms);
        _pendingSeekMs = null;
        _seekTimer?.Stop();
        // The remux pipe is unseekable (SeekTo snaps to 0, so "Skip intro"
        // replayed the chunk) — re-request it at the target.
        if (_isDirectStream) { _ = StartStreamAsync(ms, _maxHeight); return; }
        NativePlayerService.Instance?.SeekAsync(Math.Max(0, ms - _streamBaseMs));
    }

    private void TogglePlay()
    {
        // By the intent (PlayWhenReady): Playing is false while buffering, so
        // a pause pressed during buffering used to "resume" instead.
        var st = NativePlayerService.Instance?.GetState();
        SetPlaying(_retryStart is not null || st is not { PlayWhenReady: true });
    }

    /// <summary>Play / pause (HUD button, media keys, the system). Play after a
    /// failed start retries it. A pause from a media key or the system with
    /// the HUD hidden shows the HUD, so it's clear where playback stopped.</summary>
    private void SetPlaying(bool play)
    {
        if (_retryStart is { } retry)
        {
            if (!play) return;
            ClearError();
            // The failed player reports its old error until the new stream is
            // prepared — don't let Tick read it as a failure of the retry.
            _recoveringSinceUtc = DateTime.UtcNow;
            FlashInfo("Повторный запуск…");
            _ = StartStreamAsync(retry.ResumeMs, retry.MaxHeight, playWhenReady: true);
            return;
        }
        if (NativePlayerService.Instance is not { } nps) return;
        if (play) nps.ResumeAsync(); else nps.PauseAsync();
        PlayPauseIcon.Text = play ? "❚❚" : "▶";
        if (!play && !Hud.IsVisible && _sheetMode == SheetMode.None) RevealHud();
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
        SheetScroll.IsVisible = false;
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
        // The file's tracks (probe) + sidecar dubs — a Direct Stream / HLS
        // stream itself carries only the one it was started with. Every row
        // is playable: what ExoPlayer can't decode locally is transcoded by
        // the server (audioTrackIndex / externalAudio restart).
        var cur = CurrentAudio();
        var rows = new List<SheetRow>();
        if (_probeOk)
        {
            foreach (var a in _probeAudio)
            {
                var sel = new AudioSel(a.Index, null);
                var label = AudioLabel(a);
                rows.Add(new SheetRow(label, cur == sel, () => ChooseAudio(sel, label)));
            }
        }
        else
        {
            // No probe (server error): what ExoPlayer sees, minus what this
            // device can't decode.
            var media3 = NativePlayerService.Instance?.GetAudioTracks() ?? System.Array.Empty<TrackOption>();
            for (int i = 0; i < media3.Count; i++)
            {
                if (!media3[i].Supported) continue;
                var sel = new AudioSel(i, null);
                var label = media3[i].Label;
                rows.Add(new SheetRow(label, media3[i].Selected, () => ChooseAudio(sel, label)));
            }
        }
        foreach (var x in _extAudio)
        {
            var sel = new AudioSel(-1, x.Path);
            var label = ExternalLabel(x);
            rows.Add(new SheetRow(label, cur == sel, () => ChooseAudio(sel, label)));
        }
        if (!_catalogTask.IsCompleted) rows.Add(new SheetRow("Загрузка списка…", false, null));
        if (rows.Count == 0) rows.Add(new SheetRow("Оригинал", true, null));
        OpenListSheet("АУДИОДОРОЖКА", rows, AudioButton);
    }

    private void OpenSubsSheet()
    {
        // The file's tracks (probe) + sidecar files, not just what the current
        // stream carries: a Direct Stream / HLS stream has no subtitles of its
        // own. Bitmap tracks only where Media3 renders them (Direct Play).
        var cur = _sub ?? SubSel.Off;
        var rows = new List<SheetRow> { new("Выключены", cur.IsOff, () => ChooseSub(SubSel.Off)) };
        var media3 = _kind == StreamKind.DirectPlay
            ? NativePlayerService.Instance?.GetTextTracks() ?? System.Array.Empty<TrackOption>()
            : System.Array.Empty<TrackOption>();
        if (_probeOk)
        {
            foreach (var s in _probeSubs)
            {
                if (!SubListable(s, media3)) continue;
                var sel = new SubSel(s.Index, null);
                rows.Add(new SheetRow(SubLabel(sel), cur == sel, () => ChooseSub(sel)));
            }
        }
        else
        {
            // No probe (server error): Direct Play still lists what Media3 sees.
            var emb = media3.Where(t => t.Id?.Contains(NativePlayerService.SideloadTrackId) != true).ToList();
            for (int i = 0; i < emb.Count; i++)
            {
                if (!emb[i].Supported) continue;
                var sel = new SubSel(i, null);
                rows.Add(new SheetRow(emb[i].Label, cur == sel, () => ChooseSub(sel)));
            }
        }
        foreach (var x in _extSubs)
        {
            var sel = new SubSel(-1, x.Path);
            rows.Add(new SheetRow(ExternalLabel(x), cur == sel, () => ChooseSub(sel)));
        }
        if (!_catalogTask.IsCompleted) rows.Add(new SheetRow("Загрузка списка…", false, null));
        OpenListSheet("СУБТИТРЫ", rows, SubsButton);
    }

    // Aspect ratio ------------------------------------------------------------
    private string _aspect = "default";

    private void OpenAspectSheet()
    {
        // Same set as the web player: fit / cover / stretch, a crop of bars
        // baked into the picture, and forced display ratios — those STRETCH
        // the picture to the ratio (fitted), they don't crop it.
        var rows = new List<SheetRow>
        {
            new("Авто (вписать)",             _aspect is "default" or "", () => ApplyAspect("default", "Авто")),
            new("Заполнить экран (обрезать)", _aspect == "zoom",          () => ApplyAspect("zoom", "Заполнить")),
            new("Обрезать до 21:9 / 2.35",    _aspect == "crop:2.35",     () => ApplyAspect("crop:2.35", "обрезка до 2.35")),
            new("Растянуть на экран",         _aspect == "stretch",       () => ApplyAspect("stretch", "Растянуть")),
        };
        foreach (var r in new[] { "2.35:1", "21:9", "1.85:1", "16:9", "4:3" })
        {
            var ratio = r;
            rows.Add(new SheetRow($"{ratio} (растянуть)", _aspect == ratio, () => ApplyAspect(ratio, ratio)));
        }
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
        int srcH = _sourceHeight > 0 ? _sourceHeight : NativePlayerService.Instance?.GetState()?.ActualHeight ?? 0;
        int limit = srcH > 0 ? srcH : 1081;   // unknown source → offer 1080p and below

        var rows = new List<SheetRow>
        {
            new(srcH > 0 ? $"Оригинал · {srcH}p" : "Оригинал", _maxHeight == 0, () => ApplyQuality(0)),
        };
        foreach (var h in new[] { 1440, 1080, 720, 480 })
        {
            if (h >= limit) continue;   // only offer caps below the source
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

        // Focusing the row scrolls the ScrollView to it (native requestChildFocus).
        SheetScroll.IsVisible = true;
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
            TextColor = r.Selected ? Color.FromArgb("#f49157") : Colors.White,
            FontFamily = "sans-serif",
            FontSize = 15,
            MaxLines = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
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

    private bool IsRowFocused(int index)
    {
#if ANDROID
        return index >= 0 && index < SheetItems.Children.Count
            && SheetItems.Children[index] is View v
            && v.Handler?.PlatformView is Android.Views.View nv && nv.IsFocused;
#else
        return false;
#endif
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
    private bool _errorShown;

    /// <summary>A sticky message (start failure): stays up with the HUD until
    /// the next successful start or a retry.</summary>
    private void ShowError(string text)
    {
        _infoTimer?.Stop();
        _errorShown = true;
        InfoLine.Text = text;
        InfoLine.IsVisible = true;
        RevealHud();
    }

    private void ClearError()
    {
        _retryStart = null;
        if (!_errorShown) return;
        _errorShown = false;
        InfoLine.IsVisible = false;
    }

    private void FlashInfo(string text)
    {
        if (_errorShown) return;   // don't let a transient line replace the error
        InfoLine.Text = text;
        InfoLine.IsVisible = true;
        _infoTimer?.Stop();
        _infoTimer = Dispatcher.CreateTimer();
        _infoTimer.Interval = TimeSpan.FromSeconds(2.5);
        _infoTimer.IsRepeating = false;
        _infoTimer.Tick += (_, _) => InfoLine.IsVisible = false;
        _infoTimer.Start();
    }

    private long? _pendingSeekMs;
    private IDispatcherTimer? _seekTimer;

    private void OnSeekSettled(object? sender, EventArgs e)
    {
        if (_pendingSeekMs is not long t) return;
        _pendingSeekMs = null;
        if (_isDirectStream) _ = StartStreamAsync(t, _maxHeight);
        else NativePlayerService.Instance?.SeekAsync(Math.Max(0, t - _streamBaseMs));
    }

    /// <summary>Relative seek (±10 s buttons, ◀/▶ with the HUD hidden, ⏩/⏪,
    /// the system). <paramref name="feedback"/> shows the target on screen —
    /// for the presses made with the HUD (and its scrubber) out of sight.</summary>
    private void SeekBy(long deltaMs, bool feedback = false)
    {
        var st = NativePlayerService.Instance?.GetState();
        if (st is null) return;
        long dur = _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs;
        long absPos = _streamBaseMs + st.PositionMs;
        // Held / repeated presses on a server stream are coalesced into one
        // seek (below), so they accumulate onto the pending target.
        bool coalesce = _isDirectStream || _kind == StreamKind.Hls;
        long target = Math.Clamp((coalesce ? _pendingSeekMs ?? absPos : absPos) + deltaMs, 0,
                                 dur > 0 ? dur : long.MaxValue);
        if (feedback)
            FlashInfo($"{(deltaMs < 0 ? "◀◀" : "▶▶")}  {Fmt(target)}{(dur > 0 ? " / " + Fmt(dur) : "")}");
        // The /api/video remux pipe is NOT seekable — ExoPlayer.SeekTo snaps it
        // to 0 (the tester's "+10 resets to start"). Re-request the stream at
        // the target instead; PTS stay absolute via -output_ts_offset. On HLS
        // a seek past the produced segments restarts the server's ffmpeg there.
        if (coalesce)
        {
            // Every restart costs the server ffprobe + ffmpeg: accumulate held /
            // repeated presses and seek once, 450 ms after the last one; show
            // the target meanwhile.
            _pendingSeekMs = target;
            PositionLabel.Text = Fmt(target);
            if (dur > 0) Scrubber.Progress = Math.Clamp((double)target / dur, 0, 1);
            _seekTimer ??= Dispatcher.CreateTimer();
            _seekTimer.Stop();
            _seekTimer.Interval = TimeSpan.FromMilliseconds(450);
            _seekTimer.IsRepeating = false;
            _seekTimer.Tick -= OnSeekSettled;
            _seekTimer.Tick += OnSeekSettled;
            _seekTimer.Start();
            return;
        }
        // Back to the stream's own timeline (absolute for Direct Play and HLS —
        // the HLS playlist covers the whole run; the server seek-restarts ffmpeg
        // for segments before the start point).
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
        // A start still in flight must not begin playback after the page left.
        _closed = true;
        _startGen++;
        _startCts?.Cancel();
        if (NativePlayerService.Instance is { } nps) nps.TracksChanged -= OnPlayerTracksChanged;
        _timer?.Stop();
        _hudTimer?.Stop();
        _seekTimer?.Stop();
#if ANDROID
        _session?.Dispose();
        _session = null;
#endif
        var st = NativePlayerService.Instance?.GetState();
        if (st is not null && _streamFilePath == _filePath)
            RecordProgress(_streamBaseMs + st.PositionMs,
                           _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs);
        _ = NativePlayerService.Instance?.DetachAsync();
        if (_hlsToken is { } tok) { _hlsToken = null; StopHlsSession(tok); }
    }

    private async Task KeepAliveAsync(string token)
    {
        try { using var _ = await _http.PostAsync($"/api/hls/keepalive?token={Uri.EscapeDataString(token)}", null); }
        catch { /* transient — next tick retries */ }
    }

    private void StopHlsSession(string token) => _ = Task.Run(async () =>
    {
        try { using var _ = await _http.DeleteAsync($"/api/hls/{Uri.EscapeDataString(token)}"); }
        catch { /* GC reaps it anyway */ }
    });

    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms < 0 ? 0 : ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private sealed record StartResponse(
        string? DirectPlayUrl, string? DirectStreamUrl, string? ManifestUrl,
        double? TotalDuration, double? ResumeSec, string? Token, StartOutput? Output);

    private sealed record StartOutput(string? Plan, string? VideoCodec, int BitDepth, int Width, int Height);

    /// <summary>/api/hls/start error body (ProblemDetails + `code`).</summary>
    private sealed record StartProblem(string? Title, string? Detail, int? Status, string? Code);
}
