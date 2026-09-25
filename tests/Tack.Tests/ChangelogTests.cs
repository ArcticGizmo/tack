using Tack.Core.Changelog;
using Xunit;

namespace Tack.Tests;

public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        ---

        ## [Unreleased]

        ---

        ## [v0.3.0] - 2026-09-23

        - Third thing
        - Another third thing

        ---

        ## [v0.2.0] - 2026-09-10

        - Second thing

        ---

        ## [v0.1.0] - 2026-09-01

        - First thing

        ---
        """;

    [Fact]
    public void Parse_splits_every_section_newest_first_with_unreleased_unversioned()
    {
        var sections = ChangelogParser.Parse(Sample);

        Assert.Equal(4, sections.Count);
        Assert.Null(sections[0].Version);                 // [Unreleased]
        Assert.Equal("[Unreleased]", sections[0].Display); // unversioned display is the raw heading text
        Assert.Equal(new Version(0, 3, 0), sections[1].Version);
        Assert.Equal(new Version(0, 2, 0), sections[2].Version);
        Assert.Equal(new Version(0, 1, 0), sections[3].Version);
    }

    [Fact]
    public void Parse_trims_trailing_rule_and_blank_lines_but_keeps_the_heading()
    {
        var v030 = ChangelogParser.Parse(Sample).First(s => s.Version == new Version(0, 3, 0));

        Assert.StartsWith("## [v0.3.0]", v030.Block[0]);
        Assert.DoesNotContain("---", v030.Block);
        Assert.Equal("- Another third thing", v030.Block[^1]); // no trailing blanks/rule
    }

    [Fact]
    public void Latest_skips_unreleased_and_returns_the_newest_released_section()
    {
        var latest = ChangelogParser.Latest(Sample);

        Assert.NotNull(latest);
        Assert.Equal(new Version(0, 3, 0), latest!.Version);
    }

    [Fact]
    public void StripInline_removes_bold_italic_code_and_links()
    {
        Assert.Equal("bold and italic", ChangelogMarkup.StripInline("**bold** and *italic*"));
        Assert.Equal("run tack reshim", ChangelogMarkup.StripInline("run `tack reshim`"));
        Assert.Equal("see the docs", ChangelogMarkup.StripInline("see [the docs](https://example.com)"));
    }
}
