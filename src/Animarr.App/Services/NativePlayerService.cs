#if ANDROID
using Android.Content;
using Android.Views;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Hls;
using Microsoft.Extensions.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace Animarr.App.Services;

/// <summary>
/// The TV's native ExoPlayer (Media3), driven by <see cref="PlayerPage"/>:
/// prepares a Direct Play file / Direct Stream remux / HLS manifest into the
/// page's SurfaceView (its own compositor layer: 4K plane, HDR / DV, frame-
/// rate switching), side-loads subtitles, sizes the surface for the aspect
/// mode, and exposes state, tracks and track selection to the HUD. One
/// ExoPlayer per player page, built on the first play and released when the
/// page closes (decoder buffers live in native memory).
///
/// Cross-platform: on non-Android targets this is a no-op shell.
/// </summary>
public sealed class NativePlayerService : IDisposable
{
    public static NativePlayerService? Instance { get; private set; }
    public static void RegisterStaticInstance(NativePlayerService svc) => Instance = svc;

    private readonly ILogger<NativePlayerService> _logger;

#if ANDROID
    private IExoPlayer? _player;
    private readonly object _lock = new();

    // Pending play request stashed when PlayAsync runs BEFORE the SurfaceView's
    // Surface exists (the page adds the view, Android allocates its Surface a
    // moment later). SurfaceCreated drains it and completes the attach.
    private (string Url, long ResumeMs, bool PlayWhenReady, SideloadSubtitle? Subtitle, bool TextEnabled)? _pendingPlay;
    private bool _surfaceReady;

    // Diagnostic one-shot guards (logcat tag "Animarr.NativePlayer"). Used to
    // pin down why this device plays audio but no video: did ExoPlayer ever
    // report a video format (→ decoding works, problem is compositing) or a
    // PlayerError (→ codec init failed, message tells us what)?
    private bool _diagFmtLogged;
    private bool _diagErrLogged;
    private int  _diagPolls;

    // Lifecycle: remembers PlayWhenReady at OnPause so OnResume can restore
    // whatever the user had going (play→play; pause→pause).
    private bool _wasPlayingBeforePause;

    // ── Subtitles ───────────────────────────────────────────────────────────
    // ExoPlayer decodes text / bitmap subtitles into Cues but draws nothing
    // itself (there is no PlayerView here — the video goes straight into a
    // raw SurfaceView), so the TV showed no subtitles on any path. A Media3
    // SubtitleView laid over the video surface renders them, fed by onCues.
    private static global::AndroidX.Media3.UI.SubtitleView? s_subtitleView;

    /// <summary>Format id of the side-loaded subtitle track (SubtitleConfiguration
    /// id → Format.id), so the player page can tell it from embedded tracks.</summary>
    public const string SideloadTrackId = "animarr-sideload";

    /// <summary>Fired on the main thread whenever ExoPlayer's track list or
    /// selection changes (new stream prepared, track switched).</summary>
    public event Action? TracksChanged;

    public static void RegisterSubtitleView(global::AndroidX.Media3.UI.SubtitleView view)
    {
        s_subtitleView = view;
        // Readable from across the room: white, bold, black outline, no box;
        // a little above Media3's default size (0.0533 of the view height).
        // Embedded ASS font sizes are ignored (scripts authored for a
        // 1080-line canvas come out huge), positions / colours are kept.
        ApplySubtitleStyle(view);
        view.SetApplyEmbeddedFontSizes(false);
        // The bottom margin is the view's own padding, not Media3's
        // bottomPaddingFraction: that one only moves cues WITHOUT a line
        // position (SRT / embedded), while WebVTT's default "line -1" cues sat
        // on the very edge of the screen (TV overscan).
        view.SetBottomPaddingFraction(0f);
    }

    /// <summary>Size / background from the device's settings (CC sheet):
    /// white bold text with a black outline, on nothing, a translucent box
    /// or a black box.</summary>
    private static void ApplySubtitleStyle(global::AndroidX.Media3.UI.SubtitleView view)
    {
        var (bg, edge) = TvPlaybackPrefs.SubtitleBackground switch
        {
            1 => (global::Android.Graphics.Color.Argb(0x99, 0, 0, 0), 0 /* EDGE_TYPE_NONE */),
            2 => (global::Android.Graphics.Color.Black, 0),
            _ => (global::Android.Graphics.Color.Transparent, 1 /* EDGE_TYPE_OUTLINE */),
        };
        view.SetStyle(new global::AndroidX.Media3.UI.CaptionStyleCompat(
            global::Android.Graphics.Color.White, bg, global::Android.Graphics.Color.Transparent,
            edge, global::Android.Graphics.Color.Black, global::Android.Graphics.Typeface.DefaultBold));
        view.SetFractionalTextSize(TvPlaybackPrefs.SubtitleSize);
    }

    /// <summary>Re-apply the subtitle style after the settings changed.</summary>
    public static void RefreshSubtitleStyle()
    {
        if (s_subtitleView is { } v) ApplySubtitleStyle(v);
    }

    private static bool s_subtitlesRaised;

