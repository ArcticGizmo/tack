using Tack.Core.Resolution;

namespace Tack.Core.Config;

/// <summary>What a removal did, so the CLI can report it and warn about fallout.</summary>
public sealed record RemovalResult(
    IReadOnlyList<string> Removed,           // "node@20.11.0" entries actually removed
    IReadOnlyList<string> ToolsDropped,      // tool names left with no versions (dropped entirely)
    IReadOnlyList<string> DefaultsRepointed, // "node: 20.11.0 -> 18.19.0" when a default's version went away
    IReadOnlyList<string> OrphanedZones);    // zones now pointing at a version that no longer exists

/// <summary>
/// Read/mutate helpers over the central registry that the CLI's <c>tack tool</c> commands share. Pure (no
/// filesystem), so removal - with its default-repointing and zone fallout - is unit-testable without disk.
/// </summary>
public static class ToolRegistry
{
    /// <summary>Every registered tool@version, sorted by tool (case-insensitive) then <see cref="VersionOrder"/>.</summary>
    public static List<string> Entries(CentralConfig config)
    {
        var list = new List<string>();
        foreach (var (tool, t) in config.Tools.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var version in t.Versions.Keys.Order(VersionOrder.Comparer))
                list.Add($"{tool}@{version}");
        return list;
    }

    /// <summary>The registered versions of one tool, oldest first; empty if the tool isn't registered.</summary>
    public static List<string> VersionsOf(CentralConfig config, string tool) =>
        config.Tools.TryGetValue(tool, out var t)
            ? t.Versions.Keys.Order(VersionOrder.Comparer).ToList()
            : new List<string>();

    public static bool Exists(CentralConfig config, string tool, string version) =>
        config.Tools.TryGetValue(tool, out var t) && t.Versions.ContainsKey(version);

    /// <summary>
    /// Register (or re-register) <paramref name="tool"/>@<paramref name="version"/> (mutates <paramref name="config"/>).
    /// The one place both <c>tack tool add</c> and <c>tack tool install</c> go through, so they can't drift. The tool's
    /// own name is always exposed (first, if it had to be added), re-registering a version replaces everything about
    /// it, and the first version a tool gets becomes its default. Returns true when it became the default.
    /// </summary>
    public static bool Register(CentralConfig config, string tool, string version, InstalledVersion installed)
    {
        if (!installed.Exposes.Any(x => string.Equals(x, tool, StringComparison.OrdinalIgnoreCase)))
            installed.Exposes.Insert(0, tool);

        if (!config.Tools.TryGetValue(tool, out var registered))
        {
            registered = new RegisteredTool();
            config.Tools[tool] = registered;
        }
        registered.Versions[version] = installed;

        if (config.Defaults.ContainsKey(tool)) return false;
        config.Defaults[tool] = version;
        return true;
    }

    /// <summary>
    /// Remove the given tool@version pairs (mutates <paramref name="config"/>). Drops any tool left with no
    /// versions (and its default), repoints a default whose version was removed to the highest remaining
    /// version, and reports zones left pointing at a version that no longer exists (zones are not
    /// touched - the user is warned). Unknown targets are ignored. Matching is case-insensitive.
    /// </summary>
    public static RemovalResult Remove(CentralConfig config, IEnumerable<(string Tool, string Version)> targets)
    {
        var removed = new List<string>();
        var dropped = new List<string>();
        var repointed = new List<string>();

        foreach (var (tool, version) in targets)
        {
            if (!config.Tools.TryGetValue(tool, out var t)) continue;
            // Resolve to the canonical keys so reporting matches what's stored.
            string? toolKey = config.Tools.Keys.FirstOrDefault(k => k.Equals(tool, StringComparison.OrdinalIgnoreCase));
            string? versionKey = t.Versions.Keys.FirstOrDefault(v => v.Equals(version, StringComparison.OrdinalIgnoreCase));
            if (toolKey is null || versionKey is null) continue;

            if (t.Versions.Remove(versionKey))
                removed.Add($"{toolKey}@{versionKey}");
        }

        // Drop empty tools and fix up defaults.
        foreach (var toolKey in config.Tools.Keys.ToList())
        {
            var t = config.Tools[toolKey];
            if (t.Versions.Count == 0)
            {
                config.Tools.Remove(toolKey);
                config.Defaults.Remove(toolKey);
                dropped.Add(toolKey);
                continue;
            }
            if (config.Defaults.TryGetValue(toolKey, out var def) && !t.Versions.ContainsKey(def))
            {
                string next = t.Versions.Keys.OrderDescending(VersionOrder.Comparer).First();
                config.Defaults[toolKey] = next;
                repointed.Add($"{toolKey}: {def} -> {next}");
            }
        }

        // Zones that now point at a missing tool/version (warn only). A none zone names no version to lose.
        var orphaned = new List<string>();
        foreach (var z in config.Zones)
            if (!ZoneVersion.IsNone(z.Version) && !Exists(config, z.Tool, z.Version))
                orphaned.Add($"{z.Path} ({z.Tool}@{z.Version})");

        return new RemovalResult(removed, dropped, repointed, orphaned);
    }
}
