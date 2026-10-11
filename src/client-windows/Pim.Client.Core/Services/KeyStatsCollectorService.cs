using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Pim.Client.Core.Models;

namespace Pim.Client.Core.Services;

public sealed class KeyStatsCollectorService : IDisposable
{
    private static readonly string KeyStatsBase = ClientDefaults.KeyStatsBaseUrl;

    private readonly HttpClient _keyStats;
    private readonly ApiClient _api;
    private readonly Func<int, IReadOnlyList<KeyStatsProcessInfo>> _processSource;
    private readonly CancellationTokenSource _cts = new();
    private static readonly object LockObj = new();
    private DateTime? _lastUploadTime;
    private string? _lastUploadError;
    private KeyStatsHealthResult? _lastHealth;
    private string? _lastSkipReason;
    private KeyStatsCounterSnapshot? _previousSnapshot;

    public Action<string>? Log { get; set; }
    public DateTime? LastUploadTime { get { lock (LockObj) return _lastUploadTime; } }
    public string? LastUploadError { get { lock (LockObj) return _lastUploadError; } }
    public KeyStatsHealthResult? LastHealth { get { lock (LockObj) return _lastHealth; } }
    public string? LastSkipReason { get { lock (LockObj) return _lastSkipReason; } }

    public static bool ShouldUpload(KeyStatsHealthResult health) => health.CanUpload;

    public KeyStatsCollectorService(ApiClient api, KeyStatsProcessManager? processManager = null)
        : this(api, processManager, keyStatsHandlerFactory: null, processSource: null)
    {
    }

