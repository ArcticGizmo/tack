using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Platform;
using Xunit;

namespace Tack.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tack-cfg-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Missing_file_loads_as_empty_config()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        Assert.Empty(store.Load().Tools);
    }

    [Fact]
    public void Round_trips_a_config()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"));
        var c = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["20.11.0"] = new InstalledVersion { BinDir = @"C:\n20", Exposes = { "node", "npm" } } } } },
            Defaults = { ["node"] = "20.11.0" },
            Zones = { new Zone { Path = @"C:\work", Tool = "node", Version = "20.11.0", Enforce = true } },
        };
        store.Save(c);

        var loaded = store.Load();
        Assert.Equal(@"C:\n20", loaded.Tools["node"].Versions["20.11.0"].BinDir);
        Assert.Equal("20.11.0", loaded.Defaults["node"]);
        var z = Assert.Single(loaded.Zones);
        Assert.Equal(@"C:\work", z.Path);
        Assert.True(z.Enforce);
        Assert.Contains("npm", loaded.Tools["node"].Versions["20.11.0"].Exposes);
        Assert.DoesNotContain("bindings", File.ReadAllText(store.Path)); // a clean config never writes the legacy key
    }

    [Fact]
    public void Loading_a_pre_zones_config_migrates_its_bindings()
    {
        string path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """
            {
              "bindings": [
                { "glob": "C:/work/**", "tools": { "node": "18" } },
                { "glob": "C:/work/*/api/**", "tools": { "node": "20" } }
              ]
            }
            """);
        var store = new ConfigStore(path);

        var loaded = store.Load();
        Assert.Equal("C:/work", Assert.Single(loaded.Zones).Path);
        Assert.Equal(new[] { "C:/work/*/api/**" }, ZoneRegistry.Unmigrated(loaded));

        store.Save(loaded); // the unmigratable binding survives a save so doctor can keep naming it
        Assert.Single(store.Load().Bindings!);
    }
}

public sealed class ToolProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tack-probe-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Detects_exec_extensions_only()
    {
        File.WriteAllText(Path.Combine(_dir, "node.exe"), "");
        File.WriteAllText(Path.Combine(_dir, "npm.cmd"), "");
        File.WriteAllText(Path.Combine(_dir, "corepack.bat"), "");
        File.WriteAllText(Path.Combine(_dir, "README.txt"), "");

        var exposes = ToolProbe.DetectExposes(_dir);
        Assert.Equal(new[] { "corepack", "node", "npm" }, exposes); // sorted, no README
    }
}

public sealed class ShimNameTests
{
    [Theory]
    [InlineData("node")]
    [InlineData("npm")]
    [InlineData("python3.12")]
    [InlineData("g++")]
    [InlineData("pip-3_12")]
    [InlineData("7z")]
    public void Plain_command_names_are_valid(string name) => Assert.Null(ShimName.Problem(name));

    [Theory]
    [InlineData("")]
    [InlineData(@"..\evil")]
    [InlineData("../evil")]
    [InlineData(@"C:\x")]
    [InlineData("two words")]
    [InlineData("a\"b")]
    [InlineData("-flag")]
    [InlineData(".hidden")]
    [InlineData("node\n")]        // $ would have let a trailing newline through
    [InlineData("wild*")]
    [InlineData("nöde")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("lpt1")]
    [InlineData("tack")]
    [InlineData("tack-shim")]
    public void Anything_else_is_refused(string name) => Assert.NotNull(ShimName.Problem(name));

    [Fact]
    public void Exposed_skips_invalid_names_and_Invalid_names_them()
    {
        var config = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { Exposes = { "node", @"..\evil", "npm" } } } } },
        };

        Assert.Equal(new[] { "node", "npm" }, ShimName.Exposed(config));
        Assert.Contains(@"node@1: '..\evil'", Assert.Single(ShimName.Invalid(config)));
    }
}

