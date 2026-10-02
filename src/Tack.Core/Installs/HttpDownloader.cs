using System.Net;

namespace Tack.Core.Installs;

/// <summary>How the installer reaches the network. Injected, so the engine is tested without one.</summary>
public interface IDownloader
{
    /// <summary>A small text resource: an index page, a checksum manifest.</summary>
    Task<string> GetStringAsync(Uri url, CancellationToken cancel);

    /// <summary>Stream <paramref name="url"/> into a new file at <paramref name="path"/>.</summary>
    Task DownloadFileAsync(Uri url, string path, IProgress<DownloadProgress>? progress, CancellationToken cancel);
}

/// <summary>Bytes received so far, and the total when the server said.</summary>
public readonly record struct DownloadProgress(long Received, long? Total);

/// <summary>
/// The real <see cref="IDownloader"/>: HTTPS only (I3). Redirects are followed by hand rather than by the handler, so
/// every hop is checked, and one to anything but HTTPS stops the download. Proxy settings are the system's (I15).
/// Every failure is an <see cref="InstallException"/> naming the URL.
/// </summary>
public sealed class HttpDownloader : IDownloader, IDisposable
{
    public const int MaxRedirects = 5;

    private readonly HttpClient _http;

    /// <param name="handler">For tests; the default is a handler with redirects off and the system proxy.</param>
    public HttpDownloader(string userAgent, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken cancel)
    {
        using var response = await SendAsync(url, cancel).ConfigureAwait(false);
        try { return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw Unreachable(url, e); }
    }

    public async Task DownloadFileAsync(Uri url, string path, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        using var response = await SendAsync(url, cancel).ConfigureAwait(false);
        long? total = response.Content.Headers.ContentLength;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long received = 0;
            progress?.Report(new DownloadProgress(0, total));
            int read;
            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
        }
        catch (HttpRequestException e) { throw Unreachable(url, e); }
        catch (IOException e) when (e is not FileNotFoundException) { throw new InstallException($"the download from {url} broke off: {e.Message}", e); }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri original, CancellationToken cancel)
    {
        RequireHttps(original, original);
        Uri url = original;
        for (int hops = 0; ; hops++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url),
                    HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException e) { throw Unreachable(url, e); }
            catch (TaskCanceledException e) when (!cancel.IsCancellationRequested)
            {
                throw new InstallException($"{url.Host} didn't answer in time ({url}). Check your connection or proxy.", e);
            }

            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hops == MaxRedirects)
                    throw new InstallException($"{original} redirected more than {MaxRedirects} times, so tack stopped.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                RequireHttps(original, url);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                string reason = response.ReasonPhrase is { Length: > 0 } r ? $" {r}" : "";
                response.Dispose();
                throw new InstallException($"{url} answered {status}{reason}.");
            }
            return response;
        }
    }

    private static void RequireHttps(Uri original, Uri url)
    {
        if (url.Scheme == Uri.UriSchemeHttps) return;
        throw new InstallException(url == original
            ? $"tack only downloads over HTTPS, and {url} isn't."
            : $"{original} redirected to {url}, which isn't HTTPS, so tack stopped.");
    }

    private static InstallException Unreachable(Uri url, Exception e) =>
        new($"couldn't download {url}: {e.Message} Check your connection or proxy.", e);

    public void Dispose() => _http.Dispose();
}
