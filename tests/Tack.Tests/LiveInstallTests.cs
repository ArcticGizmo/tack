using System.Diagnostics;
using System.Runtime.InteropServices;
using Tack.Core.Config;
using Tack.Core.Installs;
using Xunit;

namespace Tack.Tests;

/// <summary>A test that downloads for real. Skipped unless <c>TACK_LIVE_TESTS=1</c>, so a plain <c>dotnet test</c>
/// never touches the network.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TACK_LIVE_TESTS") != "1")
            Skip = "downloads from nodejs.org / python.org; set TACK_LIVE_TESTS=1 to run";
    }
}

// The whole engine against the real sources: index, checksums, download, unpack, post-install, then the result runs.
// Into a temp folder, never tack's own.
public sealed class LiveInstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-live-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private async Task<InstalledVersion> Install(IToolSource source, string spec)
    {
        using var net = new HttpDownloader("tack-tests");
        var index = await ToolIndex.LoadAsync(source, net.GetStringAsync);
        var remote = VersionSpec.Resolve(source, index, spec, RuntimeInformation.OSArchitecture);
        string? sums = remote.ChecksumsUrl is { } u ? await net.GetStringAsync(u, default) : null;
        var plan = source.Plan(remote, RuntimeInformation.OSArchitecture, sums);
        return await new Installer(_root, net, new ProcessRunner()).InstallAsync(plan);
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    [LiveFact]
    public async Task Installs_the_newest_node_lts_and_it_runs()
    {
        var v = await Install(new NodeSource(), "lts");

        Assert.StartsWith("v", Run(Path.Combine(v.BinDir, "node.exe"), "-v"));
        Assert.Contains("npm", v.Exposes);
        Assert.DoesNotContain("install_tools", v.Exposes);
    }

    [LiveFact]
    public async Task Installs_python_with_working_pip_launchers()
    {
        var v = await Install(new PythonSource(), "3.12");

        Assert.StartsWith("Python 3.12.", Run(Path.Combine(v.BinDir, "python.exe"), "-V"));
        Assert.StartsWith("pip ", Run(Path.Combine(v.BinDir, "Scripts", "pip.exe"), "-V"));
        Assert.Equal(new[] { "python", "pip", "pip3" }, v.Exposes);
    }
}
