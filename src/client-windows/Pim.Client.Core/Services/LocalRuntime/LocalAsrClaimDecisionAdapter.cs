using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Freezes a claim's local resource verdict before any claim transport call. A typed
/// transport receives both passing and failing decisions atomically; the legacy
/// generated API path remains unchanged until its backend contract is available.
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
        return await outbox.FreezeAsync(request, trigger, decision, sampledAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<LocalAsrPendingResourceDecision?> FindPendingAsync(
        LocalAccountScope scope,
        string nodeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        outbox.FindPendingAsync(scope, nodeId, idempotencyKey, cancellationToken);

    public Task<IReadOnlyList<LocalAsrPendingResourceDecision>> ListPendingAsync(
        LocalAccountScope scope,
        string nodeId,
        CancellationToken cancellationToken = default) =>
        outbox.ListPendingAsync(scope, nodeId, cancellationToken);

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

        if (frozen.RequestPayload is null
            || !string.Equals(request.RuntimeId, frozen.RequestPayload.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(request.ModelId, frozen.RequestPayload.ModelId, StringComparison.Ordinal)
            || !string.Equals(request.ModelRevision, frozen.RequestPayload.ModelRevision, StringComparison.Ordinal)
            || !string.Equals(request.ModelSha256, frozen.RequestPayload.ModelSha256,
                StringComparison.OrdinalIgnoreCase))
            return new(LocalAsrClaimStatus.Rejected, null, LocalRuntimeReasonCodes.InvalidConfiguration);

        if (!frozen.Decision.Passed && transport is not ILocalAsrResourceDecisionTransport)
        {
            // No request was sent, so a local-only rejection must not pin future
            // manual attempts behind an intent this transport cannot report.
            if (!frozen.Acknowledged)
                await outbox.RemovePendingAsync(frozen, cancellationToken).ConfigureAwait(false);
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
            if (!frozen.Decision.Passed
                && (result.Status == LocalAsrClaimStatus.Claimed || result.Task is not null))
            {
                // A rejected resource decision must never be allowed to carry a lease
                // into the worker, even if a faulty server adapter returns one.
                return new(LocalAsrClaimStatus.Rejected, null, "resource_decision_claimed_despite_block");
            }
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
