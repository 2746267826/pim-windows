using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public partial class NoteTaskSuggestionsWindow : Window
{
    private readonly Guid _noteId;
    private readonly NoteDerivedArtifactDto _artifact;
    private readonly NoteDerivedVersionSummaryDto _versionSummary;
    private readonly NoteDerivedVersionDto _version;
    private readonly NoteTaskSuggestionSource _source;
    private readonly NoteTaskSuggestionOperationFence _operationFence;
    private readonly NoteTaskSuggestionService _service;
    private readonly CloudNoteMaterialService _cloud;
    private readonly ApiClient _api;
    private readonly NoteTaskSuggestionUiState _uiState = new();
    private readonly ObservableCollection<SuggestionRow> _suggestions = [];
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _draftSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private NoteTaskSuggestionLoadFence _loadFence = null!;
    private NoteTaskSuggestionSelectionFence? _selectionFence;
    private NoteTaskSuggestionDto? _selectedSuggestion;
    private NoteTaskSuggestionPendingConfirmation? _pendingConfirmation;
    private bool _confirmationUnresolved;
    private Guid? _extractionId;
    private long _editorGeneration;
    private bool _loadingEditor;
    private bool _refreshingSuggestions;
    private bool _sessionInvalid;
    private bool _closed;
    private bool _allowClose;
    private bool _pendingCloseAcknowledged;
    private bool _draftSaveInProgress;

    public NoteTaskSuggestionsWindow(
        Guid noteId,
        NoteDerivedArtifactDto artifact,
        NoteDerivedVersionSummaryDto versionSummary,
        NoteDerivedVersionDto version,
        NoteTaskSuggestionOperationFence operationFence)
    {
        InitializeComponent();
        _noteId = noteId;
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _versionSummary = versionSummary ?? throw new ArgumentNullException(nameof(versionSummary));
        _version = version ?? throw new ArgumentNullException(nameof(version));
        _operationFence = operationFence ?? throw new ArgumentNullException(nameof(operationFence));
        _service = App.Services.GetRequiredService<NoteTaskSuggestionService>();
        _cloud = App.Services.GetRequiredService<CloudNoteMaterialService>();
        _api = App.Services.GetRequiredService<ApiClient>();
        _api.ExternalScopeChanged += OnExternalScopeChanged;
        SuggestionsList.ItemsSource = _suggestions;
        PriorityBox.ItemsSource = Enumerable.Range(1, 9).ToArray();
        _pollTimer.Tick += OnPollTick;
        _draftSaveTimer.Tick += OnDraftSaveTick;
        _source = new NoteTaskSuggestionSource(
            noteId,
            Guid.TryParse(artifact.ArtifactId, out var artifactId) ? artifactId : Guid.Empty,
            Guid.TryParse(version.VersionId, out var versionId) ? versionId : Guid.Empty,
            version.ContentSha256,
            version.InputSha256,
            version.ManifestSha256,
            version.GenerationTaskId);
        _loadFence = _uiState.BeginLoad(
            noteId,
            _source.ArtifactId,
            _source.VersionId,
            _source,
            operationFence.Account,
            operationFence.AccountGeneration);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SourceTextBox.Text = _version.ContentMarkdown;
        SourceIdentityText.Text = $"笔记 {_noteId:D} · 固定版本 {_source.VersionId:D} · {_version.Kind}";
        SourceHashText.Text = $"正文 SHA-256：{_source.ContentSha256}\n输入 SHA-256：{_source.InputSha256} · 清单 SHA-256：{_source.ManifestSha256}";
        if (!IsSessionCurrent())
        {
            InvalidateSession("打开窗口前账号或服务器已切换；请关闭后重新打开。");
            return;
        }

        StatusText.Text = "正在读取文本模型、任务本和日历目录…";
        try
        {
            var destinationsTask = _service.GetDestinationsAsync(expectedFence: _operationFence);
            var taskBooksTask = _service.GetTaskBooksAsync(expectedFence: _operationFence);
            var calendarsTask = _service.GetCalendarsAsync(expectedFence: _operationFence);
            await Task.WhenAll(destinationsTask, taskBooksTask, calendarsTask);
            if (!IsSessionCurrent())
            {
                InvalidateSession("读取期间账号或服务器已切换；云端操作已停用，本机草稿仍可保留。");
                return;
            }

            var destinations = destinationsTask.Result
                .Where(item => item.Capabilities.Contains("Text", StringComparer.OrdinalIgnoreCase))
                .ToArray();
            DestinationBox.ItemsSource = destinations;
            DestinationBox.SelectedItem = destinations.FirstOrDefault();
            TaskBookBox.ItemsSource = new[] { new ChoiceRow(null, "不指定任务本") }
                .Concat(taskBooksTask.Result
                    .Where(item => Guid.TryParse(item.Id, out _))
                    .Select(item => new ChoiceRow(item.Id, item.Name)))
                .ToArray();
            TaskBookBox.SelectedIndex = 0;
            CalendarBox.ItemsSource = new[] { new ChoiceRow(null, "不指定日历") }
                .Concat(calendarsTask.Result
                    .Where(item => Guid.TryParse(item.Id, out _))
                    .Select(item => new ChoiceRow(item.Id, item.Name)))
                .ToArray();
            CalendarBox.SelectedIndex = 0;
            StatusText.Text = "目录已就绪；源正文为固定版本只读副本。";
            await RefreshSuggestionsAsync(selectId: null);
            UpdateActions();
        }
        catch (Exception)
        {
            if (IsSessionCurrent())
                StatusText.Text = "读取目录失败；未发送模型提取请求，本机笔记与草稿保持不变。";
            else
                InvalidateSession("账号或服务器已切换；云端操作已停用，本机草稿仍可保留。");
        }
    }

    private async void OnStartExtraction(object sender, RoutedEventArgs e)
    {
        if (_actionGate.CurrentCount == 0
            || !IsSessionCurrent()
            || DestinationBox.SelectedItem is not NoteDerivedDestinationDto destination)
        {
            StatusText.Text = "请登录有效账号并选择服务器提供的文本模型。";
            return;
        }

        if (MessageBox.Show(
                this,
                $"将把所选固定版本正文发送给“{destination.DisplayName}”（{destination.Model}，修订 {destination.ModelRevision}）进行任务提取。\n\n模型服务可能产生费用；只有选择“是”才会开始外部调用。",
                "确认模型与费用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            StatusText.Text = "已取消；未启动模型请求。";
            return;
        }

        if (!await _actionGate.WaitAsync(0))
            return;
        var refreshSuggestionsAfterRelease = false;
        UpdateActions();
        try
        {
            StatusText.Text = "正在提交固定来源提取请求…";
            var extraction = await _service.StartExtractionAsync(
                _noteId,
                _artifact,
                _versionSummary,
                _version,
                destination,
                userConfirmedPossibleCharge: true,
                expectedFence: _operationFence);
            if (!IsSessionCurrent())
            {
                InvalidateSession("提交期间账号或服务器已切换；未采用旧会话回执。请重新登录后核对服务器状态。");
                return;
            }

            _extractionId = Guid.Parse(extraction.Id);
            ApplyExtractionState(extraction);
            if (IsTerminal(extraction.Status))
            {
                refreshSuggestionsAfterRelease = extraction.Status == NoteTaskSuggestionExtractionStatusDto.Succeeded;
                await HandleTerminalExtractionAsync(extraction, refreshSuggestions: false);
            }
            else
                _pollTimer.Start();
        }
        catch (Exception)
        {
            StatusText.Text = IsSessionCurrent()
                ? "提取请求失败；固定来源未修改。可刷新状态后核对服务器记录。"
                : "账号或服务器已切换；提取回执未采用，本机笔记未修改。";
        }
        finally
        {
            _actionGate.Release();
            UpdateActions();
        }
        if (refreshSuggestionsAfterRelease && IsSessionCurrent())
            await RefreshSuggestionsAsync(selectId: null);
    }

    private async void OnRefreshExtraction(object sender, RoutedEventArgs e)
    {
        if (_actionGate.CurrentCount == 0 || _draftSaveInProgress || _loadingEditor)
            return;
        if (_extractionId is not { } extractionId || !IsSessionCurrent())
        {
            await RefreshSuggestionsAsync(_selectedSuggestion?.Id, preserveSelection: true);
            return;
        }

        await PollExtractionAsync(extractionId);
    }

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_extractionId is { } extractionId)
            await PollExtractionAsync(extractionId);
    }

    private async Task PollExtractionAsync(Guid extractionId)
    {
        if (!IsSessionCurrent())
        {
            InvalidateSession("账号或服务器已切换；提取轮询已停止，本机草稿保留。");
            return;
        }

        _pollTimer.Stop();
        try
        {
            var extraction = await _service.GetExtractionAsync(
                extractionId,
                _source,
                expectedFence: _operationFence);
            if (!IsSessionCurrent())
            {
                InvalidateSession("轮询期间账号或服务器已切换；未采用旧状态回执。");
                return;
            }

            ApplyExtractionState(extraction);
            if (IsTerminal(extraction.Status))
                await HandleTerminalExtractionAsync(extraction);
            else
                _pollTimer.Start();
        }
        catch (Exception)
        {
            StatusText.Text = IsSessionCurrent()
                ? "暂时无法刷新提取状态；记录仍保留，可稍后重试。"
                : "账号或服务器已切换；提取轮询已停止。";
        }
        finally
        {
            UpdateActions();
        }
    }

    private async Task HandleTerminalExtractionAsync(
        NoteTaskSuggestionExtractionDto extraction,
        bool refreshSuggestions = true)
    {
        _pollTimer.Stop();
        if (extraction.Status == NoteTaskSuggestionExtractionStatusDto.Succeeded)
        {
            StatusText.Text = $"提取完成：服务器返回 {extraction.SuggestionCount} 条建议。";
            if (refreshSuggestions)
                await RefreshSuggestionsAsync(selectId: null);
        }
        else
        {
            StatusText.Text = "提取失败；来源笔记未修改，可刷新状态或查看已有建议。";
        }
    }

    private void ApplyExtractionState(NoteTaskSuggestionExtractionDto extraction)
    {
        StatusText.Text = extraction.Status switch
        {
            NoteTaskSuggestionExtractionStatusDto.Queued => "提取已排队；窗口关闭不会撤销服务器任务。",
            NoteTaskSuggestionExtractionStatusDto.Running => "服务器正在处理固定来源…",
            NoteTaskSuggestionExtractionStatusDto.Succeeded => $"提取完成：{extraction.SuggestionCount} 条建议。",
            NoteTaskSuggestionExtractionStatusDto.Failed => "提取失败；固定来源未修改。",
            _ => "服务器返回未知提取状态。",
        };
        UpdateActions();
    }

    private async Task RefreshSuggestionsAsync(string? selectId, bool preserveSelection = false)
    {
        if (!IsSessionCurrent())
        {
            InvalidateSession("账号或服务器已切换；云端建议暂不可读，本机草稿仍在。" );
            return;
        }

        var previous = _selectedSuggestion;
        var preserveEditor = preserveSelection && _uiState.IsDraftDirty;
        if (_uiState.IsDraftDirty && previous is not null)
        {
            var generation = _uiState.DraftGeneration;
            var selectionBeforeSave = _selectionFence;
            var draft = BuildDraft();
            _loadingEditor = true;
            _draftSaveTimer.Stop();
            UpdateActions();
            try
            {
                await PersistLocalDraftAsync(previous, draft);
                if (_uiState.IsDraftCurrent(generation)
                    && _selectedSuggestion?.Id == previous.Id
                    && _selectionFence == selectionBeforeSave)
                {
                    _uiState.TryClearDraft(generation);
                    EditorStateText.Text = "本机草稿已保存；正在刷新建议，服务器内容未更改。";
                }
                else
                {
                    return;
                }
            }
            catch (Exception)
            {
                EditorStateText.Text = "本机草稿保存失败；当前编辑内容保留，未刷新建议。";
                return;
            }
            finally
            {
                _loadingEditor = false;
                UpdateActions();
            }
        }

        var ticket = _uiState.BeginSelection(_loadFence, Guid.Empty);
        _selectionFence = ticket;
        var refreshFence = new NoteTaskSuggestionOperationFence(
            _operationFence.Account, _operationFence.AccountGeneration);
        try
        {
            var items = await _service.GetDraftSuggestionsAsync(
                _noteId,
                _source,
                expectedFence: refreshFence);
            if (!IsCurrentRefresh(ticket, refreshFence))
                return;

            if (preserveEditor)
            {
                _selectionFence = previous is null
                    ? null
                    : _uiState.BeginSelection(_loadFence, Guid.Parse(previous.Id));
                SuggestionListStateText.Text = items.Count == 0
                    ? "服务器当前没有待处理建议；现有编辑内容和本机草稿均保留。"
                    : "建议列表已刷新；当前编辑内容和本机草稿保留，请先保存或切换建议。";
                return;
            }

            _refreshingSuggestions = true;
            _suggestions.Clear();
            foreach (var item in items.OrderBy(item => item.Ordinal))
                _suggestions.Add(new SuggestionRow(item));
            var selected = _suggestions.FirstOrDefault(row => row.Suggestion.Id == selectId)
                ?? _suggestions.FirstOrDefault();
            SuggestionsList.SelectedItem = selected;
            _refreshingSuggestions = false;
            SuggestionListStateText.Text = _suggestions.Count == 0
                ? "此固定来源暂无待处理建议。"
                : $"仅显示与当前笔记、版本、正文/输入/清单摘要完全一致的 {_suggestions.Count} 条待处理建议。";
            if (selected is not null)
                await SelectSuggestionAsync(selected);
            else
                ClearEditor("选择建议后可查看或编辑；提取不会直接写入任务本。" );
        }
        catch (Exception)
        {
            _refreshingSuggestions = false;
            if (IsCurrentRefresh(ticket, refreshFence))
            {
                _selectionFence = previous is null
                    ? null
                    : _uiState.BeginSelection(_loadFence, Guid.Parse(previous.Id));
                SuggestionListStateText.Text = "读取建议失败；现有本机草稿与编辑内容未清除。";
            }
        }
    }

    private async void OnSuggestionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSuggestions
            || !NoteTaskSuggestionUiState.CanChangeSelection(
                _closed, isBusy: _actionGate.CurrentCount == 0, isLoading: _loadingEditor)
            || SuggestionsList.SelectedItem is not SuggestionRow row)
            return;
        await SelectSuggestionAsync(row);
    }

    private async Task SelectSuggestionAsync(SuggestionRow row)
    {
        var previous = _selectedSuggestion;
        var ticket = _uiState.BeginSelection(_loadFence, Guid.Parse(row.Suggestion.Id));
        _selectionFence = ticket;
        _loadingEditor = true;
        _draftSaveTimer.Stop();
        UpdateActions();
        if (previous is not null && _uiState.IsDraftDirty)
        {
            try
            {
                var generation = _editorGeneration;
                var previousDraft = BuildDraft();
                await PersistLocalDraftAsync(previous, previousDraft);
                if (!IsCurrent(ticket))
                {
                    if (_selectionFence == ticket)
                    {
                        _loadingEditor = false;
                        EditorStateText.Text = "账号或服务器已切换；本机草稿保存在原账号目录，云端选择已停止。";
                        UpdateActions();
                    }
                    return;
                }
                if (!_uiState.TryClearDraft(generation))
                    throw new InvalidOperationException("编辑内容已变化；本机草稿仍保留。");
            }
            catch (Exception)
            {
                _loadingEditor = false;
                _selectionFence = _uiState.BeginSelection(_loadFence, Guid.Parse(previous.Id));
                RestoreSuggestionSelection(previous.Id);
                EditorStateText.Text = "本机草稿保存失败；编辑内容仍保留在当前窗口，未切换建议。";
                UpdateActions();
                return;
            }
        }

        try
        {
            var draftTask = _service.LoadDraftAsync(Guid.Parse(row.Suggestion.Id), expectedFence: _operationFence);
            var pendingTask = _service.GetPendingConfirmationAsync(Guid.Parse(row.Suggestion.Id), expectedFence: _operationFence);
            await Task.WhenAll(draftTask, pendingTask);
            if (!IsCurrent(ticket))
            {
                if (_selectionFence == ticket)
                {
                    _loadingEditor = false;
                    EditorStateText.Text = "账号或服务器已切换；云端回执已丢弃，本机草稿仍可保存。";
                    UpdateActions();
                }
                return;
            }

            var draft = draftTask.Result;
            var pendingConfirmation = pendingTask.Result;
            _selectedSuggestion = row.Suggestion;
            _pendingConfirmation = pendingConfirmation;
            _confirmationUnresolved = pendingConfirmation is not null;
            TitleBox.Text = draft?.Title ?? row.Suggestion.Title;
            DescriptionBox.Text = draft?.Description ?? row.Suggestion.Description ?? string.Empty;
            PriorityBox.SelectedItem = draft?.Priority ?? row.Suggestion.Priority;
            DueDatePicker.SelectedDate = (draft?.Due ?? row.Suggestion.Due)?.LocalDateTime.Date;
            SelectChoice(TaskBookBox, draft?.TaskBookId ?? row.Suggestion.TaskBookId);
            SelectChoice(CalendarBox, draft?.CalendarId ?? row.Suggestion.CalendarId);
            if (draft is null)
                _uiState.ClearDraft();
            else
                _editorGeneration = _uiState.MarkDraftDirtyAndRemember();

            _loadingEditor = false;
            EditorStateText.Text = _pendingConfirmation is not null
                ? "确认请求尚未核实；请先核实服务器状态，期间不能编辑、放弃或重新确认。本机草稿仍保留。"
                : draft is not null
                    ? "已恢复本机编辑草稿；保存前不会改动服务器建议。"
                    : "来源为固定只读版本；修改只作用于这条建议。";
            UpdateActions();
        }
        catch (Exception)
        {
            if (_selectionFence == ticket)
            {
                _loadingEditor = false;
                _selectionFence = previous is null
                    ? null
                    : _uiState.BeginSelection(_loadFence, Guid.Parse(previous.Id));
                if (previous is not null)
                    RestoreSuggestionSelection(previous.Id);
                EditorStateText.Text = "读取建议或本机草稿失败；原始文字未覆盖。";
                UpdateActions();
            }
        }
    }

    private void OnEditorChanged(object sender, TextChangedEventArgs e) => MarkEditorChanged();
    private void OnEditorChoiceChanged(object sender, SelectionChangedEventArgs e) => MarkEditorChanged();
    private void OnDueDateChanged(object sender, SelectionChangedEventArgs e) => MarkEditorChanged();

    private void MarkEditorChanged()
    {
        if (_loadingEditor || _selectedSuggestion is null)
            return;
        _editorGeneration = _uiState.MarkDraftDirtyAndRemember();
        EditorStateText.Text = "修改尚未保存；正在安全写入本机草稿。";
        _draftSaveTimer.Stop();
        _draftSaveTimer.Start();
        UpdateActions();
    }

    private async void OnDraftSaveTick(object? sender, EventArgs e)
    {
        _draftSaveTimer.Stop();
        if (_selectedSuggestion is { } selected && _uiState.IsDraftDirty)
        {
            var selectionTicket = _selectionFence;
            var generation = _editorGeneration;
            var draft = BuildDraft();
            _draftSaveInProgress = true;
            UpdateActions();
            try
            {
                await PersistLocalDraftAsync(selected, draft);
                if (_uiState.IsDraftCurrent(generation)
                    && _selectionFence == selectionTicket
                    && IsCurrentSelection(selected))
                    EditorStateText.Text = IsSessionCurrent()
                        ? "本机草稿已保存；服务器建议尚未变更。"
                        : "账号已切换；编辑仅保存在原账号的本机草稿中，云端操作已停用。";
            }
            catch (Exception)
            {
                if (_selectionFence == selectionTicket && IsCurrentSelection(selected))
                    EditorStateText.Text = "本机草稿写入失败；编辑内容仍留在此窗口，请勿关闭窗口。";
            }
            finally
            {
                _draftSaveInProgress = false;
                UpdateActions();
            }
        }
    }

    private async Task PersistLocalDraftAsync(NoteTaskSuggestionDto suggestion, NoteTaskSuggestionEditDraft draft)
    {
        await _service.SaveLocalDraftAsync(
            Guid.Parse(suggestion.Id),
            draft,
            expectedFence: _operationFence);
    }

    private async void OnSaveEdit(object sender, RoutedEventArgs e)
    {
        if (_selectedSuggestion is not { } selected || !CanWriteServer())
            return;
        _draftSaveTimer.Stop();
        var selectionTicket = _selectionFence;
        await RunServerActionAsync(async () =>
        {
            var captured = selected;
            var draft = BuildDraft();
            EditorStateText.Text = "正在按服务器修订号保存；冲突时本机草稿保留。";
            var updated = await _service.SaveEditAsync(captured, draft, expectedFence: _operationFence);
            if (!IsCurrentSelection(captured) || _selectionFence != selectionTicket)
                return;
            ReplaceSuggestion(updated);
            _selectedSuggestion = updated;
            _loadingEditor = true;
            TitleBox.Text = updated.Title;
            DescriptionBox.Text = updated.Description ?? string.Empty;
            PriorityBox.SelectedItem = updated.Priority;
            DueDatePicker.SelectedDate = updated.Due?.LocalDateTime.Date;
            SelectChoice(TaskBookBox, updated.TaskBookId);
            SelectChoice(CalendarBox, updated.CalendarId);
            _uiState.ClearDraft();
            _loadingEditor = false;
            EditorStateText.Text = "修改已按服务器修订号保存。";
        }, "保存失败；本机草稿仍保留，可刷新后处理修订冲突。" );
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_selectedSuggestion is not { } selected || !CanWriteServer())
            return;
        _draftSaveTimer.Stop();
        var draft = BuildDraft();
        var selectionTicket = _selectionFence;
        if (MessageBox.Show(
                this,
                $"确认把“{draft.Title}”写入所选任务本？此操作会创建实际任务，并使用当前建议修订。",
                "确认写入任务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunServerActionAsync(async () =>
        {
            var captured = selected;
            var current = captured;
            if (_uiState.IsDraftDirty || DraftDiffers(current, draft))
            {
                current = await _service.SaveEditAsync(captured, draft, expectedFence: _operationFence);
                if (!IsCurrentSelection(captured) || _selectionFence != selectionTicket)
                    return;
                ReplaceSuggestion(current);
                _selectedSuggestion = current;
                _loadingEditor = true;
                TitleBox.Text = current.Title;
                DescriptionBox.Text = current.Description ?? string.Empty;
                PriorityBox.SelectedItem = current.Priority;
                DueDatePicker.SelectedDate = current.Due?.LocalDateTime.Date;
                SelectChoice(TaskBookBox, current.TaskBookId);
                SelectChoice(CalendarBox, current.CalendarId);
                _uiState.ClearDraft();
                _loadingEditor = false;
            }

            EditorStateText.Text = "正在提交固定修订的幂等确认…";
            _confirmationUnresolved = true;
            UpdateActions();
            var confirmed = await _service.ConfirmAsync(current, current.Revision, expectedFence: _operationFence);
            if (!IsCurrentSelection(captured) || _selectionFence != selectionTicket)
                return;
            _confirmationUnresolved = false;
            _pendingConfirmation = null;
            EditorStateText.Text = "任务已创建并完成服务器核实。";
        }, "确认未完成；本机草稿/幂等确认凭据保留，请先核实服务器状态。" );
        await RefreshPendingStateAsync();
        if (IsSessionCurrent())
            await RefreshSuggestionsAsync(selectId: null);
    }

    private async void OnDismiss(object sender, RoutedEventArgs e)
    {
        if (_selectedSuggestion is not { } selected || !CanWriteServer())
            return;
        _draftSaveTimer.Stop();
        if (MessageBox.Show(
                this,
                "放弃这条待处理建议？该操作不删除固定来源或本机笔记，但会清除这条建议的本机编辑草稿。",
                "放弃任务建议",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var selectionTicket = _selectionFence;
        await RunServerActionAsync(async () =>
        {
            var captured = selected;
            var dismissed = await _service.DismissAsync(captured, captured.Revision, expectedFence: _operationFence);
            if (!IsCurrentSelection(captured) || _selectionFence != selectionTicket)
                return;
            EditorStateText.Text = "建议已放弃；固定来源和本机笔记保持不变。";
        }, "放弃操作失败；本机草稿仍保留。" );
        if (IsSessionCurrent())
            await RefreshSuggestionsAsync(selectId: null);
    }

    private async void OnVerifyPending(object sender, RoutedEventArgs e)
    {
        if (!IsSessionCurrent())
        {
            InvalidateSession("账号已切换；请重新登录并重新打开窗口核实状态。");
            return;
        }
        var selected = _selectedSuggestion;
        var selectionTicket = _selectionFence;
        await RunServerActionAsync(async () =>
        {
            var result = await _service.ReconcilePendingConfirmationsAsync(expectedFence: _operationFence);
            if (selected is not null && selectionTicket is not null
                && IsCurrentSelection(selected) && _selectionFence == selectionTicket)
            {
                var pending = await _service.GetPendingConfirmationAsync(
                    Guid.Parse(selected.Id), expectedFence: _operationFence);
                if (!IsCurrentSelection(selected) || _selectionFence != selectionTicket)
                    return;
                _pendingConfirmation = pending;
                _confirmationUnresolved = pending is not null;
            }
            EditorStateText.Text = _pendingConfirmation is null
                ? $"服务器核实完成：清除 {result.Cleared} 条待确认记录，仍待处理 {result.StillPending} 条。"
                : "服务器仍返回草稿；幂等确认凭据继续保留，编辑与重复确认保持锁定。";
            if (_pendingConfirmation is null)
                await RefreshSuggestionsAsync(_selectedSuggestion?.Id);
        }, "核实失败；待确认凭据未清除，不能改用新的确认请求。", allowPendingConfirmation: true);
        UpdateActions();
    }

    private async Task RefreshPendingStateAsync()
    {
        if (_selectedSuggestion is not { } selected || !IsSessionCurrent())
            return;
        var selectionTicket = _selectionFence;
        try
        {
            var pending = await _service.GetPendingConfirmationAsync(
                Guid.Parse(selected.Id), expectedFence: _operationFence);
            if (selectionTicket is null
                || !IsCurrentSelection(selected)
                || _selectionFence != selectionTicket)
                return;
            _pendingConfirmation = pending;
            _confirmationUnresolved = pending is not null;
            UpdateActions();
        }
        catch (Exception)
        {
            if (selectionTicket is not null
                && IsCurrentSelection(selected)
                && _selectionFence == selectionTicket)
            {
                EditorStateText.Text = "无法核实确认状态；幂等凭据仍保留，相关写操作保持锁定。";
                _pendingConfirmation = new NoteTaskSuggestionPendingConfirmation(
                    Guid.Parse(selected.Id), selected.Revision, Guid.Empty, _noteId,
                    _source.ArtifactId, _source.VersionId, _source.ContentSha256,
                    _source.InputSha256, _source.ManifestSha256, _source.GenerationTaskId);
                _confirmationUnresolved = true;
                UpdateActions();
            }
        }
    }

    private async Task RunServerActionAsync(
        Func<Task> action,
        string failureMessage,
        bool allowPendingConfirmation = false)
    {
        var canStart = allowPendingConfirmation
            ? NoteTaskSuggestionUiState.CanVerifyPending(
                IsSessionCurrent(), _pendingConfirmation is not null || _confirmationUnresolved,
                isBusy: _actionGate.CurrentCount == 0)
            : CanWriteServer();
        if (!canStart || !await _actionGate.WaitAsync(0))
            return;
        UpdateActions();
        try
        {
            await action();
            if (!IsSessionCurrent())
                InvalidateSession("操作期间账号或服务器已切换；旧回执未应用。" );
        }
        catch (Exception)
        {
            if (IsSessionCurrent())
                EditorStateText.Text = failureMessage;
            else
                InvalidateSession("操作期间账号或服务器已切换；云端操作已停止，本机草稿保留。" );
        }
        finally
        {
            _actionGate.Release();
            UpdateActions();
        }
    }

    private bool CanWriteServer()
        => NoteTaskSuggestionUiState.CanWriteServer(
            IsSessionCurrent(),
            _pendingConfirmation is not null || _confirmationUnresolved,
            _selectedSuggestion?.Status == NoteTaskSuggestionStatusDto.Draft,
            _actionGate.CurrentCount == 0 || _loadingEditor || _draftSaveInProgress);

    private void UpdateActions()
    {
        var sessionCurrent = IsSessionCurrent();
        var actionIdle = _actionGate.CurrentCount > 0 && !_draftSaveInProgress;
        var canWrite = CanWriteServer();
        DestinationBox.IsEnabled = sessionCurrent && actionIdle;
        StartExtractionButton.IsEnabled = sessionCurrent
            && actionIdle
            && DestinationBox.SelectedItem is NoteDerivedDestinationDto;
        RefreshExtractionButton.IsEnabled = sessionCurrent && actionIdle;
        SaveEditButton.IsEnabled = canWrite;
        ConfirmButton.IsEnabled = canWrite;
        DismissButton.IsEnabled = canWrite;
        VerifyPendingButton.IsEnabled = NoteTaskSuggestionUiState.CanVerifyPending(
            sessionCurrent, _pendingConfirmation is not null || _confirmationUnresolved, !actionIdle);
        SuggestionsList.IsEnabled = NoteTaskSuggestionUiState.CanChangeSelection(
            _closed, isBusy: !actionIdle, isLoading: _loadingEditor);
        TitleBox.IsEnabled = !_loadingEditor && actionIdle
            && _selectedSuggestion is { Status: NoteTaskSuggestionStatusDto.Draft }
            && _pendingConfirmation is null
            && !_confirmationUnresolved;
        DescriptionBox.IsEnabled = TitleBox.IsEnabled;
        PriorityBox.IsEnabled = TitleBox.IsEnabled;
        DueDatePicker.IsEnabled = TitleBox.IsEnabled;
        TaskBookBox.IsEnabled = TitleBox.IsEnabled;
        CalendarBox.IsEnabled = TitleBox.IsEnabled;
        if (!sessionCurrent && !_sessionInvalid)
            InvalidateSession("账号或服务器已切换；服务器写入已禁用，本机草稿仍可保存。" );
    }

    private bool IsSessionCurrent()
        => !_closed
            && !_sessionInvalid
            && _uiState.IsCurrent(_loadFence, _source, _cloud.CurrentAccountScope, _cloud.AccountGeneration)
            && Equals(_operationFence.Account, _cloud.CurrentAccountScope)
            && _operationFence.AccountGeneration == _cloud.AccountGeneration;

    private void OnRefreshSuggestions(object sender, RoutedEventArgs e)
    {
        if (_actionGate.CurrentCount == 0 || _draftSaveInProgress || _loadingEditor || !IsSessionCurrent())
            return;
        _ = RefreshSuggestionsAsync(_selectedSuggestion?.Id);
    }

    private bool IsCurrent(NoteTaskSuggestionSelectionFence ticket)
        => IsSessionCurrent()
            && _selectionFence == ticket
            && _uiState.IsCurrentSelection(
                ticket,
                ticket.SuggestionId,
                _source,
                _cloud.CurrentAccountScope,
                _cloud.AccountGeneration);

    private bool IsCurrentRefresh(
        NoteTaskSuggestionSelectionFence ticket,
        NoteTaskSuggestionOperationFence fence)
        => !_closed
            && _selectionFence == ticket
            && _uiState.IsCurrent(
                ticket.Load, _source, fence.Account, fence.AccountGeneration)
            && _cloud.CurrentAccountScope == fence.Account
            && _cloud.AccountGeneration == fence.AccountGeneration;

    private bool IsCurrentSelection(NoteTaskSuggestionDto suggestion)
        => _selectedSuggestion is not null
            && string.Equals(_selectedSuggestion.Id, suggestion.Id, StringComparison.Ordinal)
            && _selectionFence is { } ticket
            && Guid.TryParse(suggestion.Id, out var suggestionId)
            && ticket.SuggestionId == suggestionId
            && IsCurrent(ticket);

    private void OnExternalScopeChanged()
    {
        _ = Dispatcher.BeginInvoke(() => InvalidateSession(
            "账号凭据或服务器已改变；服务器操作已停用。本机草稿仍保存于原账号目录。"));
    }

    private void InvalidateSession(string message)
    {
        _sessionInvalid = true;
        _pollTimer.Stop();
        _uiState.Invalidate();
        StatusText.Text = message;
        EditorStateText.Text = "会话已失效；云端读取/写入停用。当前文本只允许保存为原账号本机草稿。";
        StartExtractionButton.IsEnabled = false;
        RefreshExtractionButton.IsEnabled = false;
        DestinationBox.IsEnabled = false;
        SaveEditButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;
        DismissButton.IsEnabled = false;
        VerifyPendingButton.IsEnabled = false;
        if (_selectedSuggestion is not null
            && !_pendingConfirmationIsPresent()
            && !_confirmationUnresolved)
        {
            TitleBox.IsEnabled = true;
            DescriptionBox.IsEnabled = true;
            PriorityBox.IsEnabled = true;
            DueDatePicker.IsEnabled = true;
            TaskBookBox.IsEnabled = true;
            CalendarBox.IsEnabled = true;
        }
    }

    private bool _pendingConfirmationIsPresent() => _pendingConfirmation is not null;

    private NoteTaskSuggestionEditDraft BuildDraft()
    {
        var selected = _selectedSuggestion ?? throw new InvalidOperationException("尚未选择任务建议。");
        return new NoteTaskSuggestionEditDraft(
            selected.Revision,
            TitleBox.Text,
            string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text,
            PriorityBox.SelectedItem is int priority ? priority : selected.Priority,
            DueDatePicker.SelectedDate is { } date
                ? new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Local)).ToUniversalTime()
                : null,
            (TaskBookBox.SelectedItem as ChoiceRow)?.Id,
            (CalendarBox.SelectedItem as ChoiceRow)?.Id);
    }

    private static bool DraftDiffers(NoteTaskSuggestionDto suggestion, NoteTaskSuggestionEditDraft draft)
        => suggestion.Title != draft.Title
            || suggestion.Description != draft.Description
            || suggestion.Priority != draft.Priority
            || suggestion.Due?.LocalDateTime.Date != draft.Due?.LocalDateTime.Date
            || suggestion.TaskBookId != draft.TaskBookId
            || suggestion.CalendarId != draft.CalendarId;

    private void SelectChoice(ComboBox box, string? id)
        => box.SelectedItem = (box.ItemsSource as IEnumerable<ChoiceRow>)?.FirstOrDefault(row => row.Id == id);

    private void ReplaceSuggestion(NoteTaskSuggestionDto suggestion)
    {
        var old = _suggestions.FirstOrDefault(row => row.Suggestion.Id == suggestion.Id);
        if (old is null)
            return;
        var index = _suggestions.IndexOf(old);
        _suggestions[index] = new SuggestionRow(suggestion);
        _refreshingSuggestions = true;
        SuggestionsList.SelectedItem = _suggestions[index];
        _refreshingSuggestions = false;
    }

    private void RestoreSuggestionSelection(string suggestionId)
    {
        _refreshingSuggestions = true;
        SuggestionsList.SelectedItem = _suggestions.FirstOrDefault(row => row.Suggestion.Id == suggestionId);
        _refreshingSuggestions = false;
    }

    private void ClearEditor(string text)
    {
        _selectedSuggestion = null;
        _selectionFence = null;
        _pendingConfirmation = null;
        _loadingEditor = true;
        TitleBox.Clear();
        DescriptionBox.Clear();
        PriorityBox.SelectedItem = null;
        DueDatePicker.SelectedDate = null;
        _uiState.ClearDraft();
        _loadingEditor = false;
        EditorStateText.Text = text;
        UpdateActions();
    }

    private void UpdateDestinationDetail()
    {
        if (DestinationBox.SelectedItem is NoteDerivedDestinationDto destination)
        {
            DestinationDetailText.Text = $"{destination.Model} · 修订 {destination.ModelRevision} · 仅在明确确认后调用";
        }
        else
        {
            DestinationDetailText.Text = "没有可用的文本模型；不会改用其他模型或服务。";
        }
        UpdateActions();
    }

    private void OnDestinationChanged(object sender, SelectionChangedEventArgs e) => UpdateDestinationDetail();

    private static bool IsTerminal(NoteTaskSuggestionExtractionStatusDto status)
        => status is NoteTaskSuggestionExtractionStatusDto.Succeeded or NoteTaskSuggestionExtractionStatusDto.Failed;

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;
        if (_actionGate.CurrentCount == 0 || _draftSaveInProgress)
        {
            e.Cancel = true;
            EditorStateText.Text = "服务器操作正在进行；请等待操作完成或显示核实结果后再关闭窗口。";
            return;
        }

        if (!_pendingCloseAcknowledged && (_pendingConfirmation is not null || _confirmationUnresolved)
            && MessageBox.Show(
                this,
                "此窗口仍有未核实的任务确认请求。关闭窗口不会清除冻结的幂等凭据；下次打开后仍需先核实服务器状态。继续关闭？",
                "待核实的任务确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _pendingCloseAcknowledged = _pendingConfirmation is not null || _confirmationUnresolved;

        if (_uiState.IsDraftDirty && _selectedSuggestion is { } selected)
        {
            e.Cancel = true;
            _draftSaveTimer.Stop();
            var selectionTicket = _selectionFence;
            var generation = _uiState.DraftGeneration;
            var draft = BuildDraft();
            _loadingEditor = true;
            UpdateActions();
            try
            {
                await PersistLocalDraftAsync(selected, draft);
                if (_selectionFence == selectionTicket
                    && _selectedSuggestion?.Id == selected.Id
                    && _uiState.TryClearDraft(generation))
                {
                    _allowClose = true;
                    Close();
                    return;
                }

                EditorStateText.Text = "保存期间编辑状态已变化；窗口保持打开，草稿未清除。";
            }
            catch (Exception)
            {
                EditorStateText.Text = "草稿保存失败；窗口保持打开，编辑内容仍在。";
            }
            finally
            {
                if (!_allowClose)
                {
                    _loadingEditor = false;
                    UpdateActions();
                }
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _pollTimer.Stop();
        _draftSaveTimer.Stop();
        _api.ExternalScopeChanged -= OnExternalScopeChanged;
    }

    private sealed record ChoiceRow(string? Id, string Label);

    private sealed record SuggestionRow(NoteTaskSuggestionDto Suggestion)
    {
        public string Label => $"{Suggestion.Ordinal}. {Suggestion.Title}";
        public string Detail => $"优先级 {Suggestion.Priority} · 修订 {Suggestion.Revision[..Math.Min(8, Suggestion.Revision.Length)]}";
    }
}
