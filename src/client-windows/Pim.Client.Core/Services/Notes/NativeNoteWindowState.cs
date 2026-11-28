using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Window-level binding state for the active note title. The WPF Title and
/// context label bind to this object so every editor/title transition follows
/// the same tested path.
/// </summary>
public sealed class NativeNoteWindowState : INotifyPropertyChanged
{
    private Guid? _noteId;
    private string _windowTitle = "笔记库";
    private string _editorContext = "从一页空白开始";
    private bool _isRecording;
    private int _recordingSegment;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid? NoteId => _noteId;

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetField(ref _windowTitle, value);
    }

    public string EditorContext
    {
        get => _editorContext;
        private set => SetField(ref _editorContext, value);
    }

    public int RecordingSegment => _recordingSegment;

    public void ShowLibrary(bool hasNotes)
    {
        _noteId = null;
        _isRecording = false;
        _recordingSegment = 0;
        WindowTitle = hasNotes ? "选择一篇笔记" : "笔记库";
        EditorContext = hasNotes ? "选择一篇笔记继续" : "从一页空白开始";
    }

    public void OpenNote(Guid noteId, string? title)
    {
        if (noteId == Guid.Empty)
        {
            throw new ArgumentException("笔记标识不能为空。", nameof(noteId));
        }

        _noteId = noteId;
        UpdateTitle(title);
        UpdateContext();
    }

    public void UpdateDraftTitle(string? title)
    {
        if (_noteId is null)
        {
            return;
        }

        UpdateTitle(title);
    }

    public void SetRecording(bool isRecording)
        => SetRecording(isRecording, 0);

    public void SetRecording(bool isRecording, int segmentNumber)
    {
        var normalizedSegment = isRecording ? Math.Max(segmentNumber, 0) : 0;
        if (_isRecording == isRecording && _recordingSegment == normalizedSegment)
        {
            return;
        }

        _isRecording = isRecording;
        _recordingSegment = normalizedSegment;
        UpdateContext();
    }

    public void Clear()
    {
        ShowLibrary(hasNotes: false);
    }

    private void UpdateTitle(string? title)
        => WindowTitle = NativeNotePresentation.WindowTitle(title);

    private void UpdateContext()
    {
        if (_noteId is null)
        {
            return;
        }

        EditorContext = _isRecording
            ? _recordingSegment > 0
                ? $"正在记录 · 第 {_recordingSegment} 段 · 本机草稿"
                : "正在记录 · 本机草稿"
            : "本机草稿 · 自动保存";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
