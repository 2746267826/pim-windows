using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NoteTaskSuggestionEventTests
{
    private const string EventSuggestionId = "33333333-3333-3333-3333-333333333333";
    private const string EventReceiptId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Fact]
    public void TaskAndEventPayloadsDeserializeKindTimesAndReceiptFields()
    {
        var task = JsonSerializer.Deserialize<NoteTaskSuggestionDto>(
            """
            {
              "artifactId":"11111111-1111-1111-1111-111111111111",
              "calendarId":null,
              "confirmedAt":null,
              "createdAt":"2026-11-02T00:00:00Z",
              "description":null,
              "dismissedAt":null,
              "due":"2026-11-20T01:00:00Z",
              "end":null,
              "eventId":null,
              "extractionId":"22222222-2222-2222-2222-222222222222",
              "id":"33333333-3333-3333-3333-333333333333",
              "kind":"Task",
              "noteId":"44444444-4444-4444-4444-444444444444",
              "ordinal":0,
              "priority":5,
              "revision":"55555555-5555-5555-5555-555555555555",
              "sourceContentSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "sourceGenerationTaskId":null,
              "sourceInputSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "sourceManifestSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "start":null,
              "status":"Draft",
              "taskBookId":null,
              "taskId":null,
              "timeZoneId":null,
              "title":"任务",
              "updatedAt":"2026-11-02T00:00:00Z",
              "versionId":"66666666-6666-6666-6666-666666666666"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var evt = JsonSerializer.Deserialize<NoteTaskSuggestionDto>(
            """
            {
              "artifactId":"11111111-1111-1111-1111-111111111111",
              "calendarId":"77777777-7777-7777-7777-777777777777",
              "confirmedAt":"2026-11-22T00:00:00Z",
              "createdAt":"2026-11-02T00:00:00Z",
              "description":"线上答疑",
              "dismissedAt":null,
              "due":null,
              "end":"2026-11-21T12:00:00Z",
              "eventId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "extractionId":"22222222-2222-2222-2222-222222222222",
              "id":"33333333-3333-3333-3333-333333333333",
              "kind":"Event",
              "noteId":"44444444-4444-4444-4444-444444444444",
              "ordinal":1,
              "priority":9,
              "revision":"55555555-5555-5555-5555-555555555555",
              "sourceContentSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "sourceGenerationTaskId":null,
              "sourceInputSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
              "sourceManifestSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
              "start":"2026-11-21T11:00:00Z",
              "status":"Confirmed",
              "taskBookId":null,
              "taskId":null,
              "timeZoneId":"Asia/Shanghai",
              "title":"机器学习答疑",
              "updatedAt":"2026-11-22T00:00:00Z",
              "versionId":"66666666-6666-6666-6666-666666666666"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(task);
        Assert.Equal(NoteTaskSuggestionKindDto.Task, task.Kind);
        Assert.Null(task.Start);
        Assert.Null(task.EventId);
        Assert.NotNull(evt);
        Assert.Equal(NoteTaskSuggestionKindDto.Event, evt.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), evt.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T12:00:00Z"), evt.End);
        Assert.Equal("Asia/Shanghai", evt.TimeZoneId);
        Assert.Null(evt.TaskId);
        Assert.Equal(EventReceiptId, evt.EventId);
    }

    [Fact]
    public void IanaValidationRejectsWindowsAndFixedOffsetIds()
    {
        Assert.True(NoteTaskSuggestionEventTime.IsIanaTimeZone("Asia/Shanghai"));
        Assert.True(NoteTaskSuggestionEventTime.IsIanaTimeZone("America/New_York"));
        Assert.True(NoteTaskSuggestionEventTime.IsIanaTimeZone("UTC"));
        Assert.False(NoteTaskSuggestionEventTime.IsIanaTimeZone("China Standard Time"));
        Assert.False(NoteTaskSuggestionEventTime.IsIanaTimeZone("Pacific Standard Time"));
        Assert.False(NoteTaskSuggestionEventTime.IsIanaTimeZone("+08:00"));
        Assert.False(NoteTaskSuggestionEventTime.IsIanaTimeZone("UTC+8"));
        Assert.False(NoteTaskSuggestionEventTime.IsIanaTimeZone("Mars/Olympus"));
    }

    [Fact]
    public void WallClockConversionUsesTheEventIanaZoneAndRoundTrips()
    {
        Assert.True(NoteTaskSuggestionEventTime.TryFromWallClock(
            new DateTime(2026, 11, 21),
            "19:00",
            "Asia/Shanghai",
            out var start,
            out var error));
        Assert.Equal("", error);
        Assert.Equal(DateTimeOffset.Parse("2026-11-21T11:00:00Z"), start);

        Assert.True(NoteTaskSuggestionEventTime.TryToWallClock(
            start,
            "Asia/Shanghai",
            out var date,
            out var time,
            out error));
        Assert.Equal("", error);
        Assert.Equal(new DateTime(2026, 11, 21), date);
        Assert.Equal("19:00", time);
        Assert.Equal(
            "2026-11-21 19:00–20:00 · Asia/Shanghai",
            NoteTaskSuggestionEventTime.FormatRange(
                start,
                DateTimeOffset.Parse("2026-11-21T12:00:00Z"),
                "Asia/Shanghai"));
    }

    [Fact]
    public void WallClockConversionTracksDaylightSavingBoundaries()
    {
        Assert.True(NoteTaskSuggestionEventTime.TryFromWallClock(
            new DateTime(2026, 3, 9),
            "09:00",
            "America/New_York",
            out var summer,
            out _));
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T13:00:00Z"), summer);

        Assert.True(NoteTaskSuggestionEventTime.TryFromWallClock(
            new DateTime(2026, 11, 2),
            "09:00",
            "America/New_York",
            out var winter,
            out _));
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T14:00:00Z"), winter);
    }

    [Fact]
    public void WallClockConversionFailsClosedForDstGapAndAmbiguousTime()
    {
        Assert.False(NoteTaskSuggestionEventTime.TryFromWallClock(
            new DateTime(2026, 3, 8),
            "02:30",
            "America/New_York",
            out _,
            out var gapError));
        Assert.Contains("不存在", gapError);

        Assert.False(NoteTaskSuggestionEventTime.TryFromWallClock(
            new DateTime(2026, 11, 1),
            "01:30",
            "America/New_York",
            out _,
            out var ambiguousError));
        Assert.Contains("重复", ambiguousError);
    }

    [Fact]
    public void UnchangedAmbiguousWallClockPreservesTheStoredInstant()
    {
        var preserved = DateTimeOffset.Parse("2026-11-01T05:30:00Z");
        Assert.True(NoteTaskSuggestionEventTime.TryToWallClock(
            preserved,
            "America/New_York",
            out var date,
            out var time,
            out _));

        Assert.True(NoteTaskSuggestionEventTime.TryFromWallClockOrPreserve(
            date,
            time,
            "America/New_York",
            preserved,
            out var value,
            out var error));
        Assert.Equal("", error);
        Assert.Equal(preserved, value);
    }

    [Fact]
    public void ConfirmedEventPresentationShowsEventIdInsteadOfTaskId()
    {
        var suggestion = new NoteTaskSuggestionDto
        {
            ArtifactId = "11111111-1111-1111-1111-111111111111",
            CalendarId = "77777777-7777-7777-7777-777777777777",
            ConfirmedAt = DateTimeOffset.Parse("2026-11-22T00:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-11-02T00:00:00Z"),
            End = DateTimeOffset.Parse("2026-11-21T12:00:00Z"),
            EventId = EventReceiptId,
            ExtractionId = "22222222-2222-2222-2222-222222222222",
            Id = EventSuggestionId,
            Kind = NoteTaskSuggestionKindDto.Event,
            NoteId = "44444444-4444-4444-4444-444444444444",
            Ordinal = 1,
            Priority = 9,
            Revision = "55555555-5555-5555-5555-555555555555",
            SourceContentSha256 = new string('a', 64),
            SourceInputSha256 = new string('b', 64),
            SourceManifestSha256 = new string('c', 64),
            Start = DateTimeOffset.Parse("2026-11-21T11:00:00Z"),
            Status = NoteTaskSuggestionStatusDto.Confirmed,
            TaskId = null,
            TimeZoneId = "Asia/Shanghai",
            Title = "机器学习答疑",
            UpdatedAt = DateTimeOffset.Parse("2026-11-22T00:00:00Z"),
            VersionId = "66666666-6666-6666-6666-666666666666",
        };

        var receipt = NoteTaskSuggestionPresentation.ReceiptSummary(suggestion);
        var detail = NoteTaskSuggestionPresentation.Detail(suggestion);

        Assert.Contains("日程", receipt);
        Assert.Contains(EventReceiptId, receipt);
        Assert.Contains(EventReceiptId, detail);
        Assert.DoesNotContain("任务 ", receipt);
    }
}
