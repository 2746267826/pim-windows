using System.Collections;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
    public async Task Login_WhenDpapiProtectionFails_DoesNotWritePlaintextTokenFile()
    {
        var apiClient = new ApiClient(() => new LoginHandler());
        var authService = new AuthService(
            apiClient,
            _tokenPath,
            TimeProvider.System,
            isWindows: () => true,
            protectForCurrentUser: _ => throw new CryptographicException("injected DPAPI failure"));

        Assert.True(await authService.LoginAsync("audit", "secret"));
        Assert.Equal("dpapi-access-canary", authService.CurrentAccessToken);
        Assert.False(File.Exists(_tokenPath), "DPAPI 失败时不得退回明文 token.json");
    }

    private sealed class LoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"code":0,"message":"ok","data":{"accessToken":"dpapi-access-canary","refreshToken":"dpapi-refresh-canary","expiresAt":"2099-01-01T00:00:00+00:00","user":{"id":"u-1","username":"audit","displayName":"Audit","role":"user"}}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    [Fact]
    public async Task TryRestoreToken_WhenHistoricalPlaintextOnWindows_ReEncryptsWithDpapiAndOverwritesFile()
    {
        var apiClient = new ApiClient();
        var validJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "migrated-access-token",
            RefreshToken = "migrated-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            UserId = "user-migrated",
            Username = "migrated-user",
            DisplayName = "Migrated User"
        });
        await File.WriteAllTextAsync(_tokenPath, validJson);
        var originalBytes = await File.ReadAllBytesAsync(_tokenPath);

        byte[] fakeEncrypted = Encoding.UTF8.GetBytes("DPAPI-ENCRYPTED-PAYLOAD");
        var authService = new AuthService(
            apiClient,
            _tokenPath,
            TimeProvider.System,
            isWindows: () => true,
            protectForCurrentUser: raw => fakeEncrypted,
            unprotectForCurrentUser: raw =>
            {
                if (StructuralComparisons.StructuralEqualityComparer.Equals(raw, fakeEncrypted))
                    return Encoding.UTF8.GetBytes(validJson);
                throw new CryptographicException("unprotect failed on plaintext");
            });

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.Success, result);
        Assert.True(authService.IsAuthenticated);
        Assert.Equal("migrated-access-token", authService.CurrentAccessToken);

        // 核心安全断言：磁盘上的明文文件必须被一次性 DPAPI 密文覆盖，不能保留明文
        var onDiskBytes = await File.ReadAllBytesAsync(_tokenPath);
        Assert.False(StructuralComparisons.StructuralEqualityComparer.Equals(onDiskBytes, originalBytes), "历史明文文件不得在磁盘上保持未加密明文");
        Assert.True(StructuralComparisons.StructuralEqualityComparer.Equals(onDiskBytes, fakeEncrypted), "磁盘文件必须已被 DPAPI 密文覆盖");

        // 冷启动必须从最终 tokenPath 上的密文重新解密；不能只把迁移前解析出的明文对象返回给当前实例。
        var coldRestored = new AuthService(
            new ApiClient(),
            _tokenPath,
            TimeProvider.System,
            isWindows: () => true,
            protectForCurrentUser: _ => throw new Xunit.Sdk.XunitException("密文冷恢复不应再次调用 Protect"),
            unprotectForCurrentUser: raw =>
            {
                Assert.True(StructuralComparisons.StructuralEqualityComparer.Equals(raw, fakeEncrypted));
                return Encoding.UTF8.GetBytes(validJson);
            });
        var coldResult = await coldRestored.TryRestoreTokenDetailedAsync();
        Assert.Equal(TokenRestoreResult.Success, coldResult);
        Assert.Equal("migrated-access-token", coldRestored.CurrentAccessToken);
        Assert.Equal("migrated-refresh-token", PrivateField(coldRestored, "_refreshToken"));
        using var expected = System.Text.Json.JsonDocument.Parse(validJson);
        Assert.Equal(expected.RootElement.GetProperty("ExpiresAt").GetDateTimeOffset(), PrivateField(coldRestored, "_accessTokenExpiry"));
        Assert.Equal("user-migrated", coldRestored.CurrentUserId);
        Assert.Equal("migrated-user", coldRestored.CurrentUsername);
        Assert.Equal("Migrated User", coldRestored.CurrentDisplayName);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.migration.tmp"));
    }

    [Fact]
    public async Task TryRestoreToken_WhenHistoricalPlaintextOnWindowsAndReEncryptionFails_FailsClosed()
    {
        var apiClient = new ApiClient();
        var validJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "plain-access-token",
            RefreshToken = "plain-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            UserId = "user-plain",
            Username = "plain-user",
            DisplayName = "Plain User"
        });
        await File.WriteAllTextAsync(_tokenPath, validJson);
        var originalBytes = await File.ReadAllBytesAsync(_tokenPath);

        var authService = new AuthService(
            apiClient,
            _tokenPath,
            TimeProvider.System,
            isWindows: () => true,
            protectForCurrentUser: _ => throw new CryptographicException("DPAPI protect failure during migration"),
            unprotectForCurrentUser: _ => throw new CryptographicException("unprotect failed on plaintext"));

        var result = await authService.TryRestoreTokenDetailedAsync();

        // 核心安全断言：重加密失败时不得静默退回明文会话，必须 fail-closed
        Assert.NotEqual(TokenRestoreResult.Success, result);
        Assert.False(authService.IsAuthenticated);
        Assert.Null(authService.CurrentAccessToken);
        Assert.True(
            StructuralComparisons.StructuralEqualityComparer.Equals(
                originalBytes,
                await File.ReadAllBytesAsync(_tokenPath)),
            "Protect 失败不得修改或删除历史明文文件");
    }

    [Fact]
    public async Task TryRestoreToken_WhenMigratedCiphertextCannotBeReadBack_FailsClosedAndPreservesOriginalFile()
    {
        var validJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "readback-access-token",
            RefreshToken = "readback-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            UserId = "user-readback",
            Username = "readback-user",
            DisplayName = "Readback User"
        });
        await File.WriteAllTextAsync(_tokenPath, validJson);
        var originalBytes = await File.ReadAllBytesAsync(_tokenPath);
        var encryptedBytes = Encoding.UTF8.GetBytes("DPAPI-CIPHERTEXT-THAT-FAILS-READBACK");

        var authService = new AuthService(
            new ApiClient(),
            _tokenPath,
            TimeProvider.System,
            isWindows: () => true,
            protectForCurrentUser: _ => encryptedBytes,
            unprotectForCurrentUser: raw => throw new CryptographicException(
                StructuralComparisons.StructuralEqualityComparer.Equals(raw, originalBytes)
                    ? "historical plaintext is not DPAPI"
                    : "persisted ciphertext cannot be read back"));

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.NotEqual(TokenRestoreResult.Success, result);
        Assert.False(authService.IsAuthenticated);
        Assert.Null(authService.CurrentAccessToken);
        Assert.True(
            StructuralComparisons.StructuralEqualityComparer.Equals(
                originalBytes,
                await File.ReadAllBytesAsync(_tokenPath)),
            "密文读回验证失败时必须保留原文件，不能先覆盖再报失败");
    }

    [Fact]
    public async Task TryRestoreToken_WhenAtomicTempCannotBeCreated_FailsClosedAndPreservesOriginalFile()
    {
        if (OperatingSystem.IsWindows()) return;

        var validJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "write-access-token",
            RefreshToken = "write-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            UserId = "user-write",
            Username = "write-user",
            DisplayName = "Write User"
        });
        await File.WriteAllTextAsync(_tokenPath, validJson);
        var originalBytes = await File.ReadAllBytesAsync(_tokenPath);
        var originalMode = File.GetUnixFileMode(_tempDir);
        var cipher = Encoding.UTF8.GetBytes("DPAPI-CIPHERTEXT-WRITE-CONTROL");
        int protectCalls = 0, ciphertextReadbacks = 0;

        try
        {
            // 原文件仍可写，但目录禁止创建迁移临时文件：直接覆盖实现会假成功，原子迁移必须关闭。
            File.SetUnixFileMode(
                _tempDir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var authService = new AuthService(
                new ApiClient(),
                _tokenPath,
                TimeProvider.System,
                isWindows: () => true,
                protectForCurrentUser: _ => { protectCalls++; return cipher; },
                unprotectForCurrentUser: raw => {
                    if (raw.AsSpan().SequenceEqual(cipher)) {
                        ciphertextReadbacks++;
                        return Encoding.UTF8.GetBytes(validJson);
                    }
                    throw new CryptographicException("historical plaintext is not DPAPI");
                });

            var result = await authService.TryRestoreTokenDetailedAsync();

            Assert.NotEqual(TokenRestoreResult.Success, result);
            Assert.False(authService.IsAuthenticated);
            Assert.Null(authService.CurrentAccessToken);
            Assert.True(
                StructuralComparisons.StructuralEqualityComparer.Equals(
                    originalBytes,
                    await File.ReadAllBytesAsync(_tokenPath)),
                "原子写入准备失败时必须原样保留旧文件");
            Assert.Equal(1, protectCalls);
            Assert.Equal(0, ciphertextReadbacks); // Failure must occur before readback, whose cipher control is valid.
            Assert.Empty(Directory.GetFiles(_tempDir, "*.migration.tmp"));
        }
        finally
        {
            File.SetUnixFileMode(_tempDir, originalMode);
        }
    }

    private static object? PrivateField(AuthService service, string name)
        => typeof(AuthService).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service);

    [Theory]
    [InlineData("AccessToken")]
    [InlineData("RefreshToken")]
    [InlineData("ExpiresAt")]
    [InlineData("UserId")]
    [InlineData("Username")]
    [InlineData("DisplayName")]
    public async Task Migration_ReadbackFieldMismatch_PreservesOriginalAndNoSession(string field)
    {
        var expiry = TimeProvider.System.GetUtcNow().AddHours(2);
        var fields = new Dictionary<string, object> {
            ["AccessToken"] = "fixture-access", ["RefreshToken"] = "fixture-refresh",
            ["ExpiresAt"] = expiry, ["UserId"] = "fixture-id",
            ["Username"] = "fixture-name", ["DisplayName"] = "Fixture Name"
        };
        var original = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(fields));
        await File.WriteAllBytesAsync(_tokenPath, original);
        fields[field] = field == "ExpiresAt" ? expiry.AddMinutes(1) : "changed-field";
        var changed = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(fields));
        var cipher = Encoding.UTF8.GetBytes("FIXTURE-CIPHER");
        int cipherReads = 0;
        var auth = new AuthService(new ApiClient(), _tokenPath, TimeProvider.System,
            () => true, _ => cipher, raw => {
                if (raw.AsSpan().SequenceEqual(cipher)) { cipherReads++; return changed; }
                throw new CryptographicException("plaintext control");
            });
        Assert.NotEqual(TokenRestoreResult.Success, await auth.TryRestoreTokenDetailedAsync());
        Assert.False(auth.IsAuthenticated);
        Assert.Null(auth.CurrentAccessToken);
        Assert.Null(PrivateField(auth, "_refreshToken"));
        Assert.Equal(original, await File.ReadAllBytesAsync(_tokenPath));
        Assert.Equal(1, cipherReads);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.migration.tmp"));
    }

    [Fact]
    public async Task CipherColdRestore_RestoresAccessRefreshExpiryWithoutProtectOrWrite()
    {
        var expiry = TimeProvider.System.GetUtcNow().AddHours(2);
        var plain = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new {
            AccessToken = "cold-access", RefreshToken = "cold-refresh", ExpiresAt = expiry,
            UserId = "cold-id", Username = "cold-name", DisplayName = "Cold Name"
        }));
        var cipher = Encoding.UTF8.GetBytes("COLD-FIXTURE-CIPHER");
        await File.WriteAllBytesAsync(_tokenPath, cipher);
        var auth = new AuthService(new ApiClient(), _tokenPath, TimeProvider.System,
            () => true, _ => throw new Xunit.Sdk.XunitException("Unexpected Protect"), raw => {
                Assert.Equal(cipher, raw); return plain;
            });
        Assert.Equal(TokenRestoreResult.Success, await auth.TryRestoreTokenDetailedAsync());
        Assert.Equal("cold-access", auth.CurrentAccessToken);
        Assert.Equal("cold-refresh", PrivateField(auth, "_refreshToken"));
        Assert.Equal(expiry, PrivateField(auth, "_accessTokenExpiry"));
        Assert.Equal(cipher, await File.ReadAllBytesAsync(_tokenPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.migration.tmp"));
    }

    [Fact]
    public async Task Save_ProtectFailure_PreservesExistingHistoricalPlaintextWithoutOverwriting()
    {
        var original = Encoding.UTF8.GetBytes("synthetic-existing-user-data");
        await File.WriteAllBytesAsync(_tokenPath, original);
        var auth = new AuthService(new ApiClient(() => new LoginHandler()), _tokenPath,
            TimeProvider.System, () => true,
            _ => throw new CryptographicException("injected protect failure"));
        Assert.True(await auth.LoginAsync("audit", "secret"));
        Assert.Equal("dpapi-access-canary", auth.CurrentAccessToken);
        Assert.Equal(original, await File.ReadAllBytesAsync(_tokenPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.migration.tmp"));
    }
}
