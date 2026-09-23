namespace Tack.Core.Resolution;

/// <summary>
/// Matches a requested version (from tack.yml / a binding / a default) against the registered versions.
/// Exact match wins; otherwise a dotted-prefix match (e.g. "3.12" matches "3.12.1" but not "3.121.0"),
/// choosing the highest. Keeps tack.yml terse ("python: 3.12") without pinning a patch.
/// </summary>
public static class VersionMatch
{
    public static string? Best(IEnumerable<string> installed, string requested)
    {
        var list = installed as IReadOnlyCollection<string> ?? installed.ToList();

        foreach (var v in list)
            if (string.Equals(v, requested, StringComparison.OrdinalIgnoreCase))
                return v;

        string prefix = requested + ".";
        string? best = null;
        foreach (var v in list)
        {
            if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || Compare(v, best) > 0) best = v;
        }
        return best;
    }

    // Compare dotted versions segment by segment: numeric where both segments parse, else ordinal.
    private static int Compare(string a, string b)
    {
        string[] pa = a.Split('.'), pb = b.Split('.');
        int n = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < n; i++)
        {
            string sa = i < pa.Length ? pa[i] : "";
            string sb = i < pb.Length ? pb[i] : "";
            if (int.TryParse(sa, out int ia) && int.TryParse(sb, out int ib))
            {
                if (ia != ib) return ia.CompareTo(ib);
            }
            else
            {
                int c = string.CompareOrdinal(sa, sb);
                if (c != 0) return c;
            }
        }
        return 0;
    }
}
