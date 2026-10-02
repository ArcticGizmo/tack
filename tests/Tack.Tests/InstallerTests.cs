using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Tack.Core.Installs;
using Xunit;

namespace Tack.Tests;

// The install engine against real folders in a temp dir, with a fake network and a fake process runner.
public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-installs-").FullName;
    private readonly FakeDownloader _net = new();
    private readonly FakeRunner _runner = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.FromHours(10));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private Installer NewInstaller(long? maxBytes = null) => maxBytes is { } max
        ? new Installer(_root, _net, _runner, () => Now) { MaxUnpackedBytes = max }
        : new Installer(_root, _net, _runner, () => Now);

    private static readonly Uri Url = new("https://example.org/node-v1.0.0-win-x64.zip");

    // A node-shaped plan for zip, which the fake network serves at Url.
    private InstallPlan NodePlan(byte[] zip, string? top = "node-v1.0.0-win-x64", string? sha = null)
    {
        _net.Serve(Url, zip);
        return new InstallPlan
        {
            Tool = "node",
            Version = "1.0.0",
            Source = "example.org",
            Url = Url,
            Sha256 = sha ?? Sha(zip),
            TopFolder = top,
            Exposes = new[] { "node", "npm", "npx", "corepack" },
        };
    }

    private static readonly (string, string)[] NodeFiles =
    {
        ("node-v1.0.0-win-x64/node.exe", "MZ"),
        ("node-v1.0.0-win-x64/npm.cmd", "@echo npm"),
        ("node-v1.0.0-win-x64/npx.cmd", "@echo npx"),
        ("node-v1.0.0-win-x64/install_tools.bat", "@echo nope"),
        ("node-v1.0.0-win-x64/node_modules/npm/package.json", "{}"),
    };

    private string Target => Path.Combine(_root, "node", "1.0.0");

    private void AssertNothingLeft()
    {
        Assert.False(Directory.Exists(Target), "nothing may be left in place");
        Assert.Empty(Entries(Path.Combine(_root, ".staging")));
        Assert.Empty(Entries(Path.Combine(_root, ".trash")));
    }

    private static IEnumerable<string> Entries(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir) : Array.Empty<string>();

    // ---- install ----

    [Fact]
    public async Task Installs_into_tool_version_and_keeps_the_names_that_exist()
    {
        var installed = await NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles)));

        Assert.Equal(Target, installed.BinDir);
        Assert.True(File.Exists(Path.Combine(Target, "node.exe")));
        Assert.True(File.Exists(Path.Combine(Target, "node_modules", "npm", "package.json")));
        Assert.Equal(new[] { "node", "npm", "npx" }, installed.Exposes); // no corepack in this archive, and never install_tools
        Assert.Null(installed.ExtraBinDirs);
        Assert.Equal("example.org", installed.Install!.Source);
        Assert.Equal(Url.ToString(), installed.Install.Url);
        Assert.Equal(Sha(Zip(NodeFiles)), installed.Install.Sha256);
        Assert.Equal(Now, installed.Install.InstalledAt);

        string marker = File.ReadAllText(Path.Combine(Target, Installer.MarkerFile));
        Assert.Contains("\"tool\": \"node\"", marker);
        Assert.Contains("\"version\": \"1.0.0\"", marker);
        Assert.Empty(Entries(Path.Combine(_root, ".staging")));
        Assert.False(File.Exists(Path.Combine(_root, ".lock")), "the lock is released");
    }

    [Fact]
    public async Task A_flat_archive_with_extra_bin_dirs_and_a_post_install_step_runs_it_in_the_final_folder()
    {
        byte[] zip = Zip(("python.exe", "MZ"), ("Lib/ensurepip/_bundled/pip.whl", "wheel"));
        var url = new Uri("https://example.org/python.zip");
        _net.Serve(url, zip);
        string target = Path.Combine(_root, "python", "3.12.10");
        // What pip does: make Scripts\pip.exe, which only works from the folder it was made in.
        _runner.OnRun = (exe, cwd) =>
        {
            Directory.CreateDirectory(Path.Combine(cwd, "Scripts"));
            File.WriteAllText(Path.Combine(cwd, "Scripts", "pip.exe"), "MZ");
        };

        var installed = await NewInstaller().InstallAsync(new InstallPlan
        {
            Tool = "python", Version = "3.12.10", Source = "example.org", Url = url, Sha256 = Sha(zip), TopFolder = null,
            Exposes = new[] { "python", "pip", "pip3" },
            ExtraBinDirs = new[] { "Scripts" },
            PostInstall = new[] { new PostInstallCommand("python.exe", new[] { "-m", "pip" }, "creating pip's launchers") },
        });

        var run = Assert.Single(_runner.Runs);
        Assert.Equal(Path.Combine(target, "python.exe"), run.Exe);
        Assert.Equal(target, run.Cwd); // not staging
        Assert.Equal(new[] { "-m", "pip" }, run.Args);
        Assert.Equal(new[] { Path.Combine(target, "Scripts") }, installed.ExtraBinDirs);
        Assert.Equal(new[] { "python", "pip" }, installed.Exposes); // pip3 wasn't made
    }

    [Fact]
    public async Task A_hash_mismatch_deletes_the_download_and_installs_nothing()
    {
        var e = await Assert.ThrowsAsync<InstallException>(() =>
            NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles), sha: new string('0', 64))));

        Assert.Contains("doesn't match its published SHA-256", e.Message);
        Assert.Contains($"expected {new string('0', 64)}", e.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task An_entry_that_escapes_the_folder_is_refused()
    {
        byte[] zip = Zip(("node-v1.0.0-win-x64/node.exe", "MZ"), ("../../escaped.txt", "x"));

        var e = await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(NodePlan(zip)));

        Assert.Contains("couldn't be unpacked", e.Message);
        Assert.Empty(Directory.EnumerateFiles(_root, "escaped.txt", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
        AssertNothingLeft();
    }

    [Fact]
    public async Task An_archive_without_the_tools_own_executable_is_discarded_after_placing()
    {
        byte[] zip = Zip(("node-v1.0.0-win-x64/npm.cmd", "@echo npm"));

        var e = await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(NodePlan(zip)));

        Assert.Contains("has no node executable", e.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task An_archive_without_its_top_folder_is_refused()
    {
        var e = await Assert.ThrowsAsync<InstallException>(() =>
            NewInstaller().InstallAsync(NodePlan(Zip(("other/node.exe", "MZ")))));
        Assert.Contains("doesn't contain the folder tack expects (node-v1.0.0-win-x64)", e.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_failed_post_install_step_discards_the_version_and_shows_its_output()
    {
        byte[] zip = Zip(("python.exe", "MZ"));
        var url = new Uri("https://example.org/python.zip");
        _net.Serve(url, zip);
        _runner.ExitCode = 2;
        _runner.Output = "line1\nERROR: pip is unhappy\n";

        var e = await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(new InstallPlan
        {
            Tool = "python", Version = "3.12.10", Source = "example.org", Url = url, Sha256 = Sha(zip),
            Exposes = new[] { "python" },
            PostInstall = new[] { new PostInstallCommand("python.exe", Array.Empty<string>(), "creating pip's launchers") },
        }));

        Assert.Contains("creating pip's launchers failed (exit code 2)", e.Message);
        Assert.Contains("ERROR: pip is unhappy", e.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "python", "3.12.10")));
        Assert.Empty(Entries(Path.Combine(_root, ".trash")));
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing()
    {
        using var cancel = new CancellationTokenSource();
        _net.BeforeWrite = () => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles)), cancel: cancel.Token));

        AssertNothingLeft();
    }

    [Fact]
    public async Task Leftovers_from_a_killed_install_are_swept_next_time()
    {
        string stale = Directory.CreateDirectory(Path.Combine(_root, ".staging", "dead", "unpacked")).FullName;
        File.WriteAllText(Path.Combine(stale, "half.exe"), "MZ");
        string trash = Directory.CreateDirectory(Path.Combine(_root, ".trash", "old")).FullName;

        await NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles)));

        Assert.False(Directory.Exists(Path.Combine(_root, ".staging", "dead")));
        Assert.False(Directory.Exists(trash));
    }

    [Fact]
    public async Task An_existing_folder_is_never_overwritten()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "mine.txt"), "keep me");

        var e = await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles))));

        Assert.Contains("already exists", e.Message);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(Target, "mine.txt")));
        Assert.Empty(_net.Fetched); // refused before downloading anything
    }

    [Fact]
    public async Task Only_one_install_runs_at_a_time()
    {
        Directory.CreateDirectory(_root);
        using var held = new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var e = await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles))));
        Assert.Contains("another tack install or removal is running", e.Message);
    }

    [Fact]
    public async Task An_archive_that_unpacks_too_big_is_refused()
    {
        var e = await Assert.ThrowsAsync<InstallException>(() =>
            NewInstaller(maxBytes: 10).InstallAsync(NodePlan(Zip(("node-v1.0.0-win-x64/node.exe", new string('x', 100))))));
        Assert.Contains("more than tack accepts", e.Message);
        AssertNothingLeft();
    }

    [Theory]
    [InlineData("node", @"..\..\evil")]
    [InlineData(@"..\x", "1.0.0")]
    public async Task A_plan_naming_an_unsafe_folder_is_refused(string tool, string version)
    {
        var plan = new InstallPlan
        {
            Tool = tool, Version = version, Source = "x", Url = Url, Sha256 = new string('a', 64), Exposes = new[] { "node" },
        };
        await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(plan));
        Assert.Empty(_net.Fetched);
    }

    [Fact]
    public async Task A_plain_http_plan_is_refused()
    {
        var plan = new InstallPlan
        {
            Tool = "node", Version = "1.0.0", Source = "x", Url = new Uri("http://example.org/a.zip"),
            Sha256 = new string('a', 64), Exposes = new[] { "node" },
        };
        var e = await Assert.ThrowsAsync<InstallException>(() => NewInstaller().InstallAsync(plan));
        Assert.Contains("only downloads over HTTPS", e.Message);
    }

    // ---- remove ----

    [Fact]
    public async Task Removes_a_version_it_installed()
    {
        var installer = NewInstaller();
        await installer.InstallAsync(NodePlan(Zip(NodeFiles)));

        var result = installer.Remove(Target);

        Assert.Null(result.Leftover);
        Assert.False(Directory.Exists(Target));
        Assert.Empty(Entries(Path.Combine(_root, ".trash")));
    }

    [Fact]
    public async Task A_version_in_use_is_left_untouched()
    {
        var installer = NewInstaller();
        await installer.InstallAsync(NodePlan(Zip(NodeFiles)));

        // A running node.exe holds its image open like this: no delete sharing, so its folder can't be renamed.
        using (new FileStream(Path.Combine(Target, "node.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var e = Assert.Throws<InstallException>(() => installer.Remove(Target));
            Assert.Contains("is in use, so nothing was removed", e.Message);
            Assert.Contains("a terminal whose current directory is inside it", e.Message);
            Assert.True(File.Exists(Path.Combine(Target, "npm.cmd")));
        }

        installer.Remove(Target); // fine once it's closed
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task A_version_a_program_is_running_from_is_left_untouched()
    {
        // Windows lets a folder be renamed while an exe in it runs, so this is checked separately from the rename.
        var installer = new Installer(_root, _net, _runner, () => Now, runningFrom: _ => new[] { "node.exe (pid 42)" });
        await installer.InstallAsync(NodePlan(Zip(NodeFiles)));

        var e = Assert.Throws<InstallException>(() => installer.Remove(Target));

        Assert.Contains("is in use by node.exe (pid 42), so nothing was removed", e.Message);
        Assert.True(File.Exists(Path.Combine(Target, Installer.MarkerFile)));
        Assert.Empty(Entries(Path.Combine(_root, ".trash")));
    }

    [Fact]
    public void Running_processes_are_found_by_their_folder()
    {
        // A real process: the test stub waits on its stdin, so it keeps running from a folder until that's closed.
        if (!OperatingSystem.IsWindows()) return;
        string bin = AppContext.BaseDirectory;
        Directory.CreateDirectory(Target);
        foreach (var f in Directory.GetFiles(bin, "tack-stub.*"))
            File.Copy(f, Path.Combine(Target, Path.GetFileName(f)));
        using var stub = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Target, "tack-stub.exe"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        try
        {
            var running = Tack.Core.Platform.RunningProcesses.From(Target);
            Assert.Equal($"tack-stub.exe (pid {stub.Id})", Assert.Single(running));
            Assert.Empty(Tack.Core.Platform.RunningProcesses.From(Path.Combine(_root, "node", "1.0"))); // a name prefix isn't inside
        }
        finally
        {
            stub.StandardInput.Close();
            stub.WaitForExit(10_000);
        }
        Assert.Empty(Tack.Core.Platform.RunningProcesses.From(Target));
    }

    [Fact]
    public void A_folder_outside_the_installs_folder_is_never_deleted()
    {
        string outside = Directory.CreateTempSubdirectory("tack-not-installs-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, Installer.MarkerFile), "{\"tool\":\"node\",\"version\":\"1.0.0\"}");

            var e = Assert.Throws<InstallException>(() => NewInstaller().Remove(outside));

            Assert.Contains("isn't inside tack's installs folder", e.Message);
            Assert.True(Directory.Exists(outside));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Theory]
    [InlineData("")]                         // the installs folder itself
    [InlineData("node")]                     // a tool folder, not a version
    [InlineData(@"node\1.0.0\sub")]          // inside a version
    [InlineData(@".staging\x")]              // a dot-folder
    [InlineData(@"..\elsewhere")]
    public void Only_a_tool_version_folder_can_be_removed(string relative)
    {
        // Made unique so the one outside the installs folder can't collide with anything in %TEMP%.
        string folder = Path.GetFullPath(Path.Combine(_root, relative.Replace("elsewhere", Path.GetFileName(_root) + "-elsewhere")));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Installer.MarkerFile), "{\"tool\":\"node\",\"version\":\"1.0.0\"}");
        try
        {
            Assert.Throws<InstallException>(() => NewInstaller().Remove(folder));
            Assert.True(Directory.Exists(folder));
        }
        finally
        {
            if (relative.StartsWith("..")) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_folder_without_a_marker_is_never_deleted()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "node.exe"), "MZ");

        var e = Assert.Throws<InstallException>(() => NewInstaller().Remove(Target));

        Assert.Contains($"has no {Installer.MarkerFile}, so tack didn't install it", e.Message);
        Assert.True(File.Exists(Path.Combine(Target, "node.exe")));
    }

    [Fact]
    public void A_marker_for_another_version_is_not_enough()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, Installer.MarkerFile), "{\"tool\":\"node\",\"version\":\"2.0.0\"}");

        var e = Assert.Throws<InstallException>(() => NewInstaller().Remove(Target));

        Assert.Contains("is for node@2.0.0, not node@1.0.0", e.Message);
        Assert.True(Directory.Exists(Target));
    }

    [Fact]
    public void A_junction_is_never_followed_or_deleted()
    {
        // A junction rather than a symlink: any user can make one, without admin or developer mode.
        string real = Directory.CreateTempSubdirectory("tack-link-target-").FullName;
        File.WriteAllText(Path.Combine(real, Installer.MarkerFile), "{\"tool\":\"node\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(real, "precious.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(_root, "node"));
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{Target}\" \"{real}\"")
               { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        Assert.True(new DirectoryInfo(Target).Attributes.HasFlag(FileAttributes.ReparsePoint));

        try
        {
            var e = Assert.Throws<InstallException>(() => NewInstaller().Remove(Target));
            Assert.Contains("it's a link", e.Message);
            Assert.True(File.Exists(Path.Combine(real, "precious.txt")));
        }
        finally { Directory.Delete(Target); Directory.Delete(real, true); }
    }

    // ---- keep-files, unregistered folders, leftovers ----

    [Fact]
    public async Task Disowning_keeps_the_files_and_tack_never_deletes_them_after()
    {
        var installer = NewInstaller();
        await installer.InstallAsync(NodePlan(Zip(NodeFiles)));

        Assert.True(installer.Disown(Target));

        Assert.True(File.Exists(Path.Combine(Target, "node.exe")));
        Assert.False(File.Exists(Path.Combine(Target, Installer.MarkerFile)));
        Assert.Throws<InstallException>(() => installer.Remove(Target)); // no marker any more
        Assert.Equal(new[] { new UnregisteredFolder(Target, Owned: false) }, installer.Unregistered(new()));
    }

    [Fact]
    public void Disowning_a_folder_tack_did_not_make_changes_nothing()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "node.exe"), "MZ");
        Assert.False(NewInstaller().Disown(Target));
        Assert.True(File.Exists(Path.Combine(Target, "node.exe")));
    }

    [Fact]
    public async Task Finds_folders_nothing_is_registered_for()
    {
        var installer = NewInstaller();
        var installed = await installer.InstallAsync(NodePlan(Zip(NodeFiles)));
        string stray = Directory.CreateDirectory(Path.Combine(_root, "python", "3.12.10")).FullName; // no marker
        Directory.CreateDirectory(Path.Combine(_root, ".staging", "x")); // dot-folders are never versions

        var config = new Tack.Core.Config.CentralConfig();
        Assert.Equal(
            new[] { new UnregisteredFolder(Target, Owned: true), new UnregisteredFolder(stray, Owned: false) },
            installer.Unregistered(config).OrderBy(f => f.Path.Contains("python")));

        Tack.Core.Config.ToolRegistry.Register(config, "node", "1.0.0", installed);
        Assert.Equal(new[] { new UnregisteredFolder(stray, Owned: false) }, installer.Unregistered(config));
    }

    [Fact]
    public void Leftovers_are_listed_and_swept()
    {
        var installer = NewInstaller();
        Assert.Empty(installer.Leftovers());
        Directory.CreateDirectory(Path.Combine(_root, ".staging", "dead"));
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, ".trash")).FullName, "old.txt"), "x");

        Assert.Equal(2, installer.Leftovers().Count);
        Assert.Empty(installer.SweepLeftovers());
        Assert.Empty(installer.Leftovers());
    }

    [Fact]
    public async Task An_installer_without_a_network_can_remove_but_not_install()
    {
        await NewInstaller().InstallAsync(NodePlan(Zip(NodeFiles)));
        var offline = new Installer(_root);

        offline.Remove(Target);

        Assert.False(Directory.Exists(Target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => offline.InstallAsync(NodePlan(Zip(NodeFiles))));
    }

    // ---- helpers ----

    internal static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open()))
                    w.Write(content);
        return ms.ToArray();
    }

    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

