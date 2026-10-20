namespace Animarr.Web.Services;

/// <summary>
/// GET a remote file into memory with a hard size cap. Server-side fetches of
/// URLs that came from a client or a third-party API (poster/backdrop picks,
/// manual theme URLs, AnimeThemes audio) must not be able to stream an
/// unbounded body into the process — <c>GetByteArrayAsync</c> buffers the
/// whole response, whatever its size.
/// </summary>
public static class BoundedDownload
{
    public static async Task<byte[]> GetBytesAsync(
        HttpClient http, string url, long maxBytes, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)
            || (uri.IsAbsoluteUri && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException($"Only http(s) URLs can be downloaded: {url}", nameof(url));

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException($"Remote file is {declared} bytes, over the {maxBytes}-byte cap.");

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException($"Remote file exceeds the {maxBytes}-byte cap.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
