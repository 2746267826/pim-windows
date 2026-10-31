using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Durable worker evidence. Lease tokens are never persisted. Any non-terminal
/// snapshot loaded after a restart is converted to ExecutionUnknown and cannot be
/// claimed, renewed, acknowledged or reused automatically.
/// </summary>
public sealed class LocalAsrWorkerStateStore
{
    private const string FileName = "worker-state.json";
    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;

    public LocalAsrWorkerStateStore(string root, Func<DateTimeOffset>? now = null)
    {
        _root = Path.GetFullPath(root);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task SaveAsync(
        LocalAsrWorkerPersistedState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        var path = StatePath(state.ServerOrigin, state.OwnerIdentity, state.NodeId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(state);
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<LocalAsrWorkerPersistedState?> LoadAsync(
        LocalAccountScope scope,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateNodeId(nodeId);
        var path = StatePath(scope.ServerBaseAddress, scope.OwnerIdentity, nodeId);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<LocalAsrWorkerPersistedState>(json);
            if (state is null)
            {
                return null;
            }

            ValidateState(state);
            return string.Equals(state.ServerOrigin, scope.ServerBaseAddress, StringComparison.Ordinal)
                   && string.Equals(state.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal)
                   && string.Equals(state.NodeId, nodeId, StringComparison.Ordinal)
                ? state
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<LocalAsrRestartRecoveryResult> RecoverAfterRestartAsync(
        LocalAccountScope scope,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(scope, nodeId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new LocalAsrRestartRecoveryResult(
                LocalAsrRestartRecoveryStatus.NoPriorState,
                null,
                null,
                null);
        }

        if (state.Stage is not (LocalAsrPersistedStage.Running
            or LocalAsrPersistedStage.ExecutionUnknown))
        {
            return new LocalAsrRestartRecoveryResult(
                LocalAsrRestartRecoveryStatus.AlreadyTerminal,
                state.AttemptId,
                state.LeaseId,
                state.Stage.ToString());
        }

        if (state.Stage == LocalAsrPersistedStage.Running)
        {
            state = state with
            {
                Stage = LocalAsrPersistedStage.ExecutionUnknown,
                ReasonCode = LocalRuntimeReasonCodes.RestartUnknown,
                Detail = "worker restart cannot prove the prior process, lease or ack state",
                UpdatedAtUtc = _now(),
            };
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return new LocalAsrRestartRecoveryResult(
            LocalAsrRestartRecoveryStatus.ExecutionUnknown,
            state.AttemptId,
            state.LeaseId,
            state.Detail);
    }

    private string StatePath(string serverOrigin, string ownerIdentity, string nodeId)
    {
        var scopeKey = PathSafety.HashAccountKey(serverOrigin + "\n" + ownerIdentity);
        var nodeKey = PathSafety.HashAccountKey(nodeId);
        return Path.Combine(_root, "accounts", scopeKey, "asr-worker", nodeKey, FileName);
    }

    private static void ValidateState(LocalAsrWorkerPersistedState state)
    {
        _ = new LocalAccountScope(state.ServerOrigin, state.OwnerIdentity);
        ValidateNodeId(state.NodeId);
        if (string.IsNullOrWhiteSpace(state.AttemptId)
            || state.AttemptId.Length > 256
            || string.IsNullOrWhiteSpace(state.LeaseId)
            || state.LeaseId.Length > 256
            || state.FencingToken <= 0)
        {
            throw new ArgumentException("worker state identity is invalid", nameof(state));
        }
    }

    private static void ValidateNodeId(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 128)
        {
            throw new ArgumentException("node id is required and bounded", nameof(nodeId));
        }
    }
}
