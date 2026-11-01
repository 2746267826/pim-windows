using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public sealed record LocalAccountScope
{
    public LocalAccountScope(string serverOrigin, string ownerIdentity)
    {
        (ServerOrigin, BasePath) = NormalizeServerScope(serverOrigin);
        OwnerIdentity = NormalizeOwnerIdentity(ownerIdentity);
    }

    public string ServerOrigin { get; }

    /// <summary>
    /// API 部署子路径（没有子路径时为空串）。ApiClient 的 base URL 必须保留它；
    /// 仅把 origin 当作作用域会把同主机不同子路径误判为同一账号边界。
    /// </summary>
    public string BasePath { get; }

    public string ServerBaseAddress =>
        BasePath.Length == 0 ? ServerOrigin : ServerOrigin + BasePath;

    public string OwnerIdentity { get; }

    public bool Matches(LocalAccountScope other) =>
        string.Equals(ServerOrigin, other.ServerOrigin, StringComparison.Ordinal)
        && string.Equals(BasePath, other.BasePath, StringComparison.Ordinal)
        && string.Equals(OwnerIdentity, other.OwnerIdentity, StringComparison.Ordinal);

    private static (string Origin, string BasePath) NormalizeServerScope(string serverOrigin)
    {
        if (!Uri.TryCreate(serverOrigin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "server address must be an absolute http(s) URL without query, fragment or userinfo",
                nameof(serverOrigin));
        }

        var origin = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var basePath = uri.AbsolutePath.TrimEnd('/');
        return (origin, string.Equals(basePath, "/", StringComparison.Ordinal) ? string.Empty : basePath);
    }

    private static string NormalizeOwnerIdentity(string ownerIdentity)
    {
        if (string.IsNullOrWhiteSpace(ownerIdentity) || ownerIdentity.Length > 256)
        {
            throw new ArgumentException("owner identity is required", nameof(ownerIdentity));
        }

        return ownerIdentity.Trim();
    }
}

public sealed record NodeCapabilitySnapshot(
    string AccountId,
    IReadOnlyList<string> ExecutableModelIds,
    DateTimeOffset RegisteredAtUtc,
    string? ServerBaseAddress = null);

public sealed record AccountSwitchResult(
    string? PreviousAccountId,
    string? CurrentAccountId,
    bool ActiveRunCancelled,
    bool CapabilityRevoked);

/// <summary>
/// Account boundary for local execution. A run is pinned to the account that
/// started it; switching accounts cancels the run and revokes the node capability
/// snapshot instead of letting an old lease serve the new account.
/// </summary>
public sealed class LocalRuntimeAccountGuard
{
    private readonly object _sync = new();
    private string? _accountId;
    private string? _serverBaseAddress;
    private CancellationTokenSource? _activeRun;
    private NodeCapabilitySnapshot? _capability;

    public string? CurrentAccountId
    {
        get
        {
            lock (_sync)
            {
                return _accountId;
            }
        }
    }

    public string? CurrentServerOrigin
    {
        get
        {
            lock (_sync)
            {
                return _serverBaseAddress is null
                    ? null
                    : new Uri(_serverBaseAddress, UriKind.Absolute)
                        .GetLeftPart(UriPartial.Authority);
            }
        }
    }

    public string? CurrentServerBaseAddress
    {
        get
        {
            lock (_sync)
            {
                return _serverBaseAddress;
            }
        }
    }

    public NodeCapabilitySnapshot? Capability
    {
        get
        {
            lock (_sync)
            {
                return _capability;
            }
        }
    }

    public CancellationToken BeginRun(string accountId)
        => BeginRunCore(accountId, serverOrigin: null);

    public CancellationToken BeginRun(LocalAccountScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return BeginRunCore(scope.OwnerIdentity, scope.ServerBaseAddress);
    }

