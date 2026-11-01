using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class NativePlannerWindow : Window
{
    private readonly CalendarTaskService _planner;
    private readonly AuthService _auth;
    private readonly ApiClient _api;
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly SemaphoreSlim _taskGate = new(1, 1);
    private readonly NativePlannerUiState _eventUi = new();
    private readonly NativePlannerUiState _taskUi = new();
    private bool _loading;
    private bool _closed;

    public NativePlannerWindow()
    {
        InitializeComponent();
        _planner = App.Services.GetRequiredService<CalendarTaskService>();
        _auth = App.Services.GetRequiredService<AuthService>();
        _api = App.Services.GetRequiredService<ApiClient>();
        _api.ExternalScopeChanged += OnExternalScopeChanged;

        EventRangeBox.ItemsSource = new[]
        {
            new RangeChoice("今天", 1),
            new RangeChoice("近 7 天", 7),
            new RangeChoice("近 30 天", 30),
        };
        EventRangeBox.SelectedIndex = 1;

        CalendarFilterBox.ItemsSource = new[] { CalendarChoice.All };
        CalendarFilterBox.SelectedIndex = 0;
        EventCalendarBox.ItemsSource = Array.Empty<CalendarChoice>();

        TaskStatusFilterBox.ItemsSource = new[]
        {
            StatusChoice.All,
            new StatusChoice("待处理", "NEEDS-ACTION"),
            new StatusChoice("进行中", "IN-PROCESS"),
            new StatusChoice("已完成", "COMPLETED"),
            new StatusChoice("已取消", "CANCELLED"),
        };
        TaskStatusFilterBox.SelectedIndex = 0;

        TaskPriorityBox.ItemsSource = Enumerable.Range(1, 9).ToArray();
        TaskPriorityBox.SelectedItem = 5;
        TaskBookBox.ItemsSource = new[] { TaskBookChoice.None };
        TaskBookBox.SelectedIndex = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        EventStartDate.SelectedDate = now.Date;
        EventEndDate.SelectedDate = now.Date;
        await RefreshEventsAsync();
        await RefreshTasksAsync();
    }

    private async void OnRefreshCurrent(object sender, RoutedEventArgs e)
    {
        if (PlannerTabs.SelectedIndex == 0)
        {
            await RefreshEventsAsync();
        }
        else
        {
            await RefreshTasksAsync();
        }
    }

    private async void OnRefreshEvents(object sender, RoutedEventArgs e)
        => await RefreshEventsAsync();

    private async void OnEventFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loading)
        {
            await RefreshEventsAsync();
        }
    }

    private async void OnEventSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_loading)
        {
            await RefreshEventsAsync();
        }
    }

    private async Task RefreshEventsAsync()
    {
        var generation = _eventUi.BeginLoad();
        var account = CurrentAccount();
        var selectedCalendar = (CalendarFilterBox.SelectedItem as CalendarChoice)?.Id;
        var selectedEventId = (EventList.SelectedItem as EventRow)?.Event.Id;
        await _eventGate.WaitAsync();
        try
        {
            if (_closed || !_eventUi.IsLoadCurrent(generation, account)
                || account != CurrentAccount())
            {
                return;
            }

            if (account is null)
            {
                ClearEventUi("请先登录 PIM 账户；日程只从当前账户读取。");
                return;
            }

            EventListStateText.Text = "正在读取日程…";
            var range = EventRangeBox.SelectedItem as RangeChoice ?? new RangeChoice("近 7 天", 7);
            var now = DateTimeOffset.Now;
            var from = new DateTimeOffset(
                now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var to = from.AddDays(range.Days);
            var agenda = await _planner.GetAgendaAsync(
                from,
                to,
                string.IsNullOrWhiteSpace(EventSearchBox.Text) ? null : EventSearchBox.Text.Trim(),
                selectedCalendar);
            if (_closed || !_eventUi.IsLoadCurrent(generation, account)
                || account != CurrentAccount()
                || !_eventUi.CommitLoad(generation, account))
            {
                return;
            }

            _loading = true;
            var fence = new PlannerDataFence(generation, account);
            ApplyCalendars(agenda.Calendars, selectedCalendar, fence);
            var rows = agenda.Events.Items
                .Select(item => new EventRow(item, fence))
                .ToArray();
            EventList.ItemsSource = rows;
            EventList.SelectedItem = rows.FirstOrDefault(row => row.Event.Id == selectedEventId);
            _loading = false;

            EventListStateText.Text = agenda.Events.TotalCount == 0
                ? "所选范围内暂无日程。"
                : $"显示 {rows.Length} / {agenda.Events.TotalCount} 个日程。";
            if (EventList.SelectedItem is null)
            {
                EventDetailText.Text = "选择日程查看详情。";
            }
        }
        catch (Exception ex)
        {
            if (!_closed && _eventUi.IsLoadCurrent(generation, account)
                && account == CurrentAccount())
            {
                EventList.ItemsSource = null;
                EventListStateText.Text = "读取日程失败。";
                ShowFailure("读取日程失败", ex);
            }
        }
        finally
        {
            _loading = false;
            _eventGate.Release();
        }
    }

    private async void OnRefreshTasks(object sender, RoutedEventArgs e)
        => await RefreshTasksAsync();

    private async void OnTaskFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_loading)
        {
            await RefreshTasksAsync();
        }
    }

    private async void OnTaskSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_loading)
        {
            await RefreshTasksAsync();
        }
    }

    private async Task RefreshTasksAsync()
    {
        var generation = _taskUi.BeginLoad();
        var account = CurrentAccount();
        var selectedTaskId = (TaskList.SelectedItem as TaskRow)?.Task.Id;
        await _taskGate.WaitAsync();
        try
        {
            if (_closed || !_taskUi.IsLoadCurrent(generation, account)
                || account != CurrentAccount())
            {
                return;
            }

            if (account is null)
            {
                ClearTaskUi("请先登录 PIM 账户；任务只从当前账户读取。");
                return;
            }

            TaskListStateText.Text = "正在读取任务…";
            var status = (TaskStatusFilterBox.SelectedItem as StatusChoice)?.Status;
            var workspace = await _planner.GetTaskWorkspaceAsync(
                string.IsNullOrWhiteSpace(TaskSearchBox.Text) ? null : TaskSearchBox.Text.Trim(),
                status);
            if (_closed || !_taskUi.IsLoadCurrent(generation, account)
                || account != CurrentAccount()
                || !_taskUi.CommitLoad(generation, account))
            {
                return;
            }

            _loading = true;
            var fence = new PlannerDataFence(generation, account);
            ApplyTaskBooks(workspace.TaskBooks, fence);
            var rows = workspace.Tasks.Items.Select(item => new TaskRow(item, fence)).ToArray();
            TaskList.ItemsSource = rows;
            TaskList.SelectedItem = rows.FirstOrDefault(row => row.Task.Id == selectedTaskId);
            _loading = false;

            TaskListStateText.Text = workspace.Tasks.TotalCount == 0
                ? "没有匹配的任务。"
                : $"显示 {rows.Length} / {workspace.Tasks.TotalCount} 个任务。";
            if (TaskList.SelectedItem is null)
            {
                TaskDetailText.Text = "选择任务查看详情。";
                CompleteTaskButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            if (!_closed && _taskUi.IsLoadCurrent(generation, account)
                && account == CurrentAccount())
            {
                TaskList.ItemsSource = null;
                TaskListStateText.Text = "读取任务失败。";
                ShowFailure("读取任务失败", ex);
            }
        }
        finally
        {
            _loading = false;
            _taskGate.Release();
        }
    }

    private void OnEventSelected(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not EventRow row
            || _eventUi.CheckWrite(row.Fence, CurrentAccount()) != PlannerFenceFailure.None)
        {
            EventDetailText.Text = "账号或服务器已变更，请重新读取日程。";
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

    private void OnTaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row
            || _taskUi.CheckWrite(row.Fence, CurrentAccount()) != PlannerFenceFailure.None)
        {
            TaskDetailText.Text = "账号或服务器已变更，请重新读取任务。";
            CompleteTaskButton.IsEnabled = false;
            return;
        }

        var item = row.Task;
        var book = (TaskBooksList.ItemsSource as IEnumerable<TaskBookRow>)?
            .FirstOrDefault(candidate => candidate.Book.Id == item.TaskBookId)?.Book.Name;
        CompleteTaskButton.IsEnabled = !string.Equals(item.Status, "COMPLETED", StringComparison.Ordinal)
            && !string.Equals(item.Status, "CANCELLED", StringComparison.Ordinal);
        TaskDetailText.Text =
            $"标题：{item.Title}\n" +
            $"状态：{StatusLabel(item.Status)} · 优先级：{item.Priority} · 完成度：{item.PercentComplete}%\n" +
            $"到期：{FormatOptional(item.Due)}\n" +
            $"任务本：{book ?? (string.IsNullOrWhiteSpace(item.TaskBookId) ? "无" : item.TaskBookId)}\n" +
            $"说明：{Fallback(item.Description)}";
    }

    private async void OnCreateEvent(object sender, RoutedEventArgs e)
    {
        if (EventCalendarBox.SelectedItem is not CalendarChoice
            {
                Id: { } calendarId,
                Fence: var calendarFence,
            })
        {
            MessageBox.Show(this, "请先在服务器上创建或选择一个可编辑日历。",
                "新建日程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadEventWindow(out var start, out var end, out var validation))
        {
            MessageBox.Show(this, validation, "新建日程", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var title = EventTitleBox.Text;
        var isAllDay = AllDayCheckBox.IsChecked == true;
        var location = EventLocationBox.Text;
        var description = EventDescriptionBox.Text;
        var account = CurrentAccount();
        var fenceFailure = _eventUi.CheckWrite(calendarFence, account);
        if (fenceFailure != PlannerFenceFailure.None)
        {
            EventCreateStateText.Text = FenceMessage(fenceFailure, "日程");
            return;
        }

        CreateEventButton.IsEnabled = false;
        EventCreateStateText.Text = "正在创建日程…";
        try
        {
            var created = await _planner.CreateEventAsync(
                calendarId,
                title,
                start,
                end,
                isAllDay,
                location,
                description);
            if (_closed || account != CurrentAccount())
            {
                return;
            }

            EventCreateStateText.Text = $"已创建：{created.Title}";
            EventTitleBox.Clear();
            EventLocationBox.Clear();
            EventDescriptionBox.Clear();
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            if (!_closed && account == CurrentAccount())
            {
                EventCreateStateText.Text = "创建日程失败；输入内容仍保留。";
                ShowFailure("创建日程失败", ex);
            }
        }
        finally
        {
            if (!_closed && account == CurrentAccount())
            {
                CreateEventButton.IsEnabled = true;
            }
        }
    }

    private async void OnCreateTask(object sender, RoutedEventArgs e)
    {
        if (TaskPriorityBox.SelectedItem is not int priority)
        {
            return;
        }

        var title = TaskTitleBox.Text;
        DateTimeOffset? due = null;
        if (TaskDueDate.SelectedDate is { } localDate)
        {
            due = ToLocalOffset(localDate.Date.AddHours(18));
        }

        if (TaskBookBox.SelectedItem is not TaskBookChoice taskBook)
        {
            TaskCreateStateText.Text = "请先读取当前账号的任务本。";
            return;
        }

        var account = CurrentAccount();
        var fenceFailure = _taskUi.CheckWrite(taskBook.Fence, account);
        if (fenceFailure != PlannerFenceFailure.None)
        {
            TaskCreateStateText.Text = FenceMessage(fenceFailure, "任务");
            return;
        }

        CreateTaskButton.IsEnabled = false;
        TaskCreateStateText.Text = "正在创建任务…";
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
            TaskDueDate.SelectedDate = null;
            await RefreshTasksAsync();
        }
        catch (Exception ex)
        {
            if (!_closed && account == CurrentAccount())
            {
                TaskCreateStateText.Text = "创建任务失败；输入内容仍保留。";
                ShowFailure("创建任务失败", ex);
            }
        }
        finally
        {
            if (!_closed && account == CurrentAccount())
            {
                CreateTaskButton.IsEnabled = true;
            }
        }
    }

    private async void OnCompleteTask(object sender, RoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row
            || !Guid.TryParse(row.Task.Id, out var taskId))
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
        if (fenceFailure != PlannerFenceFailure.None)
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

            TaskListStateText.Text = $"已标记完成：{updated.Title}";
            await RefreshTasksAsync();
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
                && TaskList.SelectedItem is TaskRow selected
                && _taskUi.CheckWrite(selected.Fence, account) == PlannerFenceFailure.None)
            {
                CompleteTaskButton.IsEnabled =
                    !string.Equals(selected.Task.Status, "COMPLETED", StringComparison.Ordinal)
                    && !string.Equals(selected.Task.Status, "CANCELLED", StringComparison.Ordinal);
            }
        }
    }

    private void OnAllDayChanged(object sender, RoutedEventArgs e)
    {
        var enabled = AllDayCheckBox.IsChecked != true;
        EventStartTimeBox.IsEnabled = enabled;
        EventEndTimeBox.IsEnabled = enabled;
    }

    private bool TryReadEventWindow(
        out DateTimeOffset start,
        out DateTimeOffset end,
        out string validation)
    {
        start = default;
        end = default;
        validation = string.Empty;
        if (EventStartDate.SelectedDate is not { } startDate)
        {
            validation = "请选择开始日期。";
            return false;
        }
        if (EventEndDate.SelectedDate is not { } endDate)
        {
            validation = "请选择结束日期。";
            return false;
        }

        if (AllDayCheckBox.IsChecked == true)
        {
            start = ToLocalOffset(startDate.Date);
            end = ToLocalOffset(endDate.Date.AddDays(1));
        }
        else
        {
            if (!TryParseTime(EventStartTimeBox.Text, out var startTime)
                || !TryParseTime(EventEndTimeBox.Text, out var endTime))
            {
                validation = "时间必须使用 HH:mm 格式，例如 09:30。";
                return false;
            }

            start = ToLocalOffset(startDate.Date.Add(startTime));
            end = ToLocalOffset(endDate.Date.Add(endTime));
        }

        if (end <= start)
        {
            validation = "日程结束时间必须晚于开始时间。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(EventTitleBox.Text))
        {
            validation = "日程标题不能为空。";
            return false;
        }

        return true;
    }

    private void ApplyCalendars(
        IReadOnlyList<CalendarResponse> calendars,
        Guid? selectedId,
        PlannerDataFence fence)
    {
        var editable = calendars
            .Where(calendar => calendar.CanEdit)
            .Select(calendar => new CalendarChoice(
                calendar.Name,
                Guid.Parse(calendar.Id),
                fence))
            .ToArray();
        var choices = new[] { CalendarChoice.All }
            .Concat(editable)
            .ToArray();
        CalendarFilterBox.ItemsSource = choices;
        CalendarFilterBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId)
            ?? choices[0];

        var priorEventCalendar = (EventCalendarBox.SelectedItem as CalendarChoice)?.Id;
        EventCalendarBox.ItemsSource = editable;
        EventCalendarBox.SelectedItem =
            editable.FirstOrDefault(choice => choice.Id == priorEventCalendar) ?? editable.FirstOrDefault();
    }

    private void ApplyTaskBooks(IReadOnlyList<TaskBookDto> books, PlannerDataFence fence)
    {
        var rows = books.Select(book => new TaskBookRow(book, fence)).ToArray();
        TaskBooksList.ItemsSource = rows;
        var prior = (TaskBookBox.SelectedItem as TaskBookChoice)?.Id;
        var choices = new[] { new TaskBookChoice("不指定任务本", null, fence) }
            .Concat(books.Select(book => new TaskBookChoice(
                book.Name,
                Guid.Parse(book.Id),
                fence)))
            .ToArray();
        TaskBookBox.ItemsSource = choices;
        TaskBookBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == prior) ?? choices[0];
    }

    private void ClearEventUi(string state)
    {
        EventList.ItemsSource = null;
        EventDetailText.Text = "选择日程查看详情。";
        EventListStateText.Text = state;
        CalendarFilterBox.ItemsSource = new[] { CalendarChoice.All };
        CalendarFilterBox.SelectedIndex = 0;
        EventCalendarBox.ItemsSource = Array.Empty<CalendarChoice>();
    }

    private void ClearTaskUi(string state)
    {
        TaskList.ItemsSource = null;
        TaskDetailText.Text = "选择任务查看详情。";
        TaskListStateText.Text = state;
        CompleteTaskButton.IsEnabled = false;
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

    private static bool TryParseTime(string value, out TimeSpan time)
        => TimeSpan.TryParseExact(
            value.Trim(),
            ["h\\:mm", "hh\\:mm"],
            CultureInfo.InvariantCulture,
            out time)
           && time >= TimeSpan.Zero
           && time < TimeSpan.FromDays(1);

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

        return $"{item.DtStart.ToLocalTime():yyyy-MM-dd HH:mm} - {item.DtEnd.ToLocalTime():HH:mm}";
    }

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
        _eventUi.Invalidate();
        _taskUi.Invalidate();
    }

    private void OnExternalScopeChanged()
    {
        _eventUi.Invalidate();
        _taskUi.Invalidate();
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

            ClearEventUi("账号或服务器已变更，请重新读取日程；未发送的输入内容仍保留。");
            ClearTaskUi("账号或服务器已变更，请重新读取任务；未发送的输入内容仍保留。");
        });
    }

    private sealed record RangeChoice(string Label, int Days)
    {
        public override string ToString() => Label;
    }

    private sealed record StatusChoice(string Label, string? Status)
    {
        public static StatusChoice All { get; } = new("全部状态", null);
        public override string ToString() => Label;
    }

    private sealed record CalendarChoice(
        string Label,
        Guid? Id,
        PlannerDataFence Fence = default)
    {
        public static CalendarChoice All { get; } = new("全部日历", null);
        public override string ToString() => Label;
    }

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

    private sealed class EventRow(EventResponse item, PlannerDataFence fence)
    {
        public EventResponse Event { get; } = item;
        public PlannerDataFence Fence { get; } = fence;
        public string Label => Event.Title;
        public string Subtitle =>
            $"{FormatEventTime(Event)} · {Fallback(Event.Location)}";
    }

    private sealed class TaskRow(TaskResponse item, PlannerDataFence fence)
    {
        public TaskResponse Task { get; } = item;
        public PlannerDataFence Fence { get; } = fence;
        public string Label => Task.Title;
        public string Subtitle =>
            $"优先级 {Task.Priority} · 到期 {FormatOptional(Task.Due)}";
        public string StatusLabel => NativePlannerWindow.StatusLabel(Task.Status);
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

    private sealed record TaskBookRow(TaskBookDto Book, PlannerDataFence Fence)
    {
        public string Label => $"{Book.Name} · {Book.Status} · {Book.TaskCount} 个任务";
    }
}
