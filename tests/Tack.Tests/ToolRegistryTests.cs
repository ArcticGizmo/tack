using Tack.Core.Config;
using Xunit;

namespace Tack.Tests;

public sealed class ToolRegistryTests
{
    private static CentralConfig TwoNodes() => new()
    {
        Tools =
        {
            ["node"] = new RegisteredTool
            {
                Versions =
                {
                    ["18.19.0"] = new InstalledVersion { BinDir = @"C:\n18", Exposes = { "node", "npm" } },
                    ["20.11.0"] = new InstalledVersion { BinDir = @"C:\n20", Exposes = { "node", "npm" } },
                },
            },
        },
        Defaults = { ["node"] = "20.11.0" },
    };

    [Fact]
    public void Entries_lists_every_tool_at_version_sorted()
    {
        Assert.Equal(new[] { "node@18.19.0", "node@20.11.0" }, ToolRegistry.Entries(TwoNodes()));
    }

    [Fact]
    public void Exists_and_VersionsOf_are_case_insensitive()
    {
        var c = TwoNodes();
        Assert.True(ToolRegistry.Exists(c, "NODE", "20.11.0"));
        Assert.Equal(new[] { "18.19.0", "20.11.0" }, ToolRegistry.VersionsOf(c, "Node"));
        Assert.Empty(ToolRegistry.VersionsOf(c, "python"));
    }

    [Fact]
    public void Removing_one_version_keeps_the_tool_and_its_default()
    {
        var c = TwoNodes();
        var r = ToolRegistry.Remove(c, new[] { ("node", "18.19.0") });

        Assert.Equal(new[] { "node@18.19.0" }, r.Removed);
        Assert.Empty(r.ToolsDropped);
        Assert.Empty(r.DefaultsRepointed);
        Assert.True(ToolRegistry.Exists(c, "node", "20.11.0"));
        Assert.Equal("20.11.0", c.Defaults["node"]);
    }

    [Fact]
    public void Removing_the_last_version_drops_the_tool_and_its_default()
    {
        var c = TwoNodes();
        ToolRegistry.Remove(c, new[] { ("node", "18.19.0") });
        var r = ToolRegistry.Remove(c, new[] { ("node", "20.11.0") });

        Assert.Contains("node", r.ToolsDropped);
        Assert.False(c.Tools.ContainsKey("node"));
        Assert.False(c.Defaults.ContainsKey("node"));
    }

    [Fact]
    public void Removing_the_default_version_repoints_to_the_highest_remaining()
    {
        var c = TwoNodes(); // default is 20.11.0
        var r = ToolRegistry.Remove(c, new[] { ("node", "20.11.0") });

        Assert.Contains("node: 20.11.0 -> 18.19.0", r.DefaultsRepointed);
        Assert.Equal("18.19.0", c.Defaults["node"]);
    }

    [Fact]
    public void Removal_is_case_insensitive_and_reports_canonical_names()
    {
        var c = TwoNodes();
        var r = ToolRegistry.Remove(c, new[] { ("NODE", "20.11.0") });
        Assert.Equal(new[] { "node@20.11.0" }, r.Removed); // canonical keys, not the caller's casing
    }

    [Fact]
    public void Unknown_targets_are_ignored()
    {
        var c = TwoNodes();
        var r = ToolRegistry.Remove(c, new[] { ("python", "3.12"), ("node", "99.0.0") });
        Assert.Empty(r.Removed);
        Assert.Equal(2, ToolRegistry.VersionsOf(c, "node").Count);
    }

    [Fact]
    public void A_zone_pointing_at_a_removed_version_is_reported()
    {
        var c = TwoNodes();
        c.Zones.Add(new Zone { Path = @"C:\work", Tool = "node", Version = "18.19.0" });

        var r = ToolRegistry.Remove(c, new[] { ("node", "18.19.0") });

        Assert.Contains(r.OrphanedZones, z => z.Contains(@"C:\work") && z.Contains("node@18.19.0"));
        Assert.Single(c.Zones); // zones are warned about, not deleted
    }

    [Fact]
    public void Removing_the_default_repoints_by_version_number_not_text()
    {
        // As text, "9.11.2" > "10.0.0" > "10.2.0"; as versions, 10.2.0 is the highest.
        var c = new CentralConfig { Defaults = { ["node"] = "20.0.0" } };
        foreach (var v in new[] { "9.11.2", "10.0.0", "10.2.0", "20.0.0" })
            ToolRegistry.Register(c, "node", v, new InstalledVersion { BinDir = $@"C:\n{v}" });

        ToolRegistry.Remove(c, new[] { ("node", "20.0.0") });

        Assert.Equal("10.2.0", c.Defaults["node"]);
        Assert.Equal(new[] { "9.11.2", "10.0.0", "10.2.0" }, ToolRegistry.VersionsOf(c, "node"));
    }

    [Fact]
    public void Register_makes_the_first_version_the_default_and_leaves_it_after()
    {
        var c = new CentralConfig();

        Assert.True(ToolRegistry.Register(c, "node", "20.11.0", new InstalledVersion { BinDir = @"C:\n20" }));
        Assert.False(ToolRegistry.Register(c, "node", "18.19.0", new InstalledVersion { BinDir = @"C:\n18" }));

        Assert.Equal("20.11.0", c.Defaults["node"]);
        Assert.Equal(new[] { "18.19.0", "20.11.0" }, ToolRegistry.VersionsOf(c, "node"));
    }

    [Fact]
    public void Register_always_exposes_the_tools_own_name_first()
    {
        var c = new CentralConfig();
        var iv = new InstalledVersion { BinDir = @"C:\n20", Exposes = { "npm", "npx" } };

        ToolRegistry.Register(c, "node", "20.11.0", iv);

        Assert.Equal(new[] { "node", "npm", "npx" }, c.Tools["node"].Versions["20.11.0"].Exposes);
    }

    [Fact]
    public void Register_keeps_the_tools_own_name_where_it_was_given()
    {
        var c = new CentralConfig();
        ToolRegistry.Register(c, "node", "20.11.0", new InstalledVersion { BinDir = @"C:\n20", Exposes = { "npm", "NODE" } });
        Assert.Equal(new[] { "npm", "NODE" }, c.Tools["node"].Versions["20.11.0"].Exposes);
    }

    [Fact]
    public void Re_registering_a_version_replaces_all_of_it()
    {
        var c = TwoNodes();
        c.Tools["node"].Versions["20.11.0"].Env = new() { ["A"] = "1" };

        ToolRegistry.Register(c, "node", "20.11.0", new InstalledVersion { BinDir = @"C:\elsewhere" });

        var v = c.Tools["node"].Versions["20.11.0"];
        Assert.Equal(@"C:\elsewhere", v.BinDir);
        Assert.Null(v.Env);
        Assert.Equal("20.11.0", c.Defaults["node"]);
    }

    [Fact]
    public void A_none_zone_is_never_reported_as_orphaned()
    {
        var c = TwoNodes();
        c.Zones.Add(new Zone { Path = @"C:\legacy", Tool = "node", Version = "none" });

        var r = ToolRegistry.Remove(c, new[] { ("node", "18.19.0") });

        Assert.Empty(r.OrphanedZones);
    }
}
