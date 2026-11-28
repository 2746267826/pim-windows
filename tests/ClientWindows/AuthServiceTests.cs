using System.Net;
using System.Text;
using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class AuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tokenPath;

    public AuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pim-auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tokenPath = Path.Combine(_tempDir, "token.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TryRestoreToken_WhenNoFile_ReturnsNoSavedToken()
    {
        var apiClient = new ApiClient();
        var authService = new AuthService(apiClient, _tokenPath);

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.NoSavedToken, result);
        Assert.False(authService.IsAuthenticated);
        Assert.False(authService.HasSavedToken);
    }

    [Fact]
    public async Task TryRestoreToken_WhenTokenValid_ReturnsSuccess()
    {
        var apiClient = new ApiClient();
        var authService = new AuthService(apiClient, _tokenPath);

        // Pre-create valid persisted token
        var validJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "valid-access-token",
            RefreshToken = "valid-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            UserId = "user-123",
            Username = "testuser",
            DisplayName = "Test User"
        });
        await File.WriteAllTextAsync(_tokenPath, validJson);

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.Success, result);
        Assert.True(authService.IsAuthenticated);
        Assert.True(authService.HasSavedToken);
        Assert.Equal("testuser", authService.CurrentUsername);
        Assert.Equal("user-123", authService.CurrentUserId);
    }

    [Fact]
    public async Task TryRestoreToken_WhenTokenExpiredAndNetworkFails_ReturnsPendingNetworkAndKeepsCredentials()
    {
        var apiClient = new ApiClient();
        apiClient.SetBaseUrl("http://127.0.0.1:59999");

        var authService = new AuthService(apiClient, _tokenPath);

        var expiredJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "expired-access-token",
            RefreshToken = "my-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-2),
            UserId = "user-456",
            Username = "offline-user",
            DisplayName = "Offline User"
        });
        await File.WriteAllTextAsync(_tokenPath, expiredJson);

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.PendingNetwork, result);
        Assert.True(authService.HasSavedToken);
        Assert.False(authService.IsAuthenticated);
        Assert.Equal("offline-user", authService.CurrentUsername);
        Assert.Equal("user-456", authService.CurrentUserId);
        Assert.True(File.Exists(_tokenPath));
    }

    [Fact]
    public async Task ServerUrlChange_InvalidatesSessionAndPreventsOldRefreshFromFollowing()
    {
        var handler = new ServerSwitchHandler();
        var apiClient = new ApiClient(() => handler);
        apiClient.SetBaseUrl("https://pim.example.test");
        var authService = new AuthService(apiClient, _tokenPath);

        Assert.True(await authService.LoginAsync("server-switch-user", "synthetic-password"));
        Assert.True(File.Exists(_tokenPath));
        var scopeChanges = 0;
        apiClient.ExternalScopeChanged += () => scopeChanges++;
        var requestsBeforeSwitch = handler.Requests.Count;

        authService.ServerUrl = "https://other.example.test";

        Assert.False(authService.IsAuthenticated);
        Assert.Null(authService.CurrentUserId);
        Assert.Null(authService.CurrentUsername);
        Assert.Null(authService.CurrentAccessToken);
        Assert.False(authService.HasSavedToken);
        Assert.False(File.Exists(_tokenPath));
        Assert.Equal("https://other.example.test/api/v1", authService.ServerUrl);
        Assert.Equal(1, scopeChanges);

        Assert.False(await authService.RefreshAsync());
        Assert.Equal(requestsBeforeSwitch, handler.Requests.Count);
        Assert.Equal(0, handler.RefreshCalls);
        Assert.DoesNotContain(
            handler.Requests,
            uri => string.Equals(uri.Host, "other.example.test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ServerUrlChange_ForSameCanonicalDeploymentKeepsSession()
    {
        var handler = new ServerSwitchHandler("same-server-user");
        var apiClient = new ApiClient(() => handler);
        apiClient.SetBaseUrl("https://pim.example.test");
        var authService = new AuthService(apiClient, _tokenPath);
        Assert.True(await authService.LoginAsync("same-server-user", "synthetic-password"));
        var scopeChanges = 0;
        apiClient.ExternalScopeChanged += () => scopeChanges++;

        authService.ServerUrl = "https://pim.example.test/";

        Assert.True(authService.IsAuthenticated);
        Assert.Equal("same-server-user", authService.CurrentUsername);
        Assert.True(File.Exists(_tokenPath));
        Assert.Equal(0, scopeChanges);
    }

    private sealed class ServerSwitchHandler(string username = "server-switch-user") : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public int RefreshCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                RefreshCalls++;
            }

            return Task.FromResult(Json(
                $$"""
                {
                  "code": 0,
                  "message": "ok",
                  "timestamp": "2026-11-25T00:00:00Z",
                  "data": {
                    "accessToken": "synthetic-access-token",
                    "refreshToken": "synthetic-refresh-token",
                    "expiresAt": "{{DateTimeOffset.UtcNow.AddHours(2):O}}",
                    "user": {
                      "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                      "username": "{{username}}",
                      "displayName": "Server Switch User",
                      "role": "user"
                    }
                  }
                }
                """));
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }
}
