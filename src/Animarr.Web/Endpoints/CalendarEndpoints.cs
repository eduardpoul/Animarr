using Animarr.Shared;
using Animarr.Shared.Models;
using Animarr.Web.Data;
using Animarr.Web.Services;
using Animarr.Web.Services.Auth;
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
            IUserContext userCtx,
            CancellationToken ct) =>
        {
            var now  = DateTime.UtcNow;
            var from = now.AddDays(-Math.Clamp(back ?? 14, 0, 60));
            var to   = now.AddDays(Math.Clamp(ahead ?? 60, 1, 120));

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var me = await userCtx.GetCurrentUserAsync(ct);
            var items = await db.MediaItems.AsNoTracking()
                .ApplyRoleFolderFilter(me?.Role)
                .Where(m => (m.NextAirAtUtc != null && m.NextAirAtUtc >= from && m.NextAirAtUtc <= to)
                         || (m.LastAiredAtUtc != null && m.LastAiredAtUtc >= from && m.LastAiredAtUtc <= to))
                .ToListAsync(ct);

            // The file check resolves an item's files; load the resolver's DB
            // inputs for every item that may need one in a single batch, and
            // resolve each item at most once (the aired and the "next" row used
            // to resolve the same title twice).
            var mayCheck = items
                .Where(m => (m.LastAiredAtUtc is DateTime a && a >= from && a <= to && m.LastAiredEpisode is not null)
                         || (m.NextAirAtUtc is DateTime n && n >= from && n <= now && m.NextEpisodeNumber is not null))
                .Select(m => m.Id)
                .ToList();
            IReadOnlyDictionary<Guid, MediaFileResolver.Inputs> inputs = new Dictionary<Guid, MediaFileResolver.Inputs>();
            if (mayCheck.Count > 0)
            {
                try { inputs = await resolver.LoadInputsAsync(db, mayCheck, ct); }
                catch { /* no file info → every check reads "aired-waiting", as a failed resolve did */ }
            }
            var filesById = new Dictionary<Guid, MediaFileDto[]?>();
            bool EpisodeOnDisk(Guid id, int episode)
            {
                if (!filesById.TryGetValue(id, out var files))
                    filesById[id] = files = ResolveFiles(resolver, inputs, id);
                return files is not null && files.Any(f => f.AbsoluteEpisode == episode || f.Episode == episode);
            }

            var events = new List<CalendarEventDto>();
            foreach (var m in items)
            {
                var poster   = string.IsNullOrEmpty(m.PosterPath) ? null : $"/api/image?path={Uri.EscapeDataString(m.PosterPath)}";
                var backdrop = string.IsNullOrEmpty(m.FanartPath) ? null : $"/api/image?path={Uri.EscapeDataString(m.FanartPath)}";

                int? airedEpEmitted = null;
                if (m.LastAiredAtUtc is DateTime aired && aired >= from && aired <= to && m.LastAiredEpisode is int airedEp)
                {
                    var status = EpisodeOnDisk(m.Id, airedEp) ? "in-library" : "aired-waiting";
                    events.Add(new CalendarEventDto(m.Id, MediaTitles.DisplayTitle(m), poster, backdrop, airedEp, aired, status));
                    airedEpEmitted = airedEp;
                }

                // Skip a "next" that duplicates the aired row — on air day the
                // sources lag and re-announce the episode that just went out.
                if (m.NextAirAtUtc is DateTime next && next >= from && next <= to
                    && m.NextEpisodeNumber is int nextEp && nextEp != airedEpEmitted)
                {
                    // Announced but the refresh pass hasn't rolled it yet —
                    // treat a passed timestamp as freshly aired.
                    var status = next > now ? "upcoming"
                        : EpisodeOnDisk(m.Id, nextEp) ? "in-library" : "aired-waiting";
                    events.Add(new CalendarEventDto(m.Id, MediaTitles.DisplayTitle(m), poster, backdrop, nextEp, next, status));
                }
            }

            return Results.Ok(events.OrderBy(e => e.AiringAtUtc).ToArray());
        }).RequireAuthorization();

        return app;
    }

    /// <summary>The item's files through the standard 3-tier resolver, for the
    /// "is the (absolute) episode already on disk?" check — matched against
    /// either the raw or the season-offset-mapped absolute number. Null when
    /// the resolve fails (the check then reads "not on disk").</summary>
    private static MediaFileDto[]? ResolveFiles(
        MediaFileResolver resolver, IReadOnlyDictionary<Guid, MediaFileResolver.Inputs> inputs, Guid mediaItemId)
    {
        try
        {
            return inputs.TryGetValue(mediaItemId, out var input)
                ? resolver.Resolve(input)
                : Array.Empty<MediaFileDto>();
        }
        catch { return null; }
    }
}
