using System.Net;
using System.Text.Json;

namespace Animarr.Shared;

/// <summary>
/// A server answer in RFC 7807 problem shape (<c>detail</c> + a
/// machine-readable <c>code</c> extension) surfaced to the UI, so it can show
/// a localized reason instead of "500 Internal Server Error".
/// </summary>
public sealed class ApiProblemException(HttpStatusCode status, string? code, string? detail)
    : HttpRequestException(detail ?? $"{(int)status} {status}", null, status)
{
    /// <summary>The problem's <c>code</c> extension (null when the body had none).</summary>
    public string? Code { get; } = code;

    /// <summary>The problem's <c>detail</c> (null when the body had none).</summary>
    public string? Detail { get; } = detail;

    /// <summary>Throws for a failed response: an <see cref="ApiProblemException"/>
    /// carrying the body's code / detail when it has them.</summary>
    public static async Task ThrowIfFailedAsync(HttpResponseMessage resp, CancellationToken ct = default)
    {
        if (resp.IsSuccessStatusCode) return;
        string? code = null, detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) detail = d.GetString();
            }
        }
        catch (JsonException) { /* not a problem body — status only */ }
        throw new ApiProblemException(resp.StatusCode, code, detail);
    }
}
