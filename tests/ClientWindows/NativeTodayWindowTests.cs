using System.IO;
using Pim.Client.App;
using Pim.Client.Core.Contract;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class NativeTodayWindowTests
{
    [Fact]
    public void TodayRangeCoversTheLocalCalendarDay()
    {
        var now = new DateTimeOffset(2026, 11, 25, 23, 45, 0, TimeSpan.FromHours(8));

        var (start, end) = NativeTodayWindow.GetLocalDayRange(now);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.FromHours(8)),
            start);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.FromHours(8)),
            end);
    }

    [Fact]
    public void SelectOpenTasksDropsCompletedAndCancelledItemsAndSortsByDueThenPriority()
    {
        var tasks = new[]
        {
            Task("later", "NEEDS-ACTION", 5, new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.FromHours(8))),
            Task("done", "COMPLETED", 1, new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.FromHours(8))),
            Task("earlier", "IN-PROCESS", 3, new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(8))),
            Task("cancelled", "CANCELLED", 1, new DateTimeOffset(2026, 11, 25, 7, 0, 0, TimeSpan.FromHours(8))),
            Task("tomorrow", "NEEDS-ACTION", 1, new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.FromHours(8))),
        };

        var selected = NativeTodayWindow.SelectOpenTasks(
            tasks,
            new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(["earlier", "later"], selected.Select(task => task.Title));
    }

    [Fact]
    public void WindowExposesTodayReadCreateCompleteAndErrorEmptyStates()
    {
        var xaml = ReadRepoFile("src", "client-windows", "Pim.Client.App", "NativeTodayWindow.xaml");
        var code = ReadRepoFile(
            "src",
            "client-windows",
            "Pim.Client.App",
            "NativeTodayWindow.xaml.cs");

        foreach (var label in new[]
                 {
                     "Title=\"今日\"",
                     "今日任务",
                     "今日日程",
                     "刷新今日",
                     "标记所选完成",
                     "快速新建今日任务",
                     "原生日历与任务",
                 })
        {
            Assert.Contains(label, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("GetTaskWorkspaceAsync(", code, StringComparison.Ordinal);
        Assert.Contains("dueFrom: _dayStart", code, StringComparison.Ordinal);
        Assert.Contains("dueTo: _dayEnd", code, StringComparison.Ordinal);
        Assert.Contains("GetAgendaAsync(_dayStart, _dayEnd)", code, StringComparison.Ordinal);
        Assert.Contains("CreateTaskAsync(", code, StringComparison.Ordinal);
        Assert.Contains("SetTaskCompletedAsync(", code, StringComparison.Ordinal);
        Assert.Contains("ExternalScopeChanged", code, StringComparison.Ordinal);
        Assert.Contains("今天没有待办任务", code, StringComparison.Ordinal);
        Assert.Contains("今天暂无日程", code, StringComparison.Ordinal);
        Assert.Contains("读取今日任务失败", code, StringComparison.Ordinal);
        Assert.Contains("读取今日日程失败", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddedWebViewHost", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayWorkspaceAndAppShareNativeTodayEntries()
    {
        var tray = ReadRepoFile("src", "client-windows", "Pim.Client.App", "TrayIcon.cs");
        var shellXaml = ReadRepoFile(
            "src",
            "client-windows",
            "Pim.Client.App",
            "MainShellWindow.xaml");
        var shellCode = ReadRepoFile(
            "src",
            "client-windows",
            "Pim.Client.App",
            "MainShellWindow.xaml.cs");
        var appCode = ReadRepoFile("src", "client-windows", "Pim.Client.App", "App.xaml.cs");

        Assert.Contains(
            "ContextMenuStrip.Items.Add(\"原生今日\", null, (_, _) => OpenNativeTodayWindow());",
            tray,
            StringComparison.Ordinal);
        Assert.Contains("App.ShowTodayWindow()", tray, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOpenNativeToday\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("今日（Web 兼容）", shellXaml, StringComparison.Ordinal);
        Assert.Contains("OnOpenNativeToday", shellCode, StringComparison.Ordinal);
        Assert.Contains("App.ShowTodayWindow(this)", shellCode, StringComparison.Ordinal);
        Assert.Contains("App.ShowPlannerWindow(this)", shellCode, StringComparison.Ordinal);
        Assert.Contains(
            "Current.Windows.OfType<NativeTodayWindow>()",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal static void ShowTodayWindow(Window? owner = null)",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "new NativeTodayWindow { Owner = owner }.Show()",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains("WindowSummoner.BringToFront(existing)", appCode, StringComparison.Ordinal);
    }

    private static TaskResponse Task(
        string title,
        string status,
        int priority,
        DateTimeOffset due)
        => new()
        {
            Id = Guid.NewGuid().ToString("D"),
            Title = title,
            Status = status,
            Priority = priority,
            SortOrder = priority,
            Uid = title,
            IsInbox = false,
            Due = due,
            SubTasks = [],
        };

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}
