using System.Linq;
using System.Net.Http.Json;
using Animarr.App.Services;
using Animarr.Shared;

namespace Animarr.App;

/// <summary>
/// The native player on a phone / tablet (plan 5.5). The WebView app's Play
/// hands the file over (<see cref="JsInterop.NativePlayerBridge"/>) and this
/// page goes up as a modal page over the WebView — Media3 with the Jellyfin
/// FFmpeg audio decoders (AC-3 / E-AC-3 / DTS / TrueHD played on the device),
/// HDR on the SurfaceView, the same streaming / track / memory logic as the
/// TV. What differs is the input and the chrome:
///
///   • landscape, system bars hidden; a tap shows / hides the HUD, a double
///     tap on the left / right third seeks -10 / +10 s (each further tap in
///     the streak another 10 s), the middle toggles play / pause;
///   • ⏮ / big ▶❚❚ / ⏭ in the middle, a draggable seek bar with chapter
///     ticks, CC, picture-in-picture and a ⚙ sheet (audio, subtitles,
///     quality, aspect, speed, ⓘ, an external player, the web player);
///   • picture-in-picture on its button and on Home (auto-enter on Android
///     12+, OnUserLeaveHint below), with ⏪ / ▶❚❚ / ⏩ in the PiP window;
///   • a MediaStyle notification on the player's MediaSession — lock screen,
///     shade and quick-settings controls;
///   • the web player stays the fallback: a start that never got to play
///     closes the page with "failed" and the WebView plays the file itself;
///     ⚙ → "Смотреть в веб-плеере" hands back with "web".
/// </summary>
public partial class PlayerPage
{
    /// <summary>Touch device — the phone app's WebView shell, not the TV.</summary>
    private static bool Phone => !DeviceKind.IsTv;

    /// <summary>The WebView's hand-off waits on this: "closed" (it played),
    /// "failed" (it never started — the web player takes over) or "web".</summary>
    internal TaskCompletionSource<string>? Outcome { get; init; }

    /// <summary>The file the WebView handed over (the web player can only
    /// take that one back).</summary>
    private string? _handOffPath;
    private bool _everPlayed;
    private bool _fallbackToWeb, _webHandOff;
    private bool _inPip;
    private bool _scrubbing;
    private DateTime _seekStreakUntil;
    private int _seekStreakDir, _seekStreakSec;
    private bool _tapConsumed;
    private IDispatcherTimer? _seekHintTimer;

    public static void OnUserLeaveHintGlobal() => Current?.OnUserLeaveHint();
    public static void OnPipModeChangedGlobal(bool inPip) => Current?.OnPipModeChanged(inPip);

    private string RetryHint => Phone ? "▶ — повторить" : "OK — повторить";

