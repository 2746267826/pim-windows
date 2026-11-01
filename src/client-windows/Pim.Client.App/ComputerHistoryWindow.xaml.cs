using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class ComputerHistoryWindow : Window
{
    private readonly TrackerHistoryStore _store;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private long _queryGeneration;

    public ComputerHistoryWindow()
    {
        InitializeComponent();
        _store = App.Services.GetRequiredService<TrackerHistoryStore>();
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

    private async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _queryGeneration);
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
            if (generation != Interlocked.Read(ref _queryGeneration))
            {
                return;
            }

            var rows = entries
                .Select(entry => new ComputerHistoryRow(entry, FormatLabel(entry)))
                .ToArray();
            HistoryList.ItemsSource = rows;
            HistoryList.SelectedItem = rows.FirstOrDefault();
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
            if (generation != Interlocked.Read(ref _queryGeneration))
            {
                return;
            }

            HistoryList.ItemsSource = null;
            DetailText.Text = "读取失败；已有本机采集不受影响。";
            SummaryText.Text = ex.Message;
        }
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
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
}
