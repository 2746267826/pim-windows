using Pim.Client.Core.Models.LocalRuntime;

namespace Pim.Client.Core.Services.LocalRuntime;

public enum LocalAsrWorkTrigger
{
    Automatic,
    Manual,
}

public enum LocalAsrClaimStatus
{
    Claimed,
    NoWork,
    Deferred,
    ResourceBlocked,
    Rejected,
}

public enum LocalAsrRenewStatus
{
    Renewed,
    StopRequested,
    LeaseExpired,
    Rejected,
    TransientFailure,
}

public enum LocalAsrAckKind
{
    SegmentSucceeded,
    SegmentFailed,
    AttemptCompleted,
    AttemptFailed,
}

public enum LocalAsrAckStatus
{
    Accepted,
    Rejected,
    TransientFailure,
}

public enum LocalAsrWithdrawStatus
{
    Withdrawn,
    AlreadyWithdrawn,
    Rejected,
    TransientFailure,
}

public enum LocalAsrWorkerRunStatus
{
    NoWork,
    Deferred,
    BackoffDeferred,
    ResourceBlocked,
    Completed,
    Failed,
    Stopped,
    LeaseExpired,
    Cancelled,
    ScopeChanged,
    ExecutionUnknown,
    AckRejected,
    InvalidClaim,
    TransportFailed,
    InvalidConfiguration,
}

public enum LocalAsrRestartRecoveryStatus
{
    NoPriorState,
    AlreadyTerminal,
    ExecutionUnknown,
}

/// <summary>
/// Internal domain lease. The opaque token is deliberately excluded from durable
/// snapshots: a process restart cannot prove ownership and must never reuse it.
/// </summary>
public sealed record LocalAsrLease(
    string LeaseId,
    string LeaseToken,
    long FencingToken,
    string AttemptId,
    string VersionId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ServerNowUtc);

public sealed record LocalAsrClaimRequest(
    LocalAccountScope Scope,
    string NodeId,
    string IdempotencyKey,
    string RuntimeId,
    string ModelId,
    string ModelRevision,
    string ModelSha256,
    LocalAsrFrozenResourceDecision? ResourceDecision = null);

/// <summary>
/// Immutable local resource result associated with one claim idempotency key.
/// This is internal worker data; it does not assert that an HTTP transport sent it.
/// </summary>
public sealed record LocalAsrFrozenResourceDecision(
    bool IsAutomatic,
    bool Passed,
    IReadOnlyList<string> Reasons,
    DateTimeOffset SampledAtUtc);

public sealed record LocalAsrInputSegment(
    int SegmentIndex,
    long SourceOffsetSamples,
    long Samples,
    int SourceFileIndex = 0,
    string? SourceSha256 = null,
    // 仅当服务端/测试明确声明时才比对；实际字节数/摘要以二进制响应为准。
    long? ExpectedContentBytes = null,
    string? ExpectedContentSha256 = null,
    string? LeaseId = null,
    string? AttemptId = null,
    string? VersionId = null,
    long? FencingToken = null);

public sealed record LocalAsrClaimedTask(
    string AttemptId,
    string TaskId,
    string VersionId,
    string Language,
    string RuntimeId,
    string ModelId,
    string ModelRevision,
    string ModelSha256,
    // claim 里的 execution.InputSha256 是输入 manifest 哈希，不是 source 文件哈希。
    string InputManifestSha256,
    // 多文件输入没有可推导的单一总采样数；仅在服务端明确提供时用于边界校验。
    long? SourceSamples,
    int ExpectedSegments,
    IReadOnlyList<LocalAsrInputSegment> Segments,
    LocalAsrLease Lease);

public sealed record LocalAsrClaimResult(
    LocalAsrClaimStatus Status,
    LocalAsrClaimedTask? Task,
    string? ReasonCode = null,
    DateTimeOffset? RetryAfterUtc = null,
    bool ResourceDecisionAccepted = false);

public sealed record LocalAsrRenewResult(
    LocalAsrRenewStatus Status,
    LocalAsrLease? Lease = null,
    bool StopRequested = false,
    string? ReasonCode = null);

public sealed record LocalAsrAckRequest(
    string AttemptId,
    string VersionId,
    string LeaseId,
    long FencingToken,
    LocalAsrAckKind Kind,
    int? SegmentIndex,
    long? SourceOffsetSamples,
    long? Samples,
    string? Text,
    TimeSpan? Start,
    TimeSpan? End,
    string? OutputSha256,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset CompletedAtUtc);

public sealed record LocalAsrAckResult(
    LocalAsrAckStatus Status,
    string? ReasonCode = null);

public sealed record LocalAsrWithdrawRequest(
    LocalAccountScope Scope,
    string NodeId,
    string Reason);

public sealed record LocalAsrWithdrawResult(
    LocalAsrWithdrawStatus Status,
    string? ReasonCode = null);

