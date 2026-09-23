using System.Text;
using System.Text.RegularExpressions;

namespace Tack.Core.Resolution;

/// <summary>
/// Directory-glob matching for central bindings. Paths and globs are normalized (forward slashes, lower
/// case, no trailing slash - tack is Windows-first and case-insensitive). Supported wildcards:
///   *   matches within a single path segment (no '/')
///   **  matches across segments (any depth), and a trailing "/**" also matches the base directory itself
/// so "C:/work/**" matches both "C:/work" and "C:/work/a/b".
/// </summary>
public static class Glob
{
    public static bool IsMatch(string glob, string dir)
    {
        string g = Normalize(glob);
        string d = Normalize(dir);
        return ToRegex(g).IsMatch(d);
    }

    /// <summary>Higher = more specific. Literal-prefix length before the first wildcard; a wildcard-free
    /// glob gets a large bonus so an exact directory always beats a pattern.</summary>
    public static int Specificity(string glob)
    {
        string g = Normalize(glob);
        int star = g.IndexOf('*');
        return star < 0 ? g.Length + 100_000 : star;
    }

    private static string Normalize(string p) =>
        p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private static Regex ToRegex(string g)
    {
        // Trailing "/**" is the common "this dir and everything under it" case.
        if (g.EndsWith("/**", StringComparison.Ordinal))
            return new Regex("^" + EscapeGlob(g[..^3]) + "(/.*)?$");
        return new Regex("^" + EscapeGlob(g) + "$");
    }

    private static string EscapeGlob(string g)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < g.Length; i++)
        {
            char c = g[i];
            if (c == '*')
            {
                if (i + 1 < g.Length && g[i + 1] == '*') { sb.Append(".*"); i++; }
                else sb.Append("[^/]*");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return sb.ToString();
    }
}
