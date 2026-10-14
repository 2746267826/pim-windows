using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

/// <summary>
/// 端点心跳的**观测输入**：只放客户端真实持有的值，不放任何"希望服务端看到"的常量。
///
/// <para>
/// 刻意不包含时间戳：<c>last_heartbeat_at</c> 由服务端按**收到请求的时刻**写入 ——
/// 客户端既不发送、也无法伪造"最后一次活着的时间"。
/// </para>
/// </summary>
/// <param name="DeviceId">设备标识。必须与其余接口同源（见 <see cref="ForLocalMachine"/>）。</param>
/// <param name="AppVersion">客户端版本，取自当前运行的程序集。</param>
/// <param name="LastUploadError">最近一次采集上传的错误（无错误为 null）。</param>
/// <param name="HasCompletedUpload">本会话是否**成功上传过**至少一批采集数据。</param>
/// <param name="CollectionCacheCount">本地已采集但尚未上传成功的条数；没有对应数据时为 null。</param>
public sealed record EndpointHeartbeatSnapshot(
    string DeviceId,
    string? AppVersion,
    string? LastUploadError,
    bool HasCompletedUpload,
    int? CollectionCacheCount);

/// <summary>
/// 端点心跳上报（工单 06 / G1）：<c>POST /api/v1/endpoints/{deviceId}/heartbeat</c>。
///
/// <para>
/// 端点页（<c>GET /api/v1/endpoints</c>）与「端点外壳」调试页的数据**只**来自这张表，
/// 而在此之前没有任何客户端在生产路径上调过它，于是页面永远是空的。本类是 Windows 侧的
/// 唯一上报入口，由守护程序既有的心跳循环每 2 分钟驱动一次（不新增定时器、不新增服务）。
/// </para>
///
/// <para>
/// 失败**不静默**：连续失败次数、最后一次错误、最后一次成功时刻都保留在本对象上，
/// 由调用方写进守护程序日志与状态窗口（G1.3）。
/// </para>
/// </summary>
public sealed class EndpointHeartbeatReporter
{
    /// <summary>契约 <c>platform</c>：服务端只认 <c>android</c> / <c>windows</c>，其余一律落回 windows。</summary>
    public const string WindowsPlatform = "windows";

    /// <summary>服务端与 pim-web 端点页在用的上传状态词表（<c>Healthy</c> 绿，其余黄）。</summary>
    public const string HealthyStatus = "Healthy";
    public const string WarningStatus = "Warning";

    /// <summary>契约默认状态：没有可信观测时不臆造"健康"。</summary>
    public const string UnknownStatus = "Unknown";

    private readonly ApiClient _api;
    private readonly TrackerLogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _lastSuccessAt;
    private string? _lastError;
    private string? _lastUploadStatus;
    private int _consecutiveFailures;

    public EndpointHeartbeatReporter(
        ApiClient api,
        TrackerLogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        _api = api;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>最近一次**发起**上报的时刻（成功或失败都算）。</summary>
    public DateTimeOffset? LastAttemptAt { get { lock (_lock) return _lastAttemptAt; } }

    /// <summary>最近一次**成功**上报的时刻；从未成功过为 null。</summary>
    public DateTimeOffset? LastSuccessAt { get { lock (_lock) return _lastSuccessAt; } }

    /// <summary>最近一次失败原因（含 HTTP 状态码）；最近一次成功后清空。</summary>
    public string? LastError { get { lock (_lock) return _lastError; } }

    /// <summary>连续失败次数；任意一次成功后归零。</summary>
    public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }

    /// <summary>最近一次**成功**上报出去的 <c>uploadStatus</c>；失败不会改写它。</summary>
    public string? LastReportedUploadStatus { get { lock (_lock) return _lastUploadStatus; } }

