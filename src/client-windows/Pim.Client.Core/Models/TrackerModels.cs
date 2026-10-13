using System.Text.Json.Serialization;
using Pim.Client.Core.Utils;

namespace Pim.Client.Core.Models;

// 说明：本文件只保留**客户端内部**模型（本机采集状态、浏览器桥协议、本地服务）。
// 与 pim-api 交互的接口 DTO 已由契约生成，位于 Pim.Client.Core.Contract
// （TrackerEventDto / TrackerEventsUploadRequest / TrackerHealthRequest / SiteEventUploadDto /
//  SiteEventsUploadRequest，见 scripts/contract/generate-csharp.py）。

public sealed class TrackerConfig
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 10;
    public int IdleThresholdSeconds { get; set; } = 300;
    public int GapThresholdSeconds { get; set; } = 60;
    public int BrowserBridgePort { get; set; } = 15601;
    public int UploadBatchSize { get; set; } = 500;
    public int UploadIntervalSeconds { get; set; } = 30;
    public int HealthReportIntervalSeconds { get; set; } = 300;
    public int LogRetentionDays { get; set; } = 30;
    public List<string> ExcludedApps { get; set; } = new();
}

public sealed class TrackerWindowInfo
{
    public IntPtr Hwnd { get; set; }
    public uint ProcessId { get; set; }
    public string ExePath { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string? CommandLine { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUwpHost => string.Equals(AppName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(AppName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>浏览器扩展 → 守护程序的桥协议消息（本机 WebSocket / HTTP，不是 pim-api 契约）。</summary>
public sealed class BrowserHeartbeat
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("audible")] public bool Audible { get; set; }
    [JsonPropertyName("incognito")] public bool Incognito { get; set; }
    [JsonPropertyName("tabCount")] public int TabCount { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    [JsonPropertyName("browser")] public string Browser { get; set; } = "other";
    [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = string.Empty;

    public DateTimeOffset ParsedTimestamp => DateTimeOffset.TryParse(Timestamp, out var ts) ? ts : DateTimeOffset.UtcNow;
    public string? Domain
    {
        get
        {
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return uri.Host;
            return null;
        }
    }
    public string? PagePath
    {
        get
        {
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return uri.IsFile ? uri.LocalPath : uri.PathAndQuery;
            return null;
        }
    }
}

public sealed class TrackerSession
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = Environment.MachineName;
    public string ExePath { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? WindowTitle { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double? DurationSecs { get; set; }
    public bool IsIdle { get; set; }
    public bool IsMediaActive { get; set; }
    public string Date => BusinessDayUtils.GetBusinessDateString(StartedAt);
    public List<TrackerPageVisit> PageVisits { get; set; } = new();
}

public sealed class TrackerPageVisit
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public string? WindowTitle { get; set; }
    public string? Url { get; set; }
    public string? Domain { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double? DurationSecs { get; set; }
}

public sealed class BrowserConnection
{
    public string InstanceId { get; set; } = string.Empty;
    public string BrowserType { get; set; } = "other";
    public string DisplayName { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public DateTimeOffset LastHeartbeat { get; set; }
    public string? LastUrl { get; set; }
    public string? LastTitle { get; set; }
    public bool? LastAudible { get; set; }
    public int? LastTabCount { get; set; }
    public bool? LastIncognito { get; set; }
    public long HeartbeatCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public readonly object SyncRoot = new();
}

/// <summary>
/// 浏览器扩展 → 守护程序的桥协议报文（本机 HTTP，不是 pim-api 契约）：
/// 扩展把站点事件批量投递到本机 bridge，字段由扩展侧决定。
/// </summary>
public sealed class SiteEventsRequest
{
    [JsonPropertyName("events")] public List<SiteEventDto> Events { get; set; } = new();
}

/// <summary>
/// 站点级浏览数据（Time Tracker fork → 守护进程）的内部传输类型，kind 取值：
/// focus(startMs/endMs) | tick(startMs/durationMs) | visit | run/media(date/durationMs)。
///
/// 它**不是** pim-api 契约里的类型：上线前由 NativeTrackerService 映射为契约生成的
/// <c>Pim.Client.Core.Contract.SiteEventUploadDto</c>；本类型多出来的 <c>at</c> 只在本机链路里流转。
/// </summary>
public sealed class SiteEventDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("startMs")] public long? StartMs { get; set; }
    [JsonPropertyName("endMs")] public long? EndMs { get; set; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("at")] public long? At { get; set; }
}
