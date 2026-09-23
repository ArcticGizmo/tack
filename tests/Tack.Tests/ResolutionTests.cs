using Tack.Core.Config;
using Tack.Core.Resolution;
using Xunit;

namespace Tack.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("C:/work/**", "C:/work", true)]           // trailing /** matches the base dir itself
    [InlineData("C:/work/**", "C:/work/a/b", true)]       // ...and any depth under it
    [InlineData("C:/work/**", "C:/workshop", false)]      // not a prefix-of-segment false match
    [InlineData("C:/work/*/repo", "C:/work/x/repo", true)]
    [InlineData("C:/work/*/repo", "C:/work/x/y/repo", false)] // * is single-segment
    [InlineData("C:\\work\\**", "c:/WORK/a", true)]       // separator + case insensitive
    public void Matches(string glob, string dir, bool expected)
        => Assert.Equal(expected, Glob.IsMatch(glob, dir));

    [Fact]
    public void More_specific_glob_has_higher_specificity()
    {
        Assert.True(Glob.Specificity("C:/work/employer/**") > Glob.Specificity("C:/work/**"));
        Assert.True(Glob.Specificity("C:/work/exact") > Glob.Specificity("C:/work/**")); // no wildcard wins
    }
}

public class VersionMatchTests
{
    private static readonly string[] Installed = { "20.11.0", "20.9.0", "18.19.0", "18.19.1" };

    [Fact] public void Exact_wins() => Assert.Equal("18.19.0", VersionMatch.Best(Installed, "18.19.0"));
    [Fact] public void Prefix_picks_highest() => Assert.Equal("18.19.1", VersionMatch.Best(Installed, "18"));
    [Fact] public void Dotted_prefix() => Assert.Equal("20.11.0", VersionMatch.Best(Installed, "20.11"));
    [Fact] public void No_match_is_null() => Assert.Null(VersionMatch.Best(Installed, "21"));
    [Fact] public void Prefix_respects_dot_boundary() => Assert.Null(VersionMatch.Best(new[] { "20.1.0" }, "2"));
}

public class MiniTackYmlTests
{
    [Fact]
    public void Parses_flat_tools_map_with_quotes_and_comments()
    {
        string yml = "# a project file\ntools:\n  node: 20.11.0\n  python: \"3.12\"  # pinned\n";
        var doc = MiniTackYml.Parse(yml);
        Assert.Equal("20.11.0", doc.Tools["node"]);
        Assert.Equal("3.12", doc.Tools["python"]);
    }

    [Fact]
    public void Ignores_content_after_the_tools_block_dedents()
    {
        string yml = "tools:\n  node: 20.11.0\nother: value\n";
        var doc = MiniTackYml.Parse(yml);
        Assert.True(doc.Tools.ContainsKey("node"));
        Assert.False(doc.Tools.ContainsKey("other"));
    }
}

public class CompilerTests
{
    [Fact]
    public void Builds_index_and_splits_bindings_per_tool()
    {
        var central = Sample();
        var rc = ConfigCompiler.Compile(central);

        Assert.Equal("node", rc.Index["npm"]);   // exposed name -> owning tool
        Assert.Equal("node", rc.Index["node"]);  // tool's own name maps to itself
        Assert.Equal("20.11.0", rc.Tools["node"].Default);
        Assert.Equal(2, rc.Tools["node"].Bindings.Count); // both the C:/work and C:/corp bindings target node
    }

    internal static CentralConfig Sample()
    {
        return new CentralConfig
        {
            Tools =
            {
                ["node"] = new RegisteredTool
                {
                    Versions =
                    {
                        ["20.11.0"] = new InstalledVersion { BinDir = @"C:\tools\node20", Exposes = { "node", "npm", "npx" } },
                        ["18.19.0"] = new InstalledVersion { BinDir = @"C:\tools\node18", Exposes = { "node", "npm", "npx" } },
                    },
                },
            },
            Defaults = { ["node"] = "20.11.0" },
            Bindings =
            {
                new Binding { Glob = "C:/work/**", Tools = { ["node"] = "18" } },
                new Binding { Glob = "C:/corp/**", Tools = { ["node"] = "18.19.0" }, Enforce = true },
            },
        };
    }
}