    /// <summary>Constructor part: the touch chrome.</summary>
    private void SetUpPhone()
    {
        if (!Phone) return;
        _handOffPath = _filePath;
        TouchLayer.IsVisible = true;
        CenterControls.IsVisible = true;
        foreach (var v in new View[] { SeekBackButton, PrevButton, PlayPauseButton, NextButton, SeekFwdButton,
                                       SpeedButton, VolumeButton, AudioButton, QualityButton, AspectButton, InfoButton })
            v.IsVisible = false;
        PipButton.IsVisible = OperatingSystem.IsAndroidVersionAtLeast(26);
        GearButton.IsVisible = true;
        Scrubber.IsVisible = false;
        ScrubSlider.IsVisible = true;
        ScrubRow.HeightRequest = 36;
        // Compact chrome for a phone held sideways.
        TopBar.HeightRequest = 100;
        BottomBar.HeightRequest = 150;
        BottomPanel.Padding = new Thickness(16, 0, 16, 8);
        BottomPanel.Spacing = 2;
        BackButton.Margin = new Thickness(12, 14, 0, 0);
        BackButton.HeightRequest = 44;
        TitleBlock.Margin = new Thickness(72, 16, 72, 0);
        InfoLine.Margin = new Thickness(0, 0, 0, 120);
        UpNextCard.Margin = new Thickness(0, 0, 16, 112);
        UpNextCard.WidthRequest = 360;
        UpNextHint.IsVisible = false;
        SheetCard.Padding = new Thickness(18, 14);
        SubsButton.WidthRequest = 52;
        foreach (var b in new[] { SubsButton, SkipButton }) b.HeightRequest = 44;

        CenterPlayFocus.Command = new Command(() => { TogglePlay(); ArmAutoHide(); });
        CenterPrevFocus.Command = new Command(() => GoAdjacentEpisode(-1));
        CenterNextFocus.Command = new Command(() => GoAdjacentEpisode(+1));
        // Long press: the previous / next chapter (the episode without chapters).
        CenterPrevFocus.LongCommand = new Command(() => { if (!GoChapter(-1)) GoAdjacentEpisode(-1); });
        CenterNextFocus.LongCommand = new Command(() => { if (!GoChapter(+1)) GoAdjacentEpisode(+1); });
        PipFocus.Command = new Command(EnterPip);
        GearFocus.Command = new Command(OpenGearSheet);

        ScrubSlider.DragStarted += (_, _) => { _scrubbing = true; _hudTimer?.Stop(); };
        ScrubSlider.ValueChanged += (_, e) =>
        {
            if (_scrubbing && _durationMs > 0) PositionLabel.Text = Fmt((long)(e.NewValue * _durationMs));
        };
        ScrubSlider.DragCompleted += (_, _) =>
        {
            _scrubbing = false;
            if (_durationMs > 0) SeekToAbs((long)(ScrubSlider.Value * _durationMs));
            ArmAutoHide();
        };

        // A tap outside the sheet's card closes it; one on the card stays there.
        var outside = new TapGestureRecognizer();
        outside.Tapped += (_, _) => CloseSheet();
        SheetScrim.GestureRecognizers.Add(outside);
        SheetCard.GestureRecognizers.Add(new TapGestureRecognizer());
#if ANDROID
        TouchLayer.HandlerChanged += (_, _) => WireTouch();
#endif
    }

    private void SetPlayIcon(bool playing)
    {
        var icon = playing ? "❚❚" : "▶";
        PlayPauseIcon.Text = icon;
        CenterPlayIcon.Text = icon;
    }

    private void SetScrubProgress(double p)
    {
        Scrubber.Progress = p;
        if (!_scrubbing && ScrubSlider.IsVisible) ScrubSlider.Value = p;
    }

    /// <summary>Per tick (after the state was read).</summary>
    private void PhoneTick(NativePlayerState st)
    {
        if (st.Playing) _everPlayed = true;
        if (!Phone) return;
        CenterPlayButton.IsVisible = !st.Buffering;   // the spinner sits there meanwhile
        CenterPrevButton.Opacity = _prevEpisode is null ? 0.35 : 1;
        CenterNextButton.Opacity = _nextEpisode is null ? 0.35 : 1;
#if ANDROID
        UpdatePipParams();
        _notification?.Update(_title, _episode is not null ? $"Сезон {_season ?? 1} · Серия {_episode}" : null,
                              st.PlayWhenReady, _prevEpisode is not null, _nextEpisode is not null);
#endif
    }

    /// <summary>A start that never got to play on the phone: back to the
    /// WebView, whose own player plays the file ("failed").</summary>
    private bool PhoneFallback(string why)
    {
        if (!Phone || Outcome is null || _everPlayed || _leaving) return false;
        Log($"native start failed before playing ({why}) → the web player");
        _fallbackToWeb = true;
        Leave();
        return true;
    }

    private void OnPhoneAppearing()
    {
        if (!Phone) return;
        JsInterop.PlatformBridge.SetOrientation("landscape");
    }

    /// <summary>AttachAndStart part: full screen + the system-side controls.</summary>
    private void OnPhoneAttached()
    {
        if (!Phone) return;
        SetPhoneImmersive(true);
#if ANDROID
        try
        {
            if (_session is { } s)
                _notification = new PlaybackNotification(Platform.CurrentActivity ?? Platform.AppContext,
                                                         s.SessionToken, OnPhoneCommand);
        }
        catch (Exception ex) { Log($"notification: {ex.Message}"); }
        UpdatePipParams(force: true);
#endif
    }

