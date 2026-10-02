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
        Assert.True(ZoneRegistry.Set(c, @"C:\work", "node", "18", ignoreTackFiles: false).Added);

        var r = ZoneRegistry.Set(c, "c:/WORK/", "node", "20", ignoreTackFiles: true); // same dir, different spelling
        Assert.False(r.Added);
        Assert.Equal("18", r.Previous!.Version);
        var z = Assert.Single(c.Zones);
        Assert.Equal("20", z.Version);
        Assert.True(z.IgnoreTackFiles);
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
                new() { Glob = "C:/corp/**", Tools = { ["node"] = "18.19.0" } },
                new() { Glob = "C:/work/*/api/**", Tools = { ["node"] = "20" } },
            },
        };

        ZoneRegistry.Migrate(c);

        Assert.Equal(3, c.Zones.Count);
        Assert.Contains(c.Zones, z => z.Path == "C:/work" && z.Tool == "python");
        Assert.Contains(c.Zones, z => z.Path == "C:/corp" && z.Tool == "node");
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
    [Fact] public void Prefix_prefers_a_release_over_its_pre_release() =>
        Assert.Equal("3.15.0", VersionMatch.Best(new[] { "3.15.0rc2", "3.15.0", "3.15.0b4" }, "3.15"));
}

public class VersionOrderTests
{
    [Theory]
    [InlineData("10.0.0", "9.11.2")]       // numeric, not text
    [InlineData("20.11.0", "20.9.0")]
    [InlineData("3.12.0", "3.12")]         // a missing segment sorts first
    [InlineData("3.15.0", "3.15.0rc2")]    // a release beats its pre-release
    [InlineData("3.15.0rc2", "3.15.0rc1")]
    [InlineData("3.15.0rc1", "3.15.0b4")]  // a < b < rc
    [InlineData("3.15.0b1", "3.15.0a7")]
    [InlineData("3.15.0a1", "3.14.8")]     // a pre-release of a newer version still beats an older release
    [InlineData("work", "personal")]       // names compare as text
    [InlineData("personal", "20")]         // against a number, a name compares as text: digits sort first
    public void Orders_newer_after_older(string newer, string older)
    {
        Assert.True(VersionOrder.Compare(newer, older) > 0, $"{newer} should sort after {older}");
        Assert.True(VersionOrder.Compare(older, newer) < 0, $"{older} should sort before {newer}");
    }

    [Theory]
    [InlineData("20.11.0", "20.11.0")]
    [InlineData("Work", "work")]
    [InlineData("3.15.0RC2", "3.15.0rc2")]
    public void Equal_versions_compare_equal(string a, string b) => Assert.Equal(0, VersionOrder.Compare(a, b));

    [Fact]
    public void Sorts_a_list()
    {
        var sorted = new[] { "10.0.0", "3.15.0rc2", "9.0.0", "3.15.0", "3.14.8" }.Order(VersionOrder.Comparer);
        Assert.Equal(new[] { "3.14.8", "3.15.0rc2", "3.15.0", "9.0.0", "10.0.0" }, sorted);
    }

    [Fact]
    public void A_number_too_long_for_a_long_still_compares()
    {
        Assert.NotEqual(0, VersionOrder.Compare("99999999999999999999999", "1"));
    }
}

public class BinaryLocatorTests
{
    [Fact]
    public void Searches_the_bin_dirs_in_order()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\py\python.exe", @"C:\py\Scripts\pip.exe" };
        string[] dirs = { @"C:\py", @"C:\py\Scripts" };