public class ResolverTests
{
    private static Resolver BuildResolver() => new(ConfigCompiler.Compile(CompilerTests.Sample()));

    private static ResolverContext Ctx(
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? files = null)
    {
        var e = env ?? new();
        var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (files is not null)
            foreach (var (k, v) in files) f[k.Replace('\\', '/')] = v;

        return new ResolverContext
        {
            GetEnv = name => e.TryGetValue(name, out var v) ? v : null,
            ReadFileOrNull = path => f.TryGetValue(path.Replace('\\', '/'), out var v) ? v : null,
        };
    }

    [Fact]
    public void Default_applies_when_nothing_else_matches()
    {
        var r = BuildResolver().Resolve("node", @"C:\somewhere\else", Ctx());
        Assert.Equal(ResolutionSource.Default, r.Source);
        Assert.Equal("20.11.0", r.Version);
        Assert.Equal(@"C:\tools\node20", r.BinDir);
    }

    [Fact]
    public void Sibling_binary_follows_its_owning_tool()
    {
        var r = BuildResolver().Resolve("npm", @"C:\somewhere", Ctx());
        Assert.Equal("node", r.Tool);
        Assert.Equal("20.11.0", r.Version); // npm resolves to node's version
    }

    [Fact]
    public void Env_override_beats_everything()
    {
        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "18.19.0" };
        var r = BuildResolver().Resolve("node", @"C:\corp\anything", Ctx(env: env));
        Assert.Equal(ResolutionSource.EnvOverride, r.Source);
        Assert.Equal("18.19.0", r.Version);
    }

    [Fact]
    public void Binding_matches_by_directory_and_prefix_version()
    {
        var r = BuildResolver().Resolve("node", @"C:\work\projA", Ctx());
        Assert.Equal(ResolutionSource.Binding, r.Source);
        Assert.Equal("18.19.0", r.Version); // "18" prefix -> 18.19.0
    }

    [Fact]
    public void TackYml_beats_a_non_enforced_binding()
    {
        var files = new Dictionary<string, string> { [@"C:\work\projA\tack.yml"] = "tools:\n  node: 20.11.0\n" };
        var r = BuildResolver().Resolve("node", @"C:\work\projA\src", Ctx(files: files));
        Assert.Equal(ResolutionSource.TackYml, r.Source);
        Assert.Equal("20.11.0", r.Version); // nearest tack.yml (walked up from src) wins over the C:/work binding
    }

    [Fact]
    public void Enforced_binding_beats_tack_yml()
    {
        var files = new Dictionary<string, string> { [@"C:\corp\proj\tack.yml"] = "tools:\n  node: 20.11.0\n" };
        var r = BuildResolver().Resolve("node", @"C:\corp\proj", Ctx(files: files));
        Assert.Equal(ResolutionSource.EnforceBinding, r.Source);
        Assert.Equal("18.19.0", r.Version);
    }

    [Fact]
    public void Unregistered_tool_is_reported()
    {
        var r = BuildResolver().Resolve("ruby", @"C:\x", Ctx());
        Assert.Equal(ResolutionSource.Unregistered, r.Source);
    }

    [Fact]
    public void A_rule_naming_an_uninstalled_version_is_reported()
    {
        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "99.0.0" };
        var r = BuildResolver().Resolve("node", @"C:\x", Ctx(env: env));
        Assert.Equal(ResolutionSource.VersionNotInstalled, r.Source);
    }

    [Fact]
    public void Passthrough_when_no_default_and_no_rule()
    {
        var central = CompilerTests.Sample();
        central.Defaults.Clear(); // remove the node default
        var resolver = new Resolver(ConfigCompiler.Compile(central));
        var r = resolver.Resolve("node", @"C:\nowhere", Ctx());
        Assert.Equal(ResolutionSource.Passthrough, r.Source);
    }
}
