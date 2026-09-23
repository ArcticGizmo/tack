using Tack.Core;
using Xunit;

namespace Tack.Tests;

public class CoreTests
{
    [Fact]
    public void TackPaths_are_rooted_under_localappdata_tack()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "tack"), TackPaths.Root);
        Assert.Equal(Path.Combine(local, "tack", "shims"), TackPaths.ShimsDir);
        Assert.Equal(Path.Combine(local, "tack", "resolved.json"), TackPaths.ResolvedJson);
        Assert.Equal(Path.Combine(local, "tack", "config.json"), TackPaths.ConfigJson);
    }
}