    /// <summary>
    /// 本机快照：设备标识固定取 <see cref="Environment.MachineName"/> ——
    /// 守护程序的守护进程心跳（<c>/api/v1/daemon/heartbeat</c>）、轨迹上传
    /// （<c>/api/v1/pc/tracker/upload</c>）与站点事件上传（<c>/api/v1/pc/browser-tt/upload</c>）
    /// 用的都是它。端点页的另一条唯一键是 <c>(user_id, device_id)</c>，
    /// 新造一个 ID 会让同一台机器在页面上变成两台设备（AC-4）。
    /// </summary>
    public static EndpointHeartbeatSnapshot ForLocalMachine(
        string? appVersion,
        string? lastUploadError,
        bool hasCompletedUpload,
        int? collectionCacheCount)
        => new(
            Environment.MachineName,
            appVersion,
            lastUploadError,
            hasCompletedUpload,
            collectionCacheCount);

    /// <summary>
    /// 上传状态口径（服务端只在两种取值上做判断：<c>Healthy</c>/<c>Unknown</c> 不算问题，其余算问题）：
    /// <list type="bullet">
    ///   <item><description>有未清除的上传错误 → <c>Warning</c>（正在失败，必须让人看见）；</description></item>
    ///   <item><description>成功上传过、当前无错误 → <c>Healthy</c>；</description></item>
    ///   <item><description>还没成功上传过（例如刚启动、或一直没拿到令牌）→ <c>Unknown</c>，
    ///     不把"还没开始"写成"健康"。</description></item>
    /// </list>
    /// 积压条数走独立的 <see cref="EndpointHeartbeatSnapshot.CollectionCacheCount"/>，不重复编码进状态串。
    /// </summary>
    public static string ResolveUploadStatus(string? lastUploadError, bool hasCompletedUpload)
    {
        if (!string.IsNullOrWhiteSpace(lastUploadError))
        {
            return WarningStatus;
        }

        return hasCompletedUpload ? HealthyStatus : UnknownStatus;
    }

    /// <summary>本机观测 → 契约请求体。只填契约里存在的四个字段。</summary>
    public static EndpointHeartbeatRequest BuildRequest(EndpointHeartbeatSnapshot snapshot) => new()
    {
        Platform = WindowsPlatform,
        AppVersion = snapshot.AppVersion,
        UploadStatus = ResolveUploadStatus(snapshot.LastUploadError, snapshot.HasCompletedUpload),
        CollectionCacheCount = snapshot.CollectionCacheCount,
    };

    /// <summary>
    /// 上报一次。**不抛异常**（调用方是周期循环）：失败返回 false 并把原因记在本对象上。
    /// 只有调用方主动取消时才把 <see cref="OperationCanceledException"/> 抛回去。
    /// </summary>
    public async Task<bool> ReportAsync(EndpointHeartbeatSnapshot snapshot, CancellationToken ct = default)
    {
        var request = BuildRequest(snapshot);
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            _lastAttemptAt = now;
        }

        try
        {
            await _api.ReportEndpointHeartbeatAsync(snapshot.DeviceId, request, ct).ConfigureAwait(false);

            int previousFailures;
            lock (_lock)
            {
                previousFailures = _consecutiveFailures;
                _consecutiveFailures = 0;
                _lastError = null;
                _lastSuccessAt = now;
                _lastUploadStatus = request.UploadStatus;
            }

            if (previousFailures > 0)
            {
                // 恢复也要留痕：否则"曾经连续失败过"只存在于上一次的告警里。
                _logger?.Info(
                    "EndpointHeartbeat",
                    $"Endpoint heartbeat recovered after {previousFailures} consecutive failure(s)");
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = Describe(ex);
            int streak;
            lock (_lock)
            {
                streak = ++_consecutiveFailures;
                _lastError = detail;
            }

            _logger?.Warn(
                "EndpointHeartbeat",
                $"Endpoint heartbeat failed (consecutive {streak}) device={snapshot.DeviceId}: {detail}");
            return false;
        }
    }

    /// <summary>把失败原因说清楚：HTTP 状态码要留住，否则只剩一句"请求失败"。</summary>
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } http =>
            $"HTTP {(int)status} {status}: {http.Message}",
        HttpRequestException http => $"transport: {http.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
