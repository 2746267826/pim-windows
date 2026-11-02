using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Freezes a claim's local resource verdict before any claim transport call. The
/// current generated API client has no resource-decision field, so its legacy path
/// remains unchanged and the durable association stays pending for typed integration.
/// </summary>
public sealed class LocalAsrClaimDecisionAdapter(
    ILocalAsrNodeTransport transport,
    LocalAsrResourceDecisionOutbox outbox,
    Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<LocalAsrFrozenDecisionResult> FreezeAsync(
        LocalAsrClaimRequest request,
        LocalAsrWorkTrigger trigger,
        ResourceDecision decision,
        DateTimeOffset sampledAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await outbox.FreezeAsync(request.Scope, request.NodeId, request.IdempotencyKey,
            trigger, decision, sampledAtUtc, cancellationToken).ConfigureAwait(false);
    }

    public Task<LocalAsrPendingResourceDecision?> FindPendingAsync(
        LocalAccountScope scope,
        string nodeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        outbox.FindPendingAsync(scope, nodeId, idempotencyKey, cancellationToken);

    public async Task<LocalAsrClaimResult> ClaimAsync(
        LocalAsrClaimRequest request,
        LocalAsrPendingResourceDecision frozen,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frozen);
        if (!request.Scope.Matches(new LocalAccountScope(frozen.ServerBaseAddress, frozen.OwnerIdentity))
            || !string.Equals(request.NodeId, frozen.NodeId, StringComparison.Ordinal)
            || !string.Equals(request.IdempotencyKey, frozen.IdempotencyKey, StringComparison.Ordinal))
            return new(LocalAsrClaimStatus.Rejected, null, LocalRuntimeReasonCodes.ScopeChanged);

        if (!frozen.Decision.Passed)
        {
            // A failed local gate never sends a claim. Keep the immutable decision
            // pending so retries cannot turn the same key into a different verdict.
            return new(LocalAsrClaimStatus.ResourceBlocked, null,
                string.Join(",", frozen.Decision.Reasons));
        }

        cancellationToken.ThrowIfCancellationRequested();
        LocalAsrClaimResult result;
        if (transport is ILocalAsrResourceDecisionTransport decisionTransport)
        {
            result = await decisionTransport.ClaimWithResourceDecisionAsync(
                    request with { ResourceDecision = frozen.Decision }, frozen.Decision, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            result = await transport.ClaimAsync(request with { ResourceDecision = null }, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result.ResourceDecisionAccepted)
        {
            await outbox.MarkAcknowledgedAsync(frozen, result.Status.ToString(), _now(), cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}
