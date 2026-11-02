using System.IO;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class RemoteComputerHistoryWindowTests
{
    [Fact]
    public void ServerHistoryShowsFiniteDateAndRowBudgetsAndEmptyResultCaveat()
    {
        var service = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.Core", "Services", "RemoteComputerHistoryService.cs"));
        var window = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "ComputerHistoryWindow.xaml.cs"));

        Assert.Contains("MaximumDateRangeDays = 31", service);
        Assert.Contains("PageSize = 200", service);
        Assert.Contains("MaximumPages = 25", service);
        Assert.Contains("MaximumRows = PageSize * MaximumPages", service);
        Assert.Contains("一次最多 31 天", window);
        Assert.Contains("最多 {RemoteComputerHistoryService.MaximumPages} 页 × ", window);
        Assert.Contains("每页 {RemoteComputerHistoryService.PageSize} 条（{RemoteComputerHistoryService.MaximumRows:N0} 条）", window);
        Assert.Contains("空结果仅表示本次筛选未返回数据，不证明设备采集或上传完整。", window);
    }

    [Fact]
    public void InvalidDatesInvalidateOlderQueriesAndPagingStopsAtBusyOrBudgetBoundary()
    {
        var window = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "ComputerHistoryWindow.xaml.cs"));
        var queryStart = window.IndexOf("private async Task QueryRemoteAsync()", StringComparison.Ordinal);
        var queryEnd = window.IndexOf("private void OnRemoteHistorySelected", queryStart, StringComparison.Ordinal);
        Assert.True(queryStart >= 0 && queryEnd > queryStart);
        var query = window[queryStart..queryEnd];
        var generationStart = query.IndexOf("var generation = Interlocked.Increment(ref _remoteQueryGeneration);", StringComparison.Ordinal);
        var missingDateGuard = query.IndexOf("if (RemoteFromBox.SelectedDate is not { } fromDate", StringComparison.Ordinal);
        Assert.True(generationStart >= 0 && missingDateGuard > generationStart);

        Assert.Contains("if (_remoteQueryInProgress || _remotePage <= 1)", window);
        Assert.Contains("if (_remoteQueryInProgress || _remotePage >= _remoteAvailablePage)", window);
        Assert.Contains("_remoteAvailablePage = Math.Min(result.TotalPages, RemoteComputerHistoryService.MaximumPages);", window);
        Assert.Contains("NextPageButton.IsEnabled = result.Page < _remoteAvailablePage;", window);
    }

    [Fact]
    public void LocalHistoryAndInertUrlDisplayRemainAvailable()
    {
        var xaml = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "ComputerHistoryWindow.xaml"));
        var window = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "ComputerHistoryWindow.xaml.cs"));

        Assert.Contains("本机历史", xaml);
        Assert.Contains("HistoryList", xaml);
        Assert.Contains("_store.QueryAsync(", window);
        Assert.Contains("_localQueryGeneration", window);
        Assert.Contains("URL 仅作为历史文本展示，不会打开。", window);
        Assert.DoesNotContain("Process.Start(", window);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }
}
