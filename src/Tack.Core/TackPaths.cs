namespace Tack.Core;

/// <summary>
/// The well-known paths tack uses (see docs/user-path-plan.md, "Target layout"). Everything is this account's own,
/// under %LOCALAPPDATA%, and comes in a release and a dev flavour (see <see cref="TackProfile"/>). The release data
/// folder (<c>tack</c>) is also Velopack's install root (<c>Tack</c>; NTFS doesn't mind the case), which is why the
/// shims sit beside <c>current\</c> rather than in it: updates replace <c>current\</c> but keep the rest.
/// </summary>
public static class TackPaths
{
    public static class User
    {
        public static string Root => DataRoot(TackProfile.DataFolderName);

        /// <summary>Central, tool-managed config: registry + zones + defaults + settings (JSON).</summary>
        public static string ConfigJson => Path.Combine(Root, "config.json");

        /// <summary>Compiled fast-lookup the shim reads (regenerated on config change).</summary>
        public static string ResolvedJson => Path.Combine(Root, "resolved.json");

        /// <summary>The shim invocation log (<c>tack log on</c>), written beside resolved.json.</summary>
        public static string ShimLog => Diagnostics.ShimLog.PathFor(ResolvedJson);

        /// <summary>The shims that go first on the user PATH (one *.exe per exposed tool binary).</summary>
        public static string ShimsDir => Path.Combine(Root, "shims");

        /// <summary>Before/after records of every user PATH edit.</summary>
        public static string PathBackupsDir => Path.Combine(Root, "path-backups");

        /// <summary>Managed installs (<c>tack tool install</c>): one folder per <c>&lt;tool&gt;\&lt;version&gt;</c>, plus the
        /// dot-folders below. Local, never roaming: they're large and specific to this machine's architecture.</summary>
        public static string InstallsDir => Path.Combine(Root, "installs");

        /// <summary>Where an install downloads and unpacks before it's renamed into place (same volume, so the rename
        /// is atomic).</summary>
        public static string InstallsStagingDir => Path.Combine(InstallsDir, ".staging");

        /// <summary>Where a removed version is renamed to before it's deleted, so an in-use version fails cleanly.</summary>
        public static string InstallsTrashDir => Path.Combine(InstallsDir, ".trash");

        /// <summary>Downloaded version indexes, kept for a while so listing what's available needn't hit the network.</summary>
        public static string InstallsCacheDir => Path.Combine(InstallsDir, ".cache");

        /// <summary>The release profile's shims, whatever profile this process runs as. A dev instance places
        /// itself directly behind them on the user PATH.</summary>
        public static string ReleaseShimsDir => Path.Combine(DataRoot(TackProfile.ReleaseDataFolder), "shims");

        /// <summary>Both profiles' shims. Anything that hunts PATH for a "real" tool (e.g. <c>tool add</c>
        /// discovery) must skip the other profile's shims too, not just its own.</summary>
        public static IReadOnlyList<string> AllShimsDirs => new[]
        {
            ReleaseShimsDir,
            Path.Combine(DataRoot(TackProfile.DevDataFolder), "shims"),
        };

        internal static string DataRoot(string folder) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }

    /// <summary>
    /// Every folder any tack build has put on a PATH: both profiles' shims, the release install's <c>current\</c>,
    /// and the Program Files folders from the per-machine experiment (M9). None of them belongs on the system PATH,
    /// and tack never writes it, so <c>doctor</c> reports any it finds there for you to remove (ADR 0002).
    /// </summary>
    public static IReadOnlyList<string> EverWired
    {
        get
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return User.AllShimsDirs.Concat(new[]
            {
                Path.Combine(User.DataRoot(TackProfile.ReleaseDataFolder), "current"),
                Path.Combine(programFiles, "Tack", "shims"),
                Path.Combine(programFiles, "Tack", "current"),
                Path.Combine(programFiles, "Tack (Dev)", "shims"),
            }).ToList();
        }
    }
}
