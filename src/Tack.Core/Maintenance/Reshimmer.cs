using System.Text.Json;
using Tack.Core.Config;

namespace Tack.Core.Maintenance;

/// <summary>The tack-shim binary to copy per tool, plus any support files that must sit beside each copy
/// (empty for a NativeAOT single-file shim; the .dll/.json set for a framework-dependent build).</summary>
public sealed class ShimPayload
{
    public required string ShimExe { get; init; }
    public IReadOnlyList<string> SupportFiles { get; init; } = Array.Empty<string>();
}

public sealed class ReshimResult
{
    public int ToolsCompiled { get; init; }
    public int ShimsWritten { get; init; }
    public int ShimsPruned { get; init; }
    /// <summary>True when the shim binary couldn't be found, so resolved.json was written but no shims stamped.</summary>
    public bool ShimPayloadMissing { get; init; }
    public IReadOnlyList<string> ShimNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Regenerates the shim state from central config: compiles config.json -> resolved.json, then stamps out
/// one copy of tack-shim.exe per exposed binary name in the shims dir and prunes copies that are no longer
/// exposed. Copy (not symlink) is deliberate - privilege-free on Windows (scope plan section 5.1).
/// </summary>
public static class Reshimmer
{
    public static ReshimResult Run(CentralConfig config, string shimsDir, string resolvedJsonPath, ShimPayload payload)
    {
        // 1. Compile + write resolved.json (the shim's fast-lookup).
        var resolved = ConfigCompiler.Compile(config);
        string? resolvedDir = Path.GetDirectoryName(resolvedJsonPath);
        if (!string.IsNullOrEmpty(resolvedDir)) Directory.CreateDirectory(resolvedDir);
        File.WriteAllText(resolvedJsonPath, JsonSerializer.Serialize(resolved, TackJson.Default.ResolvedConfig));

        // 2. Desired shim names = every exposed binary across every version of every tool.
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in config.Tools.Values)
            foreach (var version in tool.Versions.Values)
                foreach (var exposed in version.Exposes)
                    names.Add(exposed);

        Directory.CreateDirectory(shimsDir);

        bool payloadMissing = string.IsNullOrEmpty(payload.ShimExe) || !File.Exists(payload.ShimExe);
        int written = 0, pruned = 0;

        if (!payloadMissing)
        {
            // Support files (framework-dependent builds) live once in the shims dir; every copy loads them.
            foreach (var f in payload.SupportFiles)
                if (File.Exists(f))
                    File.Copy(f, Path.Combine(shimsDir, Path.GetFileName(f)), overwrite: true);

            foreach (var name in names)
            {
                File.Copy(payload.ShimExe, Path.Combine(shimsDir, name + ".exe"), overwrite: true);
                written++;
            }
        }

        // 3. Prune stale shim exes (a name no longer exposed). Support files are .dll/.json, never pruned.
        foreach (var exe in Directory.GetFiles(shimsDir, "*.exe"))
        {
            string baseName = Path.GetFileNameWithoutExtension(exe);
            if (!names.Contains(baseName))
            {
                try { File.Delete(exe); pruned++; } catch { /* in use; doctor will flag it */ }
            }
        }

        return new ReshimResult
        {
            ToolsCompiled = config.Tools.Count,
            ShimsWritten = written,
            ShimsPruned = pruned,
            ShimPayloadMissing = payloadMissing,
            ShimNames = names.ToList(),
        };
    }
}
