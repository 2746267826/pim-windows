namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// HTTPS-only artifact downloader. It never follows the caller-supplied URL as an
/// arbitrary local path and aborts as soon as the declared byte budget is exceeded.
/// </summary>
public sealed class HttpArtifactDownloader : IArtifactDownloader
{
    private readonly HttpClient _httpClient;

    public HttpArtifactDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        long maxBytes,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("artifact source must be https");
        }

        using var response = await _httpClient
            .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            throw new ArtifactSizeLimitExceededException(maxBytes);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
    }
}
