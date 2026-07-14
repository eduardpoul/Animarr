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
    // Server-decided intent: "continue" | "next" | "first" | "rewatch".
    // The hero localises the button caption from this + EpisodeHint (see
    // MediaDetailHero); Label is the server's English caption, kept as a
    // fallback for any unmapped Kind.
    string Kind,
    string Label,
    string IconName,
    bool   IsResume,
    int?   SeasonHint,
    int?   EpisodeHint)
{
    /// <summary>Bridge from the API's ContinueWatchDto into the hero's parameter shape.</summary>
    public static ContinueAction FromDto(Animarr.Shared.Models.ContinueWatchDto dto) => new(
        Kind:         dto.Kind,
        Label:        dto.Label,
        IconName:     dto.Kind == "continue" ? "play" : (dto.Kind == "rewatch" ? "refresh" : "play"),
        IsResume:     dto.Kind == "continue",
        SeasonHint:   dto.Season,
        EpisodeHint:  dto.Episode);
}
