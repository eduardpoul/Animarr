namespace Animarr.Shared.Requests;

/// <summary>PUT body for <c>/api/media/{id}/files/mapping</c>. Sets a manual
/// (season, episode) override for the single video file at
/// <see cref="FilePath"/>. A null <see cref="Season"/> or <see cref="Episode"/>
/// keeps the deterministic value for that field, so a partial correction
/// (e.g. fix the season only) composes with the on-the-fly parse.</summary>
public sealed record EpisodeMappingRequest(
    string FilePath,
    int?   Season,
    int?   Episode);
