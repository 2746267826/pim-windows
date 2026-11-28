using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CloudNotesUiState _cloudUi = new();
    private LocalNoteDocument? _current;
    private bool _loading;
    private bool _closing;
    private bool _allowClose;
    private bool _closed;
    private bool _finishing;
    private bool _notesLoading;
    private string? _notesLoadError;
    private Func<Task>? _noticeRetry;
    private Func<Task>? _editorStateRetry;
    private bool _loadingCloudResult;
    private bool _restoringCloudSelection;
    private bool _refreshingCloudResults;
    private NoteDerivedArtifactDto? _selectedArtifact;
    private NoteDerivedVersionDto? _selectedDerivedVersion;
    private NoteDerivedVersionSummaryDto? _selectedDerivedVersionSummary;
    private LoadedCloudArtifactFence? _loadedTaskSourceFence;
    private long _taskSourceSelectionGeneration;

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
        => await InitializeLibraryAsync();

    private async Task InitializeLibraryAsync()
    {
        _notesLoading = true;
        _notesLoadError = null;
        ApplyNotesPresentation();
        if (_current is null)
        {
            ShowEditorLoading("正在准备笔记库…", "正文、照片、手写和录音先保存在本机。");
        }

        try
        {
            await _store.EnsureInitializedAsync();
            if (!await RefreshNotesAsync())
            {
                ShowEmptyEditor();
                ShowEditorError(
                    "本机笔记库暂时打不开",
                    "原有笔记没有被改动。检查磁盘权限后可以重试。",
                    InitializeLibraryAsync);
                return;
            }

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
                RecordButton.Content = "■ 停止本段";
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
            _notesLoading = false;
            _notesLoadError = ex.Message;
            ApplyNotesPresentation();
            ShowEmptyEditor();
            ShowEditorError(
                "本机笔记库暂时打不开",
                "原有笔记没有被改动。检查磁盘权限后可以重试。",
                InitializeLibraryAsync);
            ShowFailure("打开本机笔记库失败", ex, InitializeLibraryAsync);
        }
    }

    private async void OnNewNote(object sender, RoutedEventArgs e)
        => await CreateNewNoteAsync();

    private async Task CreateNewNoteAsync()
    {
        try
        {
            if (!await SavePendingAsync())
            {
                return;
            }

            var note = await _store.CreateAsync();
            ClearNotice();
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                _loading = true;
                SearchBox.Text = string.Empty;
                _loading = false;
            }

            await RefreshNotesAsync();
            NotesList.SelectedItem = _notes.FirstOrDefault(summary => summary.NoteId == note.NoteId);
        }
        catch (Exception ex)
        {
            ShowFailure("新建笔记失败", ex, CreateNewNoteAsync);
        }
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _loading)
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

        if (!await SavePendingAsync())
        {
            _loading = true;
            NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == _current?.NoteId);
            _loading = false;
            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
            return;
        }

        await LoadNoteAsync(summary, selectionGeneration, artifactGeneration);
    }

    private async Task LoadNoteAsync(
        LocalNoteSummary summary,
        long selectionGeneration,
        long artifactGeneration)
    {
        var previousNoteId = _current?.NoteId;
        ShowEditorLoading("正在打开笔记…", "正在读取正文、固定版本和本机材料。");
        LocalNoteDocument note;
        try
        {
            note = await _store.LoadAsync(summary.NoteId);
        }
        catch (Exception ex)
        {
            if (selectionGeneration == _cloudUi.NoteGeneration)
            {
                _loading = true;
                NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == previousNoteId);
                _loading = false;
                RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
                if (previousNoteId is null)
                {
                    ShowEditorError(
                        "无法打开这篇笔记",
                        "原笔记没有被改动。可以重试读取。",
                        () => RetryOpenNoteAsync(summary.NoteId));
                }
                else
                {
                    ShowDocument();
                    UpdateWindowChrome();
                }

                ShowFailure(
                    "打开笔记失败；原笔记没有被改动",
                    ex,
                    () => RetryOpenNoteAsync(summary.NoteId));
            }

            if (selectionGeneration == _cloudUi.NoteGeneration)
            {
                _loading = false;
            }

            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
            return;
        }

        if (!IsSelectedNoteRequest(selectionGeneration, summary.NoteId))
        {
            if (_current is null)
            {
                ShowEditorEmpty();
            }
            else
            {
                ShowDocument();
                UpdateWindowChrome();
            }

            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
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
        ShowDocument();
        UpdateWindowChrome();

        try
        {
            await RefreshNoteDetailsAsync(note, selectionGeneration);
        }
        catch (Exception ex)
        {
            if (selectionGeneration == _cloudUi.NoteGeneration)
            {
                ShowFailure(
                    "笔记已打开，但固定版本信息暂未刷新",
                    ex,
                    () => RetryRefreshLoadedNoteAsync(
                        note.NoteId,
                        note.CurrentVersionId,
                        selectionGeneration));
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

    private async Task RefreshNoteDetailsAsync(LocalNoteDocument note, long selectionGeneration)
    {
        await RefreshVersionsAsync(note.NoteId, note.CurrentVersionId);
        if (IsCurrentNoteSelection(selectionGeneration, note.NoteId))
        {
            await RefreshCloudVersionsAsync(showErrors: false, selectionGeneration);
            await RefreshCloudResultsAsync(showErrors: false, selectionGeneration);
        }

        SaveStateText.Text = "已加载固定版本";
        ClearNotice();
    }

    private async Task RetryRefreshLoadedNoteAsync(
        Guid noteId,
        Guid versionId,
        long selectionGeneration)
    {
        if (_current?.NoteId != noteId || _current.CurrentVersionId != versionId)
        {
            return;
        }

        try
        {
            await RefreshNoteDetailsAsync(_current, selectionGeneration);
        }
        catch (Exception ex)
        {
            if (selectionGeneration == _cloudUi.NoteGeneration)
            {
                ShowFailure(
                    "笔记已打开，但固定版本信息仍未能刷新",
                    ex,
                    () => RetryRefreshLoadedNoteAsync(noteId, versionId, selectionGeneration));
            }
        }
    }

    private async Task RetryOpenNoteAsync(Guid noteId)
    {
        var summary = _notes.FirstOrDefault(note => note.NoteId == noteId);
        if (summary is null)
        {
            await RefreshNotesAsync(SearchBox.Text);
            return;
        }

        var selectionGeneration = _cloudUi.BeginNoteSelection();
        var artifactGeneration = _cloudUi.BeginArtifactSelection();
        _loading = true;
        NotesList.SelectedItem = summary;
        _loading = false;
        await LoadNoteAsync(summary, selectionGeneration, artifactGeneration);
    }

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null || _closing)
        {
            return;
        }

        UpdateWindowChrome();
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

        var noteId = _current.NoteId;
        var noteGeneration = _cloudUi.NoteGeneration;
        var account = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        try
        {
            if (!await SavePendingAsync())
            {
                return;
            }

            if (!IsHandwritingSourceCurrent(noteId, noteGeneration, account, accountGeneration))
            {
                return;
            }

            var note = _current!;
            var fence = new HandwritingEditFence(note.NoteId, note.CurrentVersionId, noteGeneration, account, accountGeneration);

            var dialog = new HandwritingWindow { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Capture is null)
            {
                return;
            }

            if (!IsHandwritingFenceCurrent(fence))
            {
                return;
            }

            using var png = new MemoryStream(dialog.Capture.PngBytes, writable: false);
            var saved = await _store.AddHandwritingAsync(
                note.NoteId,
                png,
                dialog.Capture.VectorJson,
                $"手写 {DateTime.Now:MM-dd HHmmss}",
                dialog.Capture.Width,
                dialog.Capture.Height,
                expectedCurrentVersionId: fence.VersionId);
            if (!IsHandwritingFenceCurrent(fence))
            {
                return;
            }

            _current = saved;
            var savedFence = fence with { VersionId = saved.CurrentVersionId };
            UpdateMaterials();
            await RefreshNotesAsync(SearchBox.Text, () => IsHandwritingFenceCurrent(savedFence));
            if (!IsHandwritingFenceCurrent(savedFence))
            {
                return;
            }

            await RefreshVersionsAsync(saved.NoteId, saved.CurrentVersionId, () => IsHandwritingFenceCurrent(savedFence));
            if (!IsHandwritingFenceCurrent(savedFence))
            {
                return;
            }

            SaveStateText.Text = "手写墨迹与 PNG 已保存为新的固定版本";
        }
        catch (Exception ex)
        {
            if (IsHandwritingSourceCurrent(noteId, noteGeneration, account, accountGeneration))
            {
                ShowFailure("手写材料未能保存", ex);
            }
        }
    }

    private async void OnEditHandwriting(object sender, RoutedEventArgs e)
    {
        if (_current is null || MaterialsList.SelectedItem is not LocalNoteMaterial selected
            || selected.Kind != LocalNoteMaterialKind.Handwriting)
        {
            MessageBox.Show(this, "请先选择一项手写材料。", "编辑手写", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_current.IsServerSourceReadOnly)
        {
            MessageBox.Show(this, "服务器混排原件当前为只读，不能在本机编辑笔迹。", "编辑手写", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var noteId = _current.NoteId;
        var selectionGeneration = _cloudUi.NoteGeneration;
        var account = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        try
        {
            if (!await SavePendingAsync()
                || !IsHandwritingSourceCurrent(noteId, selectionGeneration, account, accountGeneration)
                || MaterialsList.SelectedItem is not LocalNoteMaterial currentSelection
                || currentSelection.MaterialId != selected.MaterialId)
            {
                return;
            }

            var note = _current!;
            var fence = new HandwritingEditFence(note.NoteId, note.CurrentVersionId, selectionGeneration, account, accountGeneration);
            var versionFiles = await _store.ResolveVersionMaterialsAsync(note.NoteId, note.CurrentVersionId);
            if (!IsHandwritingFenceCurrent(fence))
            {
                return;
            }

            var previewFile = versionFiles.SingleOrDefault(file => file.Material.MaterialId == selected.MaterialId);
            if (previewFile is null || previewFile.Material.Kind != LocalNoteMaterialKind.Handwriting)
            {
                throw new InvalidDataException("所选手写预览不属于当前固定版本。");
            }

            var sourceMaterial = LocalInkMaterialPairing.ResolveSource(
                previewFile.Material.MaterialId,
                versionFiles.Select(file => file.Material).ToArray());
            var sourceFile = versionFiles.Single(file => file.Material.MaterialId == sourceMaterial.MaterialId);
            var previewStem = RemoveMaterialExtension(previewFile.Material.DisplayName, ".png");

            await VerifyMaterialAsync(previewFile, 64L * 1024 * 1024);
            if (!IsHandwritingFenceCurrent(fence)
                || MaterialsList.SelectedItem is not LocalNoteMaterial verifiedSelection
                || verifiedSelection.MaterialId != selected.MaterialId)
            {
                return;
            }

            var vectorBytes = await ReadVerifiedMaterialAsync(sourceFile, LocalInkDocumentCodec.MaxDocumentBytes);
            var ink = LocalInkDocumentCodec.Parse(new UTF8Encoding(false, true).GetString(vectorBytes));
            if (sourceMaterial.Width != ink.Width || sourceMaterial.Height != ink.Height
                || previewFile.Material.Width != ink.Width || previewFile.Material.Height != ink.Height)
            {
                throw new InvalidDataException("手写源尺寸与材料清单不一致。");
            }

            if (!IsHandwritingFenceCurrent(fence)
                || MaterialsList.SelectedItem is not LocalNoteMaterial currentSourceSelection
                || currentSourceSelection.MaterialId != selected.MaterialId)
            {
                return;
            }

            var dialog = new HandwritingWindow(ink) { Owner = this, Title = "编辑手写材料" };
            if (dialog.ShowDialog() != true || dialog.Capture is null)
            {
                return;
            }

            if (!IsHandwritingFenceCurrent(fence)
                || MaterialsList.SelectedItem is not LocalNoteMaterial stillSelected
                || stillSelected.MaterialId != selected.MaterialId)
            {
                return;
            }

            using var png = new MemoryStream(dialog.Capture.PngBytes, writable: false);
            var saved = await _store.AddHandwritingAsync(
                note.NoteId, png, dialog.Capture.VectorJson, previewStem,
                dialog.Capture.Width, dialog.Capture.Height, expectedCurrentVersionId: fence.VersionId);
            if (!IsHandwritingFenceCurrent(fence))
            {
                return;
            }

            _current = saved;
            var savedFence = fence with { VersionId = saved.CurrentVersionId };
            UpdateMaterials();
            await RefreshNotesAsync(SearchBox.Text, () => IsHandwritingFenceCurrent(savedFence));
            if (!IsHandwritingFenceCurrent(savedFence))
            {
                return;
            }

            await RefreshVersionsAsync(saved.NoteId, saved.CurrentVersionId, () => IsHandwritingFenceCurrent(savedFence));
            if (!IsHandwritingFenceCurrent(savedFence))
            {
                return;
            }

            SaveStateText.Text = "已将编辑后的笔迹追加为新固定版本；原材料仍保留";
        }
        catch (Exception ex)
        {
            if (IsHandwritingSourceCurrent(noteId, selectionGeneration, account, accountGeneration))
            {
                ShowFailure("手写材料未能重新打开或保存；原固定版本未修改", ex);
            }
        }
    }

    private bool IsHandwritingSourceCurrent(
        Guid noteId,
        long selectionGeneration,
        CloudNoteAccountScope? account,
        long accountGeneration)
        => _current?.NoteId == noteId
           && !_closed
           && selectionGeneration == _cloudUi.NoteGeneration
           && account == _cloud.CurrentAccountScope
           && accountGeneration == _cloud.AccountGeneration;

    private bool IsHandwritingFenceCurrent(HandwritingEditFence fence)
        => fence.IsCurrent(
            _current?.NoteId,
            _current?.CurrentVersionId,
            _cloudUi.NoteGeneration,
            _cloud.CurrentAccountScope,
            _cloud.AccountGeneration,
            _closed);

    private static string RemoveMaterialExtension(string value, string extension)
        => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? value[..^extension.Length]
            : value;

    private static async Task<byte[]> ReadVerifiedMaterialAsync(LocalNoteMaterialFile file, long maxBytes)
    {
        var material = file.Material;
        if (material.Length is <= 0 || material.Length > maxBytes || material.Length > int.MaxValue)
        {
            throw new InvalidDataException($"材料超过安全读取上限：{material.DisplayName}");
        }

        await using var stream = new FileStream(
            file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != material.Length)
        {
            throw new InvalidDataException($"材料长度与固定清单不一致：{material.DisplayName}");
        }

        var bytes = new byte[(int)material.Length];
        await stream.ReadExactlyAsync(bytes);
        if (stream.ReadByte() != -1
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                material.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"材料摘要校验失败：{material.DisplayName}");
        }

        return bytes;
    }

    private static async Task VerifyMaterialAsync(LocalNoteMaterialFile file, long maxBytes)
    {
        var material = file.Material;
        if (material.Length is <= 0 || material.Length > maxBytes)
        {
            throw new InvalidDataException($"预览材料超过安全校验上限：{material.DisplayName}");
        }

        await using var stream = new FileStream(
            file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != material.Length)
        {
            throw new InvalidDataException($"预览长度与固定清单不一致：{material.DisplayName}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"预览材料超过安全校验上限：{material.DisplayName}");
            }

            hash.AppendData(buffer, 0, read);
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset());
        if (total != material.Length
            || !string.Equals(digest, material.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"预览摘要校验失败：{material.DisplayName}");
        }
    }

    private async void OnFinishNote(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show(
                this,
                "请先选择或新建一篇笔记。",
                "结束记录",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                "结束这次记录？\n\n"
                + "会先停止当前录音，并把最新正文和材料保存为本机固定版本。"
                + "本次不会自动上传，也不会启动转写或模型任务。",
                "结束这次记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await FinishCurrentNoteAsync();
    }

    private async Task FinishCurrentNoteAsync()
    {
        if (_current is null || _finishing)
        {
            return;
        }

        _finishing = true;
        _saveTimer.Stop();
        FinishButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        NotesList.IsEnabled = false;
        SearchBox.IsEnabled = false;
        await _saveGate.WaitAsync();
        try
        {
            if (_recording.IsRecording)
            {
                if (_recording.CurrentNoteId != _current.NoteId)
                {
                    ShowNotice(
                        "当前录音属于另一篇笔记。请先切回那篇笔记结束后再结束本篇；"
                        + "本篇正文仍保留在窗口中。");
                    return;
                }

                var recording = await _recording.StopAsync();
                if (recording is null)
                {
                    RecordingStatusText.Text = "本段没有生成可用音频，继续保存正文。";
                }
                else
                {
                    _current = recording.Note;
                    RecordingStatusText.Text =
                        $"本段录音 {recording.Wave.Duration:mm\\:ss} 已保存到本机。";
                }
            }

            var noteId = _current.NoteId;
            var finishedTitle = TitleBox.Text;
            var finishedMarkdown = MarkdownBox.Text;
            var finishedNote = await _store.FinalizeAsync(noteId, finishedTitle, finishedMarkdown);
            _current = finishedNote;
            UpdateMaterials();
            UpdateWindowChrome();
            ShowDocument();
            RecordingStatusText.Text = "本次记录已结束；可继续补录，或稍后手动上传。";
            ClearNotice();

            var finishedVersionId = finishedNote.CurrentVersionId;
            await RefreshNotesAsync(SearchBox.Text);
            try
            {
                await RefreshVersionsAsync(noteId, finishedVersionId);
            }
            catch (Exception refreshException)
            {
                ShowFailure(
                    "已结束并保存本机固定版本，但版本列表暂未刷新",
                    refreshException,
                    () => RefreshFinishedVersionsAsync(noteId, finishedVersionId));
            }

            SaveStateText.Text = NativeNotePresentation.SessionFinished(finishedVersionId);
            var hasNewerEdits = !string.Equals(TitleBox.Text, finishedNote.Title, StringComparison.Ordinal)
                || !string.Equals(MarkdownBox.Text, finishedNote.Markdown, StringComparison.Ordinal);
            if (hasNewerEdits)
            {
                SaveStateText.Text += " 当前窗口仍有新的未保存修改。";
                _saveTimer.Start();
            }
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = "结束未完成；正文仍保留在窗口中";
            ShowFailure(
                "结束记录失败；正文仍保留在窗口中，可以重试",
                ex,
                FinishCurrentNoteAsync);
        }
        finally
        {
            _finishing = false;
            if (_current is not null)
            {
                UpdateMaterials();
            }
            else
            {
                FinishButton.IsEnabled = false;
            }

            NotesList.IsEnabled = true;
            SearchBox.IsEnabled = true;
            _saveGate.Release();
        }
    }

    private async Task RefreshFinishedVersionsAsync(Guid noteId, Guid versionId)
    {
        try
        {
            await RefreshVersionsAsync(noteId, versionId);
            if (_current?.NoteId == noteId)
            {
                SaveStateText.Text = NativeNotePresentation.SessionFinished(versionId);
                ClearNotice();
            }
        }
        catch (Exception ex)
        {
            ShowFailure(
                "已结束并保存本机固定版本，但版本列表仍未能刷新",
                ex,
                () => RefreshFinishedVersionsAsync(noteId, versionId));
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
                    SaveStateText.Text = "本段录音没有产生可用音频；正文仍保留在窗口中";
                    RecordingStatusText.Text = "本段未生成音频，可以继续记录或结束整篇。";
                    return;
                }

                if (_current.NoteId == recording.NoteId)
                {
                    _current = recording.Note;
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
                RecordingStatusText.Text =
                    $"本段录音 {recording.Wave.Duration:mm\\:ss} 已保存；可继续写或补录。";
                SaveStateText.Text = "本段原始 WAV 已保存到本机固定版本；云端尚未提交";
                await RefreshNotesAsync(SearchBox.Text);
                await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
                return;
            }

            if (!await SavePendingAsync())
            {
                return;
            }

            await _recording.StartAsync(_current.NoteId);
            RecordButton.Content = "■ 停止本段";
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = "录音操作未完成；正文仍保留";
            ShowFailure("主动录音失败", ex);
        }
    }

    private void OnRecordingStateChanged(object? sender, WindowsRecordingState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (state.IsRecording)
            {
                RecordingStatusText.Text =
                    $"本段录音中 {state.Elapsed:mm\\:ss} · 实际输入峰值 {state.PeakLevel:P0}";
                RecordButton.IsEnabled = !_finishing;
                UpdateWindowChrome();
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
                RecordButton.IsEnabled = !_finishing
                    && (_current is null || !_current.IsServerSourceReadOnly);
            }

            UpdateWindowChrome();
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

        await _saveGate.WaitAsync();
        try
        {
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
                UpdateWindowChrome();
                SaveStateText.Text = "已保存为新的本机固定版本";
                ClearNotice();
                await RefreshNotesAsync(SearchBox.Text);
                await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
                return true;
            }
            catch (Exception ex)
            {
                SaveStateText.Text = "保存失败，当前正文仍保留在窗口中";
                ShowFailure("本机自动保存失败；正文仍在窗口中", ex, SavePendingAsync);
                return false;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> RefreshNotesAsync(string? query = null, Func<bool>? applyGuard = null)
    {
        var wasShowingLoadError = _notesLoadError is not null;
        _notesLoading = true;
        _notesLoadError = null;
        ApplyNotesPresentation();
        var selectedId = _current?.NoteId;
        try
        {
            var rows = await _store.ListAsync(query);
            if (applyGuard is not null && !applyGuard())
            {
                return false;
            }

            _notes.Clear();
            foreach (var row in rows)
            {
                _notes.Add(row);
            }

            if (selectedId is { } id)
            {
                NotesList.SelectedItem = _notes.FirstOrDefault(note => note.NoteId == id);
            }

            if (wasShowingLoadError)
            {
                ClearNotice();
            }

            return true;
        }
        catch (Exception ex)
        {
            _notesLoadError = ex.Message;
            ShowFailure(
                "读取本机笔记库失败；原有笔记没有被改动",
                ex,
                async () => await RefreshNotesAsync(query));
            return false;
        }
        finally
        {
            _notesLoading = false;
            ApplyNotesPresentation();
        }
    }

    private void ApplyNotesPresentation()
    {
        var presentation = NativeNotePresentation.Library(
            _notesLoading,
            _notesLoadError is not null,
            _notes.Count,
            SearchBox.Text);
        var isError = presentation.State == NativeNoteLibraryState.Error;
        var dangerBrush = (System.Windows.Media.Brush)FindResource("PimDangerBrush");
        var primaryBrush = (System.Windows.Media.Brush)FindResource("PimPrimaryBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("PimTextBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("PimBorderBrush");
        NotesStatePanel.Visibility = presentation.State == NativeNoteLibraryState.Ready
            ? Visibility.Collapsed
            : Visibility.Visible;
        NotesStatePanel.BorderBrush = isError ? dangerBrush : borderBrush;
        NotesStateTitleText.Foreground = isError ? dangerBrush : textBrush;
        NotesStateIconText.Foreground = isError ? dangerBrush : primaryBrush;
        NotesLoadingBar.Visibility = presentation.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        NotesStateCreateButton.Visibility =
            presentation.ShowCreateAction ? Visibility.Visible : Visibility.Collapsed;
        NotesStateClearSearchButton.Visibility =
            presentation.ShowClearSearchAction ? Visibility.Visible : Visibility.Collapsed;
        NotesStateRetryButton.Visibility =
            presentation.ShowRetryAction ? Visibility.Visible : Visibility.Collapsed;
        NotesStateTitleText.Text = presentation.Title;
        NotesStateDetailText.Text = string.IsNullOrWhiteSpace(_notesLoadError)
            ? presentation.Detail
            : $"{presentation.Detail}\n{_notesLoadError}";
        NotesStateIconText.Text = presentation.State switch
        {
            NativeNoteLibraryState.Loading => "…",
            NativeNoteLibraryState.EmptySearch => "⌕",
            NativeNoteLibraryState.Error => "!",
            _ => "✎",
        };
    }

    private void UpdateMaterials()
    {
        if (_current is null)
        {
            MaterialsText.Text = "固定材料版本 · 尚无材料";
            MaterialsList.ItemsSource = null;
            FinishButton.IsEnabled = false;
            CloudPanelToggleButton.IsEnabled = false;
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
        RecordButton.IsEnabled = !_finishing
            && (_recording.IsRecording || !_current.IsServerSourceReadOnly);
        FinishButton.IsEnabled = !_finishing;
        CloudPanelToggleButton.IsEnabled = !_finishing;
        if (_current.IsServerSourceReadOnly)
        {
            MaterialsText.Text += " · 云端混排原件只读，导出保留完整块序";
        }

        UpdateWindowChrome();
    }

    private void ShowEditorLoading(string title, string detail)
    {
        DocumentScroll.Visibility = Visibility.Collapsed;
        EditorStatePanel.Visibility = Visibility.Visible;
        EditorStateProgress.Visibility = Visibility.Visible;
        EditorStateTitleText.Text = title;
        EditorStateTitleText.Foreground =
            (System.Windows.Media.Brush)FindResource("PimTextBrush");
        EditorStateDetailText.Text = detail;
        EditorStateCreateButton.Visibility = Visibility.Collapsed;
        EditorStateRetryButton.Visibility = Visibility.Collapsed;
        FinishButton.IsEnabled = false;
        CloudPanelToggleButton.IsEnabled = false;
        _editorStateRetry = null;
    }

    private void ShowEditorEmpty()
    {
        DocumentScroll.Visibility = Visibility.Collapsed;
        EditorStatePanel.Visibility = Visibility.Visible;
        EditorStateProgress.Visibility = Visibility.Collapsed;
        EditorStateTitleText.Text = "从一页空白开始";
        EditorStateTitleText.Foreground =
            (System.Windows.Media.Brush)FindResource("PimTextBrush");
        EditorStateDetailText.Text = "新建一篇笔记，正文、照片、手写和录音都会先保存在本机。";
        EditorStateCreateButton.Visibility = Visibility.Visible;
        EditorStateRetryButton.Visibility = Visibility.Collapsed;
        FinishButton.IsEnabled = false;
        CloudPanelToggleButton.IsEnabled = false;
        _editorStateRetry = null;
    }

    private void ShowEditorError(string title, string detail, Func<Task> retry)
    {
        DocumentScroll.Visibility = Visibility.Collapsed;
        EditorStatePanel.Visibility = Visibility.Visible;
        EditorStateProgress.Visibility = Visibility.Collapsed;
        EditorStateTitleText.Text = title;
        EditorStateTitleText.Foreground =
            (System.Windows.Media.Brush)FindResource("PimDangerBrush");
        EditorStateDetailText.Text = detail;
        EditorStateCreateButton.Visibility = Visibility.Collapsed;
        EditorStateRetryButton.Visibility = Visibility.Visible;
        FinishButton.IsEnabled = false;
        CloudPanelToggleButton.IsEnabled = false;
        _editorStateRetry = retry;
    }

    private void ShowDocument()
    {
        DocumentScroll.Visibility = Visibility.Visible;
        EditorStatePanel.Visibility = Visibility.Collapsed;
        FinishButton.IsEnabled = _current is not null && !_finishing;
        CloudPanelToggleButton.IsEnabled = _current is not null && !_finishing;
        _editorStateRetry = null;
    }

    private void ClearNotice()
    {
        _noticeRetry = null;
        EditorNoticePanel.Visibility = Visibility.Collapsed;
        RetryNoticeButton.Visibility = Visibility.Collapsed;
        EditorNoticeText.Text = string.Empty;
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e)
        => ClearNotice();

    private async void OnRetryNotice(object sender, RoutedEventArgs e)
    {
        if (_noticeRetry is not { } retry)
        {
            return;
        }

        RetryNoticeButton.IsEnabled = false;
        try
        {
            await retry();
        }
        finally
        {
            RetryNoticeButton.IsEnabled = true;
        }
    }

    private async void OnRetryEditorState(object sender, RoutedEventArgs e)
    {
        if (_editorStateRetry is not { } retry)
        {
            return;
        }

        EditorStateRetryButton.IsEnabled = false;
        try
        {
            await retry();
        }
        finally
        {
            EditorStateRetryButton.IsEnabled = true;
        }
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
        => SearchBox.Text = string.Empty;

    private async void OnRetryNotes(object sender, RoutedEventArgs e)
    {
        var refreshed = await RefreshNotesAsync(SearchBox.Text);
        if (!refreshed)
        {
            return;
        }

        ClearNotice();
        if (_current is not null)
        {
            return;
        }

        if (_notes.Count > 0)
        {
            NotesList.SelectedIndex = 0;
        }
        else if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            ShowEmptyEditor();
        }
    }

    private void OnToggleCloudPanel(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        CloudPanel.Visibility = CloudPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowEmptyEditor()
    {
        _cloudUi.BeginNoteSelection();
        _cloudUi.BeginArtifactSelection();
        Interlocked.Increment(ref _taskSourceSelectionGeneration);
        _loadedTaskSourceFence = null;
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
        FinishButton.IsEnabled = false;
        CloudPanelToggleButton.IsEnabled = false;
        CloudPanel.Visibility = Visibility.Collapsed;
        CloudPanelToggleButton.Content = "版本与云端";
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
        ClearNotice();
        ShowEditorEmpty();
        UpdateWindowChrome();
    }

    private void UpdateWindowChrome()
    {
        if (_current is null)
        {
            Title = _notes.Count == 0 ? "笔记库" : "选择一篇笔记";
            EditorContextText.Text = _notes.Count == 0
                ? "从一页空白开始"
                : "选择一篇笔记继续";
            return;
        }

        Title = NativeNotePresentation.WindowTitle(TitleBox.Text);
        EditorContextText.Text = _recording.IsRecording
            ? "正在记录 · 本机草稿"
            : "本机草稿 · 自动保存";
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (_finishing)
        {
            e.Cancel = true;
            ShowNotice("正在结束并保存本机固定版本，请稍候再关闭窗口。");
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
        _closed = true;
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

    private async Task RefreshVersionsAsync(Guid noteId, Guid currentVersionId, Func<bool>? applyGuard = null)
    {
        var versions = await _store.ListVersionsAsync(noteId);
        if (applyGuard is not null && !applyGuard())
        {
            return;
        }

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

        _loading = true;
        SearchBox.Text = string.Empty;
        _loading = false;
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
                Interlocked.Increment(ref _taskSourceSelectionGeneration);
                _loadedTaskSourceFence = null;
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
            Interlocked.Increment(ref _taskSourceSelectionGeneration);
            _loadedTaskSourceFence = null;
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

        var sourceSelectionGeneration = Interlocked.Increment(ref _taskSourceSelectionGeneration);
        _loadedTaskSourceFence = null;
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
            _loadedTaskSourceFence = new LoadedCloudArtifactFence(
                sourceSelectionGeneration,
                selectionGeneration,
                noteId!.Value,
                Guid.Parse(row.Artifact.ArtifactId),
                Guid.Parse(row.Version.VersionId),
                accountScope!,
                accountGeneration);
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

        if (_loadedTaskSourceFence is not { } loadedSource
            || !Guid.TryParse(_selectedArtifact.ArtifactId, out var loadedArtifactId)
            || !Guid.TryParse(_selectedDerivedVersion.VersionId, out var loadedVersionId)
            || !CloudNotesUiState.IsLoadedCloudArtifactCurrent(
                loadedSource,
                Interlocked.Read(ref _taskSourceSelectionGeneration),
                _cloudUi.NoteGeneration,
                _current.NoteId,
                loadedArtifactId,
                loadedVersionId,
                _cloud.CurrentAccountScope,
                _cloud.AccountGeneration))
        {
            CloudResultStateText.Text = "固定来源回执已过期或与当前账号/笔记/版本不匹配；请刷新并重新读取后再打开。";
            return;
        }

        try
        {
            var operationFence = App.Services
                .GetRequiredService<NoteTaskSuggestionService>()
                .CaptureFence();
            if (operationFence.Account != loadedSource.Account
                || operationFence.AccountGeneration != loadedSource.AccountGeneration
                || !CloudNotesUiState.IsLoadedCloudArtifactCurrent(
                    loadedSource,
                    Interlocked.Read(ref _taskSourceSelectionGeneration),
                    _cloudUi.NoteGeneration,
                    _current.NoteId,
                    loadedArtifactId,
                    loadedVersionId,
                    _cloud.CurrentAccountScope,
                    _cloud.AccountGeneration))
            {
                CloudResultStateText.Text = "账号或固定来源在打开前已变化；请刷新并重新读取来源。";
                return;
            }

            var window = new NoteTaskSuggestionsWindow(
                _current.NoteId,
                _selectedArtifact,
                _selectedDerivedVersionSummary,
                _selectedDerivedVersion,
                operationFence)
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
        LocalNoteCheckpointReason.Finished => "结束记录",
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

    private void ShowFailure(
        string operation,
        Exception exception,
        Func<Task>? retry = null)
    {
        SaveStateText.Text = operation;
        var detail = string.IsNullOrWhiteSpace(exception.Message)
            ? operation
            : $"{operation}：{exception.Message}";
        ShowNotice(detail, retry);
    }

    private void ShowNotice(string message, Func<Task>? retry = null)
    {
        _noticeRetry = retry;
        EditorNoticeText.Text = message;
        RetryNoticeButton.Visibility = retry is null ? Visibility.Collapsed : Visibility.Visible;
        EditorNoticePanel.Visibility = Visibility.Visible;
    }
}
