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

    // TextureView that MainActivity inserted at the bottom of DecorView for
    // ExoPlayer to render into. Registered once at activity startup; stays
    // for the activity's lifetime. We toggle its visibility here so the
    // surface only composites during actual playback (zero idle cost).
    private static TextureView? s_textureView;

    // Pending play request stashed when PlayAsync runs BEFORE the TextureView's
    // SurfaceTexture exists (race between MainActivity.OnCreate inserting the
    // view and the surface allocator threading-up). When the surface arrives
    // we drain this and complete the attach. Without this, an early PlayAsync
    // silently no-ops on the SetVideoTextureView line.
    private (string Url, long ResumeMs)? _pendingPlay;
    private bool _surfaceReady;

    // Lifecycle: remembers PlayWhenReady at OnPause so OnResume can restore
    // whatever the user had going (play→play; pause→pause).
    private bool _wasPlayingBeforePause;

    /// <summary>
    /// Called from <c>MainActivity.OnCreate</c> after the TextureView is
    /// inserted into the activity's view tree. We attach a
    /// SurfaceTextureListener so the service knows when the underlying
    /// SurfaceTexture is actually allocated by the renderer — PlayAsync
    /// calls that land before that point go onto a pending queue, drained
    /// once the surface is ready.
    /// </summary>
    public static void RegisterTextureView(TextureView textureView)
    {
        s_textureView = textureView;
        textureView.SurfaceTextureListener = new SurfaceWatcher();
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

    private sealed class SurfaceWatcher : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        public void OnSurfaceTextureAvailable(global::Android.Graphics.SurfaceTexture surface, int w, int h)
        {
            var svc = Instance;
            if (svc is null) return;
            svc.OnSurfaceReady();
        }
        public bool OnSurfaceTextureDestroyed(global::Android.Graphics.SurfaceTexture surface)
        {
            var svc = Instance;
            if (svc is not null) svc._surfaceReady = false;
            // Return true → caller (TextureView) releases the surface. We're
            // OK with that — ExoPlayer's surface is re-acquired on the next
            // OnSurfaceTextureAvailable callback via _pendingPlay drain.
            return true;
        }
        public void OnSurfaceTextureSizeChanged(global::Android.Graphics.SurfaceTexture surface, int w, int h)
        {
            // Aspect-ratio matrix is anchored to view dimensions; re-apply
            // whenever the view resizes (rare on TV, common on phone rotate).
            Instance?.ApplyAspectMatrix();
        }
        public void OnSurfaceTextureUpdated(global::Android.Graphics.SurfaceTexture surface)
        {
            // Per-frame callback — must be a cheap no-op or the renderer
            // throttles to JNI bridge latency (~60fps minimum).
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
        else if (_player is not null && s_textureView is not null)
        {
            // Re-attach surface to existing player (e.g. after lifecycle
            // bounce destroyed + recreated the surface).
            try { _player.SetVideoTextureView(s_textureView); }
            catch (System.Exception ex) { _logger.LogWarning(ex, "Re-attach surface failed"); }
        }
    }
#endif

    public NativePlayerService(ILogger<NativePlayerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Whether the native player path is wired up + usable on the current host.
    /// Android-TV → true (FEATURE_LEANBACK + Media3 packages installed).
    /// Android phone / tablet → false (no HDR display benefit, sticks with
    /// Artplayer). iOS / desktop → false (Phase 5 covers iOS via AVPlayer).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
#if ANDROID
            try
            {
                var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
                if (ctx?.PackageManager is null) return false;
                // Only TVs benefit from native passthrough — phones use HW
                // decoders inside WebView fine, and exposing a SurfaceView
                // behind the WebView would just complicate touch routing.
                return ctx.PackageManager.HasSystemFeature(
                    Android.Content.PM.PackageManager.FeatureLeanback);
            }
            catch { return false; }
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

            // The TextureView is created `Gone` in MainActivity so it doesn't
            // consume compositor time when no playback is happening. Android
            // only allocates a SurfaceTexture for VISIBLE TextureViews — so
            // we have to flip to Visible BEFORE the surface can become ready.
            // If the surface is ALREADY available (a previous play left it
            // hot), drain the queue immediately on this same UI tick.
            if (s_textureView is not null)
            {
                s_textureView.Post(new Java.Lang.Runnable(() =>
                {
                    s_textureView.Visibility = ViewStates.Visible;
                    s_textureView.KeepScreenOn = true;
                    if (s_textureView.IsAvailable)
                    {
                        // Subsequent play (surface kept from previous session) →
                        // skip the listener round-trip and attach right away.
                        OnSurfaceReady();
                    }
                    // Otherwise SurfaceWatcher.OnSurfaceTextureAvailable will
                    // fire after Android allocates the texture and drain
                    // _pendingPlay via OnSurfaceReady.
                }));
            }
            else
            {
                _logger.LogWarning("NativePlayer: TextureView not registered yet; play will start when MainActivity registers it");
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
                if (s_textureView is not null)
                {
                    _player.SetVideoTextureView(s_textureView);
                }
                var item = MediaItem.FromUri(url);
                _player.SetMediaItem(item);
                _player.Prepare();
                if (resumeMs > 0) _player.SeekTo(resumeMs);
                _player.PlayWhenReady = true;
                _logger.LogInformation("NativePlayer.DoPlay: attached + prepared {Url} resume={ResumeMs}ms",
                    url, resumeMs);
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
    /// Compute + apply the TextureView transform matrix for the current
    /// aspect-ratio choice. Called when the user picks a ratio AND when the
    /// player's video size becomes known (listener path, Phase 2c+).
    /// </summary>
    private void ApplyAspectMatrix()
    {
        if (s_textureView is null) return;
        var size = _lastVideoSize;
        // Pull live from the player if our cached size is stale.
        if (size is null && _player is not null)
        {
            try { size = _player.VideoSize; } catch { }
        }
        if (size is null || size.Width <= 0 || size.Height <= 0) return;

        var tv = s_textureView;
        tv.Post(new Java.Lang.Runnable(() =>
        {
            try
            {
                float vW = size.Width, vH = size.Height;
                float viewW = tv.Width, viewH = tv.Height;
                if (viewW <= 0 || viewH <= 0)
                {
                    tv.SetTransform(null);
                    return;
                }
                float vRatio    = vW / vH;
                float viewRatio = viewW / viewH;
                var m = new global::Android.Graphics.Matrix();

                if (string.Equals(_aspectValue, "default", System.StringComparison.OrdinalIgnoreCase))
                {
                    // Fit with letterbox/pillarbox. Identity already maps the
                    // texture to fill the view (stretched), so we compensate
                    // by shrinking whichever axis the stretch over-extended.
                    if (vRatio > viewRatio)
                        m.SetScale(1f, viewRatio / vRatio, viewW / 2f, viewH / 2f);
                    else if (vRatio < viewRatio)
                        m.SetScale(vRatio / viewRatio, 1f, viewW / 2f, viewH / 2f);
                    tv.SetTransform(m);
                    return;
                }

                float target = ParseAspect(_aspectValue);
                if (target <= 0) { tv.SetTransform(null); return; }

                // Force visible aspect = target via cover-zoom on the dim that
                // currently shows source bars. Math assumes view ≈ video aspect
                // (true on 16:9 TVs with 16:9 sources, the dominant case);
                // mismatched view/video gets a slightly imperfect crop but
                // still wider than identity.
                float sx = 1f, sy = 1f;
                if (target > vRatio)        sy = target / vRatio;  // crop top/bottom
                else if (target < vRatio)   sx = vRatio / target;  // crop left/right
                m.SetScale(sx, sy, viewW / 2f, viewH / 2f);
                tv.SetTransform(m);
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
                if (s_textureView is not null && _player is not null)
                {
                    try { _player.ClearVideoTextureView(s_textureView); } catch { }
                }
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
            // UI underneath visible without the TextureView painting black.
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