public sealed class ReshimmerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-reshim-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private CentralConfig NodeExposing(params string[] names)
    {
        var v = new InstalledVersion { BinDir = _root };
        v.Exposes.AddRange(names);
        return new CentralConfig { Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = v } } } };
    }

    [Fact]
    public void Compile_writes_resolved_json_and_lists_the_shims_the_config_needs()
    {
        string resolved = Path.Combine(_root, "resolved.json");

        var result = Reshimmer.Compile(NodeExposing("node", "npm", "npx"), resolved);

        Assert.True(File.Exists(resolved));
        Assert.Equal(new[] { "node", "npm", "npx" }, result.ShimNames);
        Assert.Single(Directory.GetFileSystemEntries(_root)); // nothing but resolved.json: no shims dir touched
    }

    [Fact]
    public void Compile_leaves_out_and_reports_invalid_names()
    {
        var result = Reshimmer.Compile(NodeExposing("node", "a b"), Path.Combine(_root, "resolved.json"));

        Assert.Equal(new[] { "node" }, result.ShimNames);
        Assert.Contains("'a b'", Assert.Single(result.InvalidNames));
    }

    [Fact]
    public void Resolved_json_can_be_rewritten_while_a_shim_is_reading_it()
    {
        string resolved = Path.Combine(_root, "resolved.json");
        Reshimmer.Compile(NodeExposing("node"), resolved);

        // Opened exactly as the shim opens it.
        using (new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Reshimmer.Compile(NodeExposing("node", "npm"), resolved);

        Assert.Contains("npm", File.ReadAllText(resolved));
        Assert.Empty(Directory.GetFiles(_root, "resolved.json.*.tmp"));
    }
}

