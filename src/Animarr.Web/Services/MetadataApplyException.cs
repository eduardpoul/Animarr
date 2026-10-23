namespace Animarr.Web.Services;

/// <summary>
/// A manual "Apply" of a source ID (<see cref="MetadataService.ApplyManualAsync(Guid, string, string, CancellationToken)"/>)
/// that could not be done — a malformed ID or one the source doesn't know.
/// The title is left as it was. <see cref="Code"/> is machine-readable for
/// the UI (<c>id_format</c>, <c>imdb_id_format</c>, <c>tmdb_no_key</c>,
/// <c>not_found</c>, <c>unknown_source</c>); the message is for logs and
/// older clients.
/// </summary>
public sealed class MetadataApplyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
