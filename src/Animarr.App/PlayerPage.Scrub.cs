using System.Linq;
using Animarr.App.Services;
using Animarr.Shared.Models;

namespace Animarr.App;

/// <summary>
/// The TV seek bar as a D-pad stop, the way Google TV / YouTube players do it:
/// ▲ from the buttons focuses the bar (it thickens, a thumb appears), ◀ / ▶
/// move the thumb — 10 s a press, 30 s and then 60 s while the key is held or
/// pressed in quick succession — and a preview above the thumb shows the frame
/// there (the server's trickplay sprite, the web player's seek preview) with
/// the time and chapter. Playback goes on meanwhile; the seek happens on OK,
/// once the presses stop, or when focus leaves the bar. OK on a bar that
/// wasn't moved plays / pauses; ▼ goes back to play / pause. The phone drags
/// its Slider instead.
/// </summary>
public partial class PlayerPage
{
    private bool _scrubFocused;
    /// <summary>Where the thumb is while scrubbing (null: it follows playback).</summary>
    private long? _scrubTargetMs;
    private DateTime _scrubLastStepUtc;
    private int _scrubStreak;
    private IDispatcherTimer? _scrubTimer;
    private double _previewFrac;

    /// <summary>The presses have stopped this long → seek.</summary>
    private const int ScrubSettleMs = 900;
    private const double ThumbSize = 18;

    private TrickplayDto? _trickplay;
#if ANDROID
    private Android.Graphics.BitmapRegionDecoder? _sprite;
    private int _tileShown = -1, _tileWanted = -1;
#endif

    /// <summary>EnsureFocusChain part (TV): the bar becomes a focus stop; ▼
    /// from it lands on play / pause.</summary>
    private void WireScrubFocus()
    {
#if ANDROID
        // The native ProgressBar is the stop (MAUI keeps its layout views
        // unfocusable); it spans the whole bar.
        if (Phone || Scrubber.Handler?.PlatformView is not Android.Views.View bar
            || PlayPauseButton.Handler?.PlatformView is not Android.Views.View play) return;
        bar.Focusable = true;
        bar.FocusableInTouchMode = false;
        if (play.Id == Android.Views.View.NoId) play.Id = Android.Views.View.GenerateViewId();
        bar.NextFocusDownId = play.Id;
        bar.FocusChange += (_, e) => OnScrubFocus(e.HasFocus);
        Log($"seek bar: focus stop (focusable={bar.Focusable})");
        ScrubPreview.SizeChanged += (_, _) => { if (ScrubPreview.IsVisible) PositionPreview(_previewFrac); };
#endif
    }

    private void OnScrubFocus(bool focused)
    {
        _scrubFocused = focused;
        Scrubber.ScaleY = focused ? 2 : 1;
        ScrubTrack.ScaleY = ChapterMarks.ScaleY = Scrubber.ScaleY;   // track + chapter gaps follow the bar
        Scrubber.ProgressColor = Color.FromArgb(focused ? "#f49157" : "#bb5000");
        ScrubThumb.IsVisible = focused;
        if (focused) PlaceThumb(Scrubber.Progress);
        else CommitScrub();   // leaving the bar seeks to where the thumb is
    }

#if ANDROID
    /// <summary>◀ / ▶ / OK while the bar has focus (HandleKey).</summary>
    private bool HandleScrubKey(Android.Views.Keycode kc)
    {
        switch (kc)
        {
            case Android.Views.Keycode.DpadLeft:  ScrubStep(-1); return true;
            case Android.Views.Keycode.DpadRight: ScrubStep(+1); return true;
            case Android.Views.Keycode.DpadCenter or Android.Views.Keycode.Enter or Android.Views.Keycode.NumpadEnter:
                if (_scrubTargetMs is not null) CommitScrub();
                else TogglePlay();
                return true;
        }
        return false;
    }
#endif

    private void ScrubStep(int dir)
    {
        var st = NativePlayerService.Instance?.GetState();
        long dur = _knownDurationMs > 0 ? _knownDurationMs : st?.DurationMs ?? 0;
        if (st is null || dur <= 0) return;
        // Key repeats (a held key) and quick presses speed up the thumb.
        var now = DateTime.UtcNow;
        _scrubStreak = (now - _scrubLastStepUtc).TotalMilliseconds < 350 ? _scrubStreak + 1 : 0;
        _scrubLastStepUtc = now;
        long step = _scrubStreak >= 20 ? 60_000 : _scrubStreak >= 4 ? 30_000 : 10_000;
        long from = _scrubTargetMs ?? _pendingSeekMs ?? _streamBaseMs + st.PositionMs;
        long target = Math.Clamp(from + dir * step, 0, dur);
        _scrubTargetMs = target;

        double frac = (double)target / dur;
        SetScrubProgress(frac);
        PositionLabel.Text = Fmt(target);
        PlaceThumb(frac);
        ShowScrubPreview(target, frac);

        _scrubTimer ??= Dispatcher.CreateTimer();
        _scrubTimer.Stop();
        _scrubTimer.Interval = TimeSpan.FromMilliseconds(ScrubSettleMs);
        _scrubTimer.IsRepeating = false;
        _scrubTimer.Tick -= OnScrubSettled;
        _scrubTimer.Tick += OnScrubSettled;
        _scrubTimer.Start();
    }

    private void OnScrubSettled(object? sender, EventArgs e) => CommitScrub();

