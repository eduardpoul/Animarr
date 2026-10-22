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
/// Bridges the web HUD to a native ExoPlayer instance on Android TV so HDR
/// metadata + Dolby Vision survive playback (the BlazorWebView's HTML5 video
/// element strips them on most builds).
///
/// Phase 2 architecture (2026-05-27):
///   • <see cref="IsAvailable"/> tells JS whether the native path can be taken
///     on this host. Android-TV: yes. Other platforms / non-TV Android: no.
///   • JS-side <c>NativeAdapter</c> calls <see cref="PlayAsync"/> /
///     <see cref="PauseAsync"/> / <see cref="SeekAsync"/> through the
///     <c>animarrNativePlayer.*</c> bridge that <see cref="JsInterop.PlatformBridge"/>
///     publishes on every MAUI launch.
///   • Native ExoPlayer is constructed lazily on the first <see cref="PlayAsync"/>
///     call and reused across episode boundaries (cheaper than tearing down +
///     recreating; ExoPlayer holds large decoder buffers).
///
/// What's NOT in Phase 2a (this file):
///   • Surface attachment. ExoPlayer plays audio fine without a Surface but
///     can't render video to screen yet. A later iteration will add a custom
///     MAUI Handler that lays a <c>PlayerView</c> behind the BlazorWebView
///     and binds it here via <see cref="AttachSurfaceView"/>.
///   • Resume + position reporting back to .NET / JS. Wired in Phase 2b.
///
/// Cross-platform: on non-Android targets (iOS, Mac Catalyst, Windows) this
/// is a no-op shell. <see cref="IsAvailable"/> returns false so the JS gate
/// keeps everyone on the existing Artplayer path.
/// </summary>
public sealed class NativePlayerService : IDisposable
{
    public static NativePlayerService? Instance { get; private set; }
    public static void RegisterStaticInstance(NativePlayerService svc) => Instance = svc;

    private readonly ILogger<NativePlayerService> _logger;

#if ANDROID
    private IExoPlayer? _player;
    private readonly object _lock = new();

    // SurfaceView that MainActivity inserted at the bottom of DecorView for
    // ExoPlayer to render into. SurfaceView (not TextureView) because the video
    // must show THROUGH the transparent BlazorWebView on top: a SurfaceView
    // gets its own SurfaceFlinger layer composited BELOW the window with a
    // hardware hole-punch, which is unaffected by the WebView's in-window
    // opacity. The earlier TextureView composited inside the window's GL
    // surface and got occluded by the WebView layer on this device (frames
    // confirmed reaching the surface via OnSurfaceTextureUpdated, but never
    // visible). Registered once at activity startup; visibility toggled so the
    // surface only composites during actual playback.
    private static TextureView? s_textureView;

    // Pending play request stashed when PlayAsync runs BEFORE the SurfaceView's
    // Surface is created (race between MainActivity.OnCreate inserting the view
    // and the surface being allocated once it goes Visible). When the surface
    // arrives (SurfaceCreated) we drain this and complete the attach.
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

    /// <summary>
    /// Called from <c>MainActivity.OnCreate</c> after the SurfaceView is
    /// inserted into the activity's view tree. We add a SurfaceHolder.Callback
    /// so the service knows when the underlying Surface is created — PlayAsync
    /// calls that land before that point go onto a pending queue, drained once
    /// the surface is ready (SurfaceCreated).
    /// </summary>
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
        view.SetStyle(new global::AndroidX.Media3.UI.CaptionStyleCompat(
            global::Android.Graphics.Color.White, global::Android.Graphics.Color.Transparent,
            global::Android.Graphics.Color.Transparent,
            1 /* CaptionStyleCompat.EDGE_TYPE_OUTLINE */, global::Android.Graphics.Color.Black,
            global::Android.Graphics.Typeface.DefaultBold));
        view.SetFractionalTextSize(0.06f);
        view.SetApplyEmbeddedFontSizes(false);
        // The bottom margin is the view's own padding, not Media3's
        // bottomPaddingFraction: that one only moves cues WITHOUT a line
        // position (SRT / embedded), while WebVTT's default "line -1" cues sat
        // on the very edge of the screen (TV overscan).
        view.SetBottomPaddingFraction(0f);
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