internal sealed class FakeDownloader : IDownloader
{
    private readonly Dictionary<Uri, byte[]> _files = new();
    public List<Uri> Fetched { get; } = new();
    public Action? BeforeWrite { get; set; }

    public void Serve(Uri url, byte[] bytes) => _files[url] = bytes;
    public void Serve(Uri url, string text) => _files[url] = Encoding.UTF8.GetBytes(text);

    public Task<string> GetStringAsync(Uri url, CancellationToken cancel)
    {
        Fetched.Add(url);
        return _files.TryGetValue(url, out var b)
            ? Task.FromResult(Encoding.UTF8.GetString(b))
            : throw new InstallException($"couldn't download {url}: offline.");
    }

    public async Task DownloadFileAsync(Uri url, string path, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        Fetched.Add(url);
        if (!_files.TryGetValue(url, out var bytes)) throw new InstallException($"couldn't download {url}: offline.");
        BeforeWrite?.Invoke();
        cancel.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(path, bytes, cancel);
        progress?.Report(new DownloadProgress(bytes.Length, bytes.Length));
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public List<(string Exe, IReadOnlyList<string> Args, string Cwd)> Runs { get; } = new();
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public Action<string, string>? OnRun { get; set; }

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancel)
    {
        Runs.Add((exe, args, workingDirectory));
        OnRun?.Invoke(exe, workingDirectory);
        return Task.FromResult(new ProcessResult(ExitCode, Output));
    }
}

