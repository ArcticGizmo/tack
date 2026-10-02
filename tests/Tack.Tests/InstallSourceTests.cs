using System.Runtime.InteropServices;
using Tack.Core.Installs;
using Xunit;

namespace Tack.Tests;

// The install sources against the vendor indexes recorded in checkpoint 0 (Fixtures/Installs; see
// docs/m10-spike-findings.md). No network: "fetching" a page reads the fixture with the same file name.
internal static class InstallFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Installs", name));

    /// <summary>A fetch that serves fixtures by the last segment of the URL, and fails on anything else.</summary>
    public static Func<Uri, CancellationToken, Task<string>> Fetch(Dictionary<string, string> byFileName) =>
        (uri, _) => byFileName.TryGetValue(uri.Segments[^1], out var fixture)
            ? Task.FromResult(Read(fixture))
            : throw new InvalidOperationException($"unexpected fetch: {uri}");

    public static IReadOnlyList<RemoteVersion> Node() =>
        ToolIndex.LoadAsync(new NodeSource(), Fetch(new() { ["index.json"] = "node-index.json" })).GetAwaiter().GetResult();

    public static IReadOnlyList<RemoteVersion> Python() =>
        ToolIndex.LoadAsync(new PythonSource(), Fetch(new()
        {
            ["index-windows.json"] = "python-index-windows.json",
            ["python-index-page2.json"] = "python-index-page2.json",
        })).GetAwaiter().GetResult();
}

public sealed class NodeSourceTests
{
    private static readonly NodeSource Source = new();
    private static readonly IReadOnlyList<RemoteVersion> Index = InstallFixtures.Node();

    private static string Resolve(string spec, Architecture arch = Architecture.X64) =>
        VersionSpec.Resolve(Source, Index, spec, arch).Version;

    private static string Fails(string spec, Architecture arch = Architecture.X64) =>
        Assert.Throws<InstallException>(() => VersionSpec.Resolve(Source, Index, spec, arch)).Message;

    [Fact]
    public void Parses_versions_lts_names_builds_and_checksum_urls()
    {
        var v24 = Assert.Single(Index, v => v.Version == "24.21.0");
        Assert.Equal("Krypton", v24.Lts);
        Assert.False(v24.PreRelease);
        Assert.Equal(new Uri("https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-x64.zip"), v24.Archives[Architecture.X64].Url);
        Assert.Equal(new Uri("https://nodejs.org/dist/v24.21.0/node-v24.21.0-win-arm64.zip"), v24.Archives[Architecture.Arm64].Url);
        Assert.Equal(new Uri("https://nodejs.org/dist/v24.21.0/SHASUMS256.txt"), v24.ChecksumsUrl);

        Assert.Null(Assert.Single(Index, v => v.Version == "26.10.0").Lts);
        Assert.False(Assert.Single(Index, v => v.Version == "18.20.8").Archives.ContainsKey(Architecture.Arm64));
        Assert.DoesNotContain(Index, v => v.Version.StartsWith('v'));
    }

    [Theory]
    [InlineData("latest", "26.10.0")]
    [InlineData("lts", "24.21.0")]
    [InlineData("LTS", "24.21.0")]
    [InlineData("24", "24.21.0")]
    [InlineData("24.20", "24.20.0")]
    [InlineData("20", "20.20.2")]
    [InlineData("20.11.1", "20.11.1")]
    [InlineData("v20.11.1", "20.11.1")]
    [InlineData(" 22 ", "22.23.3")]
    public void Resolves_a_spec(string spec, string expected) => Assert.Equal(expected, Resolve(spec));

    [Fact]
    public void Lts_on_arm64_skips_nothing_when_the_newest_lts_has_an_arm64_build() =>
        Assert.Equal("24.21.0", Resolve("lts", Architecture.Arm64));

    [Fact]
    public void A_prefix_with_no_arm64_build_says_so()
    {
        string message = Fails("18", Architecture.Arm64);
        Assert.Contains("no Windows arm64 build for node@18", message);
        Assert.Contains("18.20.8, is built for x64", message);
    }

    [Fact]
    public void An_exact_version_with_no_arm64_build_says_what_it_is_built_for() =>
        Assert.Contains("node 18.20.8 has no Windows arm64 build on nodejs.org (it's built for x64)", Fails("18.20.8", Architecture.Arm64));

    [Fact]
    public void A_prefix_respects_the_dot_boundary() => Assert.Contains("no Windows build of node 2.", Fails("2"));

    [Fact]
    public void A_missing_patch_points_at_the_newest_in_its_line() =>
        Assert.Contains("The newest node 20.11 it has is 20.11.1: node@20.11.1.", Fails("20.11.9"));

