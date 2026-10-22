using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Animarr.Web.Services;

/// <summary>
/// Shifts every cue of a converted subtitle (/api/subtitle's WebVTT or ASS)
/// by an offset — the player's subtitle delay applied server-side
/// (<c>offsetMs=</c>, + = later), so a client needs no time-shifted copy of
/// its own. Times never go below zero; a cue that would end before zero is
/// dropped. Everything but the cue times passes through untouched.
/// </summary>
public static partial class SubtitleShift
{
    /// <summary>Largest accepted shift either way (10 minutes).</summary>
    public const int MaxOffsetMs = 600_000;

    // WebVTT cue timing: "[HH:]MM:SS.mmm --> [HH:]MM:SS.mmm[ settings]".
    [GeneratedRegex(@"^(?<a>(?:\d+:)?\d{2}:\d{2}\.\d{3})\s+-->\s+(?<b>(?:\d+:)?\d{2}:\d{2}\.\d{3})(?<rest>.*)$")]
    private static partial Regex VttTiming();

    // ASS event: "Dialogue: Layer,Start,End,…" (and Comment:), times H:MM:SS.cc.
    [GeneratedRegex(@"^(?<head>(?:Dialogue|Comment):\s*[^,]*,)(?<a>\d+:\d{2}:\d{2}\.\d{2}),(?<b>\d+:\d{2}:\d{2}\.\d{2}),(?<rest>.*)$")]
    private static partial Regex AssEvent();

    /// <summary>WebVTT text with every cue moved by <paramref name="offsetMs"/>.</summary>
    public static string WebVtt(string text, int offsetMs)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(text.Length + 64);
        for (int i = 0; i < lines.Length; i++)
        {
            var m = VttTiming().Match(lines[i]);
            if (!m.Success)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
                continue;
            }
            long a = VttMs(m.Groups["a"].Value) + offsetMs, b = VttMs(m.Groups["b"].Value) + offsetMs;
            if (b <= 0)
            {
                // Drop the cue: its timing line, its payload, the blank line
                // after it — and its identifier line, already written.
                TrimCueId(sb);
                while (i + 1 < lines.Length && lines[i + 1].Length > 0) i++;
                if (i + 1 < lines.Length) i++;
                continue;
            }
            sb.Append(VttTime(Math.Max(0, a))).Append(" --> ").Append(VttTime(b)).Append(m.Groups["rest"].Value);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>ASS script with every event moved by <paramref name="offsetMs"/>.</summary>
    public static string Ass(string text, int offsetMs)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(text.Length + 64);
        for (int i = 0; i < lines.Length; i++)
        {
            var m = AssEvent().Match(lines[i]);
            if (m.Success)
            {
                long a = AssMs(m.Groups["a"].Value) + offsetMs, b = AssMs(m.Groups["b"].Value) + offsetMs;
                if (b <= 0) continue;
                sb.Append(m.Groups["head"].Value).Append(AssTime(Math.Max(0, a))).Append(',')
                  .Append(AssTime(b)).Append(',').Append(m.Groups["rest"].Value);
            }
            else sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append(nl);
        }
        return sb.ToString();
    }

    /// <summary>Removes a just-written cue identifier line (the line before a
    /// dropped cue's timing, when it isn't blank).</summary>
    private static void TrimCueId(StringBuilder sb)
    {
        if (sb.Length == 0 || sb[^1] != '\n') return;
        int end = sb.Length - 1, start = end;
        while (start > 0 && sb[start - 1] != '\n') start--;
        if (end > start) sb.Length = start;   // a non-empty line: the identifier
    }

    private static long VttMs(string t)
    {
        var parts = t.Split(':');
        long h = parts.Length == 3 ? long.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        long m = long.Parse(parts[^2], CultureInfo.InvariantCulture);
        var sec = parts[^1].Split('.');
        return ((h * 60 + m) * 60 + long.Parse(sec[0], CultureInfo.InvariantCulture)) * 1000
               + long.Parse(sec[1], CultureInfo.InvariantCulture);
    }

    private static string VttTime(long ms)
        => $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}";

    private static long AssMs(string t)
    {
        var parts = t.Split(':');
        var sec = parts[2].Split('.');
        return ((long.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + long.Parse(parts[1], CultureInfo.InvariantCulture)) * 60
                + long.Parse(sec[0], CultureInfo.InvariantCulture)) * 1000 + long.Parse(sec[1], CultureInfo.InvariantCulture) * 10;
    }

    // Centiseconds, rounded: ASS has no finer resolution.
    private static string AssTime(long ms)
    {
        var cs = (ms + 5) / 10;
        return $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }
}
