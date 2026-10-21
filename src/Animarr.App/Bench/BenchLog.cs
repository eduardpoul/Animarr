#if ANDROID
using System.Linq;
namespace Animarr.App.Bench;

/// <summary>THROWAWAY perf probe (tv-ui-analysis branch only). Logs
/// "since process start" milestones to logcat tag AnimarrBench, and a
/// two-frame-later mark so the cost of the layout/draw pass that follows a
/// big tree mutation is included.</summary>
internal static class BenchLog
{
    public static string? Mode;   // set from MainActivity's intent extra "bench"
    /// <summary>Mode without '+flags' (e.g. "lib+nobtf" -> "lib").</summary>
    public static string? Base => Mode?.Split('+')[0];
    public static bool Has(string flag) => Mode is not null && Mode.Split('+').Skip(1).Any(f => f.Split(':')[0] == flag);

    public static long SinceStartMs =>
        Android.OS.SystemClock.ElapsedRealtime() - Android.OS.Process.StartElapsedRealtime;

    public static void Mark(string tag) =>
        Android.Util.Log.Info("AnimarrBench", $"{tag} t={SinceStartMs}ms");

    /// <summary>Log <paramref name="tag"/> now and again after two Choreographer
    /// frames (≈ the frame that laid out + drew the mutation has finished).</summary>
    public static void MarkWithFrames(string tag)
    {
        var t0 = Android.OS.SystemClock.ElapsedRealtime();
        Mark(tag);
        var ch = Android.Views.Choreographer.Instance!;
        ch.PostFrameCallback(new Cb(() =>
            ch.PostFrameCallback(new Cb(() =>
                Android.Util.Log.Info("AnimarrBench",
                    $"{tag}+2frames t={SinceStartMs}ms (+{Android.OS.SystemClock.ElapsedRealtime() - t0}ms)")))));
    }

    private sealed class Cb : Java.Lang.Object, Android.Views.Choreographer.IFrameCallback
    {
        private readonly Action _a;
        public Cb(Action a) => _a = a;
        public void DoFrame(long frameTimeNanos) => _a();
    }
}
#endif
