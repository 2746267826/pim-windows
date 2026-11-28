using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

public sealed record NoteTaskSuggestionCalendarChoice(
    string? Id,
    string Label,
    bool UsesServerDefault);

public static class NoteTaskSuggestionCalendarPolicy
{
    public const string TaskDefaultLabel = "不指定日历";
    public const string EventDefaultLabel = "服务器默认日程日历";
    public const string NoEventCalendarHint =
        "当前账号没有可写的非 Outlook 日程日历；将使用服务器默认日程日历。";

    public static IReadOnlyList<CalendarResponse> SelectableCalendars(
        IEnumerable<CalendarResponse> calendars,
        NoteTaskSuggestionKindDto kind)
    {
        ArgumentNullException.ThrowIfNull(calendars);
        return calendars
            .Where(calendar => Guid.TryParse(calendar.Id, out _))
            .Where(calendar => kind != NoteTaskSuggestionKindDto.Event
                               || IsWritableEventCalendar(calendar))
            .ToArray();
    }

    public static bool IsWritableEventCalendar(CalendarResponse calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return string.Equals(calendar.Kind, "calendar", StringComparison.Ordinal)
               && calendar.CanEdit
               && string.IsNullOrWhiteSpace(calendar.OutlookCalendarBindingId);
    }

    public static IReadOnlyList<NoteTaskSuggestionCalendarChoice> BuildChoices(
        IEnumerable<CalendarResponse> calendars,
        NoteTaskSuggestionKindDto kind)
    {
        var choices = new List<NoteTaskSuggestionCalendarChoice>
        {
            new(
                null,
                kind == NoteTaskSuggestionKindDto.Event ? EventDefaultLabel : TaskDefaultLabel,
                UsesServerDefault: true),
        };
        choices.AddRange(SelectableCalendars(calendars, kind)
            .Select(calendar => new NoteTaskSuggestionCalendarChoice(
                calendar.Id,
                calendar.Name,
                UsesServerDefault: false)));
        return choices;
    }

    public static NoteTaskSuggestionCalendarChoice ResolveChoice(
        IReadOnlyList<NoteTaskSuggestionCalendarChoice> choices,
        string? preferredId)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
            throw new ArgumentException("日历选项不能为空。", nameof(choices));
        return choices.FirstOrDefault(choice =>
                   string.Equals(choice.Id, preferredId, StringComparison.Ordinal))
               ?? choices[0];
    }

    public static bool ShouldEnablePicker(
        NoteTaskSuggestionKindDto kind,
        bool canEdit,
        int selectableCount)
        => canEdit
           && (kind != NoteTaskSuggestionKindDto.Event || selectableCount > 0);

    public static string SelectionHint(
        NoteTaskSuggestionKindDto kind,
        int selectableCount,
        string? preferredId,
        NoteTaskSuggestionCalendarChoice selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (kind != NoteTaskSuggestionKindDto.Event)
            return "任务模式保留全部日历选项；选择“不指定日历”时由服务端决定。";
        if (selectableCount == 0)
            return NoEventCalendarHint;
        if (preferredId is not null && selected.UsesServerDefault)
        {
            return "原建议日历不可用于日程（需为可写的非 Outlook 日程日历）；"
                   + "已回退到服务器默认日程日历，保存或确认会清除原 calendarId。";
        }

        return "仅列出可写的非 Outlook 日程日历；选择服务器默认日程日历可交由服务端创建或选择默认日历。";
    }
}
