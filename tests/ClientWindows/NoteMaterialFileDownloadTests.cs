using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class NoteMaterialFileDownloadTests
{
    private static readonly Guid VersionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Download_RequiresExactHeadersAndReturnsOwnedBoundedStream()
    {
        var payload = "{\"schemaVersion\":1}"u8.ToArray();
        var content = new TrackingContent(payload);
        var handler = new StubHandler(() => NoteMaterialResponse(content, payload));
        var api = new ApiClient(() => handler);

        await using var download = await api.DownloadNoteMaterialFileAsync(
            VersionId,
            3,
            maxContentBytes: payload.Length);

        var actual = Assert.IsType<NoteMaterialFileDownload>(download);
        Assert.Equal("note.json", actual.Headers.FileName);
        Assert.Equal("private, no-store", actual.Headers.CacheControl);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            actual.Headers.Sha256);
        await using var copy = new MemoryStream();
        await actual.Content.CopyToAsync(copy);
        Assert.Equal(payload, copy.ToArray());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"/api/v1/native-notes/versions/{VersionId:D}/files/3",
            request.Path);

        await actual.DisposeAsync();
        Assert.True(content.Disposed);
    }

    [Theory]
    [InlineData("missing-sha")]
    [InlineData("uppercase-sha")]
    [InlineData("unsafe-filename")]
    [InlineData("cache-control")]
    [InlineData("nosniff")]
    [InlineData("duplicate-sha")]
    public async Task Download_RejectsInvalidSecurityHeaders(string defect)
    {
        var payload = "material"u8.ToArray();
        var content = new TrackingContent(payload);
        var handler = new StubHandler(() =>
        {
            var response = NoteMaterialResponse(content, payload);
            if (defect == "missing-sha")
            {
                response.Headers.Remove("X-Content-SHA256");
            }
            else if (defect == "uppercase-sha")
            {
                response.Headers.Remove("X-Content-SHA256");
                response.Headers.TryAddWithoutValidation(
                    "X-Content-SHA256",
                    Convert.ToHexString(SHA256.HashData(payload)));
            }
            else if (defect == "unsafe-filename")
            {
                content.Headers.ContentDisposition =
                    new ContentDispositionHeaderValue("attachment")
                    {
                        FileName = "../note.json",
                    };
            }
            else if (defect == "cache-control")
            {
                response.Headers.Remove("Cache-Control");
                response.Headers.TryAddWithoutValidation("Cache-Control", "public");
            }
            else if (defect == "nosniff")
            {
                response.Headers.Remove("X-Content-Type-Options");
                response.Headers.TryAddWithoutValidation(
                    "X-Content-Type-Options",
                    "sniff");
            }
            else if (defect == "duplicate-sha")
            {
                content.Headers.TryAddWithoutValidation(
                    "X-Content-SHA256",
                    Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
            }

            return response;
        });
        var api = new ApiClient(() => handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            api.DownloadNoteMaterialFileAsync(VersionId, 0, maxContentBytes: 1024));

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Download_RejectsWrongMediaTypeAndDisposesResponse()
    {
        var payload = "material"u8.ToArray();
        var content = new TrackingContent(payload);
        var handler = new StubHandler(() =>
        {
            var response = NoteMaterialResponse(content, payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return response;
        });
        var api = new ApiClient(() => handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            api.DownloadNoteMaterialFileAsync(VersionId, 0, maxContentBytes: 1024));

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Download_RejectsDeclaredLengthAboveBudgetBeforeReading()
    {
        var payload = new byte[16];
        var content = new TrackingContent(payload);
        var handler = new StubHandler(() => NoteMaterialResponse(content, payload));
        var api = new ApiClient(() => handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            api.DownloadNoteMaterialFileAsync(VersionId, 0, maxContentBytes: 8));

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Download_RejectsChunkedBodyAboveBudgetWhileStreaming()
    {
        var payload = new byte[32];
        var content = new UnknownLengthContent(payload);
        var handler = new StubHandler(() => NoteMaterialResponse(content, payload));
        var api = new ApiClient(() => handler);

        await using var download = await api.DownloadNoteMaterialFileAsync(
            VersionId,
            0,
            maxContentBytes: 8);
        var actual = Assert.IsType<NoteMaterialFileDownload>(download);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            actual.Content.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Download_RejectsDuplicateServerAndContentHeaders()
    {
        var payload = "material"u8.ToArray();
        var content = new TrackingContent(payload);
        var handler = new StubHandler(() =>
        {
            var response = NoteMaterialResponse(content, payload);
            content.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
            return response;
        });
        var api = new ApiClient(() => handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            api.DownloadNoteMaterialFileAsync(VersionId, 0, maxContentBytes: 1024));

        Assert.True(content.Disposed);
    }

    private static HttpResponseMessage NoteMaterialResponse(
        HttpContent content,
        byte[] payload)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment")
            {
                FileName = "note.json",
            };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "private, no-store");
        response.Headers.TryAddWithoutValidation(
            "X-Content-SHA256",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        return response;
    }

    private sealed class StubHandler(Func<HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(responseFactory());
        }
    }

    private class TrackingContent(byte[] payload) : ByteArrayContent(payload)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthContent : TrackingContent
    {
        private readonly byte[] _payload;

        public UnknownLengthContent(byte[] payload)
            : base(payload)
        {
            _payload = payload;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
