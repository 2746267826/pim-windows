using System.Globalization;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

public sealed record RemoteComputerHistoryResult(
    IReadOnlyList<EndpointStatusDto> Devices,
    IReadOnlyList<PcDetailRecord> Records,
    DateOnly From,
    DateOnly To,
    int Page,
    int TotalCount,
    int TotalPages,
    bool IsTruncated);

/// <summary>Owner-scoped, bounded read access to uploaded PC activity history.</summary>
public sealed class RemoteComputerHistoryService(ApiClient api)
{
    public const int PageSize = 200;
    public const int MaximumPages = 25;
    public const int MaximumRows = PageSize * MaximumPages;
    public const int MaximumDateRangeDays = 31;

    private readonly ApiClient _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<IReadOnlyList<EndpointStatusDto>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = _api.BeginOperationScope();
        var response = await _api.GetEndpointStatusesAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回设备列表。");
        _api.EnsureOperationScopeCurrent();
        return RequireData(response.Code, response.Message, response.Data);
    }

    public async Task<RemoteComputerHistoryResult> QueryAsync(
        DateOnly from,
        DateOnly to,
        string? deviceId = null,
        string? recordType = null,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
            throw new ArgumentException("结束日期不能早于开始日期。", nameof(to));
        if (to.DayNumber - from.DayNumber + 1 > MaximumDateRangeDays)
            throw new ArgumentOutOfRangeException(
                nameof(to),
                $"服务器历史一次最多查询 {MaximumDateRangeDays} 天。请缩小日期范围后重试。");
        if (page is < 1 or > MaximumPages)
            throw new ArgumentOutOfRangeException(nameof(page), $"安全分页上限为第 {MaximumPages} 页。");
        if (recordType is not (null or "window" or "web" or "web-page"))
            throw new ArgumentException("记录类型必须是 window、web 或 web-page。", nameof(recordType));

        using var scope = _api.BeginOperationScope();
        var deviceResponse = await _api.GetEndpointStatusesAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回设备列表。");
        _api.EnsureOperationScopeCurrent();
        var devices = RequireData(deviceResponse.Code, deviceResponse.Message, deviceResponse.Data);
        var deviceIds = devices
            .Select(device => device.DeviceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(deviceId) && !deviceIds.Contains(deviceId))
            throw new ArgumentException("所选设备不属于当前账号或已不在设备清单中。", nameof(deviceId));

        var response = await _api.GetPcDetailAsync(
            dateFrom: from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            dateTo: to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            deviceId: deviceId,
            eventType: recordType,
            sortDir: "desc",
            view: "raw",
            page: page,
            pageSize: PageSize,
            ct: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回电脑记录。");
        _api.EnsureOperationScopeCurrent();
        var result = RequireData(response.Code, response.Message, response.Data);
        if (result.Items is null)
            throw new InvalidDataException("服务器分页记录无效。");

        if (result.Page != page
            || result.PageSize is < 1 or > PageSize
            || result.TotalCount < 0
            || result.TotalPages < 0
            || result.TotalPages != ((long)result.TotalCount + result.PageSize - 1) / result.PageSize
            || page > Math.Max(1, result.TotalPages))
        {
            throw new InvalidDataException("服务器分页回执无效。");
        }

        var maximumItemCountForPage = (int)Math.Clamp(
            (long)result.TotalCount - ((long)page - 1) * result.PageSize,
            0,
            result.PageSize);
        if (result.Items.Count > PageSize
            || result.Items.Count > result.PageSize
            || result.Items.Count > maximumItemCountForPage)
        {
            throw new InvalidDataException("服务器分页记录数与分页回执不一致。");
        }

        foreach (var record in result.Items)
        {
            if (!string.IsNullOrWhiteSpace(deviceId)
                && !string.Equals(record.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("服务器返回了所选设备之外的记录，已停止显示。");
            }
        }

        _api.EnsureOperationScopeCurrent();
        return new RemoteComputerHistoryResult(
            devices,
            result.Items,
            from,
            to,
            page,
            result.TotalCount,
            result.TotalPages,
            result.TotalPages > MaximumPages);
    }

    private static T RequireData<T>(int code, string message, T? data)
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
