using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tack.Core.Installs;

/// <summary>One version a source offers, as read from its index (see <see cref="IToolSource"/>).</summary>
public sealed partial class RemoteVersion
{
    /// <summary>
    /// Whether an index's version string is safe to use as a version name and a folder name: dotted numbers with
    /// an optional pre-release tag on the end (<c>20.11.1</c>, <c>3.15.0rc2</c>). Sources skip anything else, so an
    /// index can't name a version like <c>..\..\x</c> and have it become a path under <c>installs\</c>.
    /// </summary>
    public static bool IsPlain(string? version) => version is not null && Plain().IsMatch(version);

    [GeneratedRegex(@"^\d{1,9}(\.\d{1,9}){0,3}([a-z]{1,5}\d{0,9})?\z")]
    private static partial Regex Plain();

    /// <summary>The version as tack registers it: <c>20.11.1</c>, <c>3.15.0rc2</c> (never Node's leading <c>v</c>).</summary>
    public required string Version { get; init; }

    /// <summary>The LTS line's name (<c>Krypton</c>) when this is an LTS release, otherwise null.</summary>
    public string? Lts { get; init; }

    /// <summary>An alpha, beta or release candidate. Only installed when named exactly.</summary>
    public bool PreRelease { get; init; }

    /// <summary>The Windows builds of this version, by architecture. A version with none isn't listed at all.</summary>
    public required IReadOnlyDictionary<Architecture, RemoteArchive> Archives { get; init; }

    /// <summary>Where the archives' SHA-256 values are published, when the index doesn't carry them (Node's
    /// <c>SHASUMS256.txt</c>). Null when the index does, or when nothing publishes them.</summary>
    public Uri? ChecksumsUrl { get; init; }

    /// <summary>The build for <paramref name="arch"/> if there is one tack can check (I3), otherwise null.</summary>
    public RemoteArchive? VerifiableArchive(Architecture arch) =>
        Archives.TryGetValue(arch, out var a) && (a.Sha256 is not null || ChecksumsUrl is not null) ? a : null;

    public override string ToString() => Version;
}

/// <summary>One downloadable build of a <see cref="RemoteVersion"/>.</summary>
public sealed class RemoteArchive
{
    public required Uri Url { get; init; }

    /// <summary>Lowercase hex, when the index publishes it; null when it comes from
    /// <see cref="RemoteVersion.ChecksumsUrl"/> or isn't published at all.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>Everything the installer needs to put one version on disk, worked out before anything is downloaded.</summary>
public sealed class InstallPlan
{
    /// <summary>The tool name it's registered as, e.g. <c>node</c>.</summary>
    public required string Tool { get; init; }

    public required string Version { get; init; }

    /// <summary>The source that produced it, recorded in the install receipt, e.g. <c>nodejs.org</c>.</summary>
    public required string Source { get; init; }

    public required Uri Url { get; init; }

    /// <summary>The value the downloaded archive's SHA-256 must equal (lowercase hex).</summary>
    public required string Sha256 { get; init; }

    /// <summary>The archive's single top-level folder (<c>node-v20.11.1-win-x64</c>), which is what's moved into
    /// place; null when the archive is flat (Python), so the whole unpacked folder is.</summary>
    public string? TopFolder { get; init; }

    /// <summary>The commands this version may expose. The installer keeps those that exist once it's placed (I4).</summary>
    public required IReadOnlyList<string> Exposes { get; init; }

    /// <summary>Folders under the install, relative to it, searched after it for commands (<c>Scripts</c>).</summary>
    public IReadOnlyList<string> ExtraBinDirs { get; init; } = Array.Empty<string>();

    /// <summary>Commands run in the install's final folder after it's placed, in order (I10).</summary>
    public IReadOnlyList<PostInstallCommand> PostInstall { get; init; } = Array.Empty<PostInstallCommand>();
}

/// <summary>A program inside the install (<see cref="Exe"/>, relative to it) run with <see cref="Args"/>, with the
/// install's folder as the working directory. It must exit 0, or the install fails.</summary>
public sealed record PostInstallCommand(string Exe, IReadOnlyList<string> Args, string Description);

/// <summary>A source, index or version problem, with a message meant for the user.</summary>
public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);
