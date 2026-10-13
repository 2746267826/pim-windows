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
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计整改 round5 · REQ-COM-02 / REQ-COM-06（F-10）：从**不被编译的孤儿文件**
/// <c>Wo398SuspendGapTests.cs</c> 迁移出来的 2 条**只依赖客户端类型**的用例
/// （40 分钟静默补齐、suspend→resume 补齐整段）。
///
/// round4 的独立 review（I-1）指出该文件被整文件记为“0 条可移植”是不准确的：
/// 行 66 / 93 这两条只用 <see cref="NativeTrackerService"/> 与文件内测试替身，
/// `using Pim.Core.Invariants;` 只是让**其它 3 条**（后端判据）不可编译。
/// 分母因此是 16 = 可移植 10 / 后端依赖 6；那 3 条后端用例的归属与真实验证条件见
/// <c>tests/ClientWindows/BACKEND-OWNED-CASES.md</c>（保持 NOT-VERIFIED）。
///
/// 断言与孤儿文件逐字相同（固定时钟注入、真实队列、桩 handler 捕获真实上行字节）。
/// </summary>
public sealed class SuspendGapClientTests : IDisposable
{
    private readonly string _stateDir;
    private readonly string _stateFile;
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 5, 0, 0, TimeSpan.Zero);

    public SuspendGapClientTests()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), "pim-wo398-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDir);
        _stateFile = Path.Combine(_stateDir, "tracker_state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_stateDir, recursive: true); } catch { }
    }

    private (NativeTrackerService Tracker, RecordingUploadHandler Handler, MutableTimeProvider Clock) CreateTracker()
    {
        var clock = new MutableTimeProvider(T0);
        var handler = new RecordingUploadHandler();
        // 桩 handler 拦截全部请求，无需真实网络（也不调用 SetBaseUrl：那会重建 HttpClient 并销毁桩）。
        var api = new ApiClient(() => handler);
        var config = new TrackerConfig { Enabled = true, GapThresholdSeconds = 60, UploadBatchSize = 500 };
        var logger = new TrackerLogger(1);
        var tracker = new NativeTrackerService(
            api,
            config,
            windowResolver: new NullWindowResolver(),
            idleDetector: new NullIdleDetector(),
            bridge: new BrowserBridgeService(15699, logger),
            logger: logger,
            stateManager: new TrackerStateManager(_stateFile),
            timeProvider: clock);
        return (tracker, handler, clock);
    }

    [Fact]
    public async Task Ac4_4_AliveButQuietFor40Minutes_WithoutDeclaration_EmitsGapChunksForTheWholeInterval()
    {
        var (tracker, handler, clock) = CreateTracker();
        using var _ = tracker;

        await tracker.DoPollAsync();                 // 建立基线 _lastPollTime
        clock.Advance(TimeSpan.FromMinutes(40));     // 进程活着，但 40 分钟没有任何事件
        await tracker.DoPollAsync();                 // 必须自己补齐这一段
        await tracker.FlushQueueAsync(TimeSpan.FromSeconds(5));

        var gaps = handler.AllGapEvents();
        Assert.NotEmpty(gaps);

        // 分片必须首尾相接、完整覆盖 [T0, T0+40min]（30 分钟一片，末片可短）。
        var ordered = gaps.OrderBy(g => g.Start).ToList();
        Assert.Equal(T0, ordered[0].Start);
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].End, ordered[i].Start);
        }
        Assert.Equal(T0.AddMinutes(40), ordered[^1].End);

        // 粒度维持现状：每片 ≤30 分钟。
        Assert.All(ordered, g => Assert.True(g.End - g.Start <= TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public async Task Ac4_4_SuspendThenResume_CoversTheWholeSleepIntervalWithGapChunks()
    {
        // issue #398 的现象：休眠 ≥30 分钟后唤醒，整段休眠必须留下 gap 分片。
        var (tracker, handler, clock) = CreateTracker();
        using var _ = tracker;

        await tracker.DoPollAsync();
        tracker.HandleSuspend();                     // 休眠前：关会话、存状态、刷队列
        handler.Uploads.Clear();

        clock.Advance(TimeSpan.FromMinutes(42));     // 休眠 42 分钟
        tracker.HandleResume();                      // 唤醒：必须补齐整段
        await tracker.FlushQueueAsync(TimeSpan.FromSeconds(5));

        var gaps = handler.AllGapEvents().OrderBy(g => g.Start).ToList();
        Assert.NotEmpty(gaps);
        Assert.Equal(T0, gaps[0].Start);                  // 休眠起点 = 休眠前最后一次轮询时刻
        Assert.Equal(T0.AddMinutes(42), gaps[^1].End);    // 一直覆盖到唤醒时刻
        for (int i = 1; i < gaps.Count; i++)
        {
            Assert.Equal(gaps[i - 1].End, gaps[i].Start);
        }
    }

    // ---- 测试替身（与孤儿文件逐字相同） ------------------------------------------

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

    private sealed class RecordingUploadHandler : HttpMessageHandler
    {
        private readonly List<string> _bodies = new();
        public IReadOnlyList<string> Bodies { get { lock (_bodies) return _bodies.ToList(); } }
        public List<string> Uploads { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_bodies) _bodies.Add(body);
            if (request.RequestUri!.AbsolutePath.EndsWith("/pc/tracker/upload", StringComparison.Ordinal))
            {
                lock (_bodies) Uploads.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0,\"message\":\"\",\"data\":0}", Encoding.UTF8, "application/json")
            };
        }

        /// <summary>把所有已上传的请求体里 event_type=gap 的分片解析出来（start/end 来自 raw_json.gapStart/gapEnd）。</summary>
        public List<(DateTimeOffset Start, DateTimeOffset End)> AllGapEvents()
        {
            var result = new List<(DateTimeOffset, DateTimeOffset)>();
            foreach (var body in Bodies)
            {
                if (string.IsNullOrWhiteSpace(body)) continue;
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("events", out var events)) continue;
                foreach (var ev in events.EnumerateArray())
                {
                    if (!ev.TryGetProperty("eventType", out var type) || type.GetString() != "gap") continue;
                    var raw = ev.GetProperty("rawJson");
                    if (raw.ValueKind == JsonValueKind.String)
                    {
                        using var rawDoc = JsonDocument.Parse(raw.GetString()!);
                        result.Add((rawDoc.RootElement.GetProperty("gapStart").GetDateTimeOffset(),
                                    rawDoc.RootElement.GetProperty("gapEnd").GetDateTimeOffset()));
                    }
                    else
                    {
                        result.Add((raw.GetProperty("gapStart").GetDateTimeOffset(),
                                    raw.GetProperty("gapEnd").GetDateTimeOffset()));
                    }
                }
            }

            return result;
        }
    }
}
