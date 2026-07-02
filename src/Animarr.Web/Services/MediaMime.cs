namespace Animarr.Web.Services;

/// <summary>
/// Extension → MIME lookups shared by the byte-serving endpoints. Getting
/// these right matters beyond browsers: DLNA renderers match the DIDL
/// protocolInfo string against their decoder table, so "video/x-matroska"
/// for .mkv decides whether a TV even attempts playback.
/// </summary>
public static class MediaMime
{
    /// <summary>MIME type for a video container by file extension
    /// (case-insensitive, leading dot expected).</summary>
    public static string ForVideoExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".mp4"  => "video/mp4",
        ".m4v"  => "video/x-m4v",
        ".mov"  => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv"  => "video/x-matroska",
        ".avi"  => "video/x-msvideo",
        ".ts"   => "video/mp2t",
        ".m2ts" => "video/mp2t",
        ".wmv"  => "video/x-ms-wmv",
        ".flv"  => "video/x-flv",
        ".ogv"  => "video/ogg",
        _       => "application/octet-stream",
    };

    /// <summary>MIME type for a poster / backdrop image by file extension.</summary>
    public static string ForImageExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png"            => "image/png",
        ".webp"           => "image/webp",
        ".gif"            => "image/gif",
        _                 => "application/octet-stream",
    };

    /// <summary>Containers browsers play natively from a raw file URL (with
    /// Range). Everything else goes through the ffmpeg remux/HLS paths.</summary>
    public static bool IsBrowserNativeContainer(string ext) =>
        ext.ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".webm";
}
