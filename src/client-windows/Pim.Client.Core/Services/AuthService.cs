using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Models;
using Pim.Client.Core.Security;

namespace Pim.Client.Core.Services;

public class AuthService
{
    private readonly ApiClient _apiClient;
    private readonly string _tokenPath;
    private readonly string _tokenDir;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _isWindows;
    private readonly Func<byte[], byte[]> _protectForCurrentUser;
    private readonly Func<byte[], byte[]> _unprotectForCurrentUser;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessTokenExpiry;

    /// <summary>
    /// 审计 F-11（规格 §6.2）：宿主会话结束时通知当前嵌入 WebView。
    /// 页面/宿主会话代码只据此发一帧 <c>host.session.ended</c>；事件本身不携带令牌。
    /// </summary>
    public event Action<HostSessionEndReason>? LoggedOut;

    /// <summary>
    /// 到期前续期的提前量（WO-ISSUES-396-400-20261007 REQ-5 / issue #400）。
    /// access token 有效期约 15 分钟，客户端循环最快 30 秒一跑，
    /// 提前 2 分钟续期保证在令牌失效**之前**就换好新令牌，不再制造 401。
    /// </summary>
    internal static readonly TimeSpan AccessTokenRenewalSkew = TimeSpan.FromMinutes(2);

    /// <summary>续期单飞：并发到达的多个请求共享同一次刷新，不重复打 /auth/refresh。</summary>
    private readonly object _refreshSync = new();
    private Task<bool>? _refreshInFlight;

    public AuthService(ApiClient apiClient, string? tokenPath = null, TimeProvider? timeProvider = null)
        : this(
            apiClient,
            tokenPath,
            timeProvider,
            OperatingSystem.IsWindows,
            ProtectForCurrentWindowsUser,
            UnprotectForCurrentWindowsUser)
    {
    }

