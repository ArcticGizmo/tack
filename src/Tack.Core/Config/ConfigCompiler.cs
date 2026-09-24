using Tack.Core.Resolution;

namespace Tack.Core.Config;

/// <summary>
/// Compiles the human/tool-authored <see cref="CentralConfig"/> (config.json) into the flat, shim-facing
/// <see cref="ResolvedConfig"/> (resolved.json): builds the exposed-name -> owning-tool index, splits the
/// zones list per tool with a precomputed match key, and carries defaults + settings across.
/// Run by the CLI/UI whenever central config changes; the shim only ever reads the output.
/// </summary>
public static class ConfigCompiler
{
    public static ResolvedConfig Compile(CentralConfig c)
    {
        var rc = new ResolvedConfig { Settings = c.Settings };

        foreach (var (toolName, tool) in c.Tools)
        {
            var rt = new ResolvedTool
            {
                Default = c.Defaults.TryGetValue(toolName, out var d) ? d : null,
            };
            foreach (var (version, iv) in tool.Versions)
            {
                rt.Versions[version] = new ResolvedVersion { BinDir = iv.BinDir, Exposes = new List<string>(iv.Exposes) };
                foreach (var exposed in iv.Exposes) rc.Index[exposed] = toolName;
            }
            rc.Index[toolName] = toolName; // a tool's own name always resolves to itself
            rc.Tools[toolName] = rt;
        }

        // Sorted for deterministic output; the resolver doesn't depend on the order.
        foreach (var z in ZoneRegistry.Sorted(c))
        {
            if (!rc.Tools.TryGetValue(z.Tool, out var rt)) continue;
            rt.Zones.Add(new ResolvedZone
            {
                Path = z.Path,
                Key = ZonePath.Normalize(z.Path),
                Version = z.Version,
                Enforce = z.Enforce,
            });
        }

        return rc;
    }
}
