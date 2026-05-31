namespace Animarr.Web.Data.Models;

/// <summary>
/// Singleton row (Id = 1) that stores user-editable torrent engine settings.
/// </summary>
public class TorrentConfig
{
    public int Id { get; set; } = 1;

    public int GlobalDownloadLimit { get; set; } = 0;   // bytes/s, 0=unlimited
    public int GlobalUploadLimit { get; set; } = 0;     // bytes/s, 0=unlimited

    public int ListenPort { get; set; } = 6881;
    public int MaxConnections { get; set; } = 200;

    public bool EnableDHT { get; set; } = true;
    public bool EnableLSD { get; set; } = true;
    public bool EnableUPnP { get; set; } = true;

    public bool StopSeedingAfterDone { get; set; } = false;
    public double StopSeedingRatio { get; set; } = 2.0;

    public bool DefaultStopAfterDownload { get; set; } = false;

    public string CacheDirectory { get; set; } = "/app/data/torrent-cache";
}
