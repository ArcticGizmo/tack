using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.App.Services;

/// <summary>
/// The app's composition root over Tack.Core - the UI counterpart of the CLI's TackEnvironment. Wires the
/// real filesystem, environment and shim payload into Core so every screen stays a thin shell (Core decides,
/// the UI presents). Deliberately mirrors TackEnvironment rather than sharing it, keeping Core a pure engine
/// and each front-end owning its own I/O wiring.
/// </summary>
public sealed class TackServices
{
    public ConfigStore Store { get; } = new();
    public string ShimsDir => TackPaths.ShimsDir;
    public string ResolvedJson => TackPaths.ResolvedJson;
    public string InstallDir => AppContext.BaseDirectory;

    public CentralConfig Load() => Store.Load();
    public void Save(CentralConfig config) => Store.Save(config);

    /// <summary>A resolver over the current config, compiled on the fly so it reflects intent even before a reshim.</summary>
    public Resolver ResolverFor(CentralConfig config) => new(ConfigCompiler.Compile(config));

    /// <summary>Resolve every registered tool for a directory (the dashboard/inspector table), name-sorted.</summary>
    public List<Resolution> ResolveAll(CentralConfig config, string cwd)
    {
        var resolver = ResolverFor(config);
        var ctx = Context();
        var list = new List<Resolution>();
        foreach (var name in config.Tools.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            list.Add(resolver.Resolve(name, cwd, ctx));
        return list;
    }

    /// <summary>Resolve one tool for a directory (the inspector detail panel).</summary>
    public Resolution Resolve(CentralConfig config, string tool, string cwd) =>
        ResolverFor(config).Resolve(tool, cwd, Context());

    public ResolverContext Context() => new()
    {
        GetEnv = Environment.GetEnvironmentVariable,
        ReadFileOrNull = p => { try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; } },
    };

    /// <summary>Locate the real executable a resolution would exec (probing .exe/.cmd/.bat), or null.</summary>
    public static string? LocateBinary(string binDir, string exposedName) =>
        BinaryLocator.Locate(binDir, exposedName, File.Exists);

    /// <summary>PATH health, using the live user + machine PATH.</summary>
    public DoctorReport Doctor(CentralConfig config) =>
        PathDoctor.Run(config, ShimsDir, t => Environment.GetEnvironmentVariable("PATH", t));

    /// <summary>The shim binary (+ any support files) to stamp out, located beside tack-ui.exe. Absent under a
    /// dev `dotnet run`, in which case reshim writes resolved.json but stamps no copies (reported to the user).</summary>
    public ShimPayload ShimPayload()
    {
        string exe = Path.Combine(InstallDir, "tack-shim.exe");
        var support = new List<string>();
        foreach (var f in new[] { "tack-shim.dll", "tack-shim.runtimeconfig.json", "tack-shim.deps.json", "Tack.Core.dll" })
        {
            string p = Path.Combine(InstallDir, f);
            if (File.Exists(p)) support.Add(p);
        }
        return new ShimPayload { ShimExe = exe, SupportFiles = support };
    }

    public ReshimResult Reshim(CentralConfig config) => Reshimmer.Run(config, ShimsDir, ResolvedJson, ShimPayload());

    /// <summary>The live user + machine PATH entries, in the order Windows resolves them (machine, then user).</summary>
    public IReadOnlyList<string> PathEntries()
    {
        var entries = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            var raw = Environment.GetEnvironmentVariable("PATH", target);
            if (string.IsNullOrEmpty(raw)) continue;
            entries.AddRange(raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return entries;
    }

    public static bool SamePath(string a, string b) => Norm(a) == Norm(b);

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
