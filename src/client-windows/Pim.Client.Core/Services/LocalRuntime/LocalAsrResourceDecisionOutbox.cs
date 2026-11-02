using System.Text.Json;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Durable owner/node/key association for claim-time resource decisions. It freezes
/// the local evaluator result until an accepting typed transport acknowledges it.
/// </summary>
public sealed class LocalAsrResourceDecisionOutbox
{
    private const string FileNameSuffix = ".json";
    private readonly string _root;

    public LocalAsrResourceDecisionOutbox(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("outbox root is required", nameof(root));
        _root = Path.GetFullPath(root);
    }

    public async Task<LocalAsrFrozenDecisionResult> FreezeAsync(
        LocalAccountScope scope,
        string nodeId,
        string idempotencyKey,
        LocalAsrWorkTrigger trigger,
        ResourceDecision decision,
        DateTimeOffset sampledAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(decision);
        ValidateIdentity(nodeId, idempotencyKey);
        var reasons = decision.Reasons?.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentException("resource decision reasons are required", nameof(decision));
        if (decision.Satisfied == (reasons.Length > 0) || sampledAtUtc == default)
            throw new ArgumentException("resource decision payload is inconsistent", nameof(decision));

        var pending = new LocalAsrPendingResourceDecision(
            scope.ServerBaseAddress,
            scope.OwnerIdentity,
            nodeId,
            idempotencyKey,
            new LocalAsrFrozenResourceDecision(
                trigger == LocalAsrWorkTrigger.Automatic,
                decision.Satisfied,
                Array.AsReadOnly(reasons),
                sampledAtUtc.ToUniversalTime()));
        var path = PendingPath(scope, nodeId, idempotencyKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            return new(await ReadAndValidateAsync(path, scope, nodeId, idempotencyKey, cancellationToken)
                .ConfigureAwait(false), IsReplay: true);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(pending), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(temp, path, overwrite: false);
                return new(Snapshot(pending), IsReplay: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return new(await ReadAndValidateAsync(path, scope, nodeId, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false), IsReplay: true);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<LocalAsrPendingResourceDecision?> FindPendingAsync(
        LocalAccountScope scope,
        string nodeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateIdentity(nodeId, idempotencyKey);
        var path = PendingPath(scope, nodeId, idempotencyKey);
        if (!File.Exists(path)) return null;
        return await ReadAndValidateAsync(path, scope, nodeId, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LocalAsrPendingResourceDecision>> ListPendingAsync(
        LocalAccountScope scope,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateIdentity(nodeId, "pending-list");
        var directory = NodeDirectory(scope, nodeId);
        if (!Directory.Exists(directory)) return Array.Empty<LocalAsrPendingResourceDecision>();

        var result = new List<LocalAsrPendingResourceDecision>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + FileNameSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pending.ServerBaseAddress, scope.ServerBaseAddress, StringComparison.Ordinal)
                || !string.Equals(pending.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal)
                || !string.Equals(pending.NodeId, nodeId, StringComparison.Ordinal))
                throw new InvalidDataException("resource decision outbox identity mismatch");
            if (!pending.Acknowledged)
                result.Add(Snapshot(pending));
        }

        return result.OrderBy(item => item.IdempotencyKey, StringComparer.Ordinal).ToArray();
    }

    public async Task RemovePendingAsync(
        LocalAsrPendingResourceDecision expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var scope = new LocalAccountScope(expected.ServerBaseAddress, expected.OwnerIdentity);
        ValidateIdentity(expected.NodeId, expected.IdempotencyKey);
        var path = PendingPath(scope, expected.NodeId, expected.IdempotencyKey);
        if (!File.Exists(path)) return;
        var stored = await ReadAndValidateAsync(path, scope, expected.NodeId, expected.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (!Equivalent(stored, expected))
            throw new InvalidDataException("resource decision outbox entry changed before acknowledgement");
        File.Delete(path);
    }

    public async Task MarkAcknowledgedAsync(
        LocalAsrPendingResourceDecision expected,
        string outcome,
        DateTimeOffset acknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (string.IsNullOrWhiteSpace(outcome) || outcome.Length > 64 || acknowledgedAtUtc == default)
            throw new ArgumentException("resource decision acknowledgement is invalid");

        var scope = new LocalAccountScope(expected.ServerBaseAddress, expected.OwnerIdentity);
        ValidateIdentity(expected.NodeId, expected.IdempotencyKey);
        var path = PendingPath(scope, expected.NodeId, expected.IdempotencyKey);
        if (!File.Exists(path))
            throw new FileNotFoundException("resource decision outbox entry is missing", path);

        var stored = await ReadAndValidateAsync(path, scope, expected.NodeId, expected.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (!Equivalent(stored, expected))
            throw new InvalidDataException("resource decision outbox entry changed before acknowledgement");
        if (stored.Acknowledged)
        {
            if (!string.Equals(stored.AcknowledgedOutcome, outcome, StringComparison.Ordinal))
                throw new InvalidDataException("resource decision acknowledgement outcome changed");
            return;
        }

        var acknowledged = stored with
        {
            Acknowledged = true,
            AcknowledgedOutcome = outcome,
            AcknowledgedAtUtc = acknowledgedAtUtc.ToUniversalTime(),
        };
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(acknowledged), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static async Task<LocalAsrPendingResourceDecision> ReadAndValidateAsync(
        string path,
        LocalAccountScope scope,
        string nodeId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(stored.ServerBaseAddress, scope.ServerBaseAddress, StringComparison.Ordinal)
            || !string.Equals(stored.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal)
            || !string.Equals(stored.NodeId, nodeId, StringComparison.Ordinal)
            || !string.Equals(stored.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            throw new InvalidDataException("resource decision outbox identity mismatch");
        return Snapshot(stored);
    }

    private static async Task<LocalAsrPendingResourceDecision> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var pending = JsonSerializer.Deserialize<LocalAsrPendingResourceDecision>(json)
                ?? throw new InvalidDataException("resource decision outbox entry is empty");
            if (pending.Decision is null || pending.Decision.Reasons is null)
                throw new InvalidDataException("resource decision outbox payload is incomplete");
            return pending;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("resource decision outbox entry is corrupt", error);
        }
    }

    private string PendingPath(LocalAccountScope scope, string nodeId, string idempotencyKey) =>
        Path.Combine(NodeDirectory(scope, nodeId), PathSafety.HashAccountKey(idempotencyKey) + FileNameSuffix);

    private string NodeDirectory(LocalAccountScope scope, string nodeId) =>
        Path.Combine(_root, "accounts", PathSafety.HashAccountKey(scope.ServerBaseAddress + "\n" + scope.OwnerIdentity),
            "nodes", PathSafety.HashAccountKey(nodeId), "resource-decisions");

    private static void ValidateIdentity(string nodeId, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 128 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256 ||
            nodeId.Any(char.IsControl) || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("resource decision identity is invalid");
    }

    private static bool Equivalent(LocalAsrPendingResourceDecision left, LocalAsrPendingResourceDecision right) =>
        string.Equals(left.ServerBaseAddress, right.ServerBaseAddress, StringComparison.Ordinal)
        && string.Equals(left.OwnerIdentity, right.OwnerIdentity, StringComparison.Ordinal)
        && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
        && string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
        && left.Decision.IsAutomatic == right.Decision.IsAutomatic
        && left.Decision.Passed == right.Decision.Passed
        && left.Decision.SampledAtUtc == right.Decision.SampledAtUtc
        && left.Decision.Reasons.SequenceEqual(right.Decision.Reasons, StringComparer.Ordinal);

    private static LocalAsrPendingResourceDecision Snapshot(LocalAsrPendingResourceDecision value) =>
        value with
        {
            Decision = value.Decision with
            {
                Reasons = Array.AsReadOnly(value.Decision.Reasons.ToArray()),
            },
        };
}

public sealed record LocalAsrPendingResourceDecision(
    string ServerBaseAddress,
    string OwnerIdentity,
    string NodeId,
    string IdempotencyKey,
    LocalAsrFrozenResourceDecision Decision,
    bool Acknowledged = false,
    string? AcknowledgedOutcome = null,
    DateTimeOffset? AcknowledgedAtUtc = null);

public sealed record LocalAsrFrozenDecisionResult(
    LocalAsrPendingResourceDecision Pending,
    bool IsReplay);
