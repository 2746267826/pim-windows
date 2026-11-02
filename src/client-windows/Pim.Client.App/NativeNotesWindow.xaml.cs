using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public partial class NativeNotesWindow : Window
{
    private readonly LocalNoteStore _store;
    private readonly WindowsRecordingService _recording;
    private readonly CloudNoteMaterialService _cloud;
    private readonly ObservableCollection<LocalNoteSummary> _notes = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly CloudNotesUiState _cloudUi = new();
    private LocalNoteDocument? _current;
    private bool _loading;
    private bool _closing;
    private bool _allowClose;
    private bool _loadingCloudResult;
    private bool _restoringCloudSelection;
    private bool _refreshingCloudResults;
    private NoteDerivedArtifactDto? _selectedArtifact;
    private NoteDerivedVersionDto? _selectedDerivedVersion;
    private NoteDerivedVersionSummaryDto? _selectedDerivedVersionSummary;

    public NativeNotesWindow()
    {
        InitializeComponent();
        _store = App.Services.GetRequiredService<LocalNoteStore>();
        _recording = App.Services.GetRequiredService<WindowsRecordingService>();
        _cloud = App.Services.GetRequiredService<CloudNoteMaterialService>();
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
            await RefreshCloudVersionsAsync(showErrors: false);
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

        if (_current?.NoteId == summary.NoteId)
        {
            return;
        }

        var selectionGeneration = _cloudUi.BeginNoteSelection();
        var artifactGeneration = _cloudUi.BeginArtifactSelection();
        if (_cloudUi.IsDraftDirty && !ConfirmDiscardCloudDraft("切换笔记"))
        {
            _loading = true;
            NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == _current?.NoteId);
            _loading = false;
            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
            return;
        }

        try
        {
            if (!await SavePendingAsync())
            {
                _loading = true;
                NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == _current?.NoteId);
                _loading = false;
                RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
                return;
            }

            var note = await _store.LoadAsync(summary.NoteId);
            if (!IsSelectedNoteRequest(selectionGeneration, summary.NoteId))
            {
                return;
            }

            _cloudUi.ClearDraft();
            CloudResultBox.IsEnabled = true;
            _loading = true;
            _current = note;
            TitleBox.IsEnabled = true;
            MarkdownBox.IsEnabled = true;
            TitleBox.Text = note.Title;
            MarkdownBox.Text = note.Markdown;
            UpdateMaterials();
            await RefreshVersionsAsync(note.NoteId, note.CurrentVersionId);
            if (IsCurrentNoteSelection(selectionGeneration, note.NoteId))
            {
                await RefreshCloudVersionsAsync(showErrors: false, selectionGeneration);
                await RefreshCloudResultsAsync(showErrors: false, selectionGeneration);
            }
            SaveStateText.Text = "已加载固定版本";
        }
        catch (Exception ex)
        {
            if (selectionGeneration == _cloudUi.NoteGeneration)
            {
                _loading = true;
                NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == _current?.NoteId);
                _loading = false;
                RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
                ShowFailure("打开笔记失败", ex);
            }
        }
        finally
        {
            if (selectionGeneration == _cloudUi.NoteGeneration)
            {
                _loading = false;
            }

            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
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

        if (_current.IsServerSourceReadOnly && !_recording.IsRecording)
        {
            MessageBox.Show(
                this,
                "该云端混排版本在当前编辑器中只读。请导出完整原件后，在本机笔记中开始录音。",
                "笔记版本只读",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
                RecordButton.IsEnabled = true;
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

            if (RecordButton is not null)
            {
                RecordButton.IsEnabled = _current is null || !_current.IsServerSourceReadOnly;
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
        var recordings = _current.Materials
            .Where(item => item.Kind is LocalNoteMaterialKind.Recording or LocalNoteMaterialKind.RecordingSegment)
            .Select(item => item.RecordingId ?? item.MaterialId)
            .Distinct()
            .Count();
        var photos = _current.Materials.Count(item => item.Kind == LocalNoteMaterialKind.Photo);
        var handwriting = _current.Materials.Count(item => item.Kind == LocalNoteMaterialKind.Handwriting);
        MaterialsText.Text = $"固定版本 {_current.CurrentVersionId.ToString("N")[..8]} · 照片 {photos} · 手写 {handwriting} · 音频 {recordings}";
        TitleBox.IsReadOnly = _current.IsServerSourceReadOnly;
        MarkdownBox.IsReadOnly = _current.IsServerSourceReadOnly;
        AddPhotoButton.IsEnabled = !_current.IsServerSourceReadOnly;
        AddHandwritingButton.IsEnabled = !_current.IsServerSourceReadOnly;
        AddRecordingButton.IsEnabled = !_current.IsServerSourceReadOnly;
        RecordButton.IsEnabled = _recording.IsRecording || !_current.IsServerSourceReadOnly;
        if (_current.IsServerSourceReadOnly)
        {
            MaterialsText.Text += " · 云端混排原件只读，导出保留完整块序";
        }
    }

    private void ShowEmptyEditor()
    {
        _cloudUi.BeginNoteSelection();
        _cloudUi.BeginArtifactSelection();
        _loading = true;
        _current = null;
        TitleBox.Text = string.Empty;
        MarkdownBox.Text = string.Empty;
        TitleBox.IsEnabled = false;
        MarkdownBox.IsEnabled = false;
        TitleBox.IsReadOnly = false;
        MarkdownBox.IsReadOnly = false;
        AddPhotoButton.IsEnabled = false;
        AddHandwritingButton.IsEnabled = false;
        AddRecordingButton.IsEnabled = false;
        RecordButton.IsEnabled = _recording.IsRecording;
        _loading = false;
        MaterialsText.Text = "选择或新建笔记以开始";
        MaterialsList.ItemsSource = null;
        VersionsBox.ItemsSource = null;
        CloudVersionsList.ItemsSource = null;
        CloudArtifactsList.ItemsSource = null;
        _selectedArtifact = null;
        _selectedDerivedVersion = null;
        _selectedDerivedVersionSummary = null;
        _cloudUi.ClearDraft();
        _loadingCloudResult = true;
        CloudResultBox.Text = string.Empty;
        _loadingCloudResult = false;
        CloudResultStateText.Text = "未选择笔记";
        SaveStateText.Text = "";
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        _saveTimer.Stop();
        if (_cloudUi.IsDraftDirty
            && MessageBox.Show(
                this,
                "云端结果编辑框中有尚未保存的人工修订。仍要关闭并放弃这份草稿吗？",
                "未保存的云端修订",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _cloudUi.ClearDraft();

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

    private async void OnUploadCloudVersion(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show(this, "请先选择或新建笔记。", "PIM 笔记", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!await SavePendingAsync())
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"仅将当前固定版本 {_current.CurrentVersionId.ToString("N")[..8]} 上传到已登录服务器？\n\n"
                + "包含当前正文、照片、手写预览与可编辑笔迹、WAV 录音；不会启动付费模型。",
                "确认云端材料存储",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        CloudStatusText.Text = "正在校验并上传当前固定版本…";
        try
        {
            var result = await _cloud.UploadVersionAsync(
                _current.NoteId,
                _current.CurrentVersionId,
                userConfirmedStorage: true);
            CloudStatusText.Text = result.Upload.HasConflict
                ? $"已上传 {result.Upload.VersionId[..8]}，但服务器当前版本已被另一端更新；双方版本均保留。"
                : $"已上传并完成固定版本 {result.Upload.VersionId[..8]}。";
            await RefreshCloudVersionsAsync(showErrors: false);
            await RefreshCloudResultsAsync(showErrors: false);
        }
        catch (Exception ex)
        {
            CloudStatusText.Text = "云端上传未完成；本机固定版本未修改。";
            ShowFailure("上传当前固定版本失败", ex);
        }
    }

    private async void OnRefreshCloud(object sender, RoutedEventArgs e)
        => await RefreshCloudVersionsAsync(showErrors: true);

    private async void OnOpenCloudSources(object sender, RoutedEventArgs e)
    {
        var dialog = new CloudNoteSourcesWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedNoteId is not { } noteId)
        {
            return;
        }

        SearchBox.Text = string.Empty;
        await RefreshNotesAsync();
        NotesList.SelectedItem = _notes.FirstOrDefault(item => item.NoteId == noteId);
    }

    private async Task RefreshCloudVersionsAsync(bool showErrors, long? expectedSelectionGeneration = null)
    {
        if (_current is null)
        {
            CloudVersionsList.ItemsSource = null;
            return;
        }

        var noteId = _current.NoteId;
        var accountScope = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        try
        {
            var versions = await _cloud.GetVersionsAsync(noteId);
            if (!IsCurrentCloudRequest(
                    expectedSelectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration))
            {
                return;
            }

            var rows = versions
                .Select(version => new CloudVersionRow(
                    version,
                    $"{VersionFlags(version)} {version.Phase} · {version.VersionId[..8]} · "
                    + $"{version.CreatedAt.ToLocalTime():MM-dd HH:mm}"))
                .ToArray();
            CloudVersionsList.ItemsSource = rows;
            CloudVersionsList.SelectedItem = rows.FirstOrDefault(row => row.Version.IsCurrent)
                ?? rows.FirstOrDefault();
            CloudStatusText.Text = _cloud.IsAvailable
                ? $"服务器共有 {rows.Length} 个固定材料版本。上传不会自动启动模型。"
                : "当前未登录；本机笔记仍可离线编辑和录音。";
        }
        catch (Exception ex)
        {
            if (!IsCurrentCloudRequest(
                    expectedSelectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration))
            {
                return;
            }

            CloudVersionsList.ItemsSource = null;
            CloudStatusText.Text = "云端版本暂不可用；本机固定版本不受影响。";
            if (showErrors)
            {
                ShowFailure("读取云端固定版本失败", ex);
            }
        }
    }

    private void OnCloudVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CloudVersionsList.SelectedItem is not CloudVersionRow row)
        {
            PromoteCloudVersionButton.IsEnabled = false;
            return;
        }

        PromoteCloudVersionButton.IsEnabled = !row.Version.IsCurrent;
        CloudStatusText.Text = row.Version.HasConflict
            ? $"版本 {row.Version.VersionId[..8]} 与当前 Head 分叉；服务器保留双方内容。"
            : row.Version.IsCurrent
                ? $"版本 {row.Version.VersionId[..8]} 是服务器当前固定版本。"
                : $"版本 {row.Version.VersionId[..8]} 是历史固定版本。";
    }

    private async void OnPromoteCloudVersion(object sender, RoutedEventArgs e)
    {
        if (_current is null
            || CloudVersionsList.SelectedItem is not CloudVersionRow selected
            || selected.Version.IsCurrent)
        {
            return;
        }

        var current = (CloudVersionsList.ItemsSource as IEnumerable<CloudVersionRow>)?
            .FirstOrDefault(row => row.Version.IsCurrent);
        if (MessageBox.Show(
                this,
                $"把服务器版本 {selected.Version.VersionId[..8]} 设为该笔记当前版本？\n\n"
                + "不会删除冲突版本或本机固定版本。",
                "解决云端版本冲突",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var promoted = await _cloud.PromoteVersionAsync(
                _current.NoteId,
                Guid.Parse(selected.Version.VersionId),
                current is null ? null : Guid.Parse(current.Version.VersionId));
            CloudStatusText.Text = $"服务器当前版本已切换为 {promoted.VersionId[..8]}。";
            await RefreshCloudVersionsAsync(showErrors: false);
        }
        catch (Exception ex)
        {
            ShowFailure("切换云端当前版本失败；服务器版本未删除", ex);
        }
    }

    private async void OnRefreshCloudResults(object sender, RoutedEventArgs e)
        => await RefreshCloudResultsAsync(showErrors: true);

    private async Task RefreshCloudResultsAsync(bool showErrors, long? expectedSelectionGeneration = null)
    {
        if (_current is null)
        {
            CloudArtifactsList.ItemsSource = null;
            return;
        }

        if (_cloudUi.IsDraftDirty && !ConfirmDiscardCloudDraft("刷新云端结果"))
        {
            return;
        }

        var noteId = _current.NoteId;
        var accountScope = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        var draftGeneration = _cloudUi.DraftGeneration;
        try
        {
            var artifacts = await _cloud.GetDerivedArtifactsAsync(noteId);
            if (!IsCurrentCloudRequest(
                    expectedSelectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration)
                || !_cloudUi.IsDraftCurrent(draftGeneration))
            {
                return;
            }

            var rows = artifacts
                .SelectMany(artifact => artifact.Versions.Select(version =>
                    new CloudArtifactRow(
                        artifact,
                        version,
                        $"{ArtifactLabel(artifact.Kind)} · {ResultFlags(version)} · "
                        + $"{version.Author} · {version.CreatedAt.ToLocalTime():MM-dd HH:mm} · "
                        + version.ContentSha256[..8])))
                .OrderByDescending(row => row.Version.CreatedAt)
                .ToArray();
            _cloudUi.ClearDraft();
            _refreshingCloudResults = true;
            try
            {
                CloudArtifactsList.ItemsSource = rows;
                CloudArtifactsList.SelectedItem = rows.FirstOrDefault(row => row.Version.IsHead)
                    ?? rows.FirstOrDefault();
            }
            finally
            {
                _refreshingCloudResults = false;
            }

            if (rows.Length == 0)
            {
                _selectedArtifact = null;
                _selectedDerivedVersion = null;
                _selectedDerivedVersionSummary = null;
                _loadingCloudResult = true;
                CloudResultBox.Text = string.Empty;
                _loadingCloudResult = false;
                _cloudUi.ClearDraft();
            }

            CloudResultStateText.Text = rows.Length == 0
                ? "服务器暂无记录或分析结果；只读页不会启动 ASR/模型。"
                : $"共 {rows.Length} 个结果版本；可查看历史、保存人工修订或切回历史版本。";
        }
        catch (Exception ex)
        {
            if (!IsCurrentCloudRequest(
                    expectedSelectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration))
            {
                return;
            }

            CloudArtifactsList.ItemsSource = null;
            CloudResultStateText.Text = "云端结果暂不可用。";
            if (showErrors)
            {
                ShowFailure("读取云端结果失败", ex);
            }
        }
    }

    private async void OnCloudArtifactSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringCloudSelection)
        {
            _restoringCloudSelection = false;
            return;
        }

        var artifactGeneration = _cloudUi.BeginArtifactSelection();
        var selectionGeneration = _cloudUi.NoteGeneration;
        if (CloudArtifactsList.SelectedItem is not CloudArtifactRow row)
        {
            _selectedArtifact = null;
            _selectedDerivedVersion = null;
            _selectedDerivedVersionSummary = null;
            return;
        }

        if (_cloudUi.IsDraftDirty
            && !_refreshingCloudResults
            && !ConfirmDiscardCloudDraft("切换云端结果版本"))
        {
            var priorSelection = (CloudArtifactsList.ItemsSource as IEnumerable<CloudArtifactRow>)?
                .FirstOrDefault(candidate =>
                    candidate.Artifact.ArtifactId == _selectedArtifact?.ArtifactId
                    && candidate.Version.VersionId == _selectedDerivedVersion?.VersionId);
            RestoreCloudSelection(priorSelection);
            return;
        }

        var previousSelection = (CloudArtifactsList.ItemsSource as IEnumerable<CloudArtifactRow>)?
            .FirstOrDefault(candidate =>
                candidate.Artifact.ArtifactId == _selectedArtifact?.ArtifactId
                && candidate.Version.VersionId == _selectedDerivedVersion?.VersionId);
        var noteId = _current?.NoteId;
        var accountScope = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        var draftGeneration = _cloudUi.DraftGeneration;
        CloudResultBox.IsEnabled = false;
        try
        {
            CloudResultStateText.Text = "正在读取结果版本…";
            var version = await _cloud.GetDerivedVersionAsync(
                Guid.Parse(row.Artifact.ArtifactId),
                Guid.Parse(row.Version.VersionId));
            if (!IsCurrentCloudRequest(
                    selectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration)
                || !_cloudUi.IsArtifactCurrent(artifactGeneration)
                || !_cloudUi.IsDraftCurrent(draftGeneration)
                || !ReferenceEquals(CloudArtifactsList.SelectedItem, row))
            {
                return;
            }

            _selectedArtifact = row.Artifact;
            _selectedDerivedVersion = version;
            _selectedDerivedVersionSummary = row.Version;
            _loadingCloudResult = true;
            _cloudUi.ClearDraft();
            CloudResultBox.Text = version.ContentMarkdown;
            _loadingCloudResult = false;
            CloudResultStateText.Text =
                $"{ArtifactLabel(version.Kind)} · {version.Author} · {version.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · "
                + (version.IsHead ? "当前 Head" : "历史版本")
                + (version.HasConflict ? " · 有冲突" : string.Empty);
        }
        catch (Exception ex)
        {
            if (!_cloudUi.IsArtifactCurrent(artifactGeneration)
                || !IsCurrentCloudRequest(
                    selectionGeneration,
                    _current?.NoteId,
                    accountScope,
                    accountGeneration)
                || !_cloudUi.IsDraftCurrent(draftGeneration))
            {
                return;
            }

            RestoreCloudSelection(previousSelection);
            CloudResultStateText.Text = "读取所选结果失败；当前编辑内容未覆盖。";
            ShowFailure("读取云端结果版本失败", ex);
        }
        finally
        {
            if (_cloudUi.IsResultEditorCurrent(selectionGeneration, artifactGeneration))
            {
                CloudResultBox.IsEnabled = true;
            }
        }
    }

    private async void OnSaveCloudRevision(object sender, RoutedEventArgs e)
    {
        if (_selectedArtifact is null || _selectedDerivedVersion is null)
        {
            MessageBox.Show(this, "请先选择一个云端结果版本。", "PIM 云端结果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                "把编辑框内容保存为新的 USER 人工修订？\n\n不会启动 ASR 或模型，也不会覆盖旧版本。",
                "保存人工修订",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var artifact = _selectedArtifact;
        var markdown = CloudResultBox.Text;
        var selectionGeneration = _cloudUi.NoteGeneration;
        var artifactGeneration = _cloudUi.ArtifactGeneration;
        var noteId = _current?.NoteId;
        var accountScope = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        var draftGeneration = _cloudUi.DraftGeneration;
        var expectedHeadVersionId = string.IsNullOrWhiteSpace(artifact.HeadVersionId)
            ? (Guid?)null
            : Guid.Parse(artifact.HeadVersionId);
        var expectedHeadRevision = artifact.Revision;
        try
        {
            var saved = await _cloud.SaveManualRevisionAsync(
                Guid.Parse(artifact.ArtifactId),
                expectedHeadVersionId,
                expectedHeadRevision,
                markdown);
            if (!IsCurrentCloudRequest(
                    selectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration)
                || !_cloudUi.IsArtifactCurrent(artifactGeneration))
            {
                return;
            }

            var stillShowsSavedText = _cloudUi.IsDraftCurrent(draftGeneration)
                && string.Equals(CloudResultBox.Text, markdown, StringComparison.Ordinal);
            if (stillShowsSavedText)
            {
                _cloudUi.ClearDraft();
            }

            CloudResultStateText.Text = saved.HasConflict
                ? "人工修订已保存为新版本，但当前 Head 已被另一端更新；双方版本均保留。"
                : "人工修订已保存为新的不可变结果版本。";
            if (stillShowsSavedText)
            {
                await RefreshCloudResultsAsync(showErrors: false, selectionGeneration);
            }
            else
            {
                CloudResultStateText.Text += " 保存期间的新编辑仍是未保存草稿。";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrentCloudRequest(
                    selectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration)
                && _cloudUi.IsArtifactCurrent(artifactGeneration))
            {
                ShowFailure("保存人工修订失败；草稿仍保留在编辑框中，旧结果未覆盖", ex);
            }
        }
    }

    private async void OnSelectCloudResultVersion(object sender, RoutedEventArgs e)
    {
        if (_selectedArtifact is null
            || _selectedDerivedVersion is null
            || _selectedDerivedVersionSummary is null)
        {
            MessageBox.Show(this, "请先选择一个云端结果版本。", "PIM 云端结果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_selectedDerivedVersionSummary.IsHead)
        {
            MessageBox.Show(this, "所选版本已经是结果当前版本。", "PIM 云端结果", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                $"把结果版本 {_selectedDerivedVersion.VersionId[..8]} 设为当前版本？\n\n旧版本仍保留。",
                "选择结果当前版本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var artifact = _selectedArtifact;
        var version = _selectedDerivedVersion;
        var selectionGeneration = _cloudUi.NoteGeneration;
        var artifactGeneration = _cloudUi.ArtifactGeneration;
        var noteId = _current?.NoteId;
        var accountScope = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        try
        {
            _ = await _cloud.SelectDerivedVersionAsync(
                Guid.Parse(artifact.ArtifactId),
                Guid.Parse(version.VersionId),
                artifact.Revision);
            if (!IsCurrentCloudRequest(
                    selectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration)
                || !_cloudUi.IsArtifactCurrent(artifactGeneration))
            {
                return;
            }

            CloudResultStateText.Text = "结果当前版本已切换。";
            await RefreshCloudResultsAsync(showErrors: false, selectionGeneration);
        }
        catch (Exception ex)
        {
            if (IsCurrentCloudRequest(
                    selectionGeneration,
                    noteId,
                    accountScope,
                    accountGeneration)
                && _cloudUi.IsArtifactCurrent(artifactGeneration))
            {
                ShowFailure("切换结果当前版本失败；旧版本未删除", ex);
            }
        }
    }

    private void OnOpenTaskSuggestions(object sender, RoutedEventArgs e)
    {
        if (_current is null
            || _selectedArtifact is null
            || _selectedDerivedVersion is null
            || _selectedDerivedVersionSummary is null)
        {
            MessageBox.Show(
                this,
                "请先选择一个已加载的云端结果固定版本。",
                "从笔记提取任务",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!Guid.TryParse(_selectedArtifact.ArtifactId, out _)
            || !Guid.TryParse(_selectedDerivedVersion.VersionId, out var versionId)
            || !Guid.TryParse(_selectedDerivedVersionSummary.VersionId, out var summaryVersionId)
            || versionId != summaryVersionId
            || _selectedDerivedVersion.Kind != _selectedArtifact.Kind)
        {
            CloudResultStateText.Text = "所选结果身份不完整或不匹配；未打开任务提取。";
            return;
        }

        if (_cloud.CurrentAccountScope is null)
        {
            CloudResultStateText.Text = "请先登录云端账号，再从固定结果提取任务。";
            return;
        }

        try
        {
            var window = new NoteTaskSuggestionsWindow(
                _current.NoteId,
                _selectedArtifact,
                _selectedDerivedVersionSummary,
                _selectedDerivedVersion,
                App.Services.GetRequiredService<NoteTaskSuggestionService>().CaptureFence())
            {
                Owner = this,
            };
            window.ShowDialog();
        }
        catch (InvalidOperationException)
        {
            CloudResultStateText.Text = "账号或服务器刚刚切换；请刷新登录状态后重新打开任务提取。";
        }
    }

    private void OnCloudResultEdited(object sender, TextChangedEventArgs e)
    {
        if (!_loadingCloudResult && _selectedDerivedVersion is not null)
        {
            _cloudUi.MarkDraftDirty();
            CloudResultStateText.Text = "人工修订草稿尚未保存；切换或刷新前会先询问。";
        }
    }

    private bool ConfirmDiscardCloudDraft(string action)
    {
        if (!_cloudUi.IsDraftDirty)
        {
            return true;
        }

        var answer = MessageBox.Show(
            this,
            $"当前云端结果有未保存的人工修订草稿。继续{action}并放弃草稿吗？",
            "未保存的云端修订",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private void RestoreCloudSelection(CloudArtifactRow? selection)
    {
        if (ReferenceEquals(CloudArtifactsList.SelectedItem, selection))
        {
            return;
        }

        _restoringCloudSelection = true;
        CloudArtifactsList.SelectedItem = selection;
    }

    private void RestoreCloudResultEditor(long noteTicket, long artifactTicket)
    {
        if (_cloudUi.IsResultEditorCurrent(noteTicket, artifactTicket))
        {
            CloudResultBox.IsEnabled = true;
        }
    }

    private bool IsCurrentNoteSelection(long generation, Guid noteId)
        => _cloudUi.IsNoteCurrent(
            generation,
            noteId,
            _current?.NoteId,
            (NotesList.SelectedItem as LocalNoteSummary)?.NoteId);

    private bool IsSelectedNoteRequest(long generation, Guid noteId)
        => generation == _cloudUi.NoteGeneration
           && NotesList.SelectedItem is LocalNoteSummary selected
           && selected.NoteId == noteId;

    private bool IsCurrentCloudRequest(
        long? expectedGeneration,
        Guid? noteId,
        CloudNoteAccountScope? expectedAccount = null,
        long? expectedAccountGeneration = null)
        => (expectedGeneration is null
            || expectedGeneration.Value == _cloudUi.NoteGeneration)
           && noteId is not null
           && _current?.NoteId == noteId
           && _cloud.CurrentAccountScope is not null
           && (expectedAccount is null || expectedAccount == _cloud.CurrentAccountScope)
           && (expectedAccountGeneration is null
               || expectedAccountGeneration.Value == _cloud.AccountGeneration);

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

    private sealed record CloudVersionRow(NoteMaterialUploadDto Version, string Label);

    private sealed record CloudArtifactRow(
        NoteDerivedArtifactDto Artifact,
        NoteDerivedVersionSummaryDto Version,
        string Label);

    private static string VersionFlags(NoteMaterialUploadDto version)
        => version.IsCurrent ? "[当前]" : version.HasConflict ? "[冲突]" : "[历史]";

    private static string ResultFlags(NoteDerivedVersionSummaryDto version)
        => version.IsHead ? "[当前]" : version.HasConflict ? "[冲突]" : "[历史]";

    private static string ArtifactLabel(NoteDerivedArtifactKind kind) => kind switch
    {
        NoteDerivedArtifactKind.CompleteRecord => "完整记录",
        NoteDerivedArtifactKind.IndependentAnalysis => "独立分析",
        _ => kind.ToString(),
    };

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