    private void OnPhoneDisappearing()
    {
        if (!Phone) return;
#if ANDROID
        ClearPipParams();
        _notification?.Dispose();
        _notification = null;
#endif
        SetPhoneImmersive(false);
        JsInterop.PlatformBridge.SetOrientation("auto");
    }

    /// <summary>Resolves the WebView's hand-off once the last progress is
    /// saved (the page then reads it back for its episode eyes / Continue).</summary>
    private async Task ResolveOutcomeAsync(Task saved)
    {
        if (Outcome is null) return;
        var result = _fallbackToWeb ? "failed" : _webHandOff ? "web" : "closed";
        await Task.WhenAny(saved, Task.Delay(2000));
        Log($"hand-off back to the web page: {result}");
        Outcome.TrySetResult(result);
    }

    // ── Touch ────────────────────────────────────────────────────────────────

    private void OnTap(double frac)
    {
        if (_sheetMode != SheetMode.None) { CloseSheet(); return; }
        if (Hud.IsVisible) HideHud(); else RevealHud();
    }

    /// <summary>The up of a tap: in a seek streak it seeks right away
    /// (YouTube's "keep tapping"), otherwise it waits to be a single tap.</summary>
    private bool OnTapUp(double frac)
    {
        _tapConsumed = DateTime.UtcNow < _seekStreakUntil && SideOf(frac) == _seekStreakDir && _seekStreakDir != 0;
        if (_tapConsumed) StreakSeek(_seekStreakDir);
        return _tapConsumed;
    }

    private void OnDoubleTap(double frac)
    {
        if (_sheetMode != SheetMode.None) return;
        int side = SideOf(frac);
        if (side != 0) StreakSeek(side);
        else { TogglePlay(); if (Hud.IsVisible) ArmAutoHide(); }
    }

    private static int SideOf(double frac) => frac < 0.4 ? -1 : frac > 0.6 ? +1 : 0;

    private void StreakSeek(int dir)
    {
        bool more = DateTime.UtcNow < _seekStreakUntil && _seekStreakDir == dir;
        _seekStreakSec = more ? _seekStreakSec + 10 : 10;
        _seekStreakDir = dir;
        _seekStreakUntil = DateTime.UtcNow.AddMilliseconds(750);
        SeekBy(dir * 10_000L);
        var show = dir < 0 ? SeekHintLeft : SeekHintRight;
        (dir < 0 ? SeekHintRight : SeekHintLeft).IsVisible = false;
        show.Text = dir < 0 ? $"«  {_seekStreakSec} с" : $"{_seekStreakSec} с  »";
        show.IsVisible = true;
        _seekHintTimer ??= Dispatcher.CreateTimer();
        _seekHintTimer.Stop();
        _seekHintTimer.Interval = TimeSpan.FromMilliseconds(800);
        _seekHintTimer.IsRepeating = false;
        _seekHintTimer.Tick -= OnSeekHintTimer;
        _seekHintTimer.Tick += OnSeekHintTimer;
        _seekHintTimer.Start();
    }

    private void OnSeekHintTimer(object? sender, EventArgs e)
    {
        SeekHintLeft.IsVisible = false;
        SeekHintRight.IsVisible = false;
        _seekStreakDir = 0;
    }

#if ANDROID
    private Services.PlaybackNotification? _notification;
    private Android.Views.GestureDetector? _gestures;

    private void WireTouch()
    {
        if (_gestures is not null || TouchLayer.Handler?.PlatformView is not Android.Views.View v) return;
        _gestures = new Android.Views.GestureDetector(v.Context!, new TapListener(this, v));
        v.Touch += (_, e) =>
        {
            if (e.Event is { } ev) _gestures.OnTouchEvent(ev);
            e.Handled = true;
        };
    }

    private sealed class TapListener : Android.Views.GestureDetector.SimpleOnGestureListener
    {
        private readonly WeakReference<PlayerPage> _page;
        private readonly Android.Views.View _view;
        public TapListener(PlayerPage page, Android.Views.View view) { _page = new(page); _view = view; }

        private double Frac(Android.Views.MotionEvent e) => _view.Width > 0 ? e.GetX() / _view.Width : 0.5;

        public override bool OnDown(Android.Views.MotionEvent e) => true;   // or nothing else arrives