public sealed class ShimStamperTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-stamp-").FullName;
    private string Shims => Path.Combine(_root, "shims");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private ShimPayload Payload(string content = "SHIM", params string[] support)
    {
        string exe = Path.Combine(_root, "tack-shim.exe");
        File.WriteAllText(exe, content); // stand-in binary; stamping just copies bytes
        var files = support.Select(s =>
        {
            string p = Path.Combine(_root, s);
            File.WriteAllText(p, s);
            return p;
        }).ToList();
        return new ShimPayload { ShimExe = exe, SupportFiles = files };
    }

    [Fact]
    public void Stamps_a_shim_per_name()
    {
        var result = ShimStamper.Stamp(new[] { "node", "npm", "npx" }, Shims, Payload());

        Assert.Equal(3, result.Written);
        foreach (var n in new[] { "node", "npm", "npx" })
            Assert.True(File.Exists(Path.Combine(Shims, n + ".exe")));
    }

    [Fact]
    public void Pending_is_only_missing_names_unless_asked_to_check_the_build()
    {
        // Every config change runs Pending without the check, so it doesn't hash every shim on every change.
        var payload = Payload();
        ShimStamper.Stamp(new[] { "node" }, Shims, payload);
        File.WriteAllText(payload.ShimExe, "SHIM v2");

        Assert.Equal(new[] { "npm" }, ShimStamper.Pending(new[] { "node", "npm" }, Shims, payload, checkPayload: false).Names);
        Assert.Equal(new[] { "node", "npm" }, ShimStamper.Pending(new[] { "node", "npm" }, Shims, payload, checkPayload: true).Names);
    }

    [Fact]
    public void Pending_includes_missing_support_files()
    {
        var payload = Payload("SHIM", "tack-shim.dll");
        ShimStamper.Stamp(new[] { "node" }, Shims, payload);
        File.Delete(Path.Combine(Shims, "tack-shim.dll"));

        var plan = ShimStamper.Pending(new[] { "node" }, Shims, payload, checkPayload: false);

        Assert.Empty(plan.Names);
        Assert.True(plan.SupportFiles);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void Leaves_identical_shims_untouched()
    {
        var payload = Payload();
        ShimStamper.Stamp(new[] { "node", "npm" }, Shims, payload);

        var again = ShimStamper.Stamp(new[] { "node", "npm", "npx" }, Shims, payload);

        Assert.Equal(1, again.Written);   // only the new npx
        Assert.Equal(2, again.Unchanged);
    }

    [Fact]
    public void A_new_shim_build_replaces_the_old_copies()
    {
        var payload = Payload();
        ShimStamper.Stamp(new[] { "node" }, Shims, payload);

        File.WriteAllText(payload.ShimExe, "SHIM v2"); // an update / rebuild
        var again = ShimStamper.Stamp(new[] { "node" }, Shims, payload);

        Assert.Equal(1, again.Written);
        Assert.Equal("SHIM v2", File.ReadAllText(Path.Combine(Shims, "node.exe")));
    }

    [Fact]
    public void Different_bytes_are_restamped_even_with_the_same_size_and_timestamp()
    {
        // Size + timestamp used to count as "unchanged"; both are easy to match, so the content is compared.
        var payload = Payload("SHIM A");
        ShimStamper.Stamp(new[] { "node" }, Shims, payload);
        string node = Path.Combine(Shims, "node.exe");
        File.WriteAllText(payload.ShimExe, "SHIM B");
        File.SetLastWriteTimeUtc(payload.ShimExe, File.GetLastWriteTimeUtc(node));

        Assert.Equal(new[] { "node" }, ShimStamper.Pending(new[] { "node" }, Shims, payload, checkPayload: true).Names);
        Assert.Equal(1, ShimStamper.Stamp(new[] { "node" }, Shims, payload).Written);
        Assert.Equal("SHIM B", File.ReadAllText(node));
    }

    [Fact]
    public void Stamp_refuses_invalid_names_and_writes_nothing_for_them()
    {
        var result = ShimStamper.Stamp(new[] { "node", @"..\escaped", "con" }, Shims, Payload());

        Assert.Equal(1, result.Written);
        Assert.Equal(new[] { @"..\escaped", "con" }, result.Rejected);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.exe")));
    }

    [Fact]
    public void A_locked_shim_is_moved_aside_and_replaced_instead_of_failing()
    {
        var payload = Payload();
        ShimStamper.Stamp(new[] { "node" }, Shims, payload);
        File.WriteAllText(payload.ShimExe, "SHIM v2");

        string node = Path.Combine(Shims, "node.exe");
        StampResult again;
        // Held open the way a running image is: no write sharing, but renamable.
        using (new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            again = ShimStamper.Stamp(new[] { "node" }, Shims, payload);

        Assert.Empty(again.Locked);
        Assert.Equal("SHIM v2", File.ReadAllText(node));
        Assert.Single(Directory.GetFiles(Shims, "*.tack-old")); // the old copy, parked

        ShimStamper.Stamp(new[] { "node" }, Shims, payload); // nothing holds it now
        Assert.Empty(Directory.GetFiles(Shims, "*.tack-old"));
    }

    [Fact]
    public void A_file_that_cannot_even_be_moved_is_reported_not_thrown()
    {
        var payload = Payload();
        ShimStamper.Stamp(new[] { "node" }, Shims, payload);
        File.WriteAllText(payload.ShimExe, "SHIM v2");

        string node = Path.Combine(Shims, "node.exe");
        StampResult again;
        using (new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.Read)) // no delete sharing: can't rename
            again = ShimStamper.Stamp(new[] { "node" }, Shims, payload);

        Assert.Equal(node, Assert.Single(again.Locked));
    }

    [Fact]
    public void Reports_a_missing_shim_binary()
    {
        var result = ShimStamper.Stamp(new[] { "node" }, Shims, new ShimPayload { ShimExe = Path.Combine(_root, "nope.exe") });

        Assert.True(result.PayloadMissing);
        Assert.Equal(0, result.Written);
    }

    [Fact]
    public void Prune_removes_exactly_the_given_names_and_never_support_files()
    {
        ShimStamper.Stamp(new[] { "node", "npm", "npx" }, Shims, Payload("SHIM", "tack-shim.dll"));

        var result = ShimStamper.Prune(new[] { "npm" }, Shims);

        Assert.Equal(new[] { "npm" }, result.Pruned);
        Assert.False(File.Exists(Path.Combine(Shims, "npm.exe")));
        Assert.True(File.Exists(Path.Combine(Shims, "node.exe")));
        Assert.True(File.Exists(Path.Combine(Shims, "npx.exe")));
        Assert.True(File.Exists(Path.Combine(Shims, "tack-shim.dll")));
    }

    [Fact]
    public void Prune_refuses_a_name_that_would_leave_the_shims_dir()
    {
        ShimStamper.Stamp(new[] { "node" }, Shims, Payload());
        string outside = Path.Combine(_root, "victim.exe");
        File.WriteAllText(outside, "not a shim");

        var result = ShimStamper.Prune(new[] { @"..\victim" }, Shims);

        Assert.Equal(new[] { @"..\victim" }, result.Rejected);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Stale_lists_shims_the_config_does_not_use()
    {
        ShimStamper.Stamp(new[] { "node", "npm", "yarn" }, Shims, Payload());
        File.WriteAllText(Path.Combine(Shims, "odd name.exe"), ""); // not something tack could have made

        Assert.Equal(new[] { "yarn" }, ShimStamper.Stale(new[] { "node", "npm" }, Shims));
    }
}

public sealed class FirstRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-firstrun-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakePathInstaller : IPathInstaller
    {
        public int Registered;
        public int Unregistered;
        public PathChange? Register() { Registered++; return null; }
        public PathChange? Unregister() { Unregistered++; return null; }
    }

    private ShimPayload Payload()
    {
        string exe = Path.Combine(_root, "tack-shim.exe");
        File.WriteAllText(exe, "SHIM");
        return new ShimPayload { ShimExe = exe };
    }

    private CentralConfig NodeExposing(params string[] names)
    {
        var v = new InstalledVersion { BinDir = _root };
        v.Exposes.AddRange(names);
        return new CentralConfig { Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = v } } } };
    }

    [Fact]
    public void Wires_path_then_compiles_and_stamps_the_existing_config()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var path = new FakePathInstaller();

        var result = FirstRun.Apply(path, NodeExposing("node", "npm"), shims, resolved, Payload());

        Assert.Equal(1, path.Registered);                       // PATH wired
        Assert.True(File.Exists(resolved));                     // resolved.json compiled
        Assert.Equal(2, result.Stamped.Written);                // shims stamped for the registered tool
        Assert.True(File.Exists(Path.Combine(shims, "node.exe")));
        Assert.True(File.Exists(Path.Combine(shims, "npm.exe")));
    }

    [Fact]
    public void Empty_config_still_wires_path_and_writes_resolved_json()
    {
        string resolved = Path.Combine(_root, "resolved.json");
        var path = new FakePathInstaller();

        var result = FirstRun.Apply(path, new CentralConfig(), Path.Combine(_root, "shims"), resolved, Payload());

        Assert.Equal(1, path.Registered);
        Assert.True(File.Exists(resolved));
        Assert.Equal(0, result.Stamped.Written);
    }

    [Fact]
    public void Prunes_shims_the_config_no_longer_lists()
    {
        // The shims dir is this account's own, so a shim nothing in its config needs can simply go.
        string shims = Path.Combine(_root, "shims");
        ShimStamper.Stamp(new[] { "node", "yarn" }, shims, Payload());

        var result = FirstRun.Apply(new FakePathInstaller(), NodeExposing("node"), shims,
            Path.Combine(_root, "resolved.json"), Payload());

        Assert.Equal(new[] { "yarn" }, result.Pruned.Pruned);
        Assert.True(File.Exists(Path.Combine(shims, "node.exe")));
        Assert.False(File.Exists(Path.Combine(shims, "yarn.exe")));
    }
}