        Assert.Equal(@"C:\py\python.exe", BinaryLocator.Locate(dirs, "python", files.Contains));
        Assert.Equal(@"C:\py\Scripts\pip.exe", BinaryLocator.Locate(dirs, "pip", files.Contains));
        Assert.Null(BinaryLocator.Locate(dirs, "black", files.Contains));
    }

    [Fact]
    public void An_earlier_dir_wins_whatever_the_extensions()
    {
        // Every extension is tried in one folder before the next, as cmd does.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a\pip.cmd", @"C:\b\pip.exe" };
        Assert.Equal(@"C:\a\pip.cmd", BinaryLocator.Locate(new[] { @"C:\a", @"C:\b" }, "pip", files.Contains));
    }
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

    [Fact]
    public void All_tools_zones_are_copied_into_every_tool_unless_it_has_its_own_zone_there()
    {
        var rc = ConfigCompiler.Compile(ResolverTests.AllToolsConfig());

        Assert.False(rc.Tools.ContainsKey("*")); // not a tool
        var pyLegacy = Assert.Single(rc.Tools["python"].Zones, z => z.Key == "c:/work/legacy");
        Assert.True(pyLegacy.AllTools);
        Assert.Equal("none", pyLegacy.Version);

        // C:\mixed: python gets the all-tools zone; node keeps only its own zone there.
        Assert.Single(rc.Tools["python"].Zones, z => z.Key == "c:/mixed" && z.AllTools);
        var nodeMixed = Assert.Single(rc.Tools["node"].Zones, z => z.Key == "c:/mixed");
        Assert.False(nodeMixed.AllTools);
        Assert.Equal("18", nodeMixed.Version);
    }

    [Fact]
    public void An_all_tools_zone_with_a_real_version_is_ignored()
    {
        var central = Sample();
        central.Zones.Add(new Zone { Path = @"C:\bad", Tool = "*", Version = "18" }); // hand-edited config
        Assert.DoesNotContain(ConfigCompiler.Compile(central).Tools["node"].Zones, z => z.Key == "c:/bad");
    }

    [Fact]
    public void AllTools_is_left_out_of_resolved_json_when_false()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(
            ConfigCompiler.Compile(ResolverTests.AllToolsConfig()), TackJson.Default.ResolvedConfig);
        Assert.Contains("\"allTools\": true", json);
        Assert.DoesNotContain("\"allTools\": false", json);
    }

    [Fact]
    public void A_versions_env_is_carried_into_resolved_json_and_left_out_when_empty()
    {
        var central = Sample();
        central.Tools["node"].Versions["20.11.0"].Env = new() { ["NODE_OPTIONS"] = "--max-old-space-size=4096" };
        var rc = ConfigCompiler.Compile(central);

        Assert.Equal("--max-old-space-size=4096", rc.Tools["node"].Versions["20.11.0"].Env!["node_options"]);
        Assert.Null(rc.Tools["node"].Versions["18.19.0"].Env);
        string json = System.Text.Json.JsonSerializer.Serialize(rc, TackJson.Default.ResolvedConfig);
        Assert.Equal(1, json.Split("\"env\"").Length - 1); // only the version that has one writes the key
    }

    [Fact]
    public void Extra_bin_dirs_reach_the_resolution_and_are_left_out_when_empty()
    {
        var central = Sample();
        central.Tools["node"].Versions["20.11.0"].ExtraBinDirs = new() { @"C:\tools\node20\extra" };
        central.Tools["node"].Versions["18.19.0"].ExtraBinDirs = new(); // empty is the same as none
        var rc = ConfigCompiler.Compile(central);

        Assert.Null(rc.Tools["node"].Versions["18.19.0"].ExtraBinDirs);
        string json = System.Text.Json.JsonSerializer.Serialize(rc, TackJson.Default.ResolvedConfig);
        Assert.Equal(1, json.Split("\"extraBinDirs\"").Length - 1);

        var ctx = new ResolverContext { GetEnv = _ => null, ReadFileOrNull = _ => null };
        var r = new Resolver(rc).Resolve("npm", @"C:\elsewhere", ctx); // the default, 20.11.0
        Assert.Equal(new[] { @"C:\tools\node20", @"C:\tools\node20\extra" }, r.BinDirs);
    }

    [Fact]
    public void An_unresolved_name_searches_no_bin_dirs()
    {
        var ctx = new ResolverContext { GetEnv = _ => null, ReadFileOrNull = _ => null };
        Assert.Empty(new Resolver(ConfigCompiler.Compile(Sample())).Resolve("ruby", @"C:\x", ctx).BinDirs);
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
                new Zone { Path = @"C:\corp", Tool = "node", Version = "18.19.0", IgnoreTackFiles = true },
            },
        };
    }
}

