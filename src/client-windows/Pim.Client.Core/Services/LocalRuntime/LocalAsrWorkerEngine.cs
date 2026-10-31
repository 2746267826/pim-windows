using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

/// <summary>
/// Non-HTTP worker state machine. The future generated ApiClient adapter supplies
/// claims, content and acknowledgements through ILocalAsrNodeTransport; this engine
/// owns resource gating, bounded input validation, process execution and durable
/// account/lease lifecycle evidence.
/// </summary>
public sealed class LocalAsrWorkerEngine : ILocalAsrWorkerRunner
{
    private const int MaximumIdLength = 256;
    private const int MaximumLeaseTokenLength = 4096;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly Regex LanguagePattern =
        new("^(auto|[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILocalAsrNodeTransport _transport;
    private readonly ILocalSpeechRuntimeAdapter _runtimeAdapter;
    private readonly LocalRuntimeAccountGuard _accountGuard;
    private readonly IResourceSnapshotProvider _resourceProvider;
    private readonly RuntimeBackoffStore _backoffStore;
    private readonly LocalAsrWorkerStateStore _stateStore;
    private readonly LocalAsrWorkerOptions _options;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public LocalAsrWorkerEngine(
        ILocalAsrNodeTransport transport,
        ILocalSpeechRuntimeAdapter runtimeAdapter,
        LocalRuntimeAccountGuard accountGuard,
        IResourceSnapshotProvider resourceProvider,
        RuntimeBackoffStore backoffStore,
        LocalAsrWorkerStateStore stateStore,
        LocalAsrWorkerOptions options,
        Func<DateTimeOffset>? now = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _runtimeAdapter = runtimeAdapter ?? throw new ArgumentNullException(nameof(runtimeAdapter));
        _accountGuard = accountGuard ?? throw new ArgumentNullException(nameof(accountGuard));
        _resourceProvider = resourceProvider ?? throw new ArgumentNullException(nameof(resourceProvider));
        _backoffStore = backoffStore ?? throw new ArgumentNullException(nameof(backoffStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<LocalAsrWorkerRunResult> ProcessOnceAsync(
        LocalAccountScope scope,
        LocalAsrWorkTrigger trigger,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var configurationError = ValidateConfiguration();
        if (configurationError is not null)
        {
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.InvalidConfiguration,
                LocalRuntimeReasonCodes.InvalidConfiguration,
                configurationError);
        }

        try
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.Cancelled,
                LocalRuntimeReasonCodes.WorkerCancelled);
        }

        CancellationToken accountToken;
        try
        {
            accountToken = _accountGuard.BeginRun(scope);
        }
        catch (InvalidOperationException ex)
        {
            _runGate.Release();
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.ScopeChanged,
                LocalRuntimeReasonCodes.ScopeChanged,
                ex.Message);
        }

        try
        {
            try
            {
                _accountGuard.RegisterCapability(
                    scope,
                    new[] { _options.ModelManifest.ModelId });
            }
            catch (InvalidOperationException ex)
            {
                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.ScopeChanged,
                    LocalRuntimeReasonCodes.ScopeChanged,
                    ex.Message);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                accountToken);
            var token = linked.Token;
            var backoffKey = BackoffKey(scope);

            if (trigger == LocalAsrWorkTrigger.Automatic)
            {
                var backoff = await _backoffStore
                    .EvaluateAsync(backoffKey, _options.ModelManifest.ModelId, _now())
                    .ConfigureAwait(false);
                if (!backoff.ShouldAutoAttempt)
                {
                    return LocalAsrWorkerRunResult.WithoutTask(
                        LocalAsrWorkerRunStatus.BackoffDeferred,
                        backoff.ManualRequired ? "manual-required" : "backoff-active",
                        backoff.NextAttemptAtUtc?.ToString("O"));
                }
            }

            ResourceDecision resourceDecision;
            try
            {
                var snapshot = await _resourceProvider
                    .GetSnapshotAsync(token)
                    .ConfigureAwait(false);
                resourceDecision = ResourceRequirementEvaluator.Evaluate(
                    _options.ModelManifest.Resources,
                    snapshot);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return ClassifyPreClaimCancellation(accountToken, cancellationToken);
            }

            if (!resourceDecision.Satisfied)
            {
                try
                {
                    await _backoffStore.RecordResourceFailureAsync(
                            backoffKey,
                            _options.ModelManifest.ModelId,
                            resourceDecision.Reasons,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return ClassifyPreClaimCancellation(accountToken, cancellationToken);
                }

                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.ResourceBlocked,
                    string.Join(",", resourceDecision.Reasons));
            }

            var requestedIdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? "asr-worker-" + Guid.NewGuid().ToString("N")
                : idempotencyKey.Trim();
            if (requestedIdempotencyKey.Length > MaximumIdLength)
            {
                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.InvalidConfiguration,
                    LocalRuntimeReasonCodes.InvalidConfiguration,
                    "idempotency key is too long");
            }