    [Theory]
    [InlineData("twenty")]
    [InlineData(@"..\..\evil")]
    [InlineData("20.x")]
    [InlineData("")]
    public void Something_that_is_not_a_version_is_refused_with_examples(string spec)
    {
        string message = Fails(spec);
        Assert.Contains("isn't a version", message);
        Assert.Contains("node@26.10.0", message);
        Assert.Contains("node@26,", message);
        Assert.Contains("node@lts", message);
    }

    [Fact]
    public void Plans_with_the_hash_from_shasums()
    {
        var v = Index.Single(x => x.Version == "24.21.0");
        var plan = Source.Plan(v, Architecture.X64, InstallFixtures.Read("node-24.21.0-SHASUMS256.txt"));

        Assert.Equal("node", plan.Tool);
        Assert.Equal("24.21.0", plan.Version);
        Assert.Equal("nodejs.org", plan.Source);
        Assert.Equal(v.Archives[Architecture.X64].Url, plan.Url);
        Assert.Equal("158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541", plan.Sha256);
        Assert.Equal("node-v24.21.0-win-x64", plan.TopFolder);
        Assert.Equal(new[] { "node", "npm", "npx", "corepack" }, plan.Exposes);
        Assert.Empty(plan.ExtraBinDirs);
        Assert.Empty(plan.PostInstall);

        var arm = Source.Plan(v, Architecture.Arm64, InstallFixtures.Read("node-24.21.0-SHASUMS256.txt"));
        Assert.Equal("8779b1bde1d39f8d420e3b57aa657b39891af434d3de44a919044cec06785921", arm.Sha256);
        Assert.Equal("node-v24.21.0-win-arm64", arm.TopFolder);
    }

    [Fact]
    public void Planning_without_a_hash_for_the_archive_fails()
    {
        var v = Index.Single(x => x.Version == "20.20.2");
        // 24.21.0's SHASUMS256.txt doesn't list 20.20.2's zip.
        var e = Assert.Throws<InstallException>(() => Source.Plan(v, Architecture.X64, InstallFixtures.Read("node-24.21.0-SHASUMS256.txt")));
        Assert.Contains("no usable SHA-256 for node-v20.20.2-win-x64.zip", e.Message);
        Assert.Throws<InstallException>(() => Source.Plan(v, Architecture.X64, checksums: null));
    }

    [Fact]
    public void An_index_entry_with_an_unsafe_version_is_skipped()
    {
        string body = """
            [
            {"version":"v1.2.3","files":["win-x64-zip"],"lts":false},
            {"version":"v../../evil","files":["win-x64-zip"],"lts":false},
            {"version":"v4.5.6","files":["linux-x64"],"lts":false}
            ]
            """;
        var versions = Source.ParsePage(body, out var next);
        Assert.Null(next);
        Assert.Equal("1.2.3", Assert.Single(versions).Version); // the unsafe one, and the one with no Windows zip
    }
}

public sealed class PythonSourceTests
{
    private static readonly PythonSource Source = new();
    private static readonly IReadOnlyList<RemoteVersion> Index = InstallFixtures.Python();

    private static string Resolve(string spec, Architecture arch = Architecture.X64) =>
        VersionSpec.Resolve(Source, Index, spec, arch).Version;

    private static string Fails(string spec, Architecture arch = Architecture.X64) =>
        Assert.Throws<InstallException>(() => VersionSpec.Resolve(Source, Index, spec, arch)).Message;

    [Fact]
    public void Reads_both_pages_and_only_the_standard_core_builds()
    {
        Assert.Equal(
            new[] { "3.10.11", "3.11.0", "3.12.9", "3.12.10", "3.13.16", "3.14.8", "3.15.0rc2" },
            Index.Select(v => v.Version).Order(Tack.Core.Resolution.VersionOrder.Comparer));

        var v314 = Index.Single(v => v.Version == "3.14.8");
        Assert.Equal(new Uri("https://www.python.org/ftp/python/3.14.8/python-3.14.8-amd64.zip"), v314.Archives[Architecture.X64].Url);
        Assert.Equal(new Uri("https://www.python.org/ftp/python/3.14.8/python-3.14.8-arm64.zip"), v314.Archives[Architecture.Arm64].Url);
        Assert.Equal(2, v314.Archives.Count); // no win32, and not the free-threaded, embeddable or test zips
        Assert.Null(v314.ChecksumsUrl);
        Assert.Null(v314.Lts);

        Assert.True(Index.Single(v => v.Version == "3.15.0rc2").PreRelease);
        Assert.Null(Index.Single(v => v.Version == "3.10.11").Archives[Architecture.X64].Sha256); // the NuGet one
    }

