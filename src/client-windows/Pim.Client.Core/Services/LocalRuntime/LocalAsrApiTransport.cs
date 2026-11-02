using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Freshly verifies the installed runtime/model paths and digests for the active
/// account. Returning false keeps registration and heartbeat off the network.
/// </summary>
public delegate Task<bool> LocalAsrInstalledArtifactVerifier(
    LocalRuntimeManifest runtime,
    LocalModelManifest model,
    CancellationToken cancellationToken);

/// <summary>
/// Bounded, account-pinned adapter over the generated native ASR HTTP operations.
/// Network routing always comes from the generated operation path; server-supplied
/// content URLs are treated as untrusted evidence and are never followed.
/// </summary>
public sealed class LocalAsrApiTransport : ILocalAsrNodeTransport, ILocalAsrResourceDecisionTransport
{
    public const string LocalAsrCapability = "local-asr";
    public const int DefaultLeaseSeconds = 120;

    private const int MaximumReasonLength = 512;
    private const int MaximumResourceSnapshotJsonLength = 16_384;
    private const int MaximumTrackedLeases = 64;

    private readonly ApiClient _api;
    private readonly Func<LocalAccountScope?> _currentScope;
    private readonly LocalAsrInstalledArtifactVerifier _verifyInstalledArtifacts;
    private readonly TimeSpan _requestTimeout;
    private readonly long _maxSegmentBytes;
    private readonly object _leaseSync = new();
    private readonly Dictionary<string, BoundLease> _boundLeases =
        new(StringComparer.Ordinal);