public sealed class PathDoctorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-doctor-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static CentralConfig NodeAt(string binDir) => new()
    {
        Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = binDir, Exposes = { "node" } } } } },
    };

    [Fact]
    public void Flags_shims_dir_not_on_path()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        var report = PathDoctor.Run(NodeAt(_root), shims, _ => "");
        Assert.Contains(report.Checks, c => c.Title == "Shims directory is on PATH" && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Flags_a_shadowing_dir_ahead_of_the_shims()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(_root, "realnode")).FullName;
        File.WriteAllText(Path.Combine(other, "node.exe"), ""); // a real node ahead of the shims

        // machine PATH = other (ahead), user PATH = shims (behind)
        var report = PathDoctor.Run(NodeAt(_root), shims,
            t => t == EnvironmentVariableTarget.Machine ? other : shims);

        Assert.Contains(report.Checks, c => c.Title == "'node' is shadowed" && c.Status == CheckStatus.Warn);
    }

    [Fact]
    public void Says_which_path_holds_the_shims_dir()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;

        var onUser = PathDoctor.Run(NodeAt(_root), shims,
            t => t == EnvironmentVariableTarget.User ? $@"C:\first;{shims}" : "");
        Assert.Contains(onUser.Checks, c => c.Title == "Shims directory is on PATH" && c.Status == CheckStatus.Ok
            && c.Detail == "user PATH, entry 2");
    }

    [Fact]
    public void Shims_on_the_system_path_fail_even_when_also_on_the_user_path()
    {
        // Every account searches the system PATH, and the shims folder is yours to write (the 0.1.x finding).
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;

        var report = PathDoctor.Run(NodeAt(_root), shims,
            t => t == EnvironmentVariableTarget.Machine ? $@"C:\first;{shims}" : shims);

        Assert.Contains(report.Checks, c => c.Title == "Shims directory is on PATH" && c.Status == CheckStatus.Fail
            && c.Detail.StartsWith("system PATH, entry 2"));
    }

    [Fact]
    public void Flags_a_missing_bindir()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        var report = PathDoctor.Run(NodeAt(Path.Combine(_root, "ghost")), shims, _ => shims);
        Assert.Contains(report.Checks, c => c.Title.StartsWith("Missing binDir") && c.Status == CheckStatus.Fail);
    }

    [Fact]
    public void Flags_stale_shims_and_invalid_names()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        File.WriteAllText(Path.Combine(shims, "node.exe"), "");
        File.WriteAllText(Path.Combine(shims, "yarn.exe"), "");
        var config = NodeAt(_root);
        config.Tools["node"].Versions["1"].Exposes.Add("bad name");

        var report = PathDoctor.Run(config, shims, _ => shims);

        Assert.Contains(report.Checks, c => c.Title == "Stale shim 'yarn'" && c.Status == CheckStatus.Warn);
        Assert.DoesNotContain(report.Checks, c => c.Title == "Stale shim 'node'");
        Assert.Contains(report.Checks, c => c.Title == "Invalid command name" && c.Detail.Contains("'bad name'"));
    }
}
