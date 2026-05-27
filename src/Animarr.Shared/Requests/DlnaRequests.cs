namespace Animarr.Shared.Requests;

/// <summary>"Send to TV" — pushes a media file to a discovered DLNA renderer.</summary>
public sealed record DlnaPlayRequest(
    string RendererUdn,
    string FilePath,
    long? StartTimeMs);
