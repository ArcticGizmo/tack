using Tack.Core.Resolution;

namespace Tack.Core.Config;

/// <summary>
/// Compiles the human/tool-authored <see cref="CentralConfig"/> (config.json) into the flat, shim-facing
/// <see cref="ResolvedConfig"/> (resolved.json): builds the exposed-name -> owning-tool index, splits the
/// zones list per tool with a precomputed match key, and carries defaults + settings across.
/// Run by the CLI whenever central config changes; the shim only ever reads the output.
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

        // Sorted for deterministic output; the resolver doesn't depend on the order. A zone for an unregistered
        // tool (which includes the all-tools "*") has no list to join here.
        var sorted = ZoneRegistry.Sorted(c);
        foreach (var z in sorted)
        {
            if (!rc.Tools.TryGetValue(z.Tool, out var rt)) continue;
            rt.Zones.Add(Resolve(z, allTools: false));
        }

        // All-tools zones are copied into every tool's list, so the resolver needs no special case. The tool's
        // own zone at the same directory wins (more specific at the same depth), which also keeps the rule
        // "at most one zone per tool per directory" the resolver relies on. Only `none` is meaningful for "*".
        foreach (var z in sorted.Where(z => ZoneTool.IsAll(z.Tool) && ZoneVersion.IsNone(z.Version)))
        {
            string key = ZonePath.Normalize(z.Path);
            foreach (var rt in rc.Tools.Values)
                if (!rt.Zones.Any(existing => existing.Key == key))
                    rt.Zones.Add(Resolve(z, allTools: true));
        }

        return rc;
    }

    private static ResolvedZone Resolve(Zone z, bool allTools) => new()
    {
        Path = z.Path,
        Key = ZonePath.Normalize(z.Path),
        Version = z.Version,
        Enforce = z.Enforce,
        AllTools = allTools,
    };
}