    internal AuthService(
        ApiClient apiClient,
        string? tokenPath,
        TimeProvider? timeProvider,
        Func<bool> isWindows,
        Func<byte[], byte[]> protectForCurrentUser,
        Func<byte[], byte[]>? unprotectForCurrentUser = null)
    {
        _apiClient = apiClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isWindows = isWindows;
        _protectForCurrentUser = protectForCurrentUser;
        _unprotectForCurrentUser = unprotectForCurrentUser ?? UnprotectForCurrentWindowsUser;
        _tokenPath = tokenPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PIM", "token.json");
        _tokenDir = Path.GetDirectoryName(_tokenPath) ?? "";
        _apiClient.RequestTiming += (desc, ms) =>
            System.Diagnostics.Debug.WriteLine($"[ApiTiming] {desc} took {ms}ms");
    }

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) &&
                                    _timeProvider.GetUtcNow() < _accessTokenExpiry;

    /// <summary>
    /// 是否需要在**下一次请求之前**主动续期（已过期或进入 <see cref="AccessTokenRenewalSkew"/> 窗口）。
    /// 没有 access token（尚未登录）或没有 refresh token（无法续期）时为 false —— 此时不该拦截请求。
    /// </summary>
    public bool IsAccessTokenRenewalDue =>
        !string.IsNullOrEmpty(_accessToken) &&
        !string.IsNullOrEmpty(_refreshToken) &&
        _timeProvider.GetUtcNow() >= _accessTokenExpiry - AccessTokenRenewalSkew;

    public bool HasSavedToken => File.Exists(_tokenPath);
    public bool IsTokenExpired => !string.IsNullOrEmpty(_accessToken) && _timeProvider.GetUtcNow() >= _accessTokenExpiry;

    public string? CurrentUserId { get; private set; }
    public string? CurrentUsername { get; private set; }
    public string? CurrentDisplayName { get; private set; }
    public string? CurrentAccessToken => _accessToken;

    public string ServerUrl
    {
        get => _apiClient.CurrentBaseUrl;
        set => _apiClient.SetBaseUrl(value);
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        var result = await _apiClient.PostAsync<ApiResponse<AuthResponse>>("/auth/login",
            new { username, password });

        if (result?.Data is null) return false;

        ApplyTokens(result.Data);
        SaveToken();
        return true;
    }

    public async Task<TokenRestoreResult> TryRestoreTokenDetailedAsync()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return TokenRestoreResult.NoSavedToken;

            var data = await LoadPersistedTokenAsync(_tokenPath, _isWindows, _protectForCurrentUser, _unprotectForCurrentUser);
            if (data is null) return TokenRestoreResult.NoSavedToken;

            _accessToken = data.AccessToken;
            _refreshToken = data.RefreshToken;
            _accessTokenExpiry = data.ExpiresAt;
            CurrentUserId = data.UserId;
            CurrentUsername = data.Username;
            CurrentDisplayName = data.DisplayName;

            if (!string.IsNullOrEmpty(_accessToken))
            {
                _apiClient.SetAccessToken(_accessToken);
            }
            _apiClient.RenewTokenAsync = RefreshAsync;
            _apiClient.IsTokenRenewalDue = () => IsAccessTokenRenewalDue;

            // If token not expired, restored successfully
            if (IsAuthenticated)
                return TokenRestoreResult.Success;

            // If token expired, try refresh
            if (!string.IsNullOrEmpty(_refreshToken))
            {
                try
                {
                    var refreshed = await RefreshAsync();
                    if (refreshed)
                    {
                        return TokenRestoreResult.Success;
                    }
                    if (!HasSavedToken || string.IsNullOrEmpty(_refreshToken))
                    {
                        return TokenRestoreResult.InvalidCredentials;
                    }
                    return TokenRestoreResult.PendingNetwork;
                }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Logout();
                    return TokenRestoreResult.InvalidCredentials;
                }
                catch
                {
                    return TokenRestoreResult.PendingNetwork;
                }
            }

            return TokenRestoreResult.NoSavedToken;
        }
        catch
        {
            return TokenRestoreResult.NoSavedToken;
        }
    }

    private void SaveToken()
    {
        try
        {
            if (!string.IsNullOrEmpty(_tokenDir))
            {
                Directory.CreateDirectory(_tokenDir);
            }
            var data = new PersistedToken
            {
                AccessToken = _accessToken!,
                RefreshToken = _refreshToken!,
                ExpiresAt = _accessTokenExpiry,
                UserId = CurrentUserId,
                Username = CurrentUsername,
                DisplayName = CurrentDisplayName
            };
            var json = JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            byte[] toWrite;
            if (_isWindows())
            {
                try
                {
                    toWrite = _protectForCurrentUser(bytes);
                }
                catch
                {
                    // Windows 凭据只允许写 DPAPI CurrentUser 密文。保护失败时保留内存会话，
                    // 但不创建可被同机其他进程直接读取的明文 token.json。
                    return;
                }
            }
            else
            {
                // Non-Windows fallback: plaintext (encrypted storage unavailable)
                toWrite = bytes;
            }
            File.WriteAllBytes(_tokenPath, toWrite);
        }
        catch
        {
            // Best-effort save — don't crash if disk is full
        }
    }

    private static byte[] ProtectForCurrentWindowsUser(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    }

    private static byte[] UnprotectForCurrentWindowsUser(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    }

    private async Task<PersistedToken?> LoadPersistedTokenAsync(
        string tokenPath,
        Func<bool> isWindows,
        Func<byte[], byte[]> protectForCurrentUser,
        Func<byte[], byte[]> unprotectForCurrentUser)
    {
        var raw = await File.ReadAllBytesAsync(tokenPath);
        if (isWindows())
        {
            try
            {
                var unprotected = unprotectForCurrentUser(raw);
                var json = Encoding.UTF8.GetString(unprotected);
                var data = JsonSerializer.Deserialize<PersistedToken>(json);
                if (data is not null) return data;
            }
            catch
            {
                // Fall through to historical plaintext migration attempt
            }

            try
            {
                var json = Encoding.UTF8.GetString(raw);
                var data = JsonSerializer.Deserialize<PersistedToken>(json);
                if (data is not null)
                {
                    // 历史明文安全迁移：优先保体验的一次性 DPAPI 重新加密再加载；
                    // 仅在密文写入同目录临时文件、从该文件解密读回并原子替换成功后才恢复会话。
                    var encryptedBytes = protectForCurrentUser(raw);
                    if (encryptedBytes.AsSpan().SequenceEqual(raw)) return null;

                    var tokenDirectory = Path.GetDirectoryName(tokenPath) ?? string.Empty;
                    var tempPath = Path.Combine(
                        tokenDirectory,
                        $".{Path.GetFileName(tokenPath)}.{Guid.NewGuid():N}.migration.tmp");

                    try
                    {
                        await using (var stream = new FileStream(
                            tempPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 4096,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            await stream.WriteAsync(encryptedBytes);
                            await stream.FlushAsync();
                            stream.Flush(flushToDisk: true);
                        }

                        var persistedEncryptedBytes = await File.ReadAllBytesAsync(tempPath);
                        if (!persistedEncryptedBytes.AsSpan().SequenceEqual(encryptedBytes)) return null;

                        var persistedPlaintextBytes = unprotectForCurrentUser(persistedEncryptedBytes);
                        var persistedData = JsonSerializer.Deserialize<PersistedToken>(
                            Encoding.UTF8.GetString(persistedPlaintextBytes));
                        if (persistedData is null || !SamePersistedToken(data, persistedData)) return null;

                        // File.Replace keeps the original destination intact unless the same-volume replacement succeeds.
                        File.Replace(tempPath, tokenPath, destinationBackupFileName: null);
                        return persistedData;
                    }
                    finally
                    {
                        try { File.Delete(tempPath); } catch { }
                    }
                }
            }
            catch
            {
                // 迁移重加密失败或非法数据：fail-closed
                return null;
            }
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(raw);
            return JsonSerializer.Deserialize<PersistedToken>(json);
        }
        catch
        {
            return null;
        }
    }

    private static bool SamePersistedToken(PersistedToken expected, PersistedToken actual)
        => string.Equals(expected.AccessToken, actual.AccessToken, StringComparison.Ordinal)
            && string.Equals(expected.RefreshToken, actual.RefreshToken, StringComparison.Ordinal)
            && expected.ExpiresAt.Equals(actual.ExpiresAt)
            && string.Equals(expected.UserId, actual.UserId, StringComparison.Ordinal)
            && string.Equals(expected.Username, actual.Username, StringComparison.Ordinal)
            && string.Equals(expected.DisplayName, actual.DisplayName, StringComparison.Ordinal);

    private void ApplyTokens(AuthResponse data)
    {
        _accessToken = data.AccessToken;
        _refreshToken = data.RefreshToken;
        _accessTokenExpiry = data.ExpiresAt;

        if (data.UserInfo is not null)
        {
            CurrentUserId = data.UserInfo.Id;
            CurrentUsername = data.UserInfo.Username;
            CurrentDisplayName = data.UserInfo.DisplayName;
        }

        _apiClient.SetAccessToken(_accessToken);
        _apiClient.RenewTokenAsync = RefreshAsync;
        _apiClient.IsTokenRenewalDue = () => IsAccessTokenRenewalDue;
    }

    /// <summary>
    /// 续期（主动到期前 / 401 被动共用）。并发调用共享同一次刷新：
    /// 否则一次令牌过期会同时触发多个 /auth/refresh，旧 refresh token 轮换后其余请求全部失败。
    /// </summary>
    public Task<bool> RefreshAsync()
    {
        lock (_refreshSync)
        {
            if (_refreshInFlight is { IsCompleted: false })
            {
                return _refreshInFlight;
            }

            _refreshInFlight = RefreshCoreAsync();
            return _refreshInFlight;
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken)) return false;

        try
        {
            // 走"不触发续期"的那条路径：续期接口自己再触发续期会自等待（见 ApiClient.PostWithoutTokenRenewalAsync）。
            var result = await _apiClient.PostWithoutTokenRenewalAsync<ApiResponse<AuthResponse>>("/auth/refresh",
                new { refreshToken = _refreshToken });

            if (result?.Data is null) return false;

            _accessToken = result.Data.AccessToken;
            _refreshToken = result.Data.RefreshToken;
            _accessTokenExpiry = result.Data.ExpiresAt;

            _apiClient.SetAccessToken(_accessToken);
            SaveToken();
            return true;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Logout();
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthService] Refresh failed due to transient/network error: {ex.Message}");
            return false;
        }
    }

    public void Logout(HostSessionEndReason reason = HostSessionEndReason.Expired)
    {
        _accessToken = null;
        _refreshToken = null;
        CurrentUserId = null;
        CurrentUsername = null;
        CurrentDisplayName = null;
        _apiClient.ClearAccessToken();
        try { File.Delete(_tokenPath); } catch { }

        // 审计 F-11（规格 §6.2）：宿主会话结束必须能通知当前嵌入 WebView。
        // 事件在令牌已清空之后触发，处理器只发一帧 host.session.ended，不接触令牌。
        var handlers = LoggedOut;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<HostSessionEndReason>)handler)(reason);
            }
            catch (Exception ex)
            {
                // 通知失败不得影响退出本身（例如窗口/Dispatcher 已关闭）。
                System.Diagnostics.Debug.WriteLine($"[AuthService] LoggedOut handler failed: {ex.Message}");
            }
        }
    }

    private class PersistedToken
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
        public string? UserId { get; set; }
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
    }
}
