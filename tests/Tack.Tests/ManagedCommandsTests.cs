using Tack.Core.Config;
using Tack.Core.Installs;
using Xunit;

namespace Tack.Tests;

// Rescanning managed versions for commands installed after them, against real folders in a temp dir.
public sealed class ManagedCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-rescan-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Folder(params string[] files)
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        foreach (var f in files)
        {
            string path = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return dir;
    }

    private static InstalledVersion Managed(string binDir, string publisher, params string[] exposes) => new()
    {
        BinDir = binDir,
        Exposes = exposes.ToList(),
        Install = new InstallReceipt { Source = publisher, Url = "https://example.org/x.zip", Sha256 = new string('a', 64) },
    };

    private static CentralConfig With(string tool, string version, InstalledVersion installed)
    {
        var config = new CentralConfig();
        ToolRegistry.Register(config, tool, version, installed);
        return config;
    }

    [Fact]
    public void An_npm_global_becomes_a_command_and_nodes_own_batch_files_never_do()
    {
        string dir = Folder("node.exe", "npm.cmd", "npx.cmd", "install_tools.bat", "nodevars.bat", "tsc.cmd", "tsc.ps1", "tsc");
        var config = With("node", "24.21.0", Managed(dir, "nodejs.org", "node", "npm", "npx"));

        var result = Assert.Single(ManagedCommands.Rescan(config));

        Assert.Equal(new[] { "tsc" }, result.Added);
        Assert.Empty(result.Removed);
        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { "node", "npm", "npx", "tsc" }, config.Tools["node"].Versions["24.21.0"].Exposes);
    }

    [Fact]
    public void A_pip_install_in_Scripts_becomes_a_command_and_pythonw_never_does()
    {
        string dir = Folder("python.exe", "pythonw.exe", @"Scripts\pip.exe", @"Scripts\pip3.exe", @"Scripts\black.exe");
        var installed = Managed(dir, "python.org", "python", "pip", "pip3");
        installed.ExtraBinDirs = new List<string> { Path.Combine(dir, "Scripts") };
        var config = With("python", "3.13.16", installed);

        var result = Assert.Single(ManagedCommands.Rescan(config));

        Assert.Equal(new[] { "black" }, result.Added);
        Assert.Equal(new[] { "python", "pip", "pip3", "black" }, installed.Exposes);
    }

    [Fact]
    public void An_uninstalled_global_is_dropped_but_the_tools_own_name_never_is()
    {
        string dir = Folder("npm.cmd"); // node.exe gone too, somehow
        var installed = Managed(dir, "nodejs.org", "node", "npm", "tsc");
        var config = With("node", "24.21.0", installed);

        var result = Assert.Single(ManagedCommands.Rescan(config));

        Assert.Equal(new[] { "tsc" }, result.Removed);
        Assert.Equal(new[] { "node", "npm" }, installed.Exposes);
    }

    [Fact]
    public void Nothing_new_reports_nothing_and_changes_nothing()
    {
        string dir = Folder("node.exe", "npm.cmd", "nodevars.bat");
        var installed = Managed(dir, "nodejs.org", "node", "npm");
        var before = installed.Exposes;

        Assert.Empty(ManagedCommands.Rescan(With("node", "24.21.0", installed)));
        Assert.Same(before, installed.Exposes);
    }

    [Fact]
    public void A_version_added_with_tool_add_is_left_alone()
    {
        string dir = Folder("node.exe", "npm.cmd", "tsc.cmd");
        var installed = new InstalledVersion { BinDir = dir, Exposes = { "node", "npm" } };

        Assert.Empty(ManagedCommands.Rescan(With("node", "20.11.0", installed)));
        Assert.Equal(new[] { "node", "npm" }, installed.Exposes);
    }

    [Fact]
    public void A_version_from_a_source_tack_no_longer_knows_is_left_alone()
    {
        string dir = Folder("node.exe", "tsc.cmd");
        var installed = Managed(dir, "example.org", "node");

        Assert.Empty(ManagedCommands.Rescan(With("node", "1.0.0", installed)));
        Assert.Equal(new[] { "node" }, installed.Exposes);
    }

    [Fact]
    public void A_name_another_tool_owns_is_skipped_rather_than_taken_over()
    {
        // npm i -g pnpm, with pnpm registered as a tool of its own; and a global named after another tool.
        string dir = Folder("node.exe", "pnpm.cmd", "python.cmd");
        var config = With("node", "24.21.0", Managed(dir, "nodejs.org", "node"));
        ToolRegistry.Register(config, "pnpm", "9.0.0", new InstalledVersion { BinDir = Folder("pnpm.exe") });
        ToolRegistry.Register(config, "python", "3.13.16", new InstalledVersion { BinDir = Folder("python.exe") });

        var result = Assert.Single(ManagedCommands.Rescan(config));

        Assert.Empty(result.Added);
        Assert.Equal(new[] { ("pnpm", "pnpm already provides it"), ("python", "python already provides it") }, result.Skipped);
        Assert.Equal(new[] { "node" }, config.Tools["node"].Versions["24.21.0"].Exposes);
    }

    [Fact]
    public void A_name_another_version_of_the_same_tool_has_is_fine()
    {
        var config = With("node", "20.20.2", Managed(Folder("node.exe", "tsc.cmd"), "nodejs.org", "node", "tsc"));
        ToolRegistry.Register(config, "node", "24.21.0", Managed(Folder("node.exe", "tsc.cmd"), "nodejs.org", "node"));

        var result = Assert.Single(ManagedCommands.Rescan(config));

        Assert.Equal(("node", "24.21.0"), (result.Tool, result.Version));
        Assert.Equal(new[] { "tsc" }, result.Added);
    }

    [Fact]
    public void Names_that_cant_be_shims_or_the_caller_refuses_are_skipped_with_why()
    {
        string dir = Folder("node.exe", "con.cmd", "where.cmd", "two words.cmd");
        var installed = Managed(dir, "nodejs.org", "node");

        var result = Assert.Single(ManagedCommands.Rescan(With("node", "24.21.0", installed),
            name => name == "where" ? "a Windows command" : null));

        Assert.Empty(result.Added);
        Assert.Equal(new[] { "con", "two words", "where" }, result.Skipped.Select(s => s.Name).Order());
        Assert.Contains(("where", "a Windows command"), result.Skipped);
        Assert.Equal(new[] { "node" }, installed.Exposes);
    }

    [Fact]
    public void Every_source_names_its_non_commands()
    {
        Assert.Equal(new[] { "install_tools", "nodevars" }, ToolSources.Find("node")!.NotCommands);
        Assert.Equal(new[] { "pythonw" }, ToolSources.Find("python")!.NotCommands);
        Assert.Same(ToolSources.Find("node"), ToolSources.Find("NODE", "NodeJS.org"));
        Assert.Null(ToolSources.Find("node", "python.org"));
    }
}
