#if ANDROID
using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.Media.Session;
using Android.OS;

namespace Animarr.App.Services;

/// <summary>
/// Phone: the native player's transport controls outside the app — the
/// MediaStyle notification tied to the player's MediaSession (the lock
/// screen, the shade and the quick-settings media player show it; Android
/// 13+ draws its buttons from the session's PlaybackState) and the
/// picture-in-picture window's buttons. Both send their taps as a broadcast
/// to this process, which the player page turns into the same commands as
/// its own buttons. Media session notifications need no POST_NOTIFICATIONS
/// grant on Android 13+.
/// </summary>
internal sealed class PlaybackNotification : IDisposable
{
    public const string ActionCommand = "com.animarr.app.PLAYER_COMMAND";
    public const string ExtraCommand = "cmd";
    private const string ChannelId = "animarr_playback";
    private const int NotificationId = 4711;

    private readonly Context _ctx;
    private readonly MediaSession.Token _token;
    private readonly Receiver _receiver;
    private (string Title, string? Text, bool Playing, bool Prev, bool Next)? _shown;

    public PlaybackNotification(Context ctx, MediaSession.Token token, Action<string> onCommand)
    {
        _ctx = ctx;
        _token = token;
        _receiver = new Receiver(onCommand);
        var filter = new IntentFilter(ActionCommand);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            ctx.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
        else
            ctx.RegisterReceiver(_receiver, filter);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)
            && ctx.GetSystemService(Context.NotificationService) is NotificationManager nm
            && nm.GetNotificationChannel(ChannelId) is null)
        {
            var ch = new NotificationChannel(ChannelId, "Воспроизведение", NotificationImportance.Low)
            {
                Description = "Управление плеером Animarr",
            };
            ch.SetShowBadge(false);
            nm.CreateNotificationChannel(ch);
        }
    }

    /// <summary>A tap on a notification / PiP button: this broadcast, with the command.</summary>
    public static PendingIntent Command(Context ctx, string cmd, int requestCode) =>
        PendingIntent.GetBroadcast(ctx, requestCode,
            new Intent(ActionCommand).SetPackage(ctx.PackageName).PutExtra(ExtraCommand, cmd),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;

    /// <summary>Shows / refreshes the notification (only when something changed).</summary>
    public void Update(string title, string? text, bool playing, bool hasPrev, bool hasNext)
    {
        if (_shown == (title, text, playing, hasPrev, hasNext)) return;
        _shown = (title, text, playing, hasPrev, hasNext);
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
            var open = PendingIntent.GetActivity(_ctx, 0,
                new Intent(_ctx, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ReorderToFront),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var b = new Notification.Builder(_ctx, ChannelId)
                .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)!
                .SetContentTitle(title)!
                .SetContentText(text ?? "")!
                .SetContentIntent(open)!
                .SetOngoing(playing)!
                .SetOnlyAlertOnce(true)!
                .SetShowWhen(false)!
                .SetVisibility(NotificationVisibility.Public)!
                .SetCategory(Notification.CategoryTransport)!;
            // Below Android 13 the buttons are these actions; from 13 on the
            // system builds them from the session (and ignores these).
            b.AddAction(Action("prev", Android.Resource.Drawable.IcMediaPrevious, "Предыдущая", 11));
            b.AddAction(playing
                ? Action("pause", Android.Resource.Drawable.IcMediaPause, "Пауза", 12)
                : Action("play", Android.Resource.Drawable.IcMediaPlay, "Смотреть", 12));
            b.AddAction(Action("next", Android.Resource.Drawable.IcMediaNext, "Следующая", 13));
            b.SetStyle(new Notification.MediaStyle().SetMediaSession(_token)!.SetShowActionsInCompactView(0, 1, 2));
            if (_ctx.GetSystemService(Context.NotificationService) is NotificationManager nm)
                nm.Notify(NotificationId, b.Build());
        }
        catch (Exception ex) { Android.Util.Log.Warn("Animarr.Player", $"notification: {ex.Message}"); }
    }

    private Notification.Action Action(string cmd, int icon, string title, int requestCode) =>
        new Notification.Action.Builder(Icon.CreateWithResource("android", icon), title, Command(_ctx, cmd, requestCode)).Build()!;

    public void Dispose()
    {
        try { _ctx.UnregisterReceiver(_receiver); } catch { }
        try { (_ctx.GetSystemService(Context.NotificationService) as NotificationManager)?.Cancel(NotificationId); } catch { }
    }

    private sealed class Receiver : BroadcastReceiver
    {
        private readonly Action<string> _onCommand;
        public Receiver(Action<string> onCommand) => _onCommand = onCommand;
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.GetStringExtra(ExtraCommand) is { } cmd) _onCommand(cmd);
        }
    }
}
#endif
