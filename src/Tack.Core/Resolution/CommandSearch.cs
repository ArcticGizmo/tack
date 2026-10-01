namespace Tack.Core.Resolution;

/// <summary>
/// The command search a new process on this machine would do: its PATH entries (<see cref="CommandLookup"/>), its
/// PATHEXT, and the Windows folders <c>CreateProcess</c> searches before PATH. Built once per command from the real
/// environment (<see cref="Current"/>), or by hand in tests.
/// </summary>
public sealed class CommandSearch
{
    public required IReadOnlyList<PathEntry> Entries { get; init; }
    public required IReadOnlyList<string> Extensions { get; init; }
    public required string WindowsDir { get; init; }
    public required string SystemDir { get; init; }
    public Func<string, bool> FileExists { get; init; } = File.Exists;

    public static CommandSearch Current() => new()
    {
        Entries = CommandLookup.Entries(
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)),
        Extensions = CommandLookup.Extensions(Environment.GetEnvironmentVariable("PATHEXT")),
        WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        SystemDir = Environment.SystemDirectory,
    };

    /// <summary>What runs instead of tack's shim for a bare <paramref name="name"/>; null when the shim wins.</summary>
    public CommandHit? Shadowing(string name, string shimsDir) =>
        CommandLookup.Shadowing(name, Entries, shimsDir, Extensions, FileExists);

    /// <summary>
    /// The Windows file that provides <paramref name="name"/>, if Windows itself does: one in System32 or the
    /// Windows folder (which <c>CreateProcess</c> searches before PATH, on PATH or not), or in a system PATH folder
    /// under the Windows folder (<c>System32\OpenSSH</c>, <c>System32\Wbem</c>). tack can't take those over: they
    /// can't be moved off the system PATH, and programs that start them directly never look at PATH at all.
    /// </summary>
    public string? WindowsOwner(string name)
    {
        foreach (var dir in new[] { SystemDir, WindowsDir })
            if (CommandLookup.InDir(dir, name, Extensions, FileExists) is { } exe)
                return exe;
        var windowsEntries = Entries.Where(e => e.Scope == PathScope.System && PathKey.IsUnder(e.Dir, WindowsDir));
        return CommandLookup.Find(name, windowsEntries, Extensions, FileExists)?.Exe;
    }

    /// <summary>
    /// Why a bare <paramref name="name"/> won't reach tack's shim, and what to do about it; null when it will. A
    /// shims dir in <paramref name="tackShimsDirs"/> ahead of ours isn't a problem to fix (it's the release tack in
    /// front of a dev one), so it's left to the caller.
    /// </summary>
    public Shadow? Explain(string name, string shimsDir, IEnumerable<string>? tackShimsDirs = null)
    {
        if (Shadowing(name, shimsDir) is not { } hit) return null;
        if (tackShimsDirs is not null && tackShimsDirs.Any(d => PathKey.Of(d) == PathKey.Of(hit.Entry.Dir)))
            return new Shadow(name, hit, ShadowKind.OtherTack,
                $"{hit.Entry.Dir} ({hit.Entry.Where}) is another tack instance's shims");

        if (WindowsOwner(name) is { } windows)
            return new Shadow(name, hit, ShadowKind.Windows,
                $"{windows} is part of Windows, so tack can't intercept it; take '{name}' out of the tool's commands");

        return hit.Entry.Scope == PathScope.System
            ? new Shadow(name, hit, ShadowKind.SystemPath,
                $"{hit.Exe} comes first ({hit.Entry.Where}). To let tack manage {name}, uninstall that copy or take " +
                "its folder off the system PATH (needs admin), then register the versions you want with tack")
            : new Shadow(name, hit, ShadowKind.UserPath,
                $"{hit.Exe} comes first ({hit.Entry.Where}); 'tack doctor --fix' moves tack's shims back in front");
    }
}

public enum ShadowKind
{
    /// <summary>A folder on the system PATH provides the name. Only moving that tool fixes it.</summary>
    SystemPath,
    /// <summary>A folder earlier on the user PATH provides it. <c>doctor --fix</c> fixes it.</summary>
    UserPath,
    /// <summary>Windows provides it. Nothing fixes it.</summary>
    Windows,
    /// <summary>Another tack instance's shims answer first (release ahead of dev).</summary>
    OtherTack,
}

/// <summary>A command name that won't reach tack's shim: what wins instead, and what to do about it.</summary>
public sealed record Shadow(string Name, CommandHit Winner, ShadowKind Kind, string Advice);
