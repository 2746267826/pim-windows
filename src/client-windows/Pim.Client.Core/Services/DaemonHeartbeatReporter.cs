using System.Reflection;
using System.Text.Json;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

public sealed class DaemonHeartbeatReporter
{
    private readonly ApiClient _api;

    public DaemonHeartbeatReporter(ApiClient api)
    {
        _api = api;
    }

    public async Task ReportAsync(DaemonHeartbeatRequest heartbeat, CancellationToken ct = default)
    {
        await _api.ReportDaemonHeartbeatAsync(heartbeat, ct);
    }

    /// <param name="activityWatchState">
    /// 契约枚举 <see cref="DaemonSourceState"/>：取值只能来自契约，写错编译期就不过。
    /// </param>
    /// <param name="keyStatsState">同上。</param>
    public static DaemonHeartbeatRequest BuildHeartbeat(
        string deviceId,
        string version,
        string serverUrl,
        DateTimeOffset? lastSuccessfulUploadAt,
        DateTimeOffset? lastAttemptedUploadAt,
        string? lastError,
        int? uploadQueueCount = null,
        DaemonSourceState activityWatchState = DaemonSourceState.Unknown,
        DaemonSourceState keyStatsState = DaemonSourceState.Unknown,
        object? statusDetails = null)
    {
        var normalizedServerUrl = ApiClient.NormalizeServerUrl(
            string.IsNullOrWhiteSpace(serverUrl)
                ? ClientDefaults.DefaultServerUrl
                : serverUrl);

        var statusPayload = new Dictionary<string, object?>
        {
            ["machine"] = Environment.MachineName,
            ["process"] = "pim-windows-daemon"
        };

        if (statusDetails is not null)
        {
            foreach (var prop in statusDetails.GetType().GetProperties())
            {
                statusPayload[prop.Name] = prop.GetValue(statusDetails);
            }
        }

        return new DaemonHeartbeatRequest
        {
            DeviceId = deviceId,
            DaemonKind = "windows",
            Version = string.IsNullOrWhiteSpace(version)
                ? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
                : version,
            ServerUrl = normalizedServerUrl,
            LastSuccessfulUploadAt = lastSuccessfulUploadAt,
            LastAttemptedUploadAt = lastAttemptedUploadAt,
            LastError = lastError,
            UploadQueueCount = uploadQueueCount,
            ActivityWatchState = activityWatchState,
            KeyStatsState = keyStatsState,
            CollectionPaused = false,
            StatusJson = JsonSerializer.Serialize(statusPayload),
        };
    }
}
