using System.Globalization;

namespace Emberlight.Core;

/// <summary>
/// Case-insensitive "natural" comparison: runs of digits compare by numeric value, so
/// "img2" &lt; "img10". Equivalent in spirit to NSString's localizedStandardCompare
/// (what Finder uses). Implemented in managed code so results don't depend on shlwapi.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            bool dx = char.IsAsciiDigit(x[i]), dy = char.IsAsciiDigit(y[j]);
            if (dx && dy)
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                int c = CompareDigitRuns(x.AsSpan(si, i - si), y.AsSpan(sj, j - sj));
                if (c != 0) return c;
            }
            else if (!dx && !dy)
            {
                int si = i, sj = j;
                while (i < x.Length && !char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && !char.IsAsciiDigit(y[j])) j++;
                int c = string.Compare(x.Substring(si, i - si), y.Substring(sj, j - sj),
                                       CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
                if (c != 0) return c;
            }
            else
            {
                // digits sort before letters
                return dx ? -1 : 1;
            }
        }
        if (i < x.Length) return 1;
        if (j < y.Length) return -1;
        // Equal ignoring case/leading zeros: keep the ordering total and deterministic.
        return string.CompareOrdinal(x, y);
    }

    private static int CompareDigitRuns(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
        int c = a.CompareTo(b, StringComparison.Ordinal);
        return c < 0 ? -1 : c > 0 ? 1 : 0;
    }
}
