namespace Tack.Core.Resolution;

public enum PathScope { System, User }

/// <summary>One PATH entry as a new process sees it: the stored text, its expansion, which PATH it came from and
/// its 1-based position there (what <c>doctor</c> calls "system PATH entry 4").</summary>
public sealed record PathEntry(string Raw, string Dir, PathScope Scope, int Position)
{
    public string Where => $"{(Scope == PathScope.System ? "system" : "user")} PATH entry {Position}";
}

/// <summary>Where a bare command name would be run from.</summary>
public sealed record CommandHit(PathEntry Entry, string Exe);

/// <summary>
/// Which file a bare command name runs, the way cmd finds it: PATH folder by folder (the system PATH, then the user
/// PATH), trying every <c>PATHEXT</c> extension in a folder before moving to the next. So <c>node.cmd</c> in an
/// earlier folder beats <c>node.exe</c> in a later one. Used to tell whether a call reaches tack's shim at all (ADR
/// 0002: anything on the system PATH comes first, and tack reports that rather than fixing it). PATH values, PATHEXT
/// and file existence are injected, so it's pure.
/// </summary>
public static class CommandLookup
{
    /// <summary>cmd's default when PATHEXT isn't set.</summary>
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    /// <summary>The PATH a new process gets: system entries, then user entries, each expanded.</summary>
    public static List<PathEntry> Entries(string? systemPath, string? userPath, Func<string, string>? expand = null)
    {
        expand ??= Environment.ExpandEnvironmentVariables;
        var entries = new List<PathEntry>();
        void Add(string? value, PathScope scope)
        {
            int position = 0;
            foreach (var raw in Split(value))
                entries.Add(new PathEntry(raw, expand(raw), scope, ++position));
        }
        Add(systemPath, PathScope.System);
        Add(userPath, PathScope.User);
        return entries;
    }

    /// <summary>The extensions cmd tries, in order.</summary>
    public static IReadOnlyList<string> Extensions(string? pathExt) =>
        (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.StartsWith('.'))
            .ToList();

    /// <summary>The file <paramref name="dir"/> would run for <paramref name="name"/>, or null. The extension is
    /// lower-cased (PATHEXT is upper case): the file system doesn't mind, and it reads as the file is usually named.</summary>
    public static string? InDir(string dir, string name, IReadOnlyList<string> extensions, Func<string, bool> fileExists)
    {
        foreach (var ext in extensions)
        {
            string candidate;
            try { candidate = Path.Combine(dir, name + ext.ToLowerInvariant()); }
            catch (ArgumentException) { return null; } // an entry with characters a path can't hold
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The first entry that provides <paramref name="name"/>, or null if none does.</summary>
    public static CommandHit? Find(string name, IEnumerable<PathEntry> entries, IReadOnlyList<string> extensions,
        Func<string, bool> fileExists)
    {
        foreach (var entry in entries)
            if (InDir(entry.Dir, name, extensions, fileExists) is { } exe)
                return new CommandHit(entry, exe);
        return null;
    }

    /// <summary>
    /// What a bare <paramref name="name"/> runs instead of tack's shim: the first entry ahead of
    /// <paramref name="shimsDir"/> that provides it. Null when the shim wins, or when the shims dir isn't on PATH
    /// at all (a separate problem, which doctor reports on its own).
    /// </summary>
    public static CommandHit? Shadowing(string name, IReadOnlyList<PathEntry> entries, string shimsDir,
        IReadOnlyList<string> extensions, Func<string, bool> fileExists)
    {
        string shims = PathKey.Of(shimsDir);
        int at = entries.ToList().FindIndex(e => PathKey.Of(e.Dir) == shims);
        return at < 0 ? null : Find(name, entries.Take(at), extensions, fileExists);
    }

    private static IEnumerable<string> Split(string? value) => string.IsNullOrEmpty(value)
        ? Array.Empty<string>()
        : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A comparison key for a folder: full path, no trailing separator, case-folded. Falls back to the
/// trimmed text for something that isn't a valid path.</summary>
public static class PathKey
{
    public static string Of(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return path.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        string p = Of(path), f = Of(folder);
        return p == f || p.StartsWith(f + "\\", StringComparison.Ordinal);
    }
}
