namespace Pim.Client.App;

internal sealed record PlannerAccountIdentity(string UserId, string ServerUrl);

internal readonly record struct PlannerDataFence(
    long Generation,
    PlannerAccountIdentity Account);

internal enum PlannerFenceFailure
{
    None,
    NoAccount,
    AccountChanged,
    StaleData,
}

/// <summary>
/// Binds loaded calendar/task rows and editor choices to the account/server that produced them.
/// Starting another load or changing credentials invalidates every older UI write intent.
/// </summary>
internal sealed class NativePlannerUiState
{
    private readonly object _sync = new();
    private long _loadGeneration;
    private PlannerDataFence? _loaded;

    public long BeginLoad() => Interlocked.Increment(ref _loadGeneration);

    public bool IsLoadCurrent(long generation, PlannerAccountIdentity? current)
        => current is not null && generation == Volatile.Read(ref _loadGeneration);

    public bool CommitLoad(long generation, PlannerAccountIdentity current)
    {
        if (!IsLoadCurrent(generation, current))
        {
            return false;
        }

        lock (_sync)
        {
            if (generation != Volatile.Read(ref _loadGeneration))
            {
                return false;
            }

            _loaded = new PlannerDataFence(generation, current);
            return true;
        }
    }

    public PlannerFenceFailure CheckWrite(
        PlannerDataFence fence,
        PlannerAccountIdentity? current)
    {
        if (current is null)
        {
            return PlannerFenceFailure.NoAccount;
        }

        if (current != fence.Account)
        {
            return PlannerFenceFailure.AccountChanged;
        }

        lock (_sync)
        {
            return _loaded == fence
                && fence.Generation == Volatile.Read(ref _loadGeneration)
                ? PlannerFenceFailure.None
                : PlannerFenceFailure.StaleData;
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _loadGeneration);
        lock (_sync)
        {
            _loaded = null;
        }
    }
}
