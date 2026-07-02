using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Animarr.Web.Endpoints;

/// <summary>
/// Airing calendar — the schedule fields the background refresh keeps on
/// MediaItem, flattened into per-episode events for a date window. "Aired"
/// events get a file check so the card can say "released — waiting for the
/// file" vs "already in the library".
/// </summary>
internal static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Calendar, async (
            int? back,
            int? ahead,
            IDbContextFactory<AppDbContext> dbFactory,
            MediaFileResolver resolver,
            CancellationToken ct) =>
        {
            var now  = DateTime.UtcNow;
            var from = now.AddDays(-Math.Clamp(back ?? 14, 0, 60));
            var to   = now.AddDays(Math.Clamp(ahead ?? 60, 1, 120));

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var items = await db.MediaItems.AsNoTracking()
                .Where(m => (m.NextAirAtUtc != null && m.NextAirAtUtc >= from && m.NextAirAtUtc <= to)
                         || (m.LastAiredAtUtc != null && m.LastAiredAtUtc >= from && m.LastAiredAtUtc <= to))
                .ToListAsync(ct);

            var events = new List<CalendarEventDto>();
            foreach (var m in items)
            {
                var poster   = string.IsNullOrEmpty(m.PosterPath) ? null : $"/api/image?path={Uri.EscapeDataString(m.PosterPath)}";
                var backdrop = string.IsNullOrEmpty(m.FanartPath) ? null : $"/api/image?path={Uri.EscapeDataString(m.FanartPath)}";

                if (m.NextAirAtUtc is DateTime next && next >= from && next <= to && m.NextEpisodeNumber is int nextEp)
                {
                    // Announced but the refresh pass hasn't rolled it yet —
                    // treat a passed timestamp as freshly aired.
                    var status = next > now ? "upcoming"
                        : await EpisodeOnDiskAsync(resolver, m.Id, nextEp, ct) ? "in-library" : "aired-waiting";
                    events.Add(new CalendarEventDto(m.Id, m.Title, poster, backdrop, nextEp, next, status));
                }

                if (m.LastAiredAtUtc is DateTime aired && aired >= from && aired <= to && m.LastAiredEpisode is int airedEp)
                {
                    var status = await EpisodeOnDiskAsync(resolver, m.Id, airedEp, ct) ? "in-library" : "aired-waiting";
                    events.Add(new CalendarEventDto(m.Id, m.Title, poster, backdrop, airedEp, aired, status));
                }
            }

            return Results.Ok(events.OrderBy(e => e.AiringAtUtc).ToArray());
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Is the (absolute) episode already on disk? Resolves the item's
    /// files through the standard 3-tier resolver and matches either the raw
    /// or the season-offset-mapped absolute number.</summary>
    private static async Task<bool> EpisodeOnDiskAsync(
        MediaFileResolver resolver, Guid mediaItemId, int episode, CancellationToken ct)
    {
        try
        {
            var files = await resolver.ResolveAsync(mediaItemId, ct);
            return files.Any(f => f.AbsoluteEpisode == episode || f.Episode == episode);
        }
        catch { return false; }
    }
}
