namespace Tack.Core.Resolution;

/// <summary>
/// Matches a requested version (from tack.yml / a zone / a default) against the registered versions.
/// Exact match wins; otherwise a dotted-prefix match (e.g. "3.12" matches "3.12.1" but not "3.121.0"),
/// choosing the highest by <see cref="VersionOrder"/>. Keeps tack.yml terse ("python: 3.12") without pinning a patch.
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
            if (best is null || VersionOrder.Compare(v, best) > 0) best = v;
        }
        return best;
    }
}
