namespace Animarr.Shared.Models;

/// <summary>
/// DLNA Media Renderer discovered on the local network ("Send to TV"
/// dropdown). One per SSDP NOTIFY/Response within the last 60 seconds.
/// </summary>
public sealed record DlnaRendererDto(
    string Udn,            // unique device name
    string FriendlyName,
    string ModelName,
    string Manufacturer,
    Uri    ControlUrl,
    DateTime LastSeenUtc);