    /// <summary>Lift the subtitles above the HUD's bottom bar while it is up;
    /// also re-applies the margin after the view was (re)laid out.</summary>
    public static void SetSubtitlesRaised(bool raised)
    {
        s_subtitlesRaised = raised;
        if (s_subtitleView is not { } v || v.Width <= 0 || v.Height <= 0) return;
        int w = v.Width, h = v.Height;
        v.SetPadding(0, 0, 0, (int)(h * (raised ? 0.22f : 0.07f)));
        // A raw native child of a MAUI layout: nobody else lays it out again.
        v.Measure(global::Android.Views.View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly),
                  global::Android.Views.View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly));
        v.Layout(v.Left, v.Top, v.Left + w, v.Top + h);
    }

    public static void RelayoutSubtitles() => SetSubtitlesRaised(s_subtitlesRaised);

    private sealed class PlayerEvents : Java.Lang.Object, global::AndroidX.Media3.Common.IPlayerListener
    {
        public void OnCues(global::AndroidX.Media3.Common.Text.CueGroup cueGroup)
        {
            if (s_subtitleView is not { } view) return;
            // CueGroup.cues is a Guava ImmutableList (bound as an opaque type) —
            // it IS a java.util.List, so wrap the handle instead of copying.
            var cues = cueGroup.Cues;
            view.SetCues(cues is null
                ? new List<global::AndroidX.Media3.Common.Text.Cue>()
                : global::Android.Runtime.JavaList<global::AndroidX.Media3.Common.Text.Cue>.FromJniHandle(
                    cues.Handle, global::Android.Runtime.JniHandleOwnership.DoNotTransfer));
        }

        public void OnTracksChanged(global::AndroidX.Media3.Common.Tracks tracks)
            => Instance?.TracksChanged?.Invoke();

        // Fired before the first frame of every new size (new stream, a quality
        // switch, a mid-stream format change). On a Surface the decoder applies
        // the rotation itself and Media3 reports the size already rotated.
        public void OnVideoSizeChanged(global::AndroidX.Media3.Common.VideoSize videoSize)
            => Instance?.OnVideoSize(videoSize.Width, videoSize.Height, videoSize.PixelWidthHeightRatio, "listener");
    }

    // The player page's SurfaceView: the video on its own compositor layer
    // (no UI redraw per frame, a 4K plane, HDR / DV, frame-rate switching).
    private static SurfaceView? s_surfaceView;

    /// <summary>Called by the player page once its SurfaceView is in the view
    /// tree. A SurfaceHolder callback tells the service when the Surface
    /// exists — a PlayAsync before that waits in <see cref="_pendingPlay"/>.</summary>
    public static void RegisterSurfaceView(SurfaceView sv)
    {
        s_surfaceView = sv;
        sv.Holder!.AddCallback(new SvCallback());
    }
    private sealed class SvCallback : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => Instance?.OnSurfaceReady();
        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) { }
        public void SurfaceDestroyed(ISurfaceHolder holder) { if (Instance is { } s) s._surfaceReady = false; }
    }

    /// <summary>SurfaceView aspect: size + centre the view itself, the parent
    /// clips what overflows. Modes:
    /// <list type="bullet">
    ///   <item>"default" — fit (letterbox / pillarbox), undistorted;</item>
    ///   <item>"zoom" — cover the screen, undistorted, the overflow is cut;</item>
    ///   <item>"stretch" — the whole screen, distorted;</item>
    ///   <item>"16:9" / "2.35:1" … — forced display aspect: the picture is
    ///         stretched to that ratio, fitted;</item>
    ///   <item>"crop:auto" — the same with R = the picture inside the bars
    ///         the server detected in this file (/api/media-facts).</item>
    ///   <item>"crop:2.35:1" (any "crop:R", the web's vocabulary) — cut bars
    ///         baked into the picture: the frame covers an R box fitted in the
    ///         screen, so the centre 2.35:1 band of a taller frame fills the
    ///         width (a 4:3 frame with a scope film inside, a 2.35 screen); a
    ///         wider frame loses its sides.</item>
    /// </list></summary>
    private void ApplySurfaceAspect(SurfaceView sv)
    {
        try
        {
            if (sv.Parent is not global::Android.Views.View parent) return;
            int W = parent.Width, H = parent.Height;
            if (W <= 0 || H <= 0) return;
            var (w, h) = SurfaceSize(W, H);
            int l = (W - w) / 2, t = (H - h) / 2;
            if (sv.Width != w || sv.Height != h || sv.Left != l || sv.Top != t)
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"aspect {_aspectValue}: video {_vidW}x{_vidH} par={_vidPar:0.###} → surface {w}x{h} at {l},{t} in {W}x{H}");
            sv.Measure(global::Android.Views.View.MeasureSpec.MakeMeasureSpec(w, MeasureSpecMode.Exactly),
                       global::Android.Views.View.MeasureSpec.MakeMeasureSpec(h, MeasureSpecMode.Exactly));
            sv.Layout(l, t, l + w, t + h);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("Animarr.NativePlayer", $"ApplySurfaceAspect: {ex.Message}");
        }
    }

    /// <summary>Surface size for the current mode inside a W×H screen.</summary>
    private (int W, int H) SurfaceSize(int W, int H)
    {
        float videoAspect = _vidH > 0 && _vidW > 0 ? _vidW * _vidPar / _vidH : 0f;
        var mode = _aspectValue ?? "default";
        if (mode.StartsWith("crop:", StringComparison.Ordinal))
        {
            // The web's crop:R: the frame COVERS an R box fitted in the screen.
            // crop:auto: R = the picture inside the bars baked into this file
            // (/api/media-facts contentAspect); unknown yet → fit.
            float band = mode == "crop:auto" ? _contentAspect : ParseAspect(mode[5..]);
            if (videoAspect > 0f && band > 0f)
            {
                float boxW = Math.Min(W, H * band), boxH = boxW / band;
                // A frame taller than the box overflows above / below it (its
                // baked-in bars), a wider one left / right.
                return band > videoAspect
                    ? ((int)Math.Round(boxW), (int)Math.Round(boxW / videoAspect))
                    : ((int)Math.Round(boxH * videoAspect), (int)Math.Round(boxH));
            }
            mode = "default";
        }
        float target = mode switch
        {
            "stretch" => 0f,
            "zoom" or "" or "default" => videoAspect,
            _ => ParseAspect(mode),
        };
        if (target <= 0f) return (W, H);
        float view = (float)W / H;
        bool cover = mode == "zoom";
        return (target >= view) ^ cover
            ? (W, (int)Math.Round(W / target))
            : ((int)Math.Round(H * target), H);
    }

    /// <summary>Decoded picture geometry (display orientation: width/height
    /// already swapped for a 90°/270° rotation). The listener is the source; a
    /// stream it says nothing about (same size as the previous one) is taken
    /// from the polled Format. Re-sizes the surface on every change — it used
    /// to be sized once per prepare, keeping the previous episode's geometry
    /// for up to a second and missing a mid-stream change.</summary>
    private void OnVideoSize(int width, int height, float par, string source)
    {
        if (width <= 0 || height <= 0) return;   // VideoSize.UNKNOWN while preparing
        if (par <= 0f) par = 1f;
        _sizeKnown = true;
        if (width == _vidW && height == _vidH && System.Math.Abs(par - _vidPar) <= 0.001f) return;
        global::Android.Util.Log.Info("Animarr.NativePlayer", $"video size {width}x{height} par={par:0.###} ({source})");
        _vidW = width; _vidH = height; _vidPar = par;
        ApplyAspect();
    }

    /// <summary>The current stream's geometry has been reported (reset per prepare).</summary>
    private bool _sizeKnown;

    /// <summary>Hosted-activity OnPause hook. Stashes <c>PlayWhenReady</c> so
    /// <see cref="OnHostActivityResumed"/> can restore the user's intent —
    /// without this the player would keep ticking in the background, draining
    /// battery and stealing audio focus from foreground apps.</summary>
    public void OnHostActivityPaused()
    {
        lock (_lock)
        {
            if (_player is null) return;
            _wasPlayingBeforePause = _player.PlayWhenReady;
            _player.PlayWhenReady = false;
        }
    }
    /// <summary>Hosted-activity OnResume hook. Restores
    /// <c>PlayWhenReady</c> only if playback was active when we paused —
    /// keeps an explicit user pause from being undone on every Home tap.</summary>
    public void OnHostActivityResumed()
    {
        lock (_lock)
        {
            if (_player is null) return;
            if (_wasPlayingBeforePause) _player.PlayWhenReady = true;
        }
    }
    /// <summary>Hosted-activity OnDestroy hook. Releases ExoPlayer + frees
    /// decoder buffers. Without this, configuration-change rotates leak
    /// decoder memory across activity recreations.</summary>
    public void OnHostActivityDestroyed() => _ = DetachAsync();

    /// <summary>Hosted-activity OnStop: the window is gone from the screen —
    /// after a normal OnPause nothing plays any more; a picture-in-picture
    /// window the user closed still did (OnPause skips PiP): pause it, and
    /// don't resume it on the next OnResume.</summary>
    public void OnHostActivityStopped()
    {
        lock (_lock)
        {
            if (_player is null || !_player.PlayWhenReady) return;
            _player.PlayWhenReady = false;
            _wasPlayingBeforePause = false;
        }
    }

    /// <summary>The SurfaceView's Surface exists: drain a
    /// <see cref="_pendingPlay"/> stashed by an early PlayAsync.</summary>
    private void OnSurfaceReady()
    {
        _surfaceReady = true;
        var pending = _pendingPlay;
        if (pending.HasValue)
        {
            _pendingPlay = null;
            // Direct attach — don't recurse into PlayAsync (would re-queue).
            var p = pending.Value;
            DoPlay(p.Url, p.ResumeMs, p.PlayWhenReady, p.Subtitle, p.TextEnabled);
        }
        else if (_player is not null && s_surfaceView is not null)
        {
            // Re-attach surface to existing player (e.g. after lifecycle
            // bounce destroyed + recreated the surface).
            AttachSurface();
        }
    }

    /// <summary>
    /// Bind ExoPlayer's video output to the SurfaceView. ExoPlayer registers
    /// its own SurfaceHolder.Callback and renders as soon as the Surface is
    /// created, so this is safe to call before the surface is ready — ExoPlayer
    /// waits. Using the high-level SetVideoSurfaceView (vs a raw Surface) means
    /// ExoPlayer also tracks surface-destroyed/created across lifecycle bounces.
    /// </summary>
    private void AttachSurface()
    {
        if (_player is null || s_surfaceView is null) return;
        try { _player.SetVideoSurfaceView(s_surfaceView); }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error("Animarr.NativePlayer", $"AttachSurface failed: {ex.Message}");
        }
    }
