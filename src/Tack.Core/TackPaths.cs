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

        private static string DataRoot(string folder) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }
}