            LocalAsrClaimResult claim;
            try
            {
                claim = await _transport.ClaimAsync(
                        new LocalAsrClaimRequest(
                            scope,
                            _options.NodeId,
                            requestedIdempotencyKey,
                            _options.RuntimeManifest.RuntimeId,
                            _options.ModelManifest.ModelId,
                            _options.ModelManifest.Revision,
                            _options.ModelManifest.Sha256),
                        token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return ClassifyPreClaimCancellation(accountToken, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.TransportFailed,
                    LocalRuntimeReasonCodes.TransportFailed,
                    ex.Message);
            }

            switch (claim.Status)
            {
                case LocalAsrClaimStatus.NoWork:
                    return LocalAsrWorkerRunResult.WithoutTask(
                        LocalAsrWorkerRunStatus.NoWork,
                        claim.ReasonCode);
                case LocalAsrClaimStatus.Deferred:
                    return LocalAsrWorkerRunResult.WithoutTask(
                        LocalAsrWorkerRunStatus.Deferred,
                        claim.ReasonCode,
                        claim.RetryAfterUtc?.ToString("O"));
                case LocalAsrClaimStatus.Rejected:
                    return LocalAsrWorkerRunResult.WithoutTask(
                        LocalAsrWorkerRunStatus.Failed,
                        claim.ReasonCode ?? LocalRuntimeReasonCodes.ClaimInvalid);
                case LocalAsrClaimStatus.Claimed:
                    break;
                default:
                    return LocalAsrWorkerRunResult.WithoutTask(
                        LocalAsrWorkerRunStatus.InvalidClaim,
                        LocalRuntimeReasonCodes.ClaimInvalid,
                        "unknown claim status");
            }

            if (claim.Task is null)
            {
                return LocalAsrWorkerRunResult.WithoutTask(
                    LocalAsrWorkerRunStatus.InvalidClaim,
                    LocalRuntimeReasonCodes.ClaimInvalid,
                    "claimed response has no task");
            }

            var claimError = ValidateClaim(claim.Task);
            if (claimError is not null)
            {
                return await CompleteInvalidClaimAsync(
                        scope,
                        claim.Task,
                        claimError,
                        token)
                    .ConfigureAwait(false);
            }

            await PersistAsync(
                    scope,
                    claim.Task,
                    LocalAsrPersistedStage.Running,
                    null,
                    null,
                    token)
                .ConfigureAwait(false);

            return await ExecuteClaimedTaskAsync(
                    scope,
                    claim.Task,
                    accountToken,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested || accountToken.IsCancellationRequested)
        {
            return ClassifyPreClaimCancellation(accountToken, cancellationToken);
        }
        finally
        {
            _accountGuard.EndRun(accountToken);
            _runGate.Release();
        }
    }

    public async Task<LocalAsrScopeSwitchResult> SwitchScopeAsync(
        LocalAccountScope? newScope,
        CancellationToken cancellationToken = default)
    {
        var previousScope = CurrentScope();
        var accountSwitch = await _accountGuard
            .SwitchScopeAsync(newScope)
            .ConfigureAwait(false);
        LocalAsrWithdrawResult? withdraw = null;
        if (previousScope is not null
            && (newScope is null || !previousScope.Matches(newScope)))
        {
            withdraw = await WithdrawSafelyAsync(
                    previousScope,
                    LocalRuntimeReasonCodes.ScopeChanged)
                .ConfigureAwait(false);
        }

        return new LocalAsrScopeSwitchResult(
            previousScope,
            newScope,
            accountSwitch,
            withdraw);
    }

    public Task<LocalAsrRestartRecoveryResult> RecoverAfterRestartAsync(
        LocalAccountScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _stateStore.RecoverAfterRestartAsync(
            scope,
            _options.NodeId,
            cancellationToken);
    }

