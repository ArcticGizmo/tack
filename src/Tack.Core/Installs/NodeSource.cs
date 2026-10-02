using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tack.Core.Installs;

/// <summary>
/// Node from nodejs.org (see docs/m10-spike-findings.md, "Node"). The index is one array, newest first, where
/// <c>lts</c> is <c>false</c> or the line's name and <c>files</c> says which builds exist. The archives' hashes are
/// in each version's <c>SHASUMS256.txt</c>. Each zip holds one folder, <c>node-v&lt;version&gt;-win-&lt;arch&gt;</c>.
/// </summary>
public sealed class NodeSource : IToolSource
{
    public string Tool => "node";
    public string Publisher => "nodejs.org";
    public bool HasLts => true;
    public Uri IndexUrl { get; } = new("https://nodejs.org/dist/index.json");

    // corepack is gone by Node 26; the installer drops names that don't exist. install_tools.bat and nodevars.bat
    // are deliberately left out.
    private static readonly string[] Exposes = { "node", "npm", "npx", "corepack" };

    private static readonly (Architecture Arch, string Name)[] Arches =
    {
        (Architecture.X64, "x64"),
        (Architecture.Arm64, "arm64"),
    };

    public IReadOnlyList<RemoteVersion> ParsePage(string body, out string? next)
    {
        next = null;
        using var doc = JsonDocument.Parse(body);
        var versions = new List<RemoteVersion>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("version", out var v) || v.GetString() is not { Length: > 1 } tagged || tagged[0] != 'v')
                continue;
            string version = tagged[1..];
            if (!RemoteVersion.IsPlain(version)) continue;

            var files = entry.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string?>();
            var archives = new Dictionary<Architecture, RemoteArchive>();
            foreach (var (arch, name) in Arches)
                if (files.Contains($"win-{name}-zip"))
                    archives[arch] = new RemoteArchive { Url = new Uri(IndexUrl, $"v{version}/{FileName(version, name)}") };
            if (archives.Count == 0) continue;

            versions.Add(new RemoteVersion
            {
                Version = version,
                Lts = entry.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.String ? lts.GetString() : null,
                Archives = archives,
                ChecksumsUrl = new Uri(IndexUrl, $"v{version}/SHASUMS256.txt"),
            });
        }
        return versions;
    }

    public InstallPlan Plan(RemoteVersion version, Architecture arch, string? checksums)
    {
        string? name = Arches.FirstOrDefault(a => a.Arch == arch).Name;
        if (name is null)
            throw new InstallException($"nodejs.org has no Windows {VersionSpec.ArchName(arch)} build of node.");
        if (!version.Archives.TryGetValue(arch, out var archive))
            throw new InstallException($"node {version.Version} has no Windows {name} build.");
        if (checksums is null)
            throw new InstallException($"node {version.Version}: tack needs {version.ChecksumsUrl} to check the download.");

        string file = FileName(version.Version, name);
        string sha = Checksums.FromManifest(checksums, file)
            ?? throw new InstallException($"node {version.Version}: {version.ChecksumsUrl} has no usable SHA-256 for {file}, so tack won't install it.");

        return new InstallPlan
        {
            Tool = Tool,
            Version = version.Version,
            Source = Publisher,
            Url = archive.Url,
            Sha256 = sha,
            TopFolder = Path.GetFileNameWithoutExtension(file),
            Exposes = Exposes,
        };
    }

    private static string FileName(string version, string arch) => $"node-v{version}-win-{arch}.zip";
}
