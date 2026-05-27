namespace Animarr.UI.Services;

/// <summary>
/// Drives the dynamic "Continue / Play" primary CTA shown by MediaDetailHero.
/// The label flips between "Play first episode" / "Continue · EP 03" / "Play
/// again from start" etc. based on the WatchState rows for the item.
///
/// On the WASM client this is constructed from the server's
/// <c>ContinueWatchDto</c> via <see cref="FromDto"/> — keeps the original
/// hero markup binding-compatible.
/// </summary>
public record ContinueAction(
    string Label,
    string IconName,
    bool   IsResume,
    int?   SeasonHint,
    int?   EpisodeHint)
{
    public static ContinueAction PlayFirstEpisode()  => new("Play first episode",   "play",    false, null, null);
    public static ContinueAction PlayAgainFromStart() => new("Play again from start","refresh", false, null, null);
    public static ContinueAction PlayMovie()         => new("Play movie",           "play",    false, null, null);
    public static ContinueAction RewatchMovie()      => new("Rewatch from start",   "refresh", false, null, null);

    /// <summary>Bridge from the API's ContinueWatchDto into the hero's parameter shape.</summary>
    public static ContinueAction FromDto(Animarr.Shared.Models.ContinueWatchDto dto) => new(
        Label:        dto.Label,
        IconName:     dto.Kind == "continue" ? "play" : (dto.Kind == "rewatch" ? "refresh" : "play"),
        IsResume:     dto.Kind == "continue",
        SeasonHint:   dto.Season,
        EpisodeHint:  dto.Episode);
}
