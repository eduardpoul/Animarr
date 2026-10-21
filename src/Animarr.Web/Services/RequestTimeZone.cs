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

    /// <summary>Real zone ids number a few hundred, but the header is
    /// client-controlled: past this many distinct values new ids are resolved
    /// per request instead of growing the cache without bound.</summary>
    private const int MaxCached = 1024;

    public static TimeZoneInfo Resolve(HttpContext? http)
    {
        var id = http?.Request.Headers[AnimarrHeaders.TimeZone].ToString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return TimeZoneInfo.Local;
        var key = id.Trim();
        if (Cache.TryGetValue(key, out var cached)) return cached ?? TimeZoneInfo.Local;

        TimeZoneInfo? tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(key); }
        catch { tz = null; }   // unknown id / no tzdata — cache the miss too
        if (Cache.Count < MaxCached) Cache.TryAdd(key, tz);
        return tz ?? TimeZoneInfo.Local;
    }

    /// <summary>Calendar day of <paramref name="utc"/> in <paramref name="tz"/>.</summary>
    public static DateTime LocalDay(DateTime utc, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).Date;
}
