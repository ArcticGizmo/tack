using Tack.Core;
using Xunit;

namespace Tack.Tests;

public class CoreTests
{
    [Fact]
    public void User_paths_are_rooted_under_localappdata_profile_folder()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = TackProfile.DataFolderName; // "tack", or "tack (Dev)" under a dev build/env

        Assert.Equal(Path.Combine(local, folder), TackPaths.User.Root);
        Assert.Equal(Path.Combine(local, folder, "resolved.json"), TackPaths.User.ResolvedJson);
        Assert.Equal(Path.Combine(local, folder, "config.json"), TackPaths.User.ConfigJson);
    }

    [Fact]
    public void Machine_paths_are_rooted_at_the_install()
    {
        // A release build finds its install from where it runs (Velopack's <root>\current\); a dev build runs
        // from build output, so it has a fixed Program Files root instead.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string root = TackProfile.IsDev
            ? Path.Combine(programFiles, "Tack (Dev)")
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!;

        Assert.Equal(root, TackPaths.Machine.Root);
        Assert.Equal(Path.Combine(root, "shims"), TackPaths.Machine.ShimsDir);
        Assert.Equal(Path.Combine(root, "path-backups"), TackPaths.Machine.PathBackupsDir);
        Assert.Equal(Path.Combine(programFiles, "Tack", "shims"), TackPaths.Machine.ReleaseShimsDir);
        if (TackProfile.IsDev) Assert.Contains(TackPaths.Machine.ShimsDir, TackPaths.Machine.AllShimsDirs);
    }

    [Fact]
    public void Each_install_shims_dir_maps_back_to_its_own_data_folder()
    {
        // What the shim relies on: the profile it derives from its folder is the one whose config the CLI writes.
        foreach (var dir in TackPaths.Machine.AllShimsDirs)
            Assert.Equal(dir.Contains("(Dev)") ? "tack (Dev)" : "tack", TackProfile.ForShimsDir(dir));
    }

    [Fact]
    public void Dev_profile_uses_a_separate_labelled_data_folder()
    {
        // The two profiles must never share a data folder, and the dev one carries the label the window
        // title / doctor show. (This test binary is a Debug build, so IsDev is true here.)
        Assert.Equal(TackProfile.IsDev, TackProfile.DataFolderName == "tack (Dev)");
        Assert.Equal(TackProfile.IsDev, TackProfile.DisplaySuffix == " (Dev)");
        Assert.Contains(TackProfile.DataFolderName, new[] { "tack", "tack (Dev)" });
    }

    [Theory]
    [InlineData(@"C:\Program Files\Tack (Dev)\shims\", "tack (Dev)")]
    [InlineData(@"C:\Program Files\Tack (Dev)\shims", "tack (Dev)")]
    [InlineData(@"C:\Users\someone\AppData\Local\tack (Dev)\shims\", "tack (Dev)")] // case doesn't matter
    [InlineData(@"C:\Program Files\Tack\shims\", "tack")]
    [InlineData(@"C:\Users\someone\AppData\Local\tack\shims\", "tack")]
    [InlineData(@"C:\Program Files\Tack (Dev) old\shims\", "tack")]                  // only an exact name
    [InlineData(@"C:\Program Files\Tack (Dev)\", "tack")]                            // the folder itself, not its parent
    [InlineData(@"C:\", "tack")]
    public void A_shim_takes_its_profile_from_the_folder_its_shims_folder_is_in(string shimsDir, string expected)
    {
        // Not from IsDev: a shim's profile must not follow TACK_DEV or how the shim binary was built.
        Assert.Equal(expected, TackProfile.ForShimsDir(shimsDir));
    }
}
