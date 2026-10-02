using System.Runtime.InteropServices;

namespace Tack.Core.Installs;

/// <summary>
/// Where <c>tack tool install</c> gets one tool from. Everything here is pure: the caller fetches, the source parses
/// and plans, so every rule is tested against recorded indexes (tests/Tack.Tests/Fixtures/Installs) without a network.
/// </summary>
public interface IToolSource
{
    /// <summary>The tool name versions are registered under, e.g. <c>node</c>.</summary>
    string Tool { get; }

    /// <summary>Who publishes the builds, for messages and the install receipt, e.g. <c>nodejs.org</c>.</summary>
    string Publisher { get; }

    /// <summary>Whether <c>lts</c> means anything for this tool.</summary>
    bool HasLts { get; }

    /// <summary>The index's first page.</summary>
    Uri IndexUrl { get; }

    /// <summary>One page of the index: its versions, in whatever order the page lists them, and the next page's
    /// address as the page gives it (relative or absolute), or null on the last page.</summary>
    IReadOnlyList<RemoteVersion> ParsePage(string body, out string? next);

    /// <summary>The plan for installing <paramref name="version"/>'s <paramref name="arch"/> build. When the
    /// version has a <see cref="RemoteVersion.ChecksumsUrl"/>, <paramref name="checksums"/> is that file's text.</summary>
    InstallPlan Plan(RemoteVersion version, Architecture arch, string? checksums);
}

/// <summary>The sources tack can install from. Anything else is registered with <c>tack tool add</c>.</summary>
public static class ToolSources
{
    public static IReadOnlyList<IToolSource> All { get; } = new IToolSource[] { new NodeSource(), new PythonSource() };

    public static IToolSource? Find(string tool) =>
        All.FirstOrDefault(s => string.Equals(s.Tool, tool, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads a source's whole index, following its pages.</summary>
public static class ToolIndex
{
    /// <summary>More pages than any real index has; a source that goes past it is broken, not big.</summary>
    public const int MaxPages = 10;

    /// <summary>
    /// Every version on every page. A <c>next</c> link must stay on HTTPS and on the index's own host, so a page
    /// can't send tack somewhere else, and a page seen twice or more than <see cref="MaxPages"/> pages is an error.
    /// A version listed on more than one page is one version: its first listing, plus any architecture's build
    /// that only a later page has.
    /// </summary>
    public static async Task<IReadOnlyList<RemoteVersion>> LoadAsync(IToolSource source,
        Func<Uri, CancellationToken, Task<string>> fetch, CancellationToken cancel = default)
    {
        var versions = new List<RemoteVersion>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenPages = new HashSet<Uri>();
        Uri? page = source.IndexUrl;
        while (page is not null)
        {
            if (!seenPages.Add(page))
                throw new InstallException($"{source.Publisher}'s index links back to a page it already listed ({page}).");
            if (seenPages.Count > MaxPages)
                throw new InstallException($"{source.Publisher}'s index has more than {MaxPages} pages, so tack stopped reading it.");

            string body = await fetch(page, cancel).ConfigureAwait(false);
            IReadOnlyList<RemoteVersion> parsed;
            string? next;
            try { parsed = source.ParsePage(body, out next); }
            catch (Exception e) when (e is not InstallException)
            {
                throw new InstallException($"{page} isn't the index tack expects: {e.Message}", e);
            }

            foreach (var v in parsed)
            {
                if (!byName.TryGetValue(v.Version, out int at))
                {
                    byName[v.Version] = versions.Count;
                    versions.Add(v);
                }
                else if (v.Archives.Keys.Except(versions[at].Archives.Keys).Any())
                {
                    versions[at] = Merge(versions[at], v);
                }
            }
            page = next is null ? null : NextPage(source, page, next);
        }
        return versions;
    }

    private static RemoteVersion Merge(RemoteVersion first, RemoteVersion later)
    {
        var archives = new Dictionary<System.Runtime.InteropServices.Architecture, RemoteArchive>(first.Archives);
        foreach (var (arch, archive) in later.Archives) archives.TryAdd(arch, archive);
        return new RemoteVersion
        {
            Version = first.Version,
            Lts = first.Lts,
            PreRelease = first.PreRelease,
            Archives = archives,
            ChecksumsUrl = first.ChecksumsUrl,
        };
    }

    private static Uri NextPage(IToolSource source, Uri current, string next)
    {
        if (!Uri.TryCreate(current, next, out var uri))
            throw new InstallException($"{source.Publisher}'s index names a next page tack can't read: '{next}'.");
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, source.IndexUrl.Host, StringComparison.OrdinalIgnoreCase))
            throw new InstallException($"{source.Publisher}'s index points somewhere else for its next page ({uri}), so tack stopped reading it.");
        return uri;
    }
}
