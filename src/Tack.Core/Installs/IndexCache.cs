using System.Security.Cryptography;
using System.Text;

namespace Tack.Core.Installs;

/// <summary>A source's versions, and how fresh they are.</summary>
/// <param name="CachedAt">When the oldest page used from the cache was fetched; null when every page was fetched now.</param>
/// <param name="Offline">Why the network wasn't used for some page (the cached copy stood in), or null.</param>
public sealed record IndexResult(IReadOnlyList<RemoteVersion> Versions, DateTimeOffset? CachedAt, string? Offline);

/// <summary>
/// Keeps each index page for <see cref="MaxAge"/>, so listing what's available or installing twice in a row doesn't
/// refetch Node's 330 KB index or Python's three pages. When a fetch fails and an older copy exists, the copy is used
/// and the result says so and how old it is. Pages are stored by a hash of their URL, written atomically.
/// </summary>
public sealed class IndexCache
{
    private readonly string _dir;
    private readonly Func<DateTimeOffset> _now;

    public IndexCache(string cacheDir, Func<DateTimeOffset>? now = null)
    {
        _dir = cacheDir;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static TimeSpan MaxAge { get; } = TimeSpan.FromHours(1);

    /// <param name="refresh">Fetch every page even if a fresh copy is cached (<c>--refresh</c>).</param>
    public async Task<IndexResult> LoadAsync(IToolSource source, Func<Uri, CancellationToken, Task<string>> fetch,
        bool refresh = false, CancellationToken cancel = default)
    {
        DateTimeOffset? oldest = null;
        string? offline = null;

        async Task<string> Cached(Uri url, CancellationToken ct)
        {
            string file = PathFor(source, url);
            DateTimeOffset? saved = File.Exists(file) ? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) : null;
            if (!refresh && saved is { } at && _now() - at < MaxAge)
            {
                Older(at);
                return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            try
            {
                string body = await fetch(url, ct).ConfigureAwait(false);
                Save(file, body);
                return body;
            }
            catch (InstallException e) when (saved is not null)
            {
                Older(saved.Value);
                offline ??= e.Message;
                return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
        }

        void Older(DateTimeOffset at)
        {
            if (oldest is null || at < oldest) oldest = at;
        }

        var versions = await ToolIndex.LoadAsync(source, Cached, cancel).ConfigureAwait(false);
        return new IndexResult(versions, oldest, offline);
    }

    private string PathFor(IToolSource source, Uri url)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToString())))[..16];
        return Path.Combine(_dir, $"{source.Tool}-{hash}.json");
    }

    // A cache that can't be written just means the next run fetches again.
    private void Save(string file, string body)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, body);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
