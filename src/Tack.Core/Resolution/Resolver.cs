using Tack.Core.Config;

namespace Tack.Core.Resolution;

public enum ResolutionSource
{
    /// <summary>The exposed name isn't a registered tool binary.</summary>
    Unregistered,
    EnvOverride,
    EnforceBinding,
    TackYml,
    Binding,
    Default,
    /// <summary>Nothing resolved; the shim should fall through to the next binary on PATH (or error).</summary>
    Passthrough,
    /// <summary>A rule picked a version that isn't registered on this machine.</summary>
    VersionNotInstalled,
}

/// <summary>The outcome of resolving one exposed binary in one directory, with the winning source recorded
/// so `tack info` / the UI can always explain "why this version".</summary>
public sealed class Resolution
{
    public required string ExposedName { get; init; }
    public string? Tool { get; init; }
    public ResolutionSource Source { get; init; }
    public string? Version { get; init; }
    public string? BinDir { get; init; }
    public string? Detail { get; init; }

    /// <summary>True when a concrete, installed version was selected.</summary>
    public bool Resolved => Source is ResolutionSource.EnvOverride or ResolutionSource.EnforceBinding
        or ResolutionSource.TackYml or ResolutionSource.Binding or ResolutionSource.Default;
}

/// <summary>Pluggable environment access so the resolver is pure and unit-testable (no direct FS/env reads).</summary>
public sealed class ResolverContext
{
    /// <summary>Read an environment variable (name -> value or null).</summary>
    public required Func<string, string?> GetEnv { get; init; }

    /// <summary>Read a file's text by absolute path, or null if it doesn't exist.</summary>
    public required Func<string, string?> ReadFileOrNull { get; init; }
}

/// <summary>
/// The precedence engine (scope plan section 4). Highest wins:
///   1. env override  TACK_&lt;TOOL&gt;_VERSION
///   2. enforced central binding      (org enforcement; beats a repo tack.yml)
///   3. nearest tack.yml (walking up) that names the tool
///   4. central binding (non-enforced)
///   5. central default
///   6. passthrough                    (or error, per settings)
/// The front-ends (shim, CLI, UI) are thin over this; Core decides.
/// </summary>
public sealed class Resolver
{
    private readonly ResolvedConfig _cfg;

    public Resolver(ResolvedConfig cfg) => _cfg = cfg;

    public Resolution Resolve(string exposedName, string cwd, ResolverContext ctx)
    {
        if (!_cfg.Index.TryGetValue(exposedName, out var tool) || !_cfg.Tools.TryGetValue(tool, out var rt))
            return new Resolution
            {
                ExposedName = exposedName,
                Source = ResolutionSource.Unregistered,
                Detail = $"'{exposedName}' is not a registered tool binary",
            };

        // 1. Env override.
        var env = ctx.GetEnv($"TACK_{tool.ToUpperInvariant()}_VERSION");
        if (!string.IsNullOrWhiteSpace(env))
            return Select(exposedName, tool, rt, env.Trim(), ResolutionSource.EnvOverride,
                $"TACK_{tool.ToUpperInvariant()}_VERSION");

        // 2. Enforced binding (beats tack.yml).
        var enforced = BestBinding(rt.Bindings, cwd, enforce: true);
        if (enforced is not null)
            return Select(exposedName, tool, rt, enforced.Version, ResolutionSource.EnforceBinding,
                $"enforced binding {enforced.Glob}");

        // 3. Nearest tack.yml (walking up) that names this tool.
        foreach (var dir in WalkUp(cwd))
        {
            string path = Path.Combine(dir, "tack.yml");
            var text = ctx.ReadFileOrNull(path);
            if (text is null) continue;
            var doc = MiniTackYml.Parse(text);
            if (doc.Tools.TryGetValue(tool, out var v) && !string.IsNullOrWhiteSpace(v))
                return Select(exposedName, tool, rt, v.Trim(), ResolutionSource.TackYml, path);
        }

        // 4. Non-enforced binding.
        var binding = BestBinding(rt.Bindings, cwd, enforce: false);
        if (binding is not null)
            return Select(exposedName, tool, rt, binding.Version, ResolutionSource.Binding,
                $"binding {binding.Glob}");

        // 5. Central default.
        if (!string.IsNullOrWhiteSpace(rt.Default))
            return Select(exposedName, tool, rt, rt.Default!, ResolutionSource.Default, "central default");

        // 6. Nothing resolved.
        return new Resolution
        {
            ExposedName = exposedName,
            Tool = tool,
            Source = ResolutionSource.Passthrough,
            Detail = "no rule resolved for this directory",
        };
    }

    private static Resolution Select(string exposed, string tool, ResolvedTool rt, string requested,
        ResolutionSource source, string detail)
    {
        var match = VersionMatch.Best(rt.Versions.Keys, requested);
        if (match is null)
            return new Resolution
            {
                ExposedName = exposed,
                Tool = tool,
                Source = ResolutionSource.VersionNotInstalled,
                Version = requested,
                Detail = $"{detail}: version '{requested}' is not registered for {tool}",
            };

        return new Resolution
        {
            ExposedName = exposed,
            Tool = tool,
            Source = source,
            Version = match,
            BinDir = rt.Versions[match].BinDir,
            Detail = detail,
        };
    }

    private static ResolvedBinding? BestBinding(List<ResolvedBinding> bindings, string cwd, bool enforce)
    {
        ResolvedBinding? best = null;
        foreach (var b in bindings)
        {
            if (b.Enforce != enforce) continue;
            if (!Glob.IsMatch(b.Glob, cwd)) continue;
            if (best is null || b.Specificity > best.Specificity) best = b;
        }
        return best;
    }

    private static IEnumerable<string> WalkUp(string cwd)
    {
        string? dir;
        try { dir = Path.GetFullPath(cwd); } catch { dir = cwd; }
        while (!string.IsNullOrEmpty(dir))
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
}
