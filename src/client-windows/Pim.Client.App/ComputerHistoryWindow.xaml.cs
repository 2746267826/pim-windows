using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class ComputerHistoryWindow : Window
{
    private readonly TrackerHistoryStore _store;
    private readonly RemoteComputerHistoryService _remoteHistory;
    private readonly ApiClient _api;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private long _localQueryGeneration;
    private long _remoteQueryGeneration;
    private bool _updatingRemoteSelectors;
    private bool _remoteQueryInProgress;
    private bool _componentsInitialized;
    private int _remotePage = 1;
    private int _remoteAvailablePage;

    public ComputerHistoryWindow()
        : this(
            App.Services.GetRequiredService<TrackerHistoryStore>(),
            App.Services.GetRequiredService<RemoteComputerHistoryService>(),
            App.Services.GetRequiredService<ApiClient>())
    {
    }

    internal ComputerHistoryWindow(
        TrackerHistoryStore store,
        RemoteComputerHistoryService remoteHistory,
        ApiClient api)
    {
        InitializeComponent();
        _componentsInitialized = true;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _remoteHistory = remoteHistory ?? throw new ArgumentNullException(nameof(remoteHistory));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _api.ExternalScopeChanged += OnExternalScopeChanged;
        RemotePolicyText.Text =
            $"只查询当前账号可见的服务器记录；一次最多 31 天，最多 {RemoteComputerHistoryService.MaximumPages} 页 × "
            + $"每页 {RemoteComputerHistoryService.PageSize} 条（{RemoteComputerHistoryService.MaximumRows:N0} 条）。"
            + "不会补发上传或打开 URL。空结果仅表示本次筛选未返回数据，不证明设备采集或上传完整。";
        RangeBox.ItemsSource = new[]
        {
            new RangeChoice("今天", 1),
            new RangeChoice("近 7 天", 7),
            new RangeChoice("近 30 天", 30),
        };
        CategoryBox.ItemsSource = new[]
        {
            new CategoryChoice("全部类别", null),
            new CategoryChoice("开发", TrackerActivityCategory.Development),
            new CategoryChoice("办公", TrackerActivityCategory.Office),
            new CategoryChoice("沟通", TrackerActivityCategory.Communication),
            new CategoryChoice("浏览", TrackerActivityCategory.Browsing),
            new CategoryChoice("媒体", TrackerActivityCategory.Media),
            new CategoryChoice("空闲", TrackerActivityCategory.Idle),
            new CategoryChoice("其他", TrackerActivityCategory.Other),
        };
        RangeBox.SelectedIndex = 1;
        CategoryBox.SelectedIndex = 0;
        var today = DateTime.Today;
        RemoteFromBox.SelectedDate = today.AddDays(-6);
        RemoteToBox.SelectedDate = today;
        RemoteTypeBox.ItemsSource = new[]
        {
            new RemoteTypeChoice("全部类型", null),
            new RemoteTypeChoice("电脑窗口", "window"),
            new RemoteTypeChoice("浏览器使用", "web"),
            new RemoteTypeChoice("网页明细", "web-page"),
        };
        RemoteTypeBox.SelectedIndex = 0;
        RemoteDeviceBox.ItemsSource = new[] { new RemoteDeviceChoice("全部设备", null) };
        RemoteDeviceBox.SelectedIndex = 0;
        _refreshTimer.Tick += OnTimerTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        await RefreshAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _api.ExternalScopeChanged -= OnExternalScopeChanged;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await RefreshAsync();
        }
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await RefreshAsync();
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
        => await RefreshAsync();

    private async void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, HistoryModeTabs)
            || !ReferenceEquals(sender, HistoryModeTabs))
            return;

        ShowSelectedHistoryDetail();
        if (!IsLoaded)
            return;

        if (HistoryModeTabs.SelectedIndex != 1)
            return;

        if (RemoteDeviceBox.Items.Count <= 1)
            await LoadRemoteDevicesAsync();
        else
            await QueryRemoteAsync();
    }

    private async void OnRemoteQuery(object sender, RoutedEventArgs e)
    {
        _remotePage = 1;
        await QueryRemoteAsync();
    }

    private async void OnRemoteDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_updatingRemoteSelectors && HistoryModeTabs.SelectedIndex == 1)
        {
            _remotePage = 1;
            await QueryRemoteAsync();
        }
    }

    private async void OnRemoteFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_updatingRemoteSelectors && HistoryModeTabs.SelectedIndex == 1)
        {
            _remotePage = 1;
            await QueryRemoteAsync();
        }
    }

    private async void OnPreviousPage(object sender, RoutedEventArgs e)
    {
        if (_remoteQueryInProgress || _remotePage <= 1)
            return;

        _remotePage--;
        await QueryRemoteAsync();
    }

    private async void OnNextPage(object sender, RoutedEventArgs e)
    {
        if (_remoteQueryInProgress || _remotePage >= _remoteAvailablePage)
            return;

        _remotePage++;
        await QueryRemoteAsync();
    }

    private async void OnExternalScopeChanged()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Increment(ref _remoteQueryGeneration);
            _updatingRemoteSelectors = true;
            try
            {
                RemoteHistoryList.ItemsSource = null;
                RemoteDeviceBox.ItemsSource = new[] { new RemoteDeviceChoice("全部设备", null) };
                RemoteDeviceBox.SelectedIndex = 0;
                RemoteSummaryText.Text = "账号或服务器已切换；服务器历史已清空，请重新查询。";
                ShowSelectedHistoryDetail();
                _remotePage = 1;
            }
            finally
            {
                _updatingRemoteSelectors = false;
            }

            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            _remoteQueryInProgress = false;
            _remoteAvailablePage = 0;
        });
    }

    private async Task LoadRemoteDevicesAsync()
    {
        var generation = Interlocked.Increment(ref _remoteQueryGeneration);
        try
        {
            var devices = await _remoteHistory.GetDevicesAsync();
            if (generation != Interlocked.Read(ref _remoteQueryGeneration))
                return;

            _updatingRemoteSelectors = true;
            try
            {
                var choices = new[] { new RemoteDeviceChoice("全部设备", null) }
                    .Concat(devices
                        .OrderBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                        .Select(device => new RemoteDeviceChoice(
                            $"{device.DeviceId} · {device.Platform}", device.DeviceId)))
                    .ToArray();
                RemoteDeviceBox.ItemsSource = choices;
                RemoteDeviceBox.SelectedIndex = 0;
            }
            finally
            {
                _updatingRemoteSelectors = false;
            }

            RemoteSummaryText.Text = devices.Count == 0
                ? "当前账号暂无已登记设备；服务器仍只返回当前账号授权范围内的数据。"
                : $"已读取当前账号的 {devices.Count} 台设备。选择日期与类型后查询上传记录。";
            await QueryRemoteAsync();
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _remoteQueryGeneration))
            {
                RemoteSummaryText.Text = $"读取设备失败：{ex.Message}";
                RemoteHistoryList.ItemsSource = null;
            }
        }
    }

    private async Task QueryRemoteAsync()
    {
        var generation = Interlocked.Increment(ref _remoteQueryGeneration);
        if (RemoteFromBox.SelectedDate is not { } fromDate
            || RemoteToBox.SelectedDate is not { } toDate)
        {
            _remoteQueryInProgress = false;
            _remoteAvailablePage = 0;
            RemoteHistoryList.ItemsSource = null;
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            if (HistoryModeTabs.SelectedIndex == 1)
                DetailText.Text = "选择左侧记录查看详情。";
            RemoteSummaryText.Text = "请选择开始与结束日期。空结果不代表设备采集或上传完整。";
            return;
        }

        _remoteQueryInProgress = true;
        _remoteAvailablePage = 0;
        PreviousPageButton.IsEnabled = false;
        NextPageButton.IsEnabled = false;
        RemoteSummaryText.Text = "正在查询服务器历史…";
        var deviceId = (RemoteDeviceBox.SelectedItem as RemoteDeviceChoice)?.DeviceId;
        var recordType = (RemoteTypeBox.SelectedItem as RemoteTypeChoice)?.RecordType;
        try
        {
            var result = await _remoteHistory.QueryAsync(
                DateOnly.FromDateTime(fromDate),
                DateOnly.FromDateTime(toDate),
                deviceId,
                recordType,
                _remotePage);
            if (generation != Interlocked.Read(ref _remoteQueryGeneration))
                return;

            var rows = result.Records
                .Select(record => new RemoteComputerHistoryRow(record, FormatRemoteLabel(record)))
                .ToArray();
            RemoteHistoryList.ItemsSource = rows;
            RemoteHistoryList.SelectedItem = rows.FirstOrDefault();
            var pages = result.TotalPages == 0 ? 1 : result.TotalPages;
            RemoteSummaryText.Text = result.TotalCount == 0
                ? $"所选日期范围暂无服务器记录（{result.From:yyyy-MM-dd} 至 {result.To:yyyy-MM-dd}）。空结果不证明设备采集或上传完整。"
                : result.Records.Count == 0
                    ? $"当前页暂无记录 · 共 {result.TotalCount} 条 · 第 {result.Page}/{pages} 页"
                      + (result.IsTruncated ? " · 结果超过客户端安全上限，已截断；缩小日期范围或选择设备继续查询。" : string.Empty)
                    : $"共 {result.TotalCount} 条 · 第 {result.Page}/{pages} 页 · {result.Records.Count} 条/页"
                      + (result.IsTruncated ? " · 结果超过客户端安全上限，已截断；缩小日期范围或选择设备继续查询。" : string.Empty);
            PreviousPageButton.IsEnabled = result.Page > 1;
            _remoteAvailablePage = Math.Min(result.TotalPages, RemoteComputerHistoryService.MaximumPages);
            NextPageButton.IsEnabled = result.Page < _remoteAvailablePage;
            if (rows.Length == 0 && HistoryModeTabs.SelectedIndex == 1)
                DetailText.Text = "当前页没有记录。";
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _remoteQueryGeneration))
            {
                RemoteHistoryList.ItemsSource = null;
                _remoteAvailablePage = 0;
                PreviousPageButton.IsEnabled = false;
                NextPageButton.IsEnabled = false;
                if (HistoryModeTabs.SelectedIndex == 1)
                    DetailText.Text = "服务器查询失败；本机历史不受影响。";
                RemoteSummaryText.Text = ex is ArgumentOutOfRangeException
                    ? ex.Message
                    : $"查询失败：{ex.Message}";
            }
        }
        finally
        {
            if (generation == Interlocked.Read(ref _remoteQueryGeneration))
                _remoteQueryInProgress = false;
        }
    }

    private void OnRemoteHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        ShowRemoteHistoryDetail();
    }

    private void ShowSelectedHistoryDetail()
    {
        if (!_componentsInitialized)
            return;

        if (HistoryModeTabs.SelectedIndex == 0)
            ShowLocalHistoryDetail();
        else if (HistoryModeTabs.SelectedIndex == 1)
            ShowRemoteHistoryDetail();
        else
            DetailText.Text = "选择左侧记录查看详情。";
    }

    private void ShowRemoteHistoryDetail()
    {
        if (!_componentsInitialized || HistoryModeTabs.SelectedIndex != 1)
            return;

        if (RemoteHistoryList.SelectedItem is not RemoteComputerHistoryRow row)
        {
            DetailText.Text = "选择左侧记录查看详情。";
            return;
        }

        var record = row.Record;
        DetailText.Text =
            $"来源：服务器已上传记录\n"
            + $"设备：{record.DeviceId}\n"
            + $"类型：{record.RecordType}\n"
            + $"开始：{record.Start}\n"
            + $"结束：{(record.End ?? "无")}\n"
            + $"时长：{(record.DurationSeconds is { } duration ? FormatDuration(TimeSpan.FromSeconds(Math.Max(0, duration))) : "无")}\n"
            + $"应用：{(record.AppName ?? record.BrowserAppName ?? "无")}\n"
            + $"标题：{(record.Title ?? record.BrowserWindowTitle ?? "无")}\n"
            + $"域名：{(record.Domain ?? "无")}\n"
            + $"URL：{(record.Url ?? "无")}\n"
            + "URL 仅作为历史文本展示，不会打开。";
    }

    private static string FormatRemoteLabel(PcDetailRecord record)
        => $"{FormatRemoteTimestamp(record.Start)} · {record.DeviceId} · {record.RecordType} · "
           + $"{(record.Domain ?? record.AppName ?? record.BrowserAppName ?? record.RecordType)} · "
           + $"{(record.Title ?? record.BrowserWindowTitle ?? "（无标题）")}";

    private static string FormatRemoteTimestamp(string value)
        => DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value;

    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _localQueryGeneration);
        var range = RangeBox.SelectedItem as RangeChoice ?? new RangeChoice("近 7 天", 7);
        var category = (CategoryBox.SelectedItem as CategoryChoice)?.Category;
        var search = SearchBox.Text;
        var now = DateTimeOffset.Now;
        var from = range.Days == 1
            ? new DateTimeOffset(now.LocalDateTime.Date, now.Offset)
            : now.AddDays(-range.Days);

        try
        {
            var entries = await _store.QueryAsync(
                from,
                now.AddSeconds(1),
                category,
                search);
            if (generation != Interlocked.Read(ref _localQueryGeneration))
            {
                return;
            }

            var rows = entries
                .Select(entry => new ComputerHistoryRow(entry, FormatLabel(entry)))
                .ToArray();
            HistoryList.ItemsSource = rows;
            HistoryList.SelectedItem = rows.FirstOrDefault();
            ShowLocalHistoryDetail();
            var totalSeconds = entries.Sum(entry => entry.DurationSeconds);
            var summary = string.Join(
                " · ",
                entries
                    .GroupBy(entry => entry.Category)
                    .OrderByDescending(group => group.Sum(entry => entry.DurationSeconds))
                    .Select(group =>
                        $"{CategoryLabel(group.Key)} {FormatDuration(TimeSpan.FromSeconds(group.Sum(entry => entry.DurationSeconds)))}"));
            SummaryText.Text = entries.Count == 0
                ? "所选范围暂无已结束的本机采集会话。正在进行的会话结束后会写入本机记录。"
                : $"共 {entries.Count} 段，累计 {FormatDuration(TimeSpan.FromSeconds(totalSeconds))}。{summary}"
                  + (_store.LastError is null ? string.Empty : $" 上次读取错误：{_store.LastError}");
        }
        catch (Exception ex)
        {
            if (generation != Interlocked.Read(ref _localQueryGeneration))
            {
                return;
            }

            HistoryList.ItemsSource = null;
            if (HistoryModeTabs.SelectedIndex == 0)
                DetailText.Text = "读取失败；已有本机采集不受影响。";
            SummaryText.Text = ex.Message;
        }
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        ShowLocalHistoryDetail();
    }

    private void ShowLocalHistoryDetail()
    {
        if (!_componentsInitialized || HistoryModeTabs.SelectedIndex != 0)
            return;

        if (HistoryList.SelectedItem is not ComputerHistoryRow row)
        {
            DetailText.Text = "选择左侧记录查看详情。";
            return;
        }

        var entry = row.Entry;
        var domains = entry.Domains.Length == 0 ? "无" : string.Join("、", entry.Domains);
        DetailText.Text =
            $"类别：{CategoryLabel(entry.Category)}\n"
            + $"应用：{entry.AppName}\n"
            + $"窗口：{(string.IsNullOrWhiteSpace(entry.WindowTitle) ? "无" : entry.WindowTitle)}\n"
            + $"开始：{entry.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n"
            + $"结束：{entry.EndedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n"
            + $"时长：{FormatDuration(TimeSpan.FromSeconds(entry.DurationSeconds))}\n"
            + $"空闲：{(entry.IsIdle ? "是" : "否")} · 媒体活跃：{(entry.IsMediaActive ? "是" : "否")}\n"
            + $"域名：{domains}";
    }

    private static string FormatLabel(TrackerHistoryEntry entry)
        => $"{entry.StartedAt.ToLocalTime():MM-dd HH:mm} · {CategoryLabel(entry.Category)} · "
           + $"{FormatDuration(TimeSpan.FromSeconds(entry.DurationSeconds))} · "
           + $"{(string.IsNullOrWhiteSpace(entry.WindowTitle) ? entry.AppName : entry.WindowTitle)}";

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}时{duration.Minutes}分"
            : duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes}分"
                : $"{(int)duration.TotalSeconds}秒";

    private static string CategoryLabel(TrackerActivityCategory category) => category switch
    {
        TrackerActivityCategory.Development => "开发",
        TrackerActivityCategory.Office => "办公",
        TrackerActivityCategory.Communication => "沟通",
        TrackerActivityCategory.Browsing => "浏览",
        TrackerActivityCategory.Media => "媒体",
        TrackerActivityCategory.Idle => "空闲",
        _ => "其他",
    };

    private sealed record RangeChoice(string Label, int Days)
    {
        public override string ToString() => Label;
    }

    private sealed record CategoryChoice(string Label, TrackerActivityCategory? Category)
    {
        public override string ToString() => Label;
    }

    private sealed record ComputerHistoryRow(TrackerHistoryEntry Entry, string Label);

    private sealed record RemoteComputerHistoryRow(PcDetailRecord Record, string Label);

    private sealed record RemoteDeviceChoice(string Label, string? DeviceId)
    {
        public override string ToString() => Label;
    }

    private sealed record RemoteTypeChoice(string Label, string? RecordType)
    {
        public override string ToString() => Label;
    }
}
