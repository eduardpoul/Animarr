using System.Diagnostics;

namespace Animarr.Web.Services;

/// <summary>
/// On-the-fly, disk-cached image downscaler behind <c>/api/image?w=</c>. The
/// catalog grid renders up to 240 poster cards; serving each as a full-size
/// (w500+) TMDB poster makes the client decode 240 large bitmaps at once —
/// hundreds of MB of GPU textures that thrash a ~650 MB-free Android-TV box
/// (multi-second GPU stalls in <c>dumpsys gfxinfo</c>). Capping each to roughly
/// the card's display width cuts that by ~5–7×.
///
/// Resizing is done with <c>ffmpeg</c> — already a hard dependency (HLS +
/// episode thumbnails), so no extra NuGet / no image-library CVEs. Variants are
/// cached on disk keyed by source path + width + mtime + length, so the resize
/// runs once per (image, width) and a re-identification (file overwrite) busts
/// the key automatically. Lazy-loaded &lt;img&gt; + content-visibility mean only
/// the visible cards ever request a resize, so the work is naturally paced; a
/// small semaphore caps concurrent ffmpeg spawns regardless. Every failure path
/// returns the original so a malformed/odd image never 500s the endpoint.
///
/// Process hygiene: ffmpeg's pipes are drained (an unread stderr — ~4.2 KB of
/// banner + stats — filled the 4 KB Windows pipe buffer and hung every resize
/// with its gate slot held), output goes to a temp file that is moved into
/// place only after a clean exit (a half-written JPEG used to pass the
/// "Length &gt; 0" check and be served as immutable for a year), and a cancelled
/// or stuck run is killed before its gate slot is released.
/// </summary>
public static class ImageResizer
{
    // Posters are always ≥ w500, so a plain downscale never upscales in
    // practice; we skip dimension probing to avoid a second ffprobe spawn.
    private static readonly SemaphoreSlim _gate = new(4);

    /// <summary>A single-image decode+scale takes well under a second; anything
    /// near this is a wedged ffmpeg that must not hold a gate slot forever.</summary>
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Return a path to a width-capped JPEG variant of
    /// <paramref name="originalPath"/> (cached under
    /// <paramref name="cacheRoot"/>/resized), or the original path when no
    /// resize is warranted (vector/animated formats) or on any error.</summary>
    public static async Task<string> GetResizedAsync(
        string originalPath, int width, string cacheRoot, CancellationToken ct = default)
    {
        try
        {
            if (width <= 0 || width > 2000) return originalPath;

            var ext = Path.GetExtension(originalPath).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp")) return originalPath;

            var fi = new FileInfo(originalPath);
            if (!fi.Exists) return originalPath;

            var dir = Path.Combine(cacheRoot, "resized");
            Directory.CreateDirectory(dir);

            var key  = $"{originalPath}|{width}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
            var outPath = Path.Combine(dir, $"{hash}_w{width}.jpg");

            if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;

            await _gate.WaitAsync(ct);
            try
            {
                // Re-check after the gate — a concurrent request may have built it.
                if (File.Exists(outPath) && new FileInfo(outPath).Length > 0) return outPath;

                // Unique temp name (keeps the .jpg extension so ffmpeg picks the
                // JPEG muxer); moved over outPath only once ffmpeg exits cleanly.
                var tmpPath = Path.Combine(dir, $"{hash}_w{width}.{Guid.NewGuid():N}.tmp.jpg");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        RedirectStandardError  = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                    };
                    // scale=W:-1 → cap width, keep aspect. Posters are bigger than the
                    // target, so this only ever downscales. One thread each for
                    // decode/filter/encode: a poster is tiny, and spinning up
                    // core-count threads per spawn (×4 gate slots) just steals CPU
                    // from a live transcode during a cold catalog load.
                    foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-nostats",
                                              "-threads", "1", "-i", originalPath,
                                              "-filter_threads", "1",
                                              "-vf", $"scale={width}:-1", "-q:v", "4",
                                              "-threads", "1",
                                              "-y", tmpPath })
                        psi.ArgumentList.Add(a);

                    using var p = Process.Start(psi);
                    if (p is null) return originalPath;
                    if (!await RunToExitAsync(p, ct)) return originalPath;
                    if (p.ExitCode != 0 || !File.Exists(tmpPath) || new FileInfo(tmpPath).Length == 0)
                        return originalPath;

                    try { File.Move(tmpPath, outPath, overwrite: true); }
                    catch (IOException) when (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
                    {
                        // A concurrent request for the same variant won the race and
                        // its file is being served (Windows refuses to replace an
                        // open file) — theirs is identical, use it.
                    }
                    return File.Exists(outPath) && new FileInfo(outPath).Length > 0 ? outPath : originalPath;
                }
                finally
                {
                    try { File.Delete(tmpPath); } catch { /* best-effort; absent after a successful move */ }
                }
            }
            finally { _gate.Release(); }
        }
        catch
        {
            return originalPath;
        }
    }

    /// <summary>Drain both pipes and wait for exit. On cancel or timeout the
    /// process is killed and reaped before returning, so the caller's gate slot
    /// is only released once ffmpeg is really gone. Throws on cancellation;
    /// false on timeout.</summary>
    private static async Task<bool> RunToExitAsync(Process p, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FfmpegTimeout);
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            return true;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