    private CancellationToken BeginRunCore(string accountId, string? serverOrigin)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("account id is required", nameof(accountId));
        }

        accountId = accountId.Trim();
        serverOrigin = NormalizeOptionalServerBaseAddress(serverOrigin);
        lock (_sync)
        {
            if (_accountId is not null
                && (!string.Equals(_accountId, accountId, StringComparison.Ordinal)
                    || !string.Equals(
                        _serverBaseAddress,
                        serverOrigin,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("run scope does not match the active account/server origin");
            }

            if (_activeRun is not null)
            {
                throw new InvalidOperationException("a local runtime run is already active");
            }

            _accountId = accountId;
            _serverBaseAddress = serverOrigin;
            _activeRun = new CancellationTokenSource();
            return _activeRun.Token;
        }
    }

    public void EndRun(CancellationToken runToken)
    {
        lock (_sync)
        {
            if (_activeRun is null)
            {
                return;
            }

            if (_activeRun.Token == runToken)
            {
                _activeRun.Dispose();
                _activeRun = null;
            }
        }
    }

    public void RegisterCapability(string accountId, IEnumerable<string> executableModelIds)
        => RegisterCapabilityCore(accountId, serverOrigin: null, executableModelIds);

    public void RegisterCapability(
        LocalAccountScope scope,
        IEnumerable<string> executableModelIds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RegisterCapabilityCore(scope.OwnerIdentity, scope.ServerBaseAddress, executableModelIds);
    }

    private void RegisterCapabilityCore(
        string accountId,
        string? serverOrigin,
        IEnumerable<string> executableModelIds)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("account id is required", nameof(accountId));
        }

        ArgumentNullException.ThrowIfNull(executableModelIds);
        accountId = accountId.Trim();
        serverOrigin = NormalizeOptionalServerBaseAddress(serverOrigin);
        lock (_sync)
        {
            if (_accountId is not null
                && (!string.Equals(_accountId, accountId, StringComparison.Ordinal)
                    || !string.Equals(
                        _serverBaseAddress,
                        serverOrigin,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "capability scope does not match the active account/server origin");
            }

            _accountId = accountId;
            _serverBaseAddress = serverOrigin;
            _capability = new NodeCapabilitySnapshot(
                accountId,
                executableModelIds.Distinct(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow,
                serverOrigin);
        }
    }

    public Task<AccountSwitchResult> SwitchAccountAsync(string? newAccountId)
        => SwitchScopeCore(
            string.IsNullOrWhiteSpace(newAccountId) ? null : newAccountId.Trim(),
            nextOrigin: null);

    public Task<AccountSwitchResult> SwitchScopeAsync(LocalAccountScope? newScope)
        => SwitchScopeCore(newScope?.OwnerIdentity, newScope?.ServerBaseAddress);

    private Task<AccountSwitchResult> SwitchScopeCore(
        string? nextAccount,
        string? nextOrigin)
    {
        lock (_sync)
        {
            var previous = _accountId;
            if (string.Equals(previous, nextAccount, StringComparison.Ordinal)
                && string.Equals(_serverBaseAddress, nextOrigin, StringComparison.Ordinal))
            {
                return Task.FromResult(new AccountSwitchResult(previous, previous, false, false));
            }

            var cancelled = false;
            if (_activeRun is not null)
            {
                _activeRun.Cancel();
                // Do not dispose here: the runner may still register callbacks on
                // the token while unwinding. EndRun/GC releases the source.
                _activeRun = null;
                cancelled = true;
            }

            var revoked = _capability is not null;
            _capability = null;
            _accountId = nextAccount;
            _serverBaseAddress = nextOrigin;
            return Task.FromResult(new AccountSwitchResult(previous, nextAccount, cancelled, revoked));
        }
    }

    private static string? NormalizeOptionalServerBaseAddress(string? serverOrigin)
    {
        if (serverOrigin is null)
        {
            return null;
        }

        return new LocalAccountScope(serverOrigin, "scope-validation").ServerBaseAddress;
    }
}
