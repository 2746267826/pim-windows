using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// #15 凭据持久化安全测试。
/// 跨平台部分通过注入 <see cref="ITokenProtector"/> 模拟 DPAPI 故障；
/// Windows 真实 DPAPI 行为由下方 <c>WindowsOnly</c> 用例单独覆盖（本机 Windows 运行）。
/// 全部使用临时目录与虚构 token，不接触真实凭据。
/// </summary>
public sealed class AuthTokenStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tokenPath;

    public AuthTokenStorageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pim-token-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _tokenPath = Path.Combine(_dir, "token.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PersistedTokenShape ValidShape(DateTimeOffset? expiresAt = null) => new(
        "access-token-test",
        "refresh-token-test",
        expiresAt ?? DateTimeOffset.UtcNow.AddHours(2),
        "user-1",
        "testuser",
        "Test User");

    private static async Task WritePlaintextAsync(string path, PersistedTokenShape shape)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["AccessToken"] = shape.AccessToken,
            ["RefreshToken"] = shape.RefreshToken,
            ["ExpiresAt"] = shape.ExpiresAt,
            ["UserId"] = shape.UserId,
            ["Username"] = shape.Username,
            ["DisplayName"] = shape.DisplayName,
        });
        await File.WriteAllTextAsync(path, json);
    }

    [Fact]
    public async Task EncryptionFailure_DoesNotWritePlaintext_AndKeepsExistingFile()
    {
        var existing = Encoding.UTF8.GetBytes("existing-encrypted-bytes");
        await File.WriteAllBytesAsync(_tokenPath, existing);
        var protector = new StubProtector { FailProtect = true };
        var auth = new AuthService(new ApiClient(() => new LoginStubHandler()), _tokenPath, tokenProtector: protector);
        var failures = new List<TokenSaveStatus>();
        auth.TokenSaveFailed += failures.Add;

        var loginOk = await auth.LoginAsync("u", "p");

        Assert.True(loginOk);
        Assert.Equal(TokenSaveStatus.EncryptionFailed, auth.LastSaveStatus);
        Assert.Equal(new[] { TokenSaveStatus.EncryptionFailed }, failures);
        Assert.Equal(existing, await File.ReadAllBytesAsync(_tokenPath));
    }

    [Fact]
    public async Task EncryptionFailure_OnFreshLogin_LeavesNoTokenFile()
    {
        var protector = new StubProtector { FailProtect = true };
        var auth = new AuthService(new ApiClient(() => new LoginStubHandler()), _tokenPath, tokenProtector: protector);

        var loginOk = await auth.LoginAsync("u", "p");

        Assert.True(loginOk);
        Assert.Equal(TokenSaveStatus.EncryptionFailed, auth.LastSaveStatus);
        Assert.False(File.Exists(_tokenPath));
    }

    [Fact]
    public async Task HistoricalPlaintext_LoadsThenMigratesToProtectedAtomically()
    {
        var shape = ValidShape();
        await WritePlaintextAsync(_tokenPath, shape);
        var protector = new StubProtector();
        var auth = new AuthService(new ApiClient(), _tokenPath, tokenProtector: protector);

        var result = await auth.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.Success, result);
        Assert.Equal(TokenSaveStatus.SavedEncrypted, auth.LastSaveStatus);
        var persisted = await File.ReadAllBytesAsync(_tokenPath);
        // 密文形态（桩只模拟标头，明文本体仍可辨识；真实 DPAPI 泄漏断言在 WindowsOnly 用例里）。
        Assert.True(persisted.AsSpan(0, StubProtector.Marker.Length).SequenceEqual(StubProtector.Marker));
        Assert.False(File.Exists(_tokenPath + ".tmp"));
        Assert.Equal(shape.Username, auth.CurrentUsername);
    }

    [Fact]
    public async Task MigrationEncryptionFailure_KeepsPlaintextFile_AndReportsFailure()
    {
        var shape = ValidShape();
        var plaintext = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["AccessToken"] = shape.AccessToken,
            ["RefreshToken"] = shape.RefreshToken,
            ["ExpiresAt"] = shape.ExpiresAt,
            ["UserId"] = shape.UserId,
            ["Username"] = shape.Username,
            ["DisplayName"] = shape.DisplayName,
        });
        await File.WriteAllTextAsync(_tokenPath, plaintext);
        var protector = new StubProtector { FailProtect = true };
        protector.SecretInException = "fake-secret-do-not-log";
        var auth = new AuthService(new ApiClient(), _tokenPath, tokenProtector: protector);
        var failures = new List<TokenSaveStatus>();
        auth.TokenSaveFailed += failures.Add;

        var result = await auth.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.Success, result);
        Assert.Equal(TokenSaveStatus.EncryptionFailed, auth.LastSaveStatus);
        Assert.Equal(new[] { TokenSaveStatus.EncryptionFailed }, failures);
        Assert.Equal(plaintext, await File.ReadAllTextAsync(_tokenPath));
    }

    [Fact]
    public async Task CorruptedFile_FailsSafelyWithoutReportingSaveSuccess()
    {
        await File.WriteAllBytesAsync(_tokenPath, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        var protector = new StubProtector();
        var auth = new AuthService(new ApiClient(), _tokenPath, tokenProtector: protector);
        var failures = new List<TokenSaveStatus>();
        auth.TokenSaveFailed += failures.Add;

        var result = await auth.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.NoSavedToken, result);
        Assert.Equal(TokenSaveStatus.NotAttempted, auth.LastSaveStatus);
        Assert.Equal(8, (await File.ReadAllBytesAsync(_tokenPath)).Length);
    }

    [Fact]
    public async Task EncryptedFile_LoadsWithoutRewrite()
    {
        var protector = new StubProtector();
        var protectedBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["AccessToken"] = "enc-access",
            ["RefreshToken"] = "enc-refresh",
            ["ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1),
        }));
        await File.WriteAllBytesAsync(_tokenPath, protector.Protect(protectedBytes));
        var auth = new AuthService(new ApiClient(), _tokenPath, tokenProtector: protector);
        var protectCallsAtStart = protector.ProtectCalls;
        var failures = new List<TokenSaveStatus>();
        auth.TokenSaveFailed += failures.Add;

        var result = await auth.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.Success, result);
        Assert.Equal(TokenSaveStatus.NotAttempted, auth.LastSaveStatus);
        Assert.Equal(protectCallsAtStart, protector.ProtectCalls);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task NonWindowsPlaintextCompat_KeepsPlaintextSaveAndNoMigration()
    {
        var protector = new StubProtector { CanProtect = false, ShouldMigratePlaintext = false };
        var auth = new AuthService(new ApiClient(() => new LoginStubHandler()), _tokenPath, tokenProtector: protector);

        var loginOk = await auth.LoginAsync("u", "p");

        Assert.True(loginOk);
        Assert.Equal(TokenSaveStatus.SavedPlaintext, auth.LastSaveStatus);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_tokenPath));
        Assert.Equal("access-token-1", doc.RootElement.GetProperty("AccessToken").GetString());
    }

    public sealed class WindowsOnly
    {
        /// <summary>本机 Windows 真实 DPAPI 往返（Linux CI 上显示 skip，不伪造绿）。</summary>
        [WindowsFact]
        public async Task RealDpapi_LoginPersistsEncrypted_PlaintextMigratesToProtected()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pim-token-dpapi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var tokenPath = Path.Combine(dir, "token.json");
            try
            {
                var api = new ApiClient(() => new LoginStubHandler());
                var auth = new AuthService(api, tokenPath);
                Assert.True(await auth.LoginAsync("u", "p"));
                Assert.Equal(TokenSaveStatus.SavedEncrypted, auth.LastSaveStatus);
                var saved = await File.ReadAllBytesAsync(tokenPath);
                Assert.False(Contains(saved, "access-token-1"));

                var restore = new AuthService(new ApiClient(), tokenPath);
                Assert.Equal(TokenRestoreResult.Success, await restore.TryRestoreTokenDetailedAsync());
                Assert.Equal(TokenSaveStatus.NotAttempted, restore.LastSaveStatus);

                var shape = ValidShape();
                var json = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["AccessToken"] = shape.AccessToken,
                    ["RefreshToken"] = shape.RefreshToken,
                    ["ExpiresAt"] = shape.ExpiresAt,
                    ["UserId"] = shape.UserId,
                    ["Username"] = shape.Username,
                    ["DisplayName"] = shape.DisplayName,
                });
                await File.WriteAllTextAsync(tokenPath, json);
                var migrated = new AuthService(new ApiClient(), tokenPath);
                Assert.Equal(TokenRestoreResult.Success, await migrated.TryRestoreTokenDetailedAsync());
                Assert.Equal(TokenSaveStatus.SavedEncrypted, migrated.LastSaveStatus);
                var migratedBytes = await File.ReadAllBytesAsync(tokenPath);
                Assert.False(Contains(migratedBytes, "access-token-test"));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    private sealed record PersistedTokenShape(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string UserId,
        string Username,
        string DisplayName);

    private static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    /// <summary>非 Windows 上真实标记 skip（而不是空跑假绿）；Windows 上正常执行。</summary>
    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows-only: real DPAPI round-trip";
            }
        }
    }

    private sealed class StubProtector : ITokenProtector
    {
        public static readonly byte[] Marker = "protected:"u8.ToArray();

        public bool FailProtect { get; set; }
        public string? SecretInException { get; set; }
        public int ProtectCalls { get; private set; }
        public bool CanProtect { get; set; } = true;
        public bool ShouldMigratePlaintext { get; set; } = true;

        public byte[] Protect(byte[] plaintext)
        {
            ProtectCalls++;
            if (FailProtect) throw new InvalidOperationException($"simulated DPAPI failure: {SecretInException}");
            // 与 DPAPI 同构：密文里必须能还原出明文（否则密文加载用例测的是空转）。
            var cipher = new byte[Marker.Length + plaintext.Length];
            Marker.CopyTo(cipher, 0);
            plaintext.CopyTo(cipher, Marker.Length);
            return cipher;
        }

        public byte[] Unprotect(byte[] payload)
        {
            if (payload.Length > Marker.Length && payload.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            {
                return payload[Marker.Length..];
            }
            throw new CryptographyException();
        }
    }

    private sealed class CryptographyException : Exception { }

    /// <summary>仅 /auth/login 成功；续期/业务请求都会失败（本文件用例不触网）。</summary>
    private sealed class LoginStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["code"] = 0,
                ["message"] = "ok",
                ["timestamp"] = DateTimeOffset.UtcNow,
                ["data"] = new Dictionary<string, object?>
                {
                    ["accessToken"] = "access-token-1",
                    ["refreshToken"] = "refresh-token-1",
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(2),
                    ["user"] = new Dictionary<string, object?>
                    {
                        ["id"] = "user-1",
                        ["username"] = "testuser",
                        ["displayName"] = "Test User",
                        ["role"] = "user",
                    },
                },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
