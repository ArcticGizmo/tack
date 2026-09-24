namespace Tack.Core.Platform;

/// <summary>A PATH edit that was applied: which scope, and the raw value before and after (a fallback the CLI
/// prints and backs up so a bad edit can be reverted by hand).</summary>
public sealed record PathChange(string Scope, string Before, string After);

/// <summary>
/// Pure PATH-string edits for <c>tack doctor --fix</c>, working on <b>raw</b> entries so environment tokens are
/// preserved: <c>%SystemRoot%\system32</c> stays a token, only the shims dir is added or removed. Comparisons
/// expand entries (via the injected <paramref name="expand"/>) so a token and its expansion count as the same
/// directory, but the strings returned keep every untouched entry exactly as it was stored. No registry or
/// filesystem access, so it's trivially testable; <see cref="WindowsPathInstaller"/> owns the raw read/write.
/// </summary>
public static class PathEdits
{
    /// <summary>The raw PATH with <paramref name="shimsDir"/> moved to the very front (deduped), or null if it
    /// already leads and nothing needs writing.</summary>
    public static string? PrependFront(string? rawPath, string shimsDir, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        string shims = Key(shimsDir, expand);

        var entries = Split(rawPath);
        var fixedUp = new List<string> { Trim(shimsDir) };
        fixedUp.AddRange(entries.Where(p => Key(p, expand) != shims));

        return SameByKey(entries, fixedUp, expand) ? null : string.Join(';', fixedUp);
    }

    /// <summary>The raw PATH with every occurrence of <paramref name="shimsDir"/> removed, or null if it wasn't
    /// present.</summary>
    public static string? Remove(string? rawPath, string shimsDir, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        string shims = Key(shimsDir, expand);

        var entries = Split(rawPath);
        var kept = entries.Where(p => Key(p, expand) != shims).ToList();

        return kept.Count == entries.Count ? null : string.Join(';', kept);
    }

    private static bool SameByKey(List<string> a, List<string> b, Func<string, string> expand)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Key(a[i], expand) != Key(b[i], expand)) return false;
        return true;
    }

    private static List<string> Split(string? p) => string.IsNullOrEmpty(p)
        ? new List<string>()
        : p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Trim(string p) => p.TrimEnd('\\', '/');

    /// <summary>A comparison key: expanded, full-pathed, case-folded. Falls back gracefully for a token that
    /// can't be expanded or a malformed path.</summary>
    private static string Key(string p, Func<string, string> expand)
    {
        string e = expand(p);
        try { return Path.GetFullPath(e).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return Trim(e).ToLowerInvariant(); }
    }
}
