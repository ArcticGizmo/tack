using System.Diagnostics;
using System.Text.Json;
using Tack.Core.Config;
using Xunit;

namespace Tack.Shim.Tests;

// End-to-end tests that run the ACTUAL shim exe (renamed per tool, as in production) against a compiled
// resolved.json built from a real CentralConfig. They exercise the whole path: filename -> Core resolver ->
// locate -> exec, including tack.yml walk-up, env override, .cmd vs native dispatch, and passthrough.
public sealed class ShimTests : IClassFixture<ShimFixture>, IDisposable
{
    private readonly ShimFixture _fx;
    private readonly string _work;

    public ShimTests(ShimFixture fx)
    {
        _fx = fx;
        _work = Directory.CreateTempSubdirectory("tack-shim-test-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    [Fact]
    public void Dispatches_to_native_exe_and_propagates_exit_code()
    {
        string binDir = _fx.FakeNativeInstall("node");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        var r = Run(node, ["--stub-exit=7", "hello", "world"], _work, resolved);

        Assert.Equal(7, r.ExitCode);
        Assert.Contains("TOOL=node", r.Stdout);        // the real native exe named node.exe ran
        Assert.Contains("ARGS=hello|world", r.Stdout);
        Assert.Contains($"CWD={_work}", r.Stdout);
    }

    [Fact]
    public void Dispatches_via_cmd_and_propagates_exit_code()
    {
        string binDir = CmdInstall("node", "@echo off\r\necho FROMCMD ARGS=%*\r\nexit /b 3\r\n");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        var r = Run(node, ["alpha", "beta"], _work, resolved);

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("FROMCMD ARGS=alpha beta", r.Stdout);
    }

    [Fact]
    public void Cmd_target_receives_every_argument_literally()
    {
        // npm.cmd's shape: a .cmd in a folder with a space (like C:\Program Files\nodejs) forwarding %* to a
        // native exe. With plain exe quoting through cmd /c, the spaced folder broke the call outright, `&` / `>`
        // ran as cmd operators and %OS% expanded.
        string real = _fx.FakeNativeInstall("realnode");
        string binDir = CmdInstall("node", $"@\"{Path.Combine(real, "realnode.exe")}\" %*\r\n", folder: "has space");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        string[] args = ["two words", "a&b", "c>d", "%OS%", "q\"uote", @"trail\", ""];
        var r = Run(node, args, _work, resolved);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("TOOL=realnode", r.Stdout);
        Assert.Contains($"ARGS={string.Join('|', args)}", r.Stdout);
        Assert.False(File.Exists(Path.Combine(_work, "d")), "a '>' in an argument must not redirect output");
    }

    [Fact]
    public void Cmd_target_refuses_an_argument_with_a_line_break()
    {
        string binDir = CmdInstall("node", "@echo off\r\necho RAN\r\n");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        var r = Run(node, ["a\nb"], _work, resolved);

        Assert.Equal(127, r.ExitCode);
        Assert.Contains("line break", r.Stderr);
        Assert.DoesNotContain("RAN", r.Stdout);
    }

    [Fact]
    public void Finds_a_command_in_an_extra_bin_dir()
    {
        // Python's layout: python.exe in the version's folder, pip.exe in Scripts\ under it.
        string home = CmdInstall("python", "@echo off\r\necho FROM=home\r\n");
        string scripts = Directory.CreateDirectory(Path.Combine(home, "Scripts")).FullName;
        File.WriteAllText(Path.Combine(scripts, "pip.cmd"), "@echo off\r\necho FROM=scripts ARGS=%*\r\nexit /b 5\r\n");
        var central = new CentralConfig
        {
            Tools = { ["python"] = new RegisteredTool { Versions = { ["3.12.10"] = new InstalledVersion
            {
                BinDir = home, ExtraBinDirs = new() { scripts }, Exposes = { "python", "pip" },
            } } } },
            Defaults = { ["python"] = "3.12.10" },
        };
        string resolved = WriteResolved(central);

        var pip = Run(_fx.ShimFor("pip"), ["-V"], _work, resolved);
        Assert.Equal(5, pip.ExitCode);
        Assert.Contains("FROM=scripts ARGS=-V", pip.Stdout);
        Assert.Contains("FROM=home", Run(_fx.ShimFor("python"), [], _work, resolved).Stdout);
    }

    [Fact]
    public void The_bin_dir_beats_an_extra_bin_dir()
    {
        string home = CmdInstall("pip", "@echo off\r\necho FROM=home\r\n");
        string extra = CmdInstall("pip", "@echo off\r\necho FROM=extra\r\n");
        var central = new CentralConfig
        {
            Tools = { ["python"] = new RegisteredTool { Versions = { ["3.12.10"] = new InstalledVersion
            {
                BinDir = home, ExtraBinDirs = new() { extra }, Exposes = { "python", "pip" },
            } } } },
            Defaults = { ["python"] = "3.12.10" },
        };

        Assert.Contains("FROM=home", Run(_fx.ShimFor("pip"), [], _work, WriteResolved(central)).Stdout);
    }

    [Fact]
    public void A_command_in_no_bin_dir_names_every_folder_it_searched()
    {
        string home = CmdInstall("python", "@echo off\r\n");
        string scripts = Directory.CreateDirectory(Path.Combine(home, "Scripts")).FullName;
        var central = new CentralConfig
        {
            Tools = { ["python"] = new RegisteredTool { Versions = { ["3.12.10"] = new InstalledVersion
            {
                BinDir = home, ExtraBinDirs = new() { scripts }, Exposes = { "python", "pip" },
            } } } },
            Defaults = { ["python"] = "3.12.10" },
        };

        var r = Run(_fx.ShimFor("pip"), [], _work, WriteResolved(central));
        Assert.Equal(127, r.ExitCode);
        Assert.Contains($"found in {home} or {scripts}", r.Stderr);
    }

    [Fact]
    public void Nearest_tack_yml_selects_the_version_end_to_end()
    {
        // Two versions with distinguishable .cmd targets; a tack.yml pins v2 in a subtree.
        string vDefault = CmdInstall("node", "@echo off\r\necho WHICH=default\r\n");
        string vPinned = CmdInstall("node", "@echo off\r\necho WHICH=pinned\r\n");
        string resolved = WriteResolved(NodeConfig(("1.0.0", vDefault), ("2.0.0", vPinned), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        string proj = Directory.CreateDirectory(Path.Combine(_work, "proj")).FullName;
        File.WriteAllText(Path.Combine(proj, "tack.yml"), "tools:\n  node: 2.0.0\n");
        string deep = Directory.CreateDirectory(Path.Combine(proj, "src", "deep")).FullName;

        Assert.Contains("WHICH=pinned", Run(node, [], deep, resolved).Stdout);   // walk-up finds proj/tack.yml
        Assert.Contains("WHICH=default", Run(node, [], _work, resolved).Stdout);  // outside -> default
    }

    [Fact]
    public void Env_override_selects_the_version_end_to_end()
    {
        string vDefault = CmdInstall("node", "@echo off\r\necho WHICH=default\r\n");
        string vEnv = CmdInstall("node", "@echo off\r\necho WHICH=env\r\n");
        string resolved = WriteResolved(NodeConfig(("1.0.0", vDefault), ("2.0.0", vEnv), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "2.0.0" };
        Assert.Contains("WHICH=env", Run(node, [], _work, resolved, env: env).Stdout);
    }

    [Fact]
    public void Version_env_is_set_expanded_and_unset_for_the_child()
    {
        string binDir = CmdInstall("node",
            "@echo off\r\necho CFG=[%TACK_TEST_CFG%]\r\necho GONE=[%TACK_TEST_GONE%]\r\necho KEPT=[%TACK_TEST_KEPT%]\r\n");
        var central = NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0");
        central.Tools["node"].Versions["1.0.0"].Env = new()
        {
            ["TACK_TEST_CFG"] = @"%TACK_TEST_BASE%\cfg",
            ["TACK_TEST_GONE"] = "",
        };
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        var env = new Dictionary<string, string>
        {
            ["TACK_TEST_BASE"] = @"C:\base",
            ["TACK_TEST_GONE"] = "was-here",
            ["TACK_TEST_KEPT"] = "untouched",
        };
        var r = Run(node, [], _work, resolved, env: env);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains(@"CFG=[C:\base\cfg]", r.Stdout);
        Assert.Contains("GONE=[]", r.Stdout); // a batch file expands an undefined variable to nothing
        Assert.Contains("KEPT=[untouched]", r.Stdout);
    }

    [Fact]
    public void Version_env_only_applies_to_the_version_that_sets_it()
    {
        string binDir = CmdInstall("node", "@echo off\r\necho CFG=[%TACK_TEST_CFG%]\r\n");
        var central = NodeConfig(("plain", binDir), ("work", binDir), defaultVersion: "plain");
        central.Tools["node"].Versions["work"].Env = new() { ["TACK_TEST_CFG"] = "work-cfg" };
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        Assert.Contains("CFG=[]", Run(node, [], _work, resolved).Stdout);
        var pick = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "work" };
        Assert.Contains("CFG=[work-cfg]", Run(node, [], _work, resolved, env: pick).Stdout);
    }

    [Fact]
    public void Log_records_env_names_but_never_values()
    {
        string binDir = _fx.FakeNativeInstall("node");
        var central = NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0");
        central.Tools["node"].Versions["1.0.0"].Env = new() { ["TACK_TEST_SECRET"] = "hunter2", ["TACK_TEST_GONE"] = "" };
        central.Settings.Log = true;
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        Assert.Equal(0, Run(node, ["--stub-exit=0"], _work, resolved).ExitCode);

        string log = File.ReadAllText(Path.Combine(_work, "logs", "shim.log"));
        Assert.Contains("TACK_TEST_SECRET", log);
        Assert.Contains("TACK_TEST_GONE (unset)", log);
        Assert.DoesNotContain("hunter2", log);
    }

    [Fact]
    public void Forwards_stdin_to_child()
    {
        string binDir = _fx.FakeNativeInstall("node");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        var r = Run(node, [], _work, resolved, stdin: "piped-line\n");
        Assert.Contains("STDIN=piped-line", r.Stdout);
    }

    [Fact]
    public void Passthrough_execs_the_next_matching_binary_on_path()
    {
        // node is registered but has no default and no rule for this dir -> Passthrough. A node.cmd on PATH
        // (in a dir that is not the shims dir) should be found and run.
        string binDir = CmdInstall("node", "@echo off\r\necho WHICH=registered\r\n");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: null));
        string node = _fx.ShimFor("node");

        string pathDir = Directory.CreateDirectory(Path.Combine(_work, "onpath")).FullName;
        File.WriteAllText(Path.Combine(pathDir, "node.cmd"), "@echo off\r\necho WHICH=passthrough\r\n");
        var env = new Dictionary<string, string> { ["PATH"] = pathDir };

        Assert.Contains("WHICH=passthrough", Run(node, [], _work, resolved, env: env).Stdout);
    }

    [Fact]
    public void None_zone_passes_through_to_path_even_in_error_mode()
    {
        // A default would pick the registered node, but a none zone over _work switches tack off there - and
        // noResolution=error must not turn that deliberate opt-out into a failure.
        string binDir = CmdInstall("node", "@echo off\r\necho WHICH=registered\r\n");
        var central = NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0");
        central.Zones.Add(new Zone { Path = _work, Tool = "node", Version = "none" });
        central.Settings.NoResolution = "error";
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        string pathDir = Directory.CreateDirectory(Path.Combine(_work, "onpath")).FullName;
        File.WriteAllText(Path.Combine(pathDir, "node.cmd"), "@echo off\r\necho WHICH=passthrough\r\n");
        var env = new Dictionary<string, string> { ["PATH"] = pathDir };

        var r = Run(node, [], _work, resolved, env: env);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("WHICH=passthrough", r.Stdout);
    }

    [Fact]
    public void No_config_for_this_account_passes_through()
    {
        // SYSTEM, a service or another user: tack must be invisible, not an error.
        string node = _fx.ShimFor("node");
        string pathDir = Directory.CreateDirectory(Path.Combine(_work, "onpath")).FullName;
        File.WriteAllText(Path.Combine(pathDir, "node.cmd"), "@echo off\r\necho WHICH=passthrough\r\nexit /b 4\r\n");
        var env = new Dictionary<string, string> { ["PATH"] = pathDir };

        var r = Run(node, [], _work, MissingResolved(), env: env);

        Assert.Equal(4, r.ExitCode);
        Assert.Contains("WHICH=passthrough", r.Stdout);
    }

    [Fact]
    public void No_config_and_nothing_on_path_says_why()
    {
        string node = _fx.ShimFor("node");
        string empty = Directory.CreateDirectory(Path.Combine(_work, "empty")).FullName;
        var env = new Dictionary<string, string> { ["PATH"] = empty };

        var r = Run(node, [], _work, MissingResolved(), env: env);

        Assert.Equal(127, r.ExitCode);
        Assert.Contains("isn't set up for this account", r.Stderr);
    }

    [Fact]
    public void Config_beside_or_above_the_shims_folder_is_ignored()
    {
        // Every account runs the same shims, so a resolved.json next to them (or in the folder above, where the
        // per-user install used to keep it) would be someone else's config. Only the caller's own counts.
        string binDir = CmdInstall("node", "@echo off\r\necho WHICH=registered\r\n");
        string foreign = JsonSerializer.Serialize(
            ConfigCompiler.Compile(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0")), TackJson.Default.ResolvedConfig);
        string root = Path.Combine(_work, "Tack");
        string node = _fx.ShimIn(Path.Combine(root, "shims"), "node");
        File.WriteAllText(Path.Combine(root, "shims", "resolved.json"), foreign);
        File.WriteAllText(Path.Combine(root, "resolved.json"), foreign);

        string pathDir = Directory.CreateDirectory(Path.Combine(_work, "onpath")).FullName;
        File.WriteAllText(Path.Combine(pathDir, "node.cmd"), "@echo off\r\necho WHICH=passthrough\r\n");
        var env = new Dictionary<string, string> { ["PATH"] = pathDir };

        var r = Run(node, [], _work, MissingResolved(), env: env);

        Assert.Contains("WHICH=passthrough", r.Stdout);
        Assert.DoesNotContain("WHICH=registered", r.Stdout);
    }

    [Fact]
    public void Disabled_passes_straight_through_even_in_error_mode_and_logs_nothing()
    {
        // A default would resolve node, but `tack disable` means tack is off for this account: no resolution, no
        // noResolution=error, no invocation log.
        string binDir = CmdInstall("node", "@echo off\r\necho WHICH=registered\r\n");
        var central = NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0");
        central.Settings.Disabled = true;
        central.Settings.NoResolution = "error";
        central.Settings.Log = true;
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        string pathDir = Directory.CreateDirectory(Path.Combine(_work, "onpath")).FullName;
        File.WriteAllText(Path.Combine(pathDir, "node.cmd"), "@echo off\r\necho WHICH=passthrough\r\n");
        var env = new Dictionary<string, string> { ["PATH"] = pathDir };

        var r = Run(node, [], _work, resolved, env: env);

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("WHICH=passthrough", r.Stdout);
        Assert.False(Directory.Exists(Path.Combine(_work, "logs")));
    }

    [Fact]
    public void Corrupt_config_fails_loudly()
    {
        // Unlike a missing config, a broken one is the caller's own, and passing through would hide it.
        string resolved = Path.Combine(_work, "resolved.json");
        File.WriteAllText(resolved, "{ not json");
        string node = _fx.ShimFor("node");

        var r = Run(node, [], _work, resolved);

        Assert.Equal(127, r.ExitCode);
        Assert.Contains("could not read", r.Stderr);
    }

    [Fact]
    public void Version_not_installed_fails_loudly()
    {
        string binDir = CmdInstall("node", "@echo off\r\n");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "99.0.0" };
        var r = Run(node, [], _work, resolved, env: env);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("not registered", r.Stderr);
    }

    [Fact]
    public void Log_on_records_the_call_and_its_caller_chain()
    {
        string binDir = _fx.FakeNativeInstall("node");
        var central = NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0");
        central.Settings.Log = true;
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        var r = Run(node, ["--stub-exit=0", "two words"], _work, resolved);
        Assert.Equal(0, r.ExitCode);

        string log = File.ReadAllText(Path.Combine(_work, "logs", "shim.log"));
        Assert.Contains("node 1.0.0", log);
        Assert.Contains("\"two words\"", log);
        Assert.Contains($"cwd     {_work}", log);
        Assert.Contains($"runs    {Path.Combine(binDir, "node.exe")}", log);
        // The shim's parent is this test process, so the chain starts with us.
        Assert.Contains($"caller  [{Environment.ProcessId}] {Environment.ProcessPath}", log);
    }

    [Fact]
    public void Log_on_records_a_failure_too()
    {
        string binDir = CmdInstall("node", "@echo off\r\n");
        var central = NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0");
        central.Settings.Log = true;
        string resolved = WriteResolved(central);
        string node = _fx.ShimFor("node");

        var env = new Dictionary<string, string> { ["TACK_NODE_VERSION"] = "99.0.0" };
        Assert.NotEqual(0, Run(node, [], _work, resolved, env: env).ExitCode);

        string log = File.ReadAllText(Path.Combine(_work, "logs", "shim.log"));
        Assert.Contains("error   ", log);
        Assert.DoesNotContain("runs    ", log);
    }

    [Fact]
    public void Log_off_writes_nothing()
    {
        string binDir = _fx.FakeNativeInstall("node");
        string resolved = WriteResolved(NodeConfig(("1.0.0", binDir), defaultVersion: "1.0.0"));
        string node = _fx.ShimFor("node");

        Assert.Equal(0, Run(node, [], _work, resolved).ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_work, "logs")));
    }

    // ---- config helpers ----

    private static CentralConfig NodeConfig(params (string version, string binDir)[] versions)
        => NodeConfig(versions, defaultVersion: null);

    private static CentralConfig NodeConfig((string version, string binDir)[] versions, string? defaultVersion)
    {
        var tool = new RegisteredTool();
        foreach (var (v, dir) in versions)
            tool.Versions[v] = new InstalledVersion { BinDir = dir, Exposes = { "node" } };

        var c = new CentralConfig { Tools = { ["node"] = tool } };
        if (defaultVersion is not null) c.Defaults["node"] = defaultVersion;
        return c;
    }

    // Overload sugar so call sites read naturally with a trailing defaultVersion.
    private static CentralConfig NodeConfig((string, string) v1, string? defaultVersion)
        => NodeConfig(new[] { v1 }, defaultVersion);
    private static CentralConfig NodeConfig((string, string) v1, (string, string) v2, string? defaultVersion)
        => NodeConfig(new[] { v1, v2 }, defaultVersion);

    private string WriteResolved(CentralConfig central)
    {
        var resolved = ConfigCompiler.Compile(central);
        string path = Path.Combine(_work, $"resolved-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(resolved, TackJson.Default.ResolvedConfig));
        return path;
    }

    // A TACK_RESOLVED that names no file: the shim then has no config, and never falls back to the real
    // %LOCALAPPDATA% one, so these tests can't pick up the machine's own tack setup.
    private string MissingResolved() => Path.Combine(_work, "no-such-resolved.json");

    private string CmdInstall(string exposedName, string cmdBody, string? folder = null)
    {
        string dir = Directory.CreateTempSubdirectory("tack-bin-").FullName;
        if (folder is not null) dir = Directory.CreateDirectory(Path.Combine(dir, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, exposedName + ".cmd"), cmdBody);
        return dir;
    }

    // ---- process runner ----

    private static RunResult Run(string exe, string[] args, string cwd, string resolvedJson,
        Dictionary<string, string>? env = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TACK_RESOLVED"] = resolvedJson;
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        Assert.True(p.WaitForExit(15_000), "shim did not exit within 15s");
        p.WaitForExit();
        return new RunResult(p.ExitCode, outTask.Result, errTask.Result);
    }

    private readonly record struct RunResult(int ExitCode, string Stdout, string Stderr);
}

// Lays out a private shims dir with the shim + its Tack.Core dependency, hands out per-tool copies of the
// shim, and can assemble a "fake install" dir whose <tool>.exe is the stub tool (a real native exe).
public sealed class ShimFixture : IDisposable
{
    private readonly string _bin;
    private readonly string _shimDir;

    public ShimFixture()
    {
        _bin = AppContext.BaseDirectory;
        _shimDir = Directory.CreateTempSubdirectory("tack-shim-fixture-").FullName;
        CopyShimHost(_shimDir);

        if (!File.Exists(Path.Combine(_shimDir, "tack-shim.exe")))
            throw new FileNotFoundException("tack-shim.exe not found in test output; build the solution first");
        if (!File.Exists(Path.Combine(_bin, "tack-stub.exe")))
            throw new FileNotFoundException("tack-stub.exe not found; build the solution first");
    }

    // A copy of the shim host named <tool>.exe, in the shared shim dir (which has the dlls).
    public string ShimFor(string tool)
    {
        string dest = Path.Combine(_shimDir, tool + ".exe");
        if (!File.Exists(dest)) File.Copy(Path.Combine(_shimDir, "tack-shim.exe"), dest);
        return dest;
    }

    // The same, in a shims dir of the caller's choosing, for tests that care where the shim lives.
    public string ShimIn(string shimsDir, string tool)
    {
        Directory.CreateDirectory(shimsDir);
        CopyShimHost(shimsDir);
        string dest = Path.Combine(shimsDir, tool + ".exe");
        File.Copy(Path.Combine(shimsDir, "tack-shim.exe"), dest, overwrite: true);
        return dest;
    }

    // The renamed shim host loads tack-shim.dll AND Tack.Core.dll from its own dir, so copy both sets.
    private void CopyShimHost(string dir)
    {
        foreach (var pattern in new[] { "tack-shim.*", "Tack.Core.*" })
            foreach (var f in Directory.GetFiles(_bin, pattern))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
    }

    // A temp binDir whose <exposedName>.exe is the stub tool (a real PE exe), so we can test native dispatch.
    // The stub apphost is renamed but still loads tack-stub.dll from the same dir.
    public string FakeNativeInstall(string exposedName)
    {
        string dir = Directory.CreateTempSubdirectory("tack-install-").FullName;
        File.Copy(Path.Combine(_bin, "tack-stub.exe"), Path.Combine(dir, exposedName + ".exe"));
        foreach (var f in Directory.GetFiles(_bin, "tack-stub.*"))
            if (!f.EndsWith("tack-stub.exe", StringComparison.OrdinalIgnoreCase))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_shimDir, recursive: true); } catch { }
    }
}
