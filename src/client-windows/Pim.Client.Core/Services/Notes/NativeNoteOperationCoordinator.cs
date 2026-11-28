namespace Pim.Client.Core.Services.Notes;

public sealed record NativeNoteFinishResult(bool Started, bool Succeeded, Exception? Failure)
{
    public static NativeNoteFinishResult Rejected { get; } =
        new(Started: false, Succeeded: false, Failure: null);

    public static NativeNoteFinishResult Completed { get; } =
        new(Started: true, Succeeded: true, Failure: null);

    public static NativeNoteFinishResult Failed(Exception failure) =>
        new(Started: true, Succeeded: false, Failure: failure);
}

/// <summary>
/// Serializes note writes and gives Finish a barrier over every operation that
/// already started. Once Finish begins, no new read/edit/save/import operation
/// can enter; it waits for in-flight note reads, imports and saves, then runs
/// alone.
/// </summary>
public sealed class NativeNoteOperationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _activeOperations;
    private bool _finishing;
    private TaskCompletionSource? _drained;

    public bool IsFinishing
    {
        get
        {
            lock (_sync)
            {
                return _finishing;
            }
        }
    }

    public NativeNoteOperationLease? TryBeginOperation()
    {
        lock (_sync)
        {
            if (_finishing)
            {
                return null;
            }

            _activeOperations++;
            if (_activeOperations == 1)
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new NativeNoteOperationLease(this);
        }
    }

    public async Task<NativeNoteFinishResult> FinishAsync(
        Func<Task> finish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finish);
        Task drained;
        lock (_sync)
        {
            if (_finishing)
            {
                return NativeNoteFinishResult.Rejected;
            }

            _finishing = true;
            drained = _activeOperations == 0
                ? Task.CompletedTask
                : _drained!.Task;
        }

        try
        {
            await drained.WaitAsync(cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await finish();
                return NativeNoteFinishResult.Completed;
            }
            catch (Exception ex)
            {
                return NativeNoteFinishResult.Failed(ex);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _finishing = false;
            }
        }
    }

    internal async Task EnterSerializedAsync(CancellationToken cancellationToken)
        => await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

    internal void ExitSerialized() => _gate.Release();

    internal void EndOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    public void Dispose() => _gate.Dispose();
}

public sealed class NativeNoteOperationLease : IDisposable
{
    private NativeNoteOperationCoordinator? _owner;

    internal NativeNoteOperationLease(NativeNoteOperationCoordinator owner)
    {
        _owner = owner;
    }

    public async Task EnterSerializedAsync(CancellationToken cancellationToken = default)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(NativeNoteOperationLease));
        await owner.EnterSerializedAsync(cancellationToken);
    }

    public void ExitSerialized()
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(NativeNoteOperationLease));
        owner.ExitSerialized();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.EndOperation();
    }
}
