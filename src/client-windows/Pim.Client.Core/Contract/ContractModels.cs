// ---------------------------------------------------------------------------
// 本文件由工具生成，请勿手工修改 / GENERATED FILE — DO NOT EDIT BY HAND.
//
//   契约快照      : contract/openapi.json (sha256 e5c555e2864e182f98242b0bbe50b156f755e9ea18dd5c57724518edd6bd8d76)
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

/// <summary>契约 <c>components.schemas.AiAsrDestinationConsent</c>（request+response）。</summary>
public sealed class AiAsrDestinationConsent
{
    /// <summary>契约字段 <c>confirmed</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("confirmed")]
    public required bool Confirmed { get; set; }
    /// <summary>契约字段 <c>endpoint</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("endpoint")]
    public required string Endpoint { get; set; }
    /// <summary>契约字段 <c>modelId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public required string ModelId { get; set; }
    /// <summary>契约字段 <c>modelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelRevision")]
    public required string ModelRevision { get; set; }
    /// <summary>契约字段 <c>presetId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("presetId")]
    public required string PresetId { get; set; }
    /// <summary>契约字段 <c>presetVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("presetVersion")]
    public required string PresetVersion { get; set; }
}

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

/// <summary>契约 <c>components.schemas.BeginNoteMaterialUploadRequest</c>（request）。</summary>
public sealed class BeginNoteMaterialUploadRequest
{
    /// <summary>契约字段 <c>baseVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("baseVersionId")]
    public string? BaseVersionId { get; set; }
    /// <summary>契约字段 <c>inputJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inputJson")]
    public required string InputJson { get; set; }
    /// <summary>契约字段 <c>manifestJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("manifestJson")]
    public required string ManifestJson { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>storageConsent</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("storageConsent")]
    public required bool StorageConsent { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.CalendarResponse</c>（response）。</summary>
public sealed class CalendarResponse
{
    /// <summary>契约字段 <c>canEdit</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("canEdit")]
    public bool CanEdit { get; set; }
    /// <summary>契约字段 <c>color</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("color")]
    public required string Color { get; set; }
    /// <summary>契约字段 <c>defaultMuted</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("defaultMuted")]
    public bool DefaultMuted { get; set; }
    /// <summary>契约字段 <c>eventCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("eventCount")]
    public required int EventCount { get; set; }
    /// <summary>契约字段 <c>id</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; set; }
    /// <summary>契约字段 <c>isDefault</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isDefault")]
    public required bool IsDefault { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required string Kind { get; set; }
    /// <summary>契约字段 <c>name</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public required string Name { get; set; }
    /// <summary>契约字段 <c>outlookCalendarBindingId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outlookCalendarBindingId")]
    public string? OutlookCalendarBindingId { get; set; }
    /// <summary>契约字段 <c>source</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string? Source { get; set; }
}

/// <summary>契约 <c>components.schemas.CalendarResponseListApiResponse</c>（response）。</summary>
public sealed class CalendarResponseListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<CalendarResponse>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.ClaimNoteAsrTaskRequest</c>（request）。</summary>
public sealed class ClaimNoteAsrTaskRequest
{
    /// <summary>契约字段 <c>idempotencyKey</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("idempotencyKey")]
    public required string IdempotencyKey { get; set; }
    /// <summary>契约字段 <c>leaseSeconds</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseSeconds")]
    public int LeaseSeconds { get; set; }
    /// <summary>契约字段 <c>nodeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("nodeId")]
    public required string NodeId { get; set; }
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

/// <summary>契约 <c>components.schemas.CreateEventRequest</c>（request）。</summary>
public sealed class CreateEventRequest
{
    /// <summary>契约字段 <c>attachmentReferences</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attachmentReferences")]
    public System.Collections.Generic.List<EventAttachmentReferenceDto>? AttachmentReferences { get; set; }
    /// <summary>契约字段 <c>attendees</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attendees")]
    public System.Collections.Generic.List<EventAttendeeDto>? Attendees { get; set; }
    /// <summary>契约字段 <c>calendarId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("calendarId")]
    public required string CalendarId { get; set; }
    /// <summary>契约字段 <c>categories</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("categories")]
    public System.Collections.Generic.List<string>? Categories { get; set; }
    /// <summary>契约字段 <c>description</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    /// <summary>契约字段 <c>descriptionFormat</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("descriptionFormat")]
    public string? DescriptionFormat { get; set; }
    /// <summary>契约字段 <c>dtEnd</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtEnd")]
    public required System.DateTimeOffset DtEnd { get; set; }
    /// <summary>契约字段 <c>dtStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtStart")]
    public required System.DateTimeOffset DtStart { get; set; }
    /// <summary>契约字段 <c>externalLink</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("externalLink")]
    public string? ExternalLink { get; set; }
    /// <summary>契约字段 <c>importance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("importance")]
    public string? Importance { get; set; }
    /// <summary>契约字段 <c>isAllDay</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isAllDay")]
    public bool IsAllDay { get; set; }
    /// <summary>契约字段 <c>isException</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isException")]
    public bool? IsException { get; set; }
    /// <summary>契约字段 <c>isMuted</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isMuted")]
    public bool? IsMuted { get; set; }
    /// <summary>契约字段 <c>isOnlineMeeting</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isOnlineMeeting")]
    public bool? IsOnlineMeeting { get; set; }
    /// <summary>契约字段 <c>isReminderOn</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isReminderOn")]
    public bool? IsReminderOn { get; set; }
    /// <summary>契约字段 <c>isSeriesMaster</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isSeriesMaster")]
    public bool? IsSeriesMaster { get; set; }
    /// <summary>契约字段 <c>location</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("location")]
    public string? Location { get; set; }
    /// <summary>契约字段 <c>onlineMeetingProvider</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("onlineMeetingProvider")]
    public string? OnlineMeetingProvider { get; set; }
    /// <summary>契约字段 <c>onlineMeetingUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("onlineMeetingUrl")]
    public string? OnlineMeetingUrl { get; set; }
    /// <summary>契约字段 <c>organizer</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("organizer")]
    public EventPersonDto? Organizer { get; set; }
    /// <summary>契约字段 <c>rRule</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("rRule")]
    public string? RRule { get; set; }
    /// <summary>契约字段 <c>recurrenceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recurrenceId")]
    public string? RecurrenceId { get; set; }
    /// <summary>契约字段 <c>reminderMinutesBeforeStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("reminderMinutesBeforeStart")]
    public int? ReminderMinutesBeforeStart { get; set; }
    /// <summary>契约字段 <c>sensitivity</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sensitivity")]
    public string? Sensitivity { get; set; }
    /// <summary>契约字段 <c>seriesMasterId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("seriesMasterId")]
    public string? SeriesMasterId { get; set; }
    /// <summary>契约字段 <c>showAs</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("showAs")]
    public string? ShowAs { get; set; }
    /// <summary>契约字段 <c>timeZoneId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timeZoneId")]
    public string? TimeZoneId { get; set; }
    /// <summary>契约字段 <c>title</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public required string Title { get; set; }
    /// <summary>契约字段 <c>uid</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uid")]
    public string? Uid { get; set; }
}

/// <summary>契约 <c>components.schemas.CreateTaskRequest</c>（request）。</summary>
public sealed class CreateTaskRequest
{
    /// <summary>契约字段 <c>calendarId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("calendarId")]
    public string? CalendarId { get; set; }
    /// <summary>契约字段 <c>description</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    /// <summary>契约字段 <c>dtStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtStart")]
    public System.DateTimeOffset? DtStart { get; set; }
    /// <summary>契约字段 <c>due</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("due")]
    public System.DateTimeOffset? Due { get; set; }
    /// <summary>契约字段 <c>estimatedDuration</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("estimatedDuration")]
    public string? EstimatedDuration { get; set; }
    /// <summary>契约字段 <c>minimumSegment</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("minimumSegment")]
    public string? MinimumSegment { get; set; }
    /// <summary>契约字段 <c>percentComplete</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public int? PercentComplete { get; set; }
    /// <summary>契约字段 <c>plannedEnd</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("plannedEnd")]
    public System.DateTimeOffset? PlannedEnd { get; set; }
    /// <summary>契约字段 <c>priority</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("priority")]
    public required int Priority { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public CreateTaskRequestStatus? Status { get; set; }
    /// <summary>契约字段 <c>taskBookId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskBookId")]
    public string? TaskBookId { get; set; }
    /// <summary>契约字段 <c>title</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public required string Title { get; set; }
}

/// <summary>契约 <c>components.schemas.CreateTaskRequestStatus</c>（request）。</summary>
/// <summary>
/// 契约枚举 <c>CreateTaskRequestStatus</c>：wire 值由生成 converter 精确往返。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(CreateTaskRequestStatusWireConverter))]
public enum CreateTaskRequestStatus
{
    NEEDS_ACTION,
    IN_PROCESS,
    COMPLETED,
    CANCELLED,
}

public sealed class CreateTaskRequestStatusWireConverter : System.Text.Json.Serialization.JsonConverter<CreateTaskRequestStatus>
{
    public override CreateTaskRequestStatus Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
            throw new System.Text.Json.JsonException("CreateTaskRequestStatus must be a JSON string");
        return reader.GetString() switch
        {
            "NEEDS-ACTION" => CreateTaskRequestStatus.NEEDS_ACTION,
            "IN-PROCESS" => CreateTaskRequestStatus.IN_PROCESS,
            "COMPLETED" => CreateTaskRequestStatus.COMPLETED,
            "CANCELLED" => CreateTaskRequestStatus.CANCELLED,
            _ => throw new System.Text.Json.JsonException("Unknown CreateTaskRequestStatus wire value"),
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, CreateTaskRequestStatus value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            CreateTaskRequestStatus.NEEDS_ACTION => "NEEDS-ACTION",
            CreateTaskRequestStatus.IN_PROCESS => "IN-PROCESS",
            CreateTaskRequestStatus.COMPLETED => "COMPLETED",
            CreateTaskRequestStatus.CANCELLED => "CANCELLED",
            _ => throw new System.Text.Json.JsonException("Unknown CreateTaskRequestStatus value"),
        });
    }
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
/// 契约枚举 <c>DaemonSourceState</c>：wire 值由生成 converter 精确往返。
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

/// <summary>契约 <c>components.schemas.EndpointHeartbeatRequest</c>（request）。</summary>
public sealed class EndpointHeartbeatRequest
{
    /// <summary>契约字段 <c>appVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }
    /// <summary>契约字段 <c>collectionCacheCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("collectionCacheCount")]
    public int? CollectionCacheCount { get; set; }
    /// <summary>契约字段 <c>platform</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("platform")]
    public required string Platform { get; set; }
    /// <summary>契约字段 <c>uploadStatus</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadStatus")]
    public string? UploadStatus { get; set; }
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

/// <summary>契约 <c>components.schemas.EndpointStatusDtoApiResponse</c>（response）。</summary>
public sealed class EndpointStatusDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public EndpointStatusDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
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

/// <summary>契约 <c>components.schemas.EventAttachmentReferenceDto</c>（request+response）。</summary>
public sealed class EventAttachmentReferenceDto
{
    /// <summary>契约字段 <c>canDownload</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("canDownload")]
    public bool CanDownload { get; set; }
    /// <summary>契约字段 <c>contentType</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentType")]
    public string? ContentType { get; set; }
    /// <summary>契约字段 <c>id</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required string Kind { get; set; }
    /// <summary>契约字段 <c>name</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public required string Name { get; set; }
    /// <summary>契约字段 <c>size</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("size")]
    public long? Size { get; set; }
}

/// <summary>契约 <c>components.schemas.EventAttendeeDto</c>（request+response）。</summary>
public sealed class EventAttendeeDto
{
    /// <summary>契约字段 <c>email</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public required string Email { get; set; }
    /// <summary>契约字段 <c>name</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
    /// <summary>契约字段 <c>type</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>契约 <c>components.schemas.EventPersonDto</c>（request+response）。</summary>
public sealed class EventPersonDto
{
    /// <summary>契约字段 <c>email</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public string? Email { get; set; }
    /// <summary>契约字段 <c>name</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>契约 <c>components.schemas.EventResponse</c>（response）。</summary>
public sealed class EventResponse
{
    /// <summary>契约字段 <c>attachmentReferences</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attachmentReferences")]
    public System.Collections.Generic.List<EventAttachmentReferenceDto>? AttachmentReferences { get; set; }
    /// <summary>契约字段 <c>attendees</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attendees")]
    public System.Collections.Generic.List<EventAttendeeDto>? Attendees { get; set; }
    /// <summary>契约字段 <c>calendarId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("calendarId")]
    public required string CalendarId { get; set; }
    /// <summary>契约字段 <c>categories</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("categories")]
    public System.Collections.Generic.List<string>? Categories { get; set; }
    /// <summary>契约字段 <c>description</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    /// <summary>契约字段 <c>descriptionFormat</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("descriptionFormat")]
    public string? DescriptionFormat { get; set; }
    /// <summary>契约字段 <c>dtEnd</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtEnd")]
    public required System.DateTimeOffset DtEnd { get; set; }
    /// <summary>契约字段 <c>dtStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtStart")]
    public required System.DateTimeOffset DtStart { get; set; }
    /// <summary>契约字段 <c>exDatesJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("exDatesJson")]
    public string? ExDatesJson { get; set; }
    /// <summary>契约字段 <c>externalLink</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("externalLink")]
    public string? ExternalLink { get; set; }
    /// <summary>契约字段 <c>id</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; set; }
    /// <summary>契约字段 <c>importance</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("importance")]
    public string? Importance { get; set; }
    /// <summary>契约字段 <c>isAllDay</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isAllDay")]
    public bool IsAllDay { get; set; }
    /// <summary>契约字段 <c>isCancelled</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isCancelled")]
    public bool IsCancelled { get; set; }
    /// <summary>契约字段 <c>isException</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isException")]
    public bool IsException { get; set; }
    /// <summary>契约字段 <c>isMuted</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isMuted")]
    public bool? IsMuted { get; set; }
    /// <summary>契约字段 <c>isOnlineMeeting</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isOnlineMeeting")]
    public bool? IsOnlineMeeting { get; set; }
    /// <summary>契约字段 <c>isReminderOn</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isReminderOn")]
    public bool? IsReminderOn { get; set; }
    /// <summary>契约字段 <c>isSeriesMaster</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isSeriesMaster")]
    public bool IsSeriesMaster { get; set; }
    /// <summary>契约字段 <c>location</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("location")]
    public string? Location { get; set; }
    /// <summary>契约字段 <c>onlineMeetingProvider</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("onlineMeetingProvider")]
    public string? OnlineMeetingProvider { get; set; }
    /// <summary>契约字段 <c>onlineMeetingUrl</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("onlineMeetingUrl")]
    public string? OnlineMeetingUrl { get; set; }
    /// <summary>契约字段 <c>organizer</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("organizer")]
    public EventPersonDto? Organizer { get; set; }
    /// <summary>契约字段 <c>originalEventId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("originalEventId")]
    public string? OriginalEventId { get; set; }
    /// <summary>契约字段 <c>outlookAdditionalInfo</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outlookAdditionalInfo")]
    public OutlookAdditionalInfoDto? OutlookAdditionalInfo { get; set; }
    /// <summary>契约字段 <c>outlookCalendarBindingId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outlookCalendarBindingId")]
    public string? OutlookCalendarBindingId { get; set; }
    /// <summary>契约字段 <c>outlookEtag</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outlookEtag")]
    public string? OutlookEtag { get; set; }
    /// <summary>契约字段 <c>outlookEventId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outlookEventId")]
    public string? OutlookEventId { get; set; }
    /// <summary>契约字段 <c>outlookEventType</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("outlookEventType")]
    public string? OutlookEventType { get; set; }
    /// <summary>契约字段 <c>rRule</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("rRule")]
    public string? RRule { get; set; }
    /// <summary>契约字段 <c>recurrenceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recurrenceId")]
    public string? RecurrenceId { get; set; }
    /// <summary>契约字段 <c>recurrenceMetadataJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recurrenceMetadataJson")]
    public string? RecurrenceMetadataJson { get; set; }
    /// <summary>契约字段 <c>reminderMinutesBeforeStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("reminderMinutesBeforeStart")]
    public int? ReminderMinutesBeforeStart { get; set; }
    /// <summary>契约字段 <c>sensitivity</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sensitivity")]
    public string? Sensitivity { get; set; }
    /// <summary>契约字段 <c>seriesMasterId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("seriesMasterId")]
    public string? SeriesMasterId { get; set; }
    /// <summary>契约字段 <c>showAs</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("showAs")]
    public string? ShowAs { get; set; }
    /// <summary>契约字段 <c>source</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public required string Source { get; set; }
    /// <summary>契约字段 <c>sourceTimeZoneId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceTimeZoneId")]
    public string? SourceTimeZoneId { get; set; }
    /// <summary>契约字段 <c>sourceUid</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceUid")]
    public string? SourceUid { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
    /// <summary>契约字段 <c>timeZoneId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timeZoneId")]
    public string? TimeZoneId { get; set; }
    /// <summary>契约字段 <c>title</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public required string Title { get; set; }
    /// <summary>契约字段 <c>uid</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uid")]
    public required string Uid { get; set; }
}

/// <summary>契约 <c>components.schemas.EventResponseApiResponse</c>（response）。</summary>
public sealed class EventResponseApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public EventResponse? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.EventResponsePagedResult</c>（response）。</summary>
public sealed class EventResponsePagedResult
{
    /// <summary>契约字段 <c>items</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public required System.Collections.Generic.List<EventResponse> Items { get; set; }
    /// <summary>契约字段 <c>page</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("page")]
    public required int Page { get; set; }
    /// <summary>契约字段 <c>pageSize</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pageSize")]
    public required int PageSize { get; set; }
    /// <summary>契约字段 <c>totalCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalCount")]
    public required int TotalCount { get; set; }
    /// <summary>契约字段 <c>totalPages</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalPages")]
    public required int TotalPages { get; set; }
}

/// <summary>契约 <c>components.schemas.EventResponsePagedResultApiResponse</c>（response）。</summary>
public sealed class EventResponsePagedResultApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public EventResponsePagedResult? Data { get; set; }
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

/// <summary>契约 <c>components.schemas.NoteAsrAckDto</c>（response）。</summary>
public sealed class NoteAsrAckDto
{
    /// <summary>契约字段 <c>duplicate</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("duplicate")]
    public required bool Duplicate { get; set; }
    /// <summary>契约字段 <c>fencingToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fencingToken")]
    public required long FencingToken { get; set; }
    /// <summary>契约字段 <c>leaseId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseId")]
    public required string LeaseId { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrAckDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrAckDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrAckDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrAckRequest</c>（request）。</summary>
public sealed class NoteAsrAckRequest
{
    /// <summary>契约字段 <c>coverageJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("coverageJson")]
    public string? CoverageJson { get; set; }
    /// <summary>契约字段 <c>errorCode</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
    /// <summary>契约字段 <c>errorMessage</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
    /// <summary>契约字段 <c>fencingToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fencingToken")]
    public required long FencingToken { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required string Kind { get; set; }
    /// <summary>契约字段 <c>leaseId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseId")]
    public required string LeaseId { get; set; }
    /// <summary>契约字段 <c>leaseToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseToken")]
    public required string LeaseToken { get; set; }
    /// <summary>契约字段 <c>providerHandle</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("providerHandle")]
    public string? ProviderHandle { get; set; }
    /// <summary>契约字段 <c>result</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public required string Result { get; set; }
    /// <summary>契约字段 <c>resultText</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("resultText")]
    public string? ResultText { get; set; }
    /// <summary>契约字段 <c>segmentIndex</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("segmentIndex")]
    public required int SegmentIndex { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrAttemptDto</c>（response）。</summary>
public sealed class NoteAsrAttemptDto
{
    /// <summary>契约字段 <c>errorMessage</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
    /// <summary>契约字段 <c>evidenceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("evidenceId")]
    public string? EvidenceId { get; set; }
    /// <summary>契约字段 <c>logId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("logId")]
    public string? LogId { get; set; }
    /// <summary>契约字段 <c>providerHandle</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("providerHandle")]
    public string? ProviderHandle { get; set; }
    /// <summary>契约字段 <c>receiptKind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("receiptKind")]
    public string? ReceiptKind { get; set; }
    /// <summary>契约字段 <c>requestId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public required string RequestId { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrClaimDto</c>（response）。</summary>
public sealed class NoteAsrClaimDto
{
    /// <summary>契约字段 <c>execution</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("execution")]
    public NoteAsrExecutionDto? Execution { get; set; }
    /// <summary>契约字段 <c>lease</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lease")]
    public NoteAsrLeaseDto? Lease { get; set; }
    /// <summary>契约字段 <c>leaseId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseId")]
    public string? LeaseId { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrClaimDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrClaimDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrClaimDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrExecutionDto</c>（response）。</summary>
public sealed class NoteAsrExecutionDto
{
    /// <summary>契约字段 <c>attemptId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attemptId")]
    public required string AttemptId { get; set; }
    /// <summary>契约字段 <c>configRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("configRevision")]
    public required string ConfigRevision { get; set; }
    /// <summary>契约字段 <c>consentVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("consentVersion")]
    public required string ConsentVersion { get; set; }
    /// <summary>契约字段 <c>expectedSegments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedSegments")]
    public required int ExpectedSegments { get; set; }
    /// <summary>契约字段 <c>inputSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inputSha256")]
    public required string InputSha256 { get; set; }
    /// <summary>契约字段 <c>language</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("language")]
    public required string Language { get; set; }
    /// <summary>契约字段 <c>localModelId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("localModelId")]
    public string? LocalModelId { get; set; }
    /// <summary>契约字段 <c>localModelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("localModelRevision")]
    public string? LocalModelRevision { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>ownerId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("ownerId")]
    public required string OwnerId { get; set; }
    /// <summary>契约字段 <c>route</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("route")]
    public required string Route { get; set; }
    /// <summary>契约字段 <c>segments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("segments")]
    public required System.Collections.Generic.List<NoteAsrSegmentInputDto> Segments { get; set; }
    /// <summary>契约字段 <c>serverNow</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("serverNow")]
    public required System.DateTimeOffset ServerNow { get; set; }
    /// <summary>契约字段 <c>serverOrigin</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("serverOrigin")]
    public required string ServerOrigin { get; set; }
    /// <summary>契约字段 <c>taskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrLeaseDto</c>（response）。</summary>
public sealed class NoteAsrLeaseDto
{
    /// <summary>契约字段 <c>attemptId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attemptId")]
    public required string AttemptId { get; set; }
    /// <summary>契约字段 <c>expiresAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expiresAt")]
    public required System.DateTimeOffset ExpiresAt { get; set; }
    /// <summary>契约字段 <c>fencingToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fencingToken")]
    public required long FencingToken { get; set; }
    /// <summary>契约字段 <c>leaseId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseId")]
    public required string LeaseId { get; set; }
    /// <summary>契约字段 <c>leaseToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseToken")]
    public required string LeaseToken { get; set; }
    /// <summary>契约字段 <c>ownerId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }
    /// <summary>契约字段 <c>route</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("route")]
    public required string Route { get; set; }
    /// <summary>契约字段 <c>serverNow</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("serverNow")]
    public System.DateTimeOffset ServerNow { get; set; }
    /// <summary>契约字段 <c>serverOrigin</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("serverOrigin")]
    public string? ServerOrigin { get; set; }
    /// <summary>契约字段 <c>stopRequested</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("stopRequested")]
    public bool StopRequested { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrLeaseDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrLeaseDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrLeaseDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrLeaseHeartbeatRequest</c>（request）。</summary>
public sealed class NoteAsrLeaseHeartbeatRequest
{
    /// <summary>契约字段 <c>fencingToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("fencingToken")]
    public required long FencingToken { get; set; }
    /// <summary>契约字段 <c>leaseId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseId")]
    public required string LeaseId { get; set; }
    /// <summary>契约字段 <c>leaseToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("leaseToken")]
    public required string LeaseToken { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrNodeDto</c>（response）。</summary>
public sealed class NoteAsrNodeDto
{
    /// <summary>契约字段 <c>capabilities</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("capabilities")]
    public required System.Collections.Generic.List<string> Capabilities { get; set; }
    /// <summary>契约字段 <c>claimable</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("claimable")]
    public required bool Claimable { get; set; }
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>enabled</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public required bool Enabled { get; set; }
    /// <summary>契约字段 <c>lastHeartbeatAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("lastHeartbeatAt")]
    public required System.DateTimeOffset LastHeartbeatAt { get; set; }
    /// <summary>契约字段 <c>modelId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public required string ModelId { get; set; }
    /// <summary>契约字段 <c>modelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelRevision")]
    public required string ModelRevision { get; set; }
    /// <summary>契约字段 <c>modelVerified</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelVerified")]
    public required bool ModelVerified { get; set; }
    /// <summary>契约字段 <c>nodeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("nodeId")]
    public required string NodeId { get; set; }
    /// <summary>契约字段 <c>nodeIdExternal</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("nodeIdExternal")]
    public required string NodeIdExternal { get; set; }
    /// <summary>契约字段 <c>resourceSnapshotJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("resourceSnapshotJson")]
    public string? ResourceSnapshotJson { get; set; }
    /// <summary>契约字段 <c>runtimeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("runtimeId")]
    public string? RuntimeId { get; set; }
    /// <summary>契约字段 <c>runtimeVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("runtimeVersion")]
    public string? RuntimeVersion { get; set; }
    /// <summary>契约字段 <c>sampledAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sampledAt")]
    public System.DateTimeOffset? SampledAt { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrNodeDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrNodeDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrNodeDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrNodeHeartbeatRequest</c>（request）。</summary>
public sealed class NoteAsrNodeHeartbeatRequest
{
    /// <summary>契约字段 <c>capabilities</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("capabilities")]
    public required System.Collections.Generic.List<string> Capabilities { get; set; }
    /// <summary>契约字段 <c>modelId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public required string ModelId { get; set; }
    /// <summary>契约字段 <c>modelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelRevision")]
    public required string ModelRevision { get; set; }
    /// <summary>契约字段 <c>modelVerified</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelVerified")]
    public required bool ModelVerified { get; set; }
    /// <summary>契约字段 <c>nodeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("nodeId")]
    public required string NodeId { get; set; }
    /// <summary>契约字段 <c>resourceSnapshotJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("resourceSnapshotJson")]
    public string? ResourceSnapshotJson { get; set; }
    /// <summary>契约字段 <c>runtimeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("runtimeId")]
    public string? RuntimeId { get; set; }
    /// <summary>契约字段 <c>runtimeVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("runtimeVersion")]
    public string? RuntimeVersion { get; set; }
    /// <summary>契约字段 <c>sampledAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sampledAt")]
    public System.DateTimeOffset? SampledAt { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrRouteConsent</c>（request）。</summary>
public sealed class NoteAsrRouteConsent
{
    /// <summary>契约字段 <c>batchScopeConfirmed</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("batchScopeConfirmed")]
    public bool BatchScopeConfirmed { get; set; }
    /// <summary>契约字段 <c>configRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("configRevision")]
    public required string ConfigRevision { get; set; }
    /// <summary>契约字段 <c>confirmed</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("confirmed")]
    public required bool Confirmed { get; set; }
    /// <summary>契约字段 <c>consentVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("consentVersion")]
    public required string ConsentVersion { get; set; }
    /// <summary>契约字段 <c>executionMode</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("executionMode")]
    public string? ExecutionMode { get; set; }
    /// <summary>契约字段 <c>route</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("route")]
    public required string Route { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSegmentDto</c>（response）。</summary>
public sealed class NoteAsrSegmentDto
{
    /// <summary>契约字段 <c>attempts</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attempts")]
    public required System.Collections.Generic.List<NoteAsrAttemptDto> Attempts { get; set; }
    /// <summary>契约字段 <c>chunkIndex</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("chunkIndex")]
    public required int ChunkIndex { get; set; }
    /// <summary>契约字段 <c>coverageJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("coverageJson")]
    public string? CoverageJson { get; set; }
    /// <summary>契约字段 <c>evidenceId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("evidenceId")]
    public string? EvidenceId { get; set; }
    /// <summary>契约字段 <c>index</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("index")]
    public required int Index { get; set; }
    /// <summary>契约字段 <c>providerHandle</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("providerHandle")]
    public string? ProviderHandle { get; set; }
    /// <summary>契约字段 <c>receiptKind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("receiptKind")]
    public string? ReceiptKind { get; set; }
    /// <summary>契约字段 <c>recordingId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recordingId")]
    public required string RecordingId { get; set; }
    /// <summary>契约字段 <c>recordingOffsetSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recordingOffsetSamples")]
    public required long RecordingOffsetSamples { get; set; }
    /// <summary>契约字段 <c>recordingSegment</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recordingSegment")]
    public required int RecordingSegment { get; set; }
    /// <summary>契约字段 <c>samples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("samples")]
    public required long Samples { get; set; }
    /// <summary>契约字段 <c>sourceFileIndex</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceFileIndex")]
    public required int SourceFileIndex { get; set; }
    /// <summary>契约字段 <c>sourceOffsetSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceOffsetSamples")]
    public required long SourceOffsetSamples { get; set; }
    /// <summary>契约字段 <c>sourcePath</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourcePath")]
    public required string SourcePath { get; set; }
    /// <summary>契约字段 <c>sourceSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceSha256")]
    public required string SourceSha256 { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
    /// <summary>契约字段 <c>timelineOffsetSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timelineOffsetSamples")]
    public required long TimelineOffsetSamples { get; set; }
    /// <summary>契约字段 <c>transcript</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("transcript")]
    public string? Transcript { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSegmentInputDto</c>（response）。</summary>
public sealed class NoteAsrSegmentInputDto
{
    /// <summary>契约字段 <c>chunkIndex</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("chunkIndex")]
    public required int ChunkIndex { get; set; }
    /// <summary>契约字段 <c>contentEndpoint</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentEndpoint")]
    public required string ContentEndpoint { get; set; }
    /// <summary>契约字段 <c>index</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("index")]
    public required int Index { get; set; }
    /// <summary>契约字段 <c>recordingId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recordingId")]
    public required string RecordingId { get; set; }
    /// <summary>契约字段 <c>recordingSegment</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recordingSegment")]
    public required int RecordingSegment { get; set; }
    /// <summary>契约字段 <c>samples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("samples")]
    public required long Samples { get; set; }
    /// <summary>契约字段 <c>sourceFileIndex</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceFileIndex")]
    public required int SourceFileIndex { get; set; }
    /// <summary>契约字段 <c>sourceOffsetSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceOffsetSamples")]
    public required long SourceOffsetSamples { get; set; }
    /// <summary>契约字段 <c>sourceSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceSha256")]
    public required string SourceSha256 { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSegmentsDto</c>（response）。</summary>
public sealed class NoteAsrSegmentsDto
{
    /// <summary>契约字段 <c>items</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public required System.Collections.Generic.List<NoteAsrSegmentDto> Items { get; set; }
    /// <summary>契约字段 <c>offset</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("offset")]
    public required int Offset { get; set; }
    /// <summary>契约字段 <c>total</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public required int Total { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSegmentsDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrSegmentsDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrSegmentsDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSourceDto</c>（response）。</summary>
public sealed class NoteAsrSourceDto
{
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>hasConflict</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasConflict")]
    public required bool HasConflict { get; set; }
    /// <summary>契约字段 <c>isCurrent</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isCurrent")]
    public required bool IsCurrent { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>recordings</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("recordings")]
    public required int Recordings { get; set; }
    /// <summary>契约字段 <c>sampleRate</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sampleRate")]
    public required int SampleRate { get; set; }
    /// <summary>契约字段 <c>title</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public required string Title { get; set; }
    /// <summary>契约字段 <c>totalSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalSamples")]
    public required long TotalSamples { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSourcesDto</c>（response）。</summary>
public sealed class NoteAsrSourcesDto
{
    /// <summary>契约字段 <c>items</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public required System.Collections.Generic.List<NoteAsrSourceDto> Items { get; set; }
    /// <summary>契约字段 <c>offset</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("offset")]
    public required int Offset { get; set; }
    /// <summary>契约字段 <c>total</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public required int Total { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrSourcesDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrSourcesDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrSourcesDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrTaskDto</c>（response）。</summary>
public sealed class NoteAsrTaskDto
{
    /// <summary>契约字段 <c>attemptId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attemptId")]
    public string? AttemptId { get; set; }
    /// <summary>契约字段 <c>backgroundProcessingAvailable</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("backgroundProcessingAvailable")]
    public bool BackgroundProcessingAvailable { get; set; }
    /// <summary>契约字段 <c>completedSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completedSamples")]
    public required long CompletedSamples { get; set; }
    /// <summary>契约字段 <c>completedSegments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completedSegments")]
    public required int CompletedSegments { get; set; }
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>destination</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("destination")]
    public AiAsrDestinationConsent? Destination { get; set; }
    /// <summary>契约字段 <c>executionMode</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("executionMode")]
    public string? ExecutionMode { get; set; }
    /// <summary>契约字段 <c>failedSegments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("failedSegments")]
    public required int FailedSegments { get; set; }
    /// <summary>契约字段 <c>inputSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inputSha256")]
    public required string InputSha256 { get; set; }
    /// <summary>契约字段 <c>language</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("language")]
    public required string Language { get; set; }
    /// <summary>契约字段 <c>manifestSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("manifestSha256")]
    public required string ManifestSha256 { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>phase</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("phase")]
    public required string Phase { get; set; }
    /// <summary>契约字段 <c>revision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("revision")]
    public required string Revision { get; set; }
    /// <summary>契约字段 <c>route</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("route")]
    public string? Route { get; set; }
    /// <summary>契约字段 <c>routeConfigRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("routeConfigRevision")]
    public string? RouteConfigRevision { get; set; }
    /// <summary>契约字段 <c>routeConsentVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("routeConsentVersion")]
    public string? RouteConsentVersion { get; set; }
    /// <summary>契约字段 <c>sampleRate</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sampleRate")]
    public required int SampleRate { get; set; }
    /// <summary>契约字段 <c>stopRequested</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("stopRequested")]
    public required bool StopRequested { get; set; }
    /// <summary>契约字段 <c>taskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
    /// <summary>契约字段 <c>totalSamples</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalSamples")]
    public required long TotalSamples { get; set; }
    /// <summary>契约字段 <c>totalSegments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalSegments")]
    public required int TotalSegments { get; set; }
    /// <summary>契约字段 <c>updatedAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public required System.DateTimeOffset UpdatedAt { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrTaskDtoApiResponse</c>（response）。</summary>
public sealed class NoteAsrTaskDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteAsrTaskDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteAsrTaskDtoIReadOnlyListApiResponse</c>（response）。</summary>
public sealed class NoteAsrTaskDtoIReadOnlyListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<NoteAsrTaskDto>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedArtifactDto</c>（response）。</summary>
public sealed class NoteDerivedArtifactDto
{
    /// <summary>契约字段 <c>artifactId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("artifactId")]
    public required string ArtifactId { get; set; }
    /// <summary>契约字段 <c>headVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("headVersionId")]
    public string? HeadVersionId { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required NoteDerivedArtifactKind Kind { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>revision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("revision")]
    public required string Revision { get; set; }
    /// <summary>契约字段 <c>versions</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versions")]
    public required System.Collections.Generic.List<NoteDerivedVersionSummaryDto> Versions { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedArtifactDtoApiResponse</c>（response）。</summary>
public sealed class NoteDerivedArtifactDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteDerivedArtifactDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedArtifactDtoIReadOnlyListApiResponse</c>（response）。</summary>
public sealed class NoteDerivedArtifactDtoIReadOnlyListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<NoteDerivedArtifactDto>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedArtifactKind</c>（response）。</summary>
/// <summary>
/// 契约枚举 <c>NoteDerivedArtifactKind</c>：wire 值由生成 converter 精确往返。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum NoteDerivedArtifactKind
{
    CompleteRecord,
    IndependentAnalysis,
}

/// <summary>契约 <c>components.schemas.NoteDerivedArtifactRefDto</c>（response）。</summary>
public sealed class NoteDerivedArtifactRefDto
{
    /// <summary>契约字段 <c>artifactId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("artifactId")]
    public required string ArtifactId { get; set; }
    /// <summary>契约字段 <c>headVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("headVersionId")]
    public string? HeadVersionId { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required NoteDerivedArtifactKind Kind { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedAttemptDto</c>（response）。</summary>
public sealed class NoteDerivedAttemptDto
{
    /// <summary>契约字段 <c>errorCode</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
    /// <summary>契约字段 <c>errorMessage</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
    /// <summary>契约字段 <c>logId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("logId")]
    public string? LogId { get; set; }
    /// <summary>契约字段 <c>requestId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("requestId")]
    public required string RequestId { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedCoverageDto</c>（response）。</summary>
public sealed class NoteDerivedCoverageDto
{
    /// <summary>契约字段 <c>complete</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("complete")]
    public required bool Complete { get; set; }
    /// <summary>契约字段 <c>completedInkPreviews</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completedInkPreviews")]
    public required int CompletedInkPreviews { get; set; }
    /// <summary>契约字段 <c>completedPhotos</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completedPhotos")]
    public required int CompletedPhotos { get; set; }
    /// <summary>契约字段 <c>completedTranscriptSegments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completedTranscriptSegments")]
    public required int CompletedTranscriptSegments { get; set; }
    /// <summary>契约字段 <c>completedVisionBatches</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("completedVisionBatches")]
    public required int CompletedVisionBatches { get; set; }
    /// <summary>契约字段 <c>inkPreviews</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inkPreviews")]
    public required int InkPreviews { get; set; }
    /// <summary>契约字段 <c>issues</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("issues")]
    public required System.Collections.Generic.List<NoteDerivedIssueDto> Issues { get; set; }
    /// <summary>契约字段 <c>photos</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("photos")]
    public required int Photos { get; set; }
    /// <summary>契约字段 <c>textBlocks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("textBlocks")]
    public required int TextBlocks { get; set; }
    /// <summary>契约字段 <c>transcriptSegments</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("transcriptSegments")]
    public required int TranscriptSegments { get; set; }
    /// <summary>契约字段 <c>visionBatches</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("visionBatches")]
    public required int VisionBatches { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedGenerationConsent</c>（request+response）。</summary>
public sealed class NoteDerivedGenerationConsent
{
    /// <summary>契约字段 <c>confirmed</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("confirmed")]
    public required bool Confirmed { get; set; }
    /// <summary>契约字段 <c>endpoint</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("endpoint")]
    public required string Endpoint { get; set; }
    /// <summary>契约字段 <c>modelId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public required string ModelId { get; set; }
    /// <summary>契约字段 <c>modelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelRevision")]
    public required string ModelRevision { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedIssueDto</c>（response）。</summary>
public sealed class NoteDerivedIssueDto
{
    /// <summary>契约字段 <c>blockId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("blockId")]
    public string? BlockId { get; set; }
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required string Code { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>sourcePath</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourcePath")]
    public string? SourcePath { get; set; }
    /// <summary>契约字段 <c>stepKey</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("stepKey")]
    public string? StepKey { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedMutationDto</c>（response）。</summary>
public sealed class NoteDerivedMutationDto
{
    /// <summary>契约字段 <c>artifact</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("artifact")]
    public required NoteDerivedArtifactDto Artifact { get; set; }
    /// <summary>契约字段 <c>hasConflict</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasConflict")]
    public required bool HasConflict { get; set; }
    /// <summary>契约字段 <c>isHead</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isHead")]
    public required bool IsHead { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedMutationDtoApiResponse</c>（response）。</summary>
public sealed class NoteDerivedMutationDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteDerivedMutationDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedStepDto</c>（response）。</summary>
public sealed class NoteDerivedStepDto
{
    /// <summary>契约字段 <c>attempts</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("attempts")]
    public required System.Collections.Generic.List<NoteDerivedAttemptDto> Attempts { get; set; }
    /// <summary>契约字段 <c>key</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public required string Key { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required NoteDerivedStepKind Kind { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedStepKind</c>（response）。</summary>
/// <summary>
/// 契约枚举 <c>NoteDerivedStepKind</c>：wire 值由生成 converter 精确往返。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum NoteDerivedStepKind
{
    ImageEvidence,
    RecordSynthesis,
    AnalysisSynthesis,
}

/// <summary>契约 <c>components.schemas.NoteDerivedStepsDto</c>（response）。</summary>
public sealed class NoteDerivedStepsDto
{
    /// <summary>契约字段 <c>items</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public required System.Collections.Generic.List<NoteDerivedStepDto> Items { get; set; }
    /// <summary>契约字段 <c>offset</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("offset")]
    public required int Offset { get; set; }
    /// <summary>契约字段 <c>total</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public required int Total { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedStepsDtoApiResponse</c>（response）。</summary>
public sealed class NoteDerivedStepsDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteDerivedStepsDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedTaskDto</c>（response）。</summary>
public sealed class NoteDerivedTaskDto
{
    /// <summary>契约字段 <c>artifacts</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("artifacts")]
    public required System.Collections.Generic.List<NoteDerivedArtifactRefDto> Artifacts { get; set; }
    /// <summary>契约字段 <c>asrRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("asrRevision")]
    public string? AsrRevision { get; set; }
    /// <summary>契约字段 <c>asrTaskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("asrTaskId")]
    public string? AsrTaskId { get; set; }
    /// <summary>契约字段 <c>backgroundProcessingAvailable</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("backgroundProcessingAvailable")]
    public bool BackgroundProcessingAvailable { get; set; }
    /// <summary>契约字段 <c>coverage</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("coverage")]
    public required NoteDerivedCoverageDto Coverage { get; set; }
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>destination</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("destination")]
    public required NoteDerivedGenerationConsent Destination { get; set; }
    /// <summary>契约字段 <c>inputSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inputSha256")]
    public required string InputSha256 { get; set; }
    /// <summary>契约字段 <c>issues</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("issues")]
    public required System.Collections.Generic.List<NoteDerivedIssueDto> Issues { get; set; }
    /// <summary>契约字段 <c>manifestSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("manifestSha256")]
    public required string ManifestSha256 { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>phase</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("phase")]
    public required string Phase { get; set; }
    /// <summary>契约字段 <c>planVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("planVersion")]
    public required string PlanVersion { get; set; }
    /// <summary>契约字段 <c>revision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("revision")]
    public required string Revision { get; set; }
    /// <summary>契约字段 <c>sourceVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceVersionId")]
    public required string SourceVersionId { get; set; }
    /// <summary>契约字段 <c>steps</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("steps")]
    public required System.Collections.Generic.List<NoteDerivedStepDto> Steps { get; set; }
    /// <summary>契约字段 <c>stopRequested</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("stopRequested")]
    public required bool StopRequested { get; set; }
    /// <summary>契约字段 <c>taskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
    /// <summary>契约字段 <c>updatedAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public required System.DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedTaskDtoApiResponse</c>（response）。</summary>
public sealed class NoteDerivedTaskDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteDerivedTaskDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedTaskDtoIReadOnlyListApiResponse</c>（response）。</summary>
public sealed class NoteDerivedTaskDtoIReadOnlyListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<NoteDerivedTaskDto>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedVersionAuthor</c>（response）。</summary>
/// <summary>
/// 契约枚举 <c>NoteDerivedVersionAuthor</c>：wire 值由生成 converter 精确往返。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum NoteDerivedVersionAuthor
{
    Model,
    User,
}

/// <summary>契约 <c>components.schemas.NoteDerivedVersionDto</c>（response）。</summary>
public sealed class NoteDerivedVersionDto
{
    /// <summary>契约字段 <c>aiCallLogId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("aiCallLogId")]
    public string? AiCallLogId { get; set; }
    /// <summary>契约字段 <c>artifactId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("artifactId")]
    public required string ArtifactId { get; set; }
    /// <summary>契约字段 <c>author</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("author")]
    public required NoteDerivedVersionAuthor Author { get; set; }
    /// <summary>契约字段 <c>contentMarkdown</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentMarkdown")]
    public required string ContentMarkdown { get; set; }
    /// <summary>契约字段 <c>contentSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentSha256")]
    public required string ContentSha256 { get; set; }
    /// <summary>契约字段 <c>coverage</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("coverage")]
    public NoteDerivedCoverageDto? Coverage { get; set; }
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>generationTaskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("generationTaskId")]
    public string? GenerationTaskId { get; set; }
    /// <summary>契约字段 <c>hasConflict</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasConflict")]
    public required bool HasConflict { get; set; }
    /// <summary>契约字段 <c>inputSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inputSha256")]
    public required string InputSha256 { get; set; }
    /// <summary>契约字段 <c>isHead</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isHead")]
    public required bool IsHead { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required NoteDerivedArtifactKind Kind { get; set; }
    /// <summary>契约字段 <c>manifestSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("manifestSha256")]
    public required string ManifestSha256 { get; set; }
    /// <summary>契约字段 <c>modelEndpoint</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelEndpoint")]
    public string? ModelEndpoint { get; set; }
    /// <summary>契约字段 <c>modelProfileId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelProfileId")]
    public string? ModelProfileId { get; set; }
    /// <summary>契约字段 <c>modelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelRevision")]
    public string? ModelRevision { get; set; }
    /// <summary>契约字段 <c>observedHeadVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("observedHeadVersionId")]
    public string? ObservedHeadVersionId { get; set; }
    /// <summary>契约字段 <c>parentVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("parentVersionId")]
    public string? ParentVersionId { get; set; }
    /// <summary>契约字段 <c>planVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("planVersion")]
    public required string PlanVersion { get; set; }
    /// <summary>契约字段 <c>promptVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("promptVersion")]
    public required string PromptVersion { get; set; }
    /// <summary>契约字段 <c>sourceCompleteRecordVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceCompleteRecordVersionId")]
    public string? SourceCompleteRecordVersionId { get; set; }
    /// <summary>契约字段 <c>sourceVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceVersionId")]
    public required string SourceVersionId { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedVersionDtoApiResponse</c>（response）。</summary>
public sealed class NoteDerivedVersionDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteDerivedVersionDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteDerivedVersionSummaryDto</c>（response）。</summary>
public sealed class NoteDerivedVersionSummaryDto
{
    /// <summary>契约字段 <c>author</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("author")]
    public required NoteDerivedVersionAuthor Author { get; set; }
    /// <summary>契约字段 <c>contentSha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentSha256")]
    public required string ContentSha256 { get; set; }
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>generationTaskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("generationTaskId")]
    public string? GenerationTaskId { get; set; }
    /// <summary>契约字段 <c>hasConflict</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasConflict")]
    public required bool HasConflict { get; set; }
    /// <summary>契约字段 <c>isHead</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isHead")]
    public required bool IsHead { get; set; }
    /// <summary>契约字段 <c>parentVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("parentVersionId")]
    public string? ParentVersionId { get; set; }
    /// <summary>契约字段 <c>sourceCompleteRecordVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceCompleteRecordVersionId")]
    public string? SourceCompleteRecordVersionId { get; set; }
    /// <summary>契约字段 <c>sourceVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sourceVersionId")]
    public required string SourceVersionId { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialUploadDto</c>（response）。</summary>
public sealed class NoteMaterialUploadDto
{
    /// <summary>契约字段 <c>baseVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("baseVersionId")]
    public string? BaseVersionId { get; set; }
    /// <summary>契约字段 <c>createdAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
    public required System.DateTimeOffset CreatedAt { get; set; }
    /// <summary>契约字段 <c>currentVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("currentVersionId")]
    public string? CurrentVersionId { get; set; }
    /// <summary>契约字段 <c>expiresAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expiresAt")]
    public required System.DateTimeOffset ExpiresAt { get; set; }
    /// <summary>契约字段 <c>files</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("files")]
    public required System.Collections.Generic.List<NoteMaterialUploadFileDto> Files { get; set; }
    /// <summary>契约字段 <c>hasConflict</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hasConflict")]
    public required bool HasConflict { get; set; }
    /// <summary>契约字段 <c>isCurrent</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isCurrent")]
    public required bool IsCurrent { get; set; }
    /// <summary>契约字段 <c>limits</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("limits")]
    public required NoteMaterialUploadLimitsDto Limits { get; set; }
    /// <summary>契约字段 <c>noteId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("noteId")]
    public required string NoteId { get; set; }
    /// <summary>契约字段 <c>phase</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("phase")]
    public required string Phase { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialUploadDtoApiResponse</c>（response）。</summary>
public sealed class NoteMaterialUploadDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteMaterialUploadDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialUploadDtoIReadOnlyListApiResponse</c>（response）。</summary>
public sealed class NoteMaterialUploadDtoIReadOnlyListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<NoteMaterialUploadDto>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialUploadFileDto</c>（response）。</summary>
public sealed class NoteMaterialUploadFileDto
{
    /// <summary>契约字段 <c>index</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("index")]
    public required int Index { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required string Kind { get; set; }
    /// <summary>契约字段 <c>partCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("partCount")]
    public required int PartCount { get; set; }
    /// <summary>契约字段 <c>path</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    public required string Path { get; set; }
    /// <summary>契约字段 <c>sha256</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sha256")]
    public required string Sha256 { get; set; }
    /// <summary>契约字段 <c>sizeBytes</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sizeBytes")]
    public required long SizeBytes { get; set; }
    /// <summary>契约字段 <c>uploadedParts</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadedParts")]
    public required System.Collections.Generic.List<int> UploadedParts { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialUploadLimitsDto</c>（response）。</summary>
public sealed class NoteMaterialUploadLimitsDto
{
    /// <summary>契约字段 <c>maxFiles</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("maxFiles")]
    public required int MaxFiles { get; set; }
    /// <summary>契约字段 <c>maxMetadataCharacters</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("maxMetadataCharacters")]
    public required int MaxMetadataCharacters { get; set; }
    /// <summary>契约字段 <c>maxPendingVersions</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("maxPendingVersions")]
    public required int MaxPendingVersions { get; set; }
    /// <summary>契约字段 <c>maxUserReservedBytes</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("maxUserReservedBytes")]
    public required long MaxUserReservedBytes { get; set; }
    /// <summary>契约字段 <c>maxVersionBytes</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("maxVersionBytes")]
    public required long MaxVersionBytes { get; set; }
    /// <summary>契约字段 <c>maxVersionsPerUser</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("maxVersionsPerUser")]
    public required int MaxVersionsPerUser { get; set; }
    /// <summary>契约字段 <c>partSizeBytes</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("partSizeBytes")]
    public required int PartSizeBytes { get; set; }
    /// <summary>契约字段 <c>uploadExpiryHours</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uploadExpiryHours")]
    public required int UploadExpiryHours { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialVersionDto</c>（response）。</summary>
public sealed class NoteMaterialVersionDto
{
    /// <summary>契约字段 <c>inputJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("inputJson")]
    public required string InputJson { get; set; }
    /// <summary>契约字段 <c>manifestJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("manifestJson")]
    public required string ManifestJson { get; set; }
    /// <summary>契约字段 <c>upload</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("upload")]
    public required NoteMaterialUploadDto Upload { get; set; }
}

/// <summary>契约 <c>components.schemas.NoteMaterialVersionDtoApiResponse</c>（response）。</summary>
public sealed class NoteMaterialVersionDtoApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public NoteMaterialVersionDto? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.OutlookAdditionalInfoDto</c>（response）。</summary>
public sealed class OutlookAdditionalInfoDto
{
    /// <summary>契约字段 <c>groups</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("groups")]
    public required System.Collections.Generic.List<OutlookAdditionalInfoGroupDto> Groups { get; set; }
    /// <summary>契约字段 <c>hiddenFieldCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("hiddenFieldCount")]
    public required int HiddenFieldCount { get; set; }
}

/// <summary>契约 <c>components.schemas.OutlookAdditionalInfoGroupDto</c>（response）。</summary>
public sealed class OutlookAdditionalInfoGroupDto
{
    /// <summary>契约字段 <c>items</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public required System.Collections.Generic.List<OutlookAdditionalInfoItemDto> Items { get; set; }
    /// <summary>契约字段 <c>key</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public required string Key { get; set; }
    /// <summary>契约字段 <c>label</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public required string Label { get; set; }
}

/// <summary>契约 <c>components.schemas.OutlookAdditionalInfoItemDto</c>（response）。</summary>
public sealed class OutlookAdditionalInfoItemDto
{
    /// <summary>契约字段 <c>key</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("key")]
    public required string Key { get; set; }
    /// <summary>契约字段 <c>label</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public required string Label { get; set; }
    /// <summary>契约字段 <c>value</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public required string Value { get; set; }
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

/// <summary>契约 <c>components.schemas.PromoteNoteMaterialVersionRequest</c>（request）。</summary>
public sealed class PromoteNoteMaterialVersionRequest
{
    /// <summary>契约字段 <c>expectedCurrentVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedCurrentVersionId")]
    public string? ExpectedCurrentVersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.RefreshRequest</c>（request）。</summary>
public sealed class RefreshRequest
{
    /// <summary>契约字段 <c>refreshToken</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
    public required string RefreshToken { get; set; }
}

/// <summary>契约 <c>components.schemas.RegisterNoteAsrNodeRequest</c>（request）。</summary>
public sealed class RegisterNoteAsrNodeRequest
{
    /// <summary>契约字段 <c>capabilities</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("capabilities")]
    public required System.Collections.Generic.List<string> Capabilities { get; set; }
    /// <summary>契约字段 <c>enabled</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public required bool Enabled { get; set; }
    /// <summary>契约字段 <c>modelId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelId")]
    public required string ModelId { get; set; }
    /// <summary>契约字段 <c>modelRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelRevision")]
    public required string ModelRevision { get; set; }
    /// <summary>契约字段 <c>modelVerified</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("modelVerified")]
    public required bool ModelVerified { get; set; }
    /// <summary>契约字段 <c>nodeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("nodeId")]
    public required string NodeId { get; set; }
    /// <summary>契约字段 <c>nodeIdExternal</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("nodeIdExternal")]
    public required string NodeIdExternal { get; set; }
    /// <summary>契约字段 <c>resourceSnapshotJson</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("resourceSnapshotJson")]
    public string? ResourceSnapshotJson { get; set; }
    /// <summary>契约字段 <c>runtimeId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("runtimeId")]
    public string? RuntimeId { get; set; }
    /// <summary>契约字段 <c>runtimeVersion</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("runtimeVersion")]
    public string? RuntimeVersion { get; set; }
    /// <summary>契约字段 <c>sampledAt</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sampledAt")]
    public System.DateTimeOffset? SampledAt { get; set; }
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

/// <summary>契约 <c>components.schemas.RetryNoteDerivedTaskRequest</c>（request）。</summary>
public sealed class RetryNoteDerivedTaskRequest
{
    /// <summary>契约字段 <c>acknowledgePossibleDuplicateCharge</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("acknowledgePossibleDuplicateCharge")]
    public required bool AcknowledgePossibleDuplicateCharge { get; set; }
    /// <summary>契约字段 <c>consent</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("consent")]
    public required NoteDerivedGenerationConsent Consent { get; set; }
    /// <summary>契约字段 <c>expectedRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedRevision")]
    public required string ExpectedRevision { get; set; }
    /// <summary>契约字段 <c>expectedUserId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedUserId")]
    public required string ExpectedUserId { get; set; }
    /// <summary>契约字段 <c>retryId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("retryId")]
    public required string RetryId { get; set; }
    /// <summary>契约字段 <c>stepKeys</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("stepKeys")]
    public required System.Collections.Generic.List<string> StepKeys { get; set; }
}

/// <summary>契约 <c>components.schemas.SaveNoteDerivedManualRevisionRequest</c>（request）。</summary>
public sealed class SaveNoteDerivedManualRevisionRequest
{
    /// <summary>契约字段 <c>contentMarkdown</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("contentMarkdown")]
    public required string ContentMarkdown { get; set; }
    /// <summary>契约字段 <c>expectedHeadRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedHeadRevision")]
    public required string ExpectedHeadRevision { get; set; }
    /// <summary>契约字段 <c>expectedHeadVersionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedHeadVersionId")]
    public string? ExpectedHeadVersionId { get; set; }
    /// <summary>契约字段 <c>revisionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("revisionId")]
    public required string RevisionId { get; set; }
}

/// <summary>契约 <c>components.schemas.SelectNoteDerivedVersionRequest</c>（request）。</summary>
public sealed class SelectNoteDerivedVersionRequest
{
    /// <summary>契约字段 <c>expectedHeadRevision</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedHeadRevision")]
    public required string ExpectedHeadRevision { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
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

/// <summary>契约 <c>components.schemas.StartNoteAsrRequest</c>（request）。</summary>
public sealed class StartNoteAsrRequest
{
    /// <summary>契约字段 <c>consent</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("consent")]
    public AiAsrDestinationConsent? Consent { get; set; }
    /// <summary>契约字段 <c>expectedUserId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedUserId")]
    public required string ExpectedUserId { get; set; }
    /// <summary>契约字段 <c>language</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("language")]
    public string? Language { get; set; }
    /// <summary>契约字段 <c>parentTaskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("parentTaskId")]
    public string? ParentTaskId { get; set; }
    /// <summary>契约字段 <c>reprocessReason</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("reprocessReason")]
    public string? ReprocessReason { get; set; }
    /// <summary>契约字段 <c>route</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("route")]
    public NoteAsrRouteConsent? Route { get; set; }
    /// <summary>契约字段 <c>taskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
}

/// <summary>契约 <c>components.schemas.StartNoteDerivedTaskRequest</c>（request）。</summary>
public sealed class StartNoteDerivedTaskRequest
{
    /// <summary>契约字段 <c>asrTaskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("asrTaskId")]
    public string? AsrTaskId { get; set; }
    /// <summary>契约字段 <c>consent</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("consent")]
    public required NoteDerivedGenerationConsent Consent { get; set; }
    /// <summary>契约字段 <c>expectedUserId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("expectedUserId")]
    public required string ExpectedUserId { get; set; }
    /// <summary>契约字段 <c>taskId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskId")]
    public required string TaskId { get; set; }
    /// <summary>契约字段 <c>versionId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("versionId")]
    public required string VersionId { get; set; }
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

/// <summary>契约 <c>components.schemas.TaskBookDto</c>（response）。</summary>
public sealed class TaskBookDto
{
    /// <summary>契约字段 <c>domainProjectId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("domainProjectId")]
    public string? DomainProjectId { get; set; }
    /// <summary>契约字段 <c>id</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; set; }
    /// <summary>契约字段 <c>kind</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public required string Kind { get; set; }
    /// <summary>契约字段 <c>name</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public required string Name { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
    /// <summary>契约字段 <c>taskCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskCount")]
    public int TaskCount { get; set; }
}

/// <summary>契约 <c>components.schemas.TaskBookDtoIReadOnlyListApiResponse</c>（response）。</summary>
public sealed class TaskBookDtoIReadOnlyListApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public System.Collections.Generic.List<TaskBookDto>? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.TaskResponse</c>（response）。</summary>
public sealed class TaskResponse
{
    /// <summary>契约字段 <c>calendarId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("calendarId")]
    public string? CalendarId { get; set; }
    /// <summary>契约字段 <c>description</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    /// <summary>契约字段 <c>dtStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtStart")]
    public System.DateTimeOffset? DtStart { get; set; }
    /// <summary>契约字段 <c>due</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("due")]
    public System.DateTimeOffset? Due { get; set; }
    /// <summary>契约字段 <c>estimatedDuration</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("estimatedDuration")]
    public string? EstimatedDuration { get; set; }
    /// <summary>契约字段 <c>id</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public required string Id { get; set; }
    /// <summary>契约字段 <c>isInbox</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("isInbox")]
    public required bool IsInbox { get; set; }
    /// <summary>契约字段 <c>minimumSegment</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("minimumSegment")]
    public string? MinimumSegment { get; set; }
    /// <summary>契约字段 <c>percentComplete</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public int PercentComplete { get; set; }
    /// <summary>契约字段 <c>plannedEnd</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("plannedEnd")]
    public System.DateTimeOffset? PlannedEnd { get; set; }
    /// <summary>契约字段 <c>priority</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("priority")]
    public required int Priority { get; set; }
    /// <summary>契约字段 <c>sortOrder</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("sortOrder")]
    public required int SortOrder { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public required string Status { get; set; }
    /// <summary>契约字段 <c>subTasks</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("subTasks")]
    public required System.Collections.Generic.List<TaskResponse> SubTasks { get; set; }
    /// <summary>契约字段 <c>taskBookId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskBookId")]
    public string? TaskBookId { get; set; }
    /// <summary>契约字段 <c>title</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public required string Title { get; set; }
    /// <summary>契约字段 <c>uid</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("uid")]
    public required string Uid { get; set; }
}

/// <summary>契约 <c>components.schemas.TaskResponseApiResponse</c>（response）。</summary>
public sealed class TaskResponseApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public TaskResponse? Data { get; set; }
    /// <summary>契约字段 <c>message</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public required string Message { get; set; }
    /// <summary>契约字段 <c>timestamp</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public required System.DateTimeOffset Timestamp { get; set; }
}

/// <summary>契约 <c>components.schemas.TaskResponsePagedResult</c>（response）。</summary>
public sealed class TaskResponsePagedResult
{
    /// <summary>契约字段 <c>items</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public required System.Collections.Generic.List<TaskResponse> Items { get; set; }
    /// <summary>契约字段 <c>page</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("page")]
    public required int Page { get; set; }
    /// <summary>契约字段 <c>pageSize</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("pageSize")]
    public required int PageSize { get; set; }
    /// <summary>契约字段 <c>totalCount</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalCount")]
    public required int TotalCount { get; set; }
    /// <summary>契约字段 <c>totalPages</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("totalPages")]
    public required int TotalPages { get; set; }
}

/// <summary>契约 <c>components.schemas.TaskResponsePagedResultApiResponse</c>（response）。</summary>
public sealed class TaskResponsePagedResultApiResponse
{
    /// <summary>契约字段 <c>code</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public required int Code { get; set; }
    /// <summary>契约字段 <c>data</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public TaskResponsePagedResult? Data { get; set; }
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

/// <summary>契约 <c>components.schemas.UpdateTaskRequest</c>（request）。</summary>
public sealed class UpdateTaskRequest
{
    /// <summary>契约字段 <c>calendarId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("calendarId")]
    public string? CalendarId { get; set; }
    /// <summary>契约字段 <c>description</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("description")]
    public string? Description { get; set; }
    /// <summary>契约字段 <c>dtStart</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("dtStart")]
    public System.DateTimeOffset? DtStart { get; set; }
    /// <summary>契约字段 <c>due</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("due")]
    public System.DateTimeOffset? Due { get; set; }
    /// <summary>契约字段 <c>estimatedDuration</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("estimatedDuration")]
    public string? EstimatedDuration { get; set; }
    /// <summary>契约字段 <c>minimumSegment</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("minimumSegment")]
    public string? MinimumSegment { get; set; }
    /// <summary>契约字段 <c>percentComplete</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public int PercentComplete { get; set; }
    /// <summary>契约字段 <c>plannedEnd</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("plannedEnd")]
    public System.DateTimeOffset? PlannedEnd { get; set; }
    /// <summary>契约字段 <c>priority</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("priority")]
    public int Priority { get; set; }
    /// <summary>契约字段 <c>status</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public UpdateTaskRequestStatus? Status { get; set; }
    /// <summary>契约字段 <c>taskBookId</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("taskBookId")]
    public string? TaskBookId { get; set; }
    /// <summary>契约字段 <c>title</c>。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? Title { get; set; }
}

/// <summary>契约 <c>components.schemas.UpdateTaskRequestStatus</c>（request）。</summary>
/// <summary>
/// 契约枚举 <c>UpdateTaskRequestStatus</c>：wire 值由生成 converter 精确往返。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(UpdateTaskRequestStatusWireConverter))]
public enum UpdateTaskRequestStatus
{
    NEEDS_ACTION,
    IN_PROCESS,
    COMPLETED,
    CANCELLED,
}

public sealed class UpdateTaskRequestStatusWireConverter : System.Text.Json.Serialization.JsonConverter<UpdateTaskRequestStatus>
{
    public override UpdateTaskRequestStatus Read(ref System.Text.Json.Utf8JsonReader reader, System.Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
            throw new System.Text.Json.JsonException("UpdateTaskRequestStatus must be a JSON string");
        return reader.GetString() switch
        {
            "NEEDS-ACTION" => UpdateTaskRequestStatus.NEEDS_ACTION,
            "IN-PROCESS" => UpdateTaskRequestStatus.IN_PROCESS,
            "COMPLETED" => UpdateTaskRequestStatus.COMPLETED,
            "CANCELLED" => UpdateTaskRequestStatus.CANCELLED,
            _ => throw new System.Text.Json.JsonException("Unknown UpdateTaskRequestStatus wire value"),
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, UpdateTaskRequestStatus value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            UpdateTaskRequestStatus.NEEDS_ACTION => "NEEDS-ACTION",
            UpdateTaskRequestStatus.IN_PROCESS => "IN-PROCESS",
            UpdateTaskRequestStatus.COMPLETED => "COMPLETED",
            UpdateTaskRequestStatus.CANCELLED => "CANCELLED",
            _ => throw new System.Text.Json.JsonException("Unknown UpdateTaskRequestStatus value"),
        });
    }
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
