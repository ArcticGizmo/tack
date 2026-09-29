namespace Tack.Core;

/// <summary>
/// Selects the app "profile" so a development instance can run without clobbering an installed release's
/// data. Dev mode uses a separate data directory (<c>tack (Dev)</c> instead of <c>tack</c>) under
/// %LOCALAPPDATA% - a separate config.json, resolved.json and shims dir - so a `dotnet run` / `run.bat`
/// build never touches the config, shims or PATH wiring of a real installed tack. Ported from perch's
/// AppProfile.
///
/// It's on automatically for <b>Debug</b> builds (so local dev is isolated with zero ceremony) and can be
/// forced either way with the <c>TACK_DEV</c> environment variable: any non-empty value other than
/// <c>0</c>/<c>false</c> forces it on; <c>0</c>/<c>false</c> forces it off (e.g. to point a Debug build at
/// the real profile). Because every head shares this one Tack.Core, all of them agree within a build config.
/// </summary>
public static class TackProfile
{
    /// <summary>True when running as an isolated development instance (see the type remarks).</summary>
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary>The release profile's %LOCALAPPDATA% subfolder.</summary>
    public const string ReleaseDataFolder = "tack";

    /// <summary>The dev profile's %LOCALAPPDATA% subfolder.</summary>
    public const string DevDataFolder = "tack (Dev)";

    /// <summary>The %LOCALAPPDATA% subfolder for this profile - <c>tack</c> or <c>tack (Dev)</c>.</summary>
    public static string DataFolderName => IsDev ? DevDataFolder : ReleaseDataFolder;

    /// <summary>
    /// The data folder a shim living in <paramref name="shimsDir"/> belongs to: <see cref="DevDataFolder"/> when the
    /// shims folder sits in one named <c>Tack (Dev)</c> (any case), else <see cref="ReleaseDataFolder"/>. The shim
    /// uses this instead of <see cref="IsDev"/>: its folder is admin-owned, so the caller can't switch its profile the
    /// way <c>TACK_DEV</c> could, and a Release-built shim stamped into the dev folder still reads the dev config.
    /// </summary>
    public static string ForShimsDir(string shimsDir)
    {
        string? owner = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(shimsDir)));
        return string.Equals(owner, "Tack (Dev)", StringComparison.OrdinalIgnoreCase) ? DevDataFolder : ReleaseDataFolder;
    }

    /// <summary>Suffix for user-facing labels (window title, doctor) - <c>""</c> or <c>" (Dev)"</c>.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    private static bool ComputeIsDev()
    {
        var env = Environment.GetEnvironmentVariable("TACK_DEV");
        if (!string.IsNullOrEmpty(env))
            return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
