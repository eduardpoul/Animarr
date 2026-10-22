using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Animarr.Web.Services;

/// <summary>
/// Facts measured from a media file that are expensive to get and never
/// change for one file version — the crop rectangle of baked-in black bars,
/// the loudness of an audio track. Each is computed at most once per
/// (kind, path, mtime, size): the result is kept as a small JSON file under
/// <c>&lt;cacheRoot&gt;/facts</c> (it survives restarts) and in memory, and
/// concurrent callers share one computation. The computation runs on its own
/// timeout, not a caller's token, so a caller giving up (or a short
/// <c>wait</c>) leaves it to finish for the next one. Failures (null) are not
/// stored — the next call tries again.
/// </summary>
public sealed class FileFactsStore
{
    private const int MaxMemoEntries = 4096;

    private readonly string _dir;
    private readonly ILogger<FileFactsStore> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<JsonElement?>>> _memo = new();

    public FileFactsStore(MediaCachePaths paths, ILogger<FileFactsStore> logger)
    {
        _dir    = Path.Combine(paths.CacheRoot, "facts");
        _logger = logger;
    }

    /// <summary>The stored fact, or null when it was never computed (no work).</summary>
    public T? TryGet<T>(string kind, string fullPath) where T : class
    {
        var key = Key(kind, fullPath);
        if (key is null) return null;
        if (_memo.TryGetValue(key, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            return lazy.Value.Result is { } el ? el.Deserialize<T>() : null;
        var file = Path.Combine(_dir, key + ".json");
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null; }
        catch { return null; }
    }

    /// <summary>
    /// The fact of <paramref name="kind"/> for <paramref name="fullPath"/>:
    /// stored, or computed now by <paramref name="compute"/> (bounded by
    /// <paramref name="timeout"/>). Waits at most <paramref name="wait"/>
    /// (null = until done) — on expiry it answers null and the computation
    /// carries on in the background.
    /// </summary>
    public async Task<T?> GetOrComputeAsync<T>(string kind, string fullPath, Func<CancellationToken, Task<T?>> compute,
        TimeSpan timeout, TimeSpan? wait, CancellationToken ct) where T : class
    {
        var key = Key(kind, fullPath);
        if (key is null) return null;
        // Evict finished entries only: clearing a running computation let the
        // next request start a duplicate of it.
        if (_memo.Count > MaxMemoEntries)
            foreach (var (k, v) in _memo)
                if (v.IsValueCreated && v.Value.IsCompleted) _memo.TryRemove(k, out _);
        var lazy = _memo.GetOrAdd(key, k => new Lazy<Task<JsonElement?>>(() => LoadOrRunAsync(k, kind, fullPath, compute, timeout)));
        var task = lazy.Value;
        try
        {
            var result = wait is { } w ? await task.WaitAsync(w, ct) : await task.WaitAsync(ct);
            if (result is null)
                _memo.TryRemove(new KeyValuePair<string, Lazy<Task<JsonElement?>>>(key, lazy));   // retry next time
            return result is { } el ? el.Deserialize<T>() : null;
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            return null;   // still running — the next call gets it
        }
    }

    /// <summary>True while a computation of this fact is running.</summary>
    public bool IsPending(string kind, string fullPath)
    {
        var key = Key(kind, fullPath);
        return key is not null && _memo.TryGetValue(key, out var lazy) && lazy.IsValueCreated && !lazy.Value.IsCompleted;
    }

    private async Task<JsonElement?> LoadOrRunAsync<T>(string key, string kind, string fullPath,
        Func<CancellationToken, Task<T?>> compute, TimeSpan timeout) where T : class
    {
        var file = Path.Combine(_dir, key + ".json");
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(file));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // unreadable — measure again
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var value = await Task.Run(() => compute(cts.Token), cts.Token);
            if (value is null) return null;
            var element = JsonSerializer.SerializeToElement(value);
            Directory.CreateDirectory(_dir);
            var tmp = $"{file}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmp, element.GetRawText());
            File.Move(tmp, file, overwrite: true);
            return element;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Measuring {Kind} failed for {Path}", kind, fullPath);
            return null;
        }
    }

    private static string? Key(string kind, string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return null;
            var raw = $"{kind}|{fullPath}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
            return kind.Replace(':', '-') + "-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        }
        catch { return null; }
    }
}
