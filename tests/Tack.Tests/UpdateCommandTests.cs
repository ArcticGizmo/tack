using Tack.Cli.Commands;
using Xunit;

namespace Tack.Tests;

public class UpdateCommandTests
{
    [Fact]
    public void RepoUrl_prefers_TACK_REPO_over_the_build_metadata()
    {
        Assert.Equal("https://github.com/someone/tack-fork",
            UpdateCommand.RepoUrl(" someone/tack-fork/ ", "https://github.com/ArcticGizmo/tack"));
    }

    [Fact]
    public void RepoUrl_falls_back_to_the_build_metadata_then_the_default()
    {
        Assert.Equal("https://github.com/x/y", UpdateCommand.RepoUrl(null, "https://github.com/x/y"));
        Assert.Equal("https://github.com/ArcticGizmo/tack", UpdateCommand.RepoUrl("", null));
    }
}

public class ZoneSpecTests
{
    [Theory]
    [InlineData("none", "*", "none")]
    [InlineData("NONE", "*", "none")]
    [InlineData("*@none", "*", "none")]
    [InlineData("node@none", "node", "none")]
    [InlineData("node@18", "node", "18")]
    public void ParseSpec_reads_a_bare_none_as_every_tool(string input, string tool, string version)
    {
        var s = ZonesAddSettings.ParseSpec(input);
        Assert.Equal(tool, s.Tool);
        Assert.Equal(version, s.Version, ignoreCase: true);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("node@none", true)]
    [InlineData("*@18", false)]  // one version can't fit every tool
    [InlineData("node", false)]  // no version
    public void Validate_only_allows_none_for_every_tool(string spec, bool valid)
    {
        var settings = new ZonesAddSettings { Dir = @"C:\x", Spec = spec };
        Assert.Equal(valid, settings.Validate().Successful);
    }
}
