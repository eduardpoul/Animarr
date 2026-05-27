namespace Animarr.Shared.Models;

/// <summary>
/// Single key/value entry from the <c>AppConfig</c> table. Keys come from
/// <see cref="AppConfigKeys"/>; values are opaque strings (the consumer
/// parses based on the key's known type).
/// </summary>
public sealed record AppConfigEntryDto(string Key, string? Value);
