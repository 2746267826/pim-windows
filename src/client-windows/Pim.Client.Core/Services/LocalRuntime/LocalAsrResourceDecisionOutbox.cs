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
        LocalAsrClaimRequest request,
        LocalAsrWorkTrigger trigger,
        ResourceDecision decision,
        DateTimeOffset sampledAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(decision);
        ValidateIdentity(request.NodeId, request.IdempotencyKey);
        var reasons = decision.Reasons?.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentException("resource decision reasons are required", nameof(decision));
        if (decision.Satisfied == (reasons.Length > 0) || sampledAtUtc == default)
            throw new ArgumentException("resource decision payload is inconsistent", nameof(decision));

        var pending = new LocalAsrPendingResourceDecision(
            request.Scope.ServerBaseAddress,
            request.Scope.OwnerIdentity,
            request.NodeId,
            request.IdempotencyKey,
            new LocalAsrFrozenResourceDecision(
                trigger == LocalAsrWorkTrigger.Automatic,
                decision.Satisfied,
                Array.AsReadOnly(reasons),
                sampledAtUtc.ToUniversalTime()),
            new LocalAsrClaimPayload(
                request.RuntimeId,
                request.ModelId,
                request.ModelRevision,
                request.ModelSha256));
        var path = PendingPath(request.Scope, request.NodeId, request.IdempotencyKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = await FindPendingAsync(request.Scope, request.NodeId, request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!PayloadEquals(existing.RequestPayload, pending.RequestPayload))
                throw new InvalidDataException("claim idempotency key was reused with a different payload");
            return new(existing, IsReplay: true);
        }

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
            catch (IOException) when (File.Exists(path) || File.Exists(AcknowledgedPath(
                       request.Scope, request.NodeId, request.IdempotencyKey)) || File.Exists(
                       LegacyPath(request.Scope, request.NodeId, request.IdempotencyKey)))
            {
                var raced = await FindPendingAsync(request.Scope, request.NodeId, request.IdempotencyKey,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("resource decision disappeared during creation");
                if (!PayloadEquals(raced.RequestPayload, pending.RequestPayload))
                    throw new InvalidDataException("claim idempotency key was reused with a different payload");
                return new(raced, IsReplay: true);
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
        var pendingPath = PendingPath(scope, nodeId, idempotencyKey);
        var acknowledgedPath = AcknowledgedPath(scope, nodeId, idempotencyKey);
        var legacyPath = LegacyPath(scope, nodeId, idempotencyKey);
        var path = File.Exists(acknowledgedPath)
            ? acknowledgedPath
            : File.Exists(pendingPath)
                ? pendingPath
                : legacyPath;
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
        var directory = PendingDirectory(scope, nodeId);
        var legacyDirectory = NodeDirectory(scope, nodeId);
        if (!Directory.Exists(directory) && !Directory.Exists(legacyDirectory))
            return Array.Empty<LocalAsrPendingResourceDecision>();

        var result = new List<LocalAsrPendingResourceDecision>();
        var paths = new List<string>();
        if (Directory.Exists(directory))
            paths.AddRange(Directory.EnumerateFiles(directory, "*" + FileNameSuffix));
        if (Directory.Exists(legacyDirectory))
            paths.AddRange(Directory.EnumerateFiles(legacyDirectory, "*" + FileNameSuffix));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(AcknowledgedDirectory(scope, nodeId), Path.GetFileName(path))))
                continue;
            var pending = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(pending.ServerBaseAddress, scope.ServerBaseAddress, StringComparison.Ordinal)
                || !string.Equals(pending.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal)
                || !string.Equals(pending.NodeId, nodeId, StringComparison.Ordinal))
                throw new InvalidDataException("resource decision outbox identity mismatch");
            if (!pending.Acknowledged)
                result.Add(Snapshot(pending));
        }

        return result
            .OrderBy(item => item.Decision.SampledAtUtc)
            .ThenBy(item => item.IdempotencyKey, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task RemovePendingAsync(
        LocalAsrPendingResourceDecision expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var scope = new LocalAccountScope(expected.ServerBaseAddress, expected.OwnerIdentity);
        ValidateIdentity(expected.NodeId, expected.IdempotencyKey);
        var paths = new[]
        {
            PendingPath(scope, expected.NodeId, expected.IdempotencyKey),
            AcknowledgedPath(scope, expected.NodeId, expected.IdempotencyKey),
            LegacyPath(scope, expected.NodeId, expected.IdempotencyKey),
        };
        var acknowledgedPath = paths[1];
        if (File.Exists(acknowledgedPath))
        {
            var acknowledged = await ReadAndValidateAsync(acknowledgedPath, scope, expected.NodeId,
                expected.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (!Equivalent(acknowledged, expected))
                throw new InvalidDataException("resource decision outbox entry changed before removal");
            foreach (var stale in new[] { paths[0], paths[2] }.Where(File.Exists))
                File.Delete(stale);
            return;
        }

        var path = new[] { paths[0], paths[2] }.FirstOrDefault(File.Exists);
        if (path is null) return;
        var stored = await ReadAndValidateAsync(path, scope, expected.NodeId, expected.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (!Equivalent(stored, expected))
            throw new InvalidDataException("resource decision outbox entry changed before removal");
        foreach (var candidate in new[] { paths[0], paths[2] }.Where(File.Exists))
            File.Delete(candidate);
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
        var pendingPath = PendingPath(scope, expected.NodeId, expected.IdempotencyKey);
        var acknowledgedPath = AcknowledgedPath(scope, expected.NodeId, expected.IdempotencyKey);
        var legacyPath = LegacyPath(scope, expected.NodeId, expected.IdempotencyKey);
        var existingAckPath = File.Exists(acknowledgedPath) ? acknowledgedPath : null;
        if (existingAckPath is not null)
        {
            var existingAcknowledgement = await ReadAndValidateAsync(existingAckPath, scope, expected.NodeId,
                expected.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (!Equivalent(existingAcknowledgement, expected)
                || !string.Equals(existingAcknowledgement.AcknowledgedOutcome, outcome, StringComparison.Ordinal))
                throw new InvalidDataException("resource decision acknowledgement changed");
            foreach (var candidate in new[] { pendingPath, legacyPath }.Where(File.Exists))
                File.Delete(candidate);
            return;
        }

        var sourcePath = File.Exists(pendingPath) ? pendingPath : legacyPath;
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("resource decision outbox entry is missing", sourcePath);

        var stored = await ReadAndValidateAsync(sourcePath, scope, expected.NodeId, expected.IdempotencyKey,
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
        Directory.CreateDirectory(Path.GetDirectoryName(acknowledgedPath)!);
        var temp = acknowledgedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(acknowledged), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                File.Move(temp, acknowledgedPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(acknowledgedPath))
            {
                var concurrent = await ReadAndValidateAsync(acknowledgedPath, scope, expected.NodeId,
                    expected.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                if (!Equivalent(concurrent, expected)
                    || !string.Equals(concurrent.AcknowledgedOutcome, outcome, StringComparison.Ordinal))
                    throw new InvalidDataException("resource decision acknowledgement changed");
            }

            foreach (var candidate in new[] { pendingPath, legacyPath }.Where(File.Exists))
                File.Delete(candidate);
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
        Path.Combine(PendingDirectory(scope, nodeId), PathSafety.HashAccountKey(idempotencyKey) + FileNameSuffix);

    private string AcknowledgedPath(LocalAccountScope scope, string nodeId, string idempotencyKey) =>
        Path.Combine(AcknowledgedDirectory(scope, nodeId), PathSafety.HashAccountKey(idempotencyKey) + FileNameSuffix);

    private string LegacyPath(LocalAccountScope scope, string nodeId, string idempotencyKey) =>
        Path.Combine(NodeDirectory(scope, nodeId), PathSafety.HashAccountKey(idempotencyKey) + FileNameSuffix);

    private string PendingDirectory(LocalAccountScope scope, string nodeId) =>
        Path.Combine(NodeDirectory(scope, nodeId), "pending");

    private string AcknowledgedDirectory(LocalAccountScope scope, string nodeId) =>
        Path.Combine(NodeDirectory(scope, nodeId), "acknowledged");

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
        && left.Decision.Reasons.SequenceEqual(right.Decision.Reasons, StringComparer.Ordinal)
        && PayloadEquals(left.RequestPayload, right.RequestPayload);

    private static bool PayloadEquals(LocalAsrClaimPayload? left, LocalAsrClaimPayload? right) =>
        left is null
            ? right is null
            : right is not null
                && string.Equals(left.RuntimeId, right.RuntimeId, StringComparison.Ordinal)
                && string.Equals(left.ModelId, right.ModelId, StringComparison.Ordinal)
                && string.Equals(left.ModelRevision, right.ModelRevision, StringComparison.Ordinal)
                && string.Equals(left.ModelSha256, right.ModelSha256, StringComparison.OrdinalIgnoreCase);

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
    LocalAsrClaimPayload? RequestPayload = null,
    bool Acknowledged = false,
    string? AcknowledgedOutcome = null,
    DateTimeOffset? AcknowledgedAtUtc = null);

public sealed record LocalAsrClaimPayload(
    string RuntimeId,
    string ModelId,
    string ModelRevision,
    string ModelSha256);

public sealed record LocalAsrFrozenDecisionResult(
    LocalAsrPendingResourceDecision Pending,
    bool IsReplay);