        public override bool OnSingleTapUp(Android.Views.MotionEvent e) =>
            _page.TryGetTarget(out var p) && p.OnTapUp(Frac(e));

        public override bool OnSingleTapConfirmed(Android.Views.MotionEvent e)
        {
            if (_page.TryGetTarget(out var p) && !p._tapConsumed) p.OnTap(Frac(e));
            return true;
        }

        public override bool OnDoubleTap(Android.Views.MotionEvent e)
        {
            if (_page.TryGetTarget(out var p)) p.OnDoubleTap(Frac(e));
            return true;
        }
    }

    /// <summary>A tap on the notification / PiP buttons (a broadcast).</summary>
    private void OnPhoneCommand(string cmd) => MainThread.BeginInvokeOnMainThread(() =>
    {
        Log($"system control: {cmd}");
        switch (cmd)
        {
            case "play":   SetPlaying(true); break;
            case "pause":  SetPlaying(false); break;
            case "back10": SeekBy(-10_000); break;
            case "fwd10":  SeekBy(+10_000); break;
            case "prev":   GoAdjacentEpisode(-1); break;
            case "next":   GoAdjacentEpisode(+1); break;
        }
        UpdatePipParams(force: true);
    });
#endif

    // ── Full screen ──────────────────────────────────────────────────────────

    /// <summary>System bars hidden (swipe brings them back for a moment) on the
    /// window this page is in — MAUI shows a modal page in a dialog window of
    /// its own, so the activity's window wouldn't do.</summary>
    private void SetPhoneImmersive(bool on)
    {
#if ANDROID
        try
        {
            if (RootGrid.Handler?.PlatformView is not Android.Views.View v) return;
#pragma warning disable CS0618 // ViewCompat.GetWindowInsetsController: the view's own window (a dialog's)
            var c = AndroidX.Core.View.ViewCompat.GetWindowInsetsController(v);
#pragma warning restore CS0618
            if (c is null) return;
            var bars = AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars();
            if (on)
            {
                c.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
                c.Hide(bars);
            }
            else c.Show(bars);
        }
        catch (Exception ex) { Log($"immersive: {ex.Message}"); }
#endif
    }

    // ── Picture-in-picture ───────────────────────────────────────────────────

#if ANDROID
    private string? _pipKey;

    private Android.App.PictureInPictureParams? BuildPipParams(bool playing)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return null;
        var st = NativePlayerService.Instance?.GetState();
        int w = st?.ActualWidth ?? 0, h = st?.ActualHeight ?? 0;
        double r = w > 0 && h > 0 ? (double)w / h : 16.0 / 9;
        // The system takes 1:2.39 … 2.39:1.
        var ratio = r > 2.39 ? new Android.Util.Rational(239, 100)
                  : r < 1 / 2.39 ? new Android.Util.Rational(100, 239)
                  : w > 0 && h > 0 ? new Android.Util.Rational(w, h) : new Android.Util.Rational(16, 9);
        var ctx = Platform.CurrentActivity ?? Platform.AppContext;
        var actions = new List<Android.App.RemoteAction>
        {
            PipAction(ctx, "back10", Android.Resource.Drawable.IcMediaRew, "−10 с", 21),
            playing ? PipAction(ctx, "pause", Android.Resource.Drawable.IcMediaPause, "Пауза", 22)
                    : PipAction(ctx, "play", Android.Resource.Drawable.IcMediaPlay, "Смотреть", 22),
            PipAction(ctx, "fwd10", Android.Resource.Drawable.IcMediaFf, "+10 с", 23),
        };
        var b = new Android.App.PictureInPictureParams.Builder();
        b.SetAspectRatio(ratio);
        b.SetActions(actions);
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            // Home / Recents while playing → PiP on its own (Android 12+).
            b.SetAutoEnterEnabled(playing && !_leaving);
            b.SetSeamlessResizeEnabled(true);
        }
        return b.Build();
    }

    private static Android.App.RemoteAction PipAction(Android.Content.Context ctx, string cmd, int icon, string title, int code) =>
        new(Android.Graphics.Drawables.Icon.CreateWithResource("android", icon)!, title, title,
            Services.PlaybackNotification.Command(ctx, cmd, code));

    /// <summary>Re-sent when the picture's shape or the play state changed.</summary>
    private void UpdatePipParams(bool force = false)
    {
        if (!Phone || !OperatingSystem.IsAndroidVersionAtLeast(26) || Platform.CurrentActivity is not { } a) return;
        var st = NativePlayerService.Instance?.GetState();
        bool playing = st?.PlayWhenReady == true && !_leaving;
        var key = $"{st?.ActualWidth}x{st?.ActualHeight}:{playing}";
        if (!force && key == _pipKey) return;
        _pipKey = key;
        try { if (BuildPipParams(playing) is { } p) a.SetPictureInPictureParams(p); }
        catch (Exception ex) { Log($"pip params: {ex.Message}"); }
    }

    /// <summary>Leaving the player: the WebView app must not go PiP on Home.</summary>
    private void ClearPipParams()
    {
        if (!Phone || !OperatingSystem.IsAndroidVersionAtLeast(31) || Platform.CurrentActivity is not { } a) return;
        try
        {
            var b = new Android.App.PictureInPictureParams.Builder();
            b.SetAutoEnterEnabled(false);
            b.SetActions(new List<Android.App.RemoteAction>());
            a.SetPictureInPictureParams(b.Build());
        }
        catch (Exception ex) { Log($"pip params: {ex.Message}"); }
    }
