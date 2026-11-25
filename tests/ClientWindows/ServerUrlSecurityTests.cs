using System.Net;
using System.Net.Http;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ServerUrlPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5858", "http://127.0.0.1:5858")]
    [InlineData("http://127.0.0.1:5858/", "http://127.0.0.1:5858")]
    [InlineData("http://localhost:5858", "http://127.0.0.1:5858")]
    [InlineData("localhost:5858", "http://127.0.0.1:5858")]
    [InlineData("127.0.0.1:5858", "http://127.0.0.1:5858")]
    [InlineData("http://[::1]:5858", "http://[::1]:5858")]
    [InlineData("[::1]:5858", "http://[::1]:5858")]
    [InlineData("https://pim.example.test", "https://pim.example.test")]
    [InlineData("pim.example.test", "https://pim.example.test")]
    [InlineData("https://PIM.EXAMPLE.TEST/", "https://pim.example.test")]
    [InlineData("https://pim.example.test:8443/tenant-a/", "https://pim.example.test:8443/tenant-a")]
    [InlineData("https://pim.example.test/tenant-a/api/v1", "https://pim.example.test/tenant-a")]
    [InlineData("http://127.0.0.1:5858/api/v1/", "http://127.0.0.1:5858")]
    public void AcceptsLocalHttpAndRemoteHttpsAndCanonicalizes(string input, string expected)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.True(result.IsValid, result.MessageZh);
        Assert.Equal(expected, result.NormalizedUrl);
    }

    [Theory]
    [InlineData("http://pim.example.test")]
    [InlineData("http://192.168.1.10:5858")]
    [InlineData("http://10.0.0.5:5858")]
    [InlineData("http://10.0.2.2:5858")]
    [InlineData("http://[2001:db8::1]:5858")]
    public void RejectsRemoteCleartextHttp(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(ServerUrlRejection.RemoteCleartextHttp, result.Rejection);
        Assert.Contains("HTTPS", result.MessageZh, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("密码", result.MessageZh, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://pim.example.test")]
    [InlineData("file:///C:/pim")]
    [InlineData("ws://pim.example.test")]
    public void RejectsNonHttpSchemes(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(ServerUrlRejection.UnsupportedScheme, result.Rejection);
    }

    [Theory]
    [InlineData("https://user:password@pim.example.test")]
    [InlineData("https://user@pim.example.test")]
    public void RejectsUserInfo(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(ServerUrlRejection.UserInfo, result.Rejection);
    }

    [Theory]
    [InlineData("https://pim.example.test/?tenant=other")]
    [InlineData("https://pim.example.test/path?tenant=other")]
    public void RejectsQuery(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(ServerUrlRejection.Query, result.Rejection);
    }

    [Theory]
    [InlineData("https://pim.example.test/#fragment")]
    [InlineData("https://pim.example.test/path#fragment")]
    public void RejectsFragment(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(ServerUrlRejection.Fragment, result.Rejection);
    }

    [Theory]
    [InlineData("https://pim.example.test/%2e%2e/secret")]
    [InlineData("https://pim.example.test/%252e%252e/secret")]
    [InlineData("https://pim.example.test/a%2fb")]
    [InlineData("https://pim.example.test/a%5cb")]
    [InlineData("https://pim.example.test/a%00b")]
    [InlineData("https://pim.example.test/a%3fb")]
    [InlineData("https://pim.example.test/a\\b")]
    public void RejectsAmbiguousPathEscapes(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.Equal(ServerUrlRejection.AmbiguousPath, result.Rejection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("http:/pim.example.test")]
    [InlineData("//pim.example.test")]
    [InlineData("/relative/path")]
    [InlineData("https://pim.example.test:0")]
    [InlineData("https://pim.example.test:70000")]
    [InlineData("https://pim.example.test/path with space")]
    [InlineData("https://pim.example.test/path\ninjected")]
    public void RejectsMalformedAddresses(string input)
    {
        var result = ServerUrlPolicy.Validate(input);

        Assert.False(result.IsValid);
        Assert.NotEqual(ServerUrlRejection.None, result.Rejection);
    }

    [Fact]
    public void EmptyAddressHasSpecificRejection()
    {
        Assert.Equal(ServerUrlRejection.Empty, ServerUrlPolicy.Validate("   ").Rejection);
    }

    [Fact]
    public void TryNormalizeReturnsFalseAndEmptyOutputOnRejection()
    {
        Assert.False(ServerUrlPolicy.TryNormalize(
            "http://pim.example.test",
            out var normalized,
            out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.Contains("HTTPS", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalDevelopmentDefaultIsAccepted()
    {
        var result = ServerUrlPolicy.Validate(ServerUrlPolicy.LocalDevelopmentUrl);

        Assert.True(result.IsValid, result.MessageZh);
        Assert.True(result.IsLoopback);
        Assert.False(result.IsHttps);
    }
}

public class ServerUrlSecurityTests
{
    [Fact]
    public void NormalizeServerUrl_RejectsRemoteCleartextHttp()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ApiClient.NormalizeServerUrl("http://192.168.1.10:5858"));

        Assert.Contains("HTTPS", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("密码", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetBaseUrl_RejectsRemoteCleartextHttpWithoutChangingCurrentDeployment()
    {
        var client = new ApiClient();
        var original = client.CurrentBaseUrl;

        Assert.Throws<ArgumentException>(() => client.SetBaseUrl("http://pim.example.test:5858"));

        Assert.Equal(original, client.CurrentBaseUrl);
    }

    [Fact]
    public void SetBaseUrl_StripsApiSuffixAndPreservesPathBase()
    {
        var client = new ApiClient();

        client.SetBaseUrl("https://api.example.test/tenant/api/v1/");

        Assert.Equal("https://api.example.test/tenant/api/v1", client.CurrentBaseUrl);
    }

    [Fact]
    public void SetBaseUrl_NoSchemeRemoteAddressUsesHttps()
    {
        var client = new ApiClient();

        client.SetBaseUrl("api.example.test:8443/tenant");

        Assert.Equal("https://api.example.test:8443/tenant/api/v1", client.CurrentBaseUrl);
    }

    [Fact]
    public void NormalizeServerUrl_RejectsUserInfoQueryAndFragment()
    {
        Assert.Throws<ArgumentException>(() =>
            ApiClient.NormalizeServerUrl("https://user:password@pim.example.test"));
        Assert.Throws<ArgumentException>(() =>
            ApiClient.NormalizeServerUrl("https://pim.example.test/?tenant=other"));
        Assert.Throws<ArgumentException>(() =>
            ApiClient.NormalizeServerUrl("https://pim.example.test/#fragment"));
    }

    [Fact]
    public async Task Login_RedirectResponseIsRejectedWithoutRetryOrSecondRequest()
    {
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://evil.example.test/steal") },
            });
        var client = new ApiClient(() => handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.LoginAsync(new LoginRequest
            {
                Username = "user",
                Password = "password",
            }));

        Assert.Contains("重定向", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void DefaultHandlerDisablesAutomaticRedirectsAndCookies()
    {
        using var handler = ApiClient.CreateDefaultHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
