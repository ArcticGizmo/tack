using Tack.Core;
using Xunit;

namespace Tack.Tests;

public class CoreTests
{
    [Fact]
    public void TackPaths_are_rooted_under_localappdata_profile_folder()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = TackProfile.DataFolderName; // "tack", or "tack (Dev)" under a dev build/env

        Assert.Equal(Path.Combine(local, folder), TackPaths.Root);
        Assert.Equal(Path.Combine(local, folder, "shims"), TackPaths.ShimsDir);
        Assert.Equal(Path.Combine(local, folder, "resolved.json"), TackPaths.ResolvedJson);
        Assert.Equal(Path.Combine(local, folder, "config.json"), TackPaths.ConfigJson);
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
