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

    private string CmdInstall(string exposedName, string cmdBody)
    {
        string dir = Directory.CreateTempSubdirectory("tack-bin-").FullName;
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

        // The renamed shim host loads tack-shim.dll AND Tack.Core.dll from its own dir, so copy both sets.
        foreach (var pattern in new[] { "tack-shim.*", "Tack.Core.*" })
            foreach (var f in Directory.GetFiles(_bin, pattern))
                File.Copy(f, Path.Combine(_shimDir, Path.GetFileName(f)), overwrite: true);

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
