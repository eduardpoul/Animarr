using System.Globalization;
using System.Text.RegularExpressions;

namespace Animarr.Web.Services;

/// <summary>
/// The pure file-name heuristics behind <see cref="ExternalTrackService"/>:
/// which file is a sidecar of which video, which language a name or a folder
/// path says, the <c>DELAY 500ms</c> tag, the signs/forced flavour of a
/// subtitle and the stable identity of a dub across episodes. No I/O — every
/// method is a function of the strings it is given.
/// </summary>
public static partial class ExternalTrackNaming
{
    // ─── Languages ────────────────────────────────────────────────────────────

    /// <summary>Language tokens seen in fandub / sub file and folder names
    /// (Latin + Cyrillic) → (ISO 639-2 code, display name). The two-letter
    /// ISO 639-1 forms are <i>weak</i>: "de", "es", "uk" are also ordinary words
    /// ("La Casa de Papel") — see <see cref="ExtractLanguage"/> for where they
    /// still count.</summary>
    private static readonly Dictionary<string, (string code, string name)> LangMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rus"]=("rus","Russian"), ["ru"]=("rus","Russian"), ["russian"]=("rus","Russian"), ["рус"]=("rus","Russian"), ["русский"]=("rus","Russian"), ["русские"]=("rus","Russian"), ["русская"]=("rus","Russian"), ["русское"]=("rus","Russian"),
        ["eng"]=("eng","English"), ["en"]=("eng","English"), ["english"]=("eng","English"), ["англ"]=("eng","English"), ["английский"]=("eng","English"), ["английские"]=("eng","English"), ["английская"]=("eng","English"),
        ["jpn"]=("jpn","Japanese"), ["jp"]=("jpn","Japanese"), ["ja"]=("jpn","Japanese"), ["jap"]=("jpn","Japanese"), ["japanese"]=("jpn","Japanese"), ["японский"]=("jpn","Japanese"), ["японская"]=("jpn","Japanese"),
        ["ukr"]=("ukr","Ukrainian"), ["ua"]=("ukr","Ukrainian"), ["uk"]=("ukr","Ukrainian"), ["ukrainian"]=("ukr","Ukrainian"), ["укр"]=("ukr","Ukrainian"), ["украинский"]=("ukr","Ukrainian"), ["украинская"]=("ukr","Ukrainian"), ["украинские"]=("ukr","Ukrainian"),
        ["ger"]=("ger","German"), ["deu"]=("ger","German"), ["de"]=("ger","German"), ["german"]=("ger","German"),
        ["fre"]=("fre","French"), ["fra"]=("fre","French"), ["fr"]=("fre","French"), ["french"]=("fre","French"),
        ["spa"]=("spa","Spanish"), ["es"]=("spa","Spanish"), ["spanish"]=("spa","Spanish"),
        ["ita"]=("ita","Italian"), ["it"]=("ita","Italian"), ["italian"]=("ita","Italian"),
        ["por"]=("por","Portuguese"), ["pt"]=("por","Portuguese"), ["portuguese"]=("por","Portuguese"),
        ["kor"]=("kor","Korean"), ["ko"]=("kor","Korean"), ["korean"]=("kor","Korean"),
        ["chi"]=("chi","Chinese"), ["zho"]=("chi","Chinese"), ["zh"]=("chi","Chinese"), ["chinese"]=("chi","Chinese"),
    };

    /// <summary>Two-letter codes are weak; three or more letters (and every
    /// Cyrillic form) may stand anywhere.</summary>
    private static bool IsWeak(string token) => token.Length <= 2;

    /// <summary>
    /// Best-effort language of an external file, most specific source first:
    /// <list type="number">
    /// <item>the sidecar suffix (<c>video.rus.srt</c>, <c>video.de.srt</c>) —
    ///   every token counts, the two-letter ISO codes included: that is the
    ///   Plex/Jellyfin naming convention;</item>
    /// <item>the folder path below the title root, deepest folder first
    ///   (<c>Rus Sub/Signs/</c> → "Signs" says nothing, "Rus Sub" says
    ///   Russian);</item>
    /// <item>what the file name adds to the video's own name
    ///   (<c>05 [RUS].mka</c> next to <c>Show - 05.mkv</c>).</item>
    /// </list>
    /// In 2 and 3 a two-letter code only counts when it is written in capitals
    /// ("[DE]", "UA Dub") or is the whole folder name — lower-case "de" / "es" /
    /// "uk" inside a phrase are words, not languages.
    /// </summary>
    public static (string Code, string Name)? ExtractLanguage(
        string? suffix, IEnumerable<string> dirSegmentsDeepestFirst, string? stemRemainder)
    {
        if (!string.IsNullOrWhiteSpace(suffix))
            foreach (var token in Tokens(suffix))
                if (LangMap.TryGetValue(token, out var v)) return v;

        foreach (var segment in dirSegmentsDeepestFirst)
            if (FromPhrase(segment) is { } v) return v;

        return string.IsNullOrWhiteSpace(stemRemainder) ? null : FromPhrase(stemRemainder);
    }

    private static (string, string)? FromPhrase(string phrase)
    {
        var tokens = Tokens(phrase).ToList();
        foreach (var token in tokens)
        {
            if (!LangMap.TryGetValue(token, out var v)) continue;
            if (!IsWeak(token) || tokens.Count == 1 || IsAllUpper(token)) return v;
        }
        return null;
    }

    /// <summary>A container's language tag ("rus", "ukr", "en") → the same
    /// (code, name) pair the name heuristics produce; null for "und" and tags
    /// outside <see cref="LangMap"/>.</summary>
    public static (string Code, string Name)? FromLanguageTag(string? tag)
        => !string.IsNullOrWhiteSpace(tag) && LangMap.TryGetValue(tag.Trim(), out var v) ? v : null;

    /// <summary>True when <paramref name="text"/> already names the language
    /// <paramref name="code"/> ("Rus Sub" for rus) — the label then doesn't get
    /// "Russian · " in front.</summary>
    public static bool MentionsLanguage(string text, string code)
        => Tokens(text).Any(t => LangMap.TryGetValue(t, out var v) && v.code == code);

    /// <summary>A label core that is nothing but a language word ("rus").</summary>
    public static bool IsBareLanguageWord(string core)
    {
        var tokens = Tokens(core).ToList();
        return tokens.Count == 1 && LangMap.ContainsKey(tokens[0]);
    }

    private static bool IsAllUpper(string token)
    {
        foreach (var c in token) if (!char.IsUpper(c)) return false;
        return true;
    }

    private static IEnumerable<string> Tokens(string text)
        => NonLetterRunRegex().Split(text).Where(t => t.Length > 0);

    // ─── Sidecars and episode numbers ─────────────────────────────────────────

    /// <summary>Tier-0 test: <paramref name="candStem"/> belongs to the video
    /// <paramref name="videoStem"/> when it equals it, or extends it with a
    /// separator-delimited suffix (<c>video</c> → <c>video.rus</c> /
    /// <c>video - eng</c>). The suffix feeds the language / label heuristics.
    /// A suffix that opens with a bare number right after a stem that ends in
    /// one continues that number instead — <c>Show - 01.5</c> is episode 1.5,
    /// not a sidecar of <c>Show - 01</c>.</summary>
    public static bool TryMatchSidecar(string candStem, string videoStem, out string? suffix)
    {
        suffix = null;
        if (string.Equals(candStem, videoStem, StringComparison.OrdinalIgnoreCase))
            return true;
        if (videoStem.Length == 0 || candStem.Length <= videoStem.Length
            || !candStem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
            return false;

        var sep = candStem[videoStem.Length];
        if (sep is not ('.' or ' ' or '_' or '-')) return false;

        var rest = candStem[(videoStem.Length + 1)..].Trim(' ', '.', '_', '-');
        var first = FirstToken(rest);
        if (char.IsAsciiDigit(videoStem[^1]) && first.Length > 0 && first.All(char.IsAsciiDigit))
            return false;
        suffix = rest;
        return true;
    }

    private static string FirstToken(string s)
    {
        var end = s.AsSpan().IndexOfAny(" ._-");
        return end < 0 ? s : s[..end];
    }

    /// <summary>
    /// True when exactly one of the two names numbers the episode with a
    /// decimal part (<c>01.5</c>, <c>E01.5</c>) — a recap / special between two
    /// episodes that the episode regexes read as plain episode 1. Audio tags
    /// such as <c>AAC2.0</c>, <c>DDP5.1</c>, <c>AAC 5.1</c> or <c>5.1ch</c> are not
    /// episode numbers.
    /// </summary>
    public static bool IsDecimalEpisodeVariant(string candStem, int episode, string videoStem)
        => HasDecimalEpisode(candStem, episode) != HasDecimalEpisode(videoStem, episode);

    private static bool HasDecimalEpisode(string stem, int episode)
    {
        var rx = new Regex(@"(?<!\d)0*" + episode.ToString(CultureInfo.InvariantCulture) + @"[.,]\d(?!\d)",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        foreach (Match m in rx.Matches(stem))
        {
            int i = m.Index, end = m.Index + m.Length;
            if (stem.AsSpan(end).StartsWith("ch", StringComparison.OrdinalIgnoreCase))
                continue;                                                    // "5.1ch"
            if (i > 0)
            {
                var p = stem[i - 1];
                bool episodeMarker = p is 'e' or 'E'
                    || (p is 'p' or 'P' && i >= 2 && stem[i - 2] is 'e' or 'E');  // E01.5, Ep01.5
                if (char.IsLetter(p) && !episodeMarker) continue;           // AAC2.0, DDP5.1
                if (!char.IsLetter(p) && AudioWordBefore(stem, i)) continue; // "AAC 5.1"
            }
            return true;
        }
        return false;
    }

    private static bool AudioWordBefore(string stem, int index)
    {
        var head = stem[..index].TrimEnd(' ', '_', '.', '-');
        var m = TrailingWordRegex().Match(head);
        return m.Success && AudioWordRegex().IsMatch(m.Value);
    }

    // ─── DELAY tag ────────────────────────────────────────────────────────────

    /// <summary>The audio delay a demuxer (eac3to, MKVToolNix) wrote into the
    /// name — <c>Track 2 - Russian DELAY 500ms.ac3</c>, <c>[DELAY -120ms]</c> —
    /// in milliseconds; null when the name has none.</summary>
    public static int? ParseDelayMs(string name)
    {
        var m = DelayRegex().Match(name);
        return m.Success && int.TryParse(m.Groups["ms"].ValueSpan, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var ms) ? ms : null;
    }

    /// <summary><paramref name="s"/> without its DELAY tag.</summary>
    public static string StripDelay(string s) => DelayRegex().Replace(s, " ");

    // ─── Subtitle flavour ─────────────────────────────────────────────────────

    /// <summary>"signs" (signs / songs / надписи), "forced", or "full" for a
    /// subtitle file, from its suffix, folders and name.</summary>
    public static string SubtitleTypeOf(params string?[] sources)
    {
        var tokens = sources.Where(s => !string.IsNullOrWhiteSpace(s)).SelectMany(s => Tokens(s!))
            .Select(t => t.ToLowerInvariant()).ToList();
        if (tokens.Any(t => t is "signs" or "sign" or "songs" or "надписи" or "надпись" or "написи"))
            return "signs";
        if (tokens.Any(t => t is "forced" or "форсированные" or "форсированный"))
            return "forced";
        return "full";
    }

    // ─── Folders, identity, labels ────────────────────────────────────────────

    /// <summary>Folder names from <paramref name="root"/> down to
    /// <paramref name="candDir"/>, minus the leading folders the candidate
    /// shares with the video (<c>Season 1/Russian Dub</c> next to a video in
    /// <c>Season 1</c> → <c>Russian Dub</c>). Top-down order.</summary>
    public static IReadOnlyList<string> RelativeSegments(string root, string candDir, string videoDir)
    {
        var cand  = Segments(root, candDir);
        var video = Segments(root, videoDir);
        int common = 0;
        while (common < cand.Length && common < video.Length
               && string.Equals(cand[common], video[common], StringComparison.OrdinalIgnoreCase))
            common++;
        return cand[common..];
    }

    /// <summary>The <see cref="RelativeSegments"/> that identify a dub /
    /// subtitle group: season folders dropped (<c>Russian Dub/Season 2</c> is
    /// the same dub as <c>Russian Dub/Season 1</c>).</summary>
    public static IReadOnlyList<string> GroupSegments(IReadOnlyList<string> relativeSegments)
        => relativeSegments.Where(s => !IsSeasonSegment(s)).ToList();

    /// <summary>"Season 2", "S02", "Сезон 2", "2 сезон", "Specials".</summary>
    public static bool IsSeasonSegment(string segment) => SeasonSegmentRegex().IsMatch(segment.Trim());

    /// <summary><paramref name="dir"/> relative to <paramref name="root"/>,
    /// '/'-separated; "" for the root itself.</summary>
    public static string RelativeFolder(string root, string dir) => string.Join('/', Segments(root, dir));

    private static string[] Segments(string root, string dir)
    {
        string rel;
        try { rel = Path.GetRelativePath(root, dir); }
        catch { return []; }
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return [];
        return rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Stable identity of an external track's group — the same dub / sub set on
    /// every episode, so a remembered choice can be found again on the next one:
    /// <list type="bullet">
    /// <item><c>sidecar:ukr</c> — Tier 0, <c>Show.S01E02.ukr.mka</c>;</item>
    /// <item><c>dir:russian dub</c> — Tier 1, <c>Russian Dub/02.mka</c> (season
    ///   folders dropped);</item>
    /// <item><c>dir:russian dub|sidecar:rus</c> — the video's name in a
    ///   sub-folder (<c>Russian Dub/Show.S01E02.rus.mka</c>);</item>
    /// <item><c>name:show - # [anilibria]</c> — an episode-numbered file next to
    ///   the video, numbers masked.</item>
    /// </list>
    /// </summary>
    public static string GroupKey(bool sameDir, bool stemMatch, IReadOnlyList<string> groupSegments,
        string? suffix, string candStem)
    {
        var suffixKey = "sidecar:" + Normalize(StripDelay(suffix ?? ""));
        if (sameDir && stemMatch) return suffixKey;
        if (groupSegments.Count > 0)
        {
            var dirKey = "dir:" + Normalize(string.Join(" / ", groupSegments));
            return stemMatch ? dirKey + "|" + suffixKey : dirKey;
        }
        return stemMatch ? suffixKey : "name:" + NameTemplate(candStem);
    }

    /// <summary>A group key without the <c>|name:…</c> part the service adds
    /// when two files of one episode share a key — what a remembered key is
    /// matched on when the next episode has only one of them.</summary>
    public static string BaseGroupKey(string key)
    {
        var i = key.IndexOf("|name:", StringComparison.Ordinal);
        return i < 0 ? key : key[..i];
    }

    /// <summary>A file name with its varying parts masked: digits → '#', CRC
    /// tags and the DELAY tag removed.</summary>
    public static string NameTemplate(string stem)
    {
        var s = CrcRegex().Replace(StripDelay(stem), " ");
        return DigitRunRegex().Replace(Normalize(s), "#");
    }

    /// <summary>Lower-case, punctuation runs collapsed to one space.</summary>
    public static string Normalize(string s)
        => NonWordRunRegex().Replace(s.ToLowerInvariant(), " ").Trim();

    /// <summary>The tokens of <paramref name="candStem"/> that are not part of
    /// the video's own name (<c>Show - 05 [RUS]</c> vs <c>Show - 05</c> →
    /// "RUS"), original case kept.</summary>
    public static string StemRemainder(string candStem, string videoStem)
    {
        var own = new HashSet<string>(Tokens(videoStem), StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', Tokens(StripDelay(candStem)).Where(t => !own.Contains(t)));
    }

    /// <summary>
    /// Display label: the folder path of a Tier-1 match ("Rus Sub / Signs"), the
    /// suffix of a sidecar ("eng dub"), or what the file name adds to the
    /// video's ("AniLibria"); prefixed with the language name unless the core
    /// already says it; just the language (or "External audio / subtitle") when
    /// nothing else is known. <paramref name="nameHint"/> is appended to a
    /// folder path as its last element — the file's own name where that names
    /// the dub (<c>RUS Sound/AniLibria.mka</c> of a movie).
    /// </summary>
    public static string BuildLabel(bool isAudio, IReadOnlyList<string> groupSegments, string? suffix,
        string? stemRemainder, (string Code, string Name)? lang, string? nameHint = null)
    {
        string core = groupSegments.Count > 0
            ? string.Join(" / ", groupSegments.Append(StripDelay(nameHint ?? "")).Select(CleanLabel).Where(s => s.Length > 0))
            : CleanLabel(StripDelay(suffix ?? stemRemainder ?? ""));

        var kindFallback = isAudio ? "External audio" : "External subtitle";
        if (string.IsNullOrWhiteSpace(core) || IsBareLanguageWord(core))
            return lang?.Name ?? kindFallback;
        if (lang is { } l && !core.Contains(l.Name, StringComparison.OrdinalIgnoreCase) && !MentionsLanguage(core, l.Code))
            return $"{l.Name} · {core}";
        return core;
    }

    public static string CleanLabel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('.', ' ').Replace('_', ' ');
        s = WhitespaceRunRegex().Replace(s, " ").Trim();
        return s.Trim('[', ']', '(', ')', '-', ' ');
    }

    // ─── Regexes ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"[^\p{L}]+")]
    private static partial Regex NonLetterRunRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}#]+")]
    private static partial Regex NonWordRunRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRunRegex();

    [GeneratedRegex(@"[\[(][0-9A-Fa-f]{8}[\])]")]
    private static partial Regex CrcRegex();

    [GeneratedRegex(@"(?<![\p{L}])delay[\s_=:]*(?<ms>[+-]?\d{1,6})\s*ms(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelayRegex();

    [GeneratedRegex(@"\p{L}+$")]
    private static partial Regex TrailingWordRegex();

    [GeneratedRegex(@"^(?:aac|ac3|eac3|dd|ddp|dts|opus|flac|mp3|truehd|atmos|pcm|lpcm|ch|audio|vorbis)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AudioWordRegex();

    [GeneratedRegex(@"^(?:(?:season|series|сезон|s)[\s._-]*\d{1,3}|\d{1,3}[\s._-]*(?:season|сезон)|specials?|спешлы)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonSegmentRegex();
}
