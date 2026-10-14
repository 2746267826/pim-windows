using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

public sealed class PlannedOfflineReporter
{
    private readonly ApiClient _api;

    public PlannedOfflineReporter(ApiClient api)
    {
        _api = api;
    }

    public async Task ReportAsync(PlannedOfflineRequest request, CancellationToken ct = default)
    {
        await _api.ReportPlannedOfflineAsync(request, ct);
    }

    public static PlannedOfflineRequest BuildRequest(string deviceId, string reason, DateTimeOffset occurredAt)
    {
        return new PlannedOfflineRequest
        {
            DeviceId = deviceId,
            DaemonKind = "windows",
            Reason = reason,
            OccurredAt = occurredAt,
        };
    }
}
