using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pim.Shell.App;
using Xunit;
using Xunit.Sdk;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / AC-COM-02（F-03）：补齐 <c>get /health</c> 这个缺口的
/// **真实**契约用例 —— 生产入口 <see cref="ServerHealthClient.CheckAsync"/> → 记录型 handler
/// 捕获线上 method/路径 → 对照 <c>contract/openapi.json</c>（该 operation 没有 requestBody，
/// 因此核对的是 method + 路径，而不是请求体字段）。
///
/// 本文件由 <c>src/client-shell-windows/Pim.Shell.Tests</c> 的 net8.0 目标以 Link 方式编译
/// （与 <c>ServerHealthClient.cs</c> 自身在同一程序集），CI 里由
/// <c>build-shell-windows.yml</c> 真实执行。
/// </summary>
public sealed class ShellHealthContractWireTests
{
    [Fact]
    public async Task ShellHealthCheck_ProductionEntry_RequestsTheContractOperation()
    {
        var handler = new RecordingHandler();
        var client = new ServerHealthClient(new HttpClient(handler));

        var result = await client.CheckAsync("pim.example.com");

        Assert.Equal(HealthCheckStatus.Healthy, result.Status);
        Assert.Equal("https://pim.example.com", result.NormalizedUrl);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/health", request.Path);

        // 生产实际发出的 operation 必须真的落在契约上（拼错路径/写错 method 就查不到）。
        ContractOperation.ForOperation("/health", "get");
    }

    [Fact]
    public async Task ShellHealthCheck_TrailingSlashAndExplicitPort_StillHitsTheContractPath()
    {
        // 生产入口自己归一化服务地址（ServerAddress.Normalize + TrimEnd('/')）：
        // 带尾斜杠、显式端口、以及裸主机名都必须落到契约里的 /health。
        foreach (var input in new[] { "https://pim.example.com:8443/", "pim.example.com", "http://127.0.0.1:5858/" })
        {
            var handler = new RecordingHandler();
            var client = new ServerHealthClient(new HttpClient(handler));

            Assert.Equal(HealthCheckStatus.Healthy, (await client.CheckAsync(input)).Status);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/health", request.Path);
            ContractOperation.ForOperation("/health", "get");
        }
    }

    [Fact]
    public void HealthOperation_ExistsInTheContractAsGetOnly()
    {
        var operation = ContractOperation.ForOperation("/health", "get");
        Assert.Equal("/health", operation.Path);
        Assert.Equal("get", operation.Method);

        // /health 没有请求体：ForPath（要求 requestBody）在这里必须失败，
        // 这正是 round5 需要 ForOperation 的原因 —— 判据不能因为“没有 body”而失效。
        Assert.False(operation.HasRequestBody);
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForPath("/health", "get"));

        // 反例：路径写错、method 写错都必须查不到。
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForOperation("/healthz", "get"));
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForOperation("/health", "post"));
    }

    [Fact]
    public async Task ShellHealthCheck_UnreachableServer_DoesNotProduceAPimContractRequest()
    {
        // 行为保留 + 反面：地址非法时生产代码**不发**请求（契约检查不能因为“反正发了请求”而假装覆盖）。
        var handler = new RecordingHandler();
        var client = new ServerHealthClient(new HttpClient(handler));

        var result = await client.CheckAsync("   ");

        Assert.Equal(HealthCheckStatus.Unreachable, result.Status);
        Assert.Empty(handler.Requests);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = new();

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
                Content = new StringContent("""{"status":"ok"}"""),
            });
        }
    }
}
