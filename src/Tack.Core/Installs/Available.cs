using System.Runtime.InteropServices;
using Tack.Core.Resolution;

namespace Tack.Core.Installs;

/// <summary>One row of <c>tack tool available</c>.</summary>
/// <param name="Unavailable">Why <c>tool install</c> would refuse it (no build for this architecture, no checksum),
/// or null when it can be installed. Only listed with <c>--all</c>.</param>
public sealed record AvailableVersion(string Version, string? Lts, bool PreRelease, string? Unavailable);

/// <summary>
/// What <c>tack tool available</c> lists, newest first. By default the newest installable release of each line
/// (<see cref="IToolSource.LineSegments"/>), so Node shows one per major and Python one per minor. A prefix lists every
/// installable match, pre-releases included since you asked for that line. <c>--all</c> lists everything, with
/// what can't be installed marked and why.
/// </summary>
public static class Available
{
    public static IReadOnlyList<AvailableVersion> List(IToolSource source, IReadOnlyList<RemoteVersion> versions,
        Architecture arch, string? prefix = null, bool all = false)
    {
        var sorted = versions.OrderByDescending(v => v.Version, VersionOrder.Comparer).Select(v => Row(source, v, arch));

        if (all)
            return sorted.ToList();

        if (prefix is { Length: > 0 } p)
        {
            p = p.Trim().ToLowerInvariant();
            if (p.Length > 1 && p[0] == 'v' && char.IsAsciiDigit(p[1])) p = p[1..];
            return sorted.Where(r => r.Unavailable is null && (r.Version == p || r.Version.StartsWith(p + ".", StringComparison.Ordinal)))
                .ToList();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return sorted.Where(r => r.Unavailable is null && !r.PreRelease && seen.Add(Line(r.Version, source.LineSegments))).ToList();
    }

    private static AvailableVersion Row(IToolSource source, RemoteVersion v, Architecture arch)
    {
        string? why = v.VerifiableArchive(arch) is not null ? null
            : v.Archives.ContainsKey(arch) ? "no checksum published"
            : $"no {VersionSpec.ArchName(arch)} build";
        return new AvailableVersion(v.Version, v.Lts, v.PreRelease, why);
    }

    private static string Line(string version, int segments) => string.Join('.', version.Split('.').Take(segments));
}
