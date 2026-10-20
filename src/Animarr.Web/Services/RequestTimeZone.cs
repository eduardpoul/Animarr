using System.Collections.Concurrent;
using Animarr.Shared;

namespace Animarr.Web.Services;

/// <summary>
/// The caller's time zone, as the client's OS reports it. Every
/// <see cref="HttpAnimarrApiClient"/> request carries
/// <see cref="AnimarrHeaders.TimeZone"/> (the browser's / phone's / TV's
/// IANA id, or a Windows id on Windows hosts), so "today" and day buckets
/// follow the viewer instead of the container's TZ. Unknown or missing ids
/// fall back to the server's local zone.
/// </summary>
internal static class RequestTimeZone
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static TimeZoneInfo Resolve(HttpContext? http)
    {
        var id = http?.Request.Headers[AnimarrHeaders.TimeZone].ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return TimeZoneInfo.Local;
        return Cache.GetOrAdd(id.Trim(), static key =>
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(key); }
            catch { return null; }   // unknown id / no tzdata — cache the miss too
        }) ?? TimeZoneInfo.Local;
    }

    /// <summary>Calendar day of <paramref name="utc"/> in <paramref name="tz"/>.</summary>
    public static DateTime LocalDay(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).Date;
}
