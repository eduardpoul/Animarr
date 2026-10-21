namespace Animarr.Web.Services;

/// <summary>
/// Process-wide "is anyone watching right now?" signal for background jobs
/// that read whole media files (trickplay sprites, chromaprint / black-frame
/// segment detection). Two sources:
///   • live HLS transcode sessions (<see cref="HlsSessionService.ActiveSessionCount"/>);
///   • player progress pings (POST /api/watch-states/progress) — the web player
///     and the TV app send one every ~5 s while playing, whatever the path, so
///     this is what makes Direct Play (<c>/api/file</c>) and Direct Stream
///     (<c>/api/video</c> remux) viewers visible too. HLS alone missed them.
///
/// Background passes check <see cref="IsActive"/> between files and park in
/// <see cref="WaitForIdleAsync"/> while it's true, so a full-file decode never
/// competes with a viewer's reads on an HDD/NAS. A paused player stops pinging
/// (web) or keeps pinging (TV) — either way the window below is short enough
/// that background work resumes within a couple of minutes of playback ending.
/// </summary>
public sealed class PlaybackActivity(HlsSessionService hls)
{
    /// <summary>A progress ping keeps playback "active" this long. Pings arrive
    /// every ~5 s, so 90 s rides out a slow network or a brief buffering stall
    /// without flapping, and is short enough not to starve the queue.</summary>
    private static readonly TimeSpan PingWindow = TimeSpan.FromSeconds(90);

    /// <summary>Re-check cadence while parked in <see cref="WaitForIdleAsync"/>.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Environment.TickCount64 of the last ping; 0 = never (monotonic, so a
    // wall-clock jump can't wedge background work).
    private long _lastPingMs;

    /// <summary>Record that a player just reported progress.</summary>
    public void NotePing() => Volatile.Write(ref _lastPingMs, Environment.TickCount64);

    /// <summary>True while an HLS session is alive or a player pinged recently.</summary>
    public bool IsActive
    {
        get
        {
            if (hls.ActiveSessionCount > 0) return true;
            var last = Volatile.Read(ref _lastPingMs);
            return last != 0 && Environment.TickCount64 - last < (long)PingWindow.TotalMilliseconds;
        }
    }

    /// <summary>Returns immediately when nobody is watching; otherwise polls
    /// until playback has been quiet for the ping window.</summary>
    public async Task WaitForIdleAsync(CancellationToken ct)
    {
        while (IsActive)
            await Task.Delay(PollInterval, ct);
    }
}
