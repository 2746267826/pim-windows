using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Pim.Client.App;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
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

    [Fact]
    public void SelectionAndTabChangesKeepSharedDetailsScopedToActiveMode()
    {
        RunSta(() =>
        {
            if (Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }

            var root = Path.Combine(Path.GetTempPath(), "pim-history-window-" + Guid.NewGuid().ToString("N"));
            var store = new TrackerHistoryStore(root);
            var now = DateTimeOffset.Now;
            store.AppendAsync(new TrackerSession
            {
                Id = 1,
                DeviceId = "synthetic-local-device",
                AppName = "synthetic-local-app",
                WindowTitle = "LOCAL-DETAIL-SENTINEL",
                StartedAt = now.AddMinutes(-2),
                EndedAt = now.AddMinutes(-1),
                DurationSecs = 60,
            }).GetAwaiter().GetResult();
            var entry = Assert.Single(store.QueryAsync(now.AddHours(-1), now.AddMinutes(1))
                .GetAwaiter().GetResult());

            var handler = new NoNetworkHandler();
            var api = new ApiClient(() => handler);
            var window = new ComputerHistoryWindow(store, new RemoteComputerHistoryService(api), api);
            try
            {
                var localRow = CreateRow("ComputerHistoryRow", entry, "synthetic local");
                var remoteRecord = new PcDetailRecord
                {
                    DeviceId = "synthetic-remote-device",
                    RecordType = "window",
                    Start = now.AddMinutes(-1).ToString("O"),
                    Title = "REMOTE-DETAIL-SENTINEL",
                };
                var remoteRow = CreateRow("RemoteComputerHistoryRow", remoteRecord, "synthetic remote");
                window.HistoryList.ItemsSource = new[] { localRow };
                window.RemoteHistoryList.ItemsSource = new[] { remoteRow };

                window.HistoryModeTabs.SelectedIndex = 1;
                window.RemoteHistoryList.SelectedItem = remoteRow;
                var remoteDetails = window.DetailText.Text;
                Assert.Contains("REMOTE-DETAIL-SENTINEL", remoteDetails);

                window.HistoryList.SelectedItem = null;
                Assert.Equal(remoteDetails, window.DetailText.Text);
                window.HistoryList.SelectedItem = localRow;
                Assert.Equal(remoteDetails, window.DetailText.Text);

                window.HistoryModeTabs.SelectedIndex = 0;
                var localDetails = window.DetailText.Text;
                Assert.Contains("LOCAL-DETAIL-SENTINEL", localDetails);
                window.RemoteHistoryList.SelectedItem = null;
                Assert.Equal(localDetails, window.DetailText.Text);

                window.RemoteHistoryList.SelectedItem = remoteRow;
                window.HistoryModeTabs.SelectedIndex = 1;
                Assert.Equal(remoteDetails, window.DetailText.Text);
                Assert.Equal(0, handler.RequestCount);
            }
            finally
            {
                window.Close();
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        });
    }

    private static object CreateRow(string name, object value, string label)
    {
        var type = typeof(ComputerHistoryWindow).GetNestedType(name, BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing history row type {name}.");
        return Activator.CreateInstance(
                   type,
                   BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                   binder: null,
                   args: [value, label],
                   culture: null)
               ?? throw new InvalidOperationException($"Could not create history row {name}.");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("The UI behavior test must not send HTTP requests.");
        }
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