#else
    // The same surface on the other targets (the native player page is
    // Android-only; these keep the shared page code compiling).
    public const string SideloadTrackId = "animarr-sideload";
#pragma warning disable CS0067 // never raised off Android
    public event Action? TracksChanged;
#pragma warning restore CS0067
    public static void SetSubtitlesRaised(bool raised) { }
    public static void RelayoutSubtitles() { }
    public static void RefreshSubtitleStyle() { }
#endif

    public NativePlayerService(ILogger<NativePlayerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start playback of <paramref name="url"/> (either a Direct Play
    /// <c>/api/file</c> URL or an HLS manifest URL — ExoPlayer auto-detects).
    /// <paramref name="playWhenReady"/> false prepares the stream paused — a
    /// restart (quality / track switch, remux seek) keeps the user's pause.
    /// <paramref name="subtitle"/> side-loads a WebVTT track with the stream
    /// (the server's conversion of an embedded or sidecar subtitle — a Direct
    /// Stream / HLS stream carries none); <paramref name="textEnabled"/> false
    /// keeps every text track off, so Media3 doesn't auto-pick a
    /// default/forced one behind the player's back.
    /// </summary>
    public Task PlayAsync(string url, long resumeMs = 0, bool playWhenReady = true,
                          SideloadSubtitle? subtitle = null, bool textEnabled = false)
    {
#if ANDROID
        try
        {
            // Stash the request first — the actual attach happens once the
            // SurfaceView's Surface exists. Without this we'd race the
            // surface allocation and attach to nothing (audio, black video).
            _pendingPlay = (url, resumeMs, playWhenReady, subtitle, textEnabled);
            _logger.LogInformation("NativePlayer: PlayAsync url={Url} resume={ResumeMs}ms (queued, awaiting surface)",
                url, resumeMs);

            // A Surface that is already valid (a previous play on this page
            // left it hot) is drained right away; otherwise SurfaceCreated does.
            if (s_surfaceView is not null)
            {
                s_surfaceView.Post(new Java.Lang.Runnable(() =>
                {
                    s_surfaceView.KeepScreenOn = true;
                    if (s_surfaceView.Holder?.Surface?.IsValid == true) OnSurfaceReady();
                }));
            }
            else
            {
                _logger.LogWarning("NativePlayer: SurfaceView not registered yet; play starts once the page registers it");
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "NativePlayer.PlayAsync failed for {Url}", url);
        }
#endif
        return Task.CompletedTask;
    }

#if ANDROID
    /// <summary>
    /// Actual ExoPlayer attach + prepare. Called only when the surface is
    /// confirmed ready — either directly from PlayAsync (subsequent plays
    /// with kept-warm surface) or from <see cref="OnSurfaceReady"/> on the
    /// first cold-start play. Idempotent against rapid re-entries because
    /// we drain <c>_pendingPlay</c> before doing the work.
    /// </summary>
    private void DoPlay(string url, long resumeMs, bool playWhenReady,
                        SideloadSubtitle? subtitle = null, bool textEnabled = false)
    {
        var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
        if (ctx is null)
        {
            _logger.LogWarning("NativePlayer.DoPlay: AppContext null");
            return;
        }
        try
        {
            lock (_lock)
            {
                _player ??= BuildPlayer(ctx);
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"DoPlay url={url}" + (subtitle is null ? "" : $" sub={subtitle.Url}"));
                AttachSurface();
                var builder = new MediaItem.Builder().SetUri(url)!;
                if (subtitle is not null)
                {
                    var subConfig = new MediaItem.SubtitleConfiguration.Builder(global::Android.Net.Uri.Parse(subtitle.Url)!)
                        .SetMimeType(MimeTypes.TextVtt)!        // /api/subtitle?format=webvtt = text/vtt
                        .SetLanguage(subtitle.Language ?? "und")!
                        .SetLabel(subtitle.Label)!
                        .SetId(SideloadTrackId)!
                        .SetSelectionFlags(global::AndroidX.Media3.Common.C.SelectionFlagDefault)!
                        .Build()!;
                    builder.SetSubtitleConfigurations(new List<MediaItem.SubtitleConfiguration> { subConfig });
                }
                // Text selection is the player page's call, not Media3's
                // default/forced heuristics: off, or on with no override (the
                // page picks the exact track once OnTracksChanged lists them).
                var tsp = _player.TrackSelectionParameters.BuildUpon();
                tsp.ClearOverridesOfType(global::AndroidX.Media3.Common.C.TrackTypeText);
                tsp.SetTrackTypeDisabled(global::AndroidX.Media3.Common.C.TrackTypeText, !textEnabled);
                _player.TrackSelectionParameters = tsp.Build();
                s_subtitleView?.SetCues(new List<global::AndroidX.Media3.Common.Text.Cue>());
                _player.SetMediaItem(builder.Build());
                _player.Prepare();
                if (resumeMs > 0) _player.SeekTo(resumeMs);
                _player.PlayWhenReady = playWhenReady;
                // Reset diag + aspect guards so GetState re-logs format/error
                // and re-sizes the SurfaceView for this play.
                _diagFmtLogged = false; _diagErrLogged = false; _diagPolls = 0;
                // The previous stream's geometry stays until this one reports
                // its own (its last frame is still on the surface until then).
                _sizeKnown = false;
                _logger.LogInformation("NativePlayer.DoPlay: attached + prepared {Url} resume={ResumeMs}ms",
                    url, resumeMs);
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"DoPlay prepared, PlayWhenReady={playWhenReady}");
            }
            ApplyAspect();  // the aspect mode for the current geometry (the new size follows)
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "NativePlayer.DoPlay failed");
        }
    }