public class VersionEnvTests
{
    [Theory]
    [InlineData("CLAUDE_CONFIG_DIR=C:\\cfg", "CLAUDE_CONFIG_DIR", "C:\\cfg")]
    [InlineData("NODE_OPTIONS=--foo=bar", "NODE_OPTIONS", "--foo=bar")] // only the first = splits
    [InlineData("FOO=", "FOO", "")]                                     // empty value = unset
    [InlineData("P=%USERPROFILE%\\x", "P", "%USERPROFILE%\\x")]          // kept unexpanded
    public void Parses_name_value(string spec, string name, string value)
    {
        Assert.True(VersionEnv.TryParse(spec, out var n, out var v, out var error), error);
        Assert.Equal(name, n);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData("FOO")]
    [InlineData("=bar")]
    [InlineData("MY VAR=1")]
    public void Rejects_what_isnt_name_value(string spec)
        => Assert.False(VersionEnv.TryParse(spec, out _, out _, out _));

    [Fact]
    public void Parse_is_null_for_none_and_last_duplicate_wins()
    {
        Assert.Null(VersionEnv.Parse(null, out _));
        Assert.Null(VersionEnv.Parse(Array.Empty<string>(), out _));

        var env = VersionEnv.Parse(new[] { "A=1", "a=2", "B=3" }, out var error);
        Assert.Null(error);
        Assert.Equal(2, env!.Count);
        Assert.Equal("2", env["A"]);
    }

    [Fact]
    public void Parse_reports_the_first_bad_entry()
    {
        Assert.Null(VersionEnv.Parse(new[] { "A=1", "oops" }, out var error));
        Assert.Contains("oops", error);
    }

