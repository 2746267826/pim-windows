using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public partial class NativeNotesWindow : Window
{
    private readonly LocalNoteStore _store;
    private readonly WindowsRecordingService _recording;
    private readonly ObservableCollection<LocalNoteSummary> _notes = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private LocalNoteDocument? _current;
    private bool _loading;
    private bool _closing;
    private bool _allowClose;

    public NativeNotesWindow()
    {
        InitializeComponent();
        _store = App.Services.GetRequiredService<LocalNoteStore>();
        _recording = App.Services.GetRequiredService<WindowsRecordingService>();
        NotesList.ItemsSource = _notes;
        _saveTimer.Tick += OnSaveTimer;
        _recording.StateChanged += OnRecordingStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _store.EnsureInitializedAsync();
            await RefreshNotesAsync();
            if (_notes.Count > 0)
            {
                NotesList.SelectedIndex = 0;
            }
            else
            {
                ShowEmptyEditor();
            }

            if (_recording.IsRecording)
            {
                RecordButton.Content = "■ 停止并保存";
                OnRecordingStateChanged(
                    null,
                    new WindowsRecordingState(
                        true,
                        _recording.CurrentNoteId,
                        TimeSpan.Zero,
                        0,
                        null));
            }
        }
        catch (Exception ex)
        {
            ShowFailure("打开本机笔记库失败", ex);
        }
    }

    private async void OnNewNote(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await SavePendingAsync())
            {
                return;
            }

            var note = await _store.CreateAsync();
            await RefreshNotesAsync();
            NotesList.SelectedItem = _notes.FirstOrDefault(summary => summary.NoteId == note.NoteId);
        }
        catch (Exception ex)
        {
            ShowFailure("新建笔记失败", ex);
        }
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        await RefreshNotesAsync(SearchBox.Text);
    }

    private async void OnNoteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (NotesList.SelectedItem is not LocalNoteSummary summary || _loading)
        {
            return;
        }

        try
        {
            if (!await SavePendingAsync())
            {
                _loading = true;
                NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == _current?.NoteId);
                _loading = false;
                return;
            }

            var note = await _store.LoadAsync(summary.NoteId);
            _loading = true;
            _current = note;
            TitleBox.IsEnabled = true;
            MarkdownBox.IsEnabled = true;
            TitleBox.Text = note.Title;
            MarkdownBox.Text = note.Markdown;
            UpdateMaterials();
            await RefreshVersionsAsync(note.NoteId, note.CurrentVersionId);
            SaveStateText.Text = "已加载固定版本";
        }
        catch (Exception ex)
        {
            ShowFailure("打开笔记失败", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null || _closing)
        {
            return;
        }

        SaveStateText.Text = "正在等待本机自动保存…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimer(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        await SavePendingAsync();
    }

    private async void OnAddPhoto(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择照片材料",
            Filter = "照片|*.png;*.jpg;*.jpeg|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ImportFilesAsync(dialog.FileNames, LocalNoteMaterialKind.Photo);
    }

    private async void OnAddRecording(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "添加已有音频材料",
            Filter = "WAV 音频|*.wav|所有音频/文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ImportFilesAsync(dialog.FileNames, LocalNoteMaterialKind.Recording);
    }

    private async void OnImportPackage(object sender, RoutedEventArgs e)
    {
        if (!await SavePendingAsync())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入 PIM 笔记包",
            Filter = "PIM 笔记包|*.pimnote.zip|ZIP 压缩包|*.zip",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await _store.ImportAsync(dialog.FileName);
            await RefreshNotesAsync();
            NotesList.SelectedItem = _notes.FirstOrDefault(summary => summary.NoteId == imported.NoteId);
            SaveStateText.Text = $"已导入固定版本，{imported.Materials.Count} 项材料已校验 SHA-256";
        }
        catch (Exception ex)
        {
            ShowFailure("导入笔记包失败；原笔记库未切换", ex);
        }
    }

    private async void OnAddHandwriting(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            if (!await SavePendingAsync())
            {
                return;
            }

            var dialog = new HandwritingWindow { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Capture is null)
            {
                return;
            }

            using var png = new MemoryStream(dialog.Capture.PngBytes, writable: false);
            _current = await _store.AddHandwritingAsync(
                _current.NoteId,
                png,
                dialog.Capture.VectorJson,
                $"手写 {DateTime.Now:MM-dd HHmmss}",
                dialog.Capture.Width,
                dialog.Capture.Height);
            UpdateMaterials();
            await RefreshNotesAsync(SearchBox.Text);
            await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
            SaveStateText.Text = "手写墨迹与 PNG 已保存为新的固定版本";
        }
        catch (Exception ex)
        {
            ShowFailure("手写材料未能保存", ex);
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (!await SavePendingAsync())
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出固定笔记版本",
            Filter = "PIM 笔记包|*.pimnote.zip",
            FileName = MakeExportName(_current.Title),
            AddExtension = true,
            DefaultExt = ".pimnote.zip",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var result = await _store.ExportAsync(_current.NoteId, _current.CurrentVersionId, dialog.FileName);
            MessageBox.Show(
                $"已导出固定版本，包含 {result.Materials.Count} 项材料。\nSHA-256：{result.Sha256}",
                "笔记包已导出",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowFailure("导出失败；原笔记与材料未修改", ex);
        }
    }

    private async void OnToggleRecording(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show(this, "请先选择或新建笔记。", "PIM 笔记", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_recording.IsRecording)
            {
                var recording = await _recording.StopAsync();
                if (recording is null)
                {
                    SaveStateText.Text = "录音没有产生可用音频文件";
                    return;
                }

                if (_current.NoteId == recording.NoteId)
                {
                    _current = recording.Note;
                    _loading = true;
                    TitleBox.Text = _current.Title;
                    MarkdownBox.Text = _current.Markdown;
                    _loading = false;
                }
                else
                {
                    await RefreshNotesAsync();
                    var recordingNote = _notes.FirstOrDefault(summary => summary.NoteId == recording.NoteId);
                    if (recordingNote is not null)
                    {
                        _current = await _store.LoadAsync(recording.NoteId);
                        _loading = true;
                        NotesList.SelectedItem = recordingNote;
                        TitleBox.Text = _current.Title;
                        MarkdownBox.Text = _current.Markdown;
                        _loading = false;
                    }

                    MessageBox.Show(
                        this,
                        "录音已保存到开始时绑定的另一篇笔记。窗口已切换到该笔记所在列表项。",
                        "录音已保存",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                UpdateMaterials();
                RecordingStatusText.Text = $"已保存 {recording.Wave.Duration:mm\\:ss} 原始 WAV 到固定材料版本";
                SaveStateText.Text = "录音原始 WAV 已保存；云端尚未提交";
                await RefreshNotesAsync(SearchBox.Text);
                await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
                return;
            }

            if (!await SavePendingAsync())
            {
                return;
            }

            await _recording.StartAsync(_current.NoteId);
            RecordButton.Content = "■ 停止并保存";
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = "录音未开始";
            ShowFailure("主动录音失败", ex);
        }
    }

    private void OnRecordingStateChanged(object? sender, WindowsRecordingState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (state.IsRecording)
            {
                RecordingStatusText.Text = $"录音中 {state.Elapsed:mm\\:ss} · 实际输入峰值 {state.PeakLevel:P0}";
                return;
            }

            if (RecordButton is not null)
            {
                RecordButton.Content = "● 开始录音";
            }

            if (state.Error is not null)
            {
                RecordingStatusText.Text = state.Error;
            }
        });
    }

    private async Task ImportFilesAsync(IReadOnlyList<string> paths, LocalNoteMaterialKind kind)
    {
        try
        {
            if (!await SavePendingAsync())
            {
                return;
            }

            var inputs = paths.Select(path => new LocalNoteMaterialInput(
                File.OpenRead(path),
                Path.GetFileName(path),
                kind,
                InferMediaType(path))).ToArray();
            try
            {
                _current = await _store.AddMaterialsAsync(_current!.NoteId, inputs);
            }
            finally
            {
                foreach (var input in inputs)
                {
                    await input.Content.DisposeAsync();
                }
            }

            UpdateMaterials();
            await RefreshNotesAsync(SearchBox.Text);
            await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
            SaveStateText.Text = "材料已保存为新的固定版本";
        }
        catch (Exception ex)
        {
            ShowFailure("材料未能完整保存", ex);
        }
    }

    private async Task<bool> SavePendingAsync()
    {
        _saveTimer.Stop();
        if (_current is null || _loading)
        {
            return true;
        }

        var title = TitleBox.Text;
        var markdown = MarkdownBox.Text;
        if (string.Equals(_current.Title, title, StringComparison.Ordinal)
            && string.Equals(_current.Markdown, markdown, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            _current = await _store.SaveAsync(_current.NoteId, title, markdown);
            UpdateMaterials();
            SaveStateText.Text = "已保存为新的固定版本";
            await RefreshNotesAsync(SearchBox.Text);
            await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
            return true;
        }
        catch (Exception ex)
        {
            SaveStateText.Text = "保存失败，当前正文仍保留在窗口中";
            ShowFailure("本机自动保存失败", ex);
            return false;
        }
    }

    private async Task RefreshNotesAsync(string? query = null)
    {
        var selectedId = _current?.NoteId;
        var rows = await _store.ListAsync(query);
        _notes.Clear();
        foreach (var row in rows)
        {
            _notes.Add(row);
        }

        if (selectedId is { } id)
        {
            NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == id);
        }
    }

    private void UpdateMaterials()
    {
        if (_current is null)
        {
            MaterialsText.Text = "固定材料版本 · 尚无材料";
            MaterialsList.ItemsSource = null;
            return;
        }

        MaterialsList.ItemsSource = _current.Materials;
        var recordings = _current.Materials.Count(item => item.Kind == LocalNoteMaterialKind.Recording);
        var photos = _current.Materials.Count(item => item.Kind == LocalNoteMaterialKind.Photo);
        var handwriting = _current.Materials.Count(item => item.Kind == LocalNoteMaterialKind.Handwriting);
        MaterialsText.Text = $"固定版本 {_current.CurrentVersionId.ToString("N")[..8]} · 照片 {photos} · 手写 {handwriting} · 音频 {recordings}";
    }

    private void ShowEmptyEditor()
    {
        _loading = true;
        _current = null;
        TitleBox.Text = string.Empty;
        MarkdownBox.Text = string.Empty;
        TitleBox.IsEnabled = false;
        MarkdownBox.IsEnabled = false;
        _loading = false;
        MaterialsText.Text = "选择或新建笔记以开始";
        MaterialsList.ItemsSource = null;
        VersionsBox.ItemsSource = null;
        SaveStateText.Text = "";
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        _saveTimer.Stop();
        if (_current is not null
            && (!string.Equals(_current.Title, TitleBox.Text, StringComparison.Ordinal)
                || !string.Equals(_current.Markdown, MarkdownBox.Text, StringComparison.Ordinal)))
        {
            e.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            SaveStateText.Text = "正在保存后关闭…";
            var saved = await SavePendingAsync();
            _closing = false;
            if (saved)
            {
                _allowClose = true;
                Close();
            }
            else
            {
                SaveStateText.Text = "保存失败，窗口保持打开以保护当前正文";
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _recording.StateChanged -= OnRecordingStateChanged;
    }

    private async void OnRestoreVersion(object sender, RoutedEventArgs e)
    {
        if (_current is null || VersionsBox.SelectedItem is not LocalNoteVersionRow selected)
        {
            MessageBox.Show(this, "请先选择要恢复的固定版本。", "PIM 笔记", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (selected.VersionId == _current.CurrentVersionId)
        {
            MessageBox.Show(this, "所选版本已经是当前固定版本。", "PIM 笔记", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                $"恢复固定版本 {selected.VersionId.ToString("N")[..8]}？\n\n这会新建一个固定版本，不会删除后续版本。",
                "恢复历史版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!await SavePendingAsync())
            {
                return;
            }

            _current = await _store.RestoreVersionAsync(_current.NoteId, selected.VersionId);
            _loading = true;
            TitleBox.Text = _current.Title;
            MarkdownBox.Text = _current.Markdown;
            _loading = false;
            UpdateMaterials();
            await RefreshNotesAsync(SearchBox.Text);
            await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
            SaveStateText.Text = "历史正文与材料引用已恢复为一个新的固定版本";
        }
        catch (Exception ex)
        {
            ShowFailure("恢复固定版本失败；当前版本未切换", ex);
        }
    }

    private async void OnMaterialDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_current is null || MaterialsList.SelectedItem is not LocalNoteMaterial material)
        {
            return;
        }

        try
        {
            var resolved = await _store.ResolveMaterialFileAsync(_current.NoteId, material.MaterialId);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(resolved.AbsolutePath)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowFailure("无法打开材料", ex);
        }
    }

    private async Task RefreshVersionsAsync(Guid noteId, Guid currentVersionId)
    {
        var versions = await _store.ListVersionsAsync(noteId);
        var rows = versions
            .Select(version => new LocalNoteVersionRow(
                version.VersionId,
                $"{version.CreatedAt.ToLocalTime():MM-dd HH:mm} · {ReasonLabel(version.Reason)} · {version.VersionId.ToString("N")[..8]}"))
            .ToArray();
        VersionsBox.ItemsSource = rows;
        VersionsBox.SelectedItem = rows.FirstOrDefault(row => row.VersionId == currentVersionId);
    }

    private static string ReasonLabel(LocalNoteCheckpointReason reason) => reason switch
    {
        LocalNoteCheckpointReason.Initial => "创建",
        LocalNoteCheckpointReason.ManualSave => "正文保存",
        LocalNoteCheckpointReason.MaterialAdded => "材料",
        LocalNoteCheckpointReason.RecordingAdded => "录音",
        LocalNoteCheckpointReason.ImportedPackage => "导入",
        _ => reason.ToString(),
    };

    private sealed record LocalNoteVersionRow(Guid VersionId, string Label);

    private static string InferMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    private static string MakeExportName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return (string.IsNullOrWhiteSpace(safe) ? "笔记" : safe) + ".pimnote.zip";
    }

    private void ShowFailure(string operation, Exception exception)
    {
        SaveStateText.Text = operation;
        MessageBox.Show(this, $"{operation}：{exception.Message}", "PIM 笔记", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
