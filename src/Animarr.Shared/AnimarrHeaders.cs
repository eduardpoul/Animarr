namespace Animarr.Shared;

/// <summary>Custom request headers shared by the clients and the server.</summary>
public static class AnimarrHeaders
{
    /// <summary>The client device's time zone id (IANA, or Windows on Windows
    /// hosts). The server buckets watch-history days and computes "today" for
    /// statistics in it, so the stats follow the viewer, not the container.</summary>
    public const string TimeZone = "X-Time-Zone";
}
