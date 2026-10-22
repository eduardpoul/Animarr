namespace Animarr.App.Services;

/// <summary>
/// Display text for audio / subtitle tracks in the TV player's sheets: full
/// language names (the HUD is Russian), codec short names, channel layouts —
/// and a language-code normaliser so ffprobe's ISO 639-2 tags ("rus", "ger")
/// can be matched against Media3's normalised ones ("ru", "de").
/// </summary>
internal static class TrackLabels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "Японский",    ["en"] = "Английский",  ["ru"] = "Русский",     ["uk"] = "Украинский",
        ["zh"] = "Китайский",   ["ko"] = "Корейский",   ["de"] = "Немецкий",    ["es"] = "Испанский",
        ["fr"] = "Французский", ["it"] = "Итальянский", ["pt"] = "Португальский", ["pl"] = "Польский",
        ["tr"] = "Турецкий",    ["ar"] = "Арабский",    ["hi"] = "Хинди",       ["th"] = "Тайский",
        ["vi"] = "Вьетнамский", ["id"] = "Индонезийский", ["ms"] = "Малайский", ["nl"] = "Нидерландский",
        ["sv"] = "Шведский",    ["no"] = "Норвежский",  ["nb"] = "Норвежский",  ["da"] = "Датский",
        ["fi"] = "Финский",     ["cs"] = "Чешский",     ["sk"] = "Словацкий",   ["hu"] = "Венгерский",
        ["ro"] = "Румынский",   ["el"] = "Греческий",   ["he"] = "Иврит",       ["bg"] = "Болгарский",
        ["sr"] = "Сербский",    ["hr"] = "Хорватский",  ["kk"] = "Казахский",   ["be"] = "Белорусский",
        ["ka"] = "Грузинский",  ["hy"] = "Армянский",   ["az"] = "Азербайджанский", ["uz"] = "Узбекский",
        ["lt"] = "Литовский",   ["lv"] = "Латышский",   ["et"] = "Эстонский",   ["fa"] = "Персидский",
        ["ta"] = "Тамильский",  ["te"] = "Телугу",      ["tl"] = "Тагальский",  ["la"] = "Латынь",
    };

    // ISO 639-2 (T and B forms) → 639-1, for the languages above.
    private static readonly Dictionary<string, string> ThreeToTwo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpn"] = "ja", ["eng"] = "en", ["rus"] = "ru", ["ukr"] = "uk", ["zho"] = "zh", ["chi"] = "zh",
        ["kor"] = "ko", ["deu"] = "de", ["ger"] = "de", ["spa"] = "es", ["fra"] = "fr", ["fre"] = "fr",
        ["ita"] = "it", ["por"] = "pt", ["pol"] = "pl", ["tur"] = "tr", ["ara"] = "ar", ["hin"] = "hi",
        ["tha"] = "th", ["vie"] = "vi", ["ind"] = "id", ["msa"] = "ms", ["may"] = "ms", ["nld"] = "nl",
        ["dut"] = "nl", ["swe"] = "sv", ["nor"] = "no", ["nob"] = "nb", ["dan"] = "da", ["fin"] = "fi",
        ["ces"] = "cs", ["cze"] = "cs", ["slk"] = "sk", ["slo"] = "sk", ["hun"] = "hu", ["ron"] = "ro",
        ["rum"] = "ro", ["ell"] = "el", ["gre"] = "el", ["heb"] = "he", ["bul"] = "bg", ["srp"] = "sr",
        ["hrv"] = "hr", ["kaz"] = "kk", ["bel"] = "be", ["kat"] = "ka", ["geo"] = "ka", ["hye"] = "hy",
        ["arm"] = "hy", ["aze"] = "az", ["uzb"] = "uz", ["lit"] = "lt", ["lav"] = "lv", ["est"] = "et",
        ["fas"] = "fa", ["per"] = "fa", ["tam"] = "ta", ["tel"] = "te", ["tgl"] = "tl", ["fil"] = "tl",
        ["lat"] = "la",
    };

    /// <summary>"rus" / "ru" / "ru-RU" → "ru"; "und" / "" → null.</summary>
    public static string? NormLang(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        int dash = c.IndexOfAny(new[] { '-', '_' });
        if (dash > 0) c = c[..dash];
        if (c is "und" or "unk" or "mis" or "mul" or "zxx" or "xx") return null;
        return ThreeToTwo.TryGetValue(c, out var two) ? two : c;
    }

    /// <summary>Full language name, the raw code upper-cased when unknown,
    /// empty for "und".</summary>
    public static string LangName(string? code)
    {
        var n = NormLang(code);
        if (n is null) return string.Empty;
        return Names.TryGetValue(n, out var name) ? name : code!.Trim().ToUpperInvariant();
    }

    public static string? Channels(int channels) => channels switch
    {
        1 => "Моно",
        2 => "Стерео",
        3 => "2.1",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        _ => channels > 0 ? $"{channels} кан." : null,
    };

    /// <summary>ffprobe codec_name → short display name.</summary>
    public static string? Codec(string? ffprobeCodec) => ffprobeCodec?.ToLowerInvariant() switch
    {
        null or "" => null,
        "aac" => "AAC", "ac3" => "AC3", "eac3" => "E-AC3", "dts" => "DTS", "truehd" => "TrueHD",
        "mp3" => "MP3", "mp2" => "MP2", "opus" => "Opus", "vorbis" => "Vorbis", "flac" => "FLAC",
        "alac" => "ALAC",
        var c when c.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        "subrip" => "SRT", "ass" => "ASS", "ssa" => "SSA", "webvtt" => "VTT", "mov_text" => "TX3G",
        "text" => "TXT", "microdvd" => "SUB",
        "hdmv_pgs_subtitle" => "PGS", "dvd_subtitle" => "VobSub", "dvb_subtitle" => "DVB",
        "dvb_teletext" => "Teletext", "xsub" => "XSUB",
        var c => c.ToUpperInvariant(),
    };

    /// <summary>Media3 MIME type (or Format.codecs) → short display name.</summary>
    public static string? CodecFromMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return null;
        var s = mime.ToLowerInvariant();
        if (s.Contains("eac3") || s.Contains("ec-3")) return "E-AC3";
        if (s.Contains("ac3") || s.Contains("ac-3")) return "AC3";
        if (s.Contains("mp4a") || s.Contains("aac")) return "AAC";
        if (s.Contains("dts")) return "DTS";
        if (s.Contains("truehd")) return "TrueHD";
        if (s.Contains("opus")) return "Opus";
        if (s.Contains("flac")) return "FLAC";
        if (s.Contains("vorbis")) return "Vorbis";
        if (s.Contains("mpeg-l2")) return "MP2";
        if (s.Contains("mpeg")) return "MP3";
        if (s.Contains("raw") || s.Contains("pcm")) return "PCM";
        if (s.Contains("subrip")) return "SRT";
        if (s.Contains("ssa") || s.Contains("ass")) return "ASS";
        if (s.Contains("vtt")) return "VTT";
        if (s.Contains("pgs")) return "PGS";
        if (s.Contains("vobsub")) return "VobSub";
        if (s.Contains("dvbsubs")) return "DVB";
        if (s.Contains("tx3g")) return "TX3G";
        return null;
    }

    /// <summary>Subtitle codecs ffmpeg turns into WebVTT (/api/subtitle) —
    /// bitmap ones (PGS, VobSub, DVB) can't become text.</summary>
    public static bool IsTextSubtitle(string? ffprobeCodec) => ffprobeCodec?.ToLowerInvariant() is
        "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text" or "microdvd"
        or "subviewer" or "subviewer1" or "sami" or "realtext" or "jacosub" or "mpl2" or "pjs"
        or "vplayer" or "stl";
}
