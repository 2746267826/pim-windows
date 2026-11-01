using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.Client.Tests;

public sealed class CalendarTaskServiceTests
{
    private const string CalendarId = "11111111-1111-1111-1111-111111111111";
    private const string TaskId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task AgendaAndTaskWorkspaceUseTypedContractReads()
    {
        var handler = new PlannerHandler();
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-planner-token");
        var service = new CalendarTaskService(api);
        var from = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var agenda = await service.GetAgendaAsync(
            from,
            from.AddDays(7),
            search: "评审",
            calendarId: Guid.Parse(CalendarId));
        var workspace = await service.GetTaskWorkspaceAsync(
            search: "Windows",
            status: "NEEDS-ACTION");

        Assert.Single(agenda.Calendars);
        Assert.Single(agenda.Events.Items);
        Assert.Equal("设计评审", agenda.Events.Items[0].Title);
        Assert.Single(workspace.TaskBooks);
        Assert.Single(workspace.Tasks.Items);
        Assert.Equal("Windows 回归", workspace.Tasks.Items[0].Title);

        Assert.Equal(
            [
                "/api/v1/calendar/calendars",
                "/api/v1/calendar/events",
                "/api/v1/calendar/task-books",
                "/api/v1/calendar/tasks",
            ],
            handler.Calls.Select(call => call.Path));
        Assert.Contains("search=%E8%AF%84%E5%AE%A1", handler.Calls[1].Query, StringComparison.Ordinal);
        Assert.Contains("calendarId=" + CalendarId, handler.Calls[1].Query, StringComparison.Ordinal);
        Assert.Contains("status=NEEDS-ACTION", handler.Calls[3].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateActionsValidateIdentityAndSerializeWireStatuses()
    {
        var handler = new PlannerHandler();
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-planner-token");
        var service = new CalendarTaskService(api);
        var start = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

        var createdEvent = await service.CreateEventAsync(
            Guid.Parse(CalendarId),
            "设计评审",
            start,
            start.AddHours(1),
            isAllDay: false,
            location: "会议室 A",
            description: "确认 Windows 原生日程。");
        var createdTask = await service.CreateTaskAsync(
            "Windows 回归",
            priority: 3,
            due: start.AddDays(1),
            calendarId: Guid.Parse(CalendarId),
            taskBookId: null);
        var completed = await service.SetTaskCompletedAsync(Guid.Parse(TaskId));

        Assert.Equal(CalendarId, createdEvent.CalendarId);
        Assert.Equal(TaskId, createdTask.Id);
        Assert.Equal("COMPLETED", completed.Status);

        var eventBody = Assert.Single(
            handler.Calls,
            call => call.Method == "POST" && call.Path == "/api/v1/calendar/events").Body!;
        using var eventDocument = JsonDocument.Parse(eventBody);
        Assert.Equal(CalendarId, eventDocument.RootElement.GetProperty("calendarId").GetString());
        Assert.Equal("设计评审", eventDocument.RootElement.GetProperty("title").GetString());

        var taskBody = Assert.Single(
            handler.Calls,
            call => call.Method == "POST" && call.Path == "/api/v1/calendar/tasks").Body!;
        using var taskDocument = JsonDocument.Parse(taskBody);
        Assert.Equal(3, taskDocument.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal(JsonValueKind.Null, taskDocument.RootElement.GetProperty("status").ValueKind);

        var updateBody = Assert.Single(handler.Calls, call => call.Method == "PUT").Body!;
        using var updateDocument = JsonDocument.Parse(updateBody);
        Assert.Equal("COMPLETED", updateDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(100, updateDocument.RootElement.GetProperty("percentComplete").GetInt32());
        Assert.Equal(JsonValueKind.Null, updateDocument.RootElement.GetProperty("title").ValueKind);
    }

    [Fact]
    public async Task MismatchedServerIdentityIsRejected()
    {
        var api = new ApiClient(() => new PlannerHandler(updateTaskId: Guid.NewGuid().ToString("D")));
        api.SetAccessToken("synthetic-planner-token");
        var service = new CalendarTaskService(api);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SetTaskCompletedAsync(Guid.Parse(TaskId)));
    }

    private sealed class PlannerHandler(string? updateTaskId = null) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Query, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                body));

            var path = request.RequestUri.AbsolutePath;
            if (path == "/api/v1/calendar/calendars")
            {
                return Json(
                    """
                    {
                      "code": 0,
                      "data": [{
                        "id": "11111111-1111-1111-1111-111111111111",
                        "name": "工作",
                        "kind": "PERSONAL",
                        "color": "#2563EB",
                        "eventCount": 1,
                        "isDefault": true
                      }],
                      "message": "ok",
                      "timestamp": "2026-11-01T00:00:00Z"
                    }
                    """);
            }

            if (path == "/api/v1/calendar/events" && request.Method == HttpMethod.Get)
            {
                return Json(
                    """
                    {
                      "code": 0,
                      "data": {
                        "items": [{
                          "id": "22222222-2222-2222-2222-222222222222",
                          "calendarId": "11111111-1111-1111-1111-111111111111",
                          "title": "设计评审",
                          "dtStart": "2026-11-01T09:00:00Z",
                          "dtEnd": "2026-11-01T10:00:00Z",
                          "source": "Local",
                          "status": "CONFIRMED",
                          "uid": "event-1"
                        }],
                        "page": 1,
                        "pageSize": 200,
                        "totalCount": 1,
                        "totalPages": 1
                      },
                      "message": "ok",
                      "timestamp": "2026-11-01T00:00:00Z"
                    }
                    """);
            }

            if (path == "/api/v1/calendar/events" && request.Method == HttpMethod.Post)
            {
                return Json(
                    """
                    {
                      "code": 0,
                      "data": {
                        "id": "22222222-2222-2222-2222-222222222222",
                        "calendarId": "11111111-1111-1111-1111-111111111111",
                        "title": "设计评审",
                        "dtStart": "2026-11-01T09:00:00Z",
                        "dtEnd": "2026-11-01T10:00:00Z",
                        "source": "Local",
                        "status": "CONFIRMED",
                        "uid": "event-1"
                      },
                      "message": "created",
                      "timestamp": "2026-11-01T00:00:00Z"
                    }
                    """);
            }

            if (path == "/api/v1/calendar/task-books")
            {
                return Json(
                    """
                    {
                      "code": 0,
                      "data": [{
                        "id": "44444444-4444-4444-4444-444444444444",
                        "name": "Windows",
                        "kind": "PROJECT",
                        "status": "ACTIVE",
                        "taskCount": 1
                      }],
                      "message": "ok",
                      "timestamp": "2026-11-01T00:00:00Z"
                    }
                    """);
            }

            if (path == "/api/v1/calendar/tasks" || path.StartsWith("/api/v1/calendar/tasks/", StringComparison.Ordinal))
            {
                var id = path.StartsWith("/api/v1/calendar/tasks/", StringComparison.Ordinal)
                    ? updateTaskId ?? TaskId
                    : TaskId;
                var status = request.Method == HttpMethod.Put ? "COMPLETED" : "NEEDS-ACTION";
                if (request.Method == HttpMethod.Get)
                {
                    return Json(
                        $$"""
                        {
                          "code": 0,
                          "data": {
                            "items": [{{TaskJson(id, status)}}],
                            "page": 1,
                            "pageSize": 200,
                            "totalCount": 1,
                            "totalPages": 1
                          },
                          "message": "ok",
                          "timestamp": "2026-11-01T00:00:00Z"
                        }
                        """);
                }

                return Json(
                    $$"""
                    {
                      "code": 0,
                      "data": {{TaskJson(id, status)}},
                      "message": "ok",
                      "timestamp": "2026-11-01T00:00:00Z"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string TaskJson(string id, string status)
            => $$"""
                {
                  "id": "{{id}}",
                  "title": "Windows 回归",
                  "status": "{{status}}",
                  "priority": 3,
                  "sortOrder": 0,
                  "isInbox": false,
                  "percentComplete": {{(status == "COMPLETED" ? 100 : 0)}},
                  "uid": "task-1",
                  "subTasks": []
                }
                """;

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
