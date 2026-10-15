// ---------------------------------------------------------------------------
// 本文件由工具生成，请勿手工修改 / GENERATED FILE — DO NOT EDIT BY HAND.
//
//   契约快照      : contract/openapi.json (sha256 2b1872f0ea222a9cc63a20ba7dfa69e5119f4f1c39aca9c56ee9991b7f4572d8)
//   operation 清单: scripts/contract/client-operations.json
//   生成器        : scripts/contract/generate-csharp.py
//   重新生成      : bash scripts/ci/generate-contract-csharp.sh
//   一键刷新      : bash scripts/ci/refresh-contract.sh（快照 + 生成物同批更新）
//   一致性门禁    : bash scripts/ci/check-contract-csharp.sh（CI 逐字节比对，改了就红）
//
// 只覆盖 operation 清单声明的 operation 的 schema 闭包；需要新接口时先让后端在
// pim-api 实现并刷新契约，再往清单里加一条 —— 不要在客户端造类型。
// ---------------------------------------------------------------------------
namespace Pim.Client.Core.Contract;

/// <summary>契约 <c>components.schemas.AppStatEntry</c>（request）。</summary>
public sealed class AppStatEntry
{
    /// <summary>契约字段 <c>appName</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("appName")]
    public required string AppName { get; set; }
    /// <summary>契约字段 <c>displayName</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("displayName")]
    public required string DisplayName { get; set; }
    /// <summary>契约字段 <c>keyPresses</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyPresses")]
    public required int KeyPresses { get; set; }
    /// <summary>契约字段 <c>leftClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leftClicks")]
    public required int LeftClicks { get; set; }
    /// <summary>契约字段 <c>middleClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("middleClicks")]
    public required int MiddleClicks { get; set; }
    /// <summary>契约字段 <c>rightClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("rightClicks")]
    public required int RightClicks { get; set; }
    /// <summary>契约字段 <c>scrollDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("scrollDistance")]
    public required double ScrollDistance { get; set; }
    /// <summary>契约字段 <c>sideBackClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sideBackClicks")]
    public required int SideBackClicks { get; set; }
    /// <summary>契约字段 <c>sideForwardClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sideForwardClicks")]
    public required int SideForwardClicks { get; set; }
}

/// <summary>契约 <c>components.schemas.AuthResponse</c>（response）。</summary>
public sealed class AuthResponse
{
    /// <summary>契约字段 <c>accessToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("accessToken")]
    public required string AccessToken { get; set; }
    /// <summary>契约字段 <c>expiresAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expiresAt")]
    public required System.DateTimeOffset ExpiresAt { get; set; }
    /// <summary>契约字段 <c>refreshToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
    public required string RefreshToken { get; set; }
    /// <summary>契约字段 <c>user</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("user")]
    public required UserInfo User { get; set; }
}

/// <summary>契约 <c>components.schemas.AuthResponseApiResponse</c>（response）。</summary>
public sealed class AuthResponseApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public AuthResponse? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.ClientShellLatestResponse</c>（response）。</summary>
public sealed class ClientShellLatestResponse
{
    /// <summary>契约字段 <c>androidUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("androidUrl")]
    public string? AndroidUrl { get; set; }
    /// <summary>契约字段 <c>androidVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("androidVersion")]
    public string? AndroidVersion { get; set; }
    /// <summary>契约字段 <c>checkedAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("checkedAt")]
    public System.DateTimeOffset? CheckedAt { get; set; }
    /// <summary>契约字段 <c>error</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; set; }
    /// <summary>契约字段 <c>shellAndroidUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("shellAndroidUrl")]
    public string? ShellAndroidUrl { get; set; }
    /// <summary>契约字段 <c>shellAndroidVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("shellAndroidVersion")]
    public string? ShellAndroidVersion { get; set; }
    /// <summary>契约字段 <c>shellWindowsUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("shellWindowsUrl")]
    public string? ShellWindowsUrl { get; set; }
    /// <summary>契约字段 <c>shellWindowsVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("shellWindowsVersion")]
    public string? ShellWindowsVersion { get; set; }
    /// <summary>契约字段 <c>windowsUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("windowsUrl")]
    public string? WindowsUrl { get; set; }
    /// <summary>契约字段 <c>windowsVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("windowsVersion")]
    public string? WindowsVersion { get; set; }
}

