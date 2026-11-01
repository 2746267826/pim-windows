using System.Net;
using System.Net.Sockets;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Dedicated HTTPS-only artifact downloader. It never carries API credentials,
/// disables implicit redirects by using a client whose handler does not auto-follow,
/// and manually validates every hop against <see cref="ArtifactUrlPolicy"/> before
/// sending a request. A manifest digest check still runs on the downloaded bytes.
/// </summary>
public sealed class HttpArtifactDownloader : IArtifactDownloader
{
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;

    public HttpArtifactDownloader(
        HttpClient httpClient,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        _httpClient = httpClient;
        _resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
    }

    /// <summary>Creates an isolated artifact client with no auto-redirects and no
    /// default authentication header.</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            // Pin the connection to an address that passed the same private-range
            // check as the URL, closing the resolve-then-connect rebinding window.
            ConnectCallback = ConnectValidatedAsync,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var address = addresses.FirstOrDefault(candidate => !ArtifactUrlPolicy.IsPrivateOrLocalAddress(candidate))
                      ?? throw new InvalidOperationException("artifact-url-rejected: private-address");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var current = source;
        var redirects = 0;
        while (true)
        {
            if (!ArtifactUrlPolicy.TryValidate(current, out var urlError))
            {
                throw new InvalidOperationException($"artifact-url-rejected: {urlError}");
            }

            await EnsurePublicHostAsync(current, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                redirects++;
                if (redirects > ArtifactUrlPolicy.MaxRedirects)
                {
                    throw new InvalidOperationException("artifact-url-rejected: too-many-redirects");
                }

                if (!ArtifactUrlPolicy.TryResolveRedirect(
                        current,
                        response.Headers.Location,
                        out var next,
                        out var redirectError))
                {
                    throw new InvalidOperationException($"artifact-url-rejected: {redirectError}");
                }

                current = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                throw new ArtifactSizeLimitExceededException(maxBytes);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);

            var buffer = new byte[1024 * 1024];
            var total = 0L;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new ArtifactSizeLimitExceededException(maxBytes);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(total);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.IdnHost, out _))
        {
            return; // literal addresses were already validated by the URL policy
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHost(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException("artifact-host-unresolved", ex);
        }

        if (addresses.Length == 0)
        {
            throw new InvalidOperationException("artifact-host-unresolved");
        }

        if (addresses.Any(ArtifactUrlPolicy.IsPrivateOrLocalAddress))
        {
            throw new InvalidOperationException("artifact-url-rejected: private-address");
        }
    }
}
