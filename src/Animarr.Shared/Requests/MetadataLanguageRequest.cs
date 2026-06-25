namespace Animarr.Shared.Requests;

/// <summary>PUT body for changing the metadata language. <see cref="Language"/> is a
/// UI language code (en/ru/uk/de/es); the server validates it against that list.</summary>
public sealed record MetadataLanguageRequest(string Language);