    [Theory]
    [InlineData("latest", "3.14.8")]
    [InlineData("3", "3.14.8")]
    [InlineData("3.12", "3.12.10")]
    [InlineData("3.12.9", "3.12.9")]
    [InlineData("3.15.0rc2", "3.15.0rc2")]
    [InlineData("3.15.0RC2", "3.15.0rc2")]
    public void Resolves_a_spec(string spec, string expected) => Assert.Equal(expected, Resolve(spec));

    [Fact]
    public void A_line_with_only_pre_releases_asks_for_one_exactly()
    {
        string message = Fails("3.15");
        Assert.Contains("only pre-releases of python 3.15 are available (the newest is 3.15.0rc2)", message);
        Assert.Contains("python@3.15.0rc2", message);
    }

    [Fact]
    public void A_security_only_release_points_at_the_newest_windows_build_in_its_line()
    {
        // 3.12.12 exists upstream but is source-only, so python.org's Windows index doesn't list it.
        string message = Fails("3.12.12");
        Assert.Contains("python.org has no Windows build of python 3.12.12", message);
        Assert.Contains("python@3.12.10", message);
    }

    [Theory]
    [InlineData("3.10")]
    [InlineData("3.10.11")]
    public void A_version_published_without_a_checksum_is_refused(string spec) =>
        Assert.Contains("publishes python 3.10.11 without a checksum, so tack won't install it", Fails(spec));

    [Fact]
    public void There_is_no_lts()
    {
        string message = Fails("lts");
        Assert.Contains("python has no LTS releases", message);
        Assert.Contains("python@latest", message);
        Assert.Contains("python@3.14.8", message);
    }

    [Fact]
    public void Plans_with_the_index_hash_scripts_and_pip_launchers()
    {
        var plan = Source.Plan(Index.Single(v => v.Version == "3.12.10"), Architecture.X64, checksums: null);

        Assert.Equal("python", plan.Tool);
        Assert.Equal("python.org", plan.Source);
        Assert.Equal(new Uri("https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.zip"), plan.Url);
        Assert.Equal("8649692de846c56a7189d6dae5c322ab20deb1b5908b6f39426b62a36f39415d", plan.Sha256);
        Assert.Null(plan.TopFolder); // the zip is flat
        Assert.Equal(new[] { "python", "pip", "pip3" }, plan.Exposes); // no pythonw: the shim is a console program
        Assert.Equal(new[] { "Scripts" }, plan.ExtraBinDirs);

        var pip = Assert.Single(plan.PostInstall);
        Assert.Equal("python.exe", pip.Exe);
        Assert.Equal(new[] { "-I", "-m", "pip", "--isolated", "install" }, pip.Args.Take(5));
        Assert.Contains("--no-index", pip.Args);
        Assert.Equal(new[] { "--find-links", @"Lib\ensurepip\_bundled", "pip" }, pip.Args.TakeLast(3));
    }

    [Fact]
    public void Planning_a_version_without_a_checksum_fails()
    {
        var e = Assert.Throws<InstallException>(() =>
            Source.Plan(Index.Single(v => v.Version == "3.10.11"), Architecture.X64, checksums: null));
        Assert.Contains("without a checksum", e.Message);
    }

    [Fact]
    public void Entries_with_unsafe_versions_or_plain_http_urls_are_skipped()
    {
        string body = """
            { "versions": [
              { "id": "pythoncore-3.12-64", "tag": "3.12-64", "sort-version": "3.12.10",
                "url": "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.zip", "hash": { "sha256": "NOT-A-HASH" } },
              { "id": "pythoncore-3.12-64", "tag": "3.12-64", "sort-version": "..\\..\\evil",
                "url": "https://www.python.org/x.zip", "hash": { "sha256": "8649692de846c56a7189d6dae5c322ab20deb1b5908b6f39426b62a36f39415d" } },
              { "id": "pythoncore-3.11-64", "tag": "3.11-64", "sort-version": "3.11.9",
                "url": "http://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.zip", "hash": { "sha256": "8649692de846c56a7189d6dae5c322ab20deb1b5908b6f39426b62a36f39415d" } }
            ] }
            """;
        var v = Assert.Single(Source.ParsePage(body, out var next));
        Assert.Null(next);
        Assert.Equal("3.12.10", v.Version);
        Assert.Null(v.Archives[Architecture.X64].Sha256); // a malformed hash counts as no hash
    }
}

public sealed class AvailableTests
{
    private static readonly NodeSource Node = new();
    private static readonly PythonSource Python = new();

