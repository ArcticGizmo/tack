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
    public void A_binding_pointing_at_a_removed_version_is_reported()
    {
        var c = TwoNodes();
        c.Bindings.Add(new Binding { Glob = "C:/work/**", Tools = { ["node"] = "18.19.0" } });

        var r = ToolRegistry.Remove(c, new[] { ("node", "18.19.0") });

        Assert.Contains(r.OrphanedBindings, b => b.Contains("C:/work/**") && b.Contains("node@18.19.0"));
        Assert.Single(c.Bindings); // bindings are warned about, not deleted
    }
}
