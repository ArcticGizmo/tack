using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Tack.Shim.Tests;

// Each test builds a resolved.json, copies the real shim to <tool>.exe, and runs it in a temp working
// directory -- exercising the true Windows exec path, not a mock. The stub (tack-stub.exe) reports which
// binary ran, its cwd, and the forwarded args; .cmd targets exercise the cmd.exe dispatch branch.
public sealed class ShimTests : IClassFixture<ShimFixture>, IDisposable
{
    private readonly ShimFixture _fx;
    private readonly string _work; // a throwaway working-directory root for this test

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
    public void Dispatches_to_native_exe_target_and_propagates_exit_code()
    {
        string resolved = WriteResolved(node: new(@default: _fx.StubExe, dirs: null));
        string node = _fx.ShimFor("node");

        var r = Run(node, ["--stub-exit=7", "hello", "world"], _work, resolved);

        Assert.Equal(7, r.ExitCode);                       // child's exit code propagated verbatim
        Assert.Contains("TOOL=tack-stub", r.Stdout);       // dispatched to the configured native exe
        Assert.Contains("ARGS=hello|world", r.Stdout);     // args forwarded, order preserved, flag consumed
        Assert.Contains($"CWD={_work}", r.Stdout);         // child ran in the caller's working directory
    }

    [Fact]
    public void Dispatches_via_cmd_for_cmd_targets_and_propagates_exit_code()
    {
        // The interesting Windows edge: a .cmd is not a PE image, so this must go through cmd.exe /c.
        string cmd = Path.Combine(_work, "real-node.cmd");
        File.WriteAllText(cmd, "@echo off\r\necho FROMCMD ARGS=%*\r\nexit /b 3\r\n");

        string resolved = WriteResolved(node: new(@default: cmd, dirs: null));
        string node = _fx.ShimFor("node");

        var r = Run(node, ["alpha", "beta"], _work, resolved);

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("FROMCMD ARGS=alpha beta", r.Stdout);
    }

    [Fact]
    public void Selects_target_by_working_directory()
    {
        // node resolves to a different target under projB than it does elsewhere -- the core "per-directory
        // dispatch that reaches any process" claim, in miniature.
        string projB = Directory.CreateDirectory(Path.Combine(_work, "projB")).FullName;

        string defCmd = Path.Combine(_work, "def.cmd");
        string bCmd = Path.Combine(_work, "b.cmd");
        File.WriteAllText(defCmd, "@echo off\r\necho WHICH=default\r\n");
        File.WriteAllText(bCmd, "@echo off\r\necho WHICH=projB\r\n");

        string resolved = WriteResolved(node: new(
            @default: defCmd,
            dirs: new() { [projB] = bCmd }));
        string node = _fx.ShimFor("node");

        var inProjB = Run(node, [], projB, resolved);
        var inRoot = Run(node, [], _work, resolved);
        var inNested = Run(node, [], Directory.CreateDirectory(Path.Combine(projB, "sub", "deep")).FullName, resolved);

        Assert.Contains("WHICH=projB", inProjB.Stdout);    // exact dir match
        Assert.Contains("WHICH=default", inRoot.Stdout);   // no binding -> default
        Assert.Contains("WHICH=projB", inNested.Stdout);   // ancestor match wins for descendants
    }

    [Fact]
    public void Forwards_stdin_to_child()
    {
        string resolved = WriteResolved(node: new(@default: _fx.StubExe, dirs: null));
        string node = _fx.ShimFor("node");

        var r = Run(node, [], _work, resolved, stdin: "piped-line\n");

        Assert.Contains("STDIN=piped-line", r.Stdout);     // std handles inherited end-to-end
    }

    [Fact]
    public void Unregistered_tool_fails_nonzero()
    {
        string resolved = WriteResolved(node: new(@default: _fx.StubExe, dirs: null));
        string python = _fx.ShimFor("python"); // not in resolved.json

        var r = Run(python, [], _work, resolved);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("not registered", r.Stderr);
    }

    [Fact]
    public void Missing_target_file_fails_nonzero()
    {
        string ghost = Path.Combine(_work, "does-not-exist.exe");
        string resolved = WriteResolved(node: new(@default: ghost, dirs: null));
        string node = _fx.ShimFor("node");

        var r = Run(node, [], _work, resolved);

        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("does not exist", r.Stderr);
    }

    // ---- helpers ----

    private string WriteResolved(ToolCfg node)
    {
        var doc = new Dictionary<string, object> { ["tools"] = new Dictionary<string, ToolCfg> { ["node"] = node } };
        string path = Path.Combine(_work, $"resolved-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
        return path;
    }

    private static RunResult Run(string exe, string[] args, string cwd, string resolvedJson, string? stdin = null)
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

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        Assert.True(p.WaitForExit(15_000), "shim did not exit within 15s");
        p.WaitForExit(); // ensure async stdout/stderr are flushed
        return new RunResult(p.ExitCode, outTask.Result, errTask.Result);
    }

    private readonly record struct RunResult(int ExitCode, string Stdout, string Stderr);

    // Serializes with lowercase property names ("default", "dirs") -- the shim reads case-insensitively.
    public sealed record ToolCfg(string? @default, Dictionary<string, string>? dirs);
}

// Copies the built shim + stub into a private temp dir once, and hands out per-tool copies of the shim
// (node.exe, python.exe, ...). The renamed host still loads tack-shim.dll from the same directory, and
// Environment.ProcessPath reports the renamed name -- which is exactly how the shim knows its tool.
public sealed class ShimFixture : IDisposable
{
    private readonly string _dir;
    public string StubExe { get; }

    public ShimFixture()
    {
        string bin = AppContext.BaseDirectory; // test bin dir: has tack-shim.* and tack-stub.* copied in
        _dir = Directory.CreateTempSubdirectory("tack-shim-fixture-").FullName;

        foreach (var f in Directory.GetFiles(bin, "tack-shim.*"))
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)), overwrite: true);

        StubExe = Path.Combine(bin, "tack-stub.exe");
        if (!File.Exists(StubExe))
            throw new FileNotFoundException($"stub not found at {StubExe}; build the solution first");
        if (!File.Exists(Path.Combine(_dir, "tack-shim.exe")))
            throw new FileNotFoundException("tack-shim.exe not found in test output; build the solution first");
    }

    // A copy of the shim host named <tool>.exe -- production copies the AOT exe the same way.
    public string ShimFor(string tool)
    {
        string dest = Path.Combine(_dir, tool + ".exe");
        if (!File.Exists(dest)) File.Copy(Path.Combine(_dir, "tack-shim.exe"), dest);
        return dest;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