    /// <summary>
    /// 测试接缝（审计 F-02 / F-10）：允许注入本地 KeyStats HTTP 传输与进程枚举，
    /// 从而在 Linux 上驱动<b>与定时器/SyncNowAsync 完全相同</b>的采集→上传路径，
    /// 捕获生产代码自己发出的 method / 路径 / 请求体。
    ///
    /// 生产调用只走上面的公开构造：两个可空参数为 null 时行为与注入前逐字一致
    /// （真实 <see cref="HttpClientHandler"/> 与 <see cref="KeyStatsProcessManager.ListProcesses"/>）。
    /// </summary>
    internal KeyStatsCollectorService(
        ApiClient api,
        KeyStatsProcessManager? processManager,
        Func<HttpMessageHandler>? keyStatsHandlerFactory,
        Func<int, IReadOnlyList<KeyStatsProcessInfo>>? processSource)
    {
        _api = api;
        _processSource = processSource ?? (processManager ?? new KeyStatsProcessManager()).ListProcesses;
        _keyStats = new HttpClient(keyStatsHandlerFactory?.Invoke() ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(KeyStatsBase),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// 测试接缝：同步执行一次真实的「读取本地统计 → 健康判定 → 上传」流程。
    /// 与 <see cref="Start"/> / <see cref="SyncNowAsync"/> 调用的是同一个私有方法，
    /// 路径与请求体由产品代码决定，测试不传入任何端点。
    /// </summary>
    internal Task RunCollectionCycleAsync() => CollectAndUploadAsync();

    public void Start()
    {
        Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                await CollectAndUploadAsync();

                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    await CollectAndUploadAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, _cts.Token);
    }

    public async Task SyncNowAsync()
    {
        try
        {
            await CollectAndUploadAsync();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[KeyStatsCollector] Manual sync error: {ex.Message}");
        }
    }

    private async Task CollectAndUploadAsync()
    {
        try
        {
            var stats = await _keyStats.GetFromJsonAsync<KeyStatsSnapshot>("/api/stats/", _cts.Token);
            if (stats is null) return;

            var snapshot = new KeyStatsCounterSnapshot(
                stats.KeyPresses,
                stats.LeftClicks,
                stats.RightClicks,
                stats.MiddleClicks,
                stats.SideBackClicks,
                stats.SideForwardClicks,
                stats.MouseDistance,
                stats.ScrollDistance);

            KeyStatsCounterSnapshot? previous;
            lock (LockObj) { previous = _previousSnapshot; }

            var currentSessionId = Process.GetCurrentProcess().SessionId;
            var processes = _processSource(currentSessionId);
            var health = KeyStatsHealthProbe.Evaluate(
                processes,
                currentSessionId,
                snapshot,
                previous,
                apiError: null);

            lock (LockObj)
            {
                _previousSnapshot = snapshot;
                _lastHealth = health;
                _lastSkipReason = health.SkipReason;
            }

            if (!ShouldUpload(health))
            {
                lock (LockObj) { _lastUploadError = health.SummaryZh; }
                Log?.Invoke($"[KeyStatsCollector] Skip upload: {health.SummaryZh} (reason: {health.SkipReason})");
                return;
            }

            var sample = new KeystatsSampleUploadPayload(
                stats.DeviceId,
                DateTimeOffset.UtcNow.ToString("O"),
                stats.Date,
                stats.KeyPresses,
                stats.KeyPressCounts,
                stats.LeftClicks,
                stats.RightClicks,
                stats.MiddleClicks,
                stats.SideBackClicks,
                stats.SideForwardClicks,
                stats.MouseDistance,
                stats.ScrollDistance,
                stats.PeakKps,
                stats.PeakCps,
                stats.FormattedMouseDistance,
                stats.FormattedScrollDistance,
                stats.AppStats);

            // 审计 F-02：legacy 上传体必须与 contract KeystatsUploadRequest 一致
            // （additionalProperties=false；字段名是 peakKps/peakCps，没有 formatted*）。
            // 这里用独立的命名类型，而不是给 KeyStatsSnapshot 加 [JsonIgnore]：
            // 那个类同时是本地 keyStats /api/stats/ 的**读取**类型，JsonIgnore 会把读入
            // 一起关掉，使 samples 上传的 formatted* 值静默变 null（行为回退）。
            var legacy = new KeystatsLegacyUploadPayload(
                stats.DeviceId,
                stats.Date,
                stats.KeyPresses,
                stats.KeyPressCounts,
                stats.LeftClicks,
                stats.RightClicks,
                stats.MiddleClicks,
                stats.SideBackClicks,
                stats.SideForwardClicks,
                stats.MouseDistance,
                stats.ScrollDistance,
                stats.PeakKps,
                stats.PeakCps,
                stats.AppStats);

            var sampleResult = await TryPostAsync("/pc/keystats/samples", sample);
            var legacyResult = await TryPostAsync("/pc/keystats/upload", legacy);
            var healthMessage = BuildUploadHealthMessage(sampleResult is not null, legacyResult is not null);

            if (sampleResult is not null || legacyResult is not null)
            {
                lock (LockObj)
                {
                    _lastUploadTime = DateTime.Now;
                    _lastUploadError = healthMessage;
                }
                Log?.Invoke($"[KeyStatsCollector] Uploaded snapshot for {stats.Date} (sample: {FormatUploadResult(sampleResult)}, legacy: {FormatUploadResult(legacyResult)})");
            }
            else
            {
                lock (LockObj) { _lastUploadError = healthMessage; }
                Log?.Invoke($"[KeyStatsCollector] {healthMessage}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            lock (LockObj) { _lastUploadError = ex.Message; }
            Log?.Invoke($"[KeyStatsCollector] Unavailable: {ex.Message}");
        }
        catch (Exception ex)
        {
            lock (LockObj) { _lastUploadError = ex.Message; }
            Log?.Invoke($"[KeyStatsCollector] Error: {ex.Message}");
        }
    }

    private async Task<ApiResponse<string>?> TryPostAsync(string endpoint, object payload)
    {
        try
        {
            return await _api.PostAsync<ApiResponse<string>>(endpoint, payload, _cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log?.Invoke($"[KeyStatsCollector] Upload to {endpoint} failed: {ex.Message}");
            return null;
        }
    }

    private static string FormatUploadResult(ApiResponse<string>? result)
    {
        return result is null ? "failed" : "ok";
    }

    private static string? BuildUploadHealthMessage(bool sampleOk, bool legacyOk)
    {
        return (sampleOk, legacyOk) switch
        {
            (true, true) => null,
            (true, false) => "Sample ok; legacy upload failed",
            (false, true) => "Sample upload failed; legacy ok",
            _ => "Both sample and legacy uploads returned null response"
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _keyStats.Dispose();
    }

    private sealed class KeyStatsSnapshot
    {
        [JsonPropertyName("deviceId")]
        public string DeviceId { get; set; } = Environment.MachineName;

        [JsonPropertyName("date")]
        public string Date { get; set; } = DateTimeOffset.Now.ToString("O");

        [JsonPropertyName("keyPresses")]
        public int KeyPresses { get; set; }

        [JsonPropertyName("keyPressCounts")]
        public Dictionary<string, int>? KeyPressCounts { get; set; }

        [JsonPropertyName("leftClicks")]
        public int LeftClicks { get; set; }

        [JsonPropertyName("rightClicks")]
        public int RightClicks { get; set; }

        [JsonPropertyName("middleClicks")]
        public int MiddleClicks { get; set; }

        [JsonPropertyName("sideBackClicks")]
        public int SideBackClicks { get; set; }

        [JsonPropertyName("sideForwardClicks")]
        public int SideForwardClicks { get; set; }

        [JsonPropertyName("mouseDistance")]
        public double MouseDistance { get; set; }

        [JsonPropertyName("scrollDistance")]
        public double ScrollDistance { get; set; }

        [JsonPropertyName("formattedMouseDistance")]
        public string? FormattedMouseDistance { get; set; }

        [JsonPropertyName("formattedScrollDistance")]
        public string? FormattedScrollDistance { get; set; }

        [JsonPropertyName("peakKPS")]
        public int PeakKps { get; set; }

        [JsonPropertyName("peakCPS")]
        public int PeakCps { get; set; }

        [JsonPropertyName("appStats")]
        public Dictionary<string, KeyStatsAppStats>? AppStats { get; set; }
    }

    private sealed class KeyStatsAppStats
    {
        public string AppName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int KeyPresses { get; set; }
        public int LeftClicks { get; set; }
        public int RightClicks { get; set; }
        public int MiddleClicks { get; set; }
        public int SideBackClicks { get; set; }
        public int SideForwardClicks { get; set; }
        public double ScrollDistance { get; set; }
    }

    /// <summary>
    /// legacy 上传体（审计 F-02）：字段集合与 JSON 名严格取自
    /// <c>contract/openapi.json</c> → <c>KeystatsUploadRequest</c>
    /// （<c>additionalProperties=false</c>）。
    ///
    /// 不含 <c>formattedMouseDistance</c> / <c>formattedScrollDistance</c>（契约未声明），
    /// peak 字段名是 <c>peakKps</c> / <c>peakCps</c> —— 注意与 samples 的
    /// <c>peakKPS</c> / <c>peakCPS</c> 不同，两个端点各自有自己的 schema。
    /// 本地 keyStats 侧的 formatted* 值仍被读取并使用（samples 上传带值）。
    /// </summary>
    private sealed record KeystatsLegacyUploadPayload(
        string DeviceId,
        string Date,
        int KeyPresses,
        Dictionary<string, int>? KeyPressCounts,
        int LeftClicks,
        int RightClicks,
        int MiddleClicks,
        int SideBackClicks,
        int SideForwardClicks,
        double MouseDistance,
        double ScrollDistance,
        [property: JsonPropertyName("peakKps")]
        int PeakKps,
        [property: JsonPropertyName("peakCps")]
        int PeakCps,
        Dictionary<string, KeyStatsAppStats>? AppStats
    );

    private sealed record KeystatsSampleUploadPayload(
        string PimDeviceId,
        string SampledAt,
        string Date,
        int KeyPresses,
        Dictionary<string, int>? KeyPressCounts,
        int LeftClicks,
        int RightClicks,
        int MiddleClicks,
        int SideBackClicks,
        int SideForwardClicks,
        double MouseDistance,
        double ScrollDistance,
        [property: JsonPropertyName("peakKPS")]
        int PeakKps,
        [property: JsonPropertyName("peakCPS")]
        int PeakCps,
        [property: JsonPropertyName("formattedMouseDistance")]
        string? FormattedMouseDistance,
        [property: JsonPropertyName("formattedScrollDistance")]
        string? FormattedScrollDistance,
        Dictionary<string, KeyStatsAppStats>? AppStats
    );
}
