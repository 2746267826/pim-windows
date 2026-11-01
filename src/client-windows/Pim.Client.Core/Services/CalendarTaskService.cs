using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

public sealed record CalendarAgenda(
    IReadOnlyList<CalendarResponse> Calendars,
    EventResponsePagedResult Events);

public sealed record TaskWorkspace(
    IReadOnlyList<TaskBookDto> TaskBooks,
    TaskResponsePagedResult Tasks);

/// <summary>Typed native calendar and task operations backed by the frozen contract.</summary>
public sealed class CalendarTaskService(ApiClient api)
{
    private readonly ApiClient _api = api ?? throw new ArgumentNullException(nameof(api));

    public async Task<CalendarAgenda> GetAgendaAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        string? search = null,
        Guid? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        if (to <= from)
            throw new ArgumentException("日程结束时间必须晚于开始时间。", nameof(to));

        using var scope = _api.BeginOperationScope();
        var calendarsResponse = await _api.GetCalendarsAsync(
            ct: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回日历列表。");
        var calendars = RequireResponse(
            calendarsResponse.Code,
            calendarsResponse.Message,
            calendarsResponse.Data) ?? [];

        var eventsResponse = await _api.GetCalendarEventsAsync(
            from,
            to,
            search,
            calendarId,
            pageSize: 200,
            ct: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回日程。");
        var events = RequireResponse(
            eventsResponse.Code,
            eventsResponse.Message,
            eventsResponse.Data);
        return new CalendarAgenda(calendars, events);
    }

    public async Task<TaskWorkspace> GetTaskWorkspaceAsync(
        string? search = null,
        string? status = null,
        Guid? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = _api.BeginOperationScope();
        var booksResponse = await _api.GetTaskBooksAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回任务本。");
        var books = RequireResponse(
            booksResponse.Code,
            booksResponse.Message,
            booksResponse.Data) ?? [];

        var tasksResponse = await _api.GetTasksAsync(
            search: search,
            calendarId: calendarId,
            status: status,
            pageSize: 200,
            ct: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回任务。");
        var tasks = RequireResponse(
            tasksResponse.Code,
            tasksResponse.Message,
            tasksResponse.Data);
        return new TaskWorkspace(books, tasks);
    }

    public async Task<EventResponse> CreateEventAsync(
        Guid calendarId,
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isAllDay,
        string? location,
        string? description,
        CancellationToken cancellationToken = default)
    {
        if (calendarId == Guid.Empty)
            throw new ArgumentException("请选择日历。", nameof(calendarId));
        var cleanTitle = RequireTitle(title, "日程标题");
        if (end <= start)
            throw new ArgumentException("日程结束时间必须晚于开始时间。", nameof(end));

        var response = await _api.CreateCalendarEventAsync(
            new CreateEventRequest
            {
                CalendarId = calendarId.ToString("D"),
                Description = CleanOptional(description),
                DtEnd = end,
                DtStart = start,
                IsAllDay = isAllDay,
                Location = CleanOptional(location),
                Title = cleanTitle,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回新日程。");
        var created = RequireResponse(response.Code, response.Message, response.Data);
        if (!string.Equals(created.CalendarId, calendarId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("服务器返回的新日程不属于所选举日历。");
        }

        return created;
    }

    public async Task<TaskResponse> CreateTaskAsync(
        string title,
        int priority,
        DateTimeOffset? due,
        Guid? calendarId,
        Guid? taskBookId,
        CancellationToken cancellationToken = default)
    {
        var cleanTitle = RequireTitle(title, "任务标题");
        if (priority is < 1 or > 9)
            throw new ArgumentOutOfRangeException(nameof(priority), "优先级必须在 1 到 9 之间。");

        var response = await _api.CreateTaskAsync(
            new CreateTaskRequest
            {
                CalendarId = calendarId?.ToString("D"),
                Due = due,
                Priority = priority,
                TaskBookId = taskBookId?.ToString("D"),
                Title = cleanTitle,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回新任务。");
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    public async Task<TaskResponse> SetTaskCompletedAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        if (taskId == Guid.Empty)
            throw new ArgumentException("任务标识不能为空。", nameof(taskId));
        var response = await _api.UpdateTaskAsync(
            taskId,
            new UpdateTaskRequest
            {
                PercentComplete = 100,
                Status = UpdateTaskRequestStatus.COMPLETED,
            },
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回更新后的任务。");
        var task = RequireResponse(response.Code, response.Message, response.Data);
        if (!string.Equals(task.Id, taskId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("服务器返回的任务与所选任务不一致。");
        }

        return task;
    }

    private static T RequireResponse<T>(int code, string message, T? data)
        where T : class
    {
        if (code != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "服务器操作失败。" : message);
        }

        return data ?? throw new InvalidDataException("服务器响应没有数据。");
    }

    private static string RequireTitle(string title, string field)
    {
        var clean = title.Trim();
        if (clean.Length == 0)
            throw new ArgumentException($"{field}不能为空。", nameof(title));
        if (clean.Length > 255)
            throw new ArgumentException($"{field}不能超过 255 个字符。", nameof(title));
        return clean;
    }

    private static string? CleanOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
