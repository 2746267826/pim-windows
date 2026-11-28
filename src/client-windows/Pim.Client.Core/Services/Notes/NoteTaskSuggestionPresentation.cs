using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

public static class NoteTaskSuggestionPresentation
{
    public static string KindLabel(NoteTaskSuggestionKindDto kind)
        => kind == NoteTaskSuggestionKindDto.Event ? "日程" : "任务";

    public static string StatusLabel(NoteTaskSuggestionDto suggestion)
        => suggestion.Status switch
        {
            NoteTaskSuggestionStatusDto.Confirmed =>
                suggestion.Kind == NoteTaskSuggestionKindDto.Event ? "已创建日程" : "已创建任务",
            NoteTaskSuggestionStatusDto.Dismissed => "已放弃",
            NoteTaskSuggestionStatusDto.Draft => "待确认",
            _ => suggestion.Status.ToString(),
        };

    public static string ReceiptSummary(NoteTaskSuggestionDto suggestion)
    {
        if (suggestion.Status != NoteTaskSuggestionStatusDto.Confirmed)
            return StatusLabel(suggestion);

        return suggestion.Kind == NoteTaskSuggestionKindDto.Event
            ? $"日程已创建并完成服务器核实 · 事件 {suggestion.EventId}"
            : $"任务已创建并完成服务器核实 · 任务 {suggestion.TaskId}";
    }

    public static string Detail(NoteTaskSuggestionDto suggestion)
    {
        var parts = new List<string>
        {
            KindLabel(suggestion.Kind),
            $"优先级 {suggestion.Priority}",
        };

        if (suggestion.Kind == NoteTaskSuggestionKindDto.Event)
        {
            parts.Add(NoteTaskSuggestionEventTime.FormatRange(
                suggestion.Start,
                suggestion.End,
                suggestion.TimeZoneId));
            if (suggestion.Status == NoteTaskSuggestionStatusDto.Confirmed
                && !string.IsNullOrWhiteSpace(suggestion.EventId))
            {
                parts.Add($"事件 {suggestion.EventId}");
            }
        }
        else if (suggestion.Status == NoteTaskSuggestionStatusDto.Confirmed
                 && !string.IsNullOrWhiteSpace(suggestion.TaskId))
        {
            parts.Add($"任务 {suggestion.TaskId}");
        }

        parts.Add($"修订 {suggestion.Revision[..Math.Min(8, suggestion.Revision.Length)]}");
        return string.Join(" · ", parts);
    }
}
