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
    public string ShimsDir => TackPaths.ShimsDir;
    public string ResolvedJson => TackPaths.ResolvedJson;
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

    public ReshimResult Reshim(CentralConfig config) => Reshimmer.Run(config, ShimsDir, ResolvedJson, ShimPayload());
}
