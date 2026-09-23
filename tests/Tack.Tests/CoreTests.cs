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
}
