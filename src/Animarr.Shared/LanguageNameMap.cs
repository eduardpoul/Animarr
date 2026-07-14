namespace Animarr.Shared;

/// <summary>
/// Maps ISO-639-1 language codes (returned by TMDB / MAL) to display names used by the catalog
/// hero meta strip ("MANDARIN", "JAPANESE", "ENGLISH"). Restricted to the languages relevant
/// to an anime / donghua / movies / serials library.
/// </summary>
public static class LanguageNameMap
{
    /// <summary>Returns the display name for an ISO-639-1 code, or the code itself uppercased
    /// when unknown (better debug surface than throwing).</summary>
    public static string? FromIso639(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return code.ToLowerInvariant() switch
        {
            "zh" => "Mandarin",
            "cn" => "Mandarin",
            "ja" => "Japanese",
            "ko" => "Korean",
            "en" => "English",
            "ru" => "Russian",
            "fr" => "French",
            "de" => "German",
            "es" => "Spanish",
            "it" => "Italian",
            "pt" => "Portuguese",
            "th" => "Thai",
            "vi" => "Vietnamese",
            "id" => "Indonesian",
            "ar" => "Arabic",
            "hi" => "Hindi",
            "tr" => "Turkish",
            _    => code.ToUpperInvariant(),
        };
    }

    /// <summary>Reverse of <see cref="FromIso639"/>: maps a display name (as stored in
    /// AudioPreferredLanguage / SubtitlePreferredLanguage, e.g. "Japanese") back to its
    /// ISO-639-1 code ("ja"). Case-insensitive. Returns null when the name isn't one of
    /// the library's known languages — the player then leaves the default track selected.</summary>
    public static string? ToIso639(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "mandarin" or "chinese" or "cantonese" => "zh",
            "japanese"   => "ja",
            "korean"     => "ko",
            "english"    => "en",
            "russian"    => "ru",
            "french"     => "fr",
            "german"     => "de",
            "spanish"    => "es",
            "italian"    => "it",
            "portuguese" => "pt",
            "thai"       => "th",
            "vietnamese" => "vi",
            "indonesian" => "id",
            "arabic"     => "ar",
            "hindi"      => "hi",
            "turkish"    => "tr",
            _            => null,
        };
    }
}
