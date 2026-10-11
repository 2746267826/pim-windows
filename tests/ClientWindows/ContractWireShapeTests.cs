using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;
using Xunit.Sdk;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 · REQ-WIN-01 / REQ-COM-02 / AC-COM-02：
/// 断言**真实客户端实际写出的请求体**与 <c>contract/openapi.json</c> 中该 operation 的
/// 请求 schema 一致。
///
/// 判定口径（Grok 独立 review I-1 修正后的版本）：
///   - 契约 <c>required</c> 里的字段**必须出现**；
///   - 实际出现的字段必须属于契约 <c>properties</c> 允许集合（<c>additionalProperties: false</c>）；
///   - 契约里的**可选字段允许不发送** —— 不能要求“本次请求填满所有 properties”；
///   - 值的 JSON 类型 / nullable / enum 按契约核对；
///   - 只有 fixture 明确填满全部可选字段时，才允许断言“集合相等”。
///
/// 证据层级：真实生产类型 + 真实 <see cref="ApiClient"/> 序列化 + 记录型
/// <see cref="HttpMessageHandler"/> 捕获线上字节；契约侧直接读权威快照。
/// 不接触真实服务器（服务器是否真的拒绝未知字段属 NOT-VERIFIED，见审计 N-09）。
/// </summary>
public sealed class ContractWireShapeTests
{
    private const string LegacyUploadPath = "/api/v1/pc/keystats/upload";
    private const string SamplesUploadPath = "/api/v1/pc/keystats/samples";

    // ---- F-01：browser-tt 上传的每个事件对象 --------------------------------

