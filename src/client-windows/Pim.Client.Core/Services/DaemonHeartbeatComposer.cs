using Pim.Client.Core.Models;

namespace Pim.Client.Core.Services;

/// <summary>
/// 守护进程心跳请求的装配逻辑。
///
/// 审计整改（阶段 A）测试设施：本类的内容原样来自
/// <c>src/client-windows/Pim.Client.App/App.xaml.cs</c> 的 <c>ReportHeartbeatOnceAsync</c>，
/// 是一次**行为不变**的搬运，原因是那个装配点位于 <c>net8.0-windows</c> 的 WPF 工程里，
/// Linux/CI 上无法执行 —— 于是「心跳里上报的队列积压数是否真实」这类字段语义
/// 在整仓没有任何能跑的测试（审计 F-04 / F-10）。
///
/// 搬运到 <c>Pim.Client.Core</c>（net8.0，跨平台）后，<c>tests/Pim.Client.Tests</c>
/// 可以在 Linux 与每次 PR 上真实执行它。除时钟改为可注入（<see cref="TimeProvider"/>，
/// 默认 <see cref="TimeProvider.System"/>，与原来的 <c>DateTime.UtcNow</c> 等价）外，
/// 取值与分支与搬运前逐字一致。
/// </summary>
public static class DaemonHeartbeatComposer
{
    /// <summary>
    /// 按当前采集端状态装配一条心跳请求。
    /// </summary>
    /// <param name="tracker">前台采集服务；未启用时为 null（与搬迁前一致）。</param>
    /// <param name="keyStats">键鼠统计服务（必填，调用方从 DI 取）。</param>
    /// <param name="serverUrl">配置的服务端地址。</param>
    /// <param name="version">对外程序集版本号。</param>
    /// <param name="clock">时钟；测试注入固定时钟。</param>
    public static DaemonHeartbeatRequest Build(
        NativeTrackerService? tracker,
        KeyStatsCollectorService keyStats,
        string serverUrl,
        string version,
        TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var now = clock.GetUtcNow();

        var trackerState = tracker is null ? "Unknown"
            : tracker.LastError is null && tracker.EventsUploaded > 0 ? "Available"
            : tracker.LastError is null ? "Unknown" : "Unavailable";
        var ksHealth = keyStats.LastHealth;
        var ksState = ksHealth?.DaemonSourceState ?? "Unknown";

        var lastSuccess = MaxTime(
            tracker?.EventsUploaded > 0 ? now.UtcDateTime : null,
            keyStats.LastUploadTime?.ToUniversalTime());
        var lastSuccessUtc = lastSuccess is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;
        var lastError = tracker?.LastError ?? keyStats.LastUploadError;

        return DaemonHeartbeatReporter.BuildHeartbeat(
            Environment.MachineName,
            version,
            serverUrl,
            lastSuccessUtc,
            now,
            lastError,
            tracker?.PendingUploadCount ?? 0,
            trackerState,
            ksState,
            new
            {
                keyStatsDetailState = ksHealth?.DetailState.ToString(),
                keyStatsProcessCount = ksHealth?.ProcessCount,
                keyStatsSkipReason = keyStats.LastSkipReason,
                trackerPollCount = tracker?.PollCount ?? 0,
                trackerSessionsCreated = tracker?.SessionsCreated ?? 0,
                trackerEventsUploaded = tracker?.EventsUploaded ?? 0,
                trackerHookActive = tracker?.HookActive ?? false,
                trackerBrowserConnected = tracker?.BrowserConnected ?? false
            });
    }

    private static DateTime? MaxTime(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }
}
