using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NoteTaskSuggestionCalendarPolicyTests
{
    private const string EventCalendarId = "11111111-1111-1111-1111-111111111111";
    private const string TaskCalendarId = "22222222-2222-2222-2222-222222222222";
    private const string ReadOnlyCalendarId = "33333333-3333-3333-3333-333333333333";
    private const string OutlookCalendarId = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public void EventChoicesExcludeTaskReadOnlyAndOutlookBoundCalendars()
    {
        var calendars = new[]
        {
            Calendar(EventCalendarId, "可写日程", kind: "calendar"),
            Calendar(TaskCalendarId, "任务日历", kind: "task"),
            Calendar(ReadOnlyCalendarId, "只读日程", kind: "calendar", canEdit: false),
            Calendar(
                OutlookCalendarId,
                "Outlook 日程",
                kind: "calendar",
                outlookBindingId: "55555555-5555-5555-5555-555555555555"),
        };

        var selectable = NoteTaskSuggestionCalendarPolicy.SelectableCalendars(
            calendars,
            NoteTaskSuggestionKindDto.Event);
        var choices = NoteTaskSuggestionCalendarPolicy.BuildChoices(
            calendars,
            NoteTaskSuggestionKindDto.Event);

        Assert.Equal([EventCalendarId], selectable.Select(item => item.Id).ToArray());
        Assert.Equal(
            new string?[] { null, EventCalendarId },
            choices.Select(item => item.Id).ToArray());
        Assert.True(choices[0].UsesServerDefault);
        Assert.Equal(NoteTaskSuggestionCalendarPolicy.EventDefaultLabel, choices[0].Label);
        Assert.DoesNotContain(choices, item => item.Id == TaskCalendarId);
        Assert.DoesNotContain(choices, item => item.Id == ReadOnlyCalendarId);
        Assert.DoesNotContain(choices, item => item.Id == OutlookCalendarId);
    }

    [Fact]
    public void TaskChoicesKeepTheExistingAllCalendarBehavior()
    {
        var calendars = new[]
        {
            Calendar(EventCalendarId, "可写日程", kind: "calendar"),
            Calendar(TaskCalendarId, "任务日历", kind: "task"),
            Calendar(ReadOnlyCalendarId, "只读日程", kind: "calendar", canEdit: false),
            Calendar(
                OutlookCalendarId,
                "Outlook 日程",
                kind: "calendar",
                outlookBindingId: "55555555-5555-5555-5555-555555555555"),
        };

        var choices = NoteTaskSuggestionCalendarPolicy.BuildChoices(
            calendars,
            NoteTaskSuggestionKindDto.Task);

        Assert.Equal(
            new string?[]
            {
                null,
                EventCalendarId,
                TaskCalendarId,
                ReadOnlyCalendarId,
                OutlookCalendarId,
            },
            choices.Select(item => item.Id).ToArray());
        Assert.Equal(NoteTaskSuggestionCalendarPolicy.TaskDefaultLabel, choices[0].Label);
        Assert.True(NoteTaskSuggestionCalendarPolicy.ShouldEnablePicker(
            NoteTaskSuggestionKindDto.Task,
            canEdit: true,
            selectableCount: 0));
        Assert.False(NoteTaskSuggestionCalendarPolicy.ShouldEnablePicker(
            NoteTaskSuggestionKindDto.Task,
            canEdit: false,
            selectableCount: 0));
    }

    [Fact]
    public void EventWithoutEligibleCalendarsKeepsServerDefaultAndShowsExplicitState()
    {
        var calendars = new[]
        {
            Calendar(TaskCalendarId, "任务日历", kind: "task"),
            Calendar(ReadOnlyCalendarId, "只读日程", kind: "calendar", canEdit: false),
            Calendar(
                OutlookCalendarId,
                "Outlook 日程",
                kind: "calendar",
                outlookBindingId: "55555555-5555-5555-5555-555555555555"),
        };

        var choices = NoteTaskSuggestionCalendarPolicy.BuildChoices(
            calendars,
            NoteTaskSuggestionKindDto.Event);
        var selected = NoteTaskSuggestionCalendarPolicy.ResolveChoice(choices, OutlookCalendarId);
        var hint = NoteTaskSuggestionCalendarPolicy.SelectionHint(
            NoteTaskSuggestionKindDto.Event,
            selectableCount: 0,
            preferredId: OutlookCalendarId,
            selected);

        var choice = Assert.Single(choices);
        Assert.True(choice.UsesServerDefault);
        Assert.Null(choice.Id);
        Assert.Equal(NoteTaskSuggestionCalendarPolicy.EventDefaultLabel, choice.Label);
        Assert.Same(choice, selected);
        Assert.False(NoteTaskSuggestionCalendarPolicy.ShouldEnablePicker(
            NoteTaskSuggestionKindDto.Event,
            canEdit: true,
            selectableCount: 0));
        Assert.Contains("服务器默认日程日历", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidEventCalendarRemainsSelectedAndPickerStaysEnabled()
    {
        var calendars = new[]
        {
            Calendar(EventCalendarId, "可写日程", kind: "calendar"),
            Calendar(TaskCalendarId, "任务日历", kind: "task"),
        };

        var choices = NoteTaskSuggestionCalendarPolicy.BuildChoices(
            calendars,
            NoteTaskSuggestionKindDto.Event);
        var selected = NoteTaskSuggestionCalendarPolicy.ResolveChoice(choices, EventCalendarId);

        Assert.Equal(EventCalendarId, selected.Id);
        Assert.False(selected.UsesServerDefault);
        Assert.True(NoteTaskSuggestionCalendarPolicy.ShouldEnablePicker(
            NoteTaskSuggestionKindDto.Event,
            canEdit: true,
            selectableCount: 1));
    }

    [Fact]
    public void EventPreferredIneligibleCalendarFallsBackToServerDefaultWithWarning()
    {
        var calendars = new[]
        {
            Calendar(EventCalendarId, "可写日程", kind: "calendar"),
            Calendar(TaskCalendarId, "任务日历", kind: "task"),
        };

        var choices = NoteTaskSuggestionCalendarPolicy.BuildChoices(
            calendars,
            NoteTaskSuggestionKindDto.Event);
        var selected = NoteTaskSuggestionCalendarPolicy.ResolveChoice(choices, TaskCalendarId);
        var hint = NoteTaskSuggestionCalendarPolicy.SelectionHint(
            NoteTaskSuggestionKindDto.Event,
            selectableCount: 1,
            preferredId: TaskCalendarId,
            selected);

        Assert.True(selected.UsesServerDefault);
        Assert.Null(selected.Id);
        Assert.Contains("原建议日历不可用于日程", hint, StringComparison.Ordinal);
        Assert.Contains("服务器默认日程日历", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void EventCalendarTreatsBlankOutlookBindingAsUnbound()
    {
        var calendars = new[]
        {
            Calendar(EventCalendarId, "可写日程", kind: "calendar", outlookBindingId: " "),
        };

        var selectable = NoteTaskSuggestionCalendarPolicy.SelectableCalendars(
            calendars,
            NoteTaskSuggestionKindDto.Event);

        Assert.Equal([EventCalendarId], selectable.Select(item => item.Id).ToArray());
    }

    private static CalendarResponse Calendar(
        string id,
        string name,
        string kind,
        bool canEdit = true,
        string? outlookBindingId = null)
        => new()
        {
            Id = id,
            Name = name,
            Color = "#336699",
            Kind = kind,
            IsDefault = false,
            EventCount = 0,
            CanEdit = canEdit,
            OutlookCalendarBindingId = outlookBindingId,
        };
}
