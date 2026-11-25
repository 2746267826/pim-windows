using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App;
using Pim.Client.Core.Services;
using Xunit;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfApplication = System.Windows.Application;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class NativeWorkbenchBehaviorTests(WpfApplicationFixture wpf)
{
    [Fact]
    public void WebCompatibilityUrlNormalizesApiRootAndNeverCarriesQueryCredentials()
    {
        Assert.Equal(
            "https://pim.example.test/today",
            WebCompatibilityLinks.BuildUrl("https://pim.example.test/api/v1", "/today"));
        Assert.Equal(
            "https://pim.example.test/quick-notes",
            WebCompatibilityLinks.BuildUrl("https://pim.example.test/api/v1/", "quick-notes"));
        Assert.DoesNotContain(
            "token",
            WebCompatibilityLinks.BuildUrl("https://pim.example.test/api/v1", "/today"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrayOpensNativeWorkbenchAndPlannerWithoutLoginAndWithoutNetwork()
    {
        var session = await PlannerSession.CreateAsync(
            authenticated: false,
            failBusinessRequests: false);
        using (session)
        {
            await wpf.RunAsync(async () =>
            {
                using var provider = session.BuildProvider();
                var previous = SetAppServices(provider);
                try
                {
                    var tray = new TrayIcon();
                    using var menu = new Forms.ContextMenuStrip();
                    tray.BuildContextMenu(menu);

                    Click(menu, "原生工作台");
                    await Dispatcher.Yield(DispatcherPriority.Background);

                    var workbench = Assert.Single(
                        WpfApplication.Current.Windows.OfType<NativeWorkbenchWindow>());
                    try
                    {
                        Assert.Equal(
                            "账户状态：未登录",
                            Text(workbench, "AccountStateText"));

                        var plannerButton = Assert.IsType<WpfButton>(workbench.FindName("PlannerButton"));
                        plannerButton.RaiseEvent(new RoutedEventArgs(WpfButton.ClickEvent));
                        await Dispatcher.Yield(DispatcherPriority.Background);

                        var planner = Assert.Single(
                            WpfApplication.Current.Windows.OfType<NativePlannerWindow>());
                        try
                        {
                            Assert.Contains(
                                "请先登录",
                                Text(planner, "EventListStateText"),
                                StringComparison.Ordinal);
                            Assert.Contains(
                                "请先登录",
                                Text(planner, "TaskListStateText"),
                                StringComparison.Ordinal);
                            Assert.Equal(0, session.Handler.BusinessRequests);
                        }
                        finally
                        {
                            planner.Close();
                        }
                    }
                    finally
                    {
                        workbench.Close();
                    }
                }
                finally
                {
                    SetAppServices(previous!);
                }
            });
        }
    }

    [Fact]
    public async Task PlannerReportsLoadFailuresAndClearsAccountScopedTaskBooks()
    {
        var session = await PlannerSession.CreateAsync(
            authenticated: true,
            failBusinessRequests: true);
        using (session)
        {
            await wpf.RunAsync(async () =>
            {
                var failures = new List<(string Title, string Message)>();
                var planner = new NativePlannerWindow(session.Planner, session.Auth, session.Api)
                {
                    FailureDialog = (title, message) => failures.Add((title, message)),
                };
                try
                {
                    Assert.IsType<WpfListBox>(planner.FindName("TaskBooksList")).ItemsSource =
                        new object[] { "stale-task-book" };

                    await planner.RefreshEventsAsync();
                    await planner.RefreshTasksAsync();

                    Assert.Equal("读取日程失败。", Text(planner, "EventListStateText"));
                    Assert.Equal("读取任务失败。", Text(planner, "TaskListStateText"));
                    Assert.Null(planner.FindName("TaskBooksList") is WpfListBox books
                        ? books.ItemsSource
                        : null);
                    Assert.Equal(
                        ["读取日程失败", "读取任务失败"],
                        failures.Select(failure => failure.Title).ToArray());
                    Assert.All(
                        failures,
                        failure => Assert.Contains(
                            "无法连接服务器",
                            failure.Message,
                            StringComparison.Ordinal));
                }
                finally
                {
                    planner.Close();
                }
            });
        }
    }

    [Fact]
    public async Task TodayReportsLoggedOutAndLoadFailureStatesWithoutOpeningAWebPage()
    {
        var loggedOut = await PlannerSession.CreateAsync(
            authenticated: false,
            failBusinessRequests: false);
        using (loggedOut)
        {
            await wpf.RunAsync(async () =>
            {
                var today = new NativeTodayWindow(loggedOut.Planner, loggedOut.Auth, loggedOut.Api);
                try
                {
                    await today.RefreshTodayAsync();

                    Assert.Equal("今日任务：未登录", Text(today, "TaskSummaryText"));
                    Assert.Equal("今日日程：未登录", Text(today, "EventSummaryText"));
                    Assert.Contains("未登录", Text(today, "TodayStateText"), StringComparison.Ordinal);
                    Assert.Equal(0, loggedOut.Handler.BusinessRequests);
                }
                finally
                {
                    today.Close();
                }
            });
        }

        var failing = await PlannerSession.CreateAsync(
            authenticated: true,
            failBusinessRequests: true);
        using (failing)
        {
            await wpf.RunAsync(async () =>
            {
                var failures = new List<string>();
                var today = new NativeTodayWindow(failing.Planner, failing.Auth, failing.Api)
                {
                    FailureDialog = (_, message) => failures.Add(message),
                };
                try
                {
                    await today.RefreshTodayAsync();

                    Assert.Equal("今日任务：读取失败", Text(today, "TaskSummaryText"));
                    Assert.Equal("今日日程：读取失败", Text(today, "EventSummaryText"));
                    Assert.Contains(
                        "今日数据部分或全部读取失败",
                        Text(today, "TodayStateText"),
                        StringComparison.Ordinal);
                    Assert.Equal(2, failures.Count);
                }
                finally
                {
                    today.Close();
                }
            });
        }
    }

    [Fact]
    public async Task PlannerSendsTheSharedMultiStatusSelectionToTheTypedTaskQuery()
    {
        var session = await PlannerSession.CreateAsync(
            authenticated: true,
            failBusinessRequests: false);
        using (session)
        {
            await wpf.RunAsync(async () =>
            {
                var planner = new NativePlannerWindow(session.Planner, session.Auth, session.Api);
                try
                {
                    var filter = Assert.IsType<TaskStatusFilterControl>(
                        planner.FindName("TaskStatusFilter"));
                    ((WpfCheckBox)filter.FindName("NeedsActionCheck")!).IsChecked = true;
                    ((WpfCheckBox)filter.FindName("InProcessCheck")!).IsChecked = true;

                    await planner.RefreshTasksAsync();

                    var taskQuery = Assert.Single(
                        session.Handler.Calls,
                        call => call.Path == "/api/v1/calendar/tasks").Query;
                    Assert.Contains("status=NEEDS-ACTION", taskQuery, StringComparison.Ordinal);
                    Assert.Contains("status=IN-PROCESS", taskQuery, StringComparison.Ordinal);
                }
                finally
                {
                    planner.Close();
                }
            });
        }
    }

    [Fact]
    public async Task AccountOrServerChangeClearsPlannerAndTodayRows()
    {
        var session = await PlannerSession.CreateAsync(
            authenticated: true,
            failBusinessRequests: false);
        using (session)
        {
            await wpf.RunAsync(async () =>
            {
                var planner = new NativePlannerWindow(session.Planner, session.Auth, session.Api);
                var today = new NativeTodayWindow(session.Planner, session.Auth, session.Api);
                try
                {
                    await planner.RefreshEventsAsync();
                    await planner.RefreshTasksAsync();
                    await today.RefreshTodayAsync();

                    session.Api.SetBaseUrl("https://other.example.test");
                    await Dispatcher.Yield(DispatcherPriority.Background);

                    Assert.Contains(
                        "账号或服务器已变更",
                        Text(planner, "EventListStateText"),
                        StringComparison.Ordinal);
                    Assert.Contains(
                        "账号或服务器已变更",
                        Text(planner, "TaskListStateText"),
                        StringComparison.Ordinal);
                    Assert.Null(Assert.IsType<WpfListBox>(
                        planner.FindName("TaskBooksList")).ItemsSource);
                    Assert.Contains(
                        "账号或服务器已变更",
                        Text(today, "TodayStateText"),
                        StringComparison.Ordinal);
                }
                finally
                {
                    today.Close();
                    planner.Close();
                }
            });
        }
    }

    private static string Text(Window window, string name)
        => Assert.IsType<TextBlock>(window.FindName(name)).Text;

    private static void Click(Forms.ContextMenuStrip menu, string text)
    {
        var item = menu.Items
            .OfType<Forms.ToolStripMenuItem>()
            .Single(candidate => candidate.Text == text);
        item.PerformClick();
    }

    private static IServiceProvider? SetAppServices(IServiceProvider provider)
    {
        var property = typeof(App).GetProperty(
            nameof(App.Services),
            BindingFlags.Public | BindingFlags.Static)!;
        var previous = (IServiceProvider?)property.GetValue(null);
        property.SetValue(null, provider);
        return previous;
    }

    private sealed class PlannerSession : IDisposable
    {
        private readonly string _root;

        private PlannerSession(
            string root,
            ApiClient api,
            AuthService auth,
            CalendarTaskService planner,
            PlannerHandler handler)
        {
            _root = root;
            Api = api;
            Auth = auth;
            Planner = planner;
            Handler = handler;
        }

        public ApiClient Api { get; }
        public AuthService Auth { get; }
        public CalendarTaskService Planner { get; }
        public PlannerHandler Handler { get; }

        public static async Task<PlannerSession> CreateAsync(
            bool authenticated,
            bool failBusinessRequests)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "pim-m06-workbench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var handler = new PlannerHandler(failBusinessRequests);
            var api = new ApiClient(() => handler);
            var auth = new AuthService(
                api,
                Path.Combine(root, "token.json"),
                tokenProtector: new PassthroughTokenProtector());
            if (authenticated)
            {
                Assert.True(await auth.LoginAsync("m06-user", "synthetic-password"));
            }

            return new PlannerSession(
                root,
                api,
                auth,
                new CalendarTaskService(api),
                handler);
        }

        public ServiceProvider BuildProvider()
            => new ServiceCollection()
                .AddSingleton(Api)
                .AddSingleton(Auth)
                .AddSingleton(Planner)
                .BuildServiceProvider();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class PlannerHandler(bool failBusinessRequests) : HttpMessageHandler
    {
        public List<(string Path, string Query)> Calls { get; } = [];
        public int BusinessRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """
                    {
                      "code": 0,
                      "message": "ok",
                      "timestamp": "2026-11-25T00:00:00Z",
                      "data": {
                        "accessToken": "synthetic-m06-token",
                        "refreshToken": "synthetic-m06-refresh",
                        "expiresAt": "2099-01-01T00:00:00Z",
                        "user": {
                          "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                          "username": "m06-user",
                          "displayName": "M06 User",
                          "role": "user"
                        }
                      }
                    }
                    """));
            }

            BusinessRequests++;
            Calls.Add((path, request.RequestUri.Query));
            if (failBusinessRequests)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "synthetic upstream failure",
                        Encoding.UTF8,
                        "text/plain"),
                });
            }

            var payload = path switch
            {
                "/api/v1/calendar/calendars" => "[]",
                "/api/v1/calendar/task-books" => "[]",
                "/api/v1/calendar/events" =>
                    """
                    {
                      "items": [],
                      "page": 1,
                      "pageSize": 200,
                      "totalCount": 0,
                      "totalPages": 0
                    }
                    """,
                "/api/v1/calendar/tasks" =>
                    """
                    {
                      "items": [],
                      "page": 1,
                      "pageSize": 200,
                      "totalCount": 0,
                      "totalPages": 0
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected request path: {path}"),
            };
            return Task.FromResult(Json(
                $$"""
                {
                  "code": 0,
                  "message": "ok",
                  "timestamp": "2026-11-25T00:00:00Z",
                  "data": {{payload}}
                }
                """));
        }

        private static HttpResponseMessage Json(string json)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class PassthroughTokenProtector : ITokenProtector
    {
        public bool CanProtect => false;
        public bool ShouldMigratePlaintext => false;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] payload) => payload;
    }
}