    /// <param name="apiClient">Shared typed contract client (never used with a raw URL).</param>
    /// <param name="currentScope">Live account scope; is checked again after every await.</param>
    /// <param name="verifyInstalledArtifacts">
    /// Fresh digest/path verification against the installed runtime and model.
    /// Manifest metadata alone is not installation or digest proof.
    /// </param>
    /// <param name="requestTimeout">Per HTTP call bound; defaults to 15 seconds.</param>
    /// <param name="maxSegmentBytes">Streaming and declared-length cap for one WAV segment.</param>
    public LocalAsrApiTransport(
        ApiClient apiClient,
        Func<LocalAccountScope?> currentScope,
        LocalAsrInstalledArtifactVerifier verifyInstalledArtifacts,
        TimeSpan? requestTimeout = null,
        long maxSegmentBytes = 128L * 1024 * 1024)
    {
        _api = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _currentScope = currentScope ?? throw new ArgumentNullException(nameof(currentScope));
        _verifyInstalledArtifacts = verifyInstalledArtifacts
            ?? throw new ArgumentNullException(nameof(verifyInstalledArtifacts));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (maxSegmentBytes <= 0 || maxSegmentBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxSegmentBytes));
        _maxSegmentBytes = maxSegmentBytes;
    }

    public async Task<NoteAsrNodeDto?> RegisterAsync(
        string nodeId,
        string nodeIdExternal,
        LocalRuntimeManifest runtime,
        LocalModelManifest model,
        bool enabled = true,
        string? resourceSnapshotJson = null,
        DateTimeOffset? sampledAt = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireScope();
        var prepared = ValidateRegistration(
            scope,
            nodeId,
            nodeIdExternal,
            runtime,
            model,
            enabled,
            resourceSnapshotJson,
            sampledAt);
        await VerifyInstalledArtifactsAsync(
                scope,
                runtime,
                model,
                cancellationToken)
            .ConfigureAwait(false);

        var response = await ExecuteAsync(
            scope,
            (api, token) => api.RegisterNativeAsrNodeAsync(
                new RegisterNoteAsrNodeRequest
                {
                    NodeId = prepared.NodeId.ToString("D"),
                    NodeIdExternal = nodeIdExternal,
                    Enabled = prepared.Enabled,
                    ModelId = prepared.ModelId,
                    ModelRevision = prepared.ModelRevision,
                    ModelVerified = true,
                    Capabilities = prepared.Capabilities,
                    RuntimeId = prepared.RuntimeId,
                    RuntimeVersion = prepared.RuntimeVersion,
                    ResourceSnapshotJson = prepared.ResourceSnapshotJson,
                    SampledAt = sampledAt,
                },
                token),
            cancellationToken).ConfigureAwait(false);

        return ValidateNodeResponse(scope, response, prepared, enabledExpected: prepared.Enabled);
    }

    public async Task<NoteAsrNodeDto?> HeartbeatAsync(
        string nodeId,
        LocalRuntimeManifest runtime,
        LocalModelManifest model,
        string? resourceSnapshotJson = null,
        DateTimeOffset? sampledAt = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireScope();
        var prepared = ValidateRegistration(
            scope,
            nodeId,
            nodeIdExternal: "heartbeat",
            runtime,
            model,
            enabled: true,
            resourceSnapshotJson,
            sampledAt);
        await VerifyInstalledArtifactsAsync(
                scope,
                runtime,
                model,
                cancellationToken)
            .ConfigureAwait(false);

        var response = await ExecuteAsync(
            scope,
            (api, token) => api.HeartbeatNativeAsrNodeAsync(
                prepared.NodeId.ToString("D"),
                new NoteAsrNodeHeartbeatRequest
                {
                    NodeId = prepared.NodeId.ToString("D"),
                    ModelId = prepared.ModelId,
                    ModelRevision = prepared.ModelRevision,
                    ModelVerified = true,
                    Capabilities = prepared.Capabilities,
                    RuntimeId = prepared.RuntimeId,
                    RuntimeVersion = prepared.RuntimeVersion,
                    ResourceSnapshotJson = prepared.ResourceSnapshotJson,
                    SampledAt = sampledAt,
                },
                token),
            cancellationToken).ConfigureAwait(false);

        return ValidateNodeResponse(scope, response, prepared, enabledExpected: true);
    }

    public async Task<LocalAsrWithdrawResult> WithdrawAsync(
        LocalAsrWithdrawRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope is null)
            return new(LocalAsrWithdrawStatus.Rejected, LocalRuntimeReasonCodes.AccountRequired);
        var scope = RequireScope();
        if (!scope.Matches(request.Scope))
            return new(LocalAsrWithdrawStatus.Rejected, LocalRuntimeReasonCodes.ScopeChanged);
        if (!TryGuid(request.NodeId, out var nodeId))
            return new(LocalAsrWithdrawStatus.Rejected, "invalid_node");
        if (request.Reason is null || request.Reason.Length > MaximumReasonLength)
            return new(LocalAsrWithdrawStatus.Rejected, "invalid_reason");

        try
        {
            var response = await ExecuteAsync(
                scope,
                (api, token) => api.WithdrawNativeAsrNodeAsync(
                    nodeId.ToString("D"),
                    token),
                cancellationToken).ConfigureAwait(false);

            if (response is null || response.Code != 0 || response.Data is null)
            {
                var code = response?.Code;
                var detail = response?.Message;
                if (code is 404 or 40401 || detail?.Contains("不存在", StringComparison.Ordinal) == true)
                    return new(LocalAsrWithdrawStatus.AlreadyWithdrawn);
                return new(LocalAsrWithdrawStatus.Rejected, ApiDetail(code, detail));
            }

            var node = response.Data;
            var error = ValidateNodeIdentity(scope, node, nodeId, verified: false);
            if (error is not null)
                return new(LocalAsrWithdrawStatus.Rejected, error);
            if (node.Enabled)
                return new(LocalAsrWithdrawStatus.Rejected, "withdraw_not_applied");
            RemoveBoundLeases(nodeId.ToString("D"));
            return new(LocalAsrWithdrawStatus.Withdrawn);
        }
        catch (HttpRequestException error) when (
            error.StatusCode is HttpStatusCode.NotFound)
        {
            return new(LocalAsrWithdrawStatus.AlreadyWithdrawn);
        }
        catch (HttpRequestException error) when (IsTransientStatus(error.StatusCode))
        {
            return new(LocalAsrWithdrawStatus.TransientFailure, HttpDetail(error.StatusCode, error.Message));
        }
        catch (HttpRequestException error) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrWithdrawStatus.Rejected, HttpDetail(error.StatusCode, error.Message));
        }
        catch (Exception error) when (
            error is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrWithdrawStatus.TransientFailure, ApiDetail(null, error.Message));
        }
    }

    public Task<LocalAsrClaimResult> ClaimAsync(
        LocalAsrClaimRequest request,
        CancellationToken cancellationToken = default) =>
        ClaimCoreAsync(request, resourceDecision: null, cancellationToken);

    public Task<LocalAsrClaimResult> ClaimWithResourceDecisionAsync(
        LocalAsrClaimRequest request,
        LocalAsrFrozenResourceDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return ClaimCoreAsync(request, decision, cancellationToken);
    }

    private async Task<LocalAsrClaimResult> ClaimCoreAsync(
        LocalAsrClaimRequest request,
        LocalAsrFrozenResourceDecision? resourceDecision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope is null)
            return new(LocalAsrClaimStatus.Rejected, null, LocalRuntimeReasonCodes.AccountRequired);
        var scope = RequireScope();
        if (!scope.Matches(request.Scope))
            return new(LocalAsrClaimStatus.Rejected, null, LocalRuntimeReasonCodes.ScopeChanged);
        if (!TryGuid(request.NodeId, out var nodeId))
            return new(LocalAsrClaimStatus.Rejected, null, "invalid_node");
        if (!IsBoundedText(request.IdempotencyKey, 128)
            || !LocalModelManifest.IsValidModelId(request.ModelId)
            || !IsBoundedText(request.ModelRevision, 128)
            || !LocalModelManifest.IsValidSha256(request.ModelSha256)
            || !IsBoundedText(request.RuntimeId, 128))
        {
            return new(LocalAsrClaimStatus.Rejected, null, "invalid_claim_request");
        }
        if (resourceDecision is not null
            && (resourceDecision.SampledAtUtc == default
                || resourceDecision.Reasons is null
                || resourceDecision.Reasons.Count > 8
                || resourceDecision.Reasons.Any(reason => !IsBoundedText(reason, 64))
                || resourceDecision.Reasons.Distinct(StringComparer.Ordinal).Count()
                    != resourceDecision.Reasons.Count
                || resourceDecision.Passed && resourceDecision.Reasons.Count != 0
                || !resourceDecision.Passed && resourceDecision.Reasons.Count == 0))
        {
            return new(LocalAsrClaimStatus.Rejected, null, "invalid_resource_gate_decision");
        }

        NoteAsrClaimDtoApiResponse? response;
        try
        {
            response = await ExecuteAsync(
                scope,
                (api, token) => api.ClaimNativeAsrLeaseAsync(
                    new ClaimNoteAsrTaskRequest
                    {
                        NodeId = nodeId.ToString("D"),
                        IdempotencyKey = request.IdempotencyKey,
                        LeaseSeconds = DefaultLeaseSeconds,
                        ResourceGate = resourceDecision is null
                            ? null
                            : new NoteAsrResourceGateDecision
                            {
                                IsAutomatic = resourceDecision.IsAutomatic,
                                Passed = resourceDecision.Passed,
                                Reasons = resourceDecision.Reasons.ToList(),
                                SampledAt = resourceDecision.SampledAtUtc,
                            },
                    },
                    token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (IsTransientStatus(error.StatusCode))
        {
            return new(
                LocalAsrClaimStatus.Deferred,
                null,
                    HttpDetail(error.StatusCode, error.Message),
                DateTimeOffset.UtcNow.AddSeconds(5));
        }
        catch (HttpRequestException error) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrClaimStatus.Rejected, null, HttpDetail(error.StatusCode, error.Message));
        }
        catch (Exception error) when (
            error is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return new(
                LocalAsrClaimStatus.Deferred,
                null,
                    ApiDetail(null, error.Message),
                DateTimeOffset.UtcNow.AddSeconds(5));
        }

        if (response is null || response.Code != 0 || response.Data is null)
            return new(
                LocalAsrClaimStatus.Rejected,
                null,
                ApiDetail(response?.Code, response?.Message));

        var claim = response.Data;
        var status = claim.Status?.Trim().ToLowerInvariant();
        switch (status)
        {
            case "resource_blocked":
            case "manual_required":
                return new(
                    LocalAsrClaimStatus.ResourceBlocked,
                    null,
                    string.Join(",", claim.ResourceFailureReasons ?? resourceDecision?.Reasons ?? []),
                    ResourceDecisionAccepted: resourceDecision is not null);
            case "empty":
            case "contended":
            case "replayed":
                return new(LocalAsrClaimStatus.NoWork, null, status,
                    ResourceDecisionAccepted: resourceDecision is not null);
            case "unavailable":
                return new(
                    LocalAsrClaimStatus.Deferred,
                    null,
                    status,
                    DateTimeOffset.UtcNow.AddSeconds(5),
                    ResourceDecisionAccepted: resourceDecision is not null);
            case "lease_expired":
                return new(
                    LocalAsrClaimStatus.Deferred,
                    null,
                    status,
                    DateTimeOffset.UtcNow.AddSeconds(1),
                    ResourceDecisionAccepted: resourceDecision is not null);
            case not "claimed":
                return new(LocalAsrClaimStatus.Rejected, null, "unknown_claim_status");
        }

        try
        {
            var task = MapClaim(
                scope,
                claim,
                nodeId,
                request);
            BindLease(scope, nodeId, task);
            RevalidateScope(scope);
            return new(LocalAsrClaimStatus.Claimed, task,
                ResourceDecisionAccepted: resourceDecision is not null);
        }
        catch (LocalAsrTransportValidationException error)
        {
            return new(LocalAsrClaimStatus.Rejected, null, error.ReasonCode);
        }
    }

    public async Task<LocalAsrRenewResult> RenewAsync(
        LocalAsrLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var scope = RequireScope();
        if (!TryValidLease(lease, out var leaseId, out var leaseToken))
            return new(LocalAsrRenewStatus.Rejected, null, false, "invalid_lease");
        var bound = RequireBoundLease(scope, lease);

        try
        {
            var response = await ExecuteAsync(
                scope,
                (api, token) => api.RenewNativeAsrLeaseAsync(
                    leaseId.ToString("D"),
                    new NoteAsrLeaseHeartbeatRequest
                    {
                        LeaseId = leaseId.ToString("D"),
                        LeaseToken = leaseToken.ToString("D"),
                        FencingToken = lease.FencingToken,
                    },
                    token),
                cancellationToken).ConfigureAwait(false);

            if (response is null || response.Code != 0 || response.Data is null)
                return new(
                    LocalAsrRenewStatus.Rejected,
                    null,
                    false,
                    ApiDetail(response?.Code, response?.Message));

            var renewed = MapLease(scope, response.Data, lease, requireSameIdentity: true);
            if (renewed.LeaseId.ToString("D") != bound.LeaseId
                || renewed.AttemptId.ToString("D") != bound.AttemptId)
                throw new LocalAsrTransportValidationException("lease_identity_changed");
            RevalidateScope(scope);
            if (renewed.StopRequested)
                return new(LocalAsrRenewStatus.StopRequested, ToLocalLease(renewed), true);
            var renewedLease = ToLocalLease(renewed);
            UpdateBoundLease(renewedLease);
            return new(LocalAsrRenewStatus.Renewed, renewedLease);
        }
        catch (HttpRequestException error) when (
            error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return new(LocalAsrRenewStatus.LeaseExpired, null, false, HttpDetail(error.StatusCode, error.Message));
        }
        catch (HttpRequestException error) when (IsTransientStatus(error.StatusCode))
        {
            return new(LocalAsrRenewStatus.TransientFailure, null, false, HttpDetail(error.StatusCode, error.Message));
        }
        catch (HttpRequestException error) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrRenewStatus.Rejected, null, false, HttpDetail(error.StatusCode, error.Message));
        }
        catch (LocalAsrTransportValidationException error)
        {
            return new(LocalAsrRenewStatus.Rejected, null, false, error.ReasonCode);
        }
        catch (Exception error) when (
            error is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrRenewStatus.TransientFailure, null, false, ApiDetail(null, error.Message));
        }
    }

    public async Task<LocalAsrSegmentContent> OpenSegmentContentAsync(
        LocalAsrLease lease,
        LocalAsrInputSegment segment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(segment);
        var scope = RequireScope();
        if (!TryValidLease(lease, out var leaseId, out var leaseToken))
            throw new InvalidOperationException("renew response did not preserve a valid lease");
        var bound = RequireBoundLease(scope, lease);
        if (segment.SegmentIndex < 0
            || segment.Samples <= 0
            || segment.SourceOffsetSamples < 0
            || !LocalModelManifest.IsValidSha256(segment.SourceSha256))
            throw new InvalidOperationException("claimed segment identity is incomplete");
        if (!TryGuid(segment.LeaseId, out var segmentLeaseId) || segmentLeaseId != leaseId)
            throw new InvalidOperationException("segment lease does not match the held lease");
        if (!TryGuid(segment.AttemptId, out var segmentAttemptId)
            || !string.Equals(segmentAttemptId.ToString("D"), lease.AttemptId, StringComparison.Ordinal))
            throw new InvalidOperationException("segment attempt does not match the held lease");
        if (segment.FencingToken is not { } segmentFence || segmentFence != lease.FencingToken)
            throw new InvalidOperationException("segment fence does not match the held lease");

        cancellationToken.ThrowIfCancellationRequested();
        RevalidateScope(scope);
        var timeout = new CancellationTokenSource(_requestTimeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        NativeAsrSegmentDownload? download = null;
        try
        {
            download = await _api.GetNativeAsrSegmentContentAsync(
                    leaseId.ToString("D"),
                    segment.SegmentIndex,
                    leaseToken.ToString("D"),
                    lease.FencingToken,
                    _maxSegmentBytes,
                    linked.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("native ASR segment response was empty");

            RevalidateScope(scope);
            var headers = download.Headers;
            var contentEndpoint = ClaimContentRoute(leaseId, segment.SegmentIndex);
            ValidateContentHeaders(
                scope,
                bound.NodeId,
                lease,
                segment,
                headers,
                contentEndpoint);

            var binding = new ApiContentResponseBinding(
                headers.AttemptId.ToString("D"),
                headers.VersionId.ToString("D"),
                headers.LeaseId.ToString("D"),
                headers.FencingToken,
                headers.SegmentIndex,
                headers.SourceSha256,
                headers.SourceOffsetSamples,
                headers.Samples,
                headers.ContentSha256);

            RevalidateScope(scope);
            return new LocalAsrSegmentContent(
                new TimedOwnedSegmentStream(download.Content, linked, timeout),
                scope.ServerBaseAddress,
                scope.OwnerIdentity,
                headers.NodeId.ToString("D"),
                binding);
        }
        catch
        {
            if (download is not null)
                await download.DisposeAsync().ConfigureAwait(false);
            linked.Dispose();
            timeout.Dispose();
            throw;
        }
    }

    public async Task<LocalAsrAckResult> AcknowledgeAsync(
        LocalAsrLease lease,
        LocalAsrAckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);
        var scope = RequireScope();
        if (!TryValidLease(lease, out var leaseId, out var leaseToken))
            return new(LocalAsrAckStatus.Rejected, "invalid_lease");
        _ = RequireBoundLease(scope, lease);
        if (!TryGuid(request.AttemptId, out var attemptId)
            || !string.Equals(attemptId.ToString("D"), lease.AttemptId, StringComparison.Ordinal)
            || !Guid.TryParse(request.VersionId, out var versionId)
            || !string.Equals(versionId.ToString("D"), lease.VersionId, StringComparison.Ordinal)
            || !Guid.TryParse(request.LeaseId, out var requestLeaseId)
            || requestLeaseId != leaseId
            || request.FencingToken != lease.FencingToken)
        {
            return new(LocalAsrAckStatus.Rejected, "ack_identity_mismatch");
        }

        if (!TryMapAck(request, leaseToken, out var body, out var reason))
            return new(LocalAsrAckStatus.Rejected, reason);

        try
        {
            var response = await ExecuteAsync(
                scope,
                (api, token) => api.AckNativeAsrAttemptAsync(
                    attemptId.ToString("D"),
                    body,
                    token),
                cancellationToken).ConfigureAwait(false);
            if (response is null || response.Code != 0 || response.Data is null)
                return new(
                    LocalAsrAckStatus.Rejected,
                    ApiDetail(response?.Code, response?.Message));
            var ack = response.Data;
            if (!Guid.TryParse(ack.LeaseId, out var ackLeaseId)
                || ackLeaseId != leaseId
                || ack.FencingToken != lease.FencingToken)
            {
                return new(LocalAsrAckStatus.Rejected, "ack_response_mismatch");
            }

            RevalidateScope(scope);
            return ack.Duplicate
                || string.Equals(ack.Status, "duplicate", StringComparison.OrdinalIgnoreCase)
                ? new(LocalAsrAckStatus.Accepted, "duplicate")
                : new(LocalAsrAckStatus.Accepted);
        }
        catch (HttpRequestException error) when (
            error.StatusCode is HttpStatusCode.Conflict)
        {
            return new(LocalAsrAckStatus.Rejected, HttpDetail(error.StatusCode, error.Message));
        }
        catch (HttpRequestException error) when (IsTransientStatus(error.StatusCode))
        {
            return new(LocalAsrAckStatus.TransientFailure, HttpDetail(error.StatusCode, error.Message));
        }
        catch (HttpRequestException error) when (!cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrAckStatus.Rejected, HttpDetail(error.StatusCode, error.Message));
        }
        catch (Exception error) when (
            error is not OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return new(LocalAsrAckStatus.TransientFailure, ApiDetail(null, error.Message));
        }
    }

    internal static string ClaimContentRoute(Guid leaseId, int segmentIndex)
    {
        var operation = ContractOperation.GetNativeAsrSegmentContent;
        _ = operation.Resolve(
            ("leaseId", leaseId.ToString("D")),
            ("segmentIndex", segmentIndex.ToString(CultureInfo.InvariantCulture)));
        return operation.PathTemplate
            .Replace("{leaseId}", Uri.EscapeDataString(leaseId.ToString("D")), StringComparison.Ordinal)
            .Replace(
                "{segmentIndex}",
                Uri.EscapeDataString(segmentIndex.ToString(CultureInfo.InvariantCulture)),
                StringComparison.Ordinal);
    }

    internal static List<string> CapabilitiesFor(
        LocalRuntimeManifest runtime,
        LocalModelManifest model)
    {
        if (!runtime.HasVerifiedRuntime)
            throw new InvalidOperationException("runtime manifest is not verified");
        if (!model.HasVerifiedArtifact)
            throw new InvalidOperationException("model manifest is not verified");
        if (!string.Equals(runtime.RuntimeId, model.RuntimeId, StringComparison.Ordinal))
            throw new InvalidOperationException("model does not belong to the runtime");
        if (!LocalModelManifest.IsValidModelId(model.ModelId))
            throw new InvalidOperationException("model id is not canonical");
        return [LocalAsrCapability];
    }

    private static PreparedRegistration ValidateRegistration(
        LocalAccountScope scope,
        string nodeId,
        string nodeIdExternal,
        LocalRuntimeManifest runtime,
        LocalModelManifest model,
        bool enabled,
        string? resourceSnapshotJson,
        DateTimeOffset? sampledAt)
    {
        if (!TryGuid(nodeId, out var parsedNodeId))
            throw new ArgumentException("nodeId must be a non-empty UUID", nameof(nodeId));
        if (!IsBoundedText(nodeIdExternal, 128))
            throw new ArgumentException("nodeIdExternal is invalid", nameof(nodeIdExternal));
        if (resourceSnapshotJson is not null
            && (resourceSnapshotJson.Length > MaximumResourceSnapshotJsonLength
                || !IsJsonObject(resourceSnapshotJson)))
            throw new ArgumentException("resource snapshot must be a bounded JSON object", nameof(resourceSnapshotJson));
        if (sampledAt is { } sampled && sampled < DateTimeOffset.UtcNow.AddMinutes(-60))
            throw new ArgumentException("sampledAt is stale", nameof(sampledAt));
        if (scope.OwnerIdentity.Length == 0)
            throw new InvalidOperationException("active owner is empty");

        var capabilities = CapabilitiesFor(runtime, model);
        return new PreparedRegistration(
            parsedNodeId,
            enabled,
            model.ModelId,
            model.Revision,
            runtime.RuntimeId,
            runtime.Revision,
            capabilities,
            resourceSnapshotJson);
    }

    private static NoteAsrNodeDto? ValidateNodeResponse(
        LocalAccountScope scope,
        NoteAsrNodeDtoApiResponse? response,
        PreparedRegistration prepared,
        bool enabledExpected)
    {
        if (response is null || response.Code != 0 || response.Data is null)
            throw new LocalAsrTransportValidationException(
                ApiDetail(response?.Code, response?.Message));

        var node = response.Data;
        var error = ValidateNodeIdentity(scope, node, prepared.NodeId, verified: true);
        if (error is not null)
            throw new LocalAsrTransportValidationException(error);
        if (node.Enabled != enabledExpected
            || !node.ModelVerified
            || !string.Equals(node.ModelId, prepared.ModelId, StringComparison.Ordinal)
            || !string.Equals(node.ModelRevision, prepared.ModelRevision, StringComparison.Ordinal)
            || !string.Equals(node.RuntimeId, prepared.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(node.RuntimeVersion, prepared.RuntimeVersion, StringComparison.Ordinal)
            || node.Capabilities is null
            || !node.Capabilities.SequenceEqual([LocalAsrCapability], StringComparer.Ordinal))
        {
            throw new LocalAsrTransportValidationException("node_registration_unverified");
        }

        return node;
    }

    private static string? ValidateNodeIdentity(
        LocalAccountScope scope,
        NoteAsrNodeDto node,
        Guid nodeId,
        bool verified)
    {
        if (!Guid.TryParse(node.NodeId, out var returnedNodeId))
            return "invalid_node";
        if (returnedNodeId != nodeId)
            return "node_identity_mismatch";
        if (verified && (!node.Enabled || !node.ModelVerified))
                return "node_registration_unverified";
        if (scope.OwnerIdentity.Length == 0)
            return "owner_missing";
        return null;
    }

    private static LocalAsrClaimedTask MapClaim(
        LocalAccountScope scope,
        NoteAsrClaimDto claim,
        Guid nodeId,
        LocalAsrClaimRequest request)
    {
        if (claim.Lease is null || claim.Execution is null)
            throw new LocalAsrTransportValidationException("claim_missing_lease_or_execution");
        var lease = MapLease(scope, claim.Lease, expectedLease: null, requireSameIdentity: false);
        var execution = claim.Execution;

        if (!Guid.TryParse(execution.AttemptId, out var attemptId)
            || !Guid.TryParse(execution.VersionId, out var versionId)
            || !Guid.TryParse(execution.OwnerId, out var ownerId)
            || !Guid.TryParse(execution.TaskId, out var taskId))
            throw new LocalAsrTransportValidationException("claim_invalid_identity");
        if (!string.Equals(ownerId.ToString("D"), scope.OwnerIdentity, StringComparison.Ordinal)
            || !string.Equals(ownerId.ToString("D"), lease.OwnerId, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("claim_owner_mismatch");
        if (!string.Equals(attemptId.ToString("D"), lease.AttemptIdText, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("claim_attempt_lease_mismatch");
        if (!string.Equals(claim.Lease.LeaseId, claim.LeaseId, StringComparison.Ordinal)
            || !Guid.TryParse(claim.LeaseId, out var claimLeaseId)
            || claimLeaseId != lease.LeaseId)
            throw new LocalAsrTransportValidationException("claim_lease_id_mismatch");
        var executionOrigin = RequireCanonicalBaseAddress(
            execution.ServerOrigin,
            scope,
            "claim_server_origin_invalid");
        if (!string.Equals(executionOrigin, scope.ServerBaseAddress, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("claim_cross_server");
        if (!string.Equals(executionOrigin, lease.ServerOrigin, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("claim_origin_mismatch");
        if (!LocalModelManifest.IsValidSha256(execution.InputSha256))
            throw new LocalAsrTransportValidationException("claim_input_digest_missing");
        if (execution.ExpectedSegments <= 0
            || execution.Segments is null
            || execution.Segments.Count > execution.ExpectedSegments)
            throw new LocalAsrTransportValidationException("claim_segment_bound");
        if (!string.Equals(execution.Route, "local", StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("claim_route_not_local");
        if (!LocalModelManifest.IsValidModelId(request.ModelId)
            || !string.Equals(execution.LocalModelId, request.ModelId, StringComparison.Ordinal)
            || !string.Equals(execution.LocalModelRevision, request.ModelRevision, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("claim_model_mismatch");
        if (string.IsNullOrWhiteSpace(execution.Language)
            || execution.Language.Length > 32
            || execution.Language.Any(char.IsControl))
            throw new LocalAsrTransportValidationException("claim_language_invalid");

        var indexes = new HashSet<int>();
        var segments = new List<LocalAsrInputSegment>(execution.Segments.Count);
        foreach (var item in execution.Segments)
        {
            if (item.Index < 0
                || item.Index >= execution.ExpectedSegments
                || !indexes.Add(item.Index)
                || item.SourceFileIndex < 0
                || item.SourceOffsetSamples < 0
                || item.Samples <= 0
                || !LocalModelManifest.IsValidSha256(item.SourceSha256))
                throw new LocalAsrTransportValidationException("claim_segment_invalid");

            if (!TryGuid(item.RecordingId, out _))
                throw new LocalAsrTransportValidationException("claim_segment_recording_invalid");
            if (!string.Equals(
                    item.ContentEndpoint,
                    ClaimContentRoute(lease.LeaseId, item.Index),
                    StringComparison.Ordinal))
                throw new LocalAsrTransportValidationException("claim_content_endpoint_mismatch");

            segments.Add(new LocalAsrInputSegment(
                item.Index,
                item.SourceOffsetSamples,
                item.Samples,
                item.SourceFileIndex,
                item.SourceSha256.ToLowerInvariant(),
                ExpectedContentBytes: null,
                ExpectedContentSha256: null,
                LeaseId: lease.LeaseIdText,
                AttemptId: lease.AttemptIdText,
                VersionId: execution.VersionId,
                FencingToken: lease.FencingToken));
        }

        // A claim has no authoritative total source sample count, and a
        // max(offset + samples) calculation is not a total for multi-file input.
        // Keep the domain value explicitly unknown; each segment is bounded
        // independently by its actual downloaded bytes and response metadata.
        return new LocalAsrClaimedTask(
            lease.AttemptIdText,
            taskId.ToString("D"),
            execution.VersionId,
            execution.Language,
            request.RuntimeId,
            request.ModelId,
            request.ModelRevision,
            request.ModelSha256,
            execution.InputSha256.ToLowerInvariant(),
            null,
            execution.ExpectedSegments,
            segments,
            new LocalAsrLease(
                lease.LeaseIdText,
                lease.LeaseToken.ToString("D"),
                lease.FencingToken,
                lease.AttemptIdText,
                execution.VersionId,
                lease.ServerNow,
                lease.ExpiresAt,
                lease.ServerNow));
    }

    private static MappedLease MapLease(
        LocalAccountScope scope,
        NoteAsrLeaseDto dto,
        LocalAsrLease? expectedLease,
        bool requireSameIdentity)
    {
        if (!TryGuid(dto.LeaseId, out var leaseId)
            || !TryGuid(dto.LeaseToken, out var leaseToken)
            || !TryGuid(dto.AttemptId, out var attemptId)
            || dto.FencingToken <= 0
            || dto.ExpiresAt <= dto.ServerNow
            || dto.ServerNow == default
            || dto.ServerNow < DateTimeOffset.UtcNow.AddHours(-1)
            || dto.ServerNow > DateTimeOffset.UtcNow.AddHours(1))
            throw new LocalAsrTransportValidationException("lease_invalid");

        var origin = RequireCanonicalBaseAddress(
            dto.ServerOrigin,
            scope,
            "lease_server_origin_invalid");
        if (!string.Equals(origin, scope.ServerBaseAddress, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("lease_cross_server");
        if (!Guid.TryParse(dto.OwnerId, out var ownerId))
            throw new LocalAsrTransportValidationException("lease_owner_missing");
        if (!Guid.TryParse(scope.OwnerIdentity, out var scopeOwnerId)
            || scopeOwnerId != ownerId)
            throw new LocalAsrTransportValidationException("lease_owner_mismatch");

        if (expectedLease is not null)
        {
            if (!TryGuid(expectedLease.LeaseId, out var expectedLeaseId)
                || !TryGuid(expectedLease.LeaseToken, out var expectedLeaseToken)
                || expectedLeaseId != leaseId
                || expectedLeaseToken != leaseToken
                || !string.Equals(expectedLease.AttemptId, attemptId.ToString("D"), StringComparison.Ordinal)
                || expectedLease.FencingToken != dto.FencingToken)
                throw new LocalAsrTransportValidationException("lease_identity_changed");
        }

        if (dto.StopRequested && !requireSameIdentity)
            throw new LocalAsrTransportValidationException("lease_stop_unexpected");

        return new MappedLease(
            leaseId,
            leaseToken,
            dto.FencingToken,
            attemptId,
            expectedLease?.VersionId ?? string.Empty,
            ownerId.ToString("D"),
            dto.StopRequested,
            dto.ServerNow,
            dto.ExpiresAt,
            origin);
    }

    private static void ValidateContentHeaders(
        LocalAccountScope scope,
        string expectedNodeId,
        LocalAsrLease lease,
        LocalAsrInputSegment segment,
        NativeAsrSegmentHeaders headers,
        string expectedRoute)
    {
        if (!string.Equals(headers.LeaseId.ToString("D"), lease.LeaseId, StringComparison.Ordinal)
            || !string.Equals(headers.AttemptId.ToString("D"), lease.AttemptId, StringComparison.Ordinal)
            || !string.Equals(headers.VersionId.ToString("D"), lease.VersionId, StringComparison.Ordinal)
            || !string.Equals(headers.NodeId.ToString("D"), expectedNodeId, StringComparison.Ordinal)
            || headers.FencingToken != lease.FencingToken
            || !string.Equals(headers.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal)
            || !string.Equals(
                segment.LeaseId,
                headers.LeaseId.ToString("D"),
                StringComparison.Ordinal)
            || !string.Equals(
                segment.AttemptId,
                headers.AttemptId.ToString("D"),
                StringComparison.Ordinal)
            || segment.FencingToken != headers.FencingToken
            || headers.SegmentIndex != segment.SegmentIndex
            || headers.SourceFileIndex != segment.SourceFileIndex
            || headers.SourceOffsetSamples != segment.SourceOffsetSamples
            || headers.Samples != segment.Samples
            || !string.Equals(
                headers.SourceSha256,
                segment.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalAsrTransportValidationException("content_identity_mismatch");
        }

        if (expectedRoute.Length == 0)
            throw new LocalAsrTransportValidationException("content_route_missing");
    }

    private static bool TryMapAck(
        LocalAsrAckRequest request,
        Guid leaseToken,
        out NoteAsrAckRequest body,
        out string reason)
    {
        body = null!;
        reason = string.Empty;
        var (kind, result) = request.Kind switch
        {
            LocalAsrAckKind.SegmentSucceeded => ("segment", "Succeeded"),
            LocalAsrAckKind.SegmentFailed => ("segment", "Failed"),
            LocalAsrAckKind.AttemptCompleted => ("complete", "Succeeded"),
            LocalAsrAckKind.AttemptFailed => ("fail", "Failed"),
            _ => (null, null),
        };
        if (kind is null || result is null)
        {
            reason = "invalid_ack_kind";
            return false;
        }

        if (request.Kind is LocalAsrAckKind.SegmentSucceeded or LocalAsrAckKind.SegmentFailed
            && (request.SegmentIndex is null
                || request.SourceOffsetSamples is null
                || request.Samples is null))
        {
            reason = "segment_ack_missing_identity";
            return false;
        }

        if (request.Kind is LocalAsrAckKind.AttemptCompleted or LocalAsrAckKind.AttemptFailed
            && request.SegmentIndex is not null)
        {
            reason = "terminal_ack_segment_index";
            return false;
        }

        var detail = request.Detail;
        if (detail is not null
            && (detail.Length > 2048 || detail.Any(char.IsControl)))
        {
            reason = "ack_detail_invalid";
            return false;
        }

        if (request.Kind is LocalAsrAckKind.SegmentFailed or LocalAsrAckKind.AttemptFailed
            && !IsBoundedText(request.ErrorCode, 128))
        {
            reason = "ack_error_code_missing";
            return false;
        }
        if (request.Kind == LocalAsrAckKind.SegmentSucceeded
            && (!IsBoundedText(request.Text, 64 * 1024) || !IsBoundedText(request.OutputSha256, 64)))
        {
            reason = "ack_result_not_available";
            return false;
        }

        body = new NoteAsrAckRequest
        {
            LeaseId = request.LeaseId,
            LeaseToken = leaseToken.ToString("D"),
            FencingToken = request.FencingToken,
            Kind = kind,
            Result = result,
            SegmentIndex = request.SegmentIndex ?? 0,
            ProviderHandle = request.OutputSha256,
            ErrorCode = request.ErrorCode,
            ErrorMessage = request.Detail,
            ResultText = request.Text,
            CoverageJson = null,
        };
        return true;
    }

    private async Task<T> ExecuteAsync<T>(
        LocalAccountScope scope,
        Func<ApiClient, CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken)
        => await ExecuteWithTimeoutAsync(scope, factory, cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("API returned no response body");

    private async Task<T?> ExecuteWithTimeoutAsync<T>(
        LocalAccountScope scope,
        Func<ApiClient, CancellationToken, Task<T?>> factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevalidateScope(scope);
        using var timeout = new CancellationTokenSource(_requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var response = await factory(_api, linked.Token).ConfigureAwait(false);
        RevalidateScope(scope);
        return response;
    }

    private LocalAccountScope RequireScope()
    {
        var scope = _currentScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.OwnerIdentity))
            throw new LocalAsrTransportValidationException("active_scope_missing");
        return scope;
    }

    private void BindLease(
        LocalAccountScope scope,
        Guid nodeId,
        LocalAsrClaimedTask task)
    {
        var binding = new BoundLease(
            scope.ServerOrigin,
            scope.BasePath,
            scope.OwnerIdentity,
            nodeId.ToString("D"),
            task.AttemptId,
            task.VersionId,
            task.Lease.LeaseId,
            task.Lease.LeaseToken,
            task.Lease.FencingToken,
            task.ExpectedSegments,
            task.Lease.ExpiresAtUtc);
        lock (_leaseSync)
        {
            RemoveExpiredLeases();
            _boundLeases[task.Lease.LeaseId] = binding;
            while (_boundLeases.Count > MaximumTrackedLeases)
            {
                var oldest = _boundLeases.MinBy(pair => pair.Value.ExpiresAtUtc).Key;
                _boundLeases.Remove(oldest);
            }
        }
    }

    private BoundLease RequireBoundLease(
        LocalAccountScope scope,
        LocalAsrLease lease)
    {
        lock (_leaseSync)
        {
            RemoveExpiredLeases();
            if (!_boundLeases.TryGetValue(lease.LeaseId, out var bound))
                throw new LocalAsrTransportValidationException("lease_not_issued_by_transport");
            if (!string.Equals(bound.ServerOrigin, scope.ServerOrigin, StringComparison.Ordinal)
                || !string.Equals(bound.BasePath, scope.BasePath, StringComparison.Ordinal)
                || !string.Equals(bound.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal))
                throw new LocalAsrTransportValidationException("lease_scope_mismatch");
            if (!string.Equals(bound.AttemptId, lease.AttemptId, StringComparison.Ordinal)
                || !string.Equals(bound.VersionId, lease.VersionId, StringComparison.Ordinal)
                || !string.Equals(bound.LeaseToken, lease.LeaseToken, StringComparison.Ordinal)
                || bound.FencingToken != lease.FencingToken)
                throw new LocalAsrTransportValidationException("lease_identity_mismatch");
            return bound;
        }
    }

    private static LocalAsrLease ToLocalLease(MappedLease lease)
        => new(
            lease.LeaseIdText,
            lease.LeaseToken.ToString("D"),
            lease.FencingToken,
            lease.AttemptIdText,
            lease.VersionId,
            lease.ServerNow,
            lease.ExpiresAt,
            lease.ServerNow);

    private void UpdateBoundLease(LocalAsrLease lease)
    {
        lock (_leaseSync)
        {
            if (!_boundLeases.TryGetValue(lease.LeaseId, out var bound))
                return;
            _boundLeases[lease.LeaseId] = bound with
            {
                LeaseToken = lease.LeaseToken,
                FencingToken = lease.FencingToken,
                ExpiresAtUtc = lease.ExpiresAtUtc,
            };
        }
    }

    private void RemoveExpiredLeases()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var key in _boundLeases
            .Where(pair => pair.Value.ExpiresAtUtc < now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _boundLeases.Remove(key);
        }
    }

    private void RemoveBoundLeases(string nodeId)
    {
        lock (_leaseSync)
        {
            foreach (var key in _boundLeases
                .Where(pair => string.Equals(pair.Value.NodeId, nodeId, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToArray())
            {
                _boundLeases.Remove(key);
            }
        }
    }

    private void RevalidateScope(LocalAccountScope scope)
    {
        var current = _currentScope();
        if (current is null || !current.Matches(scope))
            throw new LocalAsrTransportValidationException("scope_changed");

        var expected = new Uri(
                ApiClient.NormalizeServerUrl(scope.ServerBaseAddress).TrimEnd('/') + "/api/v1/")
            .ToString()
            .TrimEnd('/');
        if (!string.Equals(expected, _api.CurrentBaseUrl, StringComparison.Ordinal))
            throw new LocalAsrTransportValidationException("api_server_scope_changed");
    }

    private static string RequireCanonicalBaseAddress(
        string? serverOrigin,
        LocalAccountScope scope,
        string reasonCode)
    {
        if (string.IsNullOrWhiteSpace(serverOrigin))
            throw new LocalAsrTransportValidationException(reasonCode);
        try
        {
            var actual = new LocalAccountScope(serverOrigin, scope.OwnerIdentity);
            return actual.ServerBaseAddress;
        }
        catch (ArgumentException)
        {
            throw new LocalAsrTransportValidationException(reasonCode);
        }
    }

    private async Task VerifyInstalledArtifactsAsync(
        LocalAccountScope scope,
        LocalRuntimeManifest runtime,
        LocalModelManifest model,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _verifyInstalledArtifacts(runtime, model, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new LocalAsrTransportValidationException(
                "runtime_or_model_install_unverified");
        }

        cancellationToken.ThrowIfCancellationRequested();
        RevalidateScope(scope);
        _ = CapabilitiesFor(runtime, model);
    }

    private static bool TryValidLease(
        LocalAsrLease lease,
        out Guid leaseId,
        out Guid leaseToken)
    {
        leaseId = Guid.Empty;
        leaseToken = Guid.Empty;
        if (!TryGuid(lease.LeaseId, out leaseId)
            || !TryGuid(lease.LeaseToken, out leaseToken))
            return false;
        if (!TryGuid(lease.AttemptId, out _)
            || !Guid.TryParse(lease.VersionId, out var versionId)
            || versionId == Guid.Empty
            || lease.FencingToken <= 0
            || lease.ExpiresAtUtc <= lease.ServerNowUtc)
            return false;
        return true;
    }

    private static bool TryGuid(string? value, out Guid guid)
        => Guid.TryParse(value, out guid) && guid != Guid.Empty;

    private static bool IsBoundedText(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= maximumLength
           && !value.Any(char.IsControl);

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTransientStatus(HttpStatusCode? status)
        => status is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
           || (status >= HttpStatusCode.InternalServerError && status < (HttpStatusCode)600);

    private static string ApiDetail(int? code, string? message)
        => code is { } value
            ? $"http_{value}" + (string.IsNullOrWhiteSpace(message) ? string.Empty : ": " + Bounded(message))
            : string.IsNullOrWhiteSpace(message)
                ? "transport_failure"
                : Bounded(message);

    private static string HttpDetail(HttpStatusCode? status, string? message)
    {
        var bounded = string.IsNullOrWhiteSpace(message) ? "transport_failure" : Bounded(message);
        return status is null ? bounded : $"http_{(int)status.Value}: {bounded}";
    }

    private static string Bounded(string value)
        => value.Length <= 256 ? value : value[..256];

    private sealed record PreparedRegistration(
        Guid NodeId,
        bool Enabled,
        string ModelId,
        string ModelRevision,
        string RuntimeId,
        string RuntimeVersion,
        List<string> Capabilities,
        string? ResourceSnapshotJson);

    private sealed record MappedLease(
        Guid LeaseId,
        Guid LeaseToken,
        long FencingToken,
        Guid AttemptId,
        string VersionId,
        string OwnerId,
        bool StopRequested,
        DateTimeOffset ServerNow,
        DateTimeOffset ExpiresAt,
        string ServerOrigin)
    {
        public string LeaseIdText => LeaseId.ToString("D");

        public string AttemptIdText => AttemptId.ToString("D");
    }

    private sealed record BoundLease(
        string ServerOrigin,
        string BasePath,
        string OwnerIdentity,
        string NodeId,
        string AttemptId,
        string VersionId,
        string LeaseId,
        string LeaseToken,
        long FencingToken,
        int ExpectedSegments,
        DateTimeOffset ExpiresAtUtc);
}

/// <summary>Typed, non-secret rejection surfaced as a worker result reason code.</summary>
public sealed class LocalAsrTransportValidationException : Exception
{
    public LocalAsrTransportValidationException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>
/// The timeout covers both reading response headers and consuming the stream.
/// Disposal releases the owned HTTP response through the wrapped content stream.
/// </summary>
internal sealed class TimedOwnedSegmentStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenSource _timeout;
    private int _disposed;

    public TimedOwnedSegmentStream(
        Stream inner,
        CancellationTokenSource linked,
        CancellationTokenSource timeout)
    {
        _inner = inner;
        _linked = linked;
        _timeout = timeout;
    }

    public override bool CanRead => !IsDisposed && _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        return _inner.ReadAsync(buffer.AsMemory(offset, count), _linked.Token)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCall = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _linked.Token);
        return await _inner.ReadAsync(buffer, linkedCall.Token).ConfigureAwait(false);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCoreAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _linked.Dispose();
            _timeout.Dispose();
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
