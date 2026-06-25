namespace Animarr.Shared.Models;

/// <summary>
/// Skip-intro/credits segment times for one episode, in seconds from the start
/// of the file (matching the player's currentTime). Any field is null when that
/// segment is unknown.
///
/// The player shows a "Skip intro" button while currentTime is in
/// [<see cref="IntroStart"/>, <see cref="IntroEnd"/>], and surfaces the
/// "next episode" card at <see cref="CreditsStart"/> — falling back to 95% of
/// the runtime when CreditsStart is null.
/// </summary>
public sealed record EpisodeSegmentsDto(
    int Season,
    int Episode,
    double? IntroStart,
    double? IntroEnd,
    double? CreditsStart,
    double? CreditsEnd,
    double? RecapStart,
    double? RecapEnd);
