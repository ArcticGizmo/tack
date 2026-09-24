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
        string? activeShimsDir = null,
        bool disabled = false,
        IEnumerable<string>? tackShimsDirs = null)
    {
        // Shims dirs of any tack instance (both profiles). One of those ahead of us isn't a rogue install - it's
        // the release tack in front of a dev one - so it's reported as a hand-over hint, not per-tool shadowing.
        var tackDirs = new HashSet<string>((tackShimsDirs ?? TackPaths.AllShimsDirs).Select(Norm));
        fileExists ??= File.Exists;
        dirExists ??= Directory.Exists;
        // Where the shims actually live now (the parked dir while disabled); PATH/shadow checks still use the
        // canonical shimsDir, since that's the entry wired onto PATH.
        activeShimsDir ??= shimsDir;

        var report = new DoctorReport();
        string shims = Norm(shimsDir);
        var names = ExposedNames(config);

        report.Add("Shims directory exists",
            dirExists(activeShimsDir) ? CheckStatus.Ok : CheckStatus.Warn, activeShimsDir);

        // Effective resolution order on Windows: machine PATH entries, then user PATH entries.
        var effective = Split(getPath(EnvironmentVariableTarget.Machine))
            .Concat(Split(getPath(EnvironmentVariableTarget.User)))
            .ToList();
        int shimsIndex = effective.FindIndex(p => Norm(p) == shims);

        if (disabled)
        {
            // Interception is intentionally off (`tack disable`): the PATH entry points at the canonical dir
            // that's renamed away. Report the state plainly instead of failing the on-PATH / shadow checks.
            report.Add("tack is disabled", CheckStatus.Warn,
                "interception off; run 'tack enable' to turn it back on");
        }
        else
        {
            report.Add("Shims directory is on PATH",
                shimsIndex >= 0 ? CheckStatus.Ok : CheckStatus.Fail,
                shimsIndex >= 0 ? WherePlaced(getPath, shims) : "not on PATH - tack won't intercept tool calls");

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

                foreach (var (dir, shared) in byOtherTack)
                    report.Add("Behind another tack instance", CheckStatus.Warn,
                        $"{dir} answers first for {string.Join(", ", shared.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}; " +
                        "run 'tack disable' on that instance to hand them over");
            }
        }

        // Stale shims.
        if (dirExists(activeShimsDir))
        {
            foreach (var exe in Directory.GetFiles(activeShimsDir, "*.exe"))
            {
                string b = Path.GetFileNameWithoutExtension(exe);
                if (!names.Contains(b))
                    report.Add($"Stale shim '{b}'", CheckStatus.Warn, "no longer exposed; run tack doctor --fix");
            }
        }

        // Missing binDirs.
        foreach (var (toolName, tool) in config.Tools)
            foreach (var (version, iv) in tool.Versions)
                if (!dirExists(iv.BinDir))
                    report.Add($"Missing binDir for {toolName}@{version}", CheckStatus.Fail, iv.BinDir);

        // Pre-zones bindings with a mid-path wildcard: no single-directory equivalent, so they no longer apply.
        foreach (var glob in ZoneRegistry.Unmigrated(config))
            report.Add($"Binding '{glob}' no longer applies", CheckStatus.Fail,
                "zones take a plain directory (everything under it is included); re-add it with " +
                "'tack zones add <dir> tool@version' and delete it from the bindings list in config.json");

        if (names.Count == 0)
            report.Add("No tools registered", CheckStatus.Warn, "use 'tack tools add' to add an install");

        return report;
    }

    private static HashSet<string> ExposedNames(CentralConfig c)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in c.Tools.Values)
            foreach (var v in t.Versions.Values)
                foreach (var e in v.Exposes)
                    s.Add(e);
        return s;
    }

    /// <summary>Say exactly which PATH holds the shims dir, and where: "system PATH, entry 2". A user-PATH-only
    /// placement gets a plain-English caveat, since Windows searches the whole system PATH first.</summary>
    private static string WherePlaced(Func<EnvironmentVariableTarget, string?> getPath, string shims)
    {
        int machine = Split(getPath(EnvironmentVariableTarget.Machine)).FindIndex(p => Norm(p) == shims);
        if (machine >= 0) return $"system PATH, entry {machine + 1}";

        int user = Split(getPath(EnvironmentVariableTarget.User)).FindIndex(p => Norm(p) == shims);
        return $"user PATH only (entry {user + 1}) - anything on the system PATH is found first; " +
               "'tack doctor --fix' moves it onto the system PATH";
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
