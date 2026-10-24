using System.Text;
using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models;

namespace Pim.Client.Core.Services;

public class AuthService
{
    private readonly ApiClient _apiClient;
    private readonly string _tokenPath;
    private readonly string _tokenDir;
    private readonly TimeProvider _timeProvider;
    private readonly ITokenProtector _protector;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _accessTokenExpiry;

    /// <summary>写 token.json 全程互斥：登录、续期、明文迁移并发时仍保证「先加密、再原子替换」。</summary>
    private readonly object _saveLock = new();

    /// <summary>会话代数：登录/登出都会换代，旧会话的迟到续期不得再写内存或覆盖凭据文件。</summary>
    private int _sessionEpoch;

    /// <summary>
    /// 最近一次持久化结果（登录/续期/明文迁移共用）。应用层用它把「未加密/未持久化」如实暴露出来，
    /// 不把失败静默成成功。错误消息不包含 token 或 JSON 内容。
    /// </summary>
    public TokenSaveStatus LastSaveStatus { get; private set; } = TokenSaveStatus.NotAttempted;

    /// <summary>任何一次持久化失败（含后台续期/迁移）都会触发；参数只有状态码，不含 token/异常细节。</summary>
    public event Action<TokenSaveStatus>? TokenSaveFailed;

    /// <summary>
    /// 到期前续期的提前量（WO-ISSUES-396-400-20261007 REQ-5 / issue #400）。
    /// access token 有效期约 15 分钟，客户端循环最快 30 秒一跑，
    /// 提前 2 分钟续期保证在令牌失效**之前**就换好新令牌，不再制造 401。
    /// </summary>
    internal static readonly TimeSpan AccessTokenRenewalSkew = TimeSpan.FromMinutes(2);

    /// <summary>续期单飞：并发到达的多个请求共享同一次刷新，不重复打 /auth/refresh。</summary>
    private readonly object _refreshSync = new();
    private Task<bool>? _refreshInFlight;

    public AuthService(
        ApiClient apiClient,
        string? tokenPath = null,
        TimeProvider? timeProvider = null,
        ITokenProtector? tokenProtector = null)
    {
        _apiClient = apiClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _protector = tokenProtector ?? PlatformTokenProtector.Instance;
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
        var result = await _apiClient.LoginAsync(new LoginRequest
        {
            Username = username,
            Password = password,
        });

        if (result?.Data is null) return false;

        ApplyTokensAndSave(result.Data);
        return true;
    }

    public async Task<string?> RegisterAsync(
        string username, string email, string password, string? displayName)
    {
        var result = await _apiClient.RegisterAsync(new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = password,
            DisplayName = displayName,
        });

        if (result is { Code: 0, Data: not null })
        {
            ApplyTokensAndSave(result.Data);
            return null;
        }