    private static string[] List(IToolSource source, IReadOnlyList<RemoteVersion> index, Architecture arch = Architecture.X64,
        string? prefix = null, bool all = false) =>
        Available.List(source, index, arch, prefix, all).Select(r => r.Version).ToArray();

    [Fact]
    public void Node_shows_the_newest_of_each_major_with_lts_names()
    {
        var rows = Available.List(Node, InstallFixtures.Node(), Architecture.X64);

        Assert.Equal(new[] { "26.10.0", "25.9.0", "24.21.0", "23.11.1", "22.23.3", "21.7.3", "20.20.2", "19.9.0", "18.20.8", "17.9.1", "16.20.2" },
            rows.Select(r => r.Version));
        Assert.Equal("Krypton", rows.Single(r => r.Version == "24.21.0").Lts);
        Assert.All(rows, r => Assert.Null(r.Unavailable));
    }

    [Fact]
    public void Node_on_arm64_leaves_out_lines_with_no_arm64_build() =>
        Assert.Equal(new[] { "26.10.0", "25.9.0", "24.21.0", "23.11.1", "22.23.3", "21.7.3", "20.20.2", "19.9.0" },
            List(Node, InstallFixtures.Node(), Architecture.Arm64));

    [Theory]
    [InlineData("24", new[] { "24.21.0", "24.20.0", "24.0.0" })]
    [InlineData("v20", new[] { "20.20.2", "20.11.1", "20.9.0" })]
    [InlineData("20.11", new[] { "20.11.1" })]
    [InlineData("2", new string[0])] // the dot boundary: not 20.x, 24.x...
    public void A_prefix_lists_its_whole_line(string prefix, string[] expected) =>
        Assert.Equal(expected, List(Node, InstallFixtures.Node(), prefix: prefix));

    [Fact]
    public void Python_shows_the_newest_of_each_minor_it_can_install()
    {
        // Not 3.15 (only a pre-release), not 3.10 (no checksum).
        Assert.Equal(new[] { "3.14.8", "3.13.16", "3.12.10", "3.11.0" }, List(Python, InstallFixtures.Python()));
    }

    [Fact]
    public void A_prefix_includes_pre_releases_of_that_line()
    {
        var row = Assert.Single(Available.List(Python, InstallFixtures.Python(), Architecture.X64, prefix: "3.15"));
        Assert.Equal("3.15.0rc2", row.Version);
        Assert.True(row.PreRelease);
    }

    [Fact]
    public void All_lists_everything_and_says_what_cant_be_installed()
    {
        var rows = Available.List(Python, InstallFixtures.Python(), Architecture.X64, all: true);

        Assert.Equal(new[] { "3.15.0rc2", "3.14.8", "3.13.16", "3.12.10", "3.12.9", "3.11.0", "3.10.11" }, rows.Select(r => r.Version));
        Assert.Equal("no checksum published", rows.Single(r => r.Version == "3.10.11").Unavailable);

        var node = Available.List(Node, InstallFixtures.Node(), Architecture.Arm64, all: true);
        Assert.Equal("no arm64 build", node.Single(r => r.Version == "18.20.8").Unavailable);
    }
}

public sealed class ToolIndexTests
{
    private static Func<Uri, CancellationToken, Task<string>> Pages(Dictionary<string, string> byUrl) =>
        (uri, _) => byUrl.TryGetValue(uri.ToString(), out var body)
            ? Task.FromResult(body)
            : throw new InvalidOperationException($"unexpected fetch: {uri}");

    private static string Page(string? next, params (string version, string arch)[] entries) =>
        "{ " + (next is null ? "" : $"\"next\": \"{next}\", ") + "\"versions\": [" + string.Join(",", entries.Select(e =>
            $"{{ \"id\": \"pythoncore-3.12-{e.arch}\", \"tag\": \"3.12-{e.arch}\", \"sort-version\": \"{e.version}\", " +
            $"\"url\": \"https://www.python.org/{e.version}-{e.arch}.zip\", \"hash\": {{ \"sha256\": \"{new string('a', 64)}\" }} }}")) + "] }";

    private const string First = "https://www.python.org/ftp/python/index-windows.json";

    [Fact]
    public async Task A_version_split_across_pages_is_merged()
    {
        var versions = await ToolIndex.LoadAsync(new PythonSource(), Pages(new()
        {
            [First] = Page("two.json", ("3.12.10", "64")),
            ["https://www.python.org/ftp/python/two.json"] = Page(null, ("3.12.10", "arm64"), ("3.12.9", "64")),
        }));

        Assert.Equal(new[] { "3.12.10", "3.12.9" }, versions.Select(v => v.Version));
        Assert.Equal(new[] { Architecture.X64, Architecture.Arm64 }, versions[0].Archives.Keys.Order());
    }

