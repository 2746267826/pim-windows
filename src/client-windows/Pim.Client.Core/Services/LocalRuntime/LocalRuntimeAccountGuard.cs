using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public sealed record NodeCapabilitySnapshot(
    string AccountId,
    IReadOnlyList<string> ExecutableModelIds,
    DateTimeOffset RegisteredAtUtc);

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
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("account id is required", nameof(accountId));
        }

        lock (_sync)
        {
            if (_accountId is not null
                && !string.Equals(_accountId, accountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("run account does not match the active account");
            }

            if (_activeRun is not null)
            {
                throw new InvalidOperationException("a local runtime run is already active");
            }

            _accountId = accountId;
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
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("account id is required", nameof(accountId));
        }

        lock (_sync)
        {
            if (_accountId is not null
                && !string.Equals(_accountId, accountId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("capability account does not match the active account");
            }

            _accountId = accountId;
            _capability = new NodeCapabilitySnapshot(
                accountId,
                executableModelIds.Distinct(StringComparer.Ordinal).ToArray(),
                DateTimeOffset.UtcNow);
        }
    }

    public Task<AccountSwitchResult> SwitchAccountAsync(string? newAccountId)
    {
        lock (_sync)
        {
            var previous = _accountId;
            if (string.Equals(previous, newAccountId, StringComparison.Ordinal))
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
            _accountId = newAccountId;
            return Task.FromResult(new AccountSwitchResult(previous, newAccountId, cancelled, revoked));
        }
    }
}
