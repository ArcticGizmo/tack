using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
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

public sealed class ReshimmerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-reshim-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private ShimPayload Payload()
    {
        string exe = Path.Combine(_root, "tack-shim.exe");
        File.WriteAllText(exe, "SHIM"); // stand-in binary; reshim just copies bytes
        return new ShimPayload { ShimExe = exe };
    }

    [Fact]
    public void Writes_resolved_json_and_stamps_a_shim_per_exposed_name()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var config = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node", "npm", "npx" } } } } },
        };

        var result = Reshimmer.Run(config, shims, resolved, Payload());

        Assert.True(File.Exists(resolved));
        Assert.Equal(3, result.ShimsWritten);
        Assert.True(File.Exists(Path.Combine(shims, "node.exe")));
        Assert.True(File.Exists(Path.Combine(shims, "npm.exe")));
        Assert.True(File.Exists(Path.Combine(shims, "npx.exe")));
    }

    [Fact]
    public void Prunes_shims_no_longer_exposed()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");

        var withNpm = new CentralConfig { Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node", "npm" } } } } } };
        Reshimmer.Run(withNpm, shims, resolved, Payload());
        Assert.True(File.Exists(Path.Combine(shims, "npm.exe")));

        var withoutNpm = new CentralConfig { Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node" } } } } } };
        var result = Reshimmer.Run(withoutNpm, shims, resolved, Payload());

        Assert.False(File.Exists(Path.Combine(shims, "npm.exe"))); // pruned
        Assert.True(File.Exists(Path.Combine(shims, "node.exe")));
        Assert.Equal(1, result.ShimsPruned);
    }

    private CentralConfig NodeExposing(params string[] names)
    {
        var v = new InstalledVersion { BinDir = _root };
        v.Exposes.AddRange(names);
        return new CentralConfig { Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = v } } } };
    }

    [Fact]
    public void Leaves_identical_shims_untouched()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var payload = Payload();
        Reshimmer.Run(NodeExposing("node", "npm"), shims, resolved, payload);

        var again = Reshimmer.Run(NodeExposing("node", "npm", "npx"), shims, resolved, payload);

        Assert.Equal(1, again.ShimsWritten);   // only the new npx
        Assert.Equal(2, again.ShimsUnchanged);
    }

    [Fact]
    public void A_new_shim_build_replaces_the_old_copies()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var payload = Payload();
        Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);

        File.WriteAllText(payload.ShimExe, "SHIM v2"); // an update / rebuild
        var again = Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);

        Assert.Equal(1, again.ShimsWritten);
        Assert.Equal("SHIM v2", File.ReadAllText(Path.Combine(shims, "node.exe")));
    }

    [Fact]
    public void A_locked_shim_is_moved_aside_and_replaced_instead_of_failing()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var payload = Payload();
        Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);
        File.WriteAllText(payload.ShimExe, "SHIM v2");

        string node = Path.Combine(shims, "node.exe");
        ReshimResult again;
        // Held open the way a running image is: no write sharing, but renamable.
        using (new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            again = Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);

        Assert.Empty(again.Locked);
        Assert.Equal("SHIM v2", File.ReadAllText(node));
        Assert.Single(Directory.GetFiles(shims, "*.tack-old")); // the old copy, parked

        Reshimmer.Run(NodeExposing("node"), shims, resolved, payload); // nothing holds it now
        Assert.Empty(Directory.GetFiles(shims, "*.tack-old"));
    }

    [Fact]
    public void A_file_that_cannot_even_be_moved_is_reported_not_thrown()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var payload = Payload();
        Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);
        File.WriteAllText(payload.ShimExe, "SHIM v2");

        string node = Path.Combine(shims, "node.exe");
        ReshimResult again;
        using (new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.Read)) // no delete sharing: can't rename
            again = Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);

        Assert.Equal(node, Assert.Single(again.Locked));
        Assert.True(File.Exists(resolved)); // the rest of the reshim still happened
    }

    [Fact]
    public void Resolved_json_can_be_rewritten_while_a_shim_is_reading_it()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var payload = Payload();
        Reshimmer.Run(NodeExposing("node"), shims, resolved, payload);

        // Opened exactly as the shim opens it.
        using (new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Reshimmer.Run(NodeExposing("node", "npm"), shims, resolved, payload);

        Assert.Contains("npm", File.ReadAllText(resolved));
        Assert.Empty(Directory.GetFiles(_root, "resolved.json.*.tmp"));
    }

    [Fact]
    public void Reports_a_missing_shim_binary_but_still_writes_resolved_json()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var config = new CentralConfig { Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node" } } } } } };

        var result = Reshimmer.Run(config, shims, resolved, new ShimPayload { ShimExe = Path.Combine(_root, "nope.exe") });

        Assert.True(result.ShimPayloadMissing);
        Assert.True(File.Exists(resolved));
        Assert.Equal(0, result.ShimsWritten);
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
        public void Register() => Registered++;
        public void Unregister() => Unregistered++;
    }

    private ShimPayload Payload()
    {
        string exe = Path.Combine(_root, "tack-shim.exe");
        File.WriteAllText(exe, "SHIM");
        return new ShimPayload { ShimExe = exe };
    }

    [Fact]
    public void Wires_path_then_regenerates_shims_from_existing_config()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var path = new FakePathInstaller();
        var config = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node", "npm" } } } } },
        };

        var result = FirstRun.Apply(path, config, shims, resolved, Payload());

        Assert.Equal(1, path.Registered);                       // PATH wired
        Assert.True(File.Exists(resolved));                     // resolved.json compiled
        Assert.Equal(2, result.ShimsWritten);                   // shims stamped for the registered tool
        Assert.True(File.Exists(Path.Combine(shims, "node.exe")));
        Assert.True(File.Exists(Path.Combine(shims, "npm.exe")));
    }

    [Fact]
    public void Empty_config_still_wires_path_and_writes_resolved_json()
    {
        string shims = Path.Combine(_root, "shims");
        string resolved = Path.Combine(_root, "resolved.json");
        var path = new FakePathInstaller();

        var result = FirstRun.Apply(path, new CentralConfig(), shims, resolved, Payload());

        Assert.Equal(1, path.Registered);
        Assert.True(File.Exists(resolved));
        Assert.Equal(0, result.ShimsWritten);
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

        var onSystem = PathDoctor.Run(NodeAt(_root), shims,
            t => t == EnvironmentVariableTarget.Machine ? $@"C:\first;{shims}" : "");
        Assert.Contains(onSystem.Checks, c => c.Title == "Shims directory is on PATH" && c.Detail == "system PATH, entry 2");

        var userOnly = PathDoctor.Run(NodeAt(_root), shims,
            t => t == EnvironmentVariableTarget.User ? shims : "");
        Assert.Contains(userOnly.Checks, c => c.Title == "Shims directory is on PATH" && c.Detail.StartsWith("user PATH only"));
    }

    [Fact]
    public void Flags_a_missing_bindir()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        var report = PathDoctor.Run(NodeAt(Path.Combine(_root, "ghost")), shims, _ => shims);
        Assert.Contains(report.Checks, c => c.Title.StartsWith("Missing binDir") && c.Status == CheckStatus.Fail);
    }
}
