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
/// </summary>
public static class ImageResizer
{
    // Posters are always ≥ w500, so a plain downscale never upscales in
    // practice; we skip dimension probing to avoid a second ffprobe spawn.
    private static readonly SemaphoreSlim _gate = new(4);

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

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                };
                // scale=W:-1 → cap width, keep aspect. Posters are bigger than the
                // target, so this only ever downscales.
                foreach (var a in new[] { "-nostdin", "-i", originalPath,
                                          "-vf", $"scale={width}:-1", "-q:v", "4",
                                          "-y", outPath })
                    psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p is null) return originalPath;
                await p.WaitForExitAsync(ct);

                return File.Exists(outPath) && new FileInfo(outPath).Length > 0 ? outPath : originalPath;
            }
            finally { _gate.Release(); }
        }
        catch
        {
            return originalPath;
        }
    }
}
