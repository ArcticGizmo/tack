using System.Runtime.InteropServices;
using Tack.Core.Resolution;

namespace Tack.Core.Installs;

/// <summary>
/// Turns what you type after <c>tool install node@</c> into one concrete version (I2, I13): an exact version, a
/// dotted prefix (<c>20</c>, <c>3.12</c>, never matching <c>3.121</c>), <c>latest</c>, or <c>lts</c> where the tool has
/// LTS releases. Prefixes and aliases pick the newest release, by <see cref="VersionOrder"/>, that has a build for
/// this architecture that tack can check; pre-releases only when named exactly. Every failure is an
/// <see cref="InstallException"/> that says what to do instead.
/// </summary>
public static class VersionSpec
{
    public const string Latest = "latest";
    public const string Lts = "lts";

    public static RemoteVersion Resolve(IToolSource source, IReadOnlyList<RemoteVersion> available, string spec, Architecture arch)
    {
        string s = spec.Trim().ToLowerInvariant(); // versions are case-insensitive: 3.15.0RC2 is 3.15.0rc2
        if (s.Length > 1 && s[0] == 'v' && char.IsAsciiDigit(s[1])) s = s[1..]; // node@v20

        if (s.Equals(Latest, StringComparison.OrdinalIgnoreCase))
            return Newest(source, available.Where(v => !v.PreRelease), arch, $"{source.Tool}@{Latest}");

        if (s.Equals(Lts, StringComparison.OrdinalIgnoreCase))
        {
            if (!source.HasLts)
                throw new InstallException($"{source.Tool} has no LTS releases. Use {source.Tool}@{Latest}, or a version such as {source.Tool}@{Example(available)}.");
            return Newest(source, available.Where(v => v.Lts is not null && !v.PreRelease), arch, $"{source.Tool}@{Lts}");
        }

        if (!RemoteVersion.IsPlain(s))
        {
            string example = Example(available);
            throw new InstallException($"'{spec}' isn't a version. Use one such as {source.Tool}@{example}, " +
                $"a prefix such as {source.Tool}@{example.Split('.')[0]}, or {source.Tool}@{Latest}" +
                (source.HasLts ? $" or {source.Tool}@{Lts}." : "."));
        }

        var exact = available.FirstOrDefault(v => string.Equals(v.Version, s, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (exact.VerifiableArchive(arch) is not null) return exact;
            throw exact.Archives.ContainsKey(arch)
                ? new InstallException(Unverifiable(source, exact.Version))
                : new InstallException(NoBuild(source, exact, arch));
        }

        var matching = available.Where(v => v.Version.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
            throw new InstallException(NotAvailable(source, available, s));

        var releases = matching.Where(v => !v.PreRelease).ToList();
        if (releases.Count == 0)
        {
            var newestPre = matching.OrderDescending(ByVersion).First();
            throw new InstallException($"only pre-releases of {source.Tool} {s} are available (the newest is {newestPre.Version}). " +
                $"tack installs one only when it's named exactly: {source.Tool}@{newestPre.Version}.");
        }
        return Newest(source, releases, arch, $"{source.Tool}@{s}");
    }

    /// <summary>Why a version whose build isn't published with a checksum isn't installed (I3).</summary>
    public static string Unverifiable(IToolSource source, string version) =>
        $"{source.Publisher} publishes {source.Tool} {version} without a checksum, so tack won't install it. " +
        $"Install it yourself and register it with tack tool add.";

    public static string ArchName(Architecture arch) => arch switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => arch.ToString().ToLowerInvariant(),
    };

    private static readonly IComparer<RemoteVersion> ByVersion =
        Comparer<RemoteVersion>.Create((a, b) => VersionOrder.Compare(a.Version, b.Version));

    // The newest candidate tack can install here; otherwise the most useful reason it can't.
    private static RemoteVersion Newest(IToolSource source, IEnumerable<RemoteVersion> candidates, Architecture arch, string asked)
    {
        var sorted = candidates.OrderDescending(ByVersion).ToList();
        if (sorted.FirstOrDefault(v => v.VerifiableArchive(arch) is not null) is { } best) return best;
        if (sorted.FirstOrDefault(v => v.Archives.ContainsKey(arch)) is { } unchecked_)
            throw new InstallException(Unverifiable(source, unchecked_.Version));
        if (sorted.Count == 0)
            throw new InstallException($"{source.Publisher} lists nothing for {asked}.");
        throw new InstallException($"{source.Publisher} has no Windows {ArchName(arch)} build for {asked} " +
            $"(the newest match, {sorted[0].Version}, is built for {BuiltFor(sorted[0])}).");
    }

    private static string NoBuild(IToolSource source, RemoteVersion version, Architecture arch) =>
        $"{source.Tool} {version.Version} has no Windows {ArchName(arch)} build on {source.Publisher} (it's built for {BuiltFor(version)}).";

    private static string BuiltFor(RemoteVersion v) => string.Join(" and ", v.Archives.Keys.Order().Select(ArchName));

    // Not listed at all. Python's security-only releases (3.12.12) have no Windows build, so point at the newest
    // release in the nearest line that has one.
    private static string NotAvailable(IToolSource source, IReadOnlyList<RemoteVersion> available, string s)
    {
        string message = $"{source.Publisher} has no Windows build of {source.Tool} {s}.";
        for (string line = Line(s); line.Length > 0 && line != s; s = line, line = Line(line))
        {
            var newest = available.Where(v => !v.PreRelease && v.Version.StartsWith(line + ".", StringComparison.OrdinalIgnoreCase))
                .OrderDescending(ByVersion).FirstOrDefault();
            if (newest is not null)
                return $"{message} The newest {source.Tool} {line} it has is {newest.Version}: {source.Tool}@{newest.Version}.";
        }
        return message;
    }

    // "3.12.10" -> "3.12"; "20" -> "".
    private static string Line(string version)
    {
        int dot = version.LastIndexOf('.');
        return dot < 0 ? "" : version[..dot];
    }

    // The newest release, as an example of a version to type.
    private static string Example(IReadOnlyList<RemoteVersion> available) =>
        available.Where(v => !v.PreRelease).OrderDescending(ByVersion).FirstOrDefault()?.Version ?? "1.2.3";
}
