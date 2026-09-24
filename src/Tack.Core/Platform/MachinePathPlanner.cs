namespace Tack.Core.Platform;

/// <summary>The computed change to the two PATH scopes. Null strings mean "leave that scope untouched".</summary>
public sealed record PathPlan(string? NewMachinePath, string? NewUserPath)
{
    public bool MachineChanged => NewMachinePath is not null;
    public bool UserChanged => NewUserPath is not null;
    public bool AnyChange => MachineChanged || UserChanged;
}

/// <summary>
/// Pure PATH arithmetic for <c>tack doctor --fix</c>: put the shims dir at the very front of the MACHINE PATH
/// so it beats everything (on Windows, machine entries resolve before user entries, so a shims dir on the user
/// PATH still sits behind every system install - that's the shadowing users hit). A copy on the user PATH is
/// then redundant and only muddies <c>doctor</c>, so it's stripped. No filesystem or registry access here so
/// it's trivially testable; <see cref="WindowsPathInstaller"/> owns the admin-gated write.
/// </summary>
public static class MachinePathPlanner
{
    public static PathPlan PlaceFront(string? machinePath, string? userPath, string shimsDir)
    {
        string shims = Trim(shimsDir);

        var machine = Split(machinePath);
        var machineFixed = new List<string> { shims };
        machineFixed.AddRange(machine.Where(p => !PathEquals(p, shims)));
        // Machine changed unless it already led with exactly the shims dir (and only once).
        bool machineChanged = !SequenceEqualPaths(machine, machineFixed);

        var user = Split(userPath);
        var userFixed = user.Where(p => !PathEquals(p, shims)).ToList();
        bool userChanged = userFixed.Count != user.Count;

        return new PathPlan(
            machineChanged ? string.Join(';', machineFixed) : null,
            userChanged ? string.Join(';', userFixed) : null);
    }

    private static List<string> Split(string? p) => string.IsNullOrEmpty(p)
        ? new List<string>()
        : p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Trim(string p) => p.TrimEnd('\\', '/');

    private static bool PathEquals(string a, string b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static bool SequenceEqualPaths(List<string> a, List<string> b) =>
        a.Count == b.Count && !a.Where((t, i) => !PathEquals(t, b[i])).Any();
}
