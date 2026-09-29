namespace Tack.Core;

/// <summary>
/// The well-known paths tack uses, split by who owns them (see docs/m9-per-machine-plan.md, "Target layout").
/// <see cref="Machine"/> is the install: binaries, the shims on the system PATH, and PATH backups, all admin-owned
/// under Program Files. <see cref="User"/> is one account's own data under %LOCALAPPDATA%. Either comes in a release
/// and a dev flavour (see <see cref="TackProfile"/>).
/// </summary>
public static class TackPaths
{
    /// <summary>The install. Writing here needs admin.</summary>
    public static class Machine
    {
        /// <summary>The release install's Program Files folder (Velopack's pack ID).</summary>
        public const string ReleaseInstallFolder = "Tack";

        /// <summary>The dev profile's Program Files folder.</summary>
        public const string DevInstallFolder = "Tack (Dev)";

        /// <summary>This profile's install root. A release install is found from where tack.exe runs, which is
        /// Velopack's <c>&lt;root&gt;\current\</c>. A dev build runs from its build output, so it uses a fixed
        /// <c>%ProgramFiles%\Tack (Dev)</c>.</summary>
        public static string Root => TackProfile.IsDev
            ? Path.Combine(ProgramFiles, DevInstallFolder)
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!;

        /// <summary>The shims that go first on the system PATH (one *.exe per exposed tool binary). They sit in the
        /// root rather than in <c>current\</c>, so updates keep them.</summary>
        public static string ShimsDir => Path.Combine(Root, "shims");

        /// <summary>Before/after records of every system PATH edit.</summary>
        public static string PathBackupsDir => Path.Combine(Root, "path-backups");

        /// <summary>The release install's shims, whatever profile this process runs as. A dev instance places
        /// itself directly behind them on the system PATH.</summary>
        public static string ReleaseShimsDir => Path.Combine(ProgramFiles, ReleaseInstallFolder, "shims");

        /// <summary>Both profiles' shims. Anything that hunts PATH for a "real" tool (e.g. <c>tool add</c>
        /// discovery) must skip the other profile's shims too, not just its own.</summary>
        public static IReadOnlyList<string> AllShimsDirs => new[]
        {
            ReleaseShimsDir,
            Path.Combine(ProgramFiles, DevInstallFolder, "shims"),
        };

        private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }

    /// <summary>This account's own data: %LOCALAPPDATA%\tack (or \tack (Dev)).</summary>
    public static class User
    {
        public static string Root => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TackProfile.DataFolderName);

        /// <summary>Central, tool-managed config: registry + zones + defaults + settings (JSON).</summary>
        public static string ConfigJson => Path.Combine(Root, "config.json");

        /// <summary>Compiled fast-lookup the shim reads (regenerated on config change).</summary>
        public static string ResolvedJson => Path.Combine(Root, "resolved.json");

        /// <summary>The shim invocation log (<c>tack log on</c>), written beside resolved.json.</summary>
        public static string ShimLog => Diagnostics.ShimLog.PathFor(ResolvedJson);
    }
}