    [Fact]
    public async Task SiteBrowserTtUpload_EventFields_AreDeclaredByContract()
    {
        var body = await CaptureSiteEventsAsync(at: 1_700_000_000_000L);

        var schema = ContractOperation.ForPath("/api/v1/pc/browser-tt/upload", "post");
        var eventsSchema = schema.Property("events").Items!;
        schema.AssertRequiredPresent(body);
        eventsSchema.AssertActualFieldsAllowed(ObjectNamesOf(body, "events", index: 0));

        // Grok I-1：站点上传必须同时断言外层 deviceId 与 events 数组的条数，
        // 不能只看事件对象的字段集合。
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("PC-TEST", doc.RootElement.GetProperty("deviceId").GetString());
        Assert.Single(doc.RootElement.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task SiteBrowserTtUpload_EventFields_AreDeclaredByContract_WhenAtIsNull()
    {
        // 审计实测：at 即使为 null 也会被序列化（System.Text.Json Web 默认
        // DefaultIgnoreCondition=Never），因此“每条上送事件都带契约外字段”。
        var body = await CaptureSiteEventsAsync(at: null);

        var eventsSchema = ContractOperation.ForPath("/api/v1/pc/browser-tt/upload", "post").Property("events").Items!;
        eventsSchema.AssertActualFieldsAllowed(ObjectNamesOf(body, "events", index: 0));
    }

    [Fact]
    public async Task SiteBrowserTtUpload_TypesAndEnum_MatchContract()
    {
        var body = await CaptureSiteEventsAsync(at: 1_700_000_000_000L);

        var schema = ContractOperation.ForPath("/api/v1/pc/browser-tt/upload", "post");
        using var doc = JsonDocument.Parse(body);
        schema.AssertValueConforms("/", doc.RootElement);
        schema.Property("events").Items!.AssertValueConforms("/events/0", doc.RootElement.GetProperty("events")[0]);
    }

    [Fact]
    public async Task SiteBrowserTtUpload_UnknownField_FailsTheContractCheck()
    {
        var body = await CaptureSiteEventsAsync(at: 1_700_000_000_000L);
        var eventsSchema = ContractOperation.ForPath("/api/v1/pc/browser-tt/upload", "post").Property("events").Items!;

        // 反例：把实际字段集合里塞一个契约没有的名字，检查必须报错。
        var drifted = ObjectNamesOf(body, "events", index: 0).Append("notInContract").ToHashSet(StringComparer.Ordinal);
        Assert.ThrowsAny<XunitException>(() => eventsSchema.AssertActualFieldsAllowed(drifted));
    }

    [Fact]
    public async Task SiteBrowserTtUpload_MissingRequiredField_FailsTheContractCheck()
    {
        var body = await CaptureSiteEventsAsync(at: 1_700_000_000_000L);
        var schema = ContractOperation.ForPath("/api/v1/pc/browser-tt/upload", "post");

        // 契约 required=[deviceId]。去掉它必须报错（否则“必填”形同虚设）。
        var withoutDeviceId = System.Text.RegularExpressions.Regex.Replace(body, "\"deviceId\":\"[^\"]*\",?", string.Empty);
        Assert.ThrowsAny<XunitException>(() => schema.AssertRequiredPresent(withoutDeviceId));

        // 正对照：原样请求体满足 required。
        schema.AssertRequiredPresent(body);
    }

    // ---- F-02：keystats 的两个端点字段格式不同，必须分别核对 -------------------

    [Fact]
    public async Task KeystatsProductionCall_UsesContractMethodAndPath()
    {
        // 真实生产入口：本地 keyStats HTTP 由桩 handler 提供快照，进程枚举报告一个
        // 当前会话内的 KeyStats 进程（健康判定因而是“可上传”），随后执行
        // KeyStatsCollectorService **自己**的采集→上传流程。
        // method / 路径 / 请求体全部由产品代码决定；测试不传入任何端点，
        // 也不再调用私有方法手工递上“正确路径”（Grok I-2 指出的问题）。
        var requests = await CaptureKeystatsProductionRequestsAsync(SnapshotJson(appStats: "null"));

        Assert.Equal(2, requests.Count);
        var upload = requests.Single(r => r.Path == LegacyUploadPath);
        var samples = requests.Single(r => r.Path == SamplesUploadPath);
        Assert.Equal(HttpMethod.Post, upload.Method);
        Assert.Equal(HttpMethod.Post, samples.Method);

        // 生产实际发出的路径必须真的落在契约的 operation 上（这个方法拼错路径/写错 method 就查不到）。
        ContractOperation.ForPath(upload.Path, "post");
        ContractOperation.ForPath(samples.Path, "post");
    }

    [Fact]
    public void ContractPathCheck_RejectsAWrongMethodOrPath()
    {
        // 故障对照：路径拼错、method 写错时契约查询必须失败 ——
        // 否则上面那条“生产路径落在契约上”只是空话。
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForPath("/api/v1/pc/keystats/uploads", "post"));
        Assert.ThrowsAny<Exception>(() => ContractOperation.ForPath(LegacyUploadPath, "put"));
    }

    [Fact]
    public async Task KeystatsLegacyUpload_RealBody_FieldsAreDeclaredByContract()
    {
        // legacy 端点的 payload 是私有嵌套类型，这里捕获的是产品代码自己发出的**真实线上字节**。
        var body = BodyOf(await CaptureKeystatsProductionRequestsAsync(SnapshotJson(appStats: "null")), LegacyUploadPath);

        var schema = ContractOperation.ForPath(LegacyUploadPath, "post");
        schema.AssertRequiredPresent(body);
        schema.AssertActualFieldsAllowed(ObjectNamesOf(body));
        schema.AssertValueConforms("/", JsonDocument.Parse(body).RootElement);
    }

    [Fact]
    public async Task KeystatsLegacyUpload_NonEmptyNestedMaps_ConformToContract()
    {
        var body = BodyOf(
            await CaptureKeystatsProductionRequestsAsync(SnapshotJson(appStats: NestedAppStatsJson)),
            LegacyUploadPath);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // fixture 明确填满了嵌套集合，所以这里是在检查**非空**嵌套形状
        // （review-policy 第 1 条：只有说明“为何填满”的 fixture 才能要求集合相等）。
        Assert.NotEmpty(root.GetProperty("appStats").EnumerateObject());
        Assert.NotEmpty(root.GetProperty("keyPressCounts").EnumerateObject());

        // 递归核对 appStats 的 additionalProperties（AppStatEntry）与
        // keyPressCounts 的 int 值类型。
        var schema = ContractOperation.ForPath(LegacyUploadPath, "post");

        // 先单独核对两个嵌套子树：根上仍有 F-02 的顶层违规，不能让它掩盖嵌套形状的检查。
        schema.Property("appStats").AssertValueConforms("/appStats", root.GetProperty("appStats"));
        schema.Property("keyPressCounts").AssertValueConforms("/keyPressCounts", root.GetProperty("keyPressCounts"));

        schema.AssertValueConforms("/", root);
    }

    [Fact]
    public void NestedAppStatsCheck_RejectsARenamedNestedField()
    {
        // 故障对照：嵌套字段名写错（例如 C# 属性没有 camelCase 策略时发出的
        // PascalCase `AppName`）必须被检出，否则“非空嵌套已覆盖”是空话。
        var appStats = ContractOperation.ForPath(LegacyUploadPath, "post").Property("appStats");
        using var renamed = JsonDocument.Parse("""{"chrome":{"AppName":"chrome"}}""");
        Assert.ThrowsAny<XunitException>(
            () => appStats.AssertValueConforms("/appStats", renamed.RootElement));

        // 正对照：契约名 appName 通过。
        using var ok = JsonDocument.Parse("""{"chrome":{"appName":"chrome"}}""");
        appStats.AssertValueConforms("/appStats", ok.RootElement);
    }

    [Fact]
    public async Task KeystatsSamplesUpload_RealBody_FieldsAreDeclaredByContract()
    {
        // 对照组：同一条采集链路的另一个端点、另一个 payload 类型（审计 P-06）。
        var body = BodyOf(await CaptureKeystatsProductionRequestsAsync(SnapshotJson(appStats: "null")), SamplesUploadPath);

        var schema = ContractOperation.ForPath(SamplesUploadPath, "post");
        schema.AssertRequiredPresent(body);
        schema.AssertActualFieldsAllowed(ObjectNamesOf(body));
        schema.AssertValueConforms("/", JsonDocument.Parse(body).RootElement);
    }

    [Fact]
    public async Task KeystatsSamplesUpload_StillCarriesTheFormattedValuesFromLocalStats()
    {
        // 行为保留（review-policy 第 4 条）：修 F-02 只允许把 formatted* 从 **legacy upload**
        // 拿掉；本地 keyStats 提供的值必须继续出现在 samples 上传里。
        // 如果把读取类型 KeyStatsSnapshot 的这两个属性加 [JsonIgnore]，读入会一起被关掉，
        // 字段名检查抓不到这种静默回退，而这里会看到 null。
        var body = BodyOf(await CaptureKeystatsProductionRequestsAsync(SnapshotJson(appStats: "null")), SamplesUploadPath);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("123 m", doc.RootElement.GetProperty("formattedMouseDistance").GetString());
        Assert.Equal("456 m", doc.RootElement.GetProperty("formattedScrollDistance").GetString());
        Assert.Equal(9, doc.RootElement.GetProperty("peakKPS").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("peakCPS").GetInt32());
    }

    [Fact]
    public void KeystatsUpload_SchemaPermitsOptionalFieldsToBeOmitted()
    {
        // review-policy 第 1 条：可选字段允许不发送。契约里 KeystatsUploadRequest 没有 required，
        // 所以一个只带必填（此处为“一个都没有”）的最小请求体同样合法 —— 不允许因为
        // “契约声明了 14 个属性、线上只发了 3 个”就判失败。
        var schema = ContractOperation.ForPath(LegacyUploadPath, "post");
        Assert.Empty(schema.Required);

        const string minimal = """{"keyPresses":1}""";
        schema.AssertRequiredPresent(minimal);
        schema.AssertActualFieldsAllowed(ObjectNamesOf(minimal));
        schema.AssertValueConforms("/", JsonDocument.Parse(minimal).RootElement);
    }

    [Fact]
    public void KeystatsUpload_SyntheticRenamedAndExtraFields_AreRejected()
    {
        // 反例一律用**合成字段集**，不拿生产序列化结果当反例：
        // Grok I-1 指出，把“现网体必须非法”写成断言，会在 F-02 修好之后
        // 反而把合法修法判红。
        var schema = ContractOperation.ForPath(LegacyUploadPath, "post");

        var renamed = schema.PropertyNames
            .Where(n => n != "peakKps")
            .Append("peakKPS")
            .ToHashSet(StringComparer.Ordinal);
        Assert.ThrowsAny<XunitException>(() => schema.AssertActualFieldsAllowed(renamed));

        var extra = schema.PropertyNames.Append("formattedMouseDistance").ToHashSet(StringComparer.Ordinal);
        Assert.ThrowsAny<XunitException>(() => schema.AssertActualFieldsAllowed(extra));

        // 交叉对照：legacy 的合法名 peakKps 在 samples 的 schema 里并不存在，
        // 因此同一个检查在 samples 上必须报错（两个端点不能共用一套字段判定）。
        var cross = ContractOperation.ForPath(SamplesUploadPath, "post")
            .PropertyNames.Append("peakKps").ToHashSet(StringComparer.Ordinal);
        Assert.ThrowsAny<XunitException>(
            () => ContractOperation.ForPath(SamplesUploadPath, "post").AssertActualFieldsAllowed(cross));
    }

    [Fact]
    public void KeystatsPayloadTypes_AreDistinctPerEndpoint()
    {
        // 审计 review-policy 第 4 条：keystats upload 与 samples 的字段格式分别测试。
        // 这两个端点用**不同的** payload 类型与**不同的**契约 schema，
        // 不能拿 samples 的通过去证明 upload 正常。
        var legacy = ContractOperation.ForPath(LegacyUploadPath, "post");
        var samples = ContractOperation.ForPath(SamplesUploadPath, "post");
        Assert.NotEqual(legacy.PropertyNames.OrderBy(x => x), samples.PropertyNames.OrderBy(x => x));

        // samples 带 formatted* / sampledAt / pimDeviceId，legacy 不带；
        // legacy 用 peakKps，samples 用 peakKPS。大小写必须按各自 schema 判。
        Assert.Contains("peakKps", legacy.PropertyNames);
        Assert.Contains("peakKPS", samples.PropertyNames);
        Assert.Contains("formattedMouseDistance", samples.PropertyNames);
        Assert.DoesNotContain("formattedMouseDistance", legacy.PropertyNames);
    }

    // ---- 用真实线上字节替代“源码字符串断言”的覆盖（F-10） ------------------

    [Fact]
    public async Task DaemonHeartbeat_FieldsAndTypes_MatchContractSchema()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        var reporter = new DaemonHeartbeatReporter(api);
        await reporter.ReportAsync(DaemonHeartbeatReporter.BuildHeartbeat(
            "PC-TEST", "1.0.0", ClientDefaults.DefaultServerUrl, null, null, null, uploadQueueCount: 0));

        var body = await CapturedAsync(handler);
        var schema = ContractOperation.ForPath("/api/v1/daemon/heartbeat", "post");
        schema.AssertRequiredPresent(body);
        schema.AssertActualFieldsAllowed(ObjectNamesOf(body));
        schema.AssertValueConforms("/", JsonDocument.Parse(body).RootElement);
    }

