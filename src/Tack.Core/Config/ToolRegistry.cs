namespace Tack.Core.Config;

/// <summary>What a removal did, so the CLI can report it and warn about fallout.</summary>
public sealed record RemovalResult(
    IReadOnlyList<string> Removed,           // "node@20.11.0" entries actually removed
    IReadOnlyList<string> ToolsDropped,      // tool names left with no versions (dropped entirely)
    IReadOnlyList<string> DefaultsRepointed, // "node: 20.11.0 -> 18.19.0" when a default's version went away
    IReadOnlyList<string> OrphanedBindings); // binding globs now pointing at a version that no longer exists

/// <summary>
/// Read/mutate helpers over the central registry that the CLI's <c>tack tools</c> commands share. Pure (no
/// filesystem), so removal - with its default-repointing and binding fallout - is unit-testable without disk.
/// </summary>
public static class ToolRegistry
{
    /// <summary>Every registered tool@version, sorted by tool then version (case-insensitive).</summary>
    public static List<string> Entries(CentralConfig config)
    {
        var list = new List<string>();
        foreach (var (tool, t) in config.Tools.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var version in t.Versions.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                list.Add($"{tool}@{version}");
        return list;
    }

    /// <summary>The registered versions of one tool, sorted; empty if the tool isn't registered.</summary>
    public static List<string> VersionsOf(CentralConfig config, string tool) =>
        config.Tools.TryGetValue(tool, out var t)
            ? t.Versions.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    public static bool Exists(CentralConfig config, string tool, string version) =>
        config.Tools.TryGetValue(tool, out var t) && t.Versions.ContainsKey(version);

    /// <summary>
    /// Remove the given tool@version pairs (mutates <paramref name="config"/>). Drops any tool left with no
    /// versions (and its default), repoints a default whose version was removed to the highest remaining
    /// version, and reports bindings left pointing at a version that no longer exists (bindings are not
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
                string next = t.Versions.Keys.OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).First();
                config.Defaults[toolKey] = next;
                repointed.Add($"{toolKey}: {def} -> {next}");
            }
        }

        // Bindings that now point at a missing tool/version (warn only).
        var orphaned = new List<string>();
        foreach (var b in config.Bindings)
            foreach (var (tool, version) in b.Tools)
                if (!Exists(config, tool, version))
                {
                    orphaned.Add($"{b.Glob} ({tool}@{version})");
                    break;
                }

        return new RemovalResult(removed, dropped, repointed, orphaned);
    }
}
