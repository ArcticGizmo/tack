namespace Tack.Core.Platform;

/// <summary>A PATH edit that was applied: which scope, and the raw value before and after (a fallback the CLI
/// prints and backs up so a bad edit can be reverted by hand).</summary>
public sealed record PathChange(string Scope, string Before, string After);

/// <summary>
/// Pure PATH-string edits for <c>tack doctor --fix</c>, working on <b>raw</b> entries so environment tokens are
/// preserved: <c>%SystemRoot%\system32</c> stays a token, only the shims dir is moved to the front. Comparisons
/// expand entries (via the injected <paramref name="expand"/>) so a token and its expansion count as the same
/// directory, but the strings returned keep every untouched entry exactly as it was stored. No registry or
/// filesystem access, so it's trivially testable; <see cref="WindowsPathInstaller"/> owns the raw read/write.
/// </summary>
public static class PathEdits
{
    /// <summary>The raw PATH with <paramref name="shimsDir"/> moved to the very front (deduped), or null if it
    /// already leads and nothing needs writing.</summary>
    public static string? PrependFront(string? rawPath, string shimsDir, Func<string, string>? expand = null) =>
        PromoteFront(rawPath, shimsDir, behind: null, expand);

    /// <summary>
    /// The raw PATH with <paramref name="shimsDir"/> moved to the front (deduped) - but placed directly AFTER the
    /// first entry matching any of <paramref name="behind"/>, if one is on the PATH. That's how a dev instance goes
    /// on PATH: ahead of every real tool install, yet still behind the release tack's shims so it never outranks
    /// them. With nothing to sit behind it's simply the front. Null when the order is already right.
    /// </summary>
    public static string? PromoteFront(string? rawPath, string shimsDir, IEnumerable<string>? behind,
        Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        string shims = Key(shimsDir, expand);
        var anchors = new HashSet<string>((behind ?? Array.Empty<string>()).Select(b => Key(b, expand)));
        anchors.Remove(shims); // never sit behind ourselves

        var entries = Split(rawPath);
        var fixedUp = entries.Where(p => Key(p, expand) != shims).ToList();
        int anchor = fixedUp.FindIndex(p => anchors.Contains(Key(p, expand)));
        fixedUp.Insert(anchor + 1, Trim(shimsDir)); // -1 (no anchor) -> the very front

        return SameByKey(entries, fixedUp, expand) ? null : string.Join(';', fixedUp);
    }

    /// <summary>
    /// The raw PATH with tack wired in: <paramref name="shimsDir"/> at the very front (it must win over every
    /// other tool install) and <paramref name="installDir"/> appended if it's missing (it only needs to resolve).
    /// Null when both are already in place and nothing needs writing.
    /// </summary>
    public static string? Register(string? rawPath, string shimsDir, string installDir, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        string current = PromoteFront(rawPath, shimsDir, behind: null, expand) ?? string.Join(';', Split(rawPath));
        var entries = Split(current);
        string install = Key(installDir, expand);
        if (!entries.Any(p => Key(p, expand) == install))
            entries.Add(Trim(installDir));

        return SameByKey(Split(rawPath), entries, expand) ? null : string.Join(';', entries);
    }

    /// <summary>The raw PATH with every entry matching one of <paramref name="dirs"/> removed, or null if none
    /// were on it.</summary>
    public static string? Remove(string? rawPath, IEnumerable<string> dirs, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var drop = new HashSet<string>(dirs.Select(d => Key(d, expand)));
        var entries = Split(rawPath);
        var kept = entries.Where(p => !drop.Contains(Key(p, expand))).ToList();
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
