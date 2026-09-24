namespace Tack.Core.Resolution;

/// <summary>One place a tool was found on PATH: the executable and the directory holding it (the binDir).</summary>
public sealed record PathMatch(string BinDir, string ExePath);

/// <summary>
/// Finds every place a named tool lives on PATH - the in-process equivalent of Windows <c>where</c>, used by
/// <c>tack tools add</c> to offer a pick-list instead of making the user hunt down the binDir by hand. Walks
/// the effective PATH in resolution order (machine entries, then user entries), probes the exec extensions via
/// <see cref="BinaryLocator"/>, dedupes by directory, and skips excluded dirs - tack's own shims dir above all,
/// so it never offers the shim as if it were the real tool. PATH access and file existence are injected so it's
/// unit-testable without touching the real environment.
///
/// <para>PATH entries can hold unexpanded environment references (<c>%NVM_HOME%</c>, <c>%SystemRoot%\system32</c>):
/// they're stored in the registry as <c>REG_EXPAND_SZ</c>. In practice .NET's
/// <c>Environment.GetEnvironmentVariable(.., Machine/User)</c> already expands them, but we don't lean on that -
/// each entry is passed through <paramref name="expand"/> (default <see cref="Environment.ExpandEnvironmentVariables"/>)
/// before it's probed, so a raw value would still resolve. An undefined variable is left as its literal
/// <c>%FOO%</c>, which simply won't match any file and is skipped.</para>
/// </summary>
public static class PathScan
{
    public static List<PathMatch> FindOnPath(
        string name,
        Func<EnvironmentVariableTarget, string?> getPath,
        IEnumerable<string> excludeDirs,
        Func<string, bool>? fileExists = null,
        Func<string, string>? expand = null)
    {
        fileExists ??= File.Exists;
        expand ??= Environment.ExpandEnvironmentVariables;

        var excluded = new HashSet<string>(excludeDirs.Select(Norm), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PathMatch>();

        var dirs = Split(getPath(EnvironmentVariableTarget.Machine))
            .Concat(Split(getPath(EnvironmentVariableTarget.User)));

        foreach (var raw in dirs)
        {
            string dir = expand(raw);       // %NVM_HOME% -> C:\Users\..\nvm, etc.
            string key = Norm(dir);
            if (excluded.Contains(key)) continue;
            if (!seen.Add(key)) continue; // the same dir can appear on PATH more than once

            if (BinaryLocator.Locate(dir, name, fileExists) is { } exe)
                results.Add(new PathMatch(dir, exe));
        }
        return results;
    }

    private static List<string> Split(string? p) => string.IsNullOrEmpty(p)
        ? new List<string>()
        : p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