/// <summary>契约 <c>components.schemas.DaemonHeartbeatDto</c>（response）。</summary>
public sealed class DaemonHeartbeatDto
{
    /// <summary>契约字段 <c>activityWatchState</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("activityWatchState")]
    public required DaemonSourceState ActivityWatchState { get; set; }
    /// <summary>契约字段 <c>collectionPaused</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("collectionPaused")]
    public required bool CollectionPaused { get; set; }
    /// <summary>契约字段 <c>daemonKind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("daemonKind")]
    public required string DaemonKind { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>keyStatsState</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyStatsState")]
    public required DaemonSourceState KeyStatsState { get; set; }
    /// <summary>契约字段 <c>lastAttemptedUploadAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastAttemptedUploadAt")]
    public System.DateTimeOffset? LastAttemptedUploadAt { get; set; }
    /// <summary>契约字段 <c>lastError</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastError")]
    public string? LastError { get; set; }
    /// <summary>契约字段 <c>lastSuccessfulUploadAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastSuccessfulUploadAt")]
    public System.DateTimeOffset? LastSuccessfulUploadAt { get; set; }
    /// <summary>契约字段 <c>offlineReason</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("offlineReason")]
    public string? OfflineReason { get; set; }
    /// <summary>契约字段 <c>plannedOfflineAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("plannedOfflineAt")]
    public System.DateTimeOffset? PlannedOfflineAt { get; set; }
    /// <summary>契约字段 <c>receivedAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("receivedAt")]
    public required System.DateTimeOffset ReceivedAt { get; set; }
    /// <summary>契约字段 <c>serverUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("serverUrl")]
    public required string ServerUrl { get; set; }
    /// <summary>契约字段 <c>statusJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("statusJson")]
    public required string StatusJson { get; set; }
    /// <summary>契约字段 <c>uploadQueueCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadQueueCount")]
    public int? UploadQueueCount { get; set; }
    /// <summary>契约字段 <c>version</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public required string Version { get; set; }
}

/// <summary>契约 <c>components.schemas.DaemonHeartbeatDtoApiResponse</c>（response）。</summary>
public sealed class DaemonHeartbeatDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public DaemonHeartbeatDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.DaemonHeartbeatRequest</c>（request）。</summary>
public sealed class DaemonHeartbeatRequest
{
    /// <summary>契约字段 <c>activityWatchState</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("activityWatchState")]
    public required DaemonSourceState ActivityWatchState { get; set; }
    /// <summary>契约字段 <c>collectionPaused</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("collectionPaused")]
    public required bool CollectionPaused { get; set; }
    /// <summary>契约字段 <c>daemonKind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("daemonKind")]
    public required string DaemonKind { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>keyStatsState</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyStatsState")]
    public required DaemonSourceState KeyStatsState { get; set; }
    /// <summary>契约字段 <c>lastAttemptedUploadAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastAttemptedUploadAt")]
    public System.DateTimeOffset? LastAttemptedUploadAt { get; set; }
    /// <summary>契约字段 <c>lastError</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastError")]
    public string? LastError { get; set; }
    /// <summary>契约字段 <c>lastSuccessfulUploadAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastSuccessfulUploadAt")]
    public System.DateTimeOffset? LastSuccessfulUploadAt { get; set; }
    /// <summary>契约字段 <c>serverUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("serverUrl")]
    public required string ServerUrl { get; set; }
    /// <summary>契约字段 <c>statusJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("statusJson")]
    public required string StatusJson { get; set; }
    /// <summary>契约字段 <c>uploadQueueCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadQueueCount")]
    public int? UploadQueueCount { get; set; }
    /// <summary>契约字段 <c>version</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public required string Version { get; set; }
}

/// <summary>契约 <c>components.schemas.DaemonSourceState</c>（request+response）。</summary>
/// <summary>
/// 契约枚举 <c>DaemonSourceState</c>：wire 值即成员名，写错取值编译期就不通过。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum DaemonSourceState
{
    Unknown,
    Available,
    Unavailable,
    Paused,
}

/// <summary>契约 <c>components.schemas.EndpointCollectionQualityDto</c>（response）。</summary>
public sealed class EndpointCollectionQualityDto
{
    /// <summary>契约字段 <c>checkedAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("checkedAt")]
    public required System.DateTimeOffset CheckedAt { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>issueCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("issueCount")]
    public required int IssueCount { get; set; }
    /// <summary>契约字段 <c>platform</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("platform")]
    public required string Platform { get; set; }
    /// <summary>契约字段 <c>uploadStatus</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadStatus")]
    public required string UploadStatus { get; set; }
}

/// <summary>契约 <c>components.schemas.EndpointCollectionQualityDtoApiResponse</c>（response）。</summary>
public sealed class EndpointCollectionQualityDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public EndpointCollectionQualityDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.EndpointNotificationActionRequest</c>（request）。</summary>
public sealed class EndpointNotificationActionRequest
{
    /// <summary>契约字段 <c>action</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("action")]
    public required string Action { get; set; }
    /// <summary>契约字段 <c>confirmationId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("confirmationId")]
    public string? ConfirmationId { get; set; }
    /// <summary>契约字段 <c>relatedObjectId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("relatedObjectId")]
    public string? RelatedObjectId { get; set; }
    /// <summary>契约字段 <c>relatedObjectType</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("relatedObjectType")]
    public string? RelatedObjectType { get; set; }
    /// <summary>契约字段 <c>riskLevel</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("riskLevel")]
    public required string RiskLevel { get; set; }
}

/// <summary>契约 <c>components.schemas.EndpointNotificationActionResponse</c>（response）。</summary>
public sealed class EndpointNotificationActionResponse
{
    /// <summary>契约字段 <c>detailUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("detailUrl")]
    public string? DetailUrl { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }
    /// <summary>契约字段 <c>result</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public required string Result { get; set; }
}

/// <summary>契约 <c>components.schemas.EndpointNotificationActionResponseApiResponse</c>（response）。</summary>
public sealed class EndpointNotificationActionResponseApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public EndpointNotificationActionResponse? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.EndpointStatusDto</c>（response）。</summary>
public sealed class EndpointStatusDto
{
    /// <summary>契约字段 <c>appVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }
    /// <summary>契约字段 <c>collectionCacheCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("collectionCacheCount")]
    public required int CollectionCacheCount { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>lastHeartbeatAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastHeartbeatAt")]
    public System.DateTimeOffset? LastHeartbeatAt { get; set; }
    /// <summary>契约字段 <c>onlineOnlyBlockedCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("onlineOnlyBlockedCount")]
    public required int OnlineOnlyBlockedCount { get; set; }
    /// <summary>契约字段 <c>platform</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("platform")]
    public required string Platform { get; set; }
    /// <summary>契约字段 <c>uploadStatus</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadStatus")]
    public required string UploadStatus { get; set; }
}

/// <summary>契约 <c>components.schemas.EndpointStatusDtoIReadOnlyListApiResponse</c>（response）。</summary>
public sealed class EndpointStatusDtoIReadOnlyListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<EndpointStatusDto>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.Int32ApiResponse</c>（response）。</summary>
public sealed class Int32ApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public required int Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.KeystatsSampleUploadRequest</c>（request）。</summary>
public sealed class KeystatsSampleUploadRequest
{
    /// <summary>契约字段 <c>appStats</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("appStats")]
    public System.Collections.Generic.Dictionary<string, AppStatEntry>? AppStats { get; set; }
    /// <summary>契约字段 <c>date</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public required string Date { get; set; }
    /// <summary>契约字段 <c>formattedMouseDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("formattedMouseDistance")]
    public string? FormattedMouseDistance { get; set; }
    /// <summary>契约字段 <c>formattedScrollDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("formattedScrollDistance")]
    public string? FormattedScrollDistance { get; set; }
    /// <summary>契约字段 <c>keyPressCounts</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyPressCounts")]
    public System.Collections.Generic.Dictionary<string, int>? KeyPressCounts { get; set; }
    /// <summary>契约字段 <c>keyPresses</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyPresses")]
    public required int KeyPresses { get; set; }
    /// <summary>契约字段 <c>leftClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leftClicks")]
    public required int LeftClicks { get; set; }
    /// <summary>契约字段 <c>middleClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("middleClicks")]
    public required int MiddleClicks { get; set; }
    /// <summary>契约字段 <c>mouseDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("mouseDistance")]
    public required double MouseDistance { get; set; }
    /// <summary>契约字段 <c>peakCps</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("peakCps")]
    public required int PeakCps { get; set; }
    /// <summary>契约字段 <c>peakKps</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("peakKps")]
    public required int PeakKps { get; set; }
    /// <summary>契约字段 <c>pimDeviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pimDeviceId")]
    public required string PimDeviceId { get; set; }
    /// <summary>契约字段 <c>rightClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("rightClicks")]
    public required int RightClicks { get; set; }
    /// <summary>契约字段 <c>sampledAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sampledAt")]
    public required string SampledAt { get; set; }
    /// <summary>契约字段 <c>scrollDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("scrollDistance")]
    public required double ScrollDistance { get; set; }
    /// <summary>契约字段 <c>sideBackClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sideBackClicks")]
    public required int SideBackClicks { get; set; }
    /// <summary>契约字段 <c>sideForwardClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sideForwardClicks")]
    public required int SideForwardClicks { get; set; }
}

/// <summary>契约 <c>components.schemas.KeystatsUploadRequest</c>（request）。</summary>
public sealed class KeystatsUploadRequest
{
    /// <summary>契约字段 <c>appStats</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("appStats")]
    public System.Collections.Generic.Dictionary<string, AppStatEntry>? AppStats { get; set; }
    /// <summary>契约字段 <c>date</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public required string Date { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>keyPressCounts</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyPressCounts")]
    public System.Collections.Generic.Dictionary<string, int>? KeyPressCounts { get; set; }
    /// <summary>契约字段 <c>keyPresses</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("keyPresses")]
    public required int KeyPresses { get; set; }
    /// <summary>契约字段 <c>leftClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leftClicks")]
    public required int LeftClicks { get; set; }
    /// <summary>契约字段 <c>middleClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("middleClicks")]
    public required int MiddleClicks { get; set; }
    /// <summary>契约字段 <c>mouseDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("mouseDistance")]
    public required double MouseDistance { get; set; }
    /// <summary>契约字段 <c>peakCps</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("peakCps")]
    public required int PeakCps { get; set; }
    /// <summary>契约字段 <c>peakKps</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("peakKps")]
    public required int PeakKps { get; set; }
    /// <summary>契约字段 <c>rightClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("rightClicks")]
    public required int RightClicks { get; set; }
    /// <summary>契约字段 <c>scrollDistance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("scrollDistance")]
    public required double ScrollDistance { get; set; }
    /// <summary>契约字段 <c>sideBackClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sideBackClicks")]
    public required int SideBackClicks { get; set; }
    /// <summary>契约字段 <c>sideForwardClicks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sideForwardClicks")]
    public required int SideForwardClicks { get; set; }
}

/// <summary>契约 <c>components.schemas.LoginRequest</c>（request）。</summary>
public sealed class LoginRequest
{
    /// <summary>契约字段 <c>password</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public required string Password { get; set; }
    /// <summary>契约字段 <c>username</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public required string Username { get; set; }
}

/// <summary>契约 <c>components.schemas.PlannedOfflineRequest</c>（request）。</summary>
public sealed class PlannedOfflineRequest
{
    /// <summary>契约字段 <c>daemonKind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("daemonKind")]
    public required string DaemonKind { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>occurredAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("occurredAt")]
    public System.DateTimeOffset? OccurredAt { get; set; }
    /// <summary>契约字段 <c>reason</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>契约 <c>components.schemas.RefreshRequest</c>（request）。</summary>
public sealed class RefreshRequest
{
    /// <summary>契约字段 <c>refreshToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
    public required string RefreshToken { get; set; }
}

/// <summary>契约 <c>components.schemas.RegisterRequest</c>（request）。</summary>
public sealed class RegisterRequest
{
    /// <summary>契约字段 <c>displayName</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
    /// <summary>契约字段 <c>email</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public required string Email { get; set; }
    /// <summary>契约字段 <c>password</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public required string Password { get; set; }
    /// <summary>契约字段 <c>username</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public required string Username { get; set; }
}

/// <summary>契约 <c>components.schemas.SiteEventUploadDto</c>（request）。</summary>
public sealed class SiteEventUploadDto
{
    /// <summary>契约字段 <c>date</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public string? Date { get; set; }
    /// <summary>契约字段 <c>durationMs</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }
    /// <summary>契约字段 <c>endMs</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("endMs")]
    public long? EndMs { get; set; }
    /// <summary>契约字段 <c>host</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("host")]
    public required string Host { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required string Kind { get; set; }
    /// <summary>契约字段 <c>startMs</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("startMs")]
    public long? StartMs { get; set; }
}

/// <summary>契约 <c>components.schemas.SiteEventsUploadRequest</c>（request）。</summary>
public sealed class SiteEventsUploadRequest
{
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>events</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("events")]
    public required System.Collections.Generic.List<SiteEventUploadDto> Events { get; set; }
}

/// <summary>契约 <c>components.schemas.StringApiResponse</c>（response）。</summary>
public sealed class StringApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public string? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.TrackerEventDto</c>（request）。</summary>
public sealed class TrackerEventDto
{
    /// <summary>契约字段 <c>appName</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("appName")]
    public string? AppName { get; set; }
    /// <summary>契约字段 <c>audible</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("audible")]
    public bool? Audible { get; set; }
    /// <summary>契约字段 <c>browser</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("browser")]
    public string? Browser { get; set; }
    /// <summary>契约字段 <c>commandLine</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("commandLine")]
    public string? CommandLine { get; set; }
    /// <summary>契约字段 <c>date</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("date")]
    public required string Date { get; set; }
    /// <summary>契约字段 <c>displayName</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
    /// <summary>契约字段 <c>domain</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("domain")]
    public string? Domain { get; set; }
    /// <summary>契约字段 <c>duration</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("duration")]
    public required double Duration { get; set; }
    /// <summary>契约字段 <c>eventType</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("eventType")]
    public required string EventType { get; set; }
    /// <summary>契约字段 <c>exePath</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("exePath")]
    public string? ExePath { get; set; }
    /// <summary>契约字段 <c>incognito</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("incognito")]
    public bool? Incognito { get; set; }
    /// <summary>契约字段 <c>instanceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }
    /// <summary>契约字段 <c>isIdle</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isIdle")]
    public required bool IsIdle { get; set; }
    /// <summary>契约字段 <c>isMediaActive</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isMediaActive")]
    public required bool IsMediaActive { get; set; }
    /// <summary>契约字段 <c>pagePath</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pagePath")]
    public string? PagePath { get; set; }
    /// <summary>契约字段 <c>pageVisitCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pageVisitCount")]
    public required int PageVisitCount { get; set; }
    /// <summary>契约字段 <c>pageVisitDuration</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pageVisitDuration")]
    public required double PageVisitDuration { get; set; }
    /// <summary>契约字段 <c>rawJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("rawJson")]
    public System.Text.Json.JsonElement? RawJson { get; set; }
    /// <summary>契约字段 <c>tabCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("tabCount")]
    public int? TabCount { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required string Timestamp { get; set; }
    /// <summary>契约字段 <c>url</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("url")]
    public string? Url { get; set; }
    /// <summary>契约字段 <c>windowTitle</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }
}

/// <summary>契约 <c>components.schemas.TrackerEventsUploadRequest</c>（request）。</summary>
public sealed class TrackerEventsUploadRequest
{
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>events</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("events")]
    public required System.Collections.Generic.List<TrackerEventDto> Events { get; set; }
}

/// <summary>契约 <c>components.schemas.TrackerHealthRequest</c>（request）。</summary>
public sealed class TrackerHealthRequest
{
    /// <summary>契约字段 <c>browserConnected</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("browserConnected")]
    public required bool BrowserConnected { get; set; }
    /// <summary>契约字段 <c>browserHeartbeatAgeSeconds</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("browserHeartbeatAgeSeconds")]
    public double? BrowserHeartbeatAgeSeconds { get; set; }
    /// <summary>契约字段 <c>deviceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
    public required string DeviceId { get; set; }
    /// <summary>契约字段 <c>eventsUploaded</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("eventsUploaded")]
    public required long EventsUploaded { get; set; }
    /// <summary>契约字段 <c>hookActive</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hookActive")]
    public required bool HookActive { get; set; }
    /// <summary>契约字段 <c>lastError</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastError")]
    public string? LastError { get; set; }
    /// <summary>契约字段 <c>pollCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pollCount")]
    public required long PollCount { get; set; }
    /// <summary>契约字段 <c>sessionsCreated</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sessionsCreated")]
    public required long SessionsCreated { get; set; }
    /// <summary>契约字段 <c>siteConnected</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("siteConnected")]
    public bool SiteConnected { get; set; }
    /// <summary>契约字段 <c>siteEventsUploaded</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("siteEventsUploaded")]
    public long SiteEventsUploaded { get; set; }
    /// <summary>契约字段 <c>siteLastError</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("siteLastError")]
    public string? SiteLastError { get; set; }
    /// <summary>契约字段 <c>siteLastEventAgeSeconds</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("siteLastEventAgeSeconds")]
    public double? SiteLastEventAgeSeconds { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
    /// <summary>契约字段 <c>uploadFailures</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadFailures")]
    public required long UploadFailures { get; set; }
    /// <summary>契约字段 <c>uptimeSeconds</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uptimeSeconds")]
    public required double UptimeSeconds { get; set; }
}

/// <summary>契约 <c>components.schemas.UserInfo</c>（response）。</summary>
public sealed class UserInfo
{
    /// <summary>契约字段 <c>displayName</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
    /// <summary>契约字段 <c>id</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; set; }
    /// <summary>契约字段 <c>role</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("role")]
    public required string Role { get; set; }
    /// <summary>契约字段 <c>username</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("username")]
    public required string Username { get; set; }
}
