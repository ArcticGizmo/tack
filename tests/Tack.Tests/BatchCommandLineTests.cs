using Tack.Core.Platform;
using Xunit;

namespace Tack.Tests;

// The exact cmd.exe arguments the shim builds for a .cmd/.bat target. End-to-end behaviour (what the script
// actually receives) is covered in ShimTests; these pin the escaping itself.
public sealed class BatchCommandLineTests
{
    private const string Script = @"C:\Program Files\nodejs\npm.cmd";
    private const string Prefix = "/e:ON /v:OFF /d /c \"\"" + Script + "\"";

    private static string Build(params string[] args)
    {
        string? line = BatchCommandLine.Build(Script, args, out var error);
        Assert.Null(error);
        return line!;
    }

    [Fact]
    public void Wraps_the_whole_command_in_one_extra_pair_of_quotes()
    {
        // cmd strips the outer pair, leaving the (spaced) script path's own quotes intact.
        Assert.Equal(Prefix + "\"", Build());
    }

    [Fact]
    public void Leaves_plain_arguments_unquoted()
    {
        Assert.Equal(Prefix + " install lodash@4.17.21 --save-dev ./pkg C:/x\"", Build("install", "lodash@4.17.21", "--save-dev", "./pkg", "C:/x"));
    }

    [Theory]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a&b", "\"a&b\"")]
    [InlineData("a|b", "\"a|b\"")]
    [InlineData("a>b", "\"a>b\"")]
    [InlineData("a<b", "\"a<b\"")]
    [InlineData("a^b", "\"a^b\"")]
    [InlineData("(a)", "\"(a)\"")]
    [InlineData("!a!", "\"!a!\"")]
    public void Quotes_anything_cmd_would_treat_as_an_operator(string arg, string expected)
    {
        Assert.Equal($"{Prefix} {expected}\"", Build(arg));
    }

    [Fact]
    public void Defuses_percent_so_variables_never_expand()
    {
        Assert.Equal(Prefix + " \"%%cd:~,%OS%%cd:~,%\"\"", Build("%OS%"));
    }

    [Fact]
    public void Doubles_an_embedded_quote()
    {
        Assert.Equal(Prefix + " \"say \"\"hi\"\"\"\"", Build("say \"hi\""));
    }

    [Fact]
    public void Doubles_backslashes_before_a_quote_and_at_the_end_of_a_quoted_argument()
    {
        Assert.Equal(Prefix + " \"a\\\\\"\"b\"\"", Build("a\\\"b"));
        Assert.Equal(Prefix + " \"C:\\dir\\\\\"\"", Build("C:\\dir\\"));
    }

    [Fact]
    public void Keeps_an_empty_argument()
    {
        Assert.Equal(Prefix + " \"\"\"", Build(""));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void Refuses_line_breaks_and_nuls(string arg)
    {
        Assert.Null(BatchCommandLine.Build(Script, new[] { "ok", arg }, out var error));
        Assert.Contains("line break", error);
    }

    [Theory]
    [InlineData("C:\\odd\"name.cmd")]
    [InlineData("C:\\dir\\")]
    public void Refuses_a_script_path_that_cannot_be_quoted(string script)
    {
        Assert.Null(BatchCommandLine.Build(script, Array.Empty<string>(), out var error));
        Assert.NotNull(error);
    }
}