public sealed class HttpDownloaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tack-dl-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // Answers each request from a script of (url -> response).
    private sealed class Script(Dictionary<string, Func<HttpResponseMessage>> routes) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(routes.TryGetValue(request.RequestUri!.ToString(), out var r)
                ? r()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task Fetches_text_and_follows_https_redirects_including_relative_ones()
    {
        var script = new Script(new()
        {
            ["https://a.example/start"] = () => Redirect("https://b.example/next"),
            ["https://b.example/next"] = () => Redirect("/final"),
            ["https://b.example/final"] = () => Ok("hello"),
        });
        using var dl = new HttpDownloader("tack-test", script);

        Assert.Equal("hello", await dl.GetStringAsync(new Uri("https://a.example/start"), default));
        Assert.Equal(3, script.Requests.Count);
    }

    [Fact]
    public async Task Refuses_plain_http()
    {
        using var dl = new HttpDownloader("tack-test", new Script(new()));
        var e = await Assert.ThrowsAsync<InstallException>(() => dl.GetStringAsync(new Uri("http://a.example/x"), default));
        Assert.Contains("only downloads over HTTPS", e.Message);
    }

    [Fact]
    public async Task Refuses_a_redirect_to_plain_http()
    {
        var script = new Script(new() { ["https://a.example/x"] = () => Redirect("http://a.example/x") });
        using var dl = new HttpDownloader("tack-test", script);

        var e = await Assert.ThrowsAsync<InstallException>(() => dl.GetStringAsync(new Uri("https://a.example/x"), default));

        Assert.Contains("redirected to http://a.example/x, which isn't HTTPS", e.Message);
        Assert.Single(script.Requests); // never followed
    }

    [Fact]
    public async Task Stops_after_too_many_redirects()
    {
        var script = new Script(new() { ["https://a.example/loop"] = () => Redirect("https://a.example/loop") });
        using var dl = new HttpDownloader("tack-test", script);

        var e = await Assert.ThrowsAsync<InstallException>(() => dl.GetStringAsync(new Uri("https://a.example/loop"), default));
        Assert.Contains($"more than {HttpDownloader.MaxRedirects} times", e.Message);
    }

    [Fact]
    public async Task A_failure_status_names_the_url()
    {
        using var dl = new HttpDownloader("tack-test", new Script(new()));
        var e = await Assert.ThrowsAsync<InstallException>(() => dl.GetStringAsync(new Uri("https://a.example/missing"), default));
        Assert.Contains("https://a.example/missing answered 404", e.Message);
    }

    [Fact]
    public async Task Downloads_a_file_with_progress()
    {
        byte[] body = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var script = new Script(new() { ["https://a.example/f.zip"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) } });
        using var dl = new HttpDownloader("tack-test", script);
        var reports = new List<DownloadProgress>();
        string path = Path.Combine(_dir, "f.zip");

        await dl.DownloadFileAsync(new Uri("https://a.example/f.zip"), path, new SyncProgress<DownloadProgress>(reports.Add), default);

        Assert.Equal(body, File.ReadAllBytes(path));
        Assert.Equal(new DownloadProgress(body.Length, body.Length), reports[^1]);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Returns_the_exit_code_and_both_streams_from_the_working_directory()
    {
        string cwd = Path.GetTempPath();
        var r = await new ProcessRunner().RunAsync(Cmd, new[] { "/c", "echo out& echo err 1>&2& cd& exit /b 3" }, cwd, default);

        Assert.Equal(3, r.ExitCode);
        Assert.Contains("out", r.Output);
        Assert.Contains("err", r.Output);
        Assert.Contains(Path.TrimEndingDirectorySeparator(cwd), r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_program_is_an_install_error()
    {
        var e = await Assert.ThrowsAsync<InstallException>(() =>
            new ProcessRunner().RunAsync(@"C:\no\such\python.exe", Array.Empty<string>(), Path.GetTempPath(), default));
        Assert.Contains(@"couldn't run C:\no\such\python.exe", e.Message);
    }

    [Fact]
    public async Task Cancelling_kills_it()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(Cmd, new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, Path.GetTempPath(), cancel.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}

public sealed class IndexCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tack-cache-").FullName;
    private DateTimeOffset _now = new(2026, 10, 2, 4, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private int _fetches;
    private bool _offline;

    private Task<string> Fetch(Uri url, CancellationToken _)
    {
        _fetches++;
        if (_offline) throw new InstallException($"couldn't download {url}: offline.");
        return Task.FromResult(InstallFixtures.Read("node-index.json"));
    }

    private Task<IndexResult> Load(bool refresh = false) =>
        new IndexCache(_dir, () => _now).LoadAsync(new NodeSource(), Fetch, refresh);

    private void Age(TimeSpan by)
    {
        foreach (var f in Directory.GetFiles(_dir))
            File.SetLastWriteTimeUtc(f, (_now - by).UtcDateTime);
    }

    [Fact]
    public async Task A_fresh_copy_is_used_without_fetching()
    {
        _now = DateTimeOffset.UtcNow;
        var first = await Load();
        Assert.Null(first.CachedAt);
        Age(TimeSpan.FromMinutes(10));

        var second = await Load();

        Assert.Equal(1, _fetches);
        Assert.Equal(first.Versions.Count, second.Versions.Count);
        Assert.NotNull(second.CachedAt);
        Assert.Null(second.Offline);
    }

    [Fact]
    public async Task An_old_copy_or_refresh_fetches_again()
    {
        await Load();
        Age(TimeSpan.FromHours(2));
        await Load();
        await Load(refresh: true);
        Assert.Equal(3, _fetches);
    }

    [Fact]
    public async Task Offline_falls_back_to_an_old_copy_and_says_how_old()
    {
        await Load();
        Age(TimeSpan.FromDays(3));
        _offline = true;

        var result = await Load();

        Assert.NotEmpty(result.Versions);
        Assert.Equal(_now - TimeSpan.FromDays(3), result.CachedAt);
        Assert.Contains("offline", result.Offline);
    }

    [Fact]
    public async Task Offline_with_no_copy_fails()
    {
        _offline = true;
        await Assert.ThrowsAsync<InstallException>(() => Load());
    }
}
