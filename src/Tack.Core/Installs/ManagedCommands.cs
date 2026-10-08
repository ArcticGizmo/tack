using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Resolution;

namespace Tack.Core.Installs;

/// <summary>What a rescan changed for one managed version. <see cref="Skipped"/> names commands found on disk that
/// weren't added, with why.</summary>
public sealed record RescannedVersion(
    string Tool,
    string Version,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<(string Name, string Why)> Skipped)
{
    public bool Changed => Added.Count > 0 || Removed.Count > 0;
}

/// <summary>
/// Brings a managed version's exposed commands in step with its folders, for commands installed after it was:
/// <c>npm i -g typescript</c> puts <c>tsc.cmd</c> in the Node folder, <c>pip install black</c> puts <c>black.exe</c> in
/// Python's <c>Scripts\</c>. Only versions tack installed are rescanned, since tack knows their layout and what's in
/// them. A version added with <c>tool add</c> keeps the names it was registered with (re-adding it rescans its folder).
///
/// <para>A command found on disk is added unless the source says it isn't one (<see cref="IToolSource.NotCommands"/>),
/// it can't be a shim name, the caller refuses it (a Windows command), or another tool already exposes it, which would
/// otherwise quietly take that name over. A name whose file has gone (<c>npm uninstall -g</c>) is dropped, except the
/// tool's own.</para>
/// </summary>
public static class ManagedCommands
{
    /// <summary>Rescan every managed version in <paramref name="config"/>, changing its exposes in place. Returns
    /// each version that changed or had a command skipped.</summary>
    /// <param name="refuse">Why a name can't be shimmed beyond <see cref="ShimName"/>, or null if it can.</param>
    public static List<RescannedVersion> Rescan(CentralConfig config, Func<string, string?>? refuse = null)
    {
        var owners = Owners(config);
        var results = new List<RescannedVersion>();

        foreach (var (tool, registered) in config.Tools.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var (version, installed) in registered.Versions.OrderBy(v => v.Key, VersionOrder.Comparer))
            {
                if (installed.Install is not { } receipt || ToolSources.Find(tool, receipt.Source) is not { } source) continue;
                var result = Rescan(tool, version, installed, source, owners, refuse);
                if (result.Changed || result.Skipped.Count > 0) results.Add(result);
            }
        return results;
    }

    private static RescannedVersion Rescan(string tool, string version, InstalledVersion installed, IToolSource source,
        Dictionary<string, HashSet<string>> owners, Func<string, string?>? refuse)
    {
        var binDirs = installed.BinDirs().ToList();
        var kept = new List<string>();
        var removed = new List<string>();
        foreach (var name in installed.Exposes)
        {
            if (string.Equals(name, tool, StringComparison.OrdinalIgnoreCase)
                || BinaryLocator.Locate(binDirs, name, File.Exists) is not null)
                kept.Add(name);
            else
                removed.Add(name);
        }

        var added = new List<string>();
        var skipped = new List<(string, string)>();
        var found = new SortedSet<string>(binDirs.SelectMany(ToolProbe.DetectExposes), StringComparer.OrdinalIgnoreCase);
        foreach (var name in found)
        {
            if (kept.Contains(name, StringComparer.OrdinalIgnoreCase) || source.NotCommands.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            string? why = ShimName.Problem(name)
                ?? refuse?.Invoke(name)
                ?? (owners.TryGetValue(name, out var others) && others.Any(o => !string.Equals(o, tool, StringComparison.OrdinalIgnoreCase))
                    ? $"{string.Join(", ", others.Order(StringComparer.OrdinalIgnoreCase))} already provides it"
                    : null);
            if (why is not null)
            {
                skipped.Add((name, why));
                continue;
            }
            added.Add(name);
            Own(owners, name, tool);
        }

        if (added.Count > 0 || removed.Count > 0)
            installed.Exposes = kept.Concat(added).ToList();
        return new RescannedVersion(tool, version, added, removed, skipped);
    }

    // Which tools each name belongs to: every name a version exposes, and every tool's own name.
    private static Dictionary<string, HashSet<string>> Owners(CentralConfig config)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tool, registered) in config.Tools)
        {
            Own(owners, tool, tool);
            foreach (var installed in registered.Versions.Values)
                foreach (var name in installed.Exposes)
                    Own(owners, name, tool);
        }
        return owners;
    }

    private static void Own(Dictionary<string, HashSet<string>> owners, string name, string tool)
    {
        if (!owners.TryGetValue(name, out var set))
            owners[name] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(tool);
    }
}
