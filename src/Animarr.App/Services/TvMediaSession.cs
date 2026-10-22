#if ANDROID
using Android.Content;
using Android.Media;
using Android.Media.Session;
using Android.OS;

namespace Animarr.App.Services;

/// <summary>
/// Framework MediaSession for the native TV player, so the system can drive
/// it: remotes whose media keys are routed to the active session rather than
/// the focused window, Google Assistant ("Hey Google, pause / skip 30
/// seconds / next episode"), the TV's now-playing card. Commands go to the
/// player page — it owns the tier logic (a Direct Stream seek is a restart,
/// a seek on HLS is debounced) — and the page pushes state on every tick.
/// The framework session is enough here: no media3-session dependency, and
/// the player's own Media3 instance stays private to NativePlayerService.
/// </summary>
internal sealed class TvMediaSession : IDisposable
{
    public sealed record Commands(Action Play, Action Pause, Action<long> SeekTo, Action<long> SeekBy,
                                  Action Next, Action Previous, Action Stop);

    private readonly MediaSession _session;
    private (string Title, string? Subtitle, long DurationMs)? _meta;
    private (PlaybackStateCode State, long Actions, float Speed)? _lastState;
    private long _lastPositionMs;

    public TvMediaSession(Context ctx, Commands commands)
    {
        _session = new MediaSession(ctx, "Animarr");
        _session.SetCallback(new Callback(commands), new Handler(Looper.MainLooper!));
#pragma warning disable CA1422 // implied (and ignored) from API 26; needed below it
        _session.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
#pragma warning restore CA1422
        _session.Active = true;
    }

    /// <summary>For the phone's media notification (MediaStyle): lock screen,
    /// the notification shade and the quick-settings player follow this session.</summary>
    public MediaSession.Token SessionToken => _session.SessionToken;

    public void SetMetadata(string title, string? subtitle, long durationMs)
    {
        if (_meta == (title, subtitle, durationMs)) return;
        _meta = (title, subtitle, durationMs);
        var b = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, title)!
            .PutString(MediaMetadata.MetadataKeyDisplayTitle, title)!
            .PutString(MediaMetadata.MetadataKeyDisplaySubtitle, subtitle ?? "")!
            .PutLong(MediaMetadata.MetadataKeyDuration, durationMs > 0 ? durationMs : -1)!;
        _session.SetMetadata(b.Build());
    }

    /// <summary>Pushed every tick; the framework extrapolates the position from
    /// the speed, so only a change of state / actions or a jump is sent.</summary>
    public void SetState(bool playWhenReady, bool buffering, bool ended, long positionMs, float speed,
                         bool hasNext, bool hasPrevious)
    {
        var state = ended ? PlaybackStateCode.Stopped
                  : buffering && playWhenReady ? PlaybackStateCode.Buffering
                  : playWhenReady ? PlaybackStateCode.Playing
                  : PlaybackStateCode.Paused;
        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause
                     | PlaybackState.ActionSeekTo | PlaybackState.ActionFastForward | PlaybackState.ActionRewind
                     | PlaybackState.ActionStop;
        if (hasNext) actions |= PlaybackState.ActionSkipToNext;
        if (hasPrevious) actions |= PlaybackState.ActionSkipToPrevious;
        float rate = state == PlaybackStateCode.Playing ? speed : 0f;
        // Expected position if nothing happened since the last push (1 s ticks).
        long expected = _lastPositionMs + (long)(1000 * (_lastState?.Speed ?? 0f));
        if (_lastState == (state, actions, rate) && Math.Abs(positionMs - expected) < 1500)
        {
            _lastPositionMs = positionMs;
            return;
        }
        _lastState = (state, actions, rate);
        _lastPositionMs = positionMs;
        _session.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(state, positionMs, rate, SystemClock.ElapsedRealtime())!
            .Build());
    }

    public void Dispose()
    {
        try { _session.Active = false; _session.Release(); } catch { }
    }

    private sealed class Callback : MediaSession.Callback
    {
        private readonly Commands _c;
        public Callback(Commands c) => _c = c;
        public override void OnPlay() => _c.Play();
        public override void OnPause() => _c.Pause();
        public override void OnStop() => _c.Stop();
        public override void OnSeekTo(long pos) => _c.SeekTo(pos);
        public override void OnFastForward() => _c.SeekBy(+10_000);
        public override void OnRewind() => _c.SeekBy(-10_000);
        public override void OnSkipToNext() => _c.Next();
        public override void OnSkipToPrevious() => _c.Previous();
    }
}
#endif
