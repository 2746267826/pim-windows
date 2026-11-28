namespace Pim.Client.Core.Services.Notes;

public enum NativeNoteLibraryState
{
    Loading,
    EmptyLibrary,
    EmptySearch,
    Error,
    Ready,
}

public sealed record NativeNoteLibraryPresentation(
    NativeNoteLibraryState State,
    string Title,
    string Detail,
    bool ShowProgress,
    bool ShowCreateAction,
    bool ShowClearSearchAction,
    bool ShowRetryAction,
    bool ShowList = false,
    bool ShowOverlay = false,
    bool ShowInlineProgress = false,
    bool ShowInlineError = false);

public static class NativeNotePresentation
{
    public static NativeNoteLibraryPresentation Library(
        bool isLoading,
        bool hasError,
        int noteCount,
        string? query)
    {
        if (isLoading)
        {
            var hasExistingNotes = noteCount > 0;
            return new(
                NativeNoteLibraryState.Loading,
                "正在读取本机笔记…",
                "笔记与原始材料只保存在这台电脑上。",
                ShowProgress: true,
                ShowCreateAction: false,
                ShowClearSearchAction: false,
                ShowRetryAction: false,
                ShowList: hasExistingNotes,
                ShowOverlay: !hasExistingNotes,
                ShowInlineProgress: hasExistingNotes);
        }

        if (hasError)
        {
            var hasExistingNotes = noteCount > 0;
            return new(
                NativeNoteLibraryState.Error,
                "本机笔记暂时读不出来",
                "原有笔记没有被改动。检查磁盘权限后可以重试。",
                ShowProgress: false,
                ShowCreateAction: false,
                ShowClearSearchAction: false,
                ShowRetryAction: true,
                ShowList: hasExistingNotes,
                ShowOverlay: !hasExistingNotes,
                ShowInlineError: hasExistingNotes);
        }

        if (noteCount > 0)
        {
            return new(
                NativeNoteLibraryState.Ready,
                string.Empty,
                string.Empty,
                ShowProgress: false,
                ShowCreateAction: false,
                ShowClearSearchAction: false,
                ShowRetryAction: false,
                ShowList: true);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            return new(
                NativeNoteLibraryState.EmptySearch,
                "没有找到匹配的笔记",
                "可以换个关键词，或清空搜索后查看全部笔记。",
                ShowProgress: false,
                ShowCreateAction: false,
                ShowClearSearchAction: true,
                ShowRetryAction: false,
                ShowOverlay: true);
        }

        return new(
            NativeNoteLibraryState.EmptyLibrary,
            "从一页空白开始",
            "新建一篇笔记，正文和录音都会先保存在本机。",
            ShowProgress: false,
            ShowCreateAction: true,
            ShowClearSearchAction: false,
            ShowRetryAction: false,
            ShowOverlay: true);
    }

    public static string WindowTitle(string? noteTitle)
    {
        return string.IsNullOrWhiteSpace(noteTitle)
            ? "无标题笔记"
            : $"{noteTitle.Trim()} · 笔记";
    }

    public static string SessionFinished(Guid versionId)
        => $"已结束并保存本机固定版本 {versionId.ToString("N")[..8]}；尚未自动上传。";

    public static string RecordingClock(TimeSpan elapsed)
    {
        var bounded = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return $"{(long)bounded.TotalHours:00}:{bounded.Minutes:00}:{bounded.Seconds:00}";
    }
}
