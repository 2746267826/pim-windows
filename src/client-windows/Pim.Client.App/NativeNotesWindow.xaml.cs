using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;
using Point = System.Windows.Point;

namespace Pim.Client.App;

public partial class NativeNotesWindow : Window
{
    private readonly LocalNoteStore _store;
    private readonly WindowsRecordingService _recording;
    private readonly CloudNoteMaterialService _cloud;
    private readonly ObservableCollection<LocalNoteSummary> _notes = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly NativeNoteOperationCoordinator _operations = new();
    private readonly NativeNoteLibraryLoadState _library = new();
    private readonly NativeNoteLibraryViewState _libraryView = new();
    private readonly NativeNoteWindowState _windowState = new();
    private readonly NativeNoteLayoutState _layout = new(1444);
    private readonly CloudNotesUiState _cloudUi = new();
    private LocalNoteDocument? _current;
    private bool _loading;
    private bool _closing;
    private bool _allowClose;
    private bool _closed;
    private bool _finishing;
    private CancellationTokenSource? _notesRefreshCancellation;
    private HashSet<Guid> _recordingBaselineMaterialIds = [];
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
        DataContext = _windowState;
        _store = App.Services.GetRequiredService<LocalNoteStore>();
        _recording = App.Services.GetRequiredService<WindowsRecordingService>();
        _cloud = App.Services.GetRequiredService<CloudNoteMaterialService>();
        NotesList.ItemsSource = _notes;
        _saveTimer.Tick += OnSaveTimer;
        _recording.StateChanged += OnRecordingStateChanged;
        ApplyResponsiveLayout();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        await InitializeLibraryAsync();
    }

    private async Task InitializeLibraryAsync()
    {
        var initialRequest = _library.BeginRefresh(SearchBox.Text);
        ApplyNotesPresentation();
        if (_current is null)
        {
            ShowEditorLoading("正在准备笔记库…", "正文、照片、手写和录音先保存在本机。");
        }

        try
        {
            await _store.EnsureInitializedAsync();
            if (!await RefreshNotesAsync(
                    SearchBox.Text,
                    requestGeneration: initialRequest))
            {
                if (!_library.IsCurrent(initialRequest))
                {
                    return;
                }

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
            if (!_library.IsCurrent(initialRequest))
            {
                return;
            }

            if (_library.TryFail(initialRequest, ex.Message))
            {
                ApplyNotesPresentation();
            }

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
            if (!await TryRunNoteOperationAsync(
                    async () =>
                    {
                        if (!await SavePendingCoreAsync())
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
                        NotesList.SelectedItem = _notes.FirstOrDefault(
                            summary => summary.NoteId == note.NoteId);
                    },
                    "正在结束并保存当前笔记，新建未开始。"))
            {
                return;
            }
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

        if (_finishing)
        {
            RestoreSelectedNote(_current?.NoteId);
            return;
        }

        if (_current?.NoteId == summary.NoteId)
        {
            CollapseOverlayLibrary();
            return;
        }

        var previousNoteId = _current?.NoteId;
        var selectionGeneration = _cloudUi.BeginNoteSelection();
        var artifactGeneration = _cloudUi.BeginArtifactSelection();
        if (_cloudUi.IsDraftDirty && !ConfirmDiscardCloudDraft("切换笔记"))
        {
            RestoreSelectedNote(previousNoteId);
            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
            return;
        }

        if (await LoadNoteThroughBarrierAsync(
                summary,
                selectionGeneration,
                artifactGeneration,
                previousNoteId,
                savePendingFirst: true))
        {
            CollapseOverlayLibrary();
        }
    }

    private async Task<bool> LoadNoteThroughBarrierAsync(
        LocalNoteSummary summary,
        long selectionGeneration,
        long artifactGeneration,
        Guid? previousNoteId,
        bool savePendingFirst)
    {
        var lease = _operations.TryBeginOperation();
        if (lease is null)
        {
            RestoreSelectedNote(previousNoteId);
            RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
            ShowNotice("正在结束并保存当前笔记，本次切换未生效。");
            return false;
        }

        var entered = false;
        try
        {
            await lease.EnterSerializedAsync();
            entered = true;
            if (_operations.IsFinishing)
            {
                RestoreSelectedNote(previousNoteId);
                RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
                ShowNotice("正在结束并保存当前笔记，本次切换未生效。");
                return false;
            }

            if (savePendingFirst && !await SavePendingCoreAsync())
            {
                RestoreSelectedNote(previousNoteId);
                RestoreCloudResultEditor(selectionGeneration, artifactGeneration);
                return false;
            }

            return await LoadNoteAsync(summary, selectionGeneration, artifactGeneration);
        }
        finally
        {
            if (entered)
            {
                lease.ExitSerialized();
            }

            lease.Dispose();
        }
    }

    private void RestoreSelectedNote(Guid? noteId)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            NotesList.SelectedItem = noteId is { } id
                ? _notes.FirstOrDefault(note => note.NoteId == id)
                : null;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private async Task<bool> LoadNoteAsync(
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
                RestoreSelectedNote(previousNoteId);
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
            return false;
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
            return false;
        }

        _cloudUi.ClearDraft();
        CloudResultBox.IsEnabled = true;
        _loading = true;
        _current = note;
        TitleBox.IsEnabled = true;
        MarkdownBox.IsEnabled = true;
        TitleBox.Text = note.Title;
        MarkdownBox.Text = note.Markdown;
        NotesList.SelectedItem = _notes.FirstOrDefault(item => item.NoteId == note.NoteId);
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

        return true;
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
        var previousNoteId = _current?.NoteId;
        RestoreSelectedNote(summary.NoteId);
        await LoadNoteThroughBarrierAsync(
            summary,
            selectionGeneration,
            artifactGeneration,
            previousNoteId,
            savePendingFirst: true);
    }

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null || _closing || _finishing)
        {
            return;
        }

        UpdateWindowChrome();
        UpdateDocumentFlow();
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
        if (_current is null || _finishing)
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
        if (_current is null || _finishing)
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
        if (_finishing)
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
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (!await SavePendingCoreAsync())
                    {
                        return;
                    }

                    var imported = await _store.ImportAsync(dialog.FileName);
                    await RefreshNotesAsync();
                    NotesList.SelectedItem = _notes.FirstOrDefault(
                        summary => summary.NoteId == imported.NoteId);
                    SaveStateText.Text =
                        $"已导入固定版本，{imported.Materials.Count} 项材料已校验 SHA-256";
                },
                "正在结束并保存当前笔记，笔记包导入未开始。");
        }
        catch (Exception ex)
        {
            ShowFailure("导入笔记包失败；原笔记库未切换", ex);
        }
    }

    private async void OnAddHandwriting(object sender, RoutedEventArgs e)
    {
        if (_current is null || _finishing)
        {
            return;
        }

        var noteId = _current.NoteId;
        var noteGeneration = _cloudUi.NoteGeneration;
        var account = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        try
        {
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (!await SavePendingCoreAsync()
                        || !IsHandwritingSourceCurrent(
                            noteId,
                            noteGeneration,
                            account,
                            accountGeneration))
                    {
                        return;
                    }

                    var note = _current!;
                    var fence = new HandwritingEditFence(
                        note.NoteId,
                        note.CurrentVersionId,
                        noteGeneration,
                        account,
                        accountGeneration);

                    var dialog = new HandwritingWindow { Owner = this };
                    if (dialog.ShowDialog() != true
                        || dialog.Capture is null
                        || !IsHandwritingFenceCurrent(fence))
                    {
                        return;
                    }

                    var beforeIds = note.Materials
                        .Select(material => material.MaterialId)
                        .ToHashSet();
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
                    var handwritingId = saved.Materials
                        .Where(material => !beforeIds.Contains(material.MaterialId))
                        .Where(material => material.Kind == LocalNoteMaterialKind.Handwriting)
                        .Select(material => material.MaterialId)
                        .LastOrDefault();
                    if (handwritingId != Guid.Empty)
                    {
                        SetMarkdownWithInsertedMarkers([handwritingId]);
                        if (!await SavePendingCoreAsync())
                        {
                            return;
                        }
                    }

                    var savedFence = fence with { VersionId = _current!.CurrentVersionId };
                    if (!IsHandwritingFenceCurrent(savedFence))
                    {
                        return;
                    }

                    UpdateMaterials();
                    await RefreshNotesAsync(
                        SearchBox.Text,
                        () => IsHandwritingFenceCurrent(savedFence));
                    if (!IsHandwritingFenceCurrent(savedFence))
                    {
                        return;
                    }

                    await RefreshVersionsAsync(
                        _current.NoteId,
                        _current.CurrentVersionId,
                        () => IsHandwritingFenceCurrent(savedFence));
                    if (!IsHandwritingFenceCurrent(savedFence))
                    {
                        return;
                    }

                    SaveStateText.Text = "手写墨迹与 PNG 已保存为新的固定版本";
                },
                "正在结束并保存当前笔记，手写材料未写入。");
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
        if (_finishing
            || _current is null
            || MaterialsList.SelectedItem is not LocalNoteMaterial selected
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
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (!await SavePendingCoreAsync()
                        || !IsHandwritingSourceCurrent(
                            noteId,
                            selectionGeneration,
                            account,
                            accountGeneration)
                        || MaterialsList.SelectedItem is not LocalNoteMaterial currentSelection
                        || currentSelection.MaterialId != selected.MaterialId)
                    {
                        return;
                    }

                    var note = _current!;
                    var fence = new HandwritingEditFence(
                        note.NoteId,
                        note.CurrentVersionId,
                        selectionGeneration,
                        account,
                        accountGeneration);
                    var versionFiles = await _store.ResolveVersionMaterialsAsync(
                        note.NoteId,
                        note.CurrentVersionId);
                    if (!IsHandwritingFenceCurrent(fence))
                    {
                        return;
                    }

                    var previewFile = versionFiles.SingleOrDefault(
                        file => file.Material.MaterialId == selected.MaterialId);
                    if (previewFile is null
                        || previewFile.Material.Kind != LocalNoteMaterialKind.Handwriting)
                    {
                        throw new InvalidDataException("所选手写预览不属于当前固定版本。");
                    }

                    var sourceMaterial = LocalInkMaterialPairing.ResolveSource(
                        previewFile.Material.MaterialId,
                        versionFiles.Select(file => file.Material).ToArray());
                    var sourceFile = versionFiles.Single(
                        file => file.Material.MaterialId == sourceMaterial.MaterialId);
                    var previewStem = RemoveMaterialExtension(
                        previewFile.Material.DisplayName,
                        ".png");

                    await VerifyMaterialAsync(previewFile, 64L * 1024 * 1024);
                    if (!IsHandwritingFenceCurrent(fence)
                        || MaterialsList.SelectedItem is not LocalNoteMaterial verifiedSelection
                        || verifiedSelection.MaterialId != selected.MaterialId)
                    {
                        return;
                    }

                    var vectorBytes = await ReadVerifiedMaterialAsync(
                        sourceFile,
                        LocalInkDocumentCodec.MaxDocumentBytes);
                    var ink = LocalInkDocumentCodec.Parse(
                        new UTF8Encoding(false, true).GetString(vectorBytes));
                    if (sourceMaterial.Width != ink.Width
                        || sourceMaterial.Height != ink.Height
                        || previewFile.Material.Width != ink.Width
                        || previewFile.Material.Height != ink.Height)
                    {
                        throw new InvalidDataException("手写源尺寸与材料清单不一致。");
                    }

                    if (!IsHandwritingFenceCurrent(fence)
                        || MaterialsList.SelectedItem is not LocalNoteMaterial currentSourceSelection
                        || currentSourceSelection.MaterialId != selected.MaterialId)
                    {
                        return;
                    }

                    var dialog = new HandwritingWindow(ink)
                    {
                        Owner = this,
                        Title = "编辑手写材料",
                    };
                    if (dialog.ShowDialog() != true
                        || dialog.Capture is null
                        || !IsHandwritingFenceCurrent(fence)
                        || MaterialsList.SelectedItem is not LocalNoteMaterial stillSelected
                        || stillSelected.MaterialId != selected.MaterialId)
                    {
                        return;
                    }

                    var beforeIds = note.Materials
                        .Select(material => material.MaterialId)
                        .ToHashSet();
                    using var png = new MemoryStream(dialog.Capture.PngBytes, writable: false);
                    var saved = await _store.AddHandwritingAsync(
                        note.NoteId,
                        png,
                        dialog.Capture.VectorJson,
                        previewStem,
                        dialog.Capture.Width,
                        dialog.Capture.Height,
                        expectedCurrentVersionId: fence.VersionId);
                    if (!IsHandwritingFenceCurrent(fence))
                    {
                        return;
                    }

                    _current = saved;
                    var handwritingId = saved.Materials
                        .Where(material => !beforeIds.Contains(material.MaterialId))
                        .Where(material => material.Kind == LocalNoteMaterialKind.Handwriting)
                        .Select(material => material.MaterialId)
                        .LastOrDefault();
                    if (handwritingId != Guid.Empty)
                    {
                        SetMarkdownWithInsertedMarkers([handwritingId]);
                        if (!await SavePendingCoreAsync())
                        {
                            return;
                        }
                    }

                    var savedFence = fence with { VersionId = _current!.CurrentVersionId };
                    if (!IsHandwritingFenceCurrent(savedFence))
                    {
                        return;
                    }

                    UpdateMaterials();
                    await RefreshNotesAsync(
                        SearchBox.Text,
                        () => IsHandwritingFenceCurrent(savedFence));
                    if (!IsHandwritingFenceCurrent(savedFence))
                    {
                        return;
                    }

                    await RefreshVersionsAsync(
                        _current.NoteId,
                        _current.CurrentVersionId,
                        () => IsHandwritingFenceCurrent(savedFence));
                    if (!IsHandwritingFenceCurrent(savedFence))
                    {
                        return;
                    }

                    SaveStateText.Text = "已将编辑后的笔迹追加为新固定版本；原材料仍保留";
                },
                "正在结束并保存当前笔记，编辑后的手写未写入。");
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
        ApplyEditorInteractionState();
        NativeNoteFinishResult result;
        try
        {
            result = await _operations.FinishAsync(async () =>
            {
                if (_recording.IsRecording)
                {
                    if (_recording.CurrentNoteId != _current.NoteId)
                    {
                        throw new InvalidOperationException(
                            "当前录音属于另一篇笔记。请先切回那篇笔记结束后再结束本篇；"
                            + "本篇正文仍保留在窗口中。");
                    }

                    var previousMaterialIds = _current.Materials
                        .Select(material => material.MaterialId)
                        .ToHashSet();
                    var recording = await _recording.StopAsync();
                    if (recording is null)
                    {
                        RecordingStatusText.Text = "本段没有生成可用音频，继续保存正文。";
                    }
                    else
                    {
                        _current = recording.Note;
                        var recordingMaterialIds = recording.Note.Materials
                            .Where(material => !previousMaterialIds.Contains(material.MaterialId))
                            .Where(material => material.Kind is LocalNoteMaterialKind.Recording
                                or LocalNoteMaterialKind.RecordingSegment)
                            .GroupBy(material => material.RecordingId ?? material.MaterialId)
                            .Select(group => group
                                .OrderBy(material => material.RecordingSegment ?? 1)
                                .ThenBy(material => material.RecordingChunkIndex ?? 1)
                                .First()
                                .MaterialId)
                            .ToArray();
                        SetMarkdownWithInsertedMarkers(recordingMaterialIds);
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
            });
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

            ApplyEditorInteractionState();
        }

        if (!result.Started)
        {
            return;
        }

        if (!result.Succeeded)
        {
            RecordingStatusText.Text = "结束未完成；正文仍保留在窗口中";
            ShowFailure(
                "结束记录失败；正文仍保留在窗口中，可以重试",
                result.Failure ?? new InvalidOperationException("结束流程未能完成。"),
                FinishCurrentNoteAsync);
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
        if (_current is null || _finishing)
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
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (!await SavePendingCoreAsync() || _current is null)
                    {
                        return;
                    }

                    var result = await _store.ExportAsync(
                        _current.NoteId,
                        _current.CurrentVersionId,
                        dialog.FileName);
                    MessageBox.Show(
                        $"已导出固定版本，包含 {result.Materials.Count} 项材料。\nSHA-256：{result.Sha256}",
                        "笔记包已导出",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                },
                "正在结束并保存当前笔记，导出未开始。");
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

        var lease = _operations.TryBeginOperation();
        if (lease is null)
        {
            ShowNotice("正在结束并保存当前笔记，录音操作未开始。");
            return;
        }

        await lease.EnterSerializedAsync();
        try
        {
            if (_recording.IsRecording)
            {
                var baselineIds = _recordingBaselineMaterialIds.Count > 0
                    ? _recordingBaselineMaterialIds
                    : _current.Materials.Select(material => material.MaterialId).ToHashSet();
                var recording = await _recording.StopAsync();
                if (recording is null)
                {
                    _recordingBaselineMaterialIds = [];
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
                    _cloudUi.BeginNoteSelection();
                    _cloudUi.BeginArtifactSelection();
                    await RefreshNotesAsync();
                    var recordingNote = _notes.FirstOrDefault(summary => summary.NoteId == recording.NoteId);
                    if (recordingNote is null && !string.IsNullOrWhiteSpace(SearchBox.Text))
                    {
                        _loading = true;
                        SearchBox.Text = string.Empty;
                        _loading = false;
                        await RefreshNotesAsync();
                        recordingNote = _notes.FirstOrDefault(
                            summary => summary.NoteId == recording.NoteId);
                    }

                    _current = recording.Note;
                    _loading = true;
                    NotesList.SelectedItem = recordingNote;
                    TitleBox.Text = _current.Title;
                    MarkdownBox.Text = _current.Markdown;
                    _loading = false;
                    UpdateWindowChrome();

                    MessageBox.Show(
                        this,
                        "录音已保存到开始时绑定的另一篇笔记。窗口已切换到该笔记所在列表项。",
                        "录音已保存",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                var marked = NativeNoteDocumentLayout.ReferencedMaterialIds(MarkdownBox.Text);
                var newRecordingIds = _current.Materials
                    .Where(material => !baselineIds.Contains(material.MaterialId))
                    .Where(material => material.Kind is LocalNoteMaterialKind.Recording
                        or LocalNoteMaterialKind.RecordingSegment)
                    .Where(material => !marked.Contains(material.MaterialId))
                    .GroupBy(material => material.RecordingId ?? material.MaterialId)
                    .Select(group => group
                        .OrderBy(material => material.RecordingSegment ?? 1)
                        .ThenBy(material => material.RecordingChunkIndex ?? 1)
                        .First()
                        .MaterialId)
                    .ToArray();
                SetMarkdownWithInsertedMarkers(newRecordingIds);
                if (!await SavePendingCoreAsync())
                {
                    return;
                }

                _recordingBaselineMaterialIds = [];
                UpdateMaterials();
                RecordingStatusText.Text =
                    $"本段录音 {recording.Wave.Duration:mm\\:ss} 已保存；可继续写或补录。";
                SaveStateText.Text = "本段原始 WAV 已保存到本机固定版本；云端尚未提交";
                await RefreshNotesAsync(SearchBox.Text);
                await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
                return;
            }

            if (!await SavePendingCoreAsync())
            {
                return;
            }

            _recordingBaselineMaterialIds = _current.Materials
                .Select(material => material.MaterialId)
                .ToHashSet();
            await _recording.StartAsync(_current.NoteId);
            RecordButton.Content = "■ 停止本段";
        }
        catch (Exception ex)
        {
            RecordingStatusText.Text = "录音操作未完成；正文仍保留";
            ShowFailure("主动录音失败", ex);
        }
        finally
        {
            lease.ExitSerialized();
            lease.Dispose();
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
                ApplyEditorInteractionState();
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

            ApplyEditorInteractionState();
            UpdateWindowChrome();
        });
    }

    private async Task ImportFilesAsync(IReadOnlyList<string> paths, LocalNoteMaterialKind kind)
    {
        var lease = _operations.TryBeginOperation();
        if (lease is null)
        {
            ShowNotice("正在结束并保存当前笔记，材料导入未开始。");
            return;
        }

        await lease.EnterSerializedAsync();
        try
        {
            if (!await SavePendingCoreAsync() || _current is null)
            {
                return;
            }

            var beforeIds = _current.Materials
                .Select(material => material.MaterialId)
                .ToHashSet();
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

            var newMaterialIds = _current.Materials
                .Where(material => !beforeIds.Contains(material.MaterialId))
                .Where(material => kind == LocalNoteMaterialKind.Photo
                    ? material.Kind == LocalNoteMaterialKind.Photo
                    : material.Kind is LocalNoteMaterialKind.Recording
                        or LocalNoteMaterialKind.RecordingSegment)
                .Select(material => material.MaterialId)
                .Distinct()
                .ToArray();
            SetMarkdownWithInsertedMarkers(newMaterialIds);
            if (!await SavePendingCoreAsync())
            {
                return;
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
        finally
        {
            lease.ExitSerialized();
            lease.Dispose();
        }
    }

    private async Task<bool> TryRunNoteOperationAsync(
        Func<Task> operation,
        string busyMessage)
    {
        var started = await _operations.TryRunExclusiveAsync(operation);
        if (!started)
        {
            ShowNotice(busyMessage);
        }

        return started;
    }

    private async Task<bool> SavePendingAsync()
    {
        _saveTimer.Stop();
        if (_current is null || _loading)
        {
            return true;
        }

        var lease = _operations.TryBeginOperation();
        if (lease is null)
        {
            return false;
        }

        await lease.EnterSerializedAsync();
        try
        {
            return await SavePendingCoreAsync();
        }
        finally
        {
            lease.ExitSerialized();
            lease.Dispose();
        }
    }

    private async Task<bool> SavePendingCoreAsync()
    {
        if (_current is null || _loading)
        {
            return true;
        }

        var title = TitleBox.Text;
        var markdown = MarkdownBox.Text;
        var orderedMaterials = NativeNoteDocumentLayout.OrderMaterials(
            markdown,
            _current.Materials);
        if (string.Equals(_current.Title, title, StringComparison.Ordinal)
            && string.Equals(_current.Markdown, markdown, StringComparison.Ordinal)
            && _current.Materials.Select(material => material.MaterialId)
                .SequenceEqual(orderedMaterials.Select(material => material.MaterialId)))
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

    private async Task<bool> RefreshNotesAsync(
        string? query = null,
        Func<bool>? applyGuard = null,
        long? requestGeneration = null)
    {
        var wasShowingLoadError = _library.Error is not null;
        var querySnapshot = query ?? SearchBox.Text;
        var generation = requestGeneration ?? _library.BeginRefresh(querySnapshot);
        if (!_library.IsCurrent(generation))
        {
            return false;
        }

        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(
            ref _notesRefreshCancellation,
            cancellation);
        previousCancellation?.Cancel();
        ApplyNotesPresentation();
        try
        {
            var rows = await _store.ListAsync(querySnapshot, cancellation.Token);
            if (_closed)
            {
                _library.TryAbandon(generation);
                return false;
            }

            if (applyGuard is not null && !applyGuard())
            {
                _library.TryAbandon(generation);
                return false;
            }

            if (!_library.TryApply(generation, rows))
            {
                return false;
            }

            var selectedAtApply = _current?.NoteId
                ?? (NotesList.SelectedItem as LocalNoteSummary)?.NoteId;
            var viewport = CaptureNotesViewport(selectedAtApply);
            _libraryView.Capture(
                _notes.Select(note => note.NoteId).ToArray(),
                selectedAtApply,
                viewport.AnchorNoteId,
                viewport.AnchorOffsetPixels,
                viewport.SelectedVisible);
            _notes.Clear();
            foreach (var row in rows)
            {
                _notes.Add(row);
            }

            RestoreSelectedNote(selectedAtApply);

            if (_libraryView.CreateRestorePlan(
                    rows.Select(note => note.NoteId).ToArray(),
                    selectedAtApply) is { } restorePlan)
            {
                await RestoreNotesViewportAsync(restorePlan, generation);
            }

            if (wasShowingLoadError && _library.IsCurrent(generation))
            {
                ClearNotice();
            }

            return _library.IsCurrent(generation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _library.TryAbandon(generation);
            return false;
        }
        catch (Exception ex)
        {
            if (!_library.TryFail(generation, ex.Message))
            {
                return false;
            }

            ShowFailure(
                "读取本机笔记库失败；原有笔记没有被改动",
                ex,
                async () => await RefreshNotesAsync(querySnapshot));
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _notesRefreshCancellation,
                null,
                cancellation);
            cancellation.Dispose();

            if (!_closed && _library.IsCurrent(generation))
            {
                ApplyNotesPresentation();
            }
        }
    }

    private (Guid? AnchorNoteId, double AnchorOffsetPixels, bool SelectedVisible)
        CaptureNotesViewport(Guid? selectedNoteId)
    {
        if (_notes.Count == 0)
        {
            return (null, 0, false);
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(NotesList);
        if (scrollViewer is null)
        {
            return (selectedNoteId, 0, selectedNoteId is not null);
        }

        Guid? anchorNoteId = null;
        var anchorOffset = 0d;
        var selectedVisible = false;
        for (var index = 0; index < _notes.Count; index++)
        {
            if (NotesList.ItemContainerGenerator.ContainerFromIndex(index)
                    is not FrameworkElement container
                || !container.IsVisible)
            {
                continue;
            }

            var top = container.TranslatePoint(new Point(0, 0), scrollViewer).Y;
            var bottom = top + container.ActualHeight;
            if (bottom <= 0 || top >= scrollViewer.ViewportHeight)
            {
                continue;
            }

            if (anchorNoteId is null || top < anchorOffset)
            {
                anchorNoteId = _notes[index].NoteId;
                anchorOffset = top;
            }

            if (_notes[index].NoteId == selectedNoteId)
            {
                selectedVisible = true;
            }
        }

        return (
            anchorNoteId ?? selectedNoteId,
            anchorNoteId is null ? 0 : anchorOffset,
            selectedVisible);
    }

    private async Task RestoreNotesViewportAsync(
        NativeNoteLibraryViewportPlan plan,
        long requestGeneration)
    {
        if (_closed)
        {
            return;
        }

        await Dispatcher.InvokeAsync(
            () =>
            {
                if (_closed || !_library.IsCurrent(requestGeneration))
                {
                    return;
                }

                NotesList.UpdateLayout();
                var scrollViewer = FindVisualChild<ScrollViewer>(NotesList);
                if (scrollViewer is null)
                {
                    return;
                }

                if (plan.ScrollTargetNoteId is { } targetId
                    && _notes.FirstOrDefault(note => note.NoteId == targetId) is { } target)
                {
                    NotesList.ScrollIntoView(target);
                    NotesList.UpdateLayout();
                    if (NotesList.ItemContainerGenerator.ContainerFromItem(target)
                        is FrameworkElement targetContainer)
                    {
                        var currentTop = targetContainer.TranslatePoint(new Point(0, 0), scrollViewer).Y;
                        scrollViewer.ScrollToVerticalOffset(
                            scrollViewer.VerticalOffset
                            + currentTop
                            - plan.ScrollTargetOffsetPixels);
                        NotesList.UpdateLayout();
                    }
                }

                if (plan.EnsureVisibleNoteId is { } selectedId
                    && _notes.FirstOrDefault(note => note.NoteId == selectedId) is { } selected)
                {
                    var selectedContainer =
                        NotesList.ItemContainerGenerator.ContainerFromItem(selected)
                        as FrameworkElement;
                    if (selectedContainer is null
                        || !IsElementVisibleInViewport(selectedContainer, scrollViewer))
                    {
                        NotesList.ScrollIntoView(selected);
                    }
                }
            },
            DispatcherPriority.Loaded);
    }

    private static bool IsElementVisibleInViewport(
        FrameworkElement element,
        ScrollViewer scrollViewer)
    {
        var top = element.TranslatePoint(new Point(0, 0), scrollViewer).Y;
        var bottom = top + element.ActualHeight;
        return bottom > 0 && top < scrollViewer.ViewportHeight;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void ApplyNotesPresentation()
    {
        var presentation = _library.Presentation;
        var isError = presentation.State == NativeNoteLibraryState.Error;
        var dangerBrush = (System.Windows.Media.Brush)FindResource("PimDangerBrush");
        var primaryBrush = (System.Windows.Media.Brush)FindResource("PimPrimaryBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("PimTextBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("PimBorderBrush");
        NotesList.Visibility = presentation.ShowList ? Visibility.Visible : Visibility.Collapsed;
        NotesStatePanel.Visibility = presentation.ShowOverlay
            ? Visibility.Visible
            : Visibility.Collapsed;
        NotesRefreshBar.Visibility = presentation.ShowInlineProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        NotesInlineErrorPanel.Visibility = presentation.ShowInlineError
            ? Visibility.Visible
            : Visibility.Collapsed;
        NotesInlineRetryButton.Visibility = presentation.ShowRetryAction
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        NotesStateDetailText.Text = presentation.Detail;
        NotesInlineErrorText.Text = _library.Error ?? presentation.Detail;
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
            UpdateDocumentFlow();
            ApplyEditorInteractionState();
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
        if (_current.IsServerSourceReadOnly)
        {
            MaterialsText.Text += " · 云端混排原件只读，导出保留完整块序";
        }

        UpdateDocumentFlow();
        ApplyEditorInteractionState();
        UpdateWindowChrome();
    }

    private void ApplyEditorInteractionState()
    {
        var hasCurrent = _current is not null;
        var readOnly = hasCurrent && _current!.IsServerSourceReadOnly;
        var canEdit = hasCurrent && !_finishing;
        TitleBox.IsEnabled = canEdit;
        MarkdownBox.IsEnabled = canEdit;
        TitleBox.IsReadOnly = readOnly;
        MarkdownBox.IsReadOnly = readOnly;
        AddPhotoButton.IsEnabled = canEdit && !readOnly;
        AddHandwritingButton.IsEnabled = canEdit && !readOnly;
        AddRecordingButton.IsEnabled = canEdit && !readOnly;
        ImportPackageButton.IsEnabled = !_finishing;
        ExportButton.IsEnabled = canEdit;
        RecordButton.IsEnabled = !_finishing
            && (_recording.IsRecording || canEdit && !readOnly);
        FinishButton.IsEnabled = canEdit;
        CloudPanelToggleButton.IsEnabled = canEdit;
        NotesList.IsEnabled = !_finishing;
        SearchBox.IsEnabled = !_finishing;
        NewNoteButton.IsEnabled = !_finishing;
        LibraryToggleButton.IsEnabled = !_finishing;
        DocumentFlowPanel.IsEnabled = !_finishing;
        MaterialsList.IsEnabled = !_finishing;
        CloudPanel.IsEnabled = !_finishing;
    }

    private void UpdateDocumentFlow()
    {
        DocumentFlowPanel.Children.Clear();
        if (_current is null)
        {
            return;
        }

        var layout = _current.ServerSource is { } serverSource
            ? NativeNoteDocumentLayout.ParseServerSource(serverSource, _current.Materials)
            : NativeNoteDocumentLayout.Parse(MarkdownBox.Text, _current.Materials);
        foreach (var segment in layout.Segments)
        {
            switch (segment.Kind)
            {
                case NativeNoteDocumentSegmentKind.Markdown:
                    AddDocumentText(segment.Text);
                    break;
                case NativeNoteDocumentSegmentKind.Material when segment.Material is not null:
                    AddMaterialCard(segment.Material, isUnplaced: false);
                    break;
                case NativeNoteDocumentSegmentKind.UnresolvedMaterial:
                    AddUnresolvedMarker(segment);
                    break;
            }
        }

        if (layout.UnplacedMaterials.Count > 0)
        {
            DocumentFlowPanel.Children.Add(new TextBlock
            {
                Text = "未定位材料 · 旧笔记、服务器录音或尚未插入正文的材料按保存顺序显示",
                Margin = new Thickness(0, 18, 0, 6),
                FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("PimMutedTextBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var material in layout.UnplacedMaterials)
            {
                AddMaterialCard(material, isUnplaced: true);
            }
        }

        if (DocumentFlowPanel.Children.Count == 0)
        {
            DocumentFlowPanel.Children.Add(new TextBlock
            {
                Text = "正文为空。输入文字，或使用上方按钮在当前光标处插入照片、手写和录音。",
                Margin = new Thickness(0, 6, 0, 8),
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)FindResource("PimMutedTextBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void AddDocumentText(string text)
    {
        var normalized = text.Trim('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        DocumentFlowPanel.Children.Add(new TextBlock
        {
            Text = normalized,
            Margin = new Thickness(0, 4, 0, 10),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 15,
            LineHeight = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("PimTextBrush"),
        });
    }

    private void AddUnresolvedMarker(NativeNoteDocumentSegment segment)
    {
        DocumentFlowPanel.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 12),
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (System.Windows.Media.Brush)FindResource("PimDangerBrush"),
            Background = (System.Windows.Media.Brush)FindResource("PimMutedSurfaceBrush"),
            Child = new TextBlock
            {
                Text = segment.MaterialId is { } id
                    ? $"无法定位材料标记 {id.ToString("N")[..8]}；材料可能已被旧版本移除。"
                    : "无法解析材料标记；原文仍保留在 Markdown 中。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("PimDangerBrush"),
            },
        });
    }

    private void AddMaterialCard(LocalNoteMaterial material, bool isUnplaced)
    {
        var card = new Border
        {
            Margin = new Thickness(0, 6, 0, 12),
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (System.Windows.Media.Brush)FindResource("PimBorderBrush"),
            Background = (System.Windows.Media.Brush)FindResource("PimMutedSurfaceBrush"),
        };
        var content = new DockPanel { LastChildFill = true };
        var icon = new TextBlock
        {
            Text = material.Kind switch
            {
                LocalNoteMaterialKind.Photo => "▧",
                LocalNoteMaterialKind.Handwriting or LocalNoteMaterialKind.HandwritingSource => "✎",
                LocalNoteMaterialKind.Recording or LocalNoteMaterialKind.RecordingSegment => "◉",
                _ => "◇",
            },
            Width = 34,
            FontSize = 23,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("PimPrimaryBrush"),
        };
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 6, 0, 0),
        };
        actions.Children.Add(CreateMaterialButton("打开", material, OnOpenInlineMaterial));
        if (material.Kind == LocalNoteMaterialKind.Handwriting
            && !_current!.IsServerSourceReadOnly)
        {
            actions.Children.Add(CreateMaterialButton("编辑笔迹", material, OnEditInlineMaterial));
        }

        if (isUnplaced && !_current!.IsServerSourceReadOnly)
        {
            actions.Children.Add(CreateMaterialButton("插入正文", material, OnInsertInlineMaterial));
        }

        DockPanel.SetDock(actions, Dock.Bottom);
        content.Children.Add(actions);

        var details = new StackPanel();
        details.Children.Add(new TextBlock
        {
            Text = $"{MaterialKindLabel(material.Kind)} · {material.DisplayName}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("PimTextBrush"),
        });
        details.Children.Add(new TextBlock
        {
            Text = MaterialDetail(material),
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("PimMutedTextBrush"),
        });
        content.Children.Add(details);
        card.Child = content;
        DocumentFlowPanel.Children.Add(card);
    }

    private Button CreateMaterialButton(
        string label,
        LocalNoteMaterial material,
        RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = label,
            Tag = material,
            Style = (Style)FindResource("PimSecondaryButton"),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 0),
        };
        button.Click += handler;
        return button;
    }

    private static string MaterialKindLabel(LocalNoteMaterialKind kind) => kind switch
    {
        LocalNoteMaterialKind.Photo => "照片",
        LocalNoteMaterialKind.Handwriting => "手写",
        LocalNoteMaterialKind.HandwritingSource => "手写源",
        LocalNoteMaterialKind.Recording => "录音",
        LocalNoteMaterialKind.RecordingSegment => "录音分片",
        _ => "材料",
    };

    private static string MaterialDetail(LocalNoteMaterial material)
    {
        var parts = new List<string>();
        if (material.Width is { } width && material.Height is { } height)
        {
            parts.Add($"{width} × {height}");
        }

        if (material.DurationMilliseconds is { } duration)
        {
            parts.Add(TimeSpan.FromMilliseconds(duration).ToString(@"mm\:ss"));
        }

        parts.Add($"{material.Length:N0} 字节");
        return string.Join(" · ", parts);
    }

    private async void OnOpenInlineMaterial(object sender, RoutedEventArgs e)
    {
        if (_current is null
            || sender is not FrameworkElement { Tag: LocalNoteMaterial material })
        {
            return;
        }

        try
        {
            var noteId = _current.NoteId;
            await TryRunNoteOperationAsync(
                async () =>
                {
                    var resolved = await _store.ResolveMaterialFileAsync(
                        noteId,
                        material.MaterialId);
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(resolved.AbsolutePath)
                        {
                            UseShellExecute = true,
                        });
                },
                "正在结束并保存当前笔记，材料未打开。");
        }
        catch (Exception ex)
        {
            ShowFailure("无法打开材料", ex);
        }
    }

    private void OnEditInlineMaterial(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LocalNoteMaterial material })
        {
            return;
        }

        MaterialsList.SelectedItem = material;
        OnEditHandwriting(sender, e);
    }

    private async void OnInsertInlineMaterial(object sender, RoutedEventArgs e)
    {
        if (_current is null
            || _finishing
            || sender is not FrameworkElement { Tag: LocalNoteMaterial material })
        {
            return;
        }

        var lease = _operations.TryBeginOperation();
        if (lease is null)
        {
            ShowNotice("正在结束并保存当前笔记，材料未插入正文。");
            return;
        }

        await lease.EnterSerializedAsync();
        try
        {
            SetMarkdownWithInsertedMarkers([material.MaterialId]);
            await SavePendingCoreAsync();
        }
        finally
        {
            lease.ExitSerialized();
            lease.Dispose();
        }
    }

    private void SetMarkdownWithInsertedMarkers(IEnumerable<Guid> materialIds)
    {
        var insertion = _current is { } current
            ? NativeNoteDocumentLayout.InsertMarkers(
                MarkdownBox.Text,
                MarkdownBox.CaretIndex,
                current.Materials,
                materialIds)
            : NativeNoteDocumentLayout.InsertMarkers(
                MarkdownBox.Text,
                MarkdownBox.CaretIndex,
                materialIds);
        if (string.Equals(insertion.Markdown, MarkdownBox.Text, StringComparison.Ordinal))
        {
            return;
        }

        _loading = true;
        try
        {
            MarkdownBox.Text = insertion.Markdown;
            MarkdownBox.CaretIndex = Math.Clamp(
                insertion.CaretIndex,
                0,
                MarkdownBox.Text.Length);
        }
        finally
        {
            _loading = false;
        }

        UpdateDocumentFlow();
        SaveStateText.Text = "材料已插入正文，正在等待自动保存…";
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
        ApplyEditorInteractionState();
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
        ApplyEditorInteractionState();
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
        ApplyEditorInteractionState();
        _editorStateRetry = retry;
    }

    private void ShowDocument()
    {
        DocumentScroll.Visibility = Visibility.Visible;
        EditorStatePanel.Visibility = Visibility.Collapsed;
        ApplyEditorInteractionState();
        UpdateWindowChrome();
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

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout();

    private void OnToggleLibrary(object sender, RoutedEventArgs e)
    {
        if (_finishing)
        {
            return;
        }

        _layout.ToggleLibrary();
        ApplyResponsiveLayout();
    }

    private void OnToggleCloudPanel(object sender, RoutedEventArgs e)
    {
        if (_current is null || _finishing)
        {
            return;
        }

        _layout.ToggleCloud(noteAvailable: true);
        ApplyResponsiveLayout();
    }

    private void CollapseOverlayLibrary()
    {
        if (!_layout.Current.LibraryOverlay)
        {
            return;
        }

        _layout.SetLibraryExpanded(false);
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var width = RootLayoutGrid.ActualWidth > 0
            ? RootLayoutGrid.ActualWidth
            : Math.Max(0, Width - 36);
        _layout.Resize(width);
        var layout = _layout.Current;
        var libraryDocked = layout.LibraryVisible && !layout.LibraryOverlay;
        LibraryColumn.Width = new GridLength(
            libraryDocked ? NativeNoteLayoutState.LibraryWidth : 0);
        LibraryGapColumn.Width = new GridLength(
            libraryDocked ? NativeNoteLayoutState.LibraryGap : 0);
        MainColumn.Width = new GridLength(1, GridUnitType.Star);

        NoteLibraryPanel.Visibility = layout.LibraryVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        Grid.SetColumn(NoteLibraryPanel, 0);
        Grid.SetColumnSpan(NoteLibraryPanel, layout.LibraryOverlay ? 3 : 1);
        Panel.SetZIndex(NoteLibraryPanel, layout.LibraryOverlay ? 30 : 0);

        CloudColumn.Width = new GridLength(
            layout.CloudVisible && !layout.CloudOverlay
                ? NativeNoteLayoutState.CloudWidth + NativeNoteLayoutState.CloudGap
                : 0);
        CloudPanel.Visibility = layout.CloudVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        Grid.SetColumn(CloudPanel, layout.CloudOverlay ? 0 : 1);
        Grid.SetColumnSpan(CloudPanel, layout.CloudOverlay ? 2 : 1);
        CloudPanel.HorizontalAlignment = layout.CloudOverlay
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
        CloudPanel.Margin = layout.CloudOverlay
            ? new Thickness(0)
            : new Thickness(NativeNoteLayoutState.CloudGap, 0, 0, 0);
        Panel.SetZIndex(CloudPanel, layout.CloudOverlay ? 30 : 0);

        LibraryToggleButton.Content = layout.LibraryVisible ? "隐藏列表" : "笔记列表";
        CloudPanelToggleButton.Content = layout.CloudVisible ? "隐藏版本与云端" : "版本与云端";
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
        _layout.CollapseCloud();
        ApplyResponsiveLayout();
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
        ApplyEditorInteractionState();
        UpdateWindowChrome();
    }

    private void UpdateWindowChrome()
    {
        _windowState.SetRecording(_recording.IsRecording);
        if (_current is null)
        {
            _windowState.ShowLibrary(_notes.Count > 0);
            return;
        }

        _windowState.OpenNote(_current.NoteId, TitleBox.Text);
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
        _saveTimer.Stop();
        _notesRefreshCancellation?.Cancel();
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
            var noteId = _current.NoteId;
            var versionId = selected.VersionId;
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (!await SavePendingCoreAsync()
                        || _current?.NoteId != noteId)
                    {
                        return;
                    }

                    _current = await _store.RestoreVersionAsync(noteId, versionId);
                    _loading = true;
                    TitleBox.Text = _current.Title;
                    MarkdownBox.Text = _current.Markdown;
                    _loading = false;
                    UpdateMaterials();
                    await RefreshNotesAsync(SearchBox.Text);
                    await RefreshVersionsAsync(_current.NoteId, _current.CurrentVersionId);
                    SaveStateText.Text = "历史正文与材料引用已恢复为一个新的固定版本";
                },
                "正在结束并保存当前笔记，历史版本未恢复。");
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
            var noteId = _current.NoteId;
            await TryRunNoteOperationAsync(
                async () =>
                {
                    var resolved = await _store.ResolveMaterialFileAsync(
                        noteId,
                        material.MaterialId);
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(resolved.AbsolutePath)
                        {
                            UseShellExecute = true,
                        });
                },
                "正在结束并保存当前笔记，材料未打开。");
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

        try
        {
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (!await SavePendingCoreAsync() || _current is null)
                    {
                        return;
                    }

                    CloudStatusText.Text = "正在校验并上传当前固定版本…";
                    var result = await _cloud.UploadVersionAsync(
                        _current.NoteId,
                        _current.CurrentVersionId,
                        userConfirmedStorage: true);
                    CloudStatusText.Text = result.Upload.HasConflict
                        ? $"已上传 {result.Upload.VersionId[..8]}，但服务器当前版本已被另一端更新；双方版本均保留。"
                        : $"已上传并完成固定版本 {result.Upload.VersionId[..8]}。";
                    await RefreshCloudVersionsAsync(showErrors: false);
                    await RefreshCloudResultsAsync(showErrors: false);
                },
                "正在结束并保存当前笔记，云端上传未开始。");
        }
        catch (Exception ex)
        {
            CloudStatusText.Text = "云端上传未完成；本机固定版本未修改。";
            ShowFailure("上传当前固定版本失败", ex);
        }
    }

    private async void OnRefreshCloud(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            await TryRunNoteOperationAsync(
                () => RefreshCloudVersionsAsync(showErrors: true),
                "正在结束并保存当前笔记，云端版本未刷新。");
        }
        catch (Exception ex)
        {
            ShowFailure("读取云端固定版本失败", ex);
        }
    }

    private async void OnOpenCloudSources(object sender, RoutedEventArgs e)
    {
        var dialog = new CloudNoteSourcesWindow(_operations) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedNoteId is not { } noteId)
        {
            return;
        }

        try
        {
            await TryRunNoteOperationAsync(
                async () =>
                {
                    _loading = true;
                    SearchBox.Text = string.Empty;
                    _loading = false;
                    await RefreshNotesAsync();
                    NotesList.SelectedItem = _notes.FirstOrDefault(
                        item => item.NoteId == noteId);
                },
                "正在结束并保存当前笔记，云端来源切换未生效。");
        }
        catch (Exception ex)
        {
            ShowFailure("打开云端来源失败", ex);
        }
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
            await TryRunNoteOperationAsync(
                async () =>
                {
                    if (_current is null)
                    {
                        return;
                    }

                    var promoted = await _cloud.PromoteVersionAsync(
                        _current.NoteId,
                        Guid.Parse(selected.Version.VersionId),
                        current is null ? null : Guid.Parse(current.Version.VersionId));
                    CloudStatusText.Text = $"服务器当前版本已切换为 {promoted.VersionId[..8]}。";
                    await RefreshCloudVersionsAsync(showErrors: false);
                },
                "正在结束并保存当前笔记，云端当前版本未切换。");
        }
        catch (Exception ex)
        {
            ShowFailure("切换云端当前版本失败；服务器版本未删除", ex);
        }
    }

    private async void OnRefreshCloudResults(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            await TryRunNoteOperationAsync(
                () => RefreshCloudResultsAsync(showErrors: true),
                "正在结束并保存当前笔记，云端结果未刷新。");
        }
        catch (Exception ex)
        {
            ShowFailure("读取云端结果失败", ex);
        }
    }

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
