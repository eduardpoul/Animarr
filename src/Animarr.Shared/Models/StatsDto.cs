namespace Animarr.Shared.Models;

/// <summary>
/// Personal watch statistics for the signed-in user. Aggregated server-side
/// from WatchStates (what's been watched — episodes, titles, genres, studios,
/// runtime) and the WatchEvents journal (when — heatmap, streaks, monthly
/// hours). Journal-based sections may be sparse until the journal fills.
/// </summary>
public sealed record StatsDto(
    StatsSummary Summary,
    IReadOnlyList<StatBar>      TopGenres,
    IReadOnlyList<StatBar>      TopStudios,
    IReadOnlyList<TypeSlice>    ByType,
    IReadOnlyList<TopTitleStat> TopTitles,
    IReadOnlyList<HeatCell>     Heatmap,
    IReadOnlyList<MonthStat>    ByMonth);

/// <summary>Headline counters. <see cref="TotalMinutes"/> is estimated:
/// real played minutes where the journal/state knows them, else the title's
/// runtime × watched episodes.</summary>
public sealed record StatsSummary(
    int  WatchedEpisodes,
    int  WatchedTitles,
    long TotalMinutes,
    int  ActiveDays,
    int  CurrentStreak,
    int  LongestStreak);

/// <summary>One bar in the top-genres / top-studios chart: how many watched
/// titles carry this label and their estimated minutes.</summary>
public sealed record StatBar(string Label, int Titles, long Minutes);

/// <summary>Watched breakdown by media type ("anime" / "series" / "movie" /
/// "cartoon"); the client localizes the type key.</summary>
public sealed record TypeSlice(string Type, int Titles, int Episodes, long Minutes);

/// <summary>A most-watched title: episodes watched and estimated minutes.</summary>
public sealed record TopTitleStat(
    Guid    MediaItemId,
    string  Title,
    string? PosterUrl,
    int     WatchedEpisodes,
    long    Minutes);

/// <summary>One day of the activity heatmap. <see cref="Date"/> is yyyy-MM-dd
/// (UTC day); <see cref="Minutes"/> is played minutes that day.</summary>
public sealed record HeatCell(string Date, long Minutes);

/// <summary>Played minutes + episodes for one calendar month (yyyy-MM).</summary>
public sealed record MonthStat(string Month, long Minutes, int Episodes);
