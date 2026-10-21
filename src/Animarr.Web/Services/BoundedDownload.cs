using System.Buffers;

namespace Animarr.Web.Services;

/// <summary>
/// GET a remote file with a hard size cap. Server-side fetches of URLs that
/// came from a client or a third-party API (poster/backdrop picks, manual
/// theme URLs, AnimeThemes audio) must not be able to stream an unbounded body
/// into the process — <c>GetByteArrayAsync</c> buffers the whole response,
/// whatever its size.
/// </summary>
public static class BoundedDownload
{
    private const int ChunkSize = 81920;

    public static async Task<byte[]> GetBytesAsync(
        HttpClient http, string url, long maxBytes, CancellationToken ct)
    {
        var uri = ValidateUrl(url);

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException($"Remote file is {declared} bytes, over the {maxBytes}-byte cap.");

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"Remote file exceeds the {maxBytes}-byte cap.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Stream a remote file straight to <paramref name="destPath"/> under the
    /// same cap. Posters/backdrops (0.1–5 MB) and theme audio (1.5–6 MB) used to
    /// go through <see cref="GetBytesAsync"/>: a growing MemoryStream (doubling
    /// copies) plus <c>ToArray()</c> ≈ 3× the file on the Large Object Heap per
    /// download, thousands of times during an identification or re-localize
    /// pass. Here the body is copied through one pooled 80 KB buffer into a
    /// unique <c>.part</c> file next to the destination, which is moved over
    /// <paramref name="destPath"/> only once complete — so a failed, capped or
    /// cancelled download never leaves a truncated file at the real path (and
    /// a reader of the old file on Linux keeps its inode). Creates the parent
    /// directory. Throws on any failure, like <see cref="GetBytesAsync"/>.
    /// </summary>
    public static async Task DownloadToFileAsync(
        HttpClient http, string url, string destPath, long maxBytes, CancellationToken ct)
    {
        var uri = ValidateUrl(url);

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException($"Remote file is {declared} bytes, over the {maxBytes}-byte cap.");

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var part = $"{destPath}.{Guid.NewGuid():N}.part";
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using (var body = await resp.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                                   bufferSize: 0, FileOptions.Asynchronous))
            {
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(chunk.AsMemory(0, ChunkSize), ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        throw new InvalidDataException($"Remote file exceeds the {maxBytes}-byte cap.");
                    await file.WriteAsync(chunk.AsMemory(0, read), ct);
                }
            }
            File.Move(part, destPath, overwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            try { File.Delete(part); } catch { /* absent after a successful move */ }
        }
    }

    private static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
            || (uri.IsAbsoluteUri && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException($"Only http(s) URLs can be downloaded: {url}", nameof(url));
        return uri;
    }
}
