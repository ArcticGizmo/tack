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
/// Diagnoses the health of tack's PATH wiring (scope plan sections 5.4 + 10): is the shims dir on PATH,
/// is it shadowed by another tool install ahead of it (nvm-windows, a standalone Node), are there stale
/// shims, and do any registered versions point at a missing binDir. PATH access is injected for testability.
/// </summary>
public static class PathDoctor
{
    public static DoctorReport Run(
        CentralConfig config,
        string shimsDir,
        Func<EnvironmentVariableTarget, string?> getPath,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? dirExists = null,
        IEnumerable<string>? tackShimsDirs = null)
    {
        // Shims dirs of any tack instance (both profiles). One of those ahead of us isn't a rogue install - it's
        // the release tack in front of a dev one - so it's reported as a hand-over hint, not per-tool shadowing.
        var tackDirs = new HashSet<string>((tackShimsDirs ?? TackPaths.User.AllShimsDirs).Select(Norm));
        fileExists ??= File.Exists;
        dirExists ??= Directory.Exists;

        var report = new DoctorReport();
        string shims = Norm(shimsDir);
        var names = ShimName.Exposed(config);

        // `tack disable` is a per-user setting: the shims stay on PATH, so every other check still applies.
        if (config.Settings.Disabled)
            report.Add("tack is disabled", CheckStatus.Warn,
                "your tool calls pass straight through; run 'tack enable' to turn it back on");

        report.Add("Shims directory exists",
            dirExists(shimsDir) ? CheckStatus.Ok : CheckStatus.Warn, shimsDir);

        // Effective resolution order on Windows: machine PATH entries, then user PATH entries.
        var effective = Split(getPath(EnvironmentVariableTarget.Machine))
            .Concat(Split(getPath(EnvironmentVariableTarget.User)))
            .ToList();
        int shimsIndex = effective.FindIndex(p => Norm(p) == shims);

        if (shimsIndex < 0)
            report.Add("Shims directory is on PATH", CheckStatus.Fail,
                "not on PATH - tack won't intercept tool calls; run 'tack setup'");
        else
        {
            var (status, where) = WherePlaced(getPath, shims);
            report.Add("Shims directory is on PATH", status, where);
        }

        // Shadowing: an earlier PATH dir that already provides a shimmed binary wins over the shim.
        if (shimsIndex >= 0)
        {
            var byOtherTack = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                for (int i = 0; i < shimsIndex; i++)
                {
                    if (BinaryLocator.Locate(effective[i], name, fileExists) is not null)
                    {
                        if (tackDirs.Contains(Norm(effective[i])))
                        {
                            if (!byOtherTack.TryGetValue(effective[i], out var list))
                                byOtherTack[effective[i]] = list = new List<string>();
                            list.Add(name);
                        }
                        else
                        {
                            report.Add($"'{name}' is shadowed", CheckStatus.Warn,
                                $"{effective[i]} precedes the shims dir on PATH");
                        }
                        break;
                    }
                }
            }

            // That instance's shims are still there when it's disabled (it only passes calls through), so this
            // can't tell whether the hand-over has already happened.
            foreach (var (dir, shared) in byOtherTack)
                report.Add("Behind another tack instance", CheckStatus.Warn,
                    $"{dir} answers first for {string.Join(", ", shared.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}; " +
                    "run 'tack disable' on that instance to hand them over (fine to ignore if it's already disabled)");
        }

        // Stale shims: harmless (they pass straight through), and possibly another account's.
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

    /// <summary>Say exactly which PATH holds the shims dir, and where: "user PATH, entry 1". On the system PATH it's
    /// a failure, wherever it also is: every account searches the system PATH, and this folder is yours to write,
    /// which is the critical 0.1.x finding (ADR 0002). tack won't write the system PATH even to take itself off.</summary>
    private static (CheckStatus, string) WherePlaced(Func<EnvironmentVariableTarget, string?> getPath, string shims)
    {
        int machine = Split(getPath(EnvironmentVariableTarget.Machine)).FindIndex(p => Norm(p) == shims);
        if (machine >= 0)
            return (CheckStatus.Fail,
                $"system PATH, entry {machine + 1} - every account on this machine searches it, and you can write this folder; " +
                "remove it from the system PATH (needs admin), then run 'tack setup'");

        int user = Split(getPath(EnvironmentVariableTarget.User)).FindIndex(p => Norm(p) == shims);
        return (CheckStatus.Ok, $"user PATH, entry {user + 1}");
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
