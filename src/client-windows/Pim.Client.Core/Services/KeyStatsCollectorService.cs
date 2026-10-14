using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models;

namespace Pim.Client.Core.Services;

public sealed class KeyStatsCollectorService : IDisposable
{
    private static readonly string KeyStatsBase = ClientDefaults.KeyStatsBaseUrl;

    private readonly HttpClient _keyStats;
    private readonly ApiClient _api;
    private readonly KeyStatsProcessManager _processManager;
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
    {
        _api = api;
        _processManager = processManager ?? new KeyStatsProcessManager();
        _keyStats = new HttpClient
        {
            BaseAddress = new Uri(KeyStatsBase),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

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
            var stats = await _keyStats.GetFromJsonAsync<KeyStatsLocalSnapshot>("/api/stats/", _cts.Token);
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
            var processes = _processManager.ListProcesses(currentSessionId);
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

            // 上报用的两个请求体都是**契约生成类型**：字段名/类型/必填性来自 contract/openapi.json。
            // 契约曾让这两条路径的 peak 字段拼写不一致（samples 用 peakKPS/peakCPS，upload 用
            // peakKps/peakCps）；pim-api#419 已统一为 camelCase，生成物随之更新 —— 客户端照生成物写，
            // 不自己发明大小写。
            var wireAppStats = ToWireAppStats(stats.AppStats);
            var sample = new KeystatsSampleUploadRequest
            {
                PimDeviceId = stats.DeviceId,
                SampledAt = DateTimeOffset.UtcNow.ToString("O"),
                Date = stats.Date,
                KeyPresses = stats.KeyPresses,
                KeyPressCounts = stats.KeyPressCounts,
                LeftClicks = stats.LeftClicks,
                RightClicks = stats.RightClicks,
                MiddleClicks = stats.MiddleClicks,
                SideBackClicks = stats.SideBackClicks,
                SideForwardClicks = stats.SideForwardClicks,
                MouseDistance = stats.MouseDistance,
                ScrollDistance = stats.ScrollDistance,
                PeakKps = stats.PeakKps,
                PeakCps = stats.PeakCps,
                FormattedMouseDistance = stats.FormattedMouseDistance,
                FormattedScrollDistance = stats.FormattedScrollDistance,
                AppStats = wireAppStats,
            };

            var legacy = new KeystatsUploadRequest
            {
                DeviceId = stats.DeviceId,
                Date = stats.Date,
                KeyPresses = stats.KeyPresses,
                KeyPressCounts = stats.KeyPressCounts,
                LeftClicks = stats.LeftClicks,
                RightClicks = stats.RightClicks,
                MiddleClicks = stats.MiddleClicks,
                SideBackClicks = stats.SideBackClicks,
                SideForwardClicks = stats.SideForwardClicks,
                MouseDistance = stats.MouseDistance,
                ScrollDistance = stats.ScrollDistance,
                PeakKps = stats.PeakKps,
                PeakCps = stats.PeakCps,
                AppStats = wireAppStats,
            };

            var sampleResult = await TryUploadSampleAsync(sample);
            var legacyResult = await TryUploadLegacyAsync(legacy);
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

    private async Task<StringApiResponse?> TryUploadSampleAsync(KeystatsSampleUploadRequest payload)
    {
        try
        {
            return await _api.UploadKeystatsSampleAsync(payload, _cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log?.Invoke($"[KeyStatsCollector] Upload to {ContractOperation.UploadKeystatsSample.PathTemplate} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<StringApiResponse?> TryUploadLegacyAsync(KeystatsUploadRequest payload)
    {
        try
        {
            return await _api.UploadKeystatsSnapshotAsync(payload, _cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log?.Invoke($"[KeyStatsCollector] Upload to {ContractOperation.UploadKeystatsSnapshot.PathTemplate} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>本机 keystats 服务的 appStats 形状 → 契约生成的 <see cref="AppStatEntry"/>。</summary>
    private static Dictionary<string, AppStatEntry>? ToWireAppStats(Dictionary<string, KeyStatsAppStats>? appStats)
    {
        if (appStats is null)
        {
            return null;
        }

        var result = new Dictionary<string, AppStatEntry>(appStats.Count, StringComparer.Ordinal);
        foreach (var (key, value) in appStats)
        {
            result[key] = new AppStatEntry
            {
                AppName = value.AppName,
                DisplayName = value.DisplayName,
                KeyPresses = value.KeyPresses,
                LeftClicks = value.LeftClicks,
                RightClicks = value.RightClicks,
                MiddleClicks = value.MiddleClicks,
                SideBackClicks = value.SideBackClicks,
                SideForwardClicks = value.SideForwardClicks,
                ScrollDistance = value.ScrollDistance,
            };
        }

        return result;
    }

    private static string FormatUploadResult(StringApiResponse? result)
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

    /// <summary>
    /// 本机 keystats 服务（<see cref="ClientDefaults.KeyStatsBaseUrl"/>，不是 pim-api）返回的快照形状。
    /// 它的 <c>peakKPS</c>/<c>peakCPS</c> 是本机服务自己的写法，与 pim-api 契约无关；
    /// 上线前由本类逐字段映射到契约生成的请求类型。
    /// </summary>
    private sealed class KeyStatsLocalSnapshot
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

}
