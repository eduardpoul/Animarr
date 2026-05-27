namespace Animarr.Web.Services;

/// <summary>
/// Deterministic hash → hue (0..360) for poster wash and CJK watermark tint.
/// Stable across processes (unlike string.GetHashCode which is randomised per-AppDomain since .NET Core).
/// Output deliberately biased toward warm spectrum (0..60 and 280..360) more than cold (180..220),
/// because the design palette reads better with warm tints over the crimson accent.
/// </summary>
public static class HueHash
{
    public static int For(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return 0;

        // FNV-1a 32-bit. Stable across runs; well-distributed for short ASCII / unicode strings.
        const uint offset = 2166136261;
        const uint prime  = 16777619;
        uint h = offset;
        foreach (var c in title.Trim())
            h = (h ^ c) * prime;

        return (int)(h % 360);
    }
}
