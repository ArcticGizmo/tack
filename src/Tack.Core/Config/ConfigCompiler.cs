using Tack.Core.Resolution;

namespace Tack.Core.Config;

/// <summary>
/// Compiles the human/tool-authored <see cref="CentralConfig"/> (config.json) into the flat, shim-facing
/// <see cref="ResolvedConfig"/> (resolved.json): builds the exposed-name -> owning-tool index, splits the
/// shared bindings list per tool with a precomputed specificity, and carries defaults + settings across.
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

        foreach (var b in c.Bindings)
        {
            int specificity = Glob.Specificity(b.Glob);
            foreach (var (toolName, version) in b.Tools)
            {
                if (!rc.Tools.TryGetValue(toolName, out var rt)) continue;
                rt.Bindings.Add(new ResolvedBinding
                {
                    Glob = b.Glob,
                    Version = version,
                    Enforce = b.Enforce,
                    Specificity = specificity,
                });
            }
        }

        // Deterministic output: most-specific first (the resolver also selects the max).
        foreach (var rt in rc.Tools.Values)
            rt.Bindings.Sort((x, y) => y.Specificity.CompareTo(x.Specificity));

        return rc;
    }
}