#endif

    private void EnterPip()
    {
#if ANDROID
        if (!Phone || !OperatingSystem.IsAndroidVersionAtLeast(26) || Platform.CurrentActivity is not { } a) return;
        try
        {
            CloseSheet();
            HideHud();
            var p = BuildPipParams(NativePlayerService.Instance?.GetState()?.PlayWhenReady == true);
            if (p is null || !a.EnterPictureInPictureMode(p)) FlashInfo("«Картинка в картинке» недоступна");
        }
        catch (Exception ex) { Log($"pip: {ex.Message}"); FlashInfo("«Картинка в картинке» выключена для приложения"); }
#endif
    }

    private void OnUserLeaveHint()
    {
#if ANDROID
        // Android 12+ enters on its own (SetAutoEnterEnabled); below, here.
        if (!Phone || OperatingSystem.IsAndroidVersionAtLeast(31) || _leaving) return;
        if (NativePlayerService.Instance?.GetState() is { PlayWhenReady: true }) EnterPip();
#endif
    }

    private void OnPipModeChanged(bool inPip)
    {
        _inPip = inPip;
        Log($"picture-in-picture {(inPip ? "on" : "off")}");
        if (!inPip)
        {
            // Back full screen while paused: the HUD shows where it stopped.
            if (NativePlayerService.Instance?.GetState() is { PlayWhenReady: false }) RevealHud();
            return;
        }
        // Only the picture (and its subtitles) in the small window.
        if (_sheetMode != SheetMode.None) CloseSheet();
        HideHud();
        InfoLine.IsVisible = false;
        UpNextCard.IsVisible = false;
        SeekHintLeft.IsVisible = SeekHintRight.IsVisible = false;
    }

    // ── ⚙ sheet ──────────────────────────────────────────────────────────────

    private void OpenGearSheet()
    {
        // Each row opens its own sheet — after this one has closed.
        void Then(Action open) => Dispatcher.Dispatch(open);
        var rows = new List<SheetRow>
        {
            new($"Аудио: {CurrentAudioName()}", false, () => Then(OpenAudioSheet)),
            new($"Субтитры: {(_sub is { } s ? SubLabel(s) : "—")}", false, () => Then(OpenSubsSheet)),
            new($"Качество: {(_maxHeight == 0 ? "оригинал" : $"{_maxHeight}p")}", false, () => Then(OpenQualitySheet)),
            new($"Формат: {AspectName(_aspect)}", false, () => Then(OpenAspectSheet)),
            new($"Скорость: {SpeedText(_speed)}", false, () => Then(OpenSpeedSheet)),
            new("Сведения о потоке", false, () => Then(ShowInfo)),
            new("Открыть во внешнем плеере…", false, () => Then(OpenExternalSheet)),
        };
        // The web player can take back the file it handed over.
        if (Outcome is not null && _filePath == _handOffPath)
            rows.Add(new SheetRow("Смотреть в веб-плеере", false, () => _ = SwitchToWebPlayerAsync()));
        OpenListSheet("НАСТРОЙКИ", rows, GearButton, focusIndex: 0);
    }

    private string CurrentAudioName()
    {
        var a = CurrentAudio();
        if (a.External is { } dub)
            return _extAudio.FirstOrDefault(x => x.Path == dub) is { } x ? ExternalLabel(x) : FileName(dub);
        return a.Embedded >= 0 && a.Embedded < _probeAudio.Count ? AudioLabel(_probeAudio[a.Embedded]) : $"Дорожка {a.Embedded + 1}";
    }

    private static string AspectName(string a) => a switch
    {
        "" or "default" => "авто",
        "zoom"          => "заполнить",
        "stretch"       => "растянуть",
        "crop:auto"     => "без полос",
        _ when a.StartsWith("crop:", StringComparison.Ordinal) => $"обрезка {a[5..]}",
        _               => a,
    };

    /// <summary>⚙ → the web player (ASS styling by JASSUB, PGS, …) for this
    /// file, from where it was left — the progress is saved first.</summary>
    private async Task SwitchToWebPlayerAsync()
    {
        if (Outcome is null) return;
        var st = NativePlayerService.Instance?.GetState();
        SetPlaying(false);
        _webHandOff = true;
        if (st is not null && _streamFilePath == _filePath)
            await Task.WhenAny(RecordProgressTask(_streamBaseMs + st.PositionMs,
                                                  _knownDurationMs > 0 ? _knownDurationMs : st.DurationMs),
                               Task.Delay(2000));
        Leave();
    }

    // ── External player (Этап 7) ─────────────────────────────────────────────

    private void OpenExternalSheet()
    {
#if ANDROID
        var ctx = Platform.CurrentActivity ?? Platform.AppContext;
        var players = ExternalPlayers.Installed(ctx);
        var rows = players.Select(p => new SheetRow(p.Name, false, () => _ = OpenExternalAsync(p))).ToList();
        rows.Add(new SheetRow(players.Count == 0 ? "Выбрать приложение… (VLC, mpv, MX Player не найдены)" : "Другое приложение…",
                              false, () => _ = OpenExternalAsync(null)));
        OpenListSheet("ВНЕШНИЙ ПЛЕЕР", rows, GearButton, focusIndex: 0);
#endif
    }

