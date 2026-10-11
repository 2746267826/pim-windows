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

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 · REQ-WIN-01 / AC-WIN-01（F-04）：
/// <c>daemon/heartbeat</c> 的 <c>uploadQueueCount</c> 必须等于**守护进程真实的待上传积压**
/// （窗口事件队列 + 站点事件队列），而不是一个常量、也不只是其中一条队列。
///
/// 证据层级：真实 <see cref="NativeTrackerService"/>（真实队列、真实断档补齐、真实桥接通道）
/// + 真实 <see cref="DaemonHeartbeatComposer"/>（产品代码装配心跳的那一段）
/// + 记录型 handler 捕获真实线上心跳体。时钟通过 <see cref="TimeProvider"/> 注入。
///
/// 判定方式：断言心跳值**等于测试可观察到的两条队列长度之和**，而不是“大于 0”。
/// 只计 tracker 队列、漏掉站点队列的半截修复会在站点用例上失败。
/// </summary>
public sealed class HeartbeatQueueDepthTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 6, 0, 0, TimeSpan.Zero);

    private readonly RecordingHandler _handler = new();
    private readonly MutableTimeProvider _clock = new(T0);
    private readonly string _statePath;
    private ApiClient? _api;
    private NativeTrackerService? _tracker;
    private KeyStatsCollectorService? _keyStats;
    private BrowserBridgeService? _bridge;

    public HeartbeatQueueDepthTests()
    {
        _statePath = Path.Combine(Path.GetTempPath(), "pim-queue-depth-" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        _tracker?.Dispose();
        _keyStats?.Dispose();
        _bridge?.Dispose();
        try { File.Delete(_statePath); } catch { }
    }

    [Fact]
    public async Task Heartbeat_ReportsNonZeroQueueDepth_WhenEventsArePending()
    {
        await BuildPendingBacklogAsync();
        var tracker = Tracker();

        // 前置条件也要有独立证据：flush 时真的会发出 tracker/upload（证明积压确实存在过）。
        Assert.True(
            tracker.PendingUploadCount > 0,
            "前置条件：构造断档后待上传队列应当非空（由 flush 真实发出的 /pc/tracker/upload 证明）。");

        var heartbeat = BuildHeartbeat();

        Assert.Equal(tracker.PendingUploadCount, heartbeat.UploadQueueCount);
        Assert.True(
            heartbeat.UploadQueueCount > 0,
            $"队列里已经有 {tracker.PendingUploadCount} 条待上传事件，"
            + $"心跳却上报 uploadQueueCount={heartbeat.UploadQueueCount}；"
            + "服务端会据此认为该端没有积压，失去数据丢失的早期预警能力。");
    }

    [Fact]
    public async Task Heartbeat_QueueDepth_EqualsObservedBacklogAndGrows()
    {
        await BuildPendingBacklogAsync();
        var tracker = Tracker();
        var first = BuildHeartbeat().UploadQueueCount;
        Assert.Equal(tracker.PendingUploadCount, first);

        // 再静默一段并轮询一次：积压继续增长，心跳里的队列深度必须跟着变。
        _clock.Advance(TimeSpan.FromMinutes(40));
        await tracker.DoPollAsync();
        var second = BuildHeartbeat().UploadQueueCount;

        Assert.Equal(tracker.PendingUploadCount, second);
        Assert.True(
            second > first,
            $"有待上传事件持续入队时心跳队列深度必须随之增长：first={first} second={second}。");
    }

    [Fact]
    public async Task Heartbeat_QueueDepth_CountsSiteQueueWhenOnlySiteBacklogExists()
    {
        // 站点事件走的是**另一条**队列（_siteUploadQueue）。只统计 tracker 队列的实现
        // 在这一条上必须失败：此处 tracker 队列为空，积压全部来自站点事件。
        var tracker = Tracker();
        Assert.Equal(0, tracker.PendingUploadCount);

        const int siteCount = 3;
        Bridge().OnSiteEvents(Enumerable.Range(0, siteCount).Select(i => new SiteEventDto
        {
            Kind = "visit",
            Host = $"site{i}.example",
            Date = "2026-10-10",
        }).ToList());
        var drained = tracker.DrainSiteEventsFromBridge();
        Assert.Equal(siteCount, drained);
        Assert.Equal(siteCount, tracker.PendingUploadCount);

        var heartbeat = BuildHeartbeat();

        Assert.Equal(siteCount, heartbeat.UploadQueueCount);
    }

    [Fact]
    public async Task Heartbeat_QueueDepth_EqualsSumOfBothQueues()
    {
        // 两条队列同时有积压时，心跳值必须是**两者之和**。
        await BuildPendingBacklogAsync();
        var tracker = Tracker();
        var trackerBacklog = tracker.PendingUploadCount;
        Assert.True(trackerBacklog > 0);

        const int siteCount = 2;
        Bridge().OnSiteEvents(Enumerable.Range(0, siteCount).Select(i => new SiteEventDto
        {
            Kind = "visit",
            Host = $"site{i}.example",
            Date = "2026-10-10",
        }).ToList());
        tracker.DrainSiteEventsFromBridge();

        var expected = trackerBacklog + siteCount;
        Assert.Equal(expected, tracker.PendingUploadCount);
        Assert.Equal(expected, BuildHeartbeat().UploadQueueCount);
    }

    [Fact]
    public async Task Heartbeat_QueueDepth_ReturnsToZero_AfterBacklogIsUploaded()
    {
        await BuildPendingBacklogAsync();

        // 独立前置条件：先 flush，用真实发出的上传请求证明“积压确实存在过”。
        // （不能用心跳自己的 uploadQueueCount 当前置条件 —— 那正是被测对象。）
        await Tracker().FlushQueueAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            _handler.TrackerUploadBodies.Count > 0,
            "前置条件不成立：flush 之后没有观察到任何 /pc/tracker/upload 请求，说明并没有造出积压。");

        Assert.Equal(0, Tracker().PendingUploadCount);
        Assert.Equal(0, BuildHeartbeat().UploadQueueCount);
    }

    [Fact]
    public async Task Heartbeat_IsActuallySentWithTheQueueDepth()
    {
        // 心跳线上体必须带 uploadQueueCount 字段，且与装配结果一致。
        await BuildPendingBacklogAsync();
        var tracker = Tracker();
        var heartbeat = BuildHeartbeat();
        Assert.True(heartbeat.UploadQueueCount > 0, "前置条件：装配出的心跳应带真实积压数");
        Assert.Equal(tracker.PendingUploadCount, heartbeat.UploadQueueCount);

        var reporter = new DaemonHeartbeatReporter(Api());
        await reporter.ReportAsync(heartbeat);

        Assert.NotNull(_handler.LastHeartbeatBody);
        using var doc = JsonDocument.Parse(_handler.LastHeartbeatBody!);
        Assert.True(doc.RootElement.TryGetProperty("uploadQueueCount", out var sent), "线上心跳体缺少 uploadQueueCount");
        Assert.Equal(heartbeat.UploadQueueCount, sent.GetInt32());
    }

    [Fact]
    public void Heartbeat_QueueDepth_IsZeroWhenTrackerIsNotRunning()
    {
        // tracker 未启用（null）时不得抛异常，且必须上报 0 —— 这条守住行为保留，
        // 避免“修 F-04”时把 null 分支改成解引用。
        var heartbeat = DaemonHeartbeatComposer.Build(
            tracker: null, KeyStats(), ClientDefaults.DefaultServerUrl, "1.0.0-test", _clock);

        Assert.Equal(0, heartbeat.UploadQueueCount);
    }

    // ---- helpers ------------------------------------------------------------------

    private ApiClient Api() => _api ??= new ApiClient(() => _handler);

    private BrowserBridgeService Bridge() => _bridge ??= new BrowserBridgeService(15699, new TrackerLogger(1));

    private NativeTrackerService Tracker() => _tracker ??= new NativeTrackerService(
        Api(),
        new TrackerConfig { Enabled = true, GapThresholdSeconds = 60 },
        windowResolver: new NullWindowResolver(),
        idleDetector: new NullIdleDetector(),
        bridge: Bridge(),
        logger: new TrackerLogger(1),
        stateManager: new TrackerStateManager(_statePath),
        timeProvider: _clock);

    private KeyStatsCollectorService KeyStats() => _keyStats ??= new KeyStatsCollectorService(Api());

    /// <summary>建立基线后静默 40 分钟再轮询，产出必须补传的断档分片（与 Wo400 用例同一手法）。</summary>
    private async Task BuildPendingBacklogAsync()
    {
        await Tracker().DoPollAsync();
        _clock.Advance(TimeSpan.FromMinutes(40));
        await Tracker().DoPollAsync();
    }

    private DaemonHeartbeatRequest BuildHeartbeat() => DaemonHeartbeatComposer.Build(
        Tracker(), KeyStats(), ClientDefaults.DefaultServerUrl, "1.0.0-test", _clock);

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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastHeartbeatBody { get; private set; }
        public List<string> TrackerUploadBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/daemon/heartbeat", StringComparison.Ordinal))
            {
                LastHeartbeatBody = body;
            }
            else if (path.EndsWith("/pc/tracker/upload", StringComparison.Ordinal))
            {
                TrackerUploadBodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"message":"","data":0}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