#endif

    public Task PauseAsync()
    {
#if ANDROID
        try { lock (_lock) { if (_player is not null) _player.PlayWhenReady = false; } }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.PauseAsync"); }
#endif
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
#if ANDROID
        try { lock (_lock) { if (_player is not null) _player.PlayWhenReady = true; } }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.ResumeAsync"); }
#endif
        return Task.CompletedTask;
    }

    public Task SeekAsync(long positionMs)
    {
#if ANDROID
        try { lock (_lock) { _player?.SeekTo(positionMs); } }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.SeekAsync"); }
#endif
        return Task.CompletedTask;
    }

    /// <summary>Output volume on a 0..1 scale (ExoPlayer's own gain; mute = 0),
    /// times the loudness levelling's attenuation.</summary>
    public Task SetVolumeAsync(float volume)
    {
#if ANDROID
        _userVolume = System.Math.Clamp(volume, 0f, 1f);
        ApplyGain();
#endif
        return Task.CompletedTask;
    }

    /// <summary>Loudness levelling for the current track (dB; 0 = none): a
    /// negative gain lowers the player volume, a positive one goes through a
    /// LoudnessEnhancer on the player's audio session, capped at +9 dB (the
    /// server already limits it to keep the true peak ≤ -1 dBTP). Bitstream
    /// passthrough audio isn't touched by either.</summary>
    public void SetLoudnessGain(double gainDb)
    {
#if ANDROID
        _gainDb = System.Math.Clamp(gainDb, -30, MaxBoostDb);
        ApplyGain();
#endif
    }

#if ANDROID
    private const double MaxBoostDb = 9;
    private float _userVolume = 1f;
    private double _gainDb;
    private global::Android.Media.Audiofx.LoudnessEnhancer? _enhancer;
    private int _enhancerSession;

    private void ApplyGain()
    {
        try
        {
            lock (_lock)
            {
                if (_player is null) return;
                float cut = _gainDb < 0 ? (float)System.Math.Pow(10, _gainDb / 20) : 1f;
                _player.Volume = _userVolume * cut;
                int session = _player.AudioSessionId;
                if (_gainDb > 0.05 && session > 0)
                {
                    if (_enhancer is null || _enhancerSession != session)
                    {
                        _enhancer?.Release();
                        _enhancer = new global::Android.Media.Audiofx.LoudnessEnhancer(session);
                        _enhancerSession = session;
                    }
                    _enhancer.SetTargetGain((int)System.Math.Round(_gainDb * 100));   // millibels
                    _enhancer.SetEnabled(true);
                }
                else if (_enhancer is not null)
                {
                    _enhancer.SetEnabled(false);
                }
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"gain {_gainDb:+0.0;-0.0;0} dB → volume {_player.Volume:0.###}, enhancer {(_gainDb > 0.05 ? $"+{_gainDb:0.0} dB on session {session}" : "off")}");
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.ApplyGain"); }
    }
#endif

    /// <summary>Playback speed (0.5–2×, pitch kept — Media3's Sonic time
    /// stretch). The player keeps it across prepares; a new player takes it
    /// from <see cref="TvPlaybackPrefs"/>. Audio passed through to a receiver
    /// (AC-3 / DTS bitstream) can't be stretched: Media3 then plays at 1×.</summary>
    public Task SetSpeedAsync(float speed)
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                if (_player is not null)
                    _player.PlaybackParameters = new PlaybackParameters(System.Math.Clamp(speed, 0.5f, 2f));
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.SetSpeedAsync"); }
#endif
        return Task.CompletedTask;
    }

    /// <summary>The current file's picture aspect without its baked-in bars
    /// (the server's crop detection), for "crop:auto"; 0 = unknown.</summary>
    public void SetContentAspect(float aspect)
    {
#if ANDROID
        if (Math.Abs(aspect - _contentAspect) < 0.001f) return;
        _contentAspect = aspect;
        if (_aspectValue == "crop:auto") ApplyAspect();
#endif
    }

    /// <summary>Aspect mode of the video surface (see ApplySurfaceAspect for
    /// the modes); re-applied on every new decoded size.</summary>
    public Task SetAspectRatioAsync(string value)
    {
#if ANDROID
        _aspectValue = value;
        ApplyAspect();
#endif
        return Task.CompletedTask;
    }

#if ANDROID
    private string _aspectValue = "default";
    /// <summary>Display aspect of the current file's picture without its
    /// baked-in bars (0 = unknown) — what "crop:auto" crops to.</summary>
    private float _contentAspect;
    // Decoded video geometry in display orientation (OnVideoSize).
    private int   _vidW;
    private int   _vidH;
    private float _vidPar = 1f;

    /// <summary>Size the surface for the aspect mode and the current geometry
    /// (posted to the UI thread). Re-run on: aspect change, a new decoded
    /// size, a page size change.</summary>
    internal void ApplyAspect()
    {
        if (s_surfaceView is { } sv) sv.Post(() => ApplySurfaceAspect(sv));
    }

    private static float ParseAspect(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        // Accepts "W:H" ("21:9") or "W/H" ("21/9") or a decimal ("2.35").
        var s = value.Replace('/', ':').Trim();
        var i = s.IndexOf(':');
        if (i > 0 && i < s.Length - 1)
        {
            if (float.TryParse(s[..i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w) &&
                float.TryParse(s[(i + 1)..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var h) &&
                h > 0)
            {
                return w / h;
            }
        }
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r))
            return r;
        return 0;
    }
#endif

    /// <summary>
    /// Tear down the ExoPlayer instance + free decoder buffers. Called on
    /// player close. Idempotent.
    /// </summary>
    public Task DetachAsync()
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                // A new player page starts on a fresh surface: no geometry yet.
                _sizeKnown = false; _vidW = _vidH = 0; _vidPar = 1f;
                try { _enhancer?.Release(); } catch { }
                _enhancer = null; _gainDb = 0; _userVolume = 1f;
                ResetFrameRate();
                // Release() drops the decoder buffers (~50-200MB on a 4K HDR
                // stream — lives in C++ JNI heap, not the .NET GC reachable
                // heap). Without this call the buffers leak between play
                // sessions until process death. Verified by checking
                // `adb shell dumpsys meminfo com.animarr.app` after multiple
                // detach/attach cycles.
                if (_player is not null)
                {
                    _logger.LogInformation("NativePlayer: releasing ExoPlayer decoder buffers");
                    _player.Release();
                }
                _player = null;
            }
            // Non-playback pages get the OS's normal dim / screensaver timing back.
            s_surfaceView?.Post(new Java.Lang.Runnable(() => { if (s_surfaceView is { } v) v.KeepScreenOn = false; }));
            _pendingPlay = null;
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.DetachAsync"); }
#endif
        return Task.CompletedTask;
    }

