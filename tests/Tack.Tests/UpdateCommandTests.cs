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
