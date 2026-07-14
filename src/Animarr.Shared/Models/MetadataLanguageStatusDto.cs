namespace Animarr.Shared.Models;

/// <summary>Current metadata language plus progress of the "language changed →
/// re-fetch the library" background pass, for the Settings indicator.
/// <see cref="Language"/> is the saved setting (UI code en/ru/uk/de/es).
/// <see cref="Phase"/> is one of <c>idle | running | done | error</c>;
/// <see cref="Done"/>/<see cref="Total"/> count TMDB-identified titles re-fetched.</summary>
public sealed record MetadataLanguageStatusDto(
    string  Language,
    int     Total,
    int     Done,
    string  Phase,
    string? Error);