public sealed record LocalAsrSegmentContent(
    Stream Content,
    string ApiOrigin,
    string OwnerIdentity,
    string NodeId,
    ApiContentResponseBinding Binding);

/// <summary>
/// Callback seam for the future generated ApiClient adapter. Implementations must
/// keep the supplied account scope, lease and fence bound to the same trusted API
/// origin. Content is returned with the actual response metadata; the engine owns
/// the destination path and must never synthesize response evidence from a claim.
/// </summary>
public interface ILocalAsrNodeTransport
{
    Task<LocalAsrClaimResult> ClaimAsync(
        LocalAsrClaimRequest request,
        CancellationToken cancellationToken = default);

    Task<LocalAsrRenewResult> RenewAsync(
        LocalAsrLease lease,
        CancellationToken cancellationToken = default);

    Task<LocalAsrSegmentContent> OpenSegmentContentAsync(
        LocalAsrLease lease,
        LocalAsrInputSegment segment,
        CancellationToken cancellationToken = default);

    Task<LocalAsrAckResult> AcknowledgeAsync(
        LocalAsrLease lease,
        LocalAsrAckRequest request,
        CancellationToken cancellationToken = default);

    Task<LocalAsrWithdrawResult> WithdrawAsync(
        LocalAsrWithdrawRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Typed extension seam for a backend contract that accepts claim-time resource
/// decisions atomically. It must not acquire a lease for a rejected decision.
/// </summary>
public interface ILocalAsrResourceDecisionTransport
{
    Task<LocalAsrClaimResult> ClaimWithResourceDecisionAsync(
        LocalAsrClaimRequest request,
        LocalAsrFrozenResourceDecision decision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// App-facing worker seam. It exposes only the already bounded internal state
/// machine; the generated ApiClient adapter is supplied later through
/// <see cref="ILocalAsrNodeTransport"/>.
/// </summary>
public interface ILocalAsrWorkerRunner
{
    Task<LocalAsrWorkerRunResult> ProcessOnceAsync(
        LocalAccountScope scope,
        LocalAsrWorkTrigger trigger,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<LocalAsrScopeSwitchResult> SwitchScopeAsync(
        LocalAccountScope? newScope,
        CancellationToken cancellationToken = default);
}

public sealed record LocalAsrSegmentRunResult(
    int SegmentIndex,
    bool Succeeded,
    bool Acknowledged,
    string? ReasonCode,
    LocalSpeechExecutionResult? Execution);

public sealed record LocalAsrWorkerRunResult(
    LocalAsrWorkerRunStatus Status,
    string? AttemptId,
    string? ReasonCode,
    string? Detail,
    IReadOnlyList<LocalAsrSegmentRunResult> Segments,
    bool TerminalAcknowledged)
{
    public static LocalAsrWorkerRunResult WithoutTask(
        LocalAsrWorkerRunStatus status,
        string? reasonCode = null,
        string? detail = null) =>
        new(
            status,
            null,
            reasonCode,
            detail,
            Array.Empty<LocalAsrSegmentRunResult>(),
            false);
}

public sealed record LocalAsrScopeSwitchResult(
    LocalAccountScope? PreviousScope,
    LocalAccountScope? CurrentScope,
    AccountSwitchResult AccountSwitch,
    LocalAsrWithdrawResult? Withdraw);

public sealed record LocalAsrRestartRecoveryResult(
    LocalAsrRestartRecoveryStatus Status,
    string? AttemptId,
    string? LeaseId,
    string? Detail);

public sealed record LocalAsrWorkerOptions(
    string NodeId,
    string NodeDisplayName,
    LocalRuntimeManifest RuntimeManifest,
    LocalModelManifest ModelManifest,
    string StagingRoot,
    int MaxSegmentCount = 128,
    long MaxSegmentContentBytes = 128L * 1024 * 1024,
    long MaxTotalContentBytes = 1024L * 1024 * 1024,
    TimeSpan? RenewInterval = null,
    TimeSpan? AckTimeout = null,
    TimeSpan? WithdrawTimeout = null)
{
    public TimeSpan EffectiveRenewInterval => RenewInterval ?? TimeSpan.FromSeconds(20);

    public TimeSpan EffectiveAckTimeout => AckTimeout ?? TimeSpan.FromSeconds(5);

    public TimeSpan EffectiveWithdrawTimeout => WithdrawTimeout ?? TimeSpan.FromSeconds(5);
}

public enum LocalAsrPersistedStage
{
    Running,
    Completed,
    Failed,
    Stopped,
    Cancelled,
    ExecutionUnknown,
}

public sealed record LocalAsrWorkerPersistedState(
    string ServerOrigin,
    string OwnerIdentity,
    string NodeId,
    string AttemptId,
    string LeaseId,
    long FencingToken,
    LocalAsrPersistedStage Stage,
    string? ReasonCode,
    string? Detail,
    DateTimeOffset UpdatedAtUtc);
