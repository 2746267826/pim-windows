using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ArtifactDownloaderSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-downloader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task RejectsHttpRedirect_WithoutFollowingIt()
    {
        var handler = new RecordingHandler((_, _) => Redirect("http://example.com/model.bin"));
        var downloader = Create(handler, PublicResolver);

        var error = await AssertRejectedAsync(downloader, "https://example.com/model.bin");

        Assert.Contains("scheme-not-https", error.Message);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RejectsPrivateAddressRedirect_WithoutFollowingIt()
    {
        var handler = new RecordingHandler((_, _) => Redirect("https://10.0.0.5/model.bin"));
        var downloader = Create(handler, PublicResolver);

        var error = await AssertRejectedAsync(downloader, "https://example.com/model.bin");

        Assert.Contains("private-address", error.Message);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RejectsLocalhostSource_BeforeAnyRequest()
    {
        var handler = new RecordingHandler((_, _) => Content("model"));
        var downloader = Create(handler, PublicResolver);

        var error = await AssertRejectedAsync(downloader, "https://localhost/model.bin");

        Assert.Contains("local-or-private-host", error.Message);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task RejectsHostnameResolvingToPrivateAddress_BeforeAnyRequest()
    {
        var handler = new RecordingHandler((_, _) => Content("model"));
        var downloader = Create(
            handler,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") }));

        var error = await AssertRejectedAsync(downloader, "https://private.example/model.bin");

        Assert.Contains("private-address", error.Message);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task RejectsTooManyRedirects()
    {
        var handler = new RecordingHandler((_, _) => Redirect("https://example.org/next"));
        var downloader = Create(handler, PublicResolver);

        var error = await AssertRejectedAsync(downloader, "https://example.com/model.bin");

        Assert.Contains("too-many-redirects", error.Message);
        Assert.Equal(6, handler.Count);
    }

    [Fact]
    public async Task RejectsUserInfoInUrl()
    {
        var handler = new RecordingHandler((_, _) => Content("model"));
        var downloader = Create(handler, PublicResolver);

        var error = await AssertRejectedAsync(downloader, "https://user:pass@example.com/model.bin");

        Assert.Contains("userinfo-not-allowed", error.Message);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task FollowsPublicHttpsRedirect_AndSendsNoAuthorization()
    {
        var handler = new RecordingHandler((index, _) => index == 0
            ? Redirect("https://cdn.example.org/model.bin")
            : Content("model-bytes"));
        var downloader = Create(handler, PublicResolver);
        var destination = Path.Combine(_root, "model.bin");

        await downloader.DownloadAsync(
            new Uri("https://example.com/model.bin"),
            destination,
            1024,
            null,
            CancellationToken.None);

        Assert.Equal(2, handler.Count);
        Assert.Equal("model-bytes", await File.ReadAllTextAsync(destination));
        Assert.All(handler.Authorizations, value => Assert.Null(value));
    }

    private HttpArtifactDownloader Create(
        RecordingHandler handler,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver) =>
        new(
            new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            resolver);

    private async Task<InvalidOperationException> AssertRejectedAsync(
        HttpArtifactDownloader downloader,
        string source)
    {
        return await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(
                new Uri(source),
                Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin"),
                1024,
                null,
                CancellationToken.None));
    }

    private static Task<IPAddress[]> PublicResolver(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(location) },
        };

    private static HttpResponseMessage Content(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int Count { get; private set; }

        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            var response = _responder(Count++, request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
