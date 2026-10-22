using Animarr.Shared.Models;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
using Animarr.Web.Services.Segments;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Per-file facts the players read next to <c>/api/probe</c>: chapters (timeline
/// markers) and measured media facts (black-bar crop). Same authorization as
/// the media endpoints — cookie or media token + <see cref="MediaPathValidator"/>
/// (library roots, the profile's folders, link binding). Every answer is
/// cached per file version server-side.
/// </summary>
internal static class PlaybackFactsEndpoints
{
    /// <summary>Default and ceiling of <c>/api/media-facts?wait=</c>, seconds.</summary>
    private const int DefaultWaitSec = 15;
    private const int MaxWaitSec     = 60;

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
                duration = MediaFactsService.FormatDuration(await probeCache.GetStreamsJsonAsync(fullPath!, ct));

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

        // ─── /api/media-facts — measured facts (black-bar crop, 3.3) ──────────
        // The crop is measured on the first request per file version (ffmpeg
        // cropdetect on a few sampled frames, ~1–5 s) and stored. wait = how
        // long this request may wait for it (default 15 s, 0 = don't); past
        // that video.cropStatus is "pending" and a later call gets the result.
        app.MapGet("/api/media-facts", async (
            string path,
            int? wait,
            MediaPathValidator pathValidator,
            MediaFactsService facts,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            var waitSec = Math.Clamp(wait ?? DefaultWaitSec, 0, MaxWaitSec);
            return Results.Ok(await facts.GetAsync(fullPath!, TimeSpan.FromSeconds(waitSec), ct));
        })
        .WithName("GetMediaFacts")
        .RequireAuthorization(MediaAuth.Policies.Media);

        // ─── /api/loudness — EBU R128 loudness of one audio track ─────────────
        // A full decode of the track: the first request starts the measurement
        // in the background (wait = seconds to wait for it, default 0) and
        // answers "pending"; once done it is stored per file version + track.
        app.MapGet("/api/loudness", async (
            string path,
            int? track,
            int? wait,
            MediaPathValidator pathValidator,
            LoudnessService loudness,
            CancellationToken ct) =>
        {
            var (ok, fullPath, earlyResult) = await pathValidator.ResolveLibraryFileAsync(path, ct);
            if (!ok) return earlyResult!;
            var index = track ?? 0;
            var count = await loudness.AudioTrackCountAsync(fullPath!, ct);
            if (index < 0 || index >= count)
                return Results.BadRequest($"track must be 0..{Math.Max(0, count - 1)} for this file.");
            var waitSec = Math.Clamp(wait ?? 0, 0, MaxWaitSec);
            return Results.Ok(await loudness.GetAsync(fullPath!, index, TimeSpan.FromSeconds(waitSec), ct));
        })
        .WithName("GetLoudness")
        .RequireAuthorization(MediaAuth.Policies.Media);

        return app;
    }
}
