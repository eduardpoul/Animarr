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
    private static SurfaceView? s_surfaceView;

    // Pending play request stashed when PlayAsync runs BEFORE the SurfaceView's
    // Surface is created (race between MainActivity.OnCreate inserting the view
    // and the surface being allocated once it goes Visible). When the surface
    // arrives (SurfaceCreated) we drain this and complete the attach.
    private (string Url, long ResumeMs)? _pendingPlay;
    private bool _surfaceReady;

    // Diagnostic one-shot guards (logcat tag "Animarr.NativePlayer"). Used to
    // pin down why this device plays audio but no video: did ExoPlayer ever
    // report a video format (→ decoding works, problem is compositing) or a
    // PlayerError (→ codec init failed, message tells us what)?
    private bool _diagFmtLogged;
    private bool _diagErrLogged;
    private int  _diagPolls;

    // Set once the SurfaceView has been sized to the decoded video aspect.
    // Reset per play in DoPlay. GetState (polled by the HUD) drives the
    // one-shot sizing because there's no video-size listener wired.
    private bool _aspectApplied;

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
    public static void RegisterSurfaceView(SurfaceView surfaceView)
    {
        s_surfaceView = surfaceView;
        surfaceView.Holder?.AddCallback(new SurfaceWatcher());
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

    private sealed class SurfaceWatcher : Java.Lang.Object, global::Android.Views.ISurfaceHolderCallback
    {
        public void SurfaceCreated(global::Android.Views.ISurfaceHolder holder)
        {
            global::Android.Util.Log.Info("Animarr.NativePlayer", "SurfaceCreated");
            Instance?.OnSurfaceReady();
        }
        public void SurfaceChanged(global::Android.Views.ISurfaceHolder holder,
            global::Android.Graphics.Format format, int width, int height)
        {
            // Re-apply aspect sizing whenever the surface dimensions change
            // (first layout, rotation).
            Instance?.ApplyAspectMatrix();
        }
        public void SurfaceDestroyed(global::Android.Views.ISurfaceHolder holder)
        {
            var svc = Instance;
            if (svc is not null) svc._surfaceReady = false;
            // ExoPlayer (via SetVideoSurfaceView) drops its surface ref on this
            // callback automatically and re-acquires on the next SurfaceCreated.
        }
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
            DoPlay(pending.Value.Url, pending.Value.ResumeMs);
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
        try
        {
            _player.SetVideoSurfaceView(s_surfaceView);
            global::Android.Util.Log.Info("Animarr.NativePlayer",
                "AttachSurface: SetVideoSurfaceView done");
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
    /// </summary>
    public Task PlayAsync(string url, long resumeMs = 0)
    {
#if ANDROID
        try
        {
            // Stash the request first — actual ExoPlayer attach happens once
            // we know the TextureView's SurfaceTexture is allocated. Without
            // this we'd race the surface-allocation event and silently
            // attach to a null surface (audio plays, video is black).
            _pendingPlay = (url, resumeMs);
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
                    s_surfaceView.Visibility = ViewStates.Visible;
                    s_surfaceView.KeepScreenOn = true;
                    if (s_surfaceView.Holder?.Surface?.IsValid == true)
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
    private void DoPlay(string url, long resumeMs)
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
                    $"DoPlay url={url} svNull={s_surfaceView is null} " +
                    $"svValid={(s_surfaceView?.Holder?.Surface?.IsValid.ToString() ?? "?")} " +
                    $"svSize={s_surfaceView?.Width ?? -1}x{s_surfaceView?.Height ?? -1}");
                AttachSurface();
                var item = MediaItem.FromUri(url);
                _player.SetMediaItem(item);
                _player.Prepare();
                if (resumeMs > 0) _player.SeekTo(resumeMs);
                _player.PlayWhenReady = true;
                // Reset diag + aspect guards so GetState re-logs format/error
                // and re-sizes the SurfaceView for this play.
                _diagFmtLogged = false; _diagErrLogged = false; _diagPolls = 0;
                _aspectApplied = false;
                _logger.LogInformation("NativePlayer.DoPlay: attached + prepared {Url} resume={ResumeMs}ms",
                    url, resumeMs);
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"DoPlay prepared, PlayWhenReady=true");
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
    private global::AndroidX.Media3.Common.VideoSize? _lastVideoSize;

    /// <summary>
    /// Size the SurfaceView to match the video's aspect, centered (letterbox /
    /// pillarbox). ExoPlayer stretches the decoded frame to fill the
    /// SurfaceView's bounds, so — unlike the old TextureView transform-matrix
    /// approach — we get correct aspect by sizing the *view* to the video's
    /// ratio rather than transforming the texture. Called from DoPlay and from
    /// SurfaceChanged (first layout / rotation) and re-polled from GetState
    /// once the decoded video size is known.
    ///
    /// NOTE: explicit aspect-ratio crop modes (21:9 etc.) currently fall back
    /// to fit on the native/SurfaceView path — SurfaceView can't be transformed
    /// like a TextureView, and a proper cover-crop needs a clip container.
    /// Getting the picture visible + undistorted is the priority; crop modes
    /// can be layered on later.
    /// </summary>
    private void ApplyAspectMatrix()
    {
        if (s_surfaceView is null) return;
        var size = _lastVideoSize;
        if (size is null && _player is not null)
        {
            try { size = _player.VideoSize; } catch { }
        }
        if (size is null || size.Width <= 0 || size.Height <= 0) return;

        var sv = s_surfaceView;
        var vw = size.Width; var vh = size.Height;
        sv.Post(new Java.Lang.Runnable(() =>
        {
            try
            {
                var parent = sv.Parent as global::Android.Views.View;
                float pw = parent?.Width ?? 0f;
                float ph = parent?.Height ?? 0f;
                if (pw <= 0 || ph <= 0) return;

                float vRatio = (float)vw / vh;
                float screenRatio = pw / ph;
                int w, h;
                if (vRatio > screenRatio)   // video wider → full width, bars top/bottom
                {
                    w = (int)pw;
                    h = (int)System.Math.Round(pw / vRatio);
                }
                else                        // video taller → full height, bars left/right
                {
                    h = (int)ph;
                    w = (int)System.Math.Round(ph * vRatio);
                }

                sv.LayoutParameters = new global::Android.Widget.FrameLayout.LayoutParams(w, h)
                {
                    Gravity = global::Android.Views.GravityFlags.Center,
                };
                global::Android.Util.Log.Info("Animarr.NativePlayer",
                    $"Aspect-sized SurfaceView {w}x{h} (video {vw}x{vh}, screen {(int)pw}x{(int)ph})");
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "NativePlayer.ApplyAspectMatrix");
            }
        }));
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
                if (_player is not null && s_surfaceView is not null)
                {
                    try { _player.ClearVideoSurfaceView(s_surfaceView); } catch { }
                }
                _aspectApplied = false;
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
            if (s_surfaceView is not null)
            {
                s_surfaceView.Post(new Java.Lang.Runnable(() =>
                {
                    s_surfaceView.Visibility = ViewStates.Gone;
                    s_surfaceView.KeepScreenOn = false;
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
        var builder = new ExoPlayerBuilder(ctx)
            .SetAudioAttributes(attrs, handleAudioFocus: true);
        return (IExoPlayer)builder.Build()!;
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

                // Once the decoded video size is known, size the SurfaceView to
                // its aspect (one-shot). There's no video-size listener wired,
                // so this poll is what drives it. ApplyAspectMatrix reposts to
                // the UI thread internally.
                if (!_aspectApplied && actualWidth > 0 && actualHeight > 0)
                {
                    _aspectApplied = true;
                    ApplyAspectMatrix();
                }

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
                    error, actualCodec, actualBitDepth, actualWidth, actualHeight);
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
    int     ActualHeight)
{
    public static NativePlayerState Empty { get; } =
        new(0, 0, false, false, false, null, string.Empty, 0, 0, 0);
}
