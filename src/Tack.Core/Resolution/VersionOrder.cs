namespace Tack.Core.Resolution;

/// <summary>
/// The one ordering of version names tack uses for "newest": prefix matching, the default a removal repoints to,
/// and the order versions are listed in. Dotted segments compare numerically, so <c>10.0</c> beats <c>9.0</c>. A
/// segment with a suffix after its number is a pre-release of that number (<c>3.15.0rc2</c> sorts below
/// <c>3.15.0</c>, and <c>a</c> &lt; <c>b</c> &lt; <c>rc</c> by their text). A segment that doesn't start with a digit
/// compares as text, ignoring case, so named versions like <c>work</c> and <c>personal</c> still have an order. A
/// missing segment sorts first: <c>3.12</c> &lt; <c>3.12.0</c>.
/// </summary>
public static class VersionOrder
{
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        string[] pa = a.Split('.'), pb = b.Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            if (i >= pa.Length) return -1;
            if (i >= pb.Length) return 1;
            int c = CompareSegment(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int CompareSegment(string a, string b)
    {
        var (na, sa) = Split(a);
        var (nb, sb) = Split(b);
        if (na is null || nb is null)
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        if (na != nb) return na.Value.CompareTo(nb.Value);
        // Same number: the release (no suffix) beats any pre-release of it.
        if (sa.Length == 0 && sb.Length == 0) return 0;
        if (sa.Length == 0) return 1;
        if (sb.Length == 0) return -1;
        return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    // "0rc2" -> (0, "rc2"); "12" -> (12, ""); "work" -> (null, "work"). A number too long for a long compares as text.
    private static (long? Number, string Suffix) Split(string segment)
    {
        int digits = 0;
        while (digits < segment.Length && char.IsAsciiDigit(segment[digits])) digits++;
        if (digits == 0 || !long.TryParse(segment.AsSpan(0, digits), out long number)) return (null, segment);
        return (number, segment[digits..]);
    }
}