    // BENCH (tv-ui-analysis): SurfaceView variant — video on its own HWC layer.
    private static SurfaceView? s_surfaceView;
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
    ///   <item>"crop:2.35" — cut bars baked into the picture: the centre
    ///         2.35:1 band of a taller frame is what gets fitted, so on a
    ///         2.35 screen (or a 4:3 frame with a scope film inside) it fills
    ///         the width. A frame already that wide is just fitted.</item>
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
            float band = ParseAspect(mode[5..]);
            if (videoAspect > 0f && band > videoAspect)
            {
                // Width u fits the band (u × u/band) in the screen; the frame
                // (u × u/videoAspect) is taller and its bars overflow.
                float u = Math.Min(W, H * band);
                return ((int)Math.Round(u), (int)Math.Round(u / videoAspect));
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
    /// to be sized once per prepare (and only on the TextureView path when the
    /// size changed mid-stream), keeping the previous episode's geometry for
    /// up to a second and showing a first frame of a new stream stretched.</summary>
    private void OnVideoSize(int width, int height, float par, string source)
    {
        if (width <= 0 || height <= 0) return;   // VideoSize.UNKNOWN while preparing
        if (par <= 0f) par = 1f;
        _sizeKnown = true;
        if (width == _vidW && height == _vidH && System.Math.Abs(par - _vidPar) <= 0.001f) return;
        global::Android.Util.Log.Info("Animarr.NativePlayer", $"video size {width}x{height} par={par:0.###} ({source})");
        _vidW = width; _vidH = height; _vidPar = par;
        ApplyAspectMatrix();
    }

    /// <summary>The current stream's geometry has been reported (reset per prepare).</summary>
    private bool _sizeKnown;

    public static void RegisterTextureView(TextureView textureView)
    {
        s_textureView = textureView;
        textureView.SurfaceTextureListener = new TextureListener();
    }

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

    private sealed class TextureListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(global::Android.Graphics.SurfaceTexture surface, int width, int height)
        {
            global::Android.Util.Log.Info("Animarr.NativePlayer", "SurfaceTextureAvailable");
            Instance?.OnSurfaceReady();
        }
        public void OnSurfaceTextureSizeChanged(global::Android.Graphics.SurfaceTexture surface, int width, int height)
            => Instance?.ApplyAspectMatrix();
        public bool OnSurfaceTextureDestroyed(global::Android.Graphics.SurfaceTexture surface)
        {
            var svc = Instance;
            if (svc is not null) svc._surfaceReady = false;
            return true;   // let the system release the texture
        }
        public void OnSurfaceTextureUpdated(global::Android.Graphics.SurfaceTexture surface) { }
    }

    /// <summary>
    /// Called by <see cref="SurfaceWatcher"/> when the TextureView's surface
    /// is ready to receive frames. Drains any <see cref="_pendingPlay"/>
    /// stashed by an early PlayAsync call.
    /// </summary>
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
        else if (_player is not null && (s_textureView is not null || s_surfaceView is not null))
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
        if (_player is not null && s_surfaceView is not null) { _player.SetVideoSurfaceView(s_surfaceView); return; }
        if (_player is null || s_textureView is null) return;
        try
        {
            _player.SetVideoTextureView(s_textureView);
            global::Android.Util.Log.Info("Animarr.NativePlayer",
                "AttachSurface: SetVideoTextureView done");
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error("Animarr.NativePlayer",
                $"AttachSurface failed: {ex.Message}");
        }
    }