#if ANDROID
    /// <summary>
    /// Build a fresh ExoPlayer with HLS support enabled. Single-variant HLS
    /// from our server doesn't need explicit MediaSource construction —
    /// ExoPlayer picks the right MediaSource.Factory from the URL extension
    /// automatically when the Hls module is present in the classpath.
    /// </summary>
    private static IExoPlayer BuildPlayer(Context ctx)
    {
        // The Xamarin Android binding flattens Java's nested
        // `androidx.media3.exoplayer.ExoPlayer.Builder` into a top-level type
        // `ExoPlayerBuilder`. Builder + audio-focus wiring follows AndroidX
        // Media3's documented "media playback" recipe:
        //   • AudioAttributes(usage=MEDIA, contentType=MOVIE) → tells AudioFlinger
        //     to treat us as media (ducks notification sounds correctly).
        //   • handleAudioFocus=true → ExoPlayer auto-pauses when something else
        //     (phone call, alarm, voice assistant) grabs focus, and resumes when
        //     focus comes back. Without this we'd talk over phone calls.
        var attrs = new AudioAttributes.Builder()
            .SetUsage(global::AndroidX.Media3.Common.C.UsageMedia)
            .SetContentType(global::AndroidX.Media3.Common.C.AudioContentTypeMovie)
            .Build();
        // Default LoadControl targets ~131 MB for a video track (2000 × 64 KB) —
        // on a 3 GB TV that is the single biggest Java-heap consumer during
        // high-bitrate Direct Play. 64 MB still holds ~50 s of a 10 Mbit/s file
        // (the 50 s time cap wins) and ~6 s of an 80 Mbit/s remux on the LAN.
        var load = new global::AndroidX.Media3.ExoPlayer.DefaultLoadControl.Builder()
            .SetTargetBufferBytes(64 * 1024 * 1024)!
            .SetPrioritizeTimeOverSizeThresholds(false)!
            .Build();
        // Extension renderers ON (not PREFER): the platform MediaCodec decoders
        // and HDMI passthrough still come first; the FFmpeg audio renderer
        // (org.jellyfin.media3:media3-ffmpeg-decoder) only takes AC-3 / E-AC-3 /
        // DTS / TrueHD / … when the SoC has no decoder for them. Decoder
        // fallback lets a codec that fails to init fall through to the next one.
        // Passthrough "Авто" is Media3's default (the HDMI sink's reported
        // AudioCapabilities); "Выкл" builds the audio sink with stereo-PCM-only
        // capabilities, so everything is decoded on the device.
        bool pcmOnly = TvPlaybackPrefs.PassthroughOff;
        var renderers = (pcmOnly ? new PcmOnlyRenderersFactory(ctx) : new global::AndroidX.Media3.ExoPlayer.DefaultRenderersFactory(ctx))
            .SetExtensionRendererMode(global::AndroidX.Media3.ExoPlayer.DefaultRenderersFactory.ExtensionRendererModeOn)!
            .SetEnableDecoderFallback(true)!;
        var builder = new ExoPlayerBuilder(ctx, renderers)
            .SetAudioAttributes(attrs, handleAudioFocus: true)!
            .SetLoadControl(load)!;
        var player = (IExoPlayer)builder.Build()!;
        // Frame-rate matching sets the surface's rate itself (ALWAYS, which may
        // switch the display mode); Media3's own seamless-only vote would
        // compete with it.
        bool matchFps = TvPlaybackPrefs.MatchFrameRate && OperatingSystem.IsAndroidVersionAtLeast(31);
        if (matchFps) player.VideoChangeFrameRateStrategy = global::AndroidX.Media3.Common.C.VideoChangeFrameRateStrategyOff;
        // The remembered speed (Этап 7): kept by the player across prepares.
        if (Math.Abs(TvPlaybackPrefs.Speed - 1f) > 0.01f)
            player.PlaybackParameters = new PlaybackParameters(TvPlaybackPrefs.Speed);
        global::Android.Util.Log.Info("Animarr.NativePlayer",
            $"player: passthrough={(pcmOnly ? "off (PCM only)" : "auto")} matchFrameRate={TvPlaybackPrefs.MatchFrameRate} speed={TvPlaybackPrefs.Speed:0.##}");
        // Cues → SubtitleView, track changes → the player page.
        player.AddListener(new PlayerEvents());
        return player;
    }

    /// <summary>Audio passthrough "Выкл": the default sink, but told the output
    /// takes stereo PCM only (AudioCapabilities.DEFAULT_AUDIO_CAPABILITIES), so
    /// no encoded format is ever passed through.</summary>
    private sealed class PcmOnlyRenderersFactory : global::AndroidX.Media3.ExoPlayer.DefaultRenderersFactory
    {
        public PcmOnlyRenderersFactory(Context ctx) : base(ctx) { }

        protected override global::AndroidX.Media3.ExoPlayer.Audio.IAudioSink? BuildAudioSink(
            Context context, bool enableFloatOutput, bool enableAudioOutputPlaybackParameters)
            => new global::AndroidX.Media3.ExoPlayer.Audio.DefaultAudioSink.Builder(context)
                .SetAudioCapabilities(global::AndroidX.Media3.ExoPlayer.Audio.AudioCapabilities.DefaultAudioCapabilities)!
                .SetEnableFloatOutput(enableFloatOutput)!
                .SetEnableAudioOutputPlaybackParameters(enableAudioOutputPlaybackParameters)!
                .Build();
    }

    // ── Frame-rate matching (setting, off by default) ──────────────────────
    // API 31+: the surface votes for the video's rate with
    // CHANGE_FRAME_RATE_ALWAYS — the system may switch the display mode even
    // when that isn't seamless. API 23–30 (the projector is 30): the window's
    // preferredDisplayModeId = a mode of the same resolution whose refresh
    // rate is a whole multiple of the frame rate. Reset when the player goes.

    private float _appliedFps;
    private float _fpsHint;

    private void MaybeMatchFrameRate(float fps)
    {
        if (!TvPlaybackPrefs.MatchFrameRate || fps <= 1f || Math.Abs(fps - _appliedFps) < 0.01f) return;
        _appliedFps = fps;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                if (s_surfaceView?.Holder?.Surface is { IsValid: true } surface)
                {
                    surface.SetFrameRate(fps, (int)SurfaceFrameRateCompatibility.FixedSource, (int)SurfaceChangeFrameRate.Always);
                    global::Android.Util.Log.Info("Animarr.NativePlayer", $"frame rate: surface → {fps:0.###} fps (ALWAYS)");
                }
                return;
            }
            if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity?.Window is not { } window) return;
#pragma warning disable CA1422 // Activity.Display is API 30+; DefaultDisplay works on 23–30
            var display = activity.WindowManager?.DefaultDisplay;
