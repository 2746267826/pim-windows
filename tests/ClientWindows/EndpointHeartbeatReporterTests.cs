using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 工单 06 / G1：Windows 端点心跳上报。
///
/// 这里回答的是「端点页为什么永远是空的」这条断链的客户端一侧：
/// <list type="number">
///   <item><description>客户端真的会调用 <c>POST /api/v1/endpoints/{deviceId}/heartbeat</c>，
///     且请求体只带契约里那四个字段（<c>platform</c> 必填，其余三个来自真实观测）；</description></item>
///   <item><description><c>deviceId</c> 与守护程序其它接口用的是**同一个**标识
///     （<see cref="Environment.MachineName"/>），不会把同一台机器显示成两台设备（AC-4）；</description></item>
///   <item><description>上报失败**不静默**：连续次数、最后错误、最后成功时刻都能被读到（G1.3）。</description></item>
/// </list>
///
/// 注意证据等级：本文件里除 <see cref="RealHttp_RoundTripsHeartbeatToARealServer"/> 之外都是
/// 「组件 + 桩传输层」级；那条是真实 HTTP（本机 <see cref="HttpListener"/>）往返。
/// 真机（Windows 上的守护程序）验收仍由维护者执行 —— Linux 上跑不了 WPF 宿主。
/// </summary>
public class EndpointHeartbeatReporterTests
{
    private const string OkEnvelope =
        "{\"code\":0,\"data\":{\"deviceId\":\"PC-01\",\"platform\":\"windows\",\"appVersion\":\"1.0.0\"," +
        "\"uploadStatus\":\"Healthy\",\"collectionCacheCount\":0,\"onlineOnlyBlockedCount\":0," +
        "\"lastHeartbeatAt\":\"2026-10-14T00:00:00Z\"},\"message\":\"\",\"timestamp\":\"2026-10-14T00:00:00Z\"}";

    // ---- 取值口径 ------------------------------------------------------------

    /// <summary>
    /// <c>uploadStatus</c> 用服务端已在用的词表（pim-web 端点页把 <c>Healthy</c> 当绿、其余当黄；
    /// 服务端 Today 区块把 <c>Warning</c>/<c>Critical</c> 当异常）。没有数据时如实报 <c>Unknown</c>，
    /// 不把"没上报过"写成"健康"。
    /// </summary>
    [Theory]
    [InlineData(null, true, "Healthy")]
    [InlineData("", true, "Healthy")]
    [InlineData("   ", true, "Healthy")]
    [InlineData(null, false, "Unknown")]
    [InlineData("Upload returned null response", true, "Warning")]
    [InlineData("Upload returned null response", false, "Warning")]
    public void ResolveUploadStatus_UsesTheServersVocabulary(
        string? lastUploadError,
        bool hasCompletedUpload,
        string expected)
    {
        Assert.Equal(expected, EndpointHeartbeatReporter.ResolveUploadStatus(lastUploadError, hasCompletedUpload));
    }

    /// <summary>契约要求 <c>platform</c> 必填、其余可空；客户端四项都要带上（工单 §四 G1.1）。</summary>
    [Fact]
    public void BuildRequest_CarriesExactlyTheContractFields()
    {
        var request = EndpointHeartbeatReporter.BuildRequest(Snapshot());

        var fields = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .EnumerateObject()
            .Select(p => p.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { "appVersion", "collectionCacheCount", "platform", "uploadStatus" },
            fields);
        Assert.Equal("windows", request.Platform);
    }

    /// <summary>四项的值必须来自真实观测，而不是常量。</summary>
    [Fact]
    public void BuildRequest_MapsObservedValuesOntoTheWireFields()
    {
        var healthy = EndpointHeartbeatReporter.BuildRequest(Snapshot());
        Assert.Equal("1.2.3", healthy.AppVersion);
        Assert.Equal("Healthy", healthy.UploadStatus);
        Assert.Equal(7, healthy.CollectionCacheCount);

        var failing = EndpointHeartbeatReporter.BuildRequest(
            Snapshot(lastUploadError: "HTTP 500", collectionCacheCount: 0));
        Assert.Equal("Warning", failing.UploadStatus);
        Assert.Equal(0, failing.CollectionCacheCount);
        Assert.Equal("1.2.3", failing.AppVersion);

        var never = EndpointHeartbeatReporter.BuildRequest(
            Snapshot(hasCompletedUpload: false, collectionCacheCount: null));
        Assert.Equal("Unknown", never.UploadStatus);
        Assert.Null(never.CollectionCacheCount);
    }

