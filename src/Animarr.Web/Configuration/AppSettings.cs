namespace Animarr.Web.Configuration;

public class AppSettings
{
    public int WatcherDelayMs { get; set; } = 2000;
    public string[] VideoExtensions { get; set; } = [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".ts", ".wmv", ".flv", ".webm", ".rmvb", ".rm", ".3gp", ".divx", ".mpg", ".mpeg", ".ogv", ".vob", ".f4v"];
    public string[] SubtitleExtensions { get; set; } = [".srt", ".ass", ".ssa", ".vtt", ".sub"];
    public string[] ImageExtensions { get; set; } = [".jpg", ".jpeg", ".png", ".webp"];
    public string Language { get; set; } = "en";
}
