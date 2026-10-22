using System.Text.RegularExpressions;

namespace Animarr.Web.Services;

/// <summary>
/// Reads an ASS / SSA script and says whether it needs a real renderer
/// (libass — JASSUB on the web, <c>ass-media</c> on Android) or survives as
/// plain cues: positioned, moving, animated, karaoke, clipped, rotated or
/// drawn lines, or more than a handful of styles (typesetting), all get lost
/// in a WebVTT conversion. Pure text work, no I/O.
/// </summary>
public static partial class AssAnalyzer
{
    /// <summary>More styles than this reads as typesetting rather than one
    /// dialogue style plus an alternate.</summary>
    public const int ManyStyles = 4;

    /// <param name="Styles">Style lines in [V4+ Styles] / [V4 Styles].</param>
    /// <param name="Events">Dialogue lines.</param>
    /// <param name="Complex">True when a plain-text rendering loses something
    /// that matters (see <paramref name="Reasons"/>).</param>
    /// <param name="Reasons">What was found: "pos", "move", "org", "animation"
    /// (\t), "karaoke", "clip", "fade", "rotation", "shear", "drawing",
    /// "many-styles".</param>
    /// <param name="Fonts">Font names the script asks for (styles + \fn), as
    /// written, distinct case-insensitively.</param>
    public sealed record Info(int Styles, int Events, bool Complex, IReadOnlyList<string> Reasons,
        IReadOnlyList<string> Fonts);

    public static Info Analyze(string script)
    {
        int styles = 0, events = 0;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var fonts = new List<string>();
        var seenFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddFont(string? f)
        {
            f = f?.Trim().TrimStart('@');   // '@' = vertical variant of the same font
            if (!string.IsNullOrEmpty(f) && seenFonts.Add(f)) fonts.Add(f);
        }

        string section = "";
        int fontField = 1;   // "Format: Name, Fontname, …" — Fontname is the 2nd field by spec
        foreach (var rawLine in script.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty || line[0] == ';') continue;
            if (line[0] == '[')
            {
                section = line.ToString().ToLowerInvariant();
                continue;
            }
            bool inStyles = section is "[v4+ styles]" or "[v4 styles]" or "[v4 styles+]";
            if (inStyles && line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = line[7..].ToString().Split(',', StringSplitOptions.TrimEntries);
                var i = Array.FindIndex(names, n => n.Equals("Fontname", StringComparison.OrdinalIgnoreCase));
                if (i >= 0) fontField = i;
            }
            else if (inStyles && line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            {
                styles++;
                var fields = line[6..].ToString().Split(',');
                if (fontField < fields.Length) AddFont(fields[fontField]);
            }
            else if (section == "[events]" && line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                events++;
                var text = EventText(line[9..]);
                if (text.IndexOf('{') < 0) continue;
                foreach (Match block in OverrideBlockRegex().Matches(text))
                {
                    var tags = block.Value;
                    if (PosRegex().IsMatch(tags))       reasons.Add("pos");
                    if (MoveRegex().IsMatch(tags))      reasons.Add("move");
                    if (OrgRegex().IsMatch(tags))       reasons.Add("org");
                    if (AnimRegex().IsMatch(tags))      reasons.Add("animation");
                    if (KaraokeRegex().IsMatch(tags))   reasons.Add("karaoke");
                    if (ClipRegex().IsMatch(tags))      reasons.Add("clip");
                    if (FadeRegex().IsMatch(tags))      reasons.Add("fade");
                    if (RotationRegex().IsMatch(tags))  reasons.Add("rotation");
                    if (ShearRegex().IsMatch(tags))     reasons.Add("shear");
                    if (DrawingRegex().IsMatch(tags))   reasons.Add("drawing");
                    foreach (Match fn in FontOverrideRegex().Matches(tags)) AddFont(fn.Groups[1].Value);
                }
            }
        }
        if (styles > ManyStyles) reasons.Add("many-styles");
        return new Info(styles, events, reasons.Count > 0, reasons.ToList(), fonts);
    }

    /// <summary>The Text field of a Dialogue line — everything after the 9th
    /// comma of "Layer, Start, End, Style, Name, MarginL, MarginR, MarginV,
    /// Effect, Text" (the text itself may contain commas).</summary>
    private static string EventText(ReadOnlySpan<char> fields)
    {
        int commas = 0;
        for (int i = 0; i < fields.Length; i++)
            if (fields[i] == ',' && ++commas == 9) return fields[(i + 1)..].ToString();
        return "";
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverrideBlockRegex();
    [GeneratedRegex(@"\\pos\s*\(")]
    private static partial Regex PosRegex();
    [GeneratedRegex(@"\\move\s*\(")]
    private static partial Regex MoveRegex();
    [GeneratedRegex(@"\\org\s*\(")]
    private static partial Regex OrgRegex();
    [GeneratedRegex(@"\\t\s*\(")]
    private static partial Regex AnimRegex();
    [GeneratedRegex(@"\\(?:kf|ko|k|K)\s*\d")]
    private static partial Regex KaraokeRegex();
    [GeneratedRegex(@"\\i?clip\s*\(")]
    private static partial Regex ClipRegex();
    [GeneratedRegex(@"\\fade?\s*\(")]
    private static partial Regex FadeRegex();
    [GeneratedRegex(@"\\fr[xyz]?\s*-?\d")]
    private static partial Regex RotationRegex();
    [GeneratedRegex(@"\\fa[xy]\s*-?\d")]
    private static partial Regex ShearRegex();
    [GeneratedRegex(@"\\p\s*[1-9]")]
    private static partial Regex DrawingRegex();
    [GeneratedRegex(@"\\fn([^\\}]+)")]
    private static partial Regex FontOverrideRegex();
}
