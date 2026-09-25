namespace Tack.Core.Resolution;

/// <summary>
/// Directory matching for central zones. A zone is a plain directory that covers itself and everything under
/// it, so the zones that apply to a directory are always among its ancestors - and ancestors nest, so the
/// deepest one is an unambiguous winner. Matching is a walk up the tree (like tack.yml discovery), comparing
/// normalized paths: forward slashes, lower case, no trailing slash (tack is Windows-first and
/// case-insensitive). Matching is by whole segment, so "C:/work" never matches "C:/workshop".
/// </summary>
public static class ZonePath
{
    public static string Normalize(string p) =>
        p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>The normalized directory and each of its ancestors, deepest first.</summary>
    public static IEnumerable<string> Ancestors(string dir)
    {
        string d = Normalize(dir);
        while (d.Length > 0)
        {
            yield return d;
            int slash = d.LastIndexOf('/');
            if (slash < 0) yield break;
            d = d[..slash].TrimEnd('/');
        }
    }

    /// <summary>Null when <paramref name="path"/> is usable as a zone, otherwise why not.</summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "a directory is required";
        if (path.Contains('*') || path.Contains('?'))
            return "zones take a plain directory, not a glob - everything under the directory is already included";
        if (!Path.IsPathRooted(path)) return "the directory must be an absolute path";
        return null;
    }

    /// <summary>The zone directory a pre-zones binding glob maps to: "X/**" and a wildcard-free "X" both become
    /// "X". Null for a glob with a wildcard anywhere else, which has no single-directory equivalent.</summary>
    public static string? FromLegacyGlob(string glob)
    {
        string g = glob.Trim();
        if (g.EndsWith("/**", StringComparison.Ordinal) || g.EndsWith("\\**", StringComparison.Ordinal))
            g = g[..^3];
        g = g.TrimEnd('/', '\\');
        return g.Length == 0 || g.Contains('*') || g.Contains('?') ? null : g;
    }
}
