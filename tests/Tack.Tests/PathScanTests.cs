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
    public void Prefers_the_cmd_over_an_extensionless_shell_script_beside_it()
    {
        // node ships both npx (a bash script) and npx.cmd; CreateProcess can only run the latter.
        var report = PathScan.FindOnPath(
            "npx",
            _ => @"C:\fnm\node-versions\v24\installation",
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(
                @"C:\fnm\node-versions\v24\installation\npx",
                @"C:\fnm\node-versions\v24\installation\npx.cmd"));

        Assert.Single(report);
        Assert.Equal(@"C:\fnm\node-versions\v24\installation\npx.cmd", report[0].ExePath);
    }

    [Fact]
    public void Never_matches_an_extensionless_file_alone()
    {
        var report = PathScan.FindOnPath(
            "npx",
            _ => @"C:\somewhere",
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(@"C:\somewhere\npx"));

        Assert.Empty(report);
    }

    [Fact]
    public void Expands_environment_references_in_path_entries()
    {
        // A PATH entry like %NVM_SYMLINK% must be expanded before probing, or the tool is missed.
        string expandTo(string p) => p.Replace("%NVM_SYMLINK%", @"C:\Users\me\nvm\current", StringComparison.OrdinalIgnoreCase);

        var report = PathScan.FindOnPath(
            "node",
            t => t == EnvironmentVariableTarget.User ? "%NVM_SYMLINK%" : null,
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(@"C:\Users\me\nvm\current\node.exe"),
            expand: expandTo);

        Assert.Single(report);
        Assert.Equal(@"C:\Users\me\nvm\current", report[0].BinDir); // stored expanded, ready to register
    }

    [Fact]
    public void An_undefined_environment_reference_is_skipped_not_thrown()
    {
        // %DOES_NOT_EXIST% stays literal (mirroring Windows); it just matches nothing.
        var report = PathScan.FindOnPath(
            "node",
            t => t == EnvironmentVariableTarget.User ? @"%DOES_NOT_EXIST%\bin" : null,
            excludeDirs: Array.Empty<string>(),
            fileExists: Files(@"C:\real\node.exe"),
            expand: p => p); // identity: leave the literal unexpanded

        Assert.Empty(report);
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
