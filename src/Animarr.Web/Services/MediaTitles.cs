using Animarr.Web.Data.Models;

namespace Animarr.Web.Services;

/// <summary>
/// Display-title picker for list/card surfaces (recommendations, calendar,
/// watchlist). MediaItem.Title is normally the localized display name, but a
/// slice of donghua never went through the re-localize pass and still carries
/// the raw CJK string — unreadable on a card. Prefer a Latin/Cyrillic form
/// when the stored title is CJK-heavy.
/// </summary>
public static class MediaTitles
{
    public static string DisplayTitle(MediaItem m)
    {
        if (!IsMostlyCjk(m.Title)) return m.Title;
        if (m.EnglishTitle is { Length: > 0 } en && !IsMostlyCjk(en)) return en;
        if (m.OriginalTitle is { Length: > 0 } orig && !IsMostlyCjk(orig)) return orig;
        return m.Title;
    }

    /// <summary>True when over half of the letters are CJK codepoints.</summary>
    private static bool IsMostlyCjk(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        int letters = 0, cjk = 0;
        foreach (var ch in s)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if ((ch >= 0x3040 && ch <= 0x30FF)     // kana
             || (ch >= 0x3400 && ch <= 0x9FFF)     // CJK ideographs
             || (ch >= 0xF900 && ch <= 0xFAFF)     // CJK compat
             || (ch >= 0xAC00 && ch <= 0xD7AF))    // hangul
                cjk++;
        }
        return letters > 0 && cjk * 2 > letters;
    }
}
