using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tack.Core.Installs;

/// <summary>
/// Python from python.org's install-manager index (see docs/m10-spike-findings.md, "Python"): pages of
/// <c>{ "versions": [...], "next": "..." }</c>, unsorted. tack reads only <c>pythoncore-</c> entries (not the
/// embeddable or test-suite zips), only the standard builds (not free-threaded <c>3.13t</c>) and only 64-bit x86 and
/// arm64. Entries from 3.10 down point at NuGet packages with no hash; they're kept, without one, so asking for one
/// says why tack won't install it. Each zip is flat, with no pip launchers, so the plan creates them after placing it.
/// </summary>
public sealed partial class PythonSource : IToolSource
{
    public string Tool => "python";
    public string Publisher => "python.org";
    public bool HasLts => false;
    public int LineSegments => 2;
    public Uri IndexUrl { get; } = new("https://www.python.org/ftp/python/index-windows.json");

    // No pythonw: the shim is a console program, so a pythonw shim would open the console window pythonw exists to
    // avoid. pythonw.exe is still there in the install for anything that wants it by path.
    private static readonly string[] Exposes = { "python", "pip", "pip3" };

    public IReadOnlyCollection<string> NotCommands { get; } = new[] { "pythonw" };

    // pip's launchers are made in the final folder, offline from the wheel the zip ships, and isolated from the
    // caller's PYTHON*/PIP_* variables and pip config (PIP_REQUIRE_VIRTUALENV would otherwise refuse).
    private static readonly PostInstallCommand PipLaunchers = new("python.exe",
        new[]
        {
            "-I", "-m", "pip", "--isolated", "install", "--force-reinstall", "--no-index", "--no-deps",
            "--disable-pip-version-check", "--no-warn-script-location", "--quiet",
            "--find-links", @"Lib\ensurepip\_bundled", "pip",
        },
        "creating pip's launchers");

    public IReadOnlyList<RemoteVersion> ParsePage(string body, out string? next)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s
            ? s : null;

        var byVersion = new Dictionary<string, Dictionary<Architecture, RemoteArchive>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var entry in root.GetProperty("versions").EnumerateArray())
        {
            // The id, not the tag: a pre-release's tag is 3.15-dev-64, but its id is pythoncore-3.15-64 like any other.
            if (Str(entry, "id") is not { } id || CoreId().Match(id) is not { Success: true } m) continue;
            if (m.Groups["t"].Length > 0) continue; // free-threaded
            Architecture? arch = m.Groups["arch"].Value switch { "64" => Architecture.X64, "arm64" => Architecture.Arm64, _ => null };
            if (arch is null) continue;

            string? version = Str(entry, "sort-version");
            if (!RemoteVersion.IsPlain(version)) continue;
            if (!Uri.TryCreate(Str(entry, "url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) continue;
            string? sha = entry.TryGetProperty("hash", out var h) ? Checksums.Sha256(Str(h, "sha256")) : null;

            if (!byVersion.TryGetValue(version!, out var archives))
            {
                byVersion[version!] = archives = new Dictionary<Architecture, RemoteArchive>();
                order.Add(version!);
            }
            archives.TryAdd(arch.Value, new RemoteArchive { Url = url, Sha256 = sha });
        }

        return order.Select(v => new RemoteVersion
        {
            Version = v,
            PreRelease = v.Any(char.IsAsciiLetter),
            Archives = byVersion[v],
        }).ToList();
    }

    public InstallPlan Plan(RemoteVersion version, Architecture arch, string? checksums)
    {
        if (!version.Archives.TryGetValue(arch, out var archive))
            throw new InstallException($"python {version.Version} has no Windows {VersionSpec.ArchName(arch)} build.");
        if (archive.Sha256 is null)
            throw new InstallException(VersionSpec.Unverifiable(this, version.Version));

        return new InstallPlan
        {
            Tool = Tool,
            Version = version.Version,
            Source = Publisher,
            Url = archive.Url,
            Sha256 = archive.Sha256,
            TopFolder = null,
            Exposes = Exposes,
            ExtraBinDirs = new[] { "Scripts" },
            PostInstall = new[] { PipLaunchers },
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // pythoncore-3.13-64, pythoncore-3.13t-arm64. Not pythonembed-* or pythontest-*.
    [GeneratedRegex(@"^pythoncore-\d+\.\d+(?<t>t?)-(?<arch>32|64|arm64)\z")]
    private static partial Regex CoreId();
}