    private async Task<LocalAsrWorkerRunResult> ExecuteClaimedTaskAsync(
        LocalAccountScope scope,
        LocalAsrClaimedTask task,
        CancellationToken accountToken,
        CancellationToken callerToken)
    {
        // 空 pending 子集表示“服务端没有返回待处理分片”，不是“零段执行成功”。
        // 先 fail-closed no-work：不得伪造完成 ACK，也不得启动运行时。
        if (task.Segments.Count == 0)
        {
            return new LocalAsrWorkerRunResult(
                LocalAsrWorkerRunStatus.NoWork,
                task.AttemptId,
                LocalRuntimeReasonCodes.NoPendingSegments,
                "claim returned an empty pending segment set",
                Array.Empty<LocalAsrSegmentRunResult>(),
                TerminalAcknowledged: false);
        }

        using var abort = new CancellationTokenSource();
        using var linkedRun = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            accountToken,
            abort.Token);
        using var stopRenewal = new CancellationTokenSource();
        using var linkedRenewal = CancellationTokenSource.CreateLinkedTokenSource(
            linkedRun.Token,
            stopRenewal.Token);
        var control = new RunControl(task.Lease, _now());
        var renewTask = RenewLoopAsync(task, control, abort, linkedRenewal.Token);
        var segmentResults = new List<LocalAsrSegmentRunResult>(task.Segments.Count);
        long totalContentBytes = 0;
        string? runDirectory = null;
        try
        {
            runDirectory = CreateRunDirectory(scope);
            foreach (var segment in task.Segments)
            {
                linkedRun.Token.ThrowIfCancellationRequested();
                var materialized = await MaterializeSegmentAsync(
                        scope,
                        control,
                        task,
                        segment,
                        runDirectory,
                        _options.MaxTotalContentBytes - totalContentBytes,
                        linkedRun.Token)
                    .ConfigureAwait(false);
                if (!materialized.Succeeded)
                {
                    var ack = await TryAcknowledgeAsync(
                            control,
                            task,
                            FailedAck(
                                task,
                                control.CurrentLease,
                                LocalAsrAckKind.SegmentFailed,
                                segment,
                                materialized.ErrorCode,
                                materialized.Detail))
                        .ConfigureAwait(false);
                    segmentResults.Add(new LocalAsrSegmentRunResult(
                        segment.SegmentIndex,
                        false,
                        ack.Status == LocalAsrAckStatus.Accepted,
                        materialized.ErrorCode,
                        null));
                    var terminalAck = await TryAcknowledgeAsync(
                            control,
                            task,
                            FailedAck(
                                task,
                                control.CurrentLease,
                                LocalAsrAckKind.AttemptFailed,
                                segment: null,
                                materialized.ErrorCode,
                                materialized.Detail))
                        .ConfigureAwait(false);
                    return await CompleteFailedRunAsync(
                            scope,
                            task,
                            LocalAsrWorkerRunStatus.Failed,
                            materialized.ErrorCode,
                            materialized.Detail,
                            segmentResults,
                            terminalAck,
                            linkedRun.Token)
                        .ConfigureAwait(false);
                }

                totalContentBytes = checked(totalContentBytes + materialized.BytesWritten);

                LocalSpeechExecutionResult execution;
                try
                {
                    execution = await _runtimeAdapter.ExecuteAsync(
                            _options.RuntimeManifest,
                            _options.ModelManifest,
                            new LocalSpeechExecutionRequest(
                                scope,
                                materialized.Path!,
                                task.Language),
                            linkedRun.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedRun.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    execution = LocalSpeechExecutionResult.Failed(
                        LocalRuntimeReasonCodes.ProcessStartFailed,
                        ex.Message);
                }

                linkedRun.Token.ThrowIfCancellationRequested();
                if (!execution.Succeeded)
                {
                    var ack = await TryAcknowledgeAsync(
                            control,
                            task,
                            FailedAck(
                                task,
                                control.CurrentLease,
                                LocalAsrAckKind.SegmentFailed,
                                segment,
                                execution.ErrorCode,
                                execution.Detail))
                        .ConfigureAwait(false);
                    segmentResults.Add(new LocalAsrSegmentRunResult(
                        segment.SegmentIndex,
                        false,
                        ack.Status == LocalAsrAckStatus.Accepted,
                        execution.ErrorCode,
                        execution));
                    var terminalAck = await TryAcknowledgeAsync(
                            control,
                            task,
                            FailedAck(
                                task,
                                control.CurrentLease,
                                LocalAsrAckKind.AttemptFailed,
                                segment: null,
                                execution.ErrorCode,
                                execution.Detail))
                        .ConfigureAwait(false);
                    return await CompleteFailedRunAsync(
                            scope,
                            task,
                            LocalAsrWorkerRunStatus.Failed,
                            execution.ErrorCode,
                            execution.Detail,
                            segmentResults,
                            terminalAck,
                            linkedRun.Token)
                        .ConfigureAwait(false);
                }

                var segmentAck = await TryAcknowledgeAsync(
                        control,
                        task,
                        SuccessAck(task, control.CurrentLease, segment, execution))
                    .ConfigureAwait(false);
                segmentResults.Add(new LocalAsrSegmentRunResult(
                    segment.SegmentIndex,
                    true,
                    segmentAck.Status == LocalAsrAckStatus.Accepted,
                    segmentAck.Status == LocalAsrAckStatus.Accepted
                        ? null
                        : LocalRuntimeReasonCodes.AckRejected,
                    execution));
                if (segmentAck.Status != LocalAsrAckStatus.Accepted)
                {
                    return await MarkExecutionUnknownAsync(
                            scope,
                            task,
                            LocalAsrWorkerRunStatus.AckRejected,
                            LocalRuntimeReasonCodes.AckRejected,
                            segmentAck.ReasonCode,
                            segmentResults,
                            terminalAcknowledged: false,
                            linkedRun.Token)
                        .ConfigureAwait(false);
                }
            }

            linkedRun.Token.ThrowIfCancellationRequested();
            var terminal = await TryAcknowledgeAsync(
                    control,
                    task,
                    new LocalAsrAckRequest(
                        task.AttemptId,
                        task.VersionId,
                        control.CurrentLease.LeaseId,
                        control.CurrentLease.FencingToken,
                        LocalAsrAckKind.AttemptCompleted,
                        SegmentIndex: null,
                        SourceOffsetSamples: null,
                        Samples: null,
                        Text: null,
                        Start: null,
                        End: null,
                        OutputSha256: null,
                        ErrorCode: null,
                        Detail: null,
                        CompletedAtUtc: _now()))
                .ConfigureAwait(false);
            if (terminal.Status != LocalAsrAckStatus.Accepted)
            {
                return await MarkExecutionUnknownAsync(
                        scope,
                        task,
                        LocalAsrWorkerRunStatus.AckRejected,
                        LocalRuntimeReasonCodes.AckRejected,
                        terminal.ReasonCode,
                        segmentResults,
                        terminalAcknowledged: false,
                        linkedRun.Token)
                    .ConfigureAwait(false);
            }

            await PersistAsync(
                    scope,
                    task,
                    LocalAsrPersistedStage.Completed,
                    null,
                    null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await _backoffStore.RecordSuccessAsync(
                    BackoffKey(scope),
                    _options.ModelManifest.ModelId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new LocalAsrWorkerRunResult(
                LocalAsrWorkerRunStatus.Completed,
                task.AttemptId,
                null,
                null,
                segmentResults,
                TerminalAcknowledged: true);
        }
        catch (OperationCanceledException) when (linkedRun.IsCancellationRequested)
        {
            var outcome = ClassifyRunAbort(control, accountToken, callerToken);
            var terminalAcknowledged = false;
            if (outcome.Status == LocalAsrWorkerRunStatus.Stopped)
            {
                var terminalAck = await TryAcknowledgeAsync(
                        control,
                        task,
                        FailedAck(
                            task,
                            control.CurrentLease,
                            LocalAsrAckKind.AttemptFailed,
                            segment: null,
                            outcome.ReasonCode,
                            outcome.Detail))
                    .ConfigureAwait(false);
                terminalAcknowledged = terminalAck.Status == LocalAsrAckStatus.Accepted;
            }

            await PersistAsync(
                    scope,
                    task,
                    outcome.Stage,
                    outcome.ReasonCode,
                    outcome.Detail,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new LocalAsrWorkerRunResult(
                outcome.Status,
                task.AttemptId,
                outcome.ReasonCode,
                outcome.Detail,
                segmentResults,
                terminalAcknowledged);
        }
        finally
        {
            stopRenewal.Cancel();
            try
            {
                await renewTask
                    .WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                // Cancellation callbacks can still be unwinding on a loaded runner.
                // The run result is already terminal; do not block shutdown forever.
            }

            DeleteRunDirectory(runDirectory);
        }
    }

    private async Task<MaterializedSegment> MaterializeSegmentAsync(
        LocalAccountScope scope,
        RunControl control,
        LocalAsrClaimedTask task,
        LocalAsrInputSegment segment,
        string runDirectory,
        long remainingTotalBudget,
        CancellationToken cancellationToken)
    {
        var identity = new ApiContentIdentityBinding(
            task.AttemptId,
            task.VersionId,
            control.CurrentLease.LeaseId,
            control.CurrentLease.FencingToken,
            segment.SegmentIndex,
            segment.SourceSha256 ?? string.Empty,
            segment.SourceOffsetSamples,
            segment.Samples);

        var destination = Path.Combine(runDirectory, $"segment-{segment.SegmentIndex}.wav");
        long bytesWritten = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        LocalAsrSegmentContent content;
        try
        {
            content = await _transport
                .OpenSegmentContentAsync(control.CurrentLease, segment, cancellationToken)
                .ConfigureAwait(false);
            if (content is null || content.Content is null)
            {
                return MaterializedSegment.Failed(
                    LocalRuntimeReasonCodes.InputInvalid,
                    "content transport returned no stream");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MaterializedSegment.Failed(
                LocalRuntimeReasonCodes.InputInvalid,
                ex.Message);
        }

        // 内容响应的部署身份也必须包含 PathBase；同主机另一个 tenant
        // 即使 authority 相同，也不能作为当前账号的可信内容源。
        if (!string.Equals(content.ApiOrigin, scope.ServerBaseAddress, StringComparison.Ordinal)
            || !string.Equals(content.OwnerIdentity, scope.OwnerIdentity, StringComparison.Ordinal)
            || !string.Equals(content.NodeId, _options.NodeId, StringComparison.Ordinal))
        {
            await content.Content.DisposeAsync().ConfigureAwait(false);
            return MaterializedSegment.Failed(
                LocalRuntimeReasonCodes.ContentBindingMismatch,
                "content response origin, owner or node does not match the active scope");
        }

        var bindingErrors = ApiContentIntegrityValidator.ValidateBinding(
            identity,
            content.Binding);
        if (bindingErrors.Count > 0)
        {
            await content.Content.DisposeAsync().ConfigureAwait(false);
            return MaterializedSegment.Failed(
                bindingErrors[0],
                string.Join(",", bindingErrors));
        }

        try
        {
            await using var source = content.Content;

            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                while (true)
                {
                    var read = await source
                        .ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    bytesWritten += read;
                    if (bytesWritten > _options.MaxSegmentContentBytes
                        || bytesWritten > remainingTotalBudget
                        || (segment.ExpectedContentBytes is { } expected
                            && bytesWritten > expected))
                    {
                        return MaterializedSegment.Failed(
                            LocalRuntimeReasonCodes.InputInvalid,
                            "segment content exceeded its byte budget");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MaterializedSegment.Failed(
                LocalRuntimeReasonCodes.InputInvalid,
                ex.Message);
        }

        if (segment.ExpectedContentBytes is { } declaredBytes && bytesWritten != declaredBytes)
        {
            return MaterializedSegment.Failed(
                LocalRuntimeReasonCodes.SizeMismatch,
                $"expected {declaredBytes} bytes, received {bytesWritten}");
        }

        var actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!ApiContentIntegrityValidator.MatchesActualContentBytes(content.Binding, actualSha))
        {
            return MaterializedSegment.Failed(
                LocalRuntimeReasonCodes.ContentDigestMismatch,
                "actual segment bytes do not match the declared content digest");
        }

        return MaterializedSegment.Success(destination, bytesWritten);
    }

    private async Task RenewLoopAsync(
        LocalAsrClaimedTask task,
        RunControl control,
        CancellationTokenSource abort,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var lease = control.CurrentLease;
                var serverNow = control.GetServerNow(_now());
                if (serverNow >= lease.ExpiresAtUtc)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.LeaseExpired,
                        "lease expired before renewal");
                    ScheduleAbort(abort);
                    return;
                }

                var remaining = lease.ExpiresAtUtc - serverNow;
                var delay = remaining < _options.EffectiveRenewInterval
                    ? remaining
                    : _options.EffectiveRenewInterval;
                if (delay <= TimeSpan.Zero)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.LeaseExpired,
                        "lease expired before renewal");
                    ScheduleAbort(abort);
                    return;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                serverNow = control.GetServerNow(_now());
                if (serverNow >= lease.ExpiresAtUtc)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.LeaseExpired,
                        "lease expired before the renewal request");
                    ScheduleAbort(abort);
                    return;
                }

                LocalAsrRenewResult renewed;
                var renewBudgetRemaining = lease.ExpiresAtUtc - serverNow;
                using var renewBudget = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                renewBudget.CancelAfter(renewBudgetRemaining);
                try
                {
                    renewed = await _transport
                        .RenewAsync(lease, renewBudget.Token)
                        .WaitAsync(renewBudgetRemaining, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (TimeoutException)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.LeaseExpired,
                        "renew request exceeded the remaining lease budget");
                    ScheduleAbort(abort);
                    return;
                }
                catch (OperationCanceledException)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.LeaseExpired,
                        "renew request was cancelled at the lease deadline");
                    ScheduleAbort(abort);
                    return;
                }
                catch (Exception ex)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.RenewFailed,
                        ex.Message);
                    ScheduleAbort(abort);
                    return;
                }

                if (renewed.StopRequested
                    || renewed.Status == LocalAsrRenewStatus.StopRequested)
                {
                    control.Abort(
                        LocalRuntimeReasonCodes.WorkerStopped,
                        renewed.ReasonCode);
                    ScheduleAbort(abort);
                    return;
                }

                if (renewed.Status != LocalAsrRenewStatus.Renewed
                    || renewed.Lease is null
                    || !IsValidRenewal(lease, renewed.Lease))
                {
                    control.Abort(
                        renewed.Status == LocalAsrRenewStatus.LeaseExpired
                            ? LocalRuntimeReasonCodes.LeaseExpired
                            : LocalRuntimeReasonCodes.RenewFailed,
                        renewed.ReasonCode);
                    ScheduleAbort(abort);
                    return;
                }

                control.UpdateLease(renewed.Lease, _now());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsValidRenewal(LocalAsrLease previous, LocalAsrLease next) =>
        string.Equals(previous.LeaseId, next.LeaseId, StringComparison.Ordinal)
        && string.Equals(previous.LeaseToken, next.LeaseToken, StringComparison.Ordinal)
        && string.Equals(previous.AttemptId, next.AttemptId, StringComparison.Ordinal)
        && string.Equals(previous.VersionId, next.VersionId, StringComparison.Ordinal)
        && next.FencingToken == previous.FencingToken
        && next.FencingToken > 0
        && next.ExpiresAtUtc >= previous.ExpiresAtUtc
        && next.ServerNowUtc < next.ExpiresAtUtc;

    private static void ScheduleAbort(CancellationTokenSource abort)
    {
        // Async cancellation avoids recursing into the run cleanup path while the
        // renewal task itself is still unwinding.
        _ = abort.CancelAsync();
    }

    private async Task<LocalAsrAckResult> TryAcknowledgeAsync(
        RunControl control,
        LocalAsrClaimedTask task,
        LocalAsrAckRequest request)
    {
        try
        {
            var current = control.CurrentLease;
            if (!string.Equals(request.LeaseId, current.LeaseId, StringComparison.Ordinal)
                || request.FencingToken != current.FencingToken)
            {
                return new LocalAsrAckResult(
                    LocalAsrAckStatus.Rejected,
                    "lease-fence-changed");
            }

            using var timeout = new CancellationTokenSource(_options.EffectiveAckTimeout);
            return await _transport
                .AcknowledgeAsync(current, request, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new LocalAsrAckResult(
                LocalAsrAckStatus.TransientFailure,
                "ack-timeout");
        }
        catch (Exception ex)
        {
            return new LocalAsrAckResult(
                LocalAsrAckStatus.TransientFailure,
                ex.Message);
        }
    }

    private async Task<LocalAsrWorkerRunResult> CompleteInvalidClaimAsync(
        LocalAccountScope scope,
        LocalAsrClaimedTask task,
        string detail,
        CancellationToken cancellationToken)
    {
        var terminal = await TryAcknowledgeAsync(
                new RunControl(task.Lease, _now()),
                task,
                FailedAck(
                    task,
                    task.Lease,
                    LocalAsrAckKind.AttemptFailed,
                    segment: null,
                    LocalRuntimeReasonCodes.ClaimInvalid,
                    detail))
            .ConfigureAwait(false);
        if (terminal.Status == LocalAsrAckStatus.Accepted)
        {
            await PersistAsync(
                    scope,
                    task,
                    LocalAsrPersistedStage.Failed,
                    LocalRuntimeReasonCodes.ClaimInvalid,
                    detail,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new LocalAsrWorkerRunResult(
            LocalAsrWorkerRunStatus.InvalidClaim,
            task.AttemptId,
            LocalRuntimeReasonCodes.ClaimInvalid,
            detail,
            Array.Empty<LocalAsrSegmentRunResult>(),
            terminal.Status == LocalAsrAckStatus.Accepted);
    }

    private async Task<LocalAsrWorkerRunResult> CompleteFailedRunAsync(
        LocalAccountScope scope,
        LocalAsrClaimedTask task,
        LocalAsrWorkerRunStatus status,
        string? reasonCode,
        string? detail,
        IReadOnlyList<LocalAsrSegmentRunResult> segmentResults,
        LocalAsrAckResult terminalAck,
        CancellationToken cancellationToken)
    {
        if (terminalAck.Status != LocalAsrAckStatus.Accepted)
        {
            return await MarkExecutionUnknownAsync(
                    scope,
                    task,
                    LocalAsrWorkerRunStatus.ExecutionUnknown,
                    LocalRuntimeReasonCodes.AckRejected,
                    terminalAck.ReasonCode,
                    segmentResults,
                    terminalAcknowledged: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await PersistAsync(
                scope,
                task,
                LocalAsrPersistedStage.Failed,
                reasonCode,
                detail,
                CancellationToken.None)
            .ConfigureAwait(false);
        return new LocalAsrWorkerRunResult(
            status,
            task.AttemptId,
            reasonCode,
            detail,
            segmentResults,
            TerminalAcknowledged: true);
    }

    private async Task<LocalAsrWorkerRunResult> MarkExecutionUnknownAsync(
        LocalAccountScope scope,
        LocalAsrClaimedTask task,
        LocalAsrWorkerRunStatus status,
        string? reasonCode,
        string? detail,
        IReadOnlyList<LocalAsrSegmentRunResult> segmentResults,
        bool terminalAcknowledged,
        CancellationToken cancellationToken)
    {
        await PersistAsync(
                scope,
                task,
                LocalAsrPersistedStage.ExecutionUnknown,
                reasonCode,
                detail,
                cancellationToken)
            .ConfigureAwait(false);
        return new LocalAsrWorkerRunResult(
            status,
            task.AttemptId,
            reasonCode,
            detail,
            segmentResults,
            terminalAcknowledged);
    }

    private async Task PersistAsync(
        LocalAccountScope scope,
        LocalAsrClaimedTask task,
        LocalAsrPersistedStage stage,
        string? reasonCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        await _stateStore.SaveAsync(
                new LocalAsrWorkerPersistedState(
                    scope.ServerBaseAddress,
                    scope.OwnerIdentity,
                    _options.NodeId,
                    task.AttemptId,
                    task.Lease.LeaseId,
                    task.Lease.FencingToken,
                    stage,
                    reasonCode,
                    detail,
                    _now()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LocalAsrWithdrawResult> WithdrawSafelyAsync(
        LocalAccountScope scope,
        string reason)
    {
        using var timeout = new CancellationTokenSource(_options.EffectiveWithdrawTimeout);
        try
        {
            return await _transport
                .WithdrawAsync(
                    new LocalAsrWithdrawRequest(scope, _options.NodeId, reason),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new LocalAsrWithdrawResult(
                LocalAsrWithdrawStatus.TransientFailure,
                "withdraw-timeout");
        }
        catch (Exception ex)
        {
            return new LocalAsrWithdrawResult(
                LocalAsrWithdrawStatus.TransientFailure,
                ex.Message);
        }
    }

    private string? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.NodeId)
            || _options.NodeId.Length > 128)
        {
            return "node id is required and bounded";
        }

        if (!_options.RuntimeManifest.HasVerifiedRuntime
            || !_options.ModelManifest.HasVerifiedArtifact
            || !string.Equals(
                _options.RuntimeManifest.RuntimeId,
                _options.ModelManifest.RuntimeId,
                StringComparison.Ordinal))
        {
            return "runtime/model manifests must be verified and bound together";
        }

        if (string.IsNullOrWhiteSpace(_options.StagingRoot)
            || !Path.IsPathFullyQualified(_options.StagingRoot))
        {
            return "staging root must be fully qualified";
        }

        if (_options.MaxSegmentCount <= 0
            || _options.MaxSegmentContentBytes <= 0
            || _options.MaxTotalContentBytes < _options.MaxSegmentContentBytes
            || _options.EffectiveRenewInterval <= TimeSpan.Zero
            || _options.EffectiveRenewInterval > TimeSpan.FromMinutes(5)
            || _options.EffectiveAckTimeout <= TimeSpan.Zero
            || _options.EffectiveAckTimeout > TimeSpan.FromMinutes(1)
            || _options.EffectiveWithdrawTimeout <= TimeSpan.Zero
            || _options.EffectiveWithdrawTimeout > TimeSpan.FromMinutes(1))
        {
            return "worker limits are invalid";
        }

        return null;
    }

    private string? ValidateClaim(LocalAsrClaimedTask task)
    {
        if (!HasBoundedId(task.AttemptId)
            || !HasBoundedId(task.TaskId)
            || !HasBoundedId(task.VersionId)
            || !HasBoundedLeaseToken(task.Lease.LeaseToken)
            || !HasBoundedId(task.Lease.LeaseId))
        {
            return "claim or lease identity is missing or too long";
        }

        if (!string.Equals(task.AttemptId, task.Lease.AttemptId, StringComparison.Ordinal)
            || !string.Equals(task.VersionId, task.Lease.VersionId, StringComparison.Ordinal))
        {
            return "lease attempt/version does not match the claimed task";
        }

        if (task.Lease.FencingToken <= 0
            || task.Lease.ExpiresAtUtc <= task.Lease.ServerNowUtc)
        {
            return "lease expiry or server clock is invalid";
        }

        if (!string.Equals(task.RuntimeId, _options.RuntimeManifest.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(task.ModelId, _options.ModelManifest.ModelId, StringComparison.Ordinal)
            || !string.Equals(task.ModelRevision, _options.ModelManifest.Revision, StringComparison.Ordinal)
            || !string.Equals(task.ModelSha256, _options.ModelManifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return "claim runtime/model snapshot does not match the installed verified catalog";
        }

        if (!LanguagePattern.IsMatch(task.Language)
            || !LocalModelManifest.IsValidSha256(task.InputManifestSha256)
            || task.SourceSamples is <= 0)
        {
            return "claim language or source identity is invalid";
        }

        // ExpectedSegments 是 attempt 的全部固定分片总数；Segments 可能只是
        // 「尚未成功」的稀疏子集（例如 total=3, segments=[1,2]）。
        if (task.ExpectedSegments <= 0
            || task.ExpectedSegments > _options.MaxSegmentCount)
        {
            return "claim expected segment count is outside the configured bound";
        }

        if (task.Segments is null
            || task.Segments.Count > _options.MaxSegmentCount)
        {
            return "claim segment count is outside the configured bound";
        }

        var indexes = new HashSet<int>();
        long totalBytes = 0;
        foreach (var segment in task.Segments)
        {
            if (segment.SegmentIndex < 0
                || segment.SegmentIndex >= task.ExpectedSegments
                || !indexes.Add(segment.SegmentIndex)
                || segment.SourceOffsetSamples < 0
                || segment.Samples <= 0
                || segment.SourceFileIndex < 0
                || !LocalModelManifest.IsValidSha256(segment.SourceSha256)
                || segment.ExpectedContentBytes is <= 0
                || segment.ExpectedContentBytes > _options.MaxSegmentContentBytes
                || (segment.ExpectedContentSha256 is not null
                    && !LocalModelManifest.IsValidSha256(segment.ExpectedContentSha256))
                || (task.SourceSamples is { } sourceSamples
                    && segment.SourceOffsetSamples > sourceSamples - segment.Samples))
            {
                return "claim segment identity, sizes or digest is invalid";
            }

            if (segment.ExpectedContentBytes is { } declaredBytes)
            {
                try
                {
                    totalBytes = checked(totalBytes + declaredBytes);
                }
                catch (OverflowException)
                {
                    return "claim total content size overflowed";
                }
            }

            if (totalBytes > _options.MaxTotalContentBytes)
            {
                return "claim total content size exceeds the configured bound";
            }
        }

        return null;
    }

    private string CreateRunDirectory(LocalAccountScope scope)
    {
        var accountRoot = Path.Combine(
            Path.GetFullPath(_options.StagingRoot),
            "accounts",
            PathSafety.HashAccountKey(scope.ServerBaseAddress + "\n" + scope.OwnerIdentity),
            "asr-worker");
        var runDirectory = Path.Combine(accountRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        return runDirectory;
    }

    private static void DeleteRunDirectory(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private LocalAsrWorkerRunResult ClassifyPreClaimCancellation(
        CancellationToken accountToken,
        CancellationToken callerToken)
    {
        if (accountToken.IsCancellationRequested)
        {
            return LocalAsrWorkerRunResult.WithoutTask(
                LocalAsrWorkerRunStatus.ScopeChanged,
                LocalRuntimeReasonCodes.ScopeChanged);
        }

        return LocalAsrWorkerRunResult.WithoutTask(
            LocalAsrWorkerRunStatus.Cancelled,
            callerToken.IsCancellationRequested
                ? LocalRuntimeReasonCodes.WorkerCancelled
                : LocalRuntimeReasonCodes.TransportFailed);
    }

    private static RunAbortOutcome ClassifyRunAbort(
        RunControl control,
        CancellationToken accountToken,
        CancellationToken callerToken)
    {
        if (control.AbortReason is { } reason)
        {
            return reason switch
            {
                LocalRuntimeReasonCodes.WorkerStopped => new RunAbortOutcome(
                    LocalAsrWorkerRunStatus.Stopped,
                    reason,
                    control.AbortDetail,
                    LocalAsrPersistedStage.ExecutionUnknown),
                LocalRuntimeReasonCodes.LeaseExpired => new RunAbortOutcome(
                    LocalAsrWorkerRunStatus.LeaseExpired,
                    reason,
                    control.AbortDetail,
                    LocalAsrPersistedStage.ExecutionUnknown),
                _ => new RunAbortOutcome(
                    LocalAsrWorkerRunStatus.ExecutionUnknown,
                    reason,
                    control.AbortDetail,
                    LocalAsrPersistedStage.ExecutionUnknown),
            };
        }

        if (accountToken.IsCancellationRequested)
        {
            return new RunAbortOutcome(
                LocalAsrWorkerRunStatus.ScopeChanged,
                LocalRuntimeReasonCodes.ScopeChanged,
                "account or server origin changed while the worker was running",
                LocalAsrPersistedStage.ExecutionUnknown);
        }

        return new RunAbortOutcome(
            LocalAsrWorkerRunStatus.Cancelled,
            callerToken.IsCancellationRequested
                ? LocalRuntimeReasonCodes.WorkerCancelled
                : LocalRuntimeReasonCodes.TransportFailed,
            "worker execution was cancelled",
            LocalAsrPersistedStage.Cancelled);
    }

    private LocalAccountScope? CurrentScope()
    {
        var account = _accountGuard.CurrentAccountId;
        var origin = _accountGuard.CurrentServerBaseAddress;
        return account is null || origin is null
            ? null
            : new LocalAccountScope(origin, account);
    }

    private static LocalAsrAckRequest SuccessAck(
        LocalAsrClaimedTask task,
        LocalAsrLease lease,
        LocalAsrInputSegment segment,
        LocalSpeechExecutionResult execution)
    {
        var first = execution.Segments.Count > 0 ? execution.Segments[0] : null;
        return new LocalAsrAckRequest(
            task.AttemptId,
            task.VersionId,
            lease.LeaseId,
            lease.FencingToken,
            LocalAsrAckKind.SegmentSucceeded,
            segment.SegmentIndex,
            segment.SourceOffsetSamples,
            segment.Samples,
            execution.Text,
            first?.Start,
            first?.End,
            Sha256(execution.Text),
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private static LocalAsrAckRequest FailedAck(
        LocalAsrClaimedTask task,
        LocalAsrLease lease,
        LocalAsrAckKind kind,
        LocalAsrInputSegment? segment,
        string? errorCode,
        string? detail) =>
        new(
            task.AttemptId,
            task.VersionId,
            lease.LeaseId,
            lease.FencingToken,
            kind,
            segment?.SegmentIndex,
            segment?.SourceOffsetSamples,
            segment?.Samples,
            null,
            null,
            null,
            null,
            errorCode,
            detail,
            DateTimeOffset.UtcNow);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool HasBoundedId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumIdLength;

    private static bool HasBoundedLeaseToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumLeaseTokenLength;

    private static string BackoffKey(LocalAccountScope scope) =>
        PathSafety.HashAccountKey(scope.ServerBaseAddress + "\n" + scope.OwnerIdentity);

    private sealed class RunControl
    {
        private readonly object _sync = new();
        private LocalAsrLease _lease;
        private TimeSpan _serverOffset;
        private string? _abortReason;
        private string? _abortDetail;
        private bool _aborted;

        public RunControl(LocalAsrLease lease, DateTimeOffset localNow)
        {
            _lease = lease;
            _serverOffset = lease.ServerNowUtc - localNow;
        }

        public LocalAsrLease CurrentLease
        {
            get
            {
                lock (_sync)
                {
                    return _lease;
                }
            }
        }

        public string? AbortReason
        {
            get
            {
                lock (_sync)
                {
                    return _abortReason;
                }
            }
        }

        public string? AbortDetail
        {
            get
            {
                lock (_sync)
                {
                    return _abortDetail;
                }
            }
        }

        public DateTimeOffset GetServerNow(DateTimeOffset localNow)
        {
            lock (_sync)
            {
                return localNow + _serverOffset;
            }
        }

        public void UpdateLease(LocalAsrLease lease, DateTimeOffset localNow)
        {
            lock (_sync)
            {
                if (_aborted)
                {
                    return;
                }

                _lease = lease;
                _serverOffset = lease.ServerNowUtc - localNow;
            }
        }

        public void Abort(string reason, string? detail)
        {
            lock (_sync)
            {
                if (_aborted)
                {
                    return;
                }

                _aborted = true;
                _abortReason = reason;
                _abortDetail = detail;
            }
        }
    }

    private sealed record MaterializedSegment(
        bool Succeeded,
        string? Path,
        string? ErrorCode,
        string? Detail,
        long BytesWritten)
    {
        public static MaterializedSegment Success(string path, long bytesWritten) =>
            new(true, path, null, null, bytesWritten);

        public static MaterializedSegment Failed(string? errorCode, string? detail) =>
            new(false, null, errorCode, detail, 0);
    }

    private sealed record RunAbortOutcome(
        LocalAsrWorkerRunStatus Status,
        string ReasonCode,
        string? Detail,
        LocalAsrPersistedStage Stage);
}
