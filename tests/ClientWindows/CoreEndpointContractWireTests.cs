using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / AC-COM-02（F-03）：把 round4 记录的 5 个「无编译中契约用例」
/// 缺口补成**真实**用例 —— 生产入口 → 记录型 handler 捕获线上字节 → 对照
/// <c>contract/openapi.json</c>。本文件覆盖其中 3 个（都在 <c>Pim.Client.Core</c>）：
///
///   | operation | 生产入口 | 站点 |
///   |---|---|---|
///   | `post /api/v1/auth/login`   | <see cref="AuthService.LoginAsync"/>        | AuthService.cs:69 |
///   | `post /api/v1/auth/refresh` | <see cref="AuthService.RefreshAsync"/>      | AuthService.cs:257 |
///   | `post /api/v1/pc/tracker/health` | <see cref="NativeTrackerService.Start"/> 的健康上报循环 | NativeTrackerService.cs:889 |
///
/// 另外两个（`get /health`、`get /api/client/shell/latest`）在各自能真实运行的程序集里：
/// <c>ShellHealthContractWireTests.cs</c>（Pim.Shell.Tests）与
/// <c>DaemonUpdateCheckContractWireTests.cs</c>（Pim.Client.App.Tests）。
///
/// 证据层级 = 真实生产类型 + 真实 <see cref="ApiClient"/> 序列化 + 记录型
/// <see cref="HttpMessageHandler"/> 捕获线上字节 + 权威契约快照。
/// **不接触真实服务器**（服务器是否接受这些请求体属 NOT-VERIFIED，见审计 N-09）。
/// 本文件**没有**改动任何产品行为：只通过既有构造参数注入传输层与时钟。
/// </summary>
public sealed class CoreEndpointContractWireTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 6, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;

    public CoreEndpointContractWireTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pim-contract-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ---- post /api/v1/auth/login ------------------------------------------------

    [Fact]
    public async Task AuthLogin_ProductionEntry_SendsTheContractOperationAndBody()
    {
        var handler = new RoutedHandler();
        var api = new ApiClient(() => handler);
        var auth = new AuthService(api, Path.Combine(_tempDir, "token.json"), new MutableTimeProvider(T0));

        var ok = await auth.LoginAsync("alice", "s3cret");

        Assert.True(ok, "登录在生产代码里应当成功（桩返回了合法的 AuthResponse）");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v1/auth/login", request.Path);

        // 生产实际发出的 operation 必须真的落在契约上（路径拼错/写错 method 就查不到）。
        var schema = ContractOperation.ForPath("/api/v1/auth/login", "post");
        schema.AssertRequiredPresent(request.Body);
        schema.AssertActualFieldsAllowed(ContractJson.ObjectNames(request.Body));
        schema.AssertValueConforms("/", JsonDocument.Parse(request.Body).RootElement);

        // 行为保留：登录成功后令牌被应用（不是“请求发了就算过”）。
        Assert.True(auth.IsAuthenticated);
        Assert.Equal("alice", auth.CurrentUsername);
    }

    // ---- post /api/v1/auth/refresh ----------------------------------------------

    [Fact]
    public async Task AuthRefresh_ProductionEntry_SendsTheContractOperationAndBody()
    {
        var handler = new RoutedHandler();
        var api = new ApiClient(() => handler);
        var clock = new MutableTimeProvider(T0);
        var auth = new AuthService(api, Path.Combine(_tempDir, "token.json"), clock);

        Assert.True(await auth.LoginAsync("alice", "s3cret"));

        // 让 access token 进入“到期前必须续期”的窗口（AuthService.AccessTokenRenewalSkew = 2min）。
        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.True(auth.IsAccessTokenRenewalDue, "前置条件：此时应当需要续期，否则不会发出 /auth/refresh");

        Assert.True(await auth.RefreshAsync());

        var refresh = handler.Requests.Single(r => r.Path == "/api/v1/auth/refresh");
        Assert.Equal(HttpMethod.Post, refresh.Method);

        var schema = ContractOperation.ForPath("/api/v1/auth/refresh", "post");
        schema.AssertRequiredPresent(refresh.Body);
        schema.AssertActualFieldsAllowed(ContractJson.ObjectNames(refresh.Body));
        schema.AssertValueConforms("/", JsonDocument.Parse(refresh.Body).RootElement);

        // 行为保留：续期成功后用的是新令牌。
        Assert.Equal("access-2", auth.CurrentAccessToken);
    }

    // ---- post /api/v1/pc/tracker/health -----------------------------------------

    [Fact]
    public async Task TrackerHealth_ProductionEntry_SendsTheContractOperationAndBody()
    {
        var handler = new RoutedHandler();
        var api = new ApiClient(() => handler);
        var tracker = new NativeTrackerService(
            api,
            new TrackerConfig
            {
                Enabled = true,
                HealthReportIntervalSeconds = 1,
                BrowserBridgePort = FreePort(),
                PollIntervalSeconds = 3600,
                UploadIntervalSeconds = 3600,
            },
            windowResolver: new NullWindowResolver(),
            idleDetector: new NullIdleDetector(),
            bridge: new BrowserBridgeService(FreePort(), new TrackerLogger(1)),
            logger: new TrackerLogger(1),
            stateManager: new TrackerStateManager(Path.Combine(_tempDir, "tracker_state.json")),
            timeProvider: new MutableTimeProvider(T0));

        using (tracker)
        {
            tracker.Start();

            // 健康上报循环由产品代码自己按 HealthReportIntervalSeconds=1s 触发；
            // 这里只等它把请求发出来，不手工替产品拼请求。
            var health = await WaitForRequestAsync(handler, "/api/v1/pc/tracker/health", TimeSpan.FromSeconds(30));
            Assert.Equal(HttpMethod.Post, health.Method);

            var schema = ContractOperation.ForPath("/api/v1/pc/tracker/health", "post");
            schema.AssertRequiredPresent(health.Body);
            schema.AssertActualFieldsAllowed(ContractJson.ObjectNames(health.Body));
            schema.AssertValueConforms("/", JsonDocument.Parse(health.Body).RootElement);

            await tracker.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    // ---- 判据不是空话 ------------------------------------------------------------

    [Fact]
    public void ContractLookup_RejectsADriftedProductionPathOrMethod()
    {
        // 故障对照：把生产路径改一个字（模拟 NativeTrackerService 里写错端点）或写错 method，
        // 契约查询必须立刻失败 —— 否则上面三条“生产路径落在契约上”就是空话。
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForPath("/api/v1/auth/loginz", "post"));
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForPath("/api/v1/auth/login", "get"));
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForPath("/api/v1/pc/tracker/healths", "post"));

        // 正对照：契约里真实存在的 operation 能查到，且 method 归一化为小写。
        Assert.Equal("post", ContractOperation.ForPath("/api/v1/auth/login", "POST").Method);
        Assert.Equal("/api/v1/pc/tracker/health", ContractOperation.ForPath("/api/v1/pc/tracker/health", "post").Path);
    }

    [Fact]
    public async Task AuthLogin_SyntheticExtraField_IsRejectedByTheSameCheck()
    {
        // 反例用**合成字段集**，不把“现网体会非法”写成断言。
        var handler = new RoutedHandler();
        var api = new ApiClient(() => handler);
        var auth = new AuthService(api, Path.Combine(_tempDir, "token.json"), new MutableTimeProvider(T0));
        Assert.True(await auth.LoginAsync("alice", "s3cret"));

        var body = handler.Requests.Single().Body;
        var drifted = ContractJson.ObjectNames(body).Append("notInContract").ToHashSet(StringComparer.Ordinal);
        var schema = ContractOperation.ForPath("/api/v1/auth/login", "post");
        Assert.ThrowsAny<XunitException>(() => schema.AssertActualFieldsAllowed(drifted));
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task<CapturedRequest> WaitForRequestAsync(
        RoutedHandler handler, string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = handler.Requests.FirstOrDefault(r => r.Path == path);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }

        var seen = string.Join(", ", handler.Requests.Select(r => $"{r.Method} {r.Path}"));
        throw new XunitException($"等待生产代码发出 {path} 超时（{timeout.TotalSeconds:F0}s）；已捕获：[{seen}]");
    }

    /// <summary>避免与真实守护进程/其它用例的桥端口冲突（端口 0 不被 HttpListener 接受，用高位随机端口）。</summary>
    private static int FreePort() => 20000 + Random.Shared.Next(1, 20000);

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Body);

    /// <summary>
    /// 记录型 handler：按请求路径返回桩响应，并把**产品代码真正写出的** method/路径/字节记下来。
    /// </summary>
    private sealed class RoutedHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = new();
        private int _refreshCount;

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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add(new CapturedRequest(request.Method, request.RequestUri!.AbsolutePath, body));
            }

            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/api/v1/auth/login" => LoginResponse("access-1", "refresh-1", T0.AddMinutes(15)),
                "/api/v1/auth/refresh" => RefreshResponse(),
                _ => """{"code":0,"message":"","data":"ok"}""",
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private string RefreshResponse()
        {
            _refreshCount++;
            return LoginResponse("access-2", "refresh-2", T0.AddMinutes(30));
        }
    }

    private static string LoginResponse(string accessToken, string refreshToken, DateTimeOffset expiresAt) =>
        JsonSerializer.Serialize(new
        {
            code = 0,
            message = string.Empty,
            data = new
            {
                accessToken,
                refreshToken,
                expiresAt = expiresAt.ToString("O"),
                user = new { id = "u-1", username = "alice", displayName = "Alice" },
            },
        });

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset now) => _now = now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class NullWindowResolver : IWindowResolver
    {
        public TrackerWindowInfo? GetForegroundWindowInfo() => null;
    }

    private sealed class NullIdleDetector : IIdleDetector
    {
        public TimeSpan GetIdleDuration() => TimeSpan.Zero;

        public bool IsScreenOff() => false;
    }
}
