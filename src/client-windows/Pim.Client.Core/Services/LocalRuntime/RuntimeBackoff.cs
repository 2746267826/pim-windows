using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public sealed record RuntimeBackoffState(
    int AutoAttempts,
    DateTimeOffset? NextAttemptAtUtc,
    IReadOnlyList<string> LastReasons,
    bool ManualRequired,
    DateTimeOffset UpdatedAtUtc);

public sealed record RuntimeBackoffDecision(
    bool ShouldAutoAttempt,
    DateTimeOffset? NextAttemptAtUtc,
    bool ManualRequired);

public sealed record RuntimeBackoffPolicy(
    int MaxAutoAttempts = 6,
    int BaseDelaySeconds = 30,
    int MaxDelaySeconds = 30 * 60)
{
    public TimeSpan ComputeDelay(int completedAutoAttempts)
    {
        // First failure waits the base delay, then doubles per additional failure.
        var exponent = Math.Clamp(completedAutoAttempts - 1, 0, 20);
        var seconds = BaseDelaySeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelaySeconds));
    }
}

/// <summary>
/// Durable per-account/model backoff. Automatic attempts stop after the configured
/// cap; a manual action is still allowed but must run the resource gate again.
/// </summary>
public sealed class RuntimeBackoffStore
{
    private const string FileName = "backoff.json";
    private readonly string _root;
    private readonly RuntimeBackoffPolicy _policy;
    private readonly Func<DateTimeOffset> _now;

    public RuntimeBackoffStore(
        string root,
        RuntimeBackoffPolicy? policy = null,
        Func<DateTimeOffset>? now = null)
    {
        _root = Path.GetFullPath(root);
        _policy = policy ?? new RuntimeBackoffPolicy();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<RuntimeBackoffState> RecordResourceFailureAsync(
        string accountId,
        string modelId,
        IReadOnlyList<string> reasons,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(accountId, modelId).ConfigureAwait(false)
                    ?? EmptyState();
        var attempts = state.AutoAttempts + 1;
        var manualRequired = attempts >= _policy.MaxAutoAttempts;
        var now = _now();
        var next = manualRequired ? (DateTimeOffset?)null : now + _policy.ComputeDelay(attempts);
        var updated = new RuntimeBackoffState(
            attempts,
            next,
            reasons.ToArray(),
            manualRequired,
            now);
        await SaveAsync(accountId, modelId, updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<RuntimeBackoffState> RecordSuccessAsync(
        string accountId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var state = new RuntimeBackoffState(
            0,
            null,
            Array.Empty<string>(),
            false,
            _now());
        await SaveAsync(accountId, modelId, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<RuntimeBackoffDecision> EvaluateAsync(
        string accountId,
        string modelId,
        DateTimeOffset? now = null)
    {
        var state = await LoadAsync(accountId, modelId).ConfigureAwait(false)
                    ?? EmptyState();
        if (state.ManualRequired)
        {
            return new RuntimeBackoffDecision(false, null, true);
        }

        if (state.NextAttemptAtUtc is { } next && next > (now ?? _now()))
        {
            return new RuntimeBackoffDecision(false, next, false);
        }

        return new RuntimeBackoffDecision(true, state.NextAttemptAtUtc, false);
    }

    public async Task<RuntimeBackoffState?> LoadAsync(string accountId, string modelId)
    {
        var path = StatePath(accountId, modelId);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RuntimeBackoffState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task SaveAsync(
        string accountId,
        string modelId,
        RuntimeBackoffState state,
        CancellationToken cancellationToken)
    {
        var path = StatePath(accountId, modelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(state);
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private string StatePath(string accountId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || !LocalModelManifest.IsValidModelId(modelId))
        {
            throw new ArgumentException("account and model id are required");
        }

        return Path.Combine(
            _root,
            "accounts",
            PathSafety.HashAccountKey(accountId.Trim()),
            modelId,
            FileName);
    }

    private RuntimeBackoffState EmptyState() =>
        new(0, null, Array.Empty<string>(), false, _now());
}