    /// <summary>
    /// AC-4：端点心跳用的设备标识必须与守护程序其它接口一致。
    ///
    /// 这里刻意**不**写"把 Environment.MachineName 传进去、再断言它等于 Environment.MachineName"那种恒真断言
    /// （review 指出过）。真正要守住的是两条结构性事实：
    ///  1. 调用方**没有机会**自己造一个 ID —— 上报入口与快照构造都不接受 deviceId 参数；
    ///  2. 唯一的来源就是守护程序其它通道用的那个表达式。
    /// </summary>
    [Fact]
    public void DeviceIdHasASingleSourceAndNoCallerSuppliedOverride()
    {
        var snapshot = EndpointHeartbeatReporter.ForLocalMachine(
            "1.2.3",
            new EndpointUploadSignals(null, 1, null, 0, null, null, false),
            0);

        Assert.Equal(Environment.MachineName, snapshot.DeviceId);

        // 生产源码里，本机快照的设备标识只能来自 Environment.MachineName。
        var source = RepoSource("src", "client-windows", "Pim.Client.Core", "Services", "EndpointHeartbeatReporter.cs");
        var factoryStart = source.IndexOf("public static EndpointHeartbeatSnapshot ForLocalMachine", StringComparison.Ordinal);
        Assert.True(factoryStart >= 0);
        Assert.Contains("Environment.MachineName", source[factoryStart..]);

        // 调用方无法传入 deviceId：入口方法只有 (快照, ct)，快照工厂没有 deviceId 形参。
        Assert.DoesNotContain("string deviceId", source);

        // 与守护程序心跳同源：那边用的也是 Environment.MachineName（App.xaml.cs 的调用点）。
        var app = RepoSource("src", "client-windows", "Pim.Client.App", "App.xaml.cs");
        Assert.Contains("DaemonHeartbeatReporter.BuildHeartbeat(\n                Environment.MachineName", app);
    }

    /// <summary>
    /// `uploadStatus` 的输入必须覆盖**每一条**上传通道，并且不能把 KeyStats 的"正常跳过"当成失败
    /// （review 的两条 Important：站点通道被漏掉 → 持续失败却报 Healthy；stale-zero 正常节流 → 长期误报 Warning）。
    /// </summary>
    [Fact]
    public void UploadSignalsCoverEveryChannelAndSeparateSkipsFromFailures()
    {
        // 只有站点通道失败：必须 Warning（不能因为轨迹正常就报 Healthy）。
        var siteFailing = new EndpointUploadSignals(
            TrackerLastError: null,
            TrackerEventsUploaded: 10,
            SiteLastError: "HTTP 500 boom",
            SiteEventsUploaded: 0,
            KeyStatsLastUploadError: null,
            KeyStatsLastUploadTime: null,
            KeyStatsLastAttemptWasSkipped: false);
        Assert.Equal("Warning", EndpointHeartbeatReporter.ResolveUploadStatus(
            siteFailing.ResolveLastError(), siteFailing.HasCompletedUpload));

        // 只有站点通道成功过：算"上传过"，不是 Unknown。
        var siteOnly = siteFailing with { SiteLastError = null, SiteEventsUploaded = 5 };
        Assert.Null(siteOnly.ResolveLastError());
        Assert.True(siteOnly.HasCompletedUpload);
        Assert.Equal("Healthy", EndpointHeartbeatReporter.ResolveUploadStatus(
            siteOnly.ResolveLastError(), siteOnly.HasCompletedUpload));

        // KeyStats 按策略跳过（stale-zero）：LastUploadError 里是说明文案，但不是失败。
        var keyStatsSkipped = new EndpointUploadSignals(
            TrackerLastError: null,
            TrackerEventsUploaded: 0,
            SiteLastError: null,
            SiteEventsUploaded: 0,
            KeyStatsLastUploadError: "KeyStats API 可达但计数全 0 或不增长",
            KeyStatsLastUploadTime: null,
            KeyStatsLastAttemptWasSkipped: true);
        Assert.Null(keyStatsSkipped.ResolveLastError());
        Assert.False(keyStatsSkipped.HasCompletedUpload);
        Assert.Equal("Unknown", EndpointHeartbeatReporter.ResolveUploadStatus(
            keyStatsSkipped.ResolveLastError(), keyStatsSkipped.HasCompletedUpload));

        // KeyStats 真的失败（不是跳过）：必须 Warning。
        var keyStatsFailed = keyStatsSkipped with { KeyStatsLastAttemptWasSkipped = false };
        Assert.Equal("KeyStats API 可达但计数全 0 或不增长", keyStatsFailed.ResolveLastError());
        Assert.Equal("Warning", EndpointHeartbeatReporter.ResolveUploadStatus(
            keyStatsFailed.ResolveLastError(), keyStatsFailed.HasCompletedUpload));
    }

