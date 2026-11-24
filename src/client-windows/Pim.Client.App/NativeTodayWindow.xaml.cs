using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class NativeTodayWindow : Window
{
    private readonly CalendarTaskService _planner;
    private readonly AuthService _auth;
    private readonly ApiClient _api;
    private readonly NativePlannerUiState _eventUi = new();
    private readonly NativePlannerUiState _taskUi = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DateTimeOffset _dayStart;
    private DateTimeOffset _dayEnd;
    private bool _suppressSelection;
    private bool _closed;

    public NativeTodayWindow()
    {
        InitializeComponent();
        _planner = App.Services.GetRequiredService<CalendarTaskService>();
        _auth = App.Services.GetRequiredService<AuthService>();
        _api = App.Services.GetRequiredService<ApiClient>();
        _api.ExternalScopeChanged += OnExternalScopeChanged;

        TaskPriorityBox.ItemsSource = Enumerable.Range(1, 9).ToArray();
        TaskPriorityBox.SelectedItem = 5;
        TaskBookBox.ItemsSource = new[] { TaskBookChoice.None };
        TaskBookBox.SelectedIndex = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await RefreshTodayAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshTodayAsync();

    private async Task RefreshTodayAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        RefreshTodayButton.IsEnabled = false;
        try
        {
            var now = DateTimeOffset.Now;
            (_dayStart, _dayEnd) = GetLocalDayRange(now);
            TodayDateText.Text = $"{now:yyyy年M月d日 dddd} · 日程与任务";

            var account = CurrentAccount();
            if (account is null)
            {
                _taskUi.Invalidate();
                _eventUi.Invalidate();
                ClearTaskUi("请先登录 PIM 账户；今日任务只从当前账户读取。");
                ClearEventUi("请先登录 PIM 账户；今日日程只从当前账户读取。");
                TaskSummaryText.Text = "今日任务：未登录";
                EventSummaryText.Text = "今日日程：未登录";
                TodayStateText.Text = "未登录，无法读取今日数据。";
                return;
            }

            TodayStateText.Text = "正在读取今日数据…";
            var tasksLoaded = await RefreshTasksAsync(account);
            var eventsLoaded = await RefreshEventsAsync(account);
            if (!_closed && account == CurrentAccount())
            {
                TodayStateText.Text = tasksLoaded && eventsLoaded
                    ? "今日数据已刷新。"
                    : "今日数据部分或全部读取失败；可点击“刷新今日”重试。";
            }
        }
        finally
        {
            if (!_closed)
            {
                RefreshTodayButton.IsEnabled = true;
            }

            _refreshGate.Release();
        }
    }

    private async Task<bool> RefreshTasksAsync(PlannerAccountIdentity account)
    {
        var generation = _taskUi.BeginLoad();
        CompleteTaskButton.IsEnabled = false;
        CreateTaskButton.IsEnabled = false;
        TaskSummaryText.Text = "今日任务：读取中";

        if (_closed || account != CurrentAccount())
        {
            return false;
        }

        try
        {
            TaskListStateText.Text = "正在读取今日任务…";
            var workspace = await _planner.GetTaskWorkspaceAsync(
                dueFrom: _dayStart,
                dueTo: _dayEnd);
            if (_closed || !_taskUi.IsLoadCurrent(generation, account)
                || account != CurrentAccount()
                || !_taskUi.CommitLoad(generation, account))
            {
                return false;
            }

            var fence = new PlannerDataFence(generation, account);
            _suppressSelection = true;
            try
            {
                ApplyTaskBooks(workspace.TaskBooks, fence);
                var rows = SelectOpenTasks(workspace.Tasks.Items, _dayStart, _dayEnd)
                    .Select(item => new TodayTaskRow(item, fence))
                    .ToArray();
                TaskList.ItemsSource = rows;
                TaskList.SelectedItem = null;
                TaskDetailText.Text = "选择任务查看详情。";

                TaskSummaryText.Text = $"今日任务：{rows.Length} 个待办";
                TaskListStateText.Text = rows.Length == 0
                    ? "今天没有待办任务。可以在下方快速新建一个今日任务。"
                    : $"今天有 {rows.Length} 个待办任务。";
                CreateTaskButton.IsEnabled = true;
            }
            finally
            {
                _suppressSelection = false;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_closed && _taskUi.IsLoadCurrent(generation, account)
                && account == CurrentAccount())
            {
                ClearTaskUi("读取今日任务失败。请检查网络、服务器地址与登录状态后重试。");
                TaskSummaryText.Text = "今日任务：读取失败";
                ShowFailure("读取今日任务失败", ex);
            }

            return false;
        }
    }

    private async Task<bool> RefreshEventsAsync(PlannerAccountIdentity account)
    {
        var generation = _eventUi.BeginLoad();
        EventSummaryText.Text = "今日日程：读取中";

        if (_closed || account != CurrentAccount())
        {
            return false;
        }

        try
        {
            EventListStateText.Text = "正在读取今日日程…";
            var agenda = await _planner.GetAgendaAsync(_dayStart, _dayEnd);
            if (_closed || !_eventUi.IsLoadCurrent(generation, account)
                || account != CurrentAccount()
                || !_eventUi.CommitLoad(generation, account))
            {
                return false;
            }

            var fence = new PlannerDataFence(generation, account);
            _suppressSelection = true;
            try
            {
                var rows = agenda.Events.Items
                    .OrderBy(item => item.DtStart)
                    .Select(item => new TodayEventRow(item, fence))
                    .ToArray();
                EventList.ItemsSource = rows;
                EventList.SelectedItem = null;
                EventDetailText.Text = "选择日程查看详情。";

                EventSummaryText.Text = $"今日日程：{rows.Length} 个";
                EventListStateText.Text = rows.Length == 0
                    ? "今天暂无日程。需要新建时请打开原生日历与任务。"
                    : $"今天有 {rows.Length} 个日程。";
            }
            finally
            {
                _suppressSelection = false;
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_closed && _eventUi.IsLoadCurrent(generation, account)
                && account == CurrentAccount())
            {
                ClearEventUi("读取今日日程失败。请检查网络、服务器地址与登录状态后重试。");
                EventSummaryText.Text = "今日日程：读取失败";
                ShowFailure("读取今日日程失败", ex);
            }

            return false;
        }
    }

    private void OnTaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (TaskList.SelectedItem is not TodayTaskRow row
            || _taskUi.CheckWrite(row.Fence, CurrentAccount()) != PlannerFenceFailure.None)
        {
            TaskDetailText.Text = "账号或服务器已变更，请重新读取今日任务。";
            CompleteTaskButton.IsEnabled = false;
            return;
        }

        var item = row.Task;
        CompleteTaskButton.IsEnabled = IsOpenTask(item);
        TaskDetailText.Text =
            $"标题：{item.Title}\n" +
            $"状态：{StatusLabel(item.Status)} · 优先级：{item.Priority} · 完成度：{item.PercentComplete}%\n" +
            $"到期：{FormatOptional(item.Due)}\n" +
            $"说明：{Fallback(item.Description)}";
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (EventList.SelectedItem is not TodayEventRow row
            || _eventUi.CheckWrite(row.Fence, CurrentAccount()) != PlannerFenceFailure.None)
        {
            EventDetailText.Text = "账号或服务器已变更，请重新读取今日日程。";
            return;
        }

        var item = row.Event;
        EventDetailText.Text =
            $"标题：{item.Title}\n" +
            $"时间：{FormatEventTime(item)}\n" +
            $"地点：{Fallback(item.Location)}\n" +
            $"来源：{item.Source} · 状态：{item.Status}\n" +
            $"说明：{Fallback(item.Description)}";
    }

    private async void OnCreateTask(object sender, RoutedEventArgs e)
    {
        if (TaskPriorityBox.SelectedItem is not int priority)
        {
            TaskCreateStateText.Text = "请选择任务优先级。";
            return;
        }

        if (TaskBookBox.SelectedItem is not TaskBookChoice taskBook)
        {
            TaskCreateStateText.Text = "请先刷新今日任务，再创建任务。";
            return;
        }

        var title = TaskTitleBox.Text;
        if (string.IsNullOrWhiteSpace(title))
        {
            TaskCreateStateText.Text = "任务标题不能为空。";
            return;
        }

        var account = CurrentAccount();
        var fenceFailure = _taskUi.CheckWrite(taskBook.Fence, account);
        if (account is null || fenceFailure != PlannerFenceFailure.None)
        {
            TaskCreateStateText.Text = FenceMessage(fenceFailure, "任务");
            CreateTaskButton.IsEnabled = false;
            return;
        }

        var day = _dayStart == default
            ? GetLocalDayRange(DateTimeOffset.Now).Start
            : _dayStart;
        var due = ToLocalOffset(day.Date.AddHours(18));
        CreateTaskButton.IsEnabled = false;
        TaskCreateStateText.Text = "正在创建今日任务…";
        try
        {
            var created = await _planner.CreateTaskAsync(
                title,
                priority,
                due,
                calendarId: null,
                taskBook.Id);
            if (_closed || account != CurrentAccount())
            {
                return;
            }

            TaskCreateStateText.Text = $"已创建：{created.Title}";
            TaskTitleBox.Clear();
            await RefreshTasksAsync(account);
        }
        catch (Exception ex)
        {
            if (!_closed && account == CurrentAccount())
            {
                TaskCreateStateText.Text = "创建今日任务失败；标题仍保留。";
                ShowFailure("创建今日任务失败", ex);
            }
        }
        finally
        {
            if (!_closed && account == CurrentAccount()
                && _taskUi.CheckWrite(taskBook.Fence, account) == PlannerFenceFailure.None)
            {
                CreateTaskButton.IsEnabled = true;
            }
        }
    }

    private async void OnCompleteTask(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TodayTaskRow row
            || !Guid.TryParse(row.Task.Id, out var taskId)
            || !IsOpenTask(row.Task))
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"把“{row.Task.Title}”标记为已完成？",
                "完成任务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var account = CurrentAccount();
        var fenceFailure = _taskUi.CheckWrite(row.Fence, account);
        if (account is null || fenceFailure != PlannerFenceFailure.None)
        {
            TaskListStateText.Text = FenceMessage(fenceFailure, "任务");
            CompleteTaskButton.IsEnabled = false;
            return;
        }

        CompleteTaskButton.IsEnabled = false;
        TaskListStateText.Text = "正在更新任务…";
        try
        {
            var updated = await _planner.SetTaskCompletedAsync(taskId);
            if (_closed || account != CurrentAccount())
            {
                return;
            }

            if (await RefreshTasksAsync(account))
            {
                TaskListStateText.Text = $"已标记完成：{updated.Title}";
            }
        }
        catch (Exception ex)
        {
            if (!_closed && account == CurrentAccount())
            {
                TaskListStateText.Text = "标记任务完成失败。";
                ShowFailure("完成任务失败", ex);
            }
        }
        finally
        {
            if (!_closed && account == CurrentAccount()
                && TaskList.SelectedItem is TodayTaskRow selected
                && _taskUi.CheckWrite(selected.Fence, account) == PlannerFenceFailure.None)
            {
                CompleteTaskButton.IsEnabled = IsOpenTask(selected.Task);
            }
        }
    }

    private void OnOpenPlanner(object sender, RoutedEventArgs e)
        => App.ShowPlannerWindow();

    private void ApplyTaskBooks(IReadOnlyList<TaskBookDto> books, PlannerDataFence fence)
    {
        var prior = (TaskBookBox.SelectedItem as TaskBookChoice)?.Id;
        var choices = new[] { new TaskBookChoice("不指定任务本", null, fence) }
            .Concat(books.Select(book => new TaskBookChoice(
                book.Name,
                Guid.Parse(book.Id),
                fence)))
            .ToArray();
        TaskBookBox.ItemsSource = choices;
        TaskBookBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == prior)
            ?? choices[0];
    }

    private void ClearTaskUi(string state)
    {
        _suppressSelection = true;
        try
        {
            TaskList.ItemsSource = null;
            TaskList.SelectedItem = null;
            TaskDetailText.Text = "选择任务查看详情。";
            TaskListStateText.Text = state;
            CompleteTaskButton.IsEnabled = false;
            CreateTaskButton.IsEnabled = false;
            TaskBookBox.ItemsSource = new[] { TaskBookChoice.None };
            TaskBookBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void ClearEventUi(string state)
    {
        _suppressSelection = true;
        try
        {
            EventList.ItemsSource = null;
            EventList.SelectedItem = null;
            EventDetailText.Text = "选择日程查看详情。";
            EventListStateText.Text = state;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private PlannerAccountIdentity? CurrentAccount()
    {
        if (!_auth.IsAuthenticated || string.IsNullOrWhiteSpace(_auth.CurrentUserId))
        {
            return null;
        }

        return new PlannerAccountIdentity(_auth.CurrentUserId, _api.CurrentBaseUrl);
    }

    private static string FenceMessage(PlannerFenceFailure failure, string resource)
        => failure switch
        {
            PlannerFenceFailure.NoAccount => $"登录状态已失效；本次{resource}操作未发送，输入内容仍保留。",
            PlannerFenceFailure.AccountChanged =>
                $"账号或服务器已切换；本次{resource}操作未发送，输入内容仍保留，请重新读取。",
            _ => $"{resource}列表已过期；本次操作未发送，输入内容仍保留，请重新读取。",
        };

    private void ShowFailure(string title, Exception exception)
    {
        var message = exception is HttpRequestException
            ? "无法连接服务器。请检查网络、服务器地址与登录状态。"
            : exception.Message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _api.ExternalScopeChanged -= OnExternalScopeChanged;
        _taskUi.Invalidate();
        _eventUi.Invalidate();
    }

    private void OnExternalScopeChanged()
    {
        _taskUi.Invalidate();
        _eventUi.Invalidate();
        if (_closed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
            {
                return;
            }

            ClearTaskUi("账号或服务器已变更，请重新读取今日任务；未发送的输入内容仍保留。");
            ClearEventUi("账号或服务器已变更，请重新读取今日日程。");
            TaskSummaryText.Text = "今日任务：未读取";
            EventSummaryText.Text = "今日日程：未读取";
            TodayStateText.Text = "账号或服务器已变更，请点击“刷新今日”。";
        });
    }

    internal static (DateTimeOffset Start, DateTimeOffset End) GetLocalDayRange(DateTimeOffset now)
    {
        var startLocal = DateTime.SpecifyKind(now.Date, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(startLocal, now.Offset);
        var endLocal = DateTime.SpecifyKind(startLocal.AddDays(1), DateTimeKind.Unspecified);
        return (start, new DateTimeOffset(endLocal, now.Offset));
    }

    internal static bool IsOpenTask(TaskResponse task)
        => !string.Equals(task.Status, "COMPLETED", StringComparison.Ordinal)
           && !string.Equals(task.Status, "CANCELLED", StringComparison.Ordinal);

    internal static IReadOnlyList<TaskResponse> SelectOpenTasks(
        IEnumerable<TaskResponse> tasks,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd)
        => tasks
            .Where(IsOpenTask)
            .Where(task => task.Due is { } due && due >= dayStart && due < dayEnd)
            .OrderBy(task => task.Due ?? DateTimeOffset.MaxValue)
            .ThenBy(task => task.Priority)
            .ThenBy(task => task.Title, StringComparer.CurrentCulture)
            .ToArray();

    private static DateTimeOffset ToLocalOffset(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }

    private static string FormatEventTime(EventResponse item)
    {
        if (item.IsAllDay)
        {
            var end = item.DtEnd.ToLocalTime().AddDays(-1);
            return item.DtStart.ToLocalTime().Date == end.Date
                ? $"{item.DtStart.ToLocalTime():yyyy-MM-dd} · 全天"
                : $"{item.DtStart.ToLocalTime():yyyy-MM-dd} 至 {end:yyyy-MM-dd} · 全天";
        }

        return $"{item.DtStart.ToLocalTime():HH:mm} - {item.DtEnd.ToLocalTime():HH:mm}";
    }

    private static string FormatTaskDue(DateTimeOffset? value)
        => value is null ? "无" : value.Value.ToLocalTime().ToString("HH:mm");

    private static string FormatOptional(DateTimeOffset? value)
        => value is null ? "无" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static string Fallback(string? value)
        => string.IsNullOrWhiteSpace(value) ? "无" : value;

    private static string StatusLabel(string status) => status switch
    {
        "NEEDS-ACTION" => "待处理",
        "IN-PROCESS" => "进行中",
        "COMPLETED" => "已完成",
        "CANCELLED" => "已取消",
        _ => status,
    };

    private sealed record TaskBookChoice(
        string Label,
        Guid? Id,
        PlannerDataFence Fence)
    {
        public static TaskBookChoice None { get; } = new(
            "不指定任务本",
            null,
            default);
        public override string ToString() => Label;
    }

    private sealed class TodayTaskRow(TaskResponse item, PlannerDataFence fence)
    {
        public TaskResponse Task { get; } = item;
        public PlannerDataFence Fence { get; } = fence;
        public string Label => Task.Title;
        public string Subtitle =>
            $"{StatusLabel} · 到期 {FormatTaskDue(Task.Due)} · 优先级 {Task.Priority}";
        public string StatusLabel => NativeTodayWindow.StatusLabel(Task.Status);
        public System.Windows.Media.Brush StatusBrush => Task.Status switch
        {
            "COMPLETED" => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(20, 184, 166)),
            "IN-PROCESS" => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(37, 99, 235)),
            "CANCELLED" => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(100, 116, 139)),
            _ => new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(245, 158, 11)),
        };
    }

    private sealed class TodayEventRow(EventResponse item, PlannerDataFence fence)
    {
        public EventResponse Event { get; } = item;
        public PlannerDataFence Fence { get; } = fence;
        public string Label => Event.Title;
        public string Subtitle =>
            $"{FormatEventTime(Event)} · {Fallback(Event.Location)}";
    }
}