    [Fact]
    public void DaemonHeartbeat_StateEnum_RejectsOutOfContractValues()
    {
        // 契约 DaemonSourceState = [Unknown, Available, Unavailable, Paused]。
        // 把 activityWatchState 改成契约外取值必须失败，否则枚举约束是空的。
        var schema = ContractOperation.ForPath("/api/v1/daemon/heartbeat", "post").Property("activityWatchState");
        Assert.ThrowsAny<XunitException>(
            () => schema.AssertValueConforms("/activityWatchState", JsonDocument.Parse("\"Running\"").RootElement));

        // 正对照：契约内取值通过。
        schema.AssertValueConforms("/activityWatchState", JsonDocument.Parse("\"Available\"").RootElement);
    }

    [Fact]
    public async Task PlannedOffline_FieldsAndTypes_MatchContractSchema()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        var reporter = new PlannedOfflineReporter(api);
        await reporter.ReportAsync(PlannedOfflineReporter.BuildRequest(
            "PC-TEST", "shutdown", new DateTimeOffset(2026, 10, 10, 6, 0, 0, TimeSpan.Zero)));

        var body = await CapturedAsync(handler);
        var schema = ContractOperation.ForPath("/api/v1/daemon/planned-offline", "post");
        schema.AssertRequiredPresent(body);
        schema.AssertActualFieldsAllowed(ObjectNamesOf(body));
        schema.AssertValueConforms("/", JsonDocument.Parse(body).RootElement);
    }

    [Fact]
    public async Task TrackerUpload_EventFieldsAndTypes_MatchContractSchema()
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        await api.PostAsync<ApiResponse<object>>(
            "/pc/tracker/upload",
            new TrackerEventsUploadRequest
            {
                DeviceId = "PC-TEST",
                Events = new List<TrackerEventForUpload> { new() },
            });

        var body = await CapturedAsync(handler);
        var schema = ContractOperation.ForPath("/api/v1/pc/tracker/upload", "post");
        schema.AssertRequiredPresent(body);
        schema.AssertActualFieldsAllowed(ObjectNamesOf(body));
        using var doc = JsonDocument.Parse(body);
        schema.Property("events").Items!.AssertActualFieldsAllowed(ObjectNamesOf(body, "events", index: 0));
        schema.Property("events").Items!.AssertValueConforms("/events/0", doc.RootElement.GetProperty("events")[0]);
    }

    [Fact]
    public void ContractChecks_ActuallyCatchARenamedField()
    {
        // AC-COM-03 故障对照：比较逻辑必须真的会对字段改名报错，否则“CI 必须变红”是空话。
        var real = ContractOperation.ForPath("/api/v1/daemon/heartbeat", "post");
        var drifted = real.PropertyNames.Where(p => p != "deviceId").Append("deviceID").ToHashSet(StringComparer.Ordinal);
        Assert.ThrowsAny<XunitException>(() => real.AssertActualFieldsAllowed(drifted));
    }

    [Fact]
    public void ContractChecks_ActuallyCatchATypeMismatch()
    {
        var schema = ContractOperation.ForPath("/api/v1/daemon/heartbeat", "post").Property("uploadQueueCount");
        Assert.ThrowsAny<XunitException>(
            () => schema.AssertValueConforms("/uploadQueueCount", JsonDocument.Parse("\"3\"").RootElement));

        // 正对照：契约类型 integer。
        schema.AssertValueConforms("/uploadQueueCount", JsonDocument.Parse("3").RootElement);
        // nullable:true 时 null 也合法。
        schema.AssertValueConforms("/uploadQueueCount", JsonDocument.Parse("null").RootElement);
    }

    // ---- capture helpers ----------------------------------------------------------

    private static async Task<string> CapturedAsync(RecordingHandler handler)
    {
        // 记录型 handler 是同步完成的，但被测代码是 async；这里等它稳定下来。
        for (var i = 0; i < 50 && handler.LastBody is null; i++)
        {
            await Task.Delay(10);
        }

        Assert.NotNull(handler.LastBody);
        return handler.LastBody!;
    }

    private static async Task<string> CaptureSiteEventsAsync(long? at)
    {
        var normalized = SiteEventNormalizer.Normalize(new SiteEventDto
        {
            Kind = "visit",
            Host = "y.com",
            Date = "2026-10-10",
            At = at,
        });
        Assert.NotNull(normalized);

        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        await api.PostAsync<ApiResponse<object>>(
            "/pc/browser-tt/upload",
            new SiteEventsUploadRequest
            {
                DeviceId = "PC-TEST",
                Events = new List<SiteEventDto> { normalized! },
            });

        Assert.NotNull(handler.LastBody);
        return handler.LastBody!;
    }

    /// <summary>
    /// 驱动 <see cref="KeyStatsCollectorService"/> 的产品入口跑完一次「读本地统计 → 健康判定 → 上传」，
    /// 返回产品代码自己发出的全部请求（method / 绝对路径 / body）。
    ///
    /// 两个接缝都是行为保留的：本地 keyStats HTTP 换成固定快照，进程枚举报告一个
    /// 当前会话内的 KeyStats 进程（否则健康判定按“进程未运行”跳过上传）。
    /// </summary>
    private static async Task<IReadOnlyList<CapturedRequest>> CaptureKeystatsProductionRequestsAsync(string snapshotJson)
    {
        var handler = new RecordingHandler();
        var api = new ApiClient(() => handler);
        var service = new KeyStatsCollectorService(
            api,
            processManager: null,
            keyStatsHandlerFactory: () => new SnapshotHandler(snapshotJson),
            processSource: _ => new[] { new KeyStatsProcessInfo(4242, 1, true) });
        using (service)
        {
            await service.RunCollectionCycleAsync();
        }

        Assert.NotEmpty(handler.Requests);
        return handler.Requests;
    }

    private static string BodyOf(IReadOnlyList<CapturedRequest> requests, string path)
        => requests.Single(r => r.Path == path).Body;

    /// <summary>本地 keyStats 统计端点的固定快照。</summary>
    private const string NestedAppStatsJson =
        """{"chrome":{"appName":"chrome","displayName":"Google Chrome","keyPresses":20,"leftClicks":2,"rightClicks":1,"middleClicks":0,"sideBackClicks":0,"sideForwardClicks":0,"scrollDistance":100.5}}""";

    private static string SnapshotJson(string appStats) => $$"""
        {"deviceId":"PC-TEST","date":"2026-10-10T00:00:00.0000000+08:00","keyPresses":42,
         "keyPressCounts":{"65":10,"66":5},"leftClicks":7,"rightClicks":3,"middleClicks":1,
         "sideBackClicks":0,"sideForwardClicks":0,"mouseDistance":123.5,"scrollDistance":456.5,
         "formattedMouseDistance":"123 m","formattedScrollDistance":"456 m","peakKPS":9,"peakCPS":4,
         "appStats":{{appStats}}}
        """;

    // round5：契约读取与 schema 求值已抽到同目录的 ContractSchema.cs
    // （三个测试工程共用一份；本文件只保留别名，调用点不变）。
    private static HashSet<string> ObjectNamesOf(string json, string? arrayProperty = null, int index = 0)
        => ContractJson.ObjectNames(json, arrayProperty, index);

    /// <summary>一次被产品代码发出的请求：method + 绝对路径 + body。</summary>
    private sealed record CapturedRequest(HttpMethod Method, string Path, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests = new();

        public string? LastBody { get; private set; }

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
            LastBody = body;
            lock (_requests)
            {
                _requests.Add(new CapturedRequest(request.Method, request.RequestUri!.AbsolutePath, body));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"message":"","data":"ok"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>本地 keyStats 统计端点的桩：任何请求都回同一份快照。</summary>
    private sealed class SnapshotHandler : HttpMessageHandler
    {
        private readonly string _json;

        public SnapshotHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }
}
