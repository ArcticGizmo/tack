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
        Func<string, bool>? dirExists = null)
    {
        fileExists ??= File.Exists;
        dirExists ??= Directory.Exists;

        var report = new DoctorReport();
        string shims = Norm(shimsDir);
        var names = ExposedNames(config);

        report.Add("Shims directory exists",
            dirExists(shimsDir) ? CheckStatus.Ok : CheckStatus.Warn, shimsDir);

        // Effective resolution order on Windows: machine PATH entries, then user PATH entries.
        var effective = Split(getPath(EnvironmentVariableTarget.Machine))
            .Concat(Split(getPath(EnvironmentVariableTarget.User)))
            .ToList();
        int shimsIndex = effective.FindIndex(p => Norm(p) == shims);

        report.Add("Shims directory is on PATH",
            shimsIndex >= 0 ? CheckStatus.Ok : CheckStatus.Fail,
            shimsIndex >= 0 ? "on the user/machine PATH" : "not on PATH - tack won't intercept tool calls");

        // Shadowing: an earlier PATH dir that already provides a shimmed binary wins over the shim.
        if (shimsIndex >= 0)
        {
            foreach (var name in names)
            {
                for (int i = 0; i < shimsIndex; i++)
                {
                    if (BinaryLocator.Locate(effective[i], name, fileExists) is not null)
                    {
                        report.Add($"'{name}' is shadowed", CheckStatus.Warn,
                            $"{effective[i]} precedes the shims dir on PATH");
                        break;
                    }
                }
            }
        }

        // Stale shims.
        if (dirExists(shimsDir))
        {
            foreach (var exe in Directory.GetFiles(shimsDir, "*.exe"))
            {
                string b = Path.GetFileNameWithoutExtension(exe);
                if (!names.Contains(b))
                    report.Add($"Stale shim '{b}'", CheckStatus.Warn, "no longer exposed; run tack reshim");
            }
        }

        // Missing binDirs.
        foreach (var (toolName, tool) in config.Tools)
            foreach (var (version, iv) in tool.Versions)
                if (!dirExists(iv.BinDir))
                    report.Add($"Missing binDir for {toolName}@{version}", CheckStatus.Fail, iv.BinDir);

        if (names.Count == 0)
            report.Add("No tools registered", CheckStatus.Warn, "use 'tack register' to add an install");

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

    private static List<string> Split(string? p) => string.IsNullOrEmpty(p)
        ? new List<string>()
        : p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