#endif

    public NativePlayerService(ILogger<NativePlayerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Whether the native player path is wired up + usable on the current host.
    /// Android TV (Leanback) → true. Android phone/tablet, iOS, desktop → false.
    /// <para>
    /// Phones were tried on the native path (to dodge the mixed-content base64
    /// bridge that froze WebView playback) but it doesn't work on this WebView/
    /// device class: the BlazorWebView occludes any video composited under it,
    /// and a video composited ON TOP covers the HUD. The proper phone fix is the
    /// <see cref="LocalMediaProxyService"/> loopback proxy, which lets the
    /// ordinary web player stream smoothly (HUD over video, no native surface).
    /// Native stays gated to TVs, where it's still worth it for HDR/DV
    /// passthrough — to be validated on real TV hardware (the same WebView
    /// occlusion may or may not apply there). Per-media codec support is gated
    /// by <see cref="CanDecode"/> with an automatic web-player fallback.
    /// </para>
    /// </summary>
    public bool IsAvailable
    {
        get
        {
#if ANDROID
            // Native ExoPlayer is PARKED for now (returns false everywhere). The
            // loopback-proxy web player is the proven path on both phone AND TV
            // (HUD overlays cleanly, no surface-compositing fight), so we use it
            // universally. The native code stays in the tree; re-enable here
            // (e.g. gate on FEATURE_LEANBACK) once the SurfaceView-under-WebView
            // compositing + HDR passthrough are validated on real TV hardware.
            return false;
#else
            return false;
#endif
        }
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
            // Stash the request first — actual ExoPlayer attach happens once
            // we know the TextureView's SurfaceTexture is allocated. Without
            // this we'd race the surface-allocation event and silently
            // attach to a null surface (audio plays, video is black).
            _pendingPlay = (url, resumeMs, playWhenReady, subtitle, textEnabled);
            _logger.LogInformation("NativePlayer: PlayAsync url={Url} resume={ResumeMs}ms (queued, awaiting surface)",
                url, resumeMs);

            // The SurfaceView is created `Gone` in MainActivity so it doesn't
            // consume compositor time when no playback is happening. Android
            // only creates the Surface for a VISIBLE SurfaceView — so we flip
            // to Visible BEFORE the surface can become ready. If the surface is
            // ALREADY valid (a previous play left it hot), drain immediately.
            if (s_surfaceView is not null)
            {
                s_surfaceView.Post(new Java.Lang.Runnable(() =>
                {
                    s_surfaceView.KeepScreenOn = true;
                    if (s_surfaceView.Holder?.Surface?.IsValid == true) OnSurfaceReady();
                }));
            }
            else if (s_textureView is not null)
            {
                s_textureView.Post(new Java.Lang.Runnable(() =>
                {
                    s_textureView.Visibility = ViewStates.Visible;
                    s_textureView.KeepScreenOn = true;
                    if (s_textureView.IsAvailable)
                    {
                        // Surface already live (warm re-play) → attach now.
                        OnSurfaceReady();
                    }
                    // Otherwise SurfaceWatcher.SurfaceCreated fires once Android
                    // allocates the Surface and drains _pendingPlay via
                    // OnSurfaceReady.
                }));
            }
            else
            {
                _logger.LogWarning("NativePlayer: SurfaceView not registered yet; play will start when MainActivity registers it");
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
                    $"DoPlay url={url} svNull={s_textureView is null} " +
                    $"svValid={(s_textureView?.IsAvailable.ToString() ?? "?")} " +
                    $"svSize={s_textureView?.Width ?? -1}x{s_textureView?.Height ?? -1}" +
                    (subtitle is null ? "" : $" sub={subtitle.Url}"));
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
            ApplyAspectMatrix();  // re-apply user's aspect (or default fit) now that video size is incoming
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

    /// <summary>
    /// Set output volume on a 0..1 scale (ExoPlayer's native unit). Mute is
    /// modelled as volume=0 because <c>IExoPlayer</c> exposes <c>Volume</c>
    /// but no separate mute property; the JS adapter reads the previous
    /// volume back from localStorage so the slider restores on un-mute.
    /// </summary>
    public Task SetVolumeAsync(float volume)
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                if (_player is not null)
                {
                    var v = System.Math.Clamp(volume, 0f, 1f);
                    _player.Volume = v;
                }
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.SetVolumeAsync"); }
#endif
        return Task.CompletedTask;
    }

    /// <summary>
    /// Switch the active subtitle track. Pass <c>null</c> URL to disable.
    /// Rebuilds the MediaItem with a <c>SubtitleConfiguration</c> attached —
    /// ExoPlayer doesn't expose a live "swap subtitle" API for sideloaded
    /// tracks, so we tear down + recreate the MediaItem at the same position.
    /// The seek-back-to-position dance is invisible to the user (~50ms gap).
    /// </summary>
    public Task SetSubtitleAsync(string? subtitleUrl, string? language)
    {
#if ANDROID
        try
        {
            lock (_lock)
            {
                if (_player is null) return Task.CompletedTask;
                // The C# binding renames MediaItem.LocalConfiguration access
                // to the `PlaybackProperties` property (Java legacy name —
                // LocalConfiguration is the type; PlaybackProperties is the
                // accessor on MediaItem itself).
                var srcUri = _player.CurrentMediaItem?.PlaybackProperties?.Uri;
                if (srcUri is null) return Task.CompletedTask;

                var pos       = _player.CurrentPosition;
                var wasPlaying = _player.PlayWhenReady;

                var itemBuilder = new MediaItem.Builder().SetUri(srcUri);
                if (!string.IsNullOrEmpty(subtitleUrl))
                {
                    var subUri = global::Android.Net.Uri.Parse(subtitleUrl);
                    var subConfig = new MediaItem.SubtitleConfiguration.Builder(subUri!)
                        .SetMimeType(MimeTypes.TextVtt)        // /api/subtitle?format=webvtt = text/vtt
                        .SetLanguage(language ?? "und")
                        .SetSelectionFlags(global::AndroidX.Media3.Common.C.SelectionFlagDefault)
                        .Build();
                    itemBuilder.SetSubtitleConfigurations(
                        new System.Collections.Generic.List<MediaItem.SubtitleConfiguration> { subConfig });
                }

                _player.SetMediaItem(itemBuilder.Build());
                _player.Prepare();
                if (pos > 0) _player.SeekTo(pos);
                _player.PlayWhenReady = wasPlaying;
                _logger.LogInformation("NativePlayer: subtitle → {Url}", subtitleUrl ?? "(off)");
            }
        }
        catch (System.Exception ex) { _logger.LogWarning(ex, "NativePlayer.SetSubtitleAsync"); }
#endif
        return Task.CompletedTask;
    }

    /// <summary>
    /// Apply an aspect-ratio crop to the TextureView via a transform matrix.
    /// <list type="bullet">
    ///   <item>"default" — preserve native aspect with letterbox/pillarbox.</item>
    ///   <item>"21:9" / "16:9" / "4:3" / "2.35:1" — zoom one axis to crop the
    ///         opposite axis so visible content matches the target aspect.
    ///         Same behaviour as CSS <c>object-fit: cover</c> with a forced
    ///         container aspect — baked-in letterbox bars get cropped first.</item>
    /// </list>
    /// State is stashed so that <c>OnVideoSizeChanged</c> (fired by the
    /// player listener) can re-apply the matrix once video dimensions land.
    /// </summary>
    public Task SetAspectRatioAsync(string value)
    {
#if ANDROID
        _aspectValue = value;
        ApplyAspectMatrix();
#endif
        return Task.CompletedTask;
    }

#if ANDROID
    private string _aspectValue = "default";
    // Decoded video geometry, captured by GetState's poll (there is no
    // ExoPlayer listener in this service — the HUD already polls state every
    // few hundred ms, so a size change is picked up within a tick).
    private int   _vidW;
    private int   _vidH;
    private float _vidPar = 1f;

    /// <summary>
    /// A raw TextureView bound via <c>SetVideoTextureView</c> STRETCHES the
    /// decoded frame to its bounds (we bypass ExoPlayer's
    /// AspectRatioFrameLayout entirely), so anything that isn't exactly the
    /// screen's aspect renders distorted. This applies the aspect correction
    /// as a texture transform:
    /// <list type="bullet">
    ///   <item>"default" — fit: undistorted, letterbox/pillarbox bars.</item>
    ///   <item>"stretch" — identity: fill the screen, ignore the aspect.</item>
    ///   <item>"zoom" — cover: undistorted, crops the overflowing axis.</item>
    ///   <item>"16:9" / "4:3" / "2.35:1"… — force the displayed aspect to the
    ///         given ratio, fitted inside the view.</item>
    /// </list>
    /// Must run on the UI thread. Re-run on: aspect change, decoded-size
    /// change (GetState), surface size change (TextureListener).
    /// </summary>
    internal void ApplyAspectMatrix()
    {
        if (s_surfaceView is { } sv) { sv.Post(() => ApplySurfaceAspect(sv)); return; }
        try
        {
            var tv = s_textureView;
            if (tv is null) return;
            int vw = tv.Width, vh = tv.Height;
            if (vw <= 0 || vh <= 0) return;

            var m = new global::Android.Graphics.Matrix();   // identity = stretch
            float videoAspect = _vidH > 0 && _vidW > 0 ? _vidW * _vidPar / _vidH : 0f;
            float viewAspect  = (float)vw / vh;

            float target = _aspectValue switch
            {
                "stretch"          => 0f,           // identity
                "zoom"             => videoAspect,
                null or "" or "default" => videoAspect,
                _                  => ParseAspect(_aspectValue),
            };

            if (_aspectValue != "stretch" && target > 0f && viewAspect > 0f)
            {
                // k = how much wider the content should display than the view.
                // Fit keeps both scales ≤ 1 (bars); cover keeps both ≥ 1 (crop).
                float k = target / viewAspect;
                float sx, sy;
                if (_aspectValue == "zoom") { if (k >= 1f) { sx = k; sy = 1f; } else { sx = 1f; sy = 1f / k; } }
                else                        { if (k <= 1f) { sx = k; sy = 1f; } else { sx = 1f; sy = 1f / k; } }
                m.SetScale(sx, sy, vw / 2f, vh / 2f);
            }

            tv.SetTransform(m);
            tv.Invalidate();
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Warn("Animarr.NativePlayer", $"ApplyAspectMatrix: {ex.Message}");
        }
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
    /// Ask Android whether the current device can decode the given format —
    /// before we commit to the native path on attach(). Saves the user from
    /// a black screen + ExoPlayer error toast on unsupported HEVC 10-bit /
    /// Dolby Vision profiles (cheap TV boxes claim HEVC support but their
    /// MediaCodec rejects 10-bit Main10 streams at runtime).
    ///
    /// On non-Android: always returns true so the JS side never blocks the
    /// MAUI-internal pipeline. The Artplayer fallback will catch whatever
    /// the WebView can't do on that platform.
    /// </summary>
    public bool CanDecode(string codec, int bitDepth, string? hdr, int width, int height)
    {
#if ANDROID
        try
        {
            var mime = MimeForCodec(codec);
            if (string.IsNullOrEmpty(mime)) return true;  // unknown codec — let it try
            var w = width  > 0 ? width  : 1920;
            var h = height > 0 ? height : 1080;
            var format = global::Android.Media.MediaFormat.CreateVideoFormat(mime, w, h);
            if (format is null) return true;
            // Encode bit-depth + HDR hints into the format so MediaCodecList
            // matches against decoders that actually expose those profiles.
            // Without these, a Main10 stream gets misreported as decodable on
            // 8-bit-only devices, and FindDecoderForFormat returns a path
            // that crashes at first frame.
            if (bitDepth >= 10 && string.Equals(codec, "hevc", System.StringComparison.OrdinalIgnoreCase))
            {
                // HEVCProfileMain10 = 2 (Android API constant).
                format.SetInteger(global::Android.Media.MediaFormat.KeyProfile, 2);
            }
            var lowerHdr = (hdr ?? string.Empty).ToLowerInvariant();
            if (lowerHdr == "hdr10")
            {
                // COLOR_TRANSFER_ST2084 = 6.
                try { format.SetInteger(global::Android.Media.MediaFormat.KeyColorTransfer, 6); } catch { }
            }
            else if (lowerHdr == "hlg")
            {
                // COLOR_TRANSFER_HLG = 7.
                try { format.SetInteger(global::Android.Media.MediaFormat.KeyColorTransfer, 7); } catch { }
            }
            // RegularCodecs (not AllCodecs) ⇒ only software OR hardware that
            // the framework would actually hand a real stream to. AllCodecs
            // includes broken / vendor stubs that the OS won't pick.
            var list = new global::Android.Media.MediaCodecList(
                global::Android.Media.MediaCodecListKind.RegularCodecs);
            var decoder = list.FindDecoderForFormat(format);
            return !string.IsNullOrEmpty(decoder);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "NativePlayer.CanDecode threw — assuming yes");
            // Be optimistic on failure — the worst case is we go native then
            // hit an error which the polling-based error path catches.
            return true;
        }
#else
        return true;
#endif
    }

#if ANDROID
    /// <summary>Map codec slugs (from server output info) to Android MIME types.</summary>
    private static string MimeForCodec(string codec)
    {
        return (codec ?? string.Empty).ToLowerInvariant() switch
        {
            "h264" => global::Android.Media.MediaFormat.MimetypeVideoAvc,
            "hevc" => global::Android.Media.MediaFormat.MimetypeVideoHevc,
            "av1"  => global::Android.Media.MediaFormat.MimetypeVideoAv1,
            "vp9"  => global::Android.Media.MediaFormat.MimetypeVideoVp9,
            _ => string.Empty,
        };
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
                if (_player is not null && s_textureView is not null)
                {
                    try { _player.ClearVideoTextureView(s_textureView); } catch { }
                }
                // A new player page starts on a fresh surface: no geometry yet.
                _sizeKnown = false; _vidW = _vidH = 0; _vidPar = 1f;
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
            // Hide the surface so it stops compositing — keeps the WebView
            // UI underneath visible without the SurfaceView painting black.
            // Drop KeepScreenOn too so non-playback pages get the OS's normal
            // dim/lock-out timing back.
            if (s_textureView is not null)
            {
                s_textureView.Post(new Java.Lang.Runnable(() =>
                {
                    s_textureView.Visibility = ViewStates.Gone;
                    s_textureView.KeepScreenOn = false;
                }));
            }
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
        var renderers = new global::AndroidX.Media3.ExoPlayer.DefaultRenderersFactory(ctx)
            .SetExtensionRendererMode(global::AndroidX.Media3.ExoPlayer.DefaultRenderersFactory.ExtensionRendererModeOn)!
            .SetEnableDecoderFallback(true)!;
        var builder = new ExoPlayerBuilder(ctx, renderers)
            .SetAudioAttributes(attrs, handleAudioFocus: true)!
            .SetLoadControl(load)!;
        var player = (IExoPlayer)builder.Build()!;
        // Cues → SubtitleView, track changes → the player page.
        player.AddListener(new PlayerEvents());
        return player;
    }
#endif

    /// <summary>
    /// Read live player state for the HUD's polling timer. JS-side NativeAdapter
    /// calls this every ~250ms while playback is active; the values drive the
    /// progress bar, time labels, and play/pause icon. Returns a flat record so
    /// the JSInterop layer can shuttle a single object across the WebView IPC
    /// boundary instead of N round trips per tick.
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
                // PlayerError lives on the IPlayer surface — non-null means
                // playback is fatal and won't recover without a reset. We
                // surface the message so the JS HUD can show a toast +
                // offer "Reopen" without round-tripping to .NET.
                string? error = null;
                try { error = _player.PlayerError?.Message; } catch { }
                // VideoFormat reflects what the DECODER reported, which can
                // differ from what the server advertised (e.g. HDR source
                // tone-mapped to SDR by the decoder when the display lacks
                // HDR). Read what's available; default to empty strings so
                // the JS side doesn't have to null-check.
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
                        // ColorInfo's bit-depth fields aren't directly exposed
                        // as ints; pix_fmt detection happens server-side. Best
                        // we can do here is mark 10-bit when colorTransfer
                        // suggests HDR. Cheap approximation.
                        var colorInfo = fmt.ColorInfo;
                        if (colorInfo is not null)
                        {
                            // ColorSpace BT2020 ≈ 10-bit / HDR content.
                            // Constants on AndroidX.Media3.Common.C:
                            //   COLOR_SPACE_BT709 = 1, BT601=2, BT2020=6
                            try
                            {
                                if (colorInfo.ColorSpace == 6) actualBitDepth = 10;
                                else actualBitDepth = 8;
                            }
                            catch { }
                        }
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
                        $"bit={actualBitDepth} → DECODER IS RUNNING (issue is compositing)");
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
                    _player.PlayWhenReady);
            }
        }
        catch { return NativePlayerState.Empty; }
#else
        return NativePlayerState.Empty;
#endif
    }

#if ANDROID
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
    // tracks; sideloaded subtitles still go via SetSubtitleAsync).

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

/// <summary>Flat snapshot of the native player's playback state, marshalled
/// across the JS bridge each poll tick. All timing values are in milliseconds.
/// Optional fields land empty / 0 when no information is available yet.</summary>
public sealed record NativePlayerState(
    long    PositionMs,
    long    DurationMs,
    bool    Playing,
    bool    Ended,
    bool    Buffering,
    // Phase 2d additions (2026-05-27):
    string? ErrorMessage,    // non-null only when PlaybackException is fatal
    string  ActualCodec,     // post-decode codec slug ("h264" / "hevc" / "av1" / "")
    int     ActualBitDepth,  // 8 / 10 based on the decoder's ColorInfo
    int     ActualWidth,
    int     ActualHeight,
    // The play/pause intent (ExoPlayer PlayWhenReady) — true while buffering
    // too, unlike Playing. A stream restart re-applies it.
    bool    PlayWhenReady = false)
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
