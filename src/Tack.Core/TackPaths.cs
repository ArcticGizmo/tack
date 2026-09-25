namespace Tack.Core;

/// <summary>
/// The well-known per-user paths tack uses. Mirrors the on-disk layout in the scope plan (section 3.2):
/// everything lives under %LOCALAPPDATA%\tack (or %LOCALAPPDATA%\tack (Dev) for a dev build - see
/// <see cref="TackProfile"/>). The Velopack install dir (current\) is separate and found via
/// AppContext.BaseDirectory at runtime.
/// </summary>
public static class TackPaths
{
    /// <summary>%LOCALAPPDATA%\tack (or \tack (Dev) under a dev profile).</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TackProfile.DataFolderName);

    /// <summary>The directory of shim exes that goes on PATH (one *.exe per exposed tool binary).</summary>
    public static string ShimsDir => Path.Combine(Root, "shims");

    /// <summary>Where the shims dir is parked while tack is disabled (<c>tack disable</c>). The PATH entry
    /// still points at <see cref="ShimsDir"/>, so with the folder renamed away nothing resolves and tack
    /// stops intercepting - but config/reshim keep writing here, so <c>tack enable</c> brings it back live.</summary>
    public static string DisabledShimsDir => Path.Combine(Root, "shims_disabled");

    /// <summary>The RELEASE profile's shims dirs (live + parked), whatever profile this process runs as. A dev
    /// instance places itself directly behind these on the machine PATH.</summary>
    public static IReadOnlyList<string> ReleaseShimsDirs
    {
        get
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tack");
            return new[] { Path.Combine(root, "shims"), Path.Combine(root, "shims_disabled") };
        }
    }

    /// <summary>Every tack shims dir on this machine - live and parked, for BOTH profiles. A dev instance sits on
    /// PATH behind the release one, so anything that hunts PATH for a "real" tool (e.g. <c>tool add</c>
    /// discovery) must skip the other profile's shims too, not just its own.</summary>
    public static IReadOnlyList<string> AllShimsDirs
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dirs = new List<string>();
            foreach (var folder in new[] { "tack", "tack (Dev)" })
            {
                dirs.Add(Path.Combine(local, folder, "shims"));
                dirs.Add(Path.Combine(local, folder, "shims_disabled"));
            }
            return dirs;
        }
    }

    /// <summary>Central, tool-managed config: registry + zones + defaults (JSON).</summary>
    public static string ConfigJson => Path.Combine(Root, "config.json");

    /// <summary>Compiled fast-lookup the shim reads (regenerated on config change).</summary>
    public static string ResolvedJson => Path.Combine(Root, "resolved.json");
}