#pragma warning restore CA1422
            if (display?.GetMode() is not { } current) return;
            var mode = PickDisplayMode(display.GetSupportedModes() ?? Array.Empty<Display.Mode>(), current, fps);
            if (mode is null)
            {
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"frame rate: no {current.PhysicalWidth}x{current.PhysicalHeight} mode for {fps:0.###} fps");
                return;
            }
            var lp = window.Attributes!;
            lp.PreferredDisplayModeId = mode.ModeId;
            window.Attributes = lp;
            global::Android.Util.Log.Info("Animarr.NativePlayer",
                $"frame rate: {fps:0.###} fps → display mode {mode.ModeId} ({mode.PhysicalWidth}x{mode.PhysicalHeight}@{mode.RefreshRate:0.###})");
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("Animarr.NativePlayer", $"frame rate: {ex.Message}");
        }
    }

    /// <summary>Same resolution, refresh rate a whole multiple of the frame
    /// rate: exact matches (23.976 on 23.976 / 47.952 Hz) before near ones
    /// (23.976 on 24 Hz, within 0.2 %), then the lowest multiple.</summary>
    private static Display.Mode? PickDisplayMode(Display.Mode[] modes, Display.Mode current, float fps)
    {
        Display.Mode? best = null;
        (int Bucket, int K) bestKey = (int.MaxValue, int.MaxValue);
        foreach (var m in modes)
        {
            if (m.PhysicalWidth != current.PhysicalWidth || m.PhysicalHeight != current.PhysicalHeight) continue;
            int k = (int)Math.Round(m.RefreshRate / fps);
            if (k < 1) continue;
            double err = Math.Abs(m.RefreshRate - k * fps) / m.RefreshRate;
            if (err > 0.002) continue;
            var key = (err > 0.0005 ? 1 : 0, k);
            if (key.CompareTo(bestKey) < 0) { best = m; bestKey = key; }
        }
        return best;
    }

    /// <summary>Back to the system's choice when the player goes.</summary>
    private void ResetFrameRate()
    {
        if (_appliedFps == 0) return;
        _appliedFps = 0;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                if (s_surfaceView?.Holder?.Surface is { IsValid: true } surface)
                    surface.SetFrameRate(0, (int)SurfaceFrameRateCompatibility.Default, (int)SurfaceChangeFrameRate.OnlyIfSeamless);
                return;
            }
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window is { } window && window.Attributes is { } lp)
            {
                lp.PreferredDisplayModeId = 0;
                window.Attributes = lp;
            }
        }
        catch { }
    }
#endif

    /// <summary>The current file's frame rate from the server's probe — used
    /// for frame-rate matching when the container doesn't carry one in the
    /// Format (Media3's Matroska extractor never sets it). 0 = unknown.</summary>
    public void SetFrameRateHint(float fps)
    {
#if ANDROID
        _fpsHint = fps;
#endif
    }

    /// <summary>
    /// Live player state for the HUD's tick (once a second): position,
    /// duration, play state, error, the decoded video format. Also the
    /// fallback geometry / frame-rate source (see OnVideoSize).
    /// </summary>
    public NativePlayerState GetState()
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                if (_player is null) return NativePlayerState.Empty;
                var pos = _player.CurrentPosition;
                var dur = _player.Duration;
                if (dur < 0) dur = 0;  // C.TIME_UNSET sentinel → not yet known
                var state = _player.PlaybackState;
                // Player.STATE_IDLE=1, STATE_BUFFERING=2, STATE_READY=3, STATE_ENDED=4
                bool ended    = state == 4;
                bool playing  = _player.PlayWhenReady && state == 3;
                bool buffering = state == 2;
                // PlayerError non-null = fatal until the next prepare; the page
                // climbs its fallback rungs on it.
                string? error = null, errorCode = null, errorTrack = null;
                try
                {
                    if (_player.PlayerError is global::AndroidX.Media3.Common.PlaybackException pe)
                    {
                        error = pe.Message;
                        errorCode = pe.ErrorCodeName;
                        // Which decoder failed (the server's failure feedback
                        // steps the video plan or transcodes that audio codec).
                        var mime = pe is global::AndroidX.Media3.ExoPlayer.ExoPlaybackException xe
                                   && xe.Type == global::AndroidX.Media3.ExoPlayer.ExoPlaybackException.TypeRenderer
                            ? xe.RendererFormat?.SampleMimeType : null;
                        errorTrack = global::AndroidX.Media3.Common.MimeTypes.IsAudio(mime) ? "audio"
                                   : global::AndroidX.Media3.Common.MimeTypes.IsVideo(mime) ? "video"
                                   // AudioTrack init / write failures carry no renderer format.
                                   : pe.ErrorCode is 5001 or 5002 ? "audio" : null;
                    }
                }
                catch { }
                // VideoFormat is what the player actually decodes, which can
                // differ from the source (a capped re-encode, a tone-mapped
                // HDR source).
                string actualCodec = string.Empty;
                int actualBitDepth = 0;
                int actualWidth = 0, actualHeight = 0;
                try
                {
                    var fmt = _player.VideoFormat;
                    if (fmt is not null)
                    {
                        actualCodec     = NormalizeCodec(fmt.SampleMimeType);
                        actualWidth     = fmt.Width;
                        actualHeight    = fmt.Height;
                        // Fallback for a stream the size listener said nothing
                        // about: the Format, turned to display orientation
                        // (Format sizes are as encoded, RotationDegrees apart).
                        if (!_sizeKnown && actualWidth > 0 && actualHeight > 0)
                        {
                            float par = 1f;
                            try { par = fmt.PixelWidthHeightRatio; } catch { }
                            if (par <= 0f) par = 1f;
                            bool turned = fmt.RotationDegrees is 90 or 270;
                            OnVideoSize(turned ? actualHeight : actualWidth, turned ? actualWidth : actualHeight,
                                        turned ? 1f / par : par, $"format rot={fmt.RotationDegrees}");
                        }
                        // Polled on the UI thread (the HUD tick): the display
                        // follows the video's frame rate when that's enabled
                        // (the container's, else the server probe's).
                        MaybeMatchFrameRate(fmt.FrameRate > 1f ? fmt.FrameRate : _fpsHint);
                        actualBitDepth = BitDepthOf(fmt);
                    }
                }
                catch { /* VideoFormat not available before first frame */ }

                // ── Diagnostics (logcat "Animarr.NativePlayer") ──────────
                // Decides surface-vs-codec for the "audio but no video" bug.
                _diagPolls++;
                if (!_diagErrLogged && !string.IsNullOrEmpty(error))
                {
                    _diagErrLogged = true;
                    global::Android.Util.Log.Error("Animarr.NativePlayer",
                        $"PlayerError: {error}  (state={state})");
                }
                if (!_diagFmtLogged && actualWidth > 0)
                {
                    _diagFmtLogged = true;
                    global::Android.Util.Log.Info("Animarr.NativePlayer",
                        $"VideoFormat decoded: {actualCodec} {actualWidth}x{actualHeight} " +
                        $"{(actualBitDepth > 0 ? actualBitDepth + "-bit" : "bit depth unknown")}");
                }
                // At ~250ms/poll, poll 20 ≈ 5s in. If still no format and no
                // error, the video renderer never started a codec — points at
                // a missing/invalid surface or no video track selected.
                if (_diagPolls == 20 && !_diagFmtLogged && !_diagErrLogged)
                {
                    global::Android.Util.Log.Warn("Animarr.NativePlayer",
                        $"~5s in: state={state} playWhenReady={_player.PlayWhenReady} " +
                        $"no VideoFormat, no PlayerError → video renderer idle " +
                        $"(no surface or no video track)");
                }

                return new NativePlayerState(pos, dur, playing, ended, buffering,
                    error, actualCodec, actualBitDepth, actualWidth, actualHeight,
                    _player.PlayWhenReady, errorCode, errorTrack);
            }
        }
        catch { return NativePlayerState.Empty; }
