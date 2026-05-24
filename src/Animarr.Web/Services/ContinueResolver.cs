using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Result describing the dynamic "Continue / Play" primary CTA per
/// design_handoff_animarr/CHANGELOG_FOR_CLAUDE_CODE.md §2.
///
/// The button label flips between 7 states based on whether the user has
/// watched, abandoned mid-watch, or completed the title. Hints carry the
/// resolved (season, episode) so the click handler can resume the right file.
/// </summary>
public record ContinueAction(
    string  Label,
    string  IconName,
    bool    IsResume,
    int?    SeasonHint,
    int?    EpisodeHint)
{
    public static ContinueAction PlayFirstEpisode() => new("Play first episode",   "play",    false, null, null);
    public static ContinueAction PlayAgainFromStart() => new("Play again from start", "refresh", false, null, null);
    public static ContinueAction PlayMovie()        => new("Play movie",           "play",    false, null, null);
    public static ContinueAction RewatchMovie()     => new("Rewatch from start",   "refresh", false, null, null);
}

/// <summary>
/// One on-disk-or-not episode entry feeding the resolver. Comes from the
/// MediaDetail episode loop which already knows whether a file is present.
/// </summary>
public readonly record struct ContinueEpisode(int Season, int Episode, bool Have);

/// <summary>
/// Pure resolver — no DI, no DbContext, no UI. Drop the episode-have-list +
/// the IWatchStateService in, get a ContinueAction back. Spec §2 algorithm.
/// </summary>
public static class ContinueResolver
{
    public static async Task<ContinueAction> ResolveAsync(
        IWatchStateService    watchSvc,
        MediaItem             item,
        IReadOnlyList<ContinueEpisode> episodes,
        CancellationToken     ct = default)
    {
        if (item.MediaType == MediaItemType.Movie)
            return await ResolveMovieAsync(watchSvc, item.Id, ct);

        return await ResolveSeriesAsync(watchSvc, item.Id, episodes, ct);
    }

    private static async Task<ContinueAction> ResolveMovieAsync(
        IWatchStateService watchSvc, Guid mediaItemId, CancellationToken ct)
    {
        var ws = await watchSvc.GetForMovieAsync(mediaItemId, ct);
        if (ws is null)
            return ContinueAction.PlayMovie();
        if (ws.IsWatched)
            return ContinueAction.RewatchMovie();
        if (ws.ProgressMs is > 0 && ws.RuntimeMs is > 0)
        {
            var pct = (int)Math.Round(100.0 * ws.ProgressMs.Value / ws.RuntimeMs.Value);
            pct = Math.Clamp(pct, 1, 99);
            return new ContinueAction($"Continue · {pct}%", "play", IsResume: true, null, null);
        }
        return ContinueAction.PlayMovie();
    }

    private static async Task<ContinueAction> ResolveSeriesAsync(
        IWatchStateService    watchSvc,
        Guid                  mediaItemId,
        IReadOnlyList<ContinueEpisode> episodes,
        CancellationToken     ct)
    {
        // Cheap path — nothing on disk yet, the parent UI shouldn't even render
        // the CTA but be defensive and fall back to the safe default.
        var haveSorted = episodes
            .Where(e => e.Have)
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .ToList();
        if (haveSorted.Count == 0)
            return ContinueAction.PlayFirstEpisode();

        var states = await watchSvc.GetForSeriesAsync(mediaItemId, ct);
        // (season, episode) → state, lookup once.
        var stateByKey = states
            .Where(s => s.Season is not null && s.Episode is not null)
            .ToDictionary(s => (s.Season!.Value, s.Episode!.Value));

        // Step 1: first on-disk episode the user abandoned mid-watch.
        foreach (var e in haveSorted)
        {
            if (!stateByKey.TryGetValue((e.Season, e.Episode), out var ws)) continue;
            if (!ws.IsWatched && ws.ProgressMs is > 0)
                return new ContinueAction(
                    $"Continue · EP {e.Episode:D2}",
                    "play", IsResume: true, e.Season, e.Episode);
        }

        // Step 2: at least one watched + an unwatched-after exists.
        //   This covers BOTH "next-after-last-watched in normal binge order"
        //   AND "new episodes appeared after the user finished a season" —
        //   both are fresh-start playbacks, so we use "Play · EP NN" (not
        //   "Continue · EP NN" which is reserved for resume-from-progress).
        bool anyWatched = false;
        foreach (var e in haveSorted)
        {
            stateByKey.TryGetValue((e.Season, e.Episode), out var ws);
            if (ws?.IsWatched == true)
            {
                anyWatched = true;
                continue;
            }
            if (anyWatched)
                return new ContinueAction(
                    $"Play · EP {e.Episode:D2}",
                    "play", IsResume: false, e.Season, e.Episode);
            // Untouched + no watched yet → fall through to step 3.
            break;
        }

        // Step 3: nothing watched and nothing in progress → first on-disk.
        var firstUnwatched = haveSorted.FirstOrDefault(e =>
            !stateByKey.TryGetValue((e.Season, e.Episode), out var s) || !s.IsWatched);

        if (firstUnwatched is { Episode: > 0 })
            return ContinueAction.PlayFirstEpisode() with
            {
                SeasonHint  = firstUnwatched.Season,
                EpisodeHint = firstUnwatched.Episode,
            };

        // Step 4: every on-disk episode is watched → start over from episode 1.
        // Pre-hint the first episode so click handler resumes the right file.
        var first = haveSorted.First();
        return ContinueAction.PlayAgainFromStart() with
        {
            SeasonHint  = first.Season,
            EpisodeHint = first.Episode,
        };
    }
}