    [Theory]
    [InlineData("https://evil.example/index.json")]
    [InlineData("http://www.python.org/ftp/python/two.json")]
    [InlineData("//evil.example/two.json")]
    public async Task A_next_page_elsewhere_is_refused(string next)
    {
        var e = await Assert.ThrowsAsync<InstallException>(() =>
            ToolIndex.LoadAsync(new PythonSource(), Pages(new() { [First] = Page(next, ("3.12.10", "64")) })));
        Assert.Contains("points somewhere else", e.Message);
    }

    [Fact]
    public async Task A_page_that_links_back_is_refused()
    {
        var e = await Assert.ThrowsAsync<InstallException>(() => ToolIndex.LoadAsync(new PythonSource(), Pages(new()
        {
            [First] = Page("two.json", ("3.12.10", "64")),
            ["https://www.python.org/ftp/python/two.json"] = Page("index-windows.json", ("3.12.9", "64")),
        })));
        Assert.Contains("links back", e.Message);
    }

    [Fact]
    public async Task Endless_pages_stop_at_the_limit()
    {
        // Every page links to a new one.
        Func<Uri, CancellationToken, Task<string>> fetch = (uri, _) =>
            Task.FromResult(Page($"p{Guid.NewGuid():N}.json", ("3.12.10", "64")));
        var e = await Assert.ThrowsAsync<InstallException>(() => ToolIndex.LoadAsync(new PythonSource(), fetch));
        Assert.Contains($"more than {ToolIndex.MaxPages} pages", e.Message);
    }

    [Fact]
    public async Task A_page_that_is_not_an_index_names_the_page()
    {
        var e = await Assert.ThrowsAsync<InstallException>(() =>
            ToolIndex.LoadAsync(new NodeSource(), Pages(new() { ["https://nodejs.org/dist/index.json"] = "<html>captive portal</html>" })));
        Assert.Contains("https://nodejs.org/dist/index.json isn't the index tack expects", e.Message);
    }
}

public sealed class InstallBitsTests
{
    [Theory]
    [InlineData("20.11.1", true)]
    [InlineData("3.15.0rc2", true)]
    [InlineData("3.15.0a7", true)]
    [InlineData("24", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("v20.11.1", false)]
    [InlineData(@"..\..\x", false)]
    [InlineData("20.11.1/../x", false)]
    [InlineData("20.11.1\n", false)]
    [InlineData("20.11.1 ", false)]
    [InlineData("work", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_plain_versions_are_plain(string? version, bool plain) => Assert.Equal(plain, RemoteVersion.IsPlain(version));

    private const string Hash = "158f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541";

    [Fact]
    public void A_manifest_gives_the_hash_for_an_exact_name_with_either_line_ending()
    {
        string manifest = $"{new string('0', 64)}  node-v1-win-x64.zip.sig\r\n{Hash.ToUpperInvariant()}  node-v1-win-x64.zip\r\n";
        Assert.Equal(Hash, Checksums.FromManifest(manifest, "node-v1-win-x64.zip"));
        Assert.Equal(Hash, Checksums.FromManifest($"{Hash} *node-v1-win-x64.zip\n", "node-v1-win-x64.zip")); // binary-mode marker
        Assert.Null(Checksums.FromManifest(manifest, "node-v1-win-x64"));
        Assert.Null(Checksums.FromManifest(manifest, "NODE-V1-WIN-X64.ZIP"));
    }

    [Fact]
    public void A_manifest_listing_a_file_twice_with_different_hashes_gives_nothing()
    {
        string manifest = $"{Hash}  a.zip\n{new string('1', 64)}  a.zip\n";
        Assert.Null(Checksums.FromManifest(manifest, "a.zip"));
        Assert.Equal(Hash, Checksums.FromManifest($"{Hash}  a.zip\n{Hash}  a.zip\n", "a.zip"));
    }

    [Theory]
    [InlineData("abc  a.zip")]
    [InlineData("zz8f7685b44de51f6c0df1d153526cbcd3e1bc739a8dfc607721cef75de9e541  a.zip")]
    public void A_manifest_value_that_is_not_a_sha256_gives_nothing(string manifest) =>
        Assert.Null(Checksums.FromManifest(manifest, "a.zip"));

    [Fact]
    public void Sources_are_found_by_tool_name()
    {
        Assert.IsType<NodeSource>(ToolSources.Find("NODE"));
        Assert.IsType<PythonSource>(ToolSources.Find("python"));
        Assert.Null(ToolSources.Find("ruby"));
    }
}