#else
        return NativePlayerState.Empty;
#endif
    }

#if ANDROID
    /// <summary>Bit depth of the decoded stream: the ColorInfo the extractor
    /// read from the bitstream (SPS / codec config), else the RFC 6381 codecs
    /// string — hvc1 profile 2 (Main10), avc1 High10, vp09 / av01 carry the
    /// depth — else 0 (unknown; the page falls back to the server's probe).
    /// It used to be guessed from the colour space (BT.2020 ⇒ 10-bit).</summary>
    private static int BitDepthOf(global::AndroidX.Media3.Common.Format fmt)
    {
        try
        {
            if (fmt.ColorInfo is { IsBitdepthValid: true } ci && ci.LumaBitdepth > 0) return ci.LumaBitdepth;
        }
        catch { }
        var parts = (fmt.Codecs ?? "").Split('.');
        if (parts.Length < 2) return 0;
        switch (parts[0].ToLowerInvariant())
        {
            case "hvc1" or "hev1" or "dvh1" or "dvhe":
                // general_profile_space? + general_profile_idc: 1 Main, 2 Main10.
                var prof = parts[1].TrimStart('A', 'B', 'C', 'a', 'b', 'c');
                return prof == "2" ? 10 : prof == "1" ? 8 : 0;
            case "avc1" or "avc3":
                // profile_idc (hex): 0x6E High10, 0x7A High422, 0xF4 High444.
                if (parts[1].Length < 2) return 0;
                return parts[1][..2].ToUpperInvariant() switch { "6E" or "7A" or "F4" => 10, _ => 8 };
            case "vp09" when parts.Length >= 4:
                return int.TryParse(parts[3], out var vp) ? vp : 0;
            case "av01" when parts.Length >= 4:
                return int.TryParse(parts[3], out var av) ? av : 0;
        }
        return 0;
    }

    /// <summary>Map ExoPlayer's MIME-type strings to the short codec slugs
    /// the HUD plashka already understands (h264, hevc, av1, vp9, ...).</summary>
    private static string NormalizeCodec(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return string.Empty;
        var s = mime.ToLowerInvariant();
        if (s.Contains("avc"))  return "h264";
        if (s.Contains("hevc") || s.Contains("h265")) return "hevc";
        if (s.Contains("av1"))  return "av1";
        if (s.Contains("vp9"))  return "vp9";
        if (s.Contains("vp8"))  return "vp8";
        return s;
    }
#endif

    // ── Track selection (audio / subtitles / quality) ───────────────────────
    // Reads the tracks ExoPlayer parsed from the current stream so the native
    // HUD can build selection menus, and applies a choice live through
    // TrackSelectionParameters overrides (no MediaItem rebuild for embedded
    // tracks; a side-loaded subtitle is a re-prepare through PlayAsync).

    /// <summary>Audio tracks embedded in the current stream.</summary>
    public IReadOnlyList<TrackOption> GetAudioTracks()
    {
#if ANDROID
        return GetTracks(global::AndroidX.Media3.Common.C.TrackTypeAudio);
#else
        return System.Array.Empty<TrackOption>();
#endif
    }

    /// <summary>Subtitle / caption tracks embedded in the current stream.</summary>
    public IReadOnlyList<TrackOption> GetTextTracks()
    {
#if ANDROID
        return GetTracks(global::AndroidX.Media3.Common.C.TrackTypeText);
#else
        return System.Array.Empty<TrackOption>();
#endif
    }

    /// <summary>Video / quality renditions in the current stream.</summary>
    public IReadOnlyList<TrackOption> GetVideoTracks()
    {
#if ANDROID
        return GetTracks(global::AndroidX.Media3.Common.C.TrackTypeVideo);
#else
        return System.Array.Empty<TrackOption>();
#endif
    }

#if ANDROID
    private IReadOnlyList<TrackOption> GetTracks(int trackType)
    {
        var list = new List<TrackOption>();
        try
        {
            lock (_lock)
            {
                if (_player is null) return list;
                var tracks = _player.CurrentTracks;
                if (tracks is null) return list;
                var groups = GetGroups(tracks);
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    if (g.Type != trackType) continue;
                    for (int ti = 0; ti < g.Length; ti++)
                    {
                        global::AndroidX.Media3.Common.Format fmt;
                        try { fmt = g.GetTrackFormat(ti); } catch { continue; }
                        if (fmt is null) continue;
                        bool selected, supported;
                        try { selected = g.IsTrackSelected(ti); } catch { selected = false; }
                        try { supported = g.IsTrackSupported(ti); } catch { supported = true; }
                        int flags = fmt.SelectionFlags;
                        list.Add(new TrackOption(gi, ti, LabelFor(trackType, fmt, ti), selected,
                            Language: fmt.Language, Name: fmt.Label, Id: fmt.Id,
                            Codecs: fmt.Codecs ?? fmt.SampleMimeType, Channels: fmt.ChannelCount,
                            Supported: supported,
                            Forced: (flags & global::AndroidX.Media3.Common.C.SelectionFlagForced) != 0,
                            Default: (flags & global::AndroidX.Media3.Common.C.SelectionFlagDefault) != 0));
                    }
                }
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.GetTracks"); }
        return list;
    }

    /// <summary>
    /// Read <c>Tracks.getGroups()</c> via JNI. The managed accessor is absent
    /// from this binding: the Java method returns a Guava
    /// <c>ImmutableList&lt;Group&gt;</c>, an unbound type, so the generator drops
    /// it. We call it directly (the result IS a <c>java.util.List</c>) and wrap
    /// each element back into the fully-bound <c>Tracks.Group</c> peer, which DOES
    /// expose Type/Length/GetTrackFormat/IsTrackSelected/MediaTrackGroup.
    /// </summary>
    private static List<global::AndroidX.Media3.Common.Tracks.Group> GetGroups(
        global::AndroidX.Media3.Common.Tracks tracks)
    {
        var result = new List<global::AndroidX.Media3.Common.Tracks.Group>();
        IntPtr listRef = IntPtr.Zero;
        try
        {
            var handle = tracks.Handle;
            if (handle == IntPtr.Zero) return result;

            IntPtr cls = global::Android.Runtime.JNIEnv.GetObjectClass(handle);
            IntPtr mGetGroups = global::Android.Runtime.JNIEnv.GetMethodID(
                cls, "getGroups", "()Lcom/google/common/collect/ImmutableList;");
            global::Android.Runtime.JNIEnv.DeleteLocalRef(cls);
            if (mGetGroups == IntPtr.Zero) return result;

            listRef = global::Android.Runtime.JNIEnv.CallObjectMethod(handle, mGetGroups);
            if (listRef == IntPtr.Zero) return result;

            IntPtr listCls = global::Android.Runtime.JNIEnv.GetObjectClass(listRef);
            IntPtr mSize = global::Android.Runtime.JNIEnv.GetMethodID(listCls, "size", "()I");
            IntPtr mGet  = global::Android.Runtime.JNIEnv.GetMethodID(listCls, "get", "(I)Ljava/lang/Object;");
            global::Android.Runtime.JNIEnv.DeleteLocalRef(listCls);
            if (mSize == IntPtr.Zero || mGet == IntPtr.Zero) return result;

            int n = global::Android.Runtime.JNIEnv.CallIntMethod(listRef, mSize);
            for (int i = 0; i < n; i++)
            {
                IntPtr elem = global::Android.Runtime.JNIEnv.CallObjectMethod(
                    listRef, mGet, new global::Android.Runtime.JValue(i));
                if (elem == IntPtr.Zero) continue;
                var g = Java.Lang.Object.GetObject<global::AndroidX.Media3.Common.Tracks.Group>(
                    elem, global::Android.Runtime.JniHandleOwnership.TransferLocalRef);
                if (g is not null) result.Add(g);
            }
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("Animarr.NativePlayer", $"GetGroups JNI failed: {ex.Message}");
        }
        finally
        {
            if (listRef != IntPtr.Zero) global::Android.Runtime.JNIEnv.DeleteLocalRef(listRef);
        }
        return result;
    }
#endif

    /// <summary>Select a specific track (indices come from Get*Tracks).</summary>
    public void SelectTrack(int groupIndex, int trackIndex)
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                if (_player is null) return;
                var tracks = _player.CurrentTracks;
                if (tracks is null) return;
                var groups = GetGroups(tracks);
                if (groupIndex < 0 || groupIndex >= groups.Count) return;
                var g = groups[groupIndex];
                var over = new global::AndroidX.Media3.Common.TrackSelectionOverride(
                    g.MediaTrackGroup, trackIndex);
                var p = _player.TrackSelectionParameters.BuildUpon();
                p.SetTrackTypeDisabled(g.Type, false);
                p.SetOverrideForType(over);
                _player.TrackSelectionParameters = p.Build();
                _logger.LogInformation("NativePlayer: selected track g={G} t={T}", groupIndex, trackIndex);
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.SelectTrack"); }
#endif
    }

    /// <summary>Turn subtitles off (disable the text track type).</summary>
    public void DisableSubtitles()
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                if (_player is null) return;
                var p = _player.TrackSelectionParameters.BuildUpon();
                p.ClearOverridesOfType(global::AndroidX.Media3.Common.C.TrackTypeText);
                p.SetTrackTypeDisabled(global::AndroidX.Media3.Common.C.TrackTypeText, true);
                _player.TrackSelectionParameters = p.Build();
                s_subtitleView?.SetCues(new List<global::AndroidX.Media3.Common.Text.Cue>());
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.DisableSubtitles"); }
#endif
    }