#if ANDROID
    private async Task OpenExternalAsync(ExternalPlayers.Player? player)
    {
        if (Platform.CurrentActivity is not { } activity || NativePlayerService.Instance?.GetState() is not { } st) return;
        long pos = _pendingSeekMs ?? _streamBaseMs + st.PositionMs;
        var file = _filePath;
        SetPlaying(false);
        UpdatePipParams(force: true);   // paused: no auto PiP when the other app opens
        FlashInfo("Открываю во внешнем плеере…");
        var (url, token, fromStart) = await ExternalStreamAsync(pos);
        if (url is null || file != _filePath || _closed)
        {
            if (token is not null) StopHlsSession(token);
            if (url is null) FlashInfo("Не удалось получить поток для внешнего плеера");
            return;
        }
        // Only http(s) on this server goes to another app — never a file:// /
        // content:// or a foreign host, whatever the answer contained.
        if (!IsCurrentServerUrl(url))
        {
            if (token is not null) StopHlsSession(token);
            Log("external: refused a stream URL off the current server");
            FlashInfo("Не удалось получить поток для внешнего плеера");
            return;
        }
        // The chosen subtitle as a file the player adds (shifted by the server).
        var subs = new List<(string Url, string Name)>();
        if (_sub is { IsOff: false, Burn: false } sub && SubtitleRelUrl(sub) is { } rel
            && TvMedia.Url(Abs(_subOffsetMs != 0 ? rel + $"&offsetMs={_subOffsetMs}" : rel)!) is { } subUrl
            && IsCurrentServerUrl(subUrl))
            subs.Add((subUrl, SubLabel(sub)));
        var title = _episode is not null ? $"{_title} · S{_season ?? 1}E{_episode}" : _title;
        bool started = ExternalPlayers.Launch(activity, player, url, title, fromStart ? 0 : pos, subs,
                                              r => OnExternalResult(r, token, file, pos));
        if (!started)
        {
            if (token is not null) StopHlsSession(token);
            FlashInfo(player is null ? "Нет приложения, которое откроет видео" : $"{player.Name} не открылся");
        }
    }

    private bool IsCurrentServerUrl(string? url) =>
        _addr.Current is { } server
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && string.Equals(u.GetLeftPart(UriPartial.Authority), server.GetLeftPart(UriPartial.Authority),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL for the other app: the raw file (Direct Play, the
    /// player demuxes and decodes it all itself and seeks in it) unless the
    /// choice needs the server — a sidecar dub, a burned-in subtitle, a
    /// quality cap: then an HLS session of its own (full-length playlist,
    /// so the position stays absolute) — client=external, so it doesn't
    /// replace this player's session for the same file (a server without
    /// the parameter ignores it). Returns that session's token (to stop it
    /// afterwards) and whether the stream starts at the position already.</summary>
    private async Task<(string? Url, string? Token, bool FromStart)> ExternalStreamAsync(long posMs)
    {
        try
        {
            await TvMedia.EnsureTokenAsync();
            bool needsServer = _audio is { External: not null } || _sub is { Burn: true } || _maxHeight > 0;
            if (!needsServer)
                return (TvMedia.Url(Abs($"{ApiRoutes.File}?path={Uri.EscapeDataString(_filePath)}")!), null, false);
            var audio = _audio switch
            {
                { External: { } dub } => $"&externalAudio={Uri.EscapeDataString(dub)}",
                { Embedded: > 0 } a   => $"&audioTrackIndex={a.Embedded}",
                _                     => "",
            };
            var url = $"/api/hls/start?path={Uri.EscapeDataString(_filePath)}&seek={posMs / 1000}&maxHeight={_maxHeight}" +
                      audio + BurnStartParam() + "&clientHevc=1&clientHevc10=1&remux=hls&client=external" +
                      $"&maxAudioChannels={Services.TvCodecCaps.MaxAudioChannels()}";
            using var resp = await _http.PostAsync(url, null);
            if (!resp.IsSuccessStatusCode) { Log($"external start: HTTP {(int)resp.StatusCode}"); return (null, null, false); }
            var b = await resp.Content.ReadFromJsonAsync<StartResponse>(Json);
            if (b?.ManifestUrl is { } m) return (TvMedia.Url(Abs(m)!), b.Token, false);
            if (b?.DirectPlayUrl is { } dp) return (TvMedia.Url(Abs(dp)!), null, false);
            if (b?.DirectStreamUrl is { } ds) return (TvMedia.Url(Abs(ds + $"&seek={posMs / 1000}")!), null, true);
        }
        catch (Exception ex) { Log($"external start: {ex.Message}"); }
        return (null, null, false);
    }

    /// <summary>Back from the other app: its position becomes the watch
    /// progress, and this player waits there (paused) — a seek in its own
    /// stream, which the external session no longer replaced.</summary>
    private void OnExternalResult(ExternalResult r, string? token, string file, long startedAt)
    {
        if (token is not null) StopHlsSession(token);
        if (_closed || file != _filePath) return;
        long dur = r.DurationMs is > 0 ? r.DurationMs.Value : _knownDurationMs;
        if (r.Completed && dur > 0)
        {
            RecordProgress(dur, dur);
            FlashInfo("Досмотрено во внешнем плеере");
            SeekToAbs(Math.Max(0, dur - 5_000));
            return;
        }
        if (r.PositionMs is long reported && reported > 0)
        {
            long pos = dur > 0 ? Math.Min(reported, Math.Max(0, dur - 1_000)) : reported;
            RecordProgress(pos, dur);
            FlashInfo($"Внешний плеер: остановлено на {Fmt(pos)}");
            SeekToAbs(pos);
            return;
        }
        // The chooser / a player that reports nothing: stay where it was.
        FlashInfo($"Внешний плеер закрыт — продолжить с {Fmt(startedAt)}");
    }
#endif
}