    /// <summary>Seek to the thumb (OK, the presses stopped, focus left the bar).</summary>
    private void CommitScrub()
    {
        _scrubTimer?.Stop();
        ScrubPreview.IsVisible = false;
        if (_scrubTargetMs is not long t) return;
        _scrubTargetMs = null;
        _scrubStreak = 0;
        Log($"scrub → {Fmt(t)}");
        SeekToAbs(t);
    }

    private void PlaceThumb(double frac)
    {
        double w = ScrubThumbLayer.Width, h = ScrubThumbLayer.Height;
        if (w <= 0) return;
        AbsoluteLayout.SetLayoutBounds(ScrubThumb,
            new Rect(w * Math.Clamp(frac, 0, 1) - ThumbSize / 2, (h - ThumbSize) / 2, ThumbSize, ThumbSize));
    }

    // ── Preview ─────────────────────────────────────────────────────────────

    private void ShowScrubPreview(long t, double frac)
    {
        ScrubPreviewTime.Text = ChapterTitleAt(t) is { } ch ? $"{Fmt(t)} · {ch}" : Fmt(t);
        ScrubPreviewImage.IsVisible = ShowTile(t);
        ScrubPreview.IsVisible = true;
        _previewFrac = frac;
        PositionPreview(frac);
    }

    /// <summary>Centres the preview over the thumb (kept on screen), just above the bar.</summary>
    private void PositionPreview(double frac)
    {
#if ANDROID
        if (ScrubRow.Handler?.PlatformView is not Android.Views.View bar
            || Hud.Handler?.PlatformView is not Android.Views.View hud) return;
        float d = bar.Resources?.DisplayMetrics?.Density ?? 2f;
        int[] a = new int[2], b = new int[2];
        bar.GetLocationInWindow(a);
        hud.GetLocationInWindow(b);
        double x = (a[0] - b[0]) / d + bar.Width / d * frac;
        double top = (a[1] - b[1]) / d;
        double w = ScrubPreview.Width > 0 ? ScrubPreview.Width : 244;   // before its first layout
        ScrubPreview.TranslationX = Math.Clamp(x - w / 2, 16, Math.Max(16, Hud.Width - w - 16));
        var margin = new Thickness(0, 0, 0, Math.Max(0, Math.Round(Hud.Height - top + 10)));
        if (ScrubPreview.Margin != margin) ScrubPreview.Margin = margin;
#endif
    }

    private string? ChapterTitleAt(long t)
    {
        if (_chapters.Count < 2) return null;
        double sec = t / 1000.0;
        var title = _chapters.LastOrDefault(c => c.StartSec <= sec + 0.5)?.Title;
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>Puts the sprite tile for <paramref name="t"/> in the preview
    /// (decoded off the UI thread, one tile at a time); false without a sprite.</summary>
    private bool ShowTile(long t)
    {
#if ANDROID
        if (_trickplay is not { } tp || _sprite is not { } sprite) return false;
        int idx = Math.Clamp((int)(t / 1000 / Math.Max(1, tp.IntervalSec)), 0, Math.Max(0, tp.Count - 1));
        if (idx == _tileShown || idx == _tileWanted) return true;
        _tileWanted = idx;
        int col = idx % tp.Cols, row = idx / tp.Cols;
        var rect = new Android.Graphics.Rect(col * tp.TileWidth, row * tp.TileHeight,
                                             (col + 1) * tp.TileWidth, (row + 1) * tp.TileHeight);
        _ = Task.Run(() =>
        {
            Android.Graphics.Bitmap? tile = null;
            try { lock (sprite) tile = sprite.IsRecycled ? null : sprite.DecodeRegion(rect, null); }
            catch (Exception ex) { Log($"trickplay: tile {idx}: {ex.Message}"); }
            if (tile is null) return;
            Dispatcher.Dispatch(() =>
            {
                // A newer tile was asked for meanwhile.
                if (_tileWanted != idx || ScrubPreviewImage.Handler?.PlatformView is not Android.Widget.ImageView iv) return;
                iv.SetImageBitmap(tile);
                _tileShown = idx;
            });
        });
        return true;
#else
        return false;
#endif
    }

    /// <summary>Per file: the server's trickplay sprite. 204 until its
    /// background pass got to the file — the preview then shows the time only.</summary>
    private async Task LoadTrickplayAsync(string path)
    {
        if (Phone) return;
        ResetTrickplay();
        try
        {
            var tp = await _api.GetTrickplayAsync(_mediaItemId, _season, _episode);
            if (path != _filePath || _closed) return;
            if (tp is null || tp.Cols <= 0 || tp.TileWidth <= 0 || tp.TileHeight <= 0) { Log("trickplay: none yet"); return; }
            var bytes = await _http.GetByteArrayAsync(tp.SpriteUrl);
            if (path != _filePath || _closed) return;
#if ANDROID
            _sprite = OperatingSystem.IsAndroidVersionAtLeast(31)
                ? Android.Graphics.BitmapRegionDecoder.NewInstance(bytes, 0, bytes.Length)
                : Android.Graphics.BitmapRegionDecoder.NewInstance(bytes, 0, bytes.Length, false);
#endif
            _trickplay = tp;
            Log($"trickplay: {tp.Count} tiles every {tp.IntervalSec} s ({bytes.Length / 1024} KB)");
        }
        catch (Exception ex) { Log($"trickplay: {ex.Message}"); }
    }

    private void ResetTrickplay()
    {
        _trickplay = null;
#if ANDROID
        var old = _sprite;
        _sprite = null;
        _tileShown = _tileWanted = -1;
        if (old is not null) _ = Task.Run(() => { lock (old) old.Recycle(); });
#endif
    }
}
