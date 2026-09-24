namespace Tack.Core.Resolution;

/// <summary>
/// Which PATH entries a shim may fall through to when it doesn't resolve a tool itself. The shim continues the
/// search from just AFTER its own PATH entry - exactly what Windows would have picked had the shim not been there.
///
/// <para>Scanning from the top instead (skipping only itself) loops as soon as two shims dirs are on PATH, which is
/// the normal setup when a dev instance sits behind the release one: the dev shim would fall through to the
/// release shim ahead of it, which falls through to the dev shim, forever. Scanning onward means the release shim
/// can hand over to the dev shim, and the dev shim hands over to the real tool - never backwards.</para>
///
/// <para>If the shim's dir isn't on PATH at all (invoked by full path), every entry except its own dir is a
/// candidate. Pure and allocation-light: this runs on the shim's hot path.</para>
/// </summary>
public static class PassthroughScan
{
    public static IEnumerable<string> Candidates(string? pathVar, string ownDir)
    {
        string own = Norm(ownDir);
        var entries = (pathVar ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int start = Array.FindIndex(entries, e => Norm(e) == own);
        for (int i = start < 0 ? 0 : start + 1; i < entries.Length; i++)
            if (Norm(entries[i]) != own) // a duplicate later entry for ourselves is still ourselves
                yield return entries[i];
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
