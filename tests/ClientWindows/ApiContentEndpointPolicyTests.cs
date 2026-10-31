using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ApiContentEndpointPolicyTests
{
    [Fact]
    public void ContentEndpoint_AllowsOnlyRelativeSameOriginPath()
    {
        var policy = new ApiContentEndpointPolicy("https://api.example.test:9443");

        Assert.True(policy.TryResolveContentEndpoint(
            "/api/v1/asr/segments/0",
            out var resolved,
            out var error), error);
        Assert.Equal(
            "https://api.example.test:9443/api/v1/asr/segments/0",
            resolved.AbsoluteUri);

        Assert.False(policy.TryResolveContentEndpoint(
            "https://evil.example/audio",
            out _,
            out _));
        Assert.False(policy.TryResolveContentEndpoint(
            "https://api.example.test:9443/api/v1/asr/segments/0",
            out _,
            out _));
        Assert.False(policy.TryResolveContentEndpoint("//evil.example/audio", out _, out _));
        Assert.False(policy.TryResolveContentEndpoint(
            "http://api.example.test:9443/audio",
            out _,
            out _));
    }

    [Theory]
    [InlineData("/api/../secret")]
    [InlineData("/api/%2e%2e/secret")]
    [InlineData("/api/%252e%252e/secret")]
    [InlineData("/api/%25252e%25252e/secret")]
    [InlineData("/api/%2f..%2fsecret")]
    [InlineData("/api/%5csegment")]
    [InlineData("/api/segment%3Ftoken=leak")]
    [InlineData("/api/%250d%250asegment")]
    [InlineData("api/segment")]
    [InlineData("/api/segment?token=leak")]
    [InlineData("/api/segment#fragment")]
    [InlineData("/api\\segment")]
    public void ContentEndpoint_RejectsAmbiguousOrUnsafePaths(string endpoint)
    {
        var policy = new ApiContentEndpointPolicy("https://api.example.test");

        Assert.False(policy.TryResolveContentEndpoint(endpoint, out _, out _));
    }

    [Fact]
    public void Redirect_AllowsOnlySameOrigin()
    {
        var policy = new ApiContentEndpointPolicy("https://api.example.test");
        var current = new Uri("https://api.example.test/api/asr/segments/0");

        Assert.True(policy.TryResolveRedirect(
            current,
            new Uri("/api/asr/content/0", UriKind.Relative),
            out var sameOrigin,
            out var sameOriginError), sameOriginError);
        Assert.Equal(
            "https://api.example.test/api/asr/content/0",
            sameOrigin.AbsoluteUri);

        Assert.False(policy.TryResolveRedirect(
            current,
            new Uri("https://evil.example/audio"),
            out _,
            out _));
        Assert.False(policy.TryResolveRedirect(
            current,
            new Uri("http://api.example.test/audio"),
            out _,
            out _));
        Assert.False(policy.TryResolveRedirect(
            current,
            new Uri("/audio?token=leak", UriKind.Relative),
            out _,
            out _));
        Assert.False(policy.TryResolveRedirect(
            current,
            new Uri("/api/%252e%252e/audio", UriKind.Relative),
            out _,
            out _));
    }

    [Fact]
    public void ApiOrigin_RejectsPathAndUserInfo()
    {
        Assert.Throws<ArgumentException>(() =>
            new ApiContentEndpointPolicy("https://api.example.test/base"));
        Assert.Throws<ArgumentException>(() =>
            new ApiContentEndpointPolicy("https://user@api.example.test"));
    }
}
