using System.Net;
using System.Text;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public sealed class RemoteComputerHistoryServiceTests
{
    [Fact]
    public async Task QueryUsesOwnerDeviceInventoryAndTypedPagedDetail()
    {
        var handler = new HistoryHandler();
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        var result = await service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 3),
            deviceId: "windows-a",
            recordType: "web-page",
            page: 2);

        Assert.Equal(2, result.Page);
        Assert.Equal(201, result.TotalCount);
        Assert.Equal(2, result.TotalPages);
        Assert.False(result.IsTruncated);
        Assert.Single(result.Records);
        Assert.Equal("web-page", result.Records[0].RecordType);
        Assert.Equal("https://example.test/path", result.Records[0].Url);
        Assert.Equal("Bearer synthetic-user-a-token", handler.Authorization);
        Assert.Contains("/api/v1/pc/detail?", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("dateFrom=2026-10-01", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("dateTo=2026-10-03", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("deviceId=windows-a", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("eventType=web-page", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("view=raw", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("page=2", handler.LastPath, StringComparison.Ordinal);
        Assert.Contains("pageSize=200", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRejectsUnknownDeviceBeforeRequestingHistory()
    {
        var handler = new HistoryHandler();
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            deviceId: "user-b-device"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task QueryRejectsCrossAccountScopeChangeBetweenInventoryAndRecords()
    {
        var handler = new HistoryHandler(onFirstRequest: api => api.SetAccessToken("synthetic-user-b-token"));
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1)));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task QueryReportsServerSideTruncationAndRejectsUnsafePagination()
    {
        var handler = new HistoryHandler(totalPages: RemoteComputerHistoryService.MaximumPages + 1);
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);
        var result = await service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1));

        Assert.True(result.IsTruncated);
        Assert.Equal(RemoteComputerHistoryService.MaximumPages + 1, result.TotalPages);
        Assert.Equal(5_000, RemoteComputerHistoryService.MaximumRows);
        var requestCount = handler.RequestCount;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            page: RemoteComputerHistoryService.MaximumPages + 1));
        Assert.Equal(requestCount, handler.RequestCount);
    }

    [Theory]
    [InlineData(201, 200, 2, 1, 201)]
    [InlineData(201, 100, 3, 1, 101)]
    [InlineData(201, 200, 3, 1, 200)]
    [InlineData(201, 200, 2, 2, 2)]
    [InlineData(0, 200, 0, 1, 1)]
    [InlineData(201, 200, 2, 3, 0)]
    public async Task QueryRejectsItemsThatExceedOrContradictPageReceipt(
        int totalCount,
        int pageSize,
        int totalPages,
        int page,
        int actualItemCount)
    {
        var handler = new HistoryHandler(
            totalPages: totalPages,
            totalCount: totalCount,
            responsePageSize: pageSize,
            actualItemCount: actualItemCount);
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            page: page));

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task QueryAcceptsThirtyOneInclusiveDays()
    {
        var handler = new HistoryHandler();
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        var result = await service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31));

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(RemoteComputerHistoryService.MaximumDateRangeDays, 31);
        Assert.Equal(new DateOnly(2026, 10, 1), result.From);
        Assert.Equal(new DateOnly(2026, 10, 31), result.To);
    }

    [Fact]
    public async Task QueryRejectsDateRangeOverThirtyOneDaysBeforeAnyRequest()
    {
        var handler = new HistoryHandler();
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 11, 1)));

        Assert.Contains("最多查询 31 天", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task QueryRejectsRecordOutsideSelectedOwnerDevice()
    {
        var handler = new HistoryHandler(recordDeviceId: "foreign-device");
        var api = new ApiClient(() => handler);
        handler.SetApi(api);
        api.SetAccessToken("synthetic-user-a-token");
        var service = new RemoteComputerHistoryService(api);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.QueryAsync(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 1),
            deviceId: "windows-a",
            page: 2));
    }

    private sealed class HistoryHandler(
        int totalPages = 2,
        string recordDeviceId = "windows-a",
        Action<ApiClient>? onFirstRequest = null,
        int? totalCount = null,
        int? responsePageSize = null,
        int? actualItemCount = null) : HttpMessageHandler
    {
        private int _requestCount;
        private ApiClient? _api;

        public int RequestCount => _requestCount;
        public string LastPath { get; private set; } = string.Empty;
        public string? Authorization { get; private set; }

        public void SetApi(ApiClient api) => _api = api;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestCount++;
            LastPath = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString();
            if (_requestCount == 1)
                onFirstRequest?.Invoke(_api!);

            if (request.RequestUri.AbsolutePath == "/api/v1/endpoints")
            {
                return Task.FromResult(Raw(
                    """
                    {
                      "code": 0,
                      "data": [
                        { "deviceId": "windows-a", "platform": "windows", "uploadStatus": "healthy", "collectionCacheCount": 0, "onlineOnlyBlockedCount": 0 },
                        { "deviceId": "browser-a", "platform": "browser", "uploadStatus": "healthy", "collectionCacheCount": 0, "onlineOnlyBlockedCount": 0 }
                      ],
                      "message": "ok",
                      "timestamp": "2026-11-02T00:00:00Z"
                    }
                    """));
            }

            if (request.RequestUri.AbsolutePath == "/api/v1/pc/detail")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var page = int.Parse(query["page"]!);
                var effectivePageSize = responsePageSize ?? RemoteComputerHistoryService.PageSize;
                var effectiveTotalCount = totalCount
                    ?? (totalPages - 1) * effectivePageSize + 1;
                var defaultItemCount = (int)Math.Clamp(
                    (long)effectiveTotalCount - ((long)page - 1) * effectivePageSize,
                    0,
                    effectivePageSize);
                var itemCount = actualItemCount ?? defaultItemCount;
                var item = $$"""
                    {
                        "recordType": "web-page",
                        "start": "2026-10-01T12:00:00Z",
                        "end": "2026-10-01T12:01:00Z",
                        "deviceId": "{{recordDeviceId}}",
                        "url": "https://example.test/path"
                    }
                    """;
                var items = $"[{string.Join(",", Enumerable.Repeat(item, itemCount))}]";
                return Task.FromResult(Raw(
                    $$"""
                    {
                      "code": 0,
                      "data": {
                        "items": {{items}},
                        "page": {{page}},
                        "pageSize": {{effectivePageSize}},
                        "totalCount": {{effectiveTotalCount}},
                        "totalPages": {{totalPages}}
                      },
                      "message": "ok",
                      "timestamp": "2026-11-02T00:00:00Z"
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Raw(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }
}
