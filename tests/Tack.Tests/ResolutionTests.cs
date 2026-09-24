using Tack.Core.Config;
using Tack.Core.Resolution;
using Xunit;

namespace Tack.Tests;

public class ZonePathTests
{
    [Fact]
    public void Ancestors_walk_up_deepest_first_normalized()
        => Assert.Equal(new[] { "c:/work/a/b", "c:/work/a", "c:/work", "c:" }, ZonePath.Ancestors(@"C:\Work\a\b\"));

    [Fact]
    public void Drive_root_normalizes_to_its_ancestor_form()
        => Assert.Contains(ZonePath.Normalize(@"C:\"), ZonePath.Ancestors(@"C:\work"));

    [Theory]
    [InlineData("C:/work/**", "C:/work")]
    [InlineData(@"C:\work\**", @"C:\work")]
    [InlineData("C:/work/exact", "C:/work/exact")]   // a bare path now covers its children too
    [InlineData("C:/work/", "C:/work")]
    [InlineData("C:/work/*/api/**", null)]           // mid-path wildcard: no single-directory equivalent
    [InlineData("**/node_modules", null)]
    public void Legacy_globs_map_to_a_directory(string glob, string? expected)
        => Assert.Equal(expected, ZonePath.FromLegacyGlob(glob));

    [Theory]
    [InlineData(@"C:\work", true)]
    [InlineData(@"C:\work\*", false)]
    [InlineData(@"work\sub", false)]
    [InlineData("", false)]
    public void Validates_zone_directories(string path, bool ok)
        => Assert.Equal(ok, ZonePath.Validate(path) is null);
}

public class ZoneRegistryTests
{
    [Fact]
    public void Setting_the_same_directory_and_tool_replaces_rather_than_duplicates()
    {
        var c = new CentralConfig();
        Assert.True(ZoneRegistry.Set(c, @"C:\work", "node", "18", enforce: false).Added);

        var r = ZoneRegistry.Set(c, "c:/WORK/", "node", "20", enforce: true); // same dir, different spelling
        Assert.False(r.Added);
        Assert.Equal("18", r.Previous!.Version);
        var z = Assert.Single(c.Zones);
        Assert.Equal("20", z.Version);
        Assert.True(z.Enforce);
    }

    [Fact]
    public void Different_tools_share_a_directory()
    {
        var c = new CentralConfig();
        ZoneRegistry.Set(c, @"C:\work", "node", "18", false);
        ZoneRegistry.Set(c, @"C:\work", "python", "3.12", false);
        Assert.Equal(2, c.Zones.Count);
    }

    [Fact]
    public void Remove_takes_one_tool_or_the_whole_directory()
    {
        var c = new CentralConfig();
        ZoneRegistry.Set(c, @"C:\work", "node", "18", false);
        ZoneRegistry.Set(c, @"C:\work", "python", "3.12", false);
        ZoneRegistry.Set(c, @"C:\other", "node", "20", false);

        Assert.Single(ZoneRegistry.Remove(c, @"c:\work\", "NODE"));
        Assert.Equal(2, c.Zones.Count);

        Assert.Single(ZoneRegistry.Remove(c, @"C:\work"));
        Assert.Equal(@"C:\other", Assert.Single(c.Zones).Path);

        Assert.Empty(ZoneRegistry.Remove(c, @"C:\nowhere"));
    }

    [Fact]
    public void Drive_root_keeps_its_separator()
    {
        var c = new CentralConfig();
        ZoneRegistry.Set(c, @"C:\", "node", "18", false);
        Assert.Equal(@"C:\", c.Zones[0].Path);
    }

    [Fact]
    public void Migrates_legacy_bindings_and_keeps_the_ones_it_cannot()
    {
        var c = new CentralConfig
        {
            Bindings = new List<LegacyBinding>
            {
                new() { Glob = "C:/work/**", Tools = { ["node"] = "18", ["python"] = "3.12" } },
                new() { Glob = "C:/corp/**", Tools = { ["node"] = "18.19.0" }, Enforce = true },
                new() { Glob = "C:/work/*/api/**", Tools = { ["node"] = "20" } },
            },
        };

        ZoneRegistry.Migrate(c);

        Assert.Equal(3, c.Zones.Count);
        Assert.Contains(c.Zones, z => z.Path == "C:/work" && z.Tool == "python");
        Assert.Contains(c.Zones, z => z.Path == "C:/corp" && z.Enforce);
        Assert.Equal(new[] { "C:/work/*/api/**" }, ZoneRegistry.Unmigrated(c));

        ZoneRegistry.Migrate(c); // idempotent
        Assert.Equal(3, c.Zones.Count);
    }

    [Fact]
    public void A_fully_migrated_config_drops_the_legacy_list()
    {
        var c = new CentralConfig { Bindings = new List<LegacyBinding> { new() { Glob = "C:/work/**", Tools = { ["node"] = "18" } } } };
        ZoneRegistry.Migrate(c);
        Assert.Null(c.Bindings);
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
    public void Builds_index_and_splits_zones_per_tool()
    {
        var central = Sample();
        var rc = ConfigCompiler.Compile(central);

        Assert.Equal("node", rc.Index["npm"]);   // exposed name -> owning tool
        Assert.Equal("node", rc.Index["node"]);  // tool's own name maps to itself
        Assert.Equal("20.11.0", rc.Tools["node"].Default);
        Assert.Equal(3, rc.Tools["node"].Zones.Count);
        Assert.Contains(rc.Tools["node"].Zones, z => z.Key == "c:/work" && z.Path == @"C:\work");
    }

    [Fact]
    public void Zones_for_unregistered_tools_are_dropped()
    {
        var central = Sample();
        central.Zones.Add(new Zone { Path = @"C:\work", Tool = "ruby", Version = "3" });
        Assert.False(ConfigCompiler.Compile(central).Tools.ContainsKey("ruby"));
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
            Zones =
            {
                new Zone { Path = @"C:\work", Tool = "node", Version = "18" },
                new Zone { Path = @"C:\work\modern", Tool = "node", Version = "20" },
                new Zone { Path = @"C:\corp", Tool = "node", Version = "18.19.0", Enforce = true },
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
    public void Zone_covers_its_subtree_with_prefix_version()
    {
        var r = BuildResolver().Resolve("node", @"C:\work\projA\src", Ctx());
        Assert.Equal(ResolutionSource.Zone, r.Source);
        Assert.Equal("18.19.0", r.Version); // "18" prefix -> 18.19.0
        Assert.Equal(@"zone C:\work", r.Detail);
    }

    [Fact]
    public void Zone_covers_its_own_directory()
        => Assert.Equal(ResolutionSource.Zone, BuildResolver().Resolve("node", @"c:\WORK", Ctx()).Source);

    [Fact]
    public void Deepest_zone_wins()
    {
        var r = BuildResolver().Resolve("node", @"C:\work\modern\app", Ctx());
        Assert.Equal("20.11.0", r.Version);
        Assert.Equal(@"zone C:\work\modern", r.Detail);
    }

    [Fact]
    public void Zone_matches_whole_directory_names_only()
        => Assert.Equal(ResolutionSource.Default, BuildResolver().Resolve("node", @"C:\workshop", Ctx()).Source);

    [Fact]
    public void TackYml_beats_a_non_enforced_zone()
    {
        var files = new Dictionary<string, string> { [@"C:\work\projA\tack.yml"] = "tools:\n  node: 20.11.0\n" };
        var r = BuildResolver().Resolve("node", @"C:\work\projA\src", Ctx(files: files));
        Assert.Equal(ResolutionSource.TackYml, r.Source);
        Assert.Equal("20.11.0", r.Version); // nearest tack.yml (walked up from src) wins over the C:\work zone
    }

    [Fact]
    public void Enforced_zone_beats_tack_yml()
    {
        var files = new Dictionary<string, string> { [@"C:\corp\proj\tack.yml"] = "tools:\n  node: 20.11.0\n" };
        var r = BuildResolver().Resolve("node", @"C:\corp\proj", Ctx(files: files));
        Assert.Equal(ResolutionSource.EnforcedZone, r.Source);
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
