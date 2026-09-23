using Tack.Cli;
using Tack.Core.Config;
using Xunit;

namespace Tack.Tests;

/// <summary>
/// The scope plan's guardrail (section 3.3): the shim's dependency-free mini tack.yml parser and the CLI's
/// full YamlDotNet parser must agree on the supported grammar, or a project file could resolve differently
/// depending on who read it.
/// </summary>
public class TackYmlAgreementTests
{
    [Theory]
    [InlineData("tools:\n  node: 20.11.0\n")]
    [InlineData("tools:\n  node: \"20.11.0\"\n  python: '3.12'\n")]      // quoted values
    [InlineData("# a project file\ntools:\n  node: 18  # inline comment\n")]
    [InlineData("tools:\n  node: 20.11.0\nother: ignored\n")]            // content outside the tools map
    [InlineData("tools:\n  node: 20.11.0\n  python: 3.12.4\n")]
    [InlineData("\n\ntools:\n\n  node: 20.11.0\n")]                       // blank lines
    public void Mini_and_full_parsers_agree(string yml)
    {
        var mini = MiniTackYml.Parse(yml).Tools;
        var full = TackYmlParser.Parse(yml).Tools;

        Assert.Equal(full.Count, mini.Count);
        foreach (var (key, value) in full)
        {
            Assert.True(mini.TryGetValue(key, out var miniValue), $"mini parser missing key '{key}'");
            Assert.Equal(value, miniValue);
        }
    }
}
