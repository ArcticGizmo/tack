using Tack.Core.Config;
using Tack.Core.Resolution;

namespace Tack.Core.Maintenance;

public enum CheckStatus { Ok, Warn, Fail }

public sealed record DoctorCheck(string Title, CheckStatus Status, string Detail);

public sealed class DoctorReport
{
    public List<DoctorCheck> Checks { get; } = new();
    public bool HasProblems => Checks.Exists(c => c.Status != CheckStatus.Ok);
    public void Add(string title, CheckStatus status, string detail) => Checks.Add(new DoctorCheck(title, status, detail));
}

/// <summary>
/// Diagnoses the health of tack's PATH wiring (scope plan sections 5.4 + 10, ADR 0002): is the shims dir on the
/// user PATH, is any tack folder on the system PATH (where every account would search a folder you can write), which
/// commands won't reach the shims because something ahead of them on PATH provides them, are there stale shims, and
/// do any registered versions point at a missing binDir. The command search is injected for testability.
/// </summary>
public static class PathDoctor
{
    /// <summary>How to take an entry off the system PATH. Spelled out rather than done: tack never writes the system
    /// PATH, and the Environment Variables dialog keeps the value's %VAR% tokens (setx would flatten them).</summary>
    public const string RemoveFromSystemPath =
        "remove it under System variables > Path in the Environment Variables dialog (run SystemPropertiesAdvanced.exe " +
        "from an admin shell, then Environment Variables)";

    public static DoctorReport Run(
        CentralConfig config,
        string shimsDir,
        CommandSearch search,
        Func<string, bool>? dirExists = null,
        IEnumerable<string>? tackShimsDirs = null,
        IEnumerable<string>? everWired = null)
    {
        // Shims dirs of any tack instance (both profiles). One of those ahead of us isn't a rogue install - it's
        // the release tack in front of a dev one - so it's reported as a hand-over hint, not per-tool shadowing.
        var tackDirs = (tackShimsDirs ?? TackPaths.User.AllShimsDirs).ToList();
        dirExists ??= Directory.Exists;

        var report = new DoctorReport();
        string shims = PathKey.Of(shimsDir);
        var names = ShimName.Exposed(config);

        // `tack disable` is a setting: the shims stay on PATH, so every other check still applies.
        if (config.Settings.Disabled)
            report.Add("tack is disabled", CheckStatus.Warn,
                "your tool calls pass straight through; run 'tack enable' to turn it back on");

        report.Add("Shims directory exists",
            dirExists(shimsDir) ? CheckStatus.Ok : CheckStatus.Warn, shimsDir);

        var onUserPath = search.Entries.FirstOrDefault(e => e.Scope == PathScope.User && PathKey.Of(e.Dir) == shims);
        report.Add("Shims directory is on your user PATH",
            onUserPath is not null ? CheckStatus.Ok : CheckStatus.Fail,
            onUserPath is not null ? $"entry {onUserPath.Position}" : "not on it - tack won't intercept tool calls; run 'tack setup'");

        // Every account searches the system PATH, and these folders are yours to write: the critical 0.1.x finding.
        var wired = new HashSet<string>((everWired ?? TackPaths.EverWired).Append(shimsDir).Select(PathKey.Of));
        foreach (var entry in search.Entries.Where(e => e.Scope == PathScope.System && wired.Contains(PathKey.Of(e.Dir))))
            report.Add("tack folder on the system PATH", CheckStatus.Fail,
                $"{entry.Raw} ({entry.Where}): every account on this machine searches the system PATH, and tack's " +
                $"folders are writable by you, so it doesn't belong there; {RemoveFromSystemPath}");

        // Commands that won't reach the shims: something ahead of them on PATH provides the same name.
        if (onUserPath is not null)
        {
            var byOtherTack = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (search.Explain(name, shimsDir, tackDirs) is not { } shadow) continue;
                if (shadow.Kind == ShadowKind.OtherTack)
                {
                    string dir = shadow.Winner.Entry.Dir;
                    if (!byOtherTack.TryGetValue(dir, out var list))
                        byOtherTack[dir] = list = new List<string>();
                    list.Add(name);
                }
                else
                {
                    report.Add($"'{name}' isn't intercepted", CheckStatus.Warn, shadow.Advice);
                }
            }

            // That instance's shims are still there when it's disabled (it only passes calls through), so this
            // can't tell whether the hand-over has already happened.
            foreach (var (dir, shared) in byOtherTack)
                report.Add("Behind another tack instance", CheckStatus.Warn,
                    $"{dir} answers first for {string.Join(", ", shared.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}; " +
                    "run 'tack disable' on that instance to hand them over (fine to ignore if it's already disabled)");
        }

        // Stale shims: harmless (they pass straight through), and normally pruned by the next config change.
        if (dirExists(shimsDir))
            foreach (var name in ShimStamper.Stale(names, shimsDir))
                report.Add($"Stale shim '{name}'", CheckStatus.Warn,
                    "not in your config; it only passes calls through - tack doctor --fix removes it");

        foreach (var bad in ShimName.Invalid(config))
            report.Add("Invalid command name", CheckStatus.Fail, $"{bad}; it's never intercepted - fix it in config.json");

        // Missing binDirs.
        foreach (var (toolName, tool) in config.Tools)
            foreach (var (version, iv) in tool.Versions)
                if (!dirExists(iv.BinDir))
                    report.Add($"Missing binDir for {toolName}@{version}", CheckStatus.Fail, iv.BinDir);

        // Pre-zones bindings with a mid-path wildcard: no single-directory equivalent, so they no longer apply.
        foreach (var glob in ZoneRegistry.Unmigrated(config))
            report.Add($"Binding '{glob}' no longer applies", CheckStatus.Fail,
                "zones take a plain directory (everything under it is included); re-add it with " +
                "'tack zone add <dir> tool@version' and delete it from the bindings list in config.json");

        if (names.Count == 0)
            report.Add("No tools registered", CheckStatus.Warn, "use 'tack tool add' to add an install");

        return report;
    }
}
