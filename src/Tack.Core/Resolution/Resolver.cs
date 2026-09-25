using Tack.Core.Config;

namespace Tack.Core.Resolution;

public enum ResolutionSource
{
    /// <summary>The exposed name isn't a registered tool binary.</summary>
    Unregistered,
    EnvOverride,
    EnforcedZone,
    TackYml,
    Zone,
    Default,
    /// <summary>The winning zone is a <c>none</c> zone: tack is deliberately off for this tool here. The shim
    /// always falls through to the next binary on PATH - even with <c>noResolution: error</c>, since the user
    /// asked for exactly this.</summary>
    ZoneNone,
    /// <summary>Nothing resolved; the shim should fall through to the next binary on PATH (or error).</summary>
    Passthrough,
    /// <summary>A rule picked a version that isn't registered on this machine.</summary>
    VersionNotInstalled,
}

/// <summary>The outcome of resolving one exposed binary in one directory, with the winning source recorded
/// so `tack info` can always explain "why this version".</summary>
public sealed class Resolution
{
    public required string ExposedName { get; init; }
    public string? Tool { get; init; }
    public ResolutionSource Source { get; init; }
    public string? Version { get; init; }
    public string? BinDir { get; init; }
    public string? Detail { get; init; }

    /// <summary>True when a concrete, installed version was selected.</summary>
    public bool Resolved => Source is ResolutionSource.EnvOverride or ResolutionSource.EnforcedZone
        or ResolutionSource.TackYml or ResolutionSource.Zone or ResolutionSource.Default;
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
///   2. deepest enforced zone         (org enforcement; beats a repo tack.yml)
///   3. nearest tack.yml (walking up) that names the tool
///   4. deepest zone (non-enforced)
///   5. central default
///   6. passthrough                    (or error, per settings)
/// A zone at step 2 or 4 whose version is <c>none</c> wins like any other zone, but resolves to
/// <see cref="ResolutionSource.ZoneNone"/> (passthrough) instead of a version - so it also skips the default.
/// The front-ends (shim, CLI) are thin over this; Core decides.
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

        // 2. Enforced zone (beats tack.yml).
        var enforced = NearestZone(rt.Zones, cwd, enforce: true);
        if (enforced is not null)
            return ZoneVersion.IsNone(enforced.Version)
                ? Off(exposedName, tool, $"enforced zone {enforced.Path} sets {(enforced.AllTools ? "every tool" : tool)} to none")
                : Select(exposedName, tool, rt, enforced.Version, ResolutionSource.EnforcedZone,
                    $"enforced zone {enforced.Path}");

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

        // 4. Non-enforced zone.
        var zone = NearestZone(rt.Zones, cwd, enforce: false);
        if (zone is not null)
            return ZoneVersion.IsNone(zone.Version)
                ? Off(exposedName, tool, $"zone {zone.Path} sets {(zone.AllTools ? "every tool" : tool)} to none")
                : Select(exposedName, tool, rt, zone.Version, ResolutionSource.Zone, $"zone {zone.Path}");

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

    private static Resolution Off(string exposed, string tool, string detail) => new()
    {
        ExposedName = exposed,
        Tool = tool,
        Source = ResolutionSource.ZoneNone,
        Detail = detail,
    };

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

    // Zones that apply are cwd's ancestors, and ancestors nest - so the first hit walking up is the deepest,
    // and there's never a tie to break.
    private static ResolvedZone? NearestZone(List<ResolvedZone> zones, string cwd, bool enforce)
    {
        if (zones.Count == 0) return null;
        foreach (var dir in ZonePath.Ancestors(FullPath(cwd)))
            foreach (var z in zones)
                if (z.Enforce == enforce && string.Equals(z.Key, dir, StringComparison.Ordinal))
                    return z;
        return null;
    }

    private static string FullPath(string cwd)
    {
        try { return Path.GetFullPath(cwd); } catch { return cwd; }
    }

    private static IEnumerable<string> WalkUp(string cwd)
    {
        string? dir = FullPath(cwd);
        while (!string.IsNullOrEmpty(dir))
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
}
