using System.Globalization;
using System.Text.Json;
using Animarr.Shared.Models;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Segments;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Per-file facts the players read next to <c>/api/probe</c>: chapters (timeline
/// markers). Same authorization as the media endpoints — cookie or media token
/// + <see cref="MediaPathValidator"/> (library roots, the profile's folders,
/// link binding). Every answer is cached per file version server-side.
/// </summary>
internal static class PlaybackFactsEndpoints
{
    public static IEndpointRouteBuilder MapPlaybackFactsEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── /api/chapters — container chapters for timeline markers ──────────
        app.MapGet("/api/chapters", async (
            string path,
            MediaPathValidator pathValidator,
            MediaProbeCache probeCache,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;

            var chapters = MediaProbe.ParseChapters(await probeCache.GetChaptersJsonAsync(fullPath!, ct))
                .Where(c => c.StartSec >= 0)
                .OrderBy(c => c.StartSec)
                .ToList();
            double? duration = null;
            if (chapters.Count > 0 && chapters[^1].EndSec <= chapters[^1].StartSec)
                duration = FormatDuration(await probeCache.GetStreamsJsonAsync(fullPath!, ct));

            var result = new List<ChapterDto>(chapters.Count);
            for (int i = 0; i < chapters.Count; i++)
            {
                var c = chapters[i];
                var end = c.EndSec > c.StartSec ? c.EndSec
                        : i + 1 < chapters.Count ? chapters[i + 1].StartSec
                        : duration ?? c.StartSec;
                result.Add(new ChapterDto(i, string.IsNullOrWhiteSpace(c.Title) ? null : c.Title.Trim(),
                    Math.Round(c.StartSec, 3), Math.Round(Math.Max(end, c.StartSec), 3)));
            }
            return Results.Ok(result);
        })
        .WithName("GetChapters")
        .RequireAuthorization(MediaAuth.Policies.Media);

        return app;
    }

    /// <summary><c>format.duration</c> of a cached ffprobe streams JSON.</summary>
    internal static double? FormatDuration(string? probeJson)
    {
        if (string.IsNullOrWhiteSpace(probeJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(probeJson);
            return doc.RootElement.TryGetProperty("format", out var f)
                   && f.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String
                   && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
                ? s : null;
        }
        catch (JsonException) { return null; }
    }
}
