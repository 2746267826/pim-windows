using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

public sealed record DeviceSnapshot(
    EndpointStatusDto Status,
    bool IsHeartbeatFresh,
    string PresenceLabel);

/// <summary>Typed, read-only device inventory and collection-quality access.</summary>
public sealed class DeviceCenterService(ApiClient api)
{
    private readonly ApiClient _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<IReadOnlyList<DeviceSnapshot>> GetDevicesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var response = await _api.GetEndpointStatusesAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回设备列表。");
        var devices = RequireResponse(response.Code, response.Message, response.Data);
        return devices
            .Select(device =>
            {
                var fresh = device.LastHeartbeatAt is { } heartbeat
                    && now - heartbeat <= TimeSpan.FromMinutes(5);
                return new DeviceSnapshot(
                    device,
                    fresh,
                    fresh ? "在线" : device.LastHeartbeatAt is null ? "从未心跳" : "心跳超时");
            })
            .OrderByDescending(device => device.IsHeartbeatFresh)
            .ThenBy(device => device.Status.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<EndpointCollectionQualityDto> GetCollectionQualityAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("设备标识不能为空。", nameof(deviceId));

        var response = await _api.GetEndpointCollectionQualityAsync(deviceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回采集质量。");
        var quality = RequireResponse(response.Code, response.Message, response.Data);
        if (!string.Equals(quality.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("服务器返回的采集质量不属于所选设备。");
        }

        return quality;
    }

    private static T RequireResponse<T>(int code, string message, T? data)
        where T : class
    {
        if (code != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "服务器操作失败。" : message);
        }

        return data ?? throw new InvalidDataException("服务器响应没有数据。");
    }
}
