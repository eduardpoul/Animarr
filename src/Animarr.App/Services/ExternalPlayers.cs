#if ANDROID
using Android.App;
using Android.Content;

namespace Animarr.App.Services;

/// <summary>
/// "Открыть во внешнем плеере" (phone): VLC / mpv / MX Player through an
/// ACTION_VIEW intent with a stream URL that carries the media token, the
/// title and the resume position, started for a result so the position the
/// player stopped at comes back (and is saved as watch progress):
/// <list type="bullet">
///   <item>VLC — VideoPlayerActivity; extras <c>title</c>, <c>position</c>
///         (long ms), <c>subtitles_location</c>; returns <c>extra_position</c> /
///         <c>extra_duration</c> (long ms);</item>
///   <item>MX Player (free / Pro) and mpv-android (MX's intent API) —
///         <c>title</c>, <c>position</c> (int ms), <c>return_result</c>,
///         <c>subs</c> / <c>subs.name</c> / <c>subs.enable</c>; return
///         <c>position</c> / <c>duration</c> (int ms) and <c>end_by</c>
///         ("playback_completion" — then without a position).</item>
/// </list>
/// Package visibility (Android 11+) comes from the manifest's &lt;queries&gt;.
/// </summary>
internal static class ExternalPlayers
{
    public enum Api { Vlc, Mx }

    public sealed record Player(string Package, string Name, string? Activity, Api Api);

    private static readonly Player[] Known =
    {
        new("org.videolan.vlc", "VLC", "org.videolan.vlc.gui.video.VideoPlayerActivity", Api.Vlc),
        new("is.xyz.mpv", "mpv", null, Api.Mx),
        new("com.mxtech.videoplayer.pro", "MX Player Pro", null, Api.Mx),
        new("com.mxtech.videoplayer.ad", "MX Player", null, Api.Mx),
    };

    public const int RequestCode = 0x4A11;

    private static Action<ExternalResult>? s_pending;

    public static List<Player> Installed(Context ctx) =>
        Known.Where(p =>
        {
            try { ctx.PackageManager!.GetPackageInfo(p.Package, 0); return true; }
            catch { return false; }
        }).ToList();

    /// <summary>Starts the player (null: the system chooser — then nothing
    /// comes back). False when no app takes the intent.</summary>
    public static bool Launch(Activity activity, Player? player, string url, string title, long positionMs,
                              IReadOnlyList<(string Url, string Name)> subtitles, Action<ExternalResult> onResult)
    {
        var view = new Intent(Intent.ActionView);
        view.SetDataAndType(Android.Net.Uri.Parse(url), "video/*");
        view.PutExtra("title", title);
        if (player?.Api == Api.Vlc)
        {
            view.PutExtra("position", positionMs);
            view.PutExtra("from_start", false);
            if (subtitles.Count > 0) view.PutExtra("subtitles_location", subtitles[0].Url);
        }
        else
        {
            view.PutExtra("position", (int)Math.Min(int.MaxValue, positionMs));
            view.PutExtra("return_result", true);
            if (subtitles.Count > 0)
            {
                var uris = subtitles.Select(s => (Android.OS.IParcelable)Android.Net.Uri.Parse(s.Url)!).ToArray();
                view.PutExtra("subs", uris);
                view.PutExtra("subs.name", subtitles.Select(s => s.Name).ToArray());
                view.PutExtra("subs.enable", new[] { uris[0] });
            }
        }
        Intent intent;
        if (player is null)
        {
            intent = Intent.CreateChooser(view, "Открыть в…")!;
            // Not this app itself ("Open in Animarr" takes video URLs too).
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                intent.PutExtra(Intent.ExtraExcludeComponents,
                    new Android.OS.IParcelable[] { new ComponentName(activity, "com.animarr.app.MainActivity") });
        }
        else
        {
            if (player.Activity is { } cls) view.SetComponent(new ComponentName(player.Package, cls));
            else view.SetPackage(player.Package);
            intent = view;
        }
        Android.Util.Log.Info("Animarr.Player", $"external: {player?.Name ?? "chooser"} {Redact(view.ToUri(IntentUriType.Scheme))}");
        s_pending = onResult;
        if (Bench.BenchLog.Has("fakeexternal"))
        {
            // Bench: no player on the emulator — answer as VLC would, 30 s on.
            var fake = new Intent().PutExtra("extra_position", positionMs + 30_000).PutExtra("extra_duration", 0L);
            new Android.OS.Handler(Android.OS.Looper.MainLooper!).PostDelayed(
                () => HandleResult(RequestCode, Result.Ok, fake), 1500);
            return true;
        }
        try
        {
            activity.StartActivityForResult(intent, RequestCode);
            return true;
        }
        catch (ActivityNotFoundException) { s_pending = null; return false; }
    }

    /// <summary>MainActivity.OnActivityResult: true when it was ours.</summary>
    public static bool HandleResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode) return false;
        var done = s_pending;
        s_pending = null;
        long? Ms(string key)
        {
            try
            {
                if (data?.Extras is not { } x || !x.ContainsKey(key)) return null;
#pragma warning disable CA1422 // Bundle.Get(key): the type (int / long) differs per player
                return x.Get(key) is Java.Lang.Number n ? n.LongValue() : null;
#pragma warning restore CA1422
            }
            catch { return null; }
        }
        var endBy = data?.GetStringExtra("end_by");
        var r = new ExternalResult(resultCode, Ms("extra_position") ?? Ms("position"), Ms("extra_duration") ?? Ms("duration"),
                                   endBy == "playback_completion", endBy);
        Android.Util.Log.Info("Animarr.Player", $"external result: code={(int)resultCode} position={r.PositionMs} duration={r.DurationMs} end_by={endBy}");
        done?.Invoke(r);
        return true;
    }

    /// <summary>Logs the intent without the media token.</summary>
    private static string Redact(string? s) =>
        System.Text.RegularExpressions.Regex.Replace(s ?? "", "mt(=|%3D)[A-Za-z0-9_%-]+", "mt=…");
}

/// <summary>What the external player reported when it closed.</summary>
internal sealed record ExternalResult(Result Code, long? PositionMs, long? DurationMs, bool Completed, string? EndBy);
#endif