    // ---- 上报行为 ------------------------------------------------------------

    [Fact]
    public async Task ReportAsync_PostsToTheContractPathAndRecordsSuccess()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, OkEnvelope));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler));

        var ok = await reporter.ReportAsync(Snapshot(deviceId: "PC-01"));

        Assert.True(ok);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api/v1/endpoints/PC-01/heartbeat", sent.Path);
        Assert.Equal(
            new[] { "appVersion", "collectionCacheCount", "platform", "uploadStatus" },
            JsonDocument.Parse(sent.Body!).RootElement.EnumerateObject()
                .Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.Contains("\"platform\":\"windows\"", sent.Body!);
        Assert.NotNull(reporter.LastAttemptAt);
        Assert.NotNull(reporter.LastSuccessAt);
        Assert.Equal(0, reporter.ConsecutiveFailures);
        Assert.Null(reporter.LastError);
        Assert.Equal("Healthy", reporter.LastReportedUploadStatus);
    }

    /// <summary>时刻来自注入的时钟（B1：测试不依赖真实时间）。</summary>
    [Fact]
    public async Task ReportAsync_TimestampsComeFromTheInjectedClock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, OkEnvelope));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler), null, clock);

        Assert.True(await reporter.ReportAsync(Snapshot()));

        Assert.Equal(clock.GetUtcNow(), reporter.LastAttemptAt);
        Assert.Equal(clock.GetUtcNow(), reporter.LastSuccessAt);
    }

    /// <summary>设备标识要进 URL 路由并转义，不能未转义地拼进去。</summary>
    [Fact]
    public async Task ReportAsync_EscapesTheDeviceIdIntoTheRoute()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, OkEnvelope));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler));

        await reporter.ReportAsync(Snapshot(deviceId: "PC 01/../etc"));

        Assert.Equal(
            "/api/v1/endpoints/PC%2001%2F..%2Fetc/heartbeat",
            Assert.Single(handler.Requests).Path);
    }

    /// <summary>服务端 5xx：必须记账、必须不抛（它是守护程序周期循环里的一步）。</summary>
    [Fact]
    public async Task ReportAsync_OnServerError_RecordsTheFailureStreakWithoutThrowing()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.InternalServerError, "boom"));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler));

        Assert.False(await reporter.ReportAsync(Snapshot()));
        Assert.False(await reporter.ReportAsync(Snapshot()));

        Assert.Equal(2, reporter.ConsecutiveFailures);
        Assert.NotNull(reporter.LastError);
        Assert.Contains("500", reporter.LastError);
        Assert.NotNull(reporter.LastAttemptAt);
        Assert.Null(reporter.LastSuccessAt);
    }

    /// <summary>
    /// 200 + 空/null 正文：服务端没有确认落库，**不能**记成成功（否则"没上报"会被显示成"刚上报"）。
    /// </summary>
    [Fact]
    public async Task NullResponseIsRecordedAsFailure()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "null"));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler));

        Assert.False(await reporter.ReportAsync(Snapshot()));

        Assert.Equal(1, reporter.ConsecutiveFailures);
        Assert.Contains("null", reporter.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(reporter.LastSuccessAt);
        Assert.Null(reporter.LastReportedUploadStatus);
    }

    /// <summary>传输层异常（断网/未启动）：同样可见，且不把队列/循环炸掉。</summary>
    [Fact]
    public async Task ReportAsync_OnTransportFailure_RecordsVisibleFailure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler), new TrackerLogger(1));

        Assert.False(await reporter.ReportAsync(Snapshot()));

        Assert.Equal(1, reporter.ConsecutiveFailures);
        Assert.Contains("connection refused", reporter.LastError);
    }

    [Fact]
    public async Task ReportAsync_AfterFailures_RecoveryResetsTheStreak()
    {
        var fail = true;
        var handler = new StubHandler(_ => fail
            ? Response(HttpStatusCode.InternalServerError, "boom")
            : Response(HttpStatusCode.OK, OkEnvelope));
        var reporter = new EndpointHeartbeatReporter(new ApiClient(() => handler));

        await reporter.ReportAsync(Snapshot());
        await reporter.ReportAsync(Snapshot());
        Assert.Equal(2, reporter.ConsecutiveFailures);

        fail = false;
        Assert.True(await reporter.ReportAsync(Snapshot()));

        Assert.Equal(0, reporter.ConsecutiveFailures);
        Assert.Null(reporter.LastError);
        Assert.NotNull(reporter.LastSuccessAt);
    }

    // ---- 真实 HTTP 往返 ------------------------------------------------------

    /// <summary>
    /// 真实 HTTP：真 <see cref="HttpClient"/> → 本机 <see cref="HttpListener"/>，
    /// 由**服务端一侧**（而不是桩 handler）核对方法、路径与请求体字段。
    /// 这条在 Linux 与 Windows 上都会真的执行（没有 Windows-only 短路）。
    /// </summary>
    [Fact]
    public async Task RealHttp_RoundTripsHeartbeatToARealServer()
    {
        var port = FreePort();
        using var server = new RecordingServer(port);
        var api = new ApiClient();
        api.SetBaseUrl($"http://{server.Host}:{port}");
        var reporter = new EndpointHeartbeatReporter(api);

        Assert.True(await reporter.ReportAsync(Snapshot(deviceId: "PC-01")));

        var received = await server.WaitForRequestAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("POST", received.Method);
        Assert.Equal("/api/v1/endpoints/PC-01/heartbeat", received.Path);

        using var body = JsonDocument.Parse(received.Body);
        Assert.Equal("windows", body.RootElement.GetProperty("platform").GetString());
        Assert.Equal("1.2.3", body.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal("Healthy", body.RootElement.GetProperty("uploadStatus").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("collectionCacheCount").GetInt32());
        Assert.Null(server.LastError);
    }

    /// <summary>
    /// `collectionCacheCount` 的来源必须覆盖"桥已接收、还没上传成功"的事件
    /// （review 的 Important：只数两个上传队列会漏掉这部分，让"还有多少没传上去"少报）。
    ///
    /// 这里用真实 HTTP 把一批站点事件推进**没有**守护程序搬运的桥里：事件停在那儿，
    /// <see cref="NativeTrackerService.PendingUploadCount"/> 必须把它算进去。
    /// </summary>
    [Fact]
    public async Task PendingUploadCountIncludesEventsWaitingInTheBridgeChannel()
    {
        var port = FreePort();
        using var bridge = new BrowserBridgeService(port);
        bridge.Start();
        using var tracker = new NativeTrackerService(
            new ApiClient(() => new StubHandler(_ => Response(HttpStatusCode.OK, OkEnvelope))),
            new TrackerConfig { Enabled = true, BrowserBridgePort = port },
            bridge: bridge);

        using var http = new HttpClient();
        var payload = """
            {"events":[
              {"kind":"focus","host":"github.com","startMs":1000,"endMs":6000},
              {"kind":"tick","host":"github.com","startMs":2000,"durationMs":30000}
            ]}
            """;
        var response = await http.PostAsync(
            $"http://localhost:{port}/browser/site/heartbeat",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode, $"site batch rejected: {response.StatusCode}");
        Assert.Equal(2, bridge.SiteEventsReceived);
        Assert.Equal(2, bridge.SiteEventsPendingInChannel);
        Assert.Equal(2, tracker.PendingUploadCount);
    }

    // ---- 装配（这条缺陷本身就是"接口在、生产路径没人调"）----------------------

    /// <summary>
    /// 本工单的原始故障形态是「接口早就存在，但没有任何客户端在调」。所以"新写了一个 reporter"
    /// 不算修好：必须先装配，并且真的挂在**既有的**守护程序周期循环里（不新增定时器、不新增服务）。
    /// </summary>
    [Fact]
    public void DaemonStartup_WiresTheHeartbeatIntoTheExistingPeriodicLoop()
    {
        var app = RepoSource("src", "client-windows", "Pim.Client.App", "App.xaml.cs");
        var startup = RepoSource("src", "client-windows", "Pim.Client.App", "Startup.cs");

        Assert.Contains("AddSingleton<EndpointHeartbeatReporter>", startup);
        Assert.Contains("EndpointHeartbeatReporter.ForLocalMachine", app);

        // 上报点必须落在既有循环里那"一拍"的方法内部（RunHeartbeatLoopAsync 每 2 分钟调它一次），
        // 而不是装配在别处、或者只在启动时打一枪。切片到方法结束（下一个成员声明）为止，
        // 不能一路读到文件末尾 —— 那样"字符串存在"证明不了它在这条可执行路径上（review 指出过）。
        const string onceMarker = "private static async Task ReportHeartbeatOnceAsync";
        var onceStart = app.IndexOf(onceMarker, StringComparison.Ordinal);
        Assert.True(onceStart >= 0, "App.xaml.cs 里找不到 ReportHeartbeatOnceAsync");
        var onceEnd = app.IndexOf("\n    private static DateTime? MaxTime", onceStart, StringComparison.Ordinal);
        Assert.True(onceEnd > onceStart, "App.xaml.cs 里找不到 ReportHeartbeatOnceAsync 的方法边界");
        var onceBody = app[onceStart..onceEnd];
        Assert.Contains("endpointReporter.ReportAsync", onceBody);
        // 两条通道互相隔离地执行（守护进程心跳失败不会跳过端点心跳）。
        Assert.Contains("HeartbeatTickRunner.RunAsync", onceBody);

        // 这一拍确实由既有循环驱动（每 2 分钟一次）。
        Assert.Contains("RunHeartbeatLoopAsync", app);
        var loopStart = app.IndexOf("private async Task RunHeartbeatLoopAsync", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "App.xaml.cs 里找不到 RunHeartbeatLoopAsync");
        Assert.Contains("ReportHeartbeatOnceAsync", app[loopStart..]);
    }

    // ---- helpers -------------------------------------------------------------

    private static string RepoSource(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = System.IO.Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (System.IO.File.Exists(candidate))
            {
                return System.IO.File.ReadAllText(candidate);
            }

            current = System.IO.Directory.GetParent(current)?.FullName;
        }

        throw new System.IO.FileNotFoundException(System.IO.Path.Combine(parts));
    }

    private static EndpointHeartbeatSnapshot Snapshot(
        string deviceId = "PC-01",
        string? lastUploadError = null,
        bool hasCompletedUpload = true,
        int? collectionCacheCount = 7)
        => new(deviceId, "1.2.3", lastUploadError, hasCompletedUpload, collectionCacheCount);

    private static HttpResponseMessage Response(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>只读时钟：让"什么时候被记为上报过"可断言，而不是看上真实时间。</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class StubHandler : HttpMessageHandler    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<(string Method, string Path, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            return _respond(request);
        }
    }

    /// <summary>本机真实 HTTP 服务端：只用 <see cref="HttpListener"/>，不引入测试框架依赖。</summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<(string Method, string Path, string Body)> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;

        public RecordingServer(int port)
        {
            Host = "127.0.0.1";
            _listener.Prefixes.Add($"http://{Host}:{port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Host { get; }

        public string? LastError { get; private set; }

        public Task<(string Method, string Path, string Body)> WaitForRequestAsync(TimeSpan timeout)
            => _received.Task.WaitAsync(timeout);

        private async Task LoopAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                using var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                _received.TrySetResult((
                    context.Request.HttpMethod,
                    context.Request.Url!.AbsolutePath,
                    body));

                var bytes = Encoding.UTF8.GetBytes(OkEnvelope);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception ex)
            {
                LastError = ex.ToString();
                _received.TrySetException(ex);
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