    [Fact]
    public void ApplyTo_sets_expanded_values_and_removes_empty_ones()
    {
        var target = new Dictionary<string, string?> { ["KEEP"] = "k", ["DROP"] = "d" };
        var env = new Dictionary<string, string> { ["NEW"] = "%X%", ["DROP"] = "" };

        VersionEnv.ApplyTo(target, env, v => v.Replace("%X%", "expanded"));

        Assert.Equal("expanded", target["NEW"]);
        Assert.Equal("k", target["KEEP"]);
        Assert.False(target.ContainsKey("DROP"));
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
    public void A_named_version_carries_its_env_and_other_versions_dont()
    {
        // The CLAUDE_CONFIG_DIR case: the same install registered twice, one name setting a variable.
        var central = new CentralConfig
        {
            Tools =
            {
                ["claude"] = new RegisteredTool
                {
                    Versions =
                    {
                        ["stock"] = new InstalledVersion { BinDir = @"C:\bin", Exposes = { "claude" } },
                        ["work"] = new InstalledVersion
                        {
                            BinDir = @"C:\bin", Exposes = { "claude" },
                            Env = new() { ["CLAUDE_CONFIG_DIR"] = @"%USERPROFILE%\.claude-work" },
                        },
                    },
                },
            },
            Defaults = { ["claude"] = "stock" },
            Zones = { new Zone { Path = @"C:\work", Tool = "claude", Version = "work" } },
        };
        var resolver = new Resolver(ConfigCompiler.Compile(central));

        var inZone = resolver.Resolve("claude", @"C:\work\repo", Ctx());
        Assert.Equal("work", inZone.Version);
        Assert.Equal(@"%USERPROFILE%\.claude-work", inZone.Env!["CLAUDE_CONFIG_DIR"]); // expanded by the shim, not here

        var outside = resolver.Resolve("claude", @"C:\elsewhere", Ctx());
        Assert.Equal("stock", outside.Version);
        Assert.Null(outside.Env);
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
    public void TackYml_beats_an_ordinary_zone()
    {
        var files = new Dictionary<string, string> { [@"C:\work\projA\tack.yml"] = "tools:\n  node: 20.11.0\n" };
        var r = BuildResolver().Resolve("node", @"C:\work\projA\src", Ctx(files: files));
        Assert.Equal(ResolutionSource.TackYml, r.Source);
        Assert.Equal("20.11.0", r.Version); // nearest tack.yml (walked up from src) wins over the C:\work zone
    }

    [Fact]
    public void Zone_ignoring_tack_files_beats_tack_yml()
    {
        var files = new Dictionary<string, string> { [@"C:\corp\proj\tack.yml"] = "tools:\n  node: 20.11.0\n" };
        var r = BuildResolver().Resolve("node", @"C:\corp\proj", Ctx(files: files));
        Assert.Equal(ResolutionSource.ZoneIgnoringTackFiles, r.Source);
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

    // A tack.yml comes with whatever repo you clone, so it may only choose between versions already registered in
    // your own config. Whatever it says, the folder that runs comes from config.json.
    [Theory]
    [InlineData(@"C:\evil")]
    [InlineData(@"C:\evil\node.exe")]
    [InlineData(@"..\..\evil")]
    [InlineData(@"\\attacker\share\node")]
    [InlineData("%TEMP%")]
    [InlineData("*")]
    [InlineData("20.11.0/../../evil")]
    [InlineData("20.11.0.")]   // a dotted prefix of nothing registered
    public void Tack_yml_cannot_name_anything_but_a_registered_version(string value)
    {
        var files = new Dictionary<string, string> { [@"C:\repo\tack.yml"] = $"tools:\n  node: \"{value}\"\n" };
        var r = BuildResolver().Resolve("node", @"C:\repo", Ctx(files: files));

        Assert.Equal(ResolutionSource.VersionNotInstalled, r.Source);
        Assert.Null(r.BinDir);
        Assert.Null(r.Env);
    }

    [Fact]
    public void Env_override_cannot_name_a_path_either()
    {
        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = @"C:\evil" };
        var r = BuildResolver().Resolve("node", @"C:\x", Ctx(env: env));
        Assert.Equal(ResolutionSource.VersionNotInstalled, r.Source);
        Assert.Null(r.BinDir);
    }

    [Fact]
    public void Tack_yml_only_ever_resolves_to_a_registered_bindir()
    {
        var registered = CompilerTests.Sample().Tools["node"].Versions.Values.Select(v => v.BinDir).ToHashSet();
        foreach (var value in new[] { "18", "18.19.0", "20", "20.11.0" })
        {
            var files = new Dictionary<string, string> { [@"C:\repo\tack.yml"] = $"tools:\n  node: {value}\n" };
            var r = BuildResolver().Resolve("node", @"C:\repo", Ctx(files: files));
            Assert.Equal(ResolutionSource.TackYml, r.Source);
            Assert.Contains(r.BinDir!, registered);
        }
    }

    [Fact]
    public void Tack_yml_entries_for_unregistered_tools_are_ignored()
    {
        // No way to invent a tool from a repo: `ruby` isn't registered, so there's nothing for it to pick.
        var files = new Dictionary<string, string> { [@"C:\repo\tack.yml"] = "tools:\n  ruby: C:\\evil\n  node: 18\n" };
        Assert.Equal(ResolutionSource.Unregistered, BuildResolver().Resolve("ruby", @"C:\repo", Ctx(files: files)).Source);
        Assert.Equal("18.19.0", BuildResolver().Resolve("node", @"C:\repo", Ctx(files: files)).Version);
    }

    // Sample() plus: C:\work\legacy switches node off, C:\work\legacy\revived switches it back on, and C:\corp\free
    // is a none zone that ignores tack files, inside the C:\corp one that does too.
    private static Resolver NoneResolver()
    {
        var c = CompilerTests.Sample();
        c.Zones.Add(new Zone { Path = @"C:\work\legacy", Tool = "node", Version = "none" });
        c.Zones.Add(new Zone { Path = @"C:\work\legacy\revived", Tool = "node", Version = "20" });
        c.Zones.Add(new Zone { Path = @"C:\corp\free", Tool = "node", Version = "None", IgnoreTackFiles = true });
        return new Resolver(ConfigCompiler.Compile(c));
    }

    [Fact]
    public void None_zone_switches_off_an_ancestor_zone_and_the_default()
    {
        var r = NoneResolver().Resolve("npm", @"C:\work\legacy\app", Ctx());
        Assert.Equal(ResolutionSource.ZoneNone, r.Source);
        Assert.False(r.Resolved);
        Assert.Null(r.Version);
        Assert.Equal("node", r.Tool);
        Assert.Equal(@"zone C:\work\legacy sets node to none", r.Detail);
    }

    [Fact]
    public void Deeper_zone_switches_tack_back_on_under_a_none_zone()
    {
        var r = NoneResolver().Resolve("node", @"C:\work\legacy\revived\src", Ctx());
        Assert.Equal(ResolutionSource.Zone, r.Source);
        Assert.Equal("20.11.0", r.Version);
    }

    [Fact]
    public void TackYml_beats_an_ordinary_none_zone()
    {
        var files = new Dictionary<string, string> { [@"C:\work\legacy\app\tack.yml"] = "tools:\n  node: 18\n" };
        var r = NoneResolver().Resolve("node", @"C:\work\legacy\app", Ctx(files: files));
        Assert.Equal(ResolutionSource.TackYml, r.Source);
    }

    [Fact]
    public void None_zone_ignoring_tack_files_beats_tack_yml_and_the_zone_above_it()
    {
        var files = new Dictionary<string, string> { [@"C:\corp\free\x\tack.yml"] = "tools:\n  node: 20\n" };
        var r = NoneResolver().Resolve("node", @"C:\corp\free\x", Ctx(files: files));
        Assert.Equal(ResolutionSource.ZoneNone, r.Source);
        Assert.StartsWith(@"zone C:\corp\free (ignores tack files)", r.Detail);
    }

    [Fact]
    public void Env_override_still_beats_a_none_zone()
    {
        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "18.19.0" };
        Assert.Equal(ResolutionSource.EnvOverride, NoneResolver().Resolve("node", @"C:\work\legacy", Ctx(env: env)).Source);
    }

    // Sample() plus python (with a default), an all-tools none zone at C:\work\legacy, node back on in
    // C:\work\legacy\app, and a node zone at the SAME directory as an all-tools zone at C:\mixed.
    internal static CentralConfig AllToolsConfig()
    {
        var c = CompilerTests.Sample();
        c.Tools["python"] = new RegisteredTool
        {
            Versions = { ["3.12.1"] = new InstalledVersion { BinDir = @"C:\tools\py312", Exposes = { "python", "pip" } } },
        };
        c.Defaults["python"] = "3.12.1";
        c.Zones.Add(new Zone { Path = @"C:\work\legacy", Tool = "*", Version = "none" });
        c.Zones.Add(new Zone { Path = @"C:\work\legacy\app", Tool = "node", Version = "20" });
        c.Zones.Add(new Zone { Path = @"C:\mixed", Tool = "*", Version = "none" });
        c.Zones.Add(new Zone { Path = @"C:\mixed", Tool = "node", Version = "18" });
        return c;
    }

    private static Resolver AllToolsResolver() => new(ConfigCompiler.Compile(AllToolsConfig()));

    [Fact]
    public void All_tools_none_zone_switches_off_every_tool()
    {
        var resolver = AllToolsResolver();
        foreach (var cmd in new[] { "node", "npm", "python", "pip" })
        {
            var r = resolver.Resolve(cmd, @"C:\work\legacy\x", Ctx());
            Assert.Equal(ResolutionSource.ZoneNone, r.Source);
            Assert.Equal(@"zone C:\work\legacy sets every tool to none", r.Detail);
        }
    }

    [Fact]
    public void A_deeper_tool_zone_switches_just_that_tool_back_on()
    {
        var resolver = AllToolsResolver();
        Assert.Equal("20.11.0", resolver.Resolve("node", @"C:\work\legacy\app", Ctx()).Version);
        Assert.Equal(ResolutionSource.ZoneNone, resolver.Resolve("python", @"C:\work\legacy\app", Ctx()).Source);
    }

    [Fact]
    public void A_tools_own_zone_beats_an_all_tools_zone_at_the_same_directory()
    {
        var resolver = AllToolsResolver();
        var node = resolver.Resolve("node", @"C:\mixed", Ctx());
        Assert.Equal(ResolutionSource.Zone, node.Source);
        Assert.Equal("18.19.0", node.Version);
        Assert.Equal(ResolutionSource.ZoneNone, resolver.Resolve("python", @"C:\mixed", Ctx()).Source);
    }

    [Fact]
    public void TackYml_beats_an_ordinary_all_tools_zone()
    {
        var files = new Dictionary<string, string> { [@"C:\work\legacy\x\tack.yml"] = "tools:\n  python: 3.12\n" };
        var r = AllToolsResolver().Resolve("python", @"C:\work\legacy\x", Ctx(files: files));
        Assert.Equal(ResolutionSource.TackYml, r.Source);
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
