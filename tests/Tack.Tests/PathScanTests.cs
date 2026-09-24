using Tack.Core.Resolution;
using Xunit;

namespace Tack.Tests;

public sealed class PathScanTests
{
    // A fake filesystem: only these exact paths "exist".
    private static Func<string, bool> Files(params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Finds_a_tool_across_several_path_dirs_in_order()
    {
        var report = PathScan.FindOnPath(
            "node",
            t => t == EnvironmentVariableTarget.Machine
                ? @"C:\Program Files\nodejs"
                : @"C:\Users\me\scoop\apps\nodejs\current",
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Users\me\scoop\apps\nodejs\current\node.exe"));

        Assert.Equal(2, report.Count);
        Assert.Equal(@"C:\Program Files\nodejs", report[0].BinDir);          // machine first
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", report[0].ExePath);
        Assert.Equal(@"C:\Users\me\scoop\apps\nodejs\current", report[1].BinDir);
    }

    [Fact]
    public void Excludes_the_tack_shims_dir_so_the_shim_is_never_offered()
    {
        string shims = @"C:\Users\me\AppData\Local\tack\shims";
        var report = PathScan.FindOnPath(
            "node",
            t => t == EnvironmentVariableTarget.Machine ? shims : @"C:\Program Files\nodejs",
            excludeDirs: new[] { shims },
            fileExists: Files(
                shims + @"\node.exe",                         // the shim - must be skipped
                @"C:\Program Files\nodejs\node.exe"));

        Assert.Single(report);
        Assert.Equal(@"C:\Program Files\nodejs", report[0].BinDir);
    }

    [Fact]
    public void Dedupes_a_dir_that_appears_on_path_more_than_once()
    {
        var report = PathScan.FindOnPath(
            "node",
            t => t == EnvironmentVariableTarget.Machine
                ? @"C:\Program Files\nodejs;C:\Program Files\nodejs\"  // same dir, twice, trailing slash
                : null,
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(@"C:\Program Files\nodejs\node.exe"));

        Assert.Single(report);
    }

    [Fact]
    public void Probes_exec_extensions_not_just_exe()
    {
        var report = PathScan.FindOnPath(
            "npm",
            _ => @"C:\Program Files\nodejs",
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(@"C:\Program Files\nodejs\npm.cmd")); // npm is a .cmd

        Assert.Single(report);
        Assert.Equal(@"C:\Program Files\nodejs\npm.cmd", report[0].ExePath);
    }

    [Fact]
    public void Returns_empty_when_the_tool_is_nowhere_on_path()
    {
        var report = PathScan.FindOnPath(
            "python",
            _ => @"C:\Program Files\nodejs",
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(@"C:\Program Files\nodejs\node.exe"));

        Assert.Empty(report);
    }
}
