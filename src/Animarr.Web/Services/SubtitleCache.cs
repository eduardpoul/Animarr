using System.Diagnostics;

namespace Animarr.Web.Services;

/// <summary>
/// Disk cache behind <c>/api/subtitle</c>. Extracting one embedded track makes
/// ffmpeg read the WHOLE container — Matroska has no index for subtitle
/// packets — so every request was a 0.3–2 GB read (seconds on an HDD), fired at
/// each playback start with subs while the video stream reads the same file.
///
/// The converted track is cached under <c>&lt;cacheRoot&gt;/subtitles</c>, keyed
/// by source path + track + format + mtime + size (like ImageResizer's
/// variants), so a re-watch, another profile or a request after the browser's
/// 1-hour TTL is a plain file send, and a replaced file busts the key. Only a
/// successful, non-empty conversion is cached — written to a temp file and
/// atomically renamed — so a bitmap track or a broken stream stays a 422 and
/// never becomes a cached empty subtitle. The directory is size-capped, oldest
/// first.
/// </summary>
public static class SubtitleCache
{
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private const long TrimToBytes   = 192L * 1024 * 1024;
    private const int  SweepEveryWrites = 32;
    private static int _writesSinceSweep;
    private static int _sweeping;

    /// <summary><paramref name="Path"/> = the cached track; null = conversion
    /// failed (<paramref name="ExitCode"/> / <paramref name="Error"/> = ffmpeg's).</summary>
    public sealed record Result(string? Path, int ExitCode, string Error);

    /// <param name="outFormat">ffmpeg muxer: <c>webvtt</c> or <c>ass</c>.</param>
    public static async Task<Result> GetOrExtractAsync(string sourcePath, int track, string outFormat,
        string cacheRoot, CancellationToken ct)
    {
        var dir = Path.Combine(cacheRoot, "subtitles");
        Directory.CreateDirectory(dir);

        var fi   = new FileInfo(sourcePath);
        var key  = $"{sourcePath}|{track}|{outFormat}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var cached = Path.Combine(dir, $"{hash}.{(outFormat == "ass" ? "ass" : "vtt")}");

        if (File.Exists(cached))
        {
            TouchForLru(cached);
            return new Result(cached, 0, "");
        }

        // Unique temp name: two concurrent requests for the same track each
        // extract (as before) and the second rename just replaces the first.
        var tmp = $"{cached}.{Guid.NewGuid():N}.tmp";
        var psi = new ProcessStartInfo
        {
            FileName               = "ffmpeg",
            // Output goes to the temp file, nothing to read on stdout.
            RedirectStandardOutput = false,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "warning",
            "-i", sourcePath,
            "-map", $"0:s:{track}",
            "-f", outFormat,
            "-y", tmp,
        }) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        // Drain stderr alongside the run — a full stderr pipe stalls ffmpeg.
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch
        {
            // Client went away (or worse): stop the full-file read and drop the
            // partial output.
            try { if (!p.HasExited) p.Kill(true); } catch { }
            try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            TryDelete(tmp);
            throw;
        }
        var err = await stderr;

        var length = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
        if (p.ExitCode != 0 || length == 0)
        {
            TryDelete(tmp);
            return new Result(null, p.ExitCode, err);
        }
        File.Move(tmp, cached, overwrite: true);

        if (Interlocked.Increment(ref _writesSinceSweep) >= SweepEveryWrites)
        {
            Interlocked.Exchange(ref _writesSinceSweep, 0);
            _ = Task.Run(() => Sweep(dir));
        }
        return new Result(cached, 0, err);
    }

    /// <summary>Refresh mtime at most daily so the size-cap sweep evicts the
    /// least recently used tracks. Daily, not per hit: mtime is also the
    /// response's Last-Modified.</summary>
    private static void TouchForLru(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (now - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(1))
                File.SetLastWriteTimeUtc(path, now);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Keep the cache under <see cref="MaxCacheBytes"/> (trim the oldest
    /// down to <see cref="TrimToBytes"/>) and clear temp files a crash left.</summary>
    private static void Sweep(string dir)
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            var files = new DirectoryInfo(dir).EnumerateFiles().ToList();
            var staleTmpCutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var f in files.Where(f => f.Extension == ".tmp" && f.LastWriteTimeUtc < staleTmpCutoff))
                TryDelete(f.FullName);

            var live = files.Where(f => f.Extension != ".tmp").ToList();
            var total = live.Sum(f => f.Length);
            if (total <= MaxCacheBytes) return;
            foreach (var f in live.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= TrimToBytes) break;
                TryDelete(f.FullName);
                total -= f.Length;
            }
        }
        catch { /* best-effort */ }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
