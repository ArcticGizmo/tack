using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Cli;

/// <summary>
/// Shared services for the commands: the config store, the well-known paths, a resolver built from current
/// config, and reshim. Every command is a thin shell over Tack.Core - this just wires the real filesystem
/// and environment in.
/// </summary>
public sealed class TackEnvironment
{
    public ConfigStore Store { get; } = new();

    /// <summary>The canonical shims dir wired onto PATH. PATH/doctor checks use this even while disabled.</summary>
    public string ShimsDir => TackPaths.ShimsDir;

    /// <summary>Where the shims dir is parked while disabled.</summary>
    public string DisabledShimsDir => TackPaths.DisabledShimsDir;

    /// <summary>True when tack is disabled (interception off; see <see cref="ShimGate"/>).</summary>
    public bool IsDisabled => ShimGate.IsDisabled(DisabledShimsDir);

    /// <summary>The shims dir writes should target now - the parked dir while disabled, so config still works.</summary>
    public string ActiveShimsDir => ShimGate.ActiveDir(ShimsDir, DisabledShimsDir);

    public string ResolvedJson => TackPaths.ResolvedJson;
    public string ShimLog => TackPaths.ShimLog;
    public string InstallDir => AppContext.BaseDirectory;

    public CentralConfig Load() => Store.Load();
    public void Save(CentralConfig config) => Store.Save(config);

    /// <summary>A resolver over the current config (compiled on the fly, so it reflects intent even before a reshim).</summary>
    public Resolver ResolverFor(CentralConfig config) => new(ConfigCompiler.Compile(config));

    public ResolverContext Context() => new()
    {
        GetEnv = Environment.GetEnvironmentVariable,
        ReadFileOrNull = p => { try { return File.Exists(p) ? File.ReadAllText(p) : null; } catch { return null; } },
    };

    /// <summary>The shim binary to stamp out, plus any support files (framework-dependent builds), located
    /// beside tack.exe. Missing in a `dotnet run` dev build - reshim reports that but still writes resolved.json.</summary>
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

    // Reshim into the ACTIVE dir so adding tools/zones keeps working while disabled (writes land in the
    // parked dir and go live on `tack enable`).
    public ReshimResult Reshim(CentralConfig config) => Reshimmer.Run(config, ActiveShimsDir, ResolvedJson, ShimPayload());
}
