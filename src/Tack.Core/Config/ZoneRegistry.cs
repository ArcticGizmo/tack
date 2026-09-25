using Tack.Core.Resolution;

namespace Tack.Core.Config;

/// <summary>What <see cref="ZoneRegistry.Set"/> did. <see cref="Previous"/> is the replaced entry on an update.</summary>
public sealed record ZoneSetResult(bool Added, Zone? Previous);

/// <summary>
/// Read/mutate helpers over the central zones behind the CLI's <c>tack zones</c> commands. A
/// zone is keyed by (directory, tool): setting one that exists replaces it rather than adding a rival, so two
/// zones can never disagree about the same tool in the same directory. Pure (no filesystem).
/// </summary>
public static class ZoneRegistry
{
    /// <summary>Every zone, sorted by directory then tool (case-insensitive).</summary>
    public static List<Zone> Sorted(CentralConfig config) => config.Zones
        .OrderBy(z => ZonePath.Normalize(z.Path), StringComparer.Ordinal)
        .ThenBy(z => z.Tool, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Add or replace the zone for (<paramref name="path"/>, <paramref name="tool"/>). The path is
    /// stored as given, minus any trailing separator; the caller makes it absolute.</summary>
    public static ZoneSetResult Set(CentralConfig config, string path, string tool, string version, bool enforce)
    {
        string trimmed = TrimSeparators(path);
        var zone = new Zone { Path = trimmed, Tool = tool, Version = version, Enforce = enforce };

        int i = config.Zones.FindIndex(z => Same(z, trimmed, tool));
        if (i < 0)
        {
            config.Zones.Add(zone);
            return new ZoneSetResult(true, null);
        }

        var previous = config.Zones[i];
        config.Zones[i] = zone;
        return new ZoneSetResult(false, previous);
    }

    /// <summary>Remove the zones at <paramref name="path"/> - just <paramref name="tool"/>'s, or all of them
    /// when it's null. Returns what was removed (empty when nothing matched).</summary>
    public static List<Zone> Remove(CentralConfig config, string path, string? tool = null)
    {
        string key = ZonePath.Normalize(path);
        var removed = config.Zones
            .Where(z => ZonePath.Normalize(z.Path) == key
                        && (tool is null || z.Tool.Equals(tool, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        config.Zones.RemoveAll(removed.Contains);
        return removed;
    }

    /// <summary>
    /// Fold any pre-zones glob <see cref="CentralConfig.Bindings"/> into zones (mutates <paramref name="config"/>).
    /// "X/**" and a bare "X" become a zone at X, one per tool. A binding with a wildcard anywhere else has no
    /// single-directory equivalent: it stays in <see cref="CentralConfig.Bindings"/> (and is ignored by
    /// resolution) so doctor/reshim can name it. Idempotent; run by <see cref="ConfigStore.Load"/>.
    /// </summary>
    public static void Migrate(CentralConfig config)
    {
        if (config.Bindings is null) return;

        var kept = new List<LegacyBinding>();
        foreach (var b in config.Bindings)
        {
            string? path = ZonePath.FromLegacyGlob(b.Glob);
            if (path is null)
            {
                kept.Add(b);
                continue;
            }
            foreach (var (tool, version) in b.Tools)
                Set(config, path, tool, version, b.Enforce);
        }

        config.Bindings = kept.Count > 0 ? kept : null;
    }

    /// <summary>The globs of bindings <see cref="Migrate"/> couldn't turn into zones.</summary>
    public static List<string> Unmigrated(CentralConfig config) =>
        config.Bindings?.Select(b => b.Glob).ToList() ?? new List<string>();

    private static bool Same(Zone z, string path, string tool) =>
        ZonePath.Normalize(z.Path) == ZonePath.Normalize(path)
        && z.Tool.Equals(tool, StringComparison.OrdinalIgnoreCase);

    // Keep a drive root's separator ("C:\"), which is a different path from "C:" (the drive's current dir).
    private static string TrimSeparators(string path)
    {
        string t = path.Trim().TrimEnd('/', '\\');
        return t.Length == 2 && t[1] == ':' ? t + "\\" : t;
    }
}
