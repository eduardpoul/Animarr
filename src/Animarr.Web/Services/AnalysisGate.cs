namespace Animarr.Web.Services;

/// <summary>
/// One shared limit for the server's file-analysis ffmpeg / ffprobe runs —
/// the ones that read a whole file or decode part of it on request: the
/// black-bar crop detection (/api/media-facts), the key-frame packet scan
/// (KeyframeIndexService), subtitle extraction from the container
/// (SubtitleCache) and the loudness measurement (LoudnessService). Each of
/// these used to start as many processes as requests came in (or held one
/// global slot for up to 20 minutes with unbounded waiters), so a client
/// could fan out full-file reads against the media disk.
///
/// <see cref="Slots"/> run at once; at most <see cref="MaxWaiters"/> wait,
/// beyond that a caller gets <see cref="AnalysisBusyException"/> (treated as
/// "not available now"). Background work (<c>park: true</c> — the loudness
/// pass) also waits until nobody is watching (PlaybackActivity), like the TV
/// remux job.
/// </summary>
public sealed class AnalysisGate
{
    public const int Slots = 3, MaxWaiters = 32;

    /// <summary>The DI instance (static helpers like SubtitleCache use it);
    /// a standalone one until the container builds it.</summary>
    public static AnalysisGate Default { get; private set; } = new(null);

    private readonly SemaphoreSlim _slots = new(Slots, Slots);
    private readonly IServiceProvider? _services;
    private int _waiting;

    public AnalysisGate(IServiceProvider? services)
    {
        _services = services;
        if (services is not null) Default = this;
    }

    /// <summary>Runs <paramref name="work"/> in a slot. <paramref name="park"/>:
    /// background work — first wait until playback is idle.</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, bool park, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _waiting) > MaxWaiters)
        {
            Interlocked.Decrement(ref _waiting);
            throw new AnalysisBusyException();
        }
        try
        {
            if (park && _services?.GetService(typeof(PlaybackActivity)) is PlaybackActivity activity)
                await activity.WaitForIdleAsync(ct);
            await _slots.WaitAsync(ct);
        }
        finally { Interlocked.Decrement(ref _waiting); }
        try { return await work(ct); }
        finally { _slots.Release(); }
    }

    /// <summary>Runs in use / waiting (diagnostics).</summary>
    public (int Running, int Waiting) Load => (Slots - _slots.CurrentCount, Volatile.Read(ref _waiting));
}

/// <summary>The analysis queue is full — try again later.</summary>
public sealed class AnalysisBusyException() : Exception("The analysis queue is full.");
