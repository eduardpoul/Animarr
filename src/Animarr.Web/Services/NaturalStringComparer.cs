namespace Animarr.Web.Services;

/// <summary>
/// Compares strings so that embedded numbers sort numerically rather than lexicographically.
/// "2.mp4" comes before "10.mp4"; "Season 2" comes before "Season 10".
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Ordinal = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return NaturalCompare(x, y);
    }

    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            bool aDigit = char.IsAsciiDigit(a[i]);
            bool bDigit = char.IsAsciiDigit(b[j]);

            if (aDigit && bDigit)
            {
                // Skip leading zeros so "01" == "1" numerically
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                // Parse as long to handle big numbers without overflow
                var na = long.Parse(a.AsSpan(si, i - si));
                var nb = long.Parse(b.AsSpan(sj, j - sj));
                var cmp = na.CompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
