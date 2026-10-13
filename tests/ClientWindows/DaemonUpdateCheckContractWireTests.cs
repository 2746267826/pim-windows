using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.App;
using Pim.Client.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / AC-COM-02（F-03）：补齐
/// <c>get /api/client/shell/latest</c> 这个缺口的**真实**契约用例 ——
/// 生产入口 <see cref="TrayIcon.FetchLatestAsync"/>（托盘「检查更新」真正调用的那一段）
/// → 记录型 handler 捕获线上 method/路径 → 对照 <c>contract/openapi.json</c>。
///
/// 该 operation 没有 requestBody，核对的维度是 method + 路径（与 <c>/health</c> 同理）。
/// 本文件在 <c>Pim.Client.App.Tests</c>（net8.0-windows）里编译，由 CI 的
/// <c>test-client-app</c> job 在 Windows runner 上真实执行；
/// <c>TrayIcon.FetchLatestAsync</c> 是 internal（程序集已对测试开放 InternalsVisibleTo）。
/// </summary>
public sealed class DaemonUpdateCheckContractWireTests
{
    [Fact]
    public async Task DaemonUpdateCheck_ProductionEntry_RequestsTheContractOperation()
    {
        var handler = new RecordingHandler("""{"windowsVersion":"2026.9.2","windowsUrl":"https://example.invalid/pim.zip","checkedAt":"2026-10-11T00:00:00Z"}""");
        using var http = new HttpClient(handler);

        var latest = await TrayIcon.FetchLatestAsync(http, "https://pim.example.com");

        Assert.Equal("2026.9.2", latest?.windowsVersion);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/client/shell/latest", request.Path);

        // 生产实际发出的 operation 必须真的落在契约上。
        ContractOperation.ForOperation("/api/client/shell/latest", "get");

        // 行为保留：更新比较用的是响应里的版本号，而不是“请求发出去了”就算过。
        // UpdateChecker 只比较版本号最后一段的数字（见 src/.../UpdateChecker.cs），所以这里是 2026.9.1 → 2026.9.2。
        Assert.True(UpdateChecker.IsNewer("2026.9.1", latest!.windowsVersion!), "远端最后一段更大时必须判定为有新版");
        Assert.False(UpdateChecker.IsNewer(latest!.windowsVersion!, "2026.9.2"), "同一个版本不得判定为有新版");
    }

    [Fact]
    public async Task DaemonUpdateCheck_TrailingSlashInConfiguredServerUrl_StillHitsTheContractPath()
    {
        // 托盘从配置/默认值取服务地址，可能带尾斜杠；归一化后必须仍打到契约路径。
        foreach (var input in new[] { "http://127.0.0.1:5858", "http://127.0.0.1:5858/" })
        {
            var handler = new RecordingHandler("""{"windowsVersion":"2026.9.2"}""");
            using var http = new HttpClient(handler);

            await TrayIcon.FetchLatestAsync(http, input);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/client/shell/latest", request.Path);
            ContractOperation.ForOperation("/api/client/shell/latest", "get");
        }
    }

    [Fact]
    public void ShellLatestOperation_ExistsInTheContractAsGetOnly()
    {
        var operation = ContractOperation.ForOperation("/api/client/shell/latest", "get");
        Assert.Equal("/api/client/shell/latest", operation.Path);
        Assert.Equal("get", operation.Method);
        Assert.False(operation.HasRequestBody);

        Assert.ThrowsAny<Exception>(() => ContractOperation.ForOperation("/api/client/shell/latests", "get"));
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForOperation("/api/client/shell/latest", "post"));
    }

    [Fact]
    public async Task DaemonUpdateCheck_ErrorPayload_SurfacesTheServerError_NotAFalseSuccess()
    {
        // 行为保留（反面）：server 回 error 时生产代码必须把 error 交回调用方，
        // 不能因为“请求成功”就当作已是最新版本。
        var handler = new RecordingHandler("""{"error":"maintenance"}""");
        using var http = new HttpClient(handler);

        var latest = await TrayIcon.FetchLatestAsync(http, "https://pim.example.com");

        Assert.Equal("maintenance", latest?.error);
        ContractOperation.ForOperation("/api/client/shell/latest", "get");
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly List<CapturedRequest> _requests = new();

        public RecordingHandler(string json) => _json = json;

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(new CapturedRequest(request.Method, request.RequestUri!.AbsolutePath));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