        return result is null
            ? "服务器无响应"
            : $"错误码 {result.Code}: {result.Message}";
    }

    public async Task<TokenRestoreResult> TryRestoreTokenDetailedAsync()
    {
        try
        {
            if (!File.Exists(_tokenPath)) return TokenRestoreResult.NoSavedToken;

            var load = await LoadPersistedTokenAsync(_tokenPath);
            if (load.Data is null) return TokenRestoreResult.NoSavedToken;
            var data = load.Data;

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

            // 历史（或非 Windows）明文成功加载后立即安全迁移：内存里已是本次会话的凭据，
            // 复用同一条「加密 + 临时文件 + 原子替换」路径。失败不删原文件、不影响本次恢复。
            var loadEpoch = Volatile.Read(ref _sessionEpoch);
            if (load.WasPlaintext && _protector.ShouldMigratePlaintext)
            {
                TokenSaveStatus? migrationFailure;
                lock (_saveLock)
                {
                    if (Volatile.Read(ref _sessionEpoch) != loadEpoch)
                    {
                        migrationFailure = null;
                    }
                    else
                    {
                        migrationFailure = SaveTokenLocked(loadEpoch);
                    }
                }
                if (migrationFailure is { } mf) TokenSaveFailed?.Invoke(mf);
            }

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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthService] Token restore failed: {ex.GetType().Name}");
            return TokenRestoreResult.NoSavedToken;
        }
    }

    public async Task<bool> TryRestoreTokenAsync()
    {
        var result = await TryRestoreTokenDetailedAsync();
        return result == TokenRestoreResult.Success;
    }

    /// <summary>
    /// 在已持有 <see cref="_saveLock"/> 的前提下落盘；epoch 不匹配直接放弃（旧会话迟到写）。
    /// 返回失败状态（null=成功/跳过），事件由调用方退出临界区后触发。
    /// </summary>
    private TokenSaveStatus? SaveTokenLocked(int epoch)
    {
        TokenSaveStatus? failure = null;
        // 调用方必须已持有 _saveLock：加密、写盘与会话状态变更在同一临界区内，
        // 并发登录/续期/登出不会交错出「半新半旧」或「登出后旧会话写回」。
            if (Volatile.Read(ref _sessionEpoch) != epoch) return null;
        try
        {
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
            if (!_protector.CanProtect)
            {
                // Non-Windows fallback: plaintext (encrypted storage unavailable)
                WriteTokenFileAtomically(bytes);
                LastSaveStatus = TokenSaveStatus.SavedPlaintext;
                return null;
            }

            // Windows：加密失败绝不回落明文（原有行为是把原始 JSON 写进 token.json）。
            byte[]? toWrite = null;
            try
            {
                toWrite = _protector.Protect(bytes);
            }
            catch (Exception ex)
            {
                LastSaveStatus = TokenSaveStatus.EncryptionFailed;
                // 异常消息可能含密文/JSON 片段：对外只暴露状态码，细节仅保留异常类型。
                System.Diagnostics.Debug.WriteLine($"[AuthService] Token protect failed: {ex.GetType().Name}");
                failure = TokenSaveStatus.EncryptionFailed;
            }

            if (toWrite is not null)
            {
                WriteTokenFileAtomically(toWrite);
                LastSaveStatus = TokenSaveStatus.SavedEncrypted;
            }
        }
        catch (Exception ex)
        {
            // Best-effort save — don't crash if disk is full
            LastSaveStatus = TokenSaveStatus.Failed;
            System.Diagnostics.Debug.WriteLine($"[AuthService] Token save failed: {ex.GetType().Name}");
            failure = TokenSaveStatus.Failed;
        }
        return failure;
    }

    private async Task<(PersistedToken? Data, bool WasPlaintext)> LoadPersistedTokenAsync(string tokenPath)
    {
        var raw = await File.ReadAllBytesAsync(tokenPath);
        // Try protection first; fall back to plaintext for historical files.
        // 失败可能是密文损坏也可能是明文，不区分原因，统一尝试明文解析。
        if (_protector.CanProtect)
        {
            try
            {
                var unprotected = _protector.Unprotect(raw);
                var json = Encoding.UTF8.GetString(unprotected);
                var data = JsonSerializer.Deserialize<PersistedToken>(json);
                if (data is not null) return (data, WasPlaintext: false);
            }
            catch
            {
                // Fall through to plaintext attempt (migration / fallback)
            }
        }
        try
        {
            var json = Encoding.UTF8.GetString(raw);
            return (JsonSerializer.Deserialize<PersistedToken>(json), WasPlaintext: true);
        }
        catch
        {
            return (null, WasPlaintext: false);
        }
    }

    /// <summary>
    /// 登录/注册成功：换代 + Apply + 落盘在同一临界区内完成，避免与 Logout/续期交错。
    /// </summary>
    private void ApplyTokensAndSave(AuthResponse data)
    {
        TokenSaveStatus? failure;
        lock (_saveLock)
        {
            var epoch = Interlocked.Increment(ref _sessionEpoch);
            ApplyTokens(data);
            failure = SaveTokenLocked(epoch);
        }
        if (failure is { } status) TokenSaveFailed?.Invoke(status);
    }

    private void WriteTokenFileAtomically(byte[] toWrite)
    {
        if (!string.IsNullOrEmpty(_tokenDir))
        {
            Directory.CreateDirectory(_tokenDir);
        }
        var tempPath = _tokenPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, toWrite);
            File.Move(tempPath, _tokenPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private void ApplyTokens(AuthResponse data)
    {
        _accessToken = data.AccessToken;
        _refreshToken = data.RefreshToken;
        _accessTokenExpiry = data.ExpiresAt;

        // 契约 `AuthResponse.user` 是 required：服务端一定会给。
        CurrentUserId = data.User.Id;
        CurrentUsername = data.User.Username;
        CurrentDisplayName = data.User.DisplayName;

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
        // 请求前在同一临界区捕获会话代数与 refresh token 快照：
        // 请求期间若发生 Logout/新登录，旧响应/旧令牌不得再复活或覆盖凭据。
        string localRefreshToken;
        int requestEpoch;
        lock (_saveLock)
        {
            requestEpoch = Volatile.Read(ref _sessionEpoch);
            localRefreshToken = _refreshToken!;
        }

        try
        {
            // 续期接口自身走的是"不触发续期"的那条路径（ApiClient.RefreshTokenAsync）：
            // 续期接口自己再触发续期会自等待（见 ApiClient 的 InsideTokenRenewal 说明）。
            var result = await _apiClient.RefreshTokenAsync(new RefreshRequest
            {
                RefreshToken = localRefreshToken,
            });

            if (result?.Data is null) return false;

            TokenSaveStatus? failure;
            lock (_saveLock)
            {
                if (Volatile.Read(ref _sessionEpoch) != requestEpoch)
                {
                    return false;
                }
                // 会话仍有效：应用新令牌并落盘，锁外再触发失败事件。
                _accessToken = result.Data.AccessToken;
                _refreshToken = result.Data.RefreshToken;
                _accessTokenExpiry = result.Data.ExpiresAt;
                _apiClient.SetAccessToken(_accessToken);
                failure = SaveTokenLocked(requestEpoch);
            }
            if (failure is { } refreshFailure) TokenSaveFailed?.Invoke(refreshFailure);
            return true;
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // 401 只代表本会话的 refresh token 失效：请求期间若已换代（新登录/登出），不得踢掉新会话。
            // epoch 比较与 Logout 必须同一临界区，防比较后新登录的 TOCTOU；Monitor 可重入，锁内调 Logout 安全。
            lock (_saveLock)
            {
                if (Volatile.Read(ref _sessionEpoch) == requestEpoch)
                {
                    Logout();
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthService] Refresh failed due to transient/network error: {ex.Message}");
            return false;
        }
    }

    public void Logout()
    {
        lock (_saveLock)
        {
            Interlocked.Increment(ref _sessionEpoch);
            _accessToken = null;
            _refreshToken = null;
            CurrentUserId = null;
            CurrentUsername = null;
            CurrentDisplayName = null;
            _apiClient.ClearAccessToken();
            try { File.Delete(_tokenPath); } catch { }
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
