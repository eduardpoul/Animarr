namespace Animarr.Web.Configuration;

public class AppSettings
{
    public int WatcherDelayMs { get; set; } = 2000;

    /// <summary>HDR → SDR tone mapping when an HDR source has to be re-encoded
    /// to SDR H.264: "auto" (libplacebo on Vulkan when the GPU passes the
    /// startup probe, else the CPU tonemapx), "libplacebo", "tonemapx" or
    /// "off" (old behaviour: truncate to 8-bit, washed-out colours).</summary>
    public string HdrToneMapping { get; set; } = "auto";

    public string[] VideoExtensions { get; set; } = [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".wmv", ".flv", ".webm", ".rmvb", ".rm", ".3gp", ".divx", ".mpg", ".mpeg", ".ogv", ".vob", ".f4v"];
    public string[] SubtitleExtensions { get; set; } = [".srt", ".ass", ".ssa", ".vtt", ".sub"];
    /// <summary>Standalone audio containers we treat as sideload-able external
    /// dub tracks. .mka is the Matroska audio container most fandubs ship in;
    /// the rest are raw/lossless codecs that occasionally appear loose.</summary>
    public string[] AudioExtensions { get; set; } = [".mka", ".mp3", ".flac", ".aac", ".ac3", ".eac3", ".dts", ".m4a", ".opus", ".ogg", ".wav", ".wma"];
    public string[] ImageExtensions { get; set; } = [".jpg", ".jpeg", ".png", ".webp"];
    public string Language { get; set; } = "en";
}
