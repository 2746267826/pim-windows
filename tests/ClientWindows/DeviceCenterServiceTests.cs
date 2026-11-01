using System.Net;
using System.Text;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public sealed class DeviceCenterServiceTests
{
    [Fact]
    public async Task DeviceInventoryAndQualityUseTypedResponsesAndDeviceIdentity()
    {
        var handler = new DeviceHandler();
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-device-token");
        var service = new DeviceCenterService(api);
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        var devices = await service.GetDevicesAsync(now);

        Assert.Equal(2, devices.Count);
        Assert.True(devices[0].IsHeartbeatFresh);
        Assert.Equal("在线", devices[0].PresenceLabel);
        Assert.False(devices[1].IsHeartbeatFresh);
        Assert.Equal("心跳超时", devices[1].PresenceLabel);

        var quality = await service.GetCollectionQualityAsync("device-windows");
        Assert.Equal("device-windows", quality.DeviceId);
        Assert.Equal(2, quality.IssueCount);
        Assert.Contains("device-windows/collection-quality", handler.LastPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QualityResponseForAnotherDeviceIsRejected()
    {
        var api = new ApiClient(() => new DeviceHandler(qualityDeviceId: "other-device"));
        api.SetAccessToken("synthetic-device-token");
        var service = new DeviceCenterService(api);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetCollectionQualityAsync("device-windows"));
    }

    private sealed class DeviceHandler(string? qualityDeviceId = null) : HttpMessageHandler
    {
        public string LastPath { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get
                && request.RequestUri.AbsolutePath == "/api/v1/endpoints")
            {
                return Task.FromResult(Raw(
                    """
                    {
                      "code": 0,
                      "data": [
                        {
                          "deviceId": "device-windows",
                          "platform": "windows",
                          "lastHeartbeatAt": "2026-11-01T11:59:00Z",
                          "uploadStatus": "healthy",
                          "collectionCacheCount": 12,
                          "onlineOnlyBlockedCount": 0,
                          "appVersion": "2.0.0"
                        },
                        {
                          "deviceId": "device-android",
                          "platform": "android",
                          "lastHeartbeatAt": "2026-11-01T10:00:00Z",
                          "uploadStatus": "delayed",
                          "collectionCacheCount": 4,
                          "onlineOnlyBlockedCount": 3
                        }
                      ],
                      "message": "ok",
                      "timestamp": "2026-11-01T12:00:00Z"
                    }
                    """));
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri.AbsolutePath.EndsWith(
                    "/collection-quality",
                    StringComparison.Ordinal))
            {
                var deviceId = qualityDeviceId ?? "device-windows";
                return Task.FromResult(Raw(
                    $$"""
                    {
                      "code": 0,
                      "data": {
                        "deviceId": "{{deviceId}}",
                        "platform": "windows",
                        "checkedAt": "2026-11-01T11:59:30Z",
                        "uploadStatus": "healthy",
                        "issueCount": 2
                      },
                      "message": "ok",
                      "timestamp": "2026-11-01T12:00:00Z"
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