#if ANDROID
    /// <summary>Build a human label for a track: "Русский · Дубляж · 5.1 · AC3",
    /// "Английский · SRT" (subtitle), "1080p · HEVC" (video). Falls back to an index.</summary>
    private static string LabelFor(int trackType, global::AndroidX.Media3.Common.Format fmt, int ti)
    {
        string lang = TrackLabels.LangName(fmt.Language);
        if (trackType is global::AndroidX.Media3.Common.C.TrackTypeAudio or global::AndroidX.Media3.Common.C.TrackTypeText)
        {
            bool audio = trackType == global::AndroidX.Media3.Common.C.TrackTypeAudio;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(lang)) parts.Add(lang);
            if (!string.IsNullOrWhiteSpace(fmt.Label) && !string.Equals(fmt.Label, lang, StringComparison.OrdinalIgnoreCase))
                parts.Add(fmt.Label!);
            if (parts.Count == 0) parts.Add(audio ? $"Дорожка {ti + 1}" : $"Субтитры {ti + 1}");
            if (audio && TrackLabels.Channels(fmt.ChannelCount) is { } ch) parts.Add(ch);
            var codec = TrackLabels.CodecFromMime(fmt.Codecs) ?? TrackLabels.CodecFromMime(fmt.SampleMimeType);
            if (codec is not null) parts.Add(codec);
            return string.Join(" · ", parts);
        }
        var vp = new List<string>();
        if (fmt.Height > 0) vp.Add($"{fmt.Height}p");
        var vc = VideoCodecShort(fmt.SampleMimeType);
        if (vc is not null) vp.Add(vc);
        return vp.Count > 0 ? string.Join(" · ", vp) : $"Видео {ti + 1}";
    }

    private static string? VideoCodecShort(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return null;
        var s = mime.ToLowerInvariant();
        if (s.Contains("avc"))  return "H.264";
        if (s.Contains("hevc") || s.Contains("h265")) return "HEVC";
        if (s.Contains("av1"))  return "AV1";
        if (s.Contains("vp9"))  return "VP9";
        return null;
    }
#endif

    public void Dispose()
    {
#if ANDROID
        try { _player?.Release(); } catch { }
        _player = null;
#endif
    }
}

/// <summary>Flat snapshot of the native player's playback state for the HUD
/// tick. All timing values are in milliseconds. Optional fields land empty /
/// 0 when no information is available yet.</summary>
public sealed record NativePlayerState(
    long    PositionMs,
    long    DurationMs,
    bool    Playing,
    bool    Ended,
    bool    Buffering,
    // Phase 2d additions (2026-05-27):
    string? ErrorMessage,    // non-null only when PlaybackException is fatal
    string  ActualCodec,     // post-decode codec slug ("h264" / "hevc" / "av1" / "")
    int     ActualBitDepth,  // 8 / 10 from the stream's ColorInfo / codecs string; 0 = unknown
    int     ActualWidth,
    int     ActualHeight,
    // The play/pause intent (ExoPlayer PlayWhenReady) — true while buffering
    // too, unlike Playing. A stream restart re-applies it.
    bool    PlayWhenReady = false,
    // The fatal error's PlaybackException code name ("ERROR_CODE_DECODING_FAILED")
    // and which renderer failed: "video" / "audio" (null: not a decoder).
    string? ErrorCode = null,
    string? ErrorTrack = null)
{
    public static NativePlayerState Empty { get; } =
        new(0, 0, false, false, false, null, string.Empty, 0, 0, 0);
}

/// <summary>One selectable track for the HUD's audio / subtitle / quality menus.
/// <paramref name="GroupIndex"/> + <paramref name="TrackIndex"/> address it inside
/// ExoPlayer's current <c>Tracks.Groups</c>; <paramref name="Label"/> is display
/// text; <paramref name="Selected"/> marks the currently active one. The rest is
/// the raw Format data (language, container track name, Format.id, codecs,
/// channels, default/forced flags) and whether a renderer on this device can
/// play it — what the player page needs to match a track to the server's
/// probe and to hide what the device can't play.</summary>
public sealed record TrackOption(int GroupIndex, int TrackIndex, string Label, bool Selected,
    string? Language = null, string? Name = null, string? Id = null, string? Codecs = null,
    int Channels = 0, bool Supported = true, bool Forced = false, bool Default = false);

/// <summary>A WebVTT subtitle side-loaded next to the stream (MediaItem
/// SubtitleConfiguration): the server's conversion of an embedded track or a
/// sidecar file.</summary>
public sealed record SideloadSubtitle(string Url, string? Language, string? Label);
