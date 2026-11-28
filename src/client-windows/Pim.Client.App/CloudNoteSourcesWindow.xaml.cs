using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

public partial class CloudNoteSourcesWindow : Window
{
    private readonly CloudNoteMaterialService _cloud;
    private readonly NativeNoteOperationCoordinator? _operations;
    private readonly CloudNoteSourceActionState _actions = new();
    private CloudNoteAccountScope? _loadedScope;
    private long? _loadedAccountGeneration;
    private long _refreshGeneration;
    private bool _closed;

    public CloudNoteSourcesWindow()
    {
        InitializeComponent();
        _cloud = App.Services.GetRequiredService<CloudNoteMaterialService>();
    }

    internal CloudNoteSourcesWindow(NativeNoteOperationCoordinator operations)
        : this()
    {
        _operations = operations;
    }

    public Guid? SelectedNoteId { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await RefreshThroughBarrierAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshThroughBarrierAsync();

    private async Task RefreshThroughBarrierAsync()
    {
        if (!TryBeginAction(
                CloudNoteSourceAction.Refresh,
                "正在读取或恢复服务器笔记，请稍后再刷新目录。"))
        {
            return;
        }

        try
        {
            if (_operations is null)
            {
                await RefreshAsync();
                return;
            }

            var started = await _operations.TryRunExclusiveAsync(RefreshAsync);
            if (!started)
            {
                StatusText.Text = "正在结束并保存本机笔记，请稍后再刷新服务器目录。";
            }
        }
        finally
        {
            CompleteAction(CloudNoteSourceAction.Refresh);
        }
    }

    private async Task RefreshAsync()
    {
        var requestGeneration = ++_refreshGeneration;
        var accountScope = _cloud.CurrentAccountScope;
        var accountGeneration = _cloud.AccountGeneration;
        _loadedScope = null;
        _loadedAccountGeneration = null;
        StatusText.Text = "正在读取服务器目录…";
        if (accountScope is null)
        {
            _loadedScope = null;
            _loadedAccountGeneration = null;
            SourcesList.ItemsSource = null;
            StatusText.Text = "请先登录以读取服务器笔记目录。";
            return;
        }

        try
        {
            var sources = await _cloud.GetMaterialSourcesAsync();
            if (!IsCurrentRequest(requestGeneration, accountScope, accountGeneration))
            {
                return;
            }

            var rows = sources
                .GroupBy(source => source.NoteId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var items = group.OrderByDescending(source => source.CreatedAt).ToArray();
                    var representative = items.FirstOrDefault(source => source.IsCurrent) ?? items[0];
                    return new CloudSourceRow(
                        representative,
                        items.Length,
                        items.Count(source => source.HasConflict),
                        Label(representative, items.Length, items.Count(source => source.HasConflict)));
                })
                .OrderByDescending(row => row.Source.CreatedAt)
                .ToArray();
            _loadedScope = accountScope;
            _loadedAccountGeneration = accountGeneration;
            SourcesList.ItemsSource = rows;
            SourcesList.SelectedItem = rows.FirstOrDefault();
            StatusText.Text = rows.Length == 0
                ? "服务器尚无已完成材料版本。"
                : $"服务器共有 {rows.Length} 篇笔记、{sources.Count} 个已完成材料版本。";
        }
        catch (Exception ex)
        {
            if (!IsCurrentRequest(requestGeneration, accountScope, accountGeneration))
            {
                return;
            }

            _loadedScope = null;
            _loadedAccountGeneration = null;
            SourcesList.ItemsSource = null;
            StatusText.Text = "服务器目录暂不可用；没有修改本机笔记。";
            MessageBox.Show(
                this,
                ex.Message,
                "读取服务器目录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnSourceDoubleClick(object sender, MouseButtonEventArgs e)
        => SelectCurrent();

    private void OnSelect(object sender, RoutedEventArgs e)
        => SelectCurrent();

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_actions.IsBusy)
        {
            StatusText.Text = "正在处理服务器目录操作，请稍后再恢复。";
            return;
        }

        if (!IsLoadedScopeCurrent())
        {
            StatusText.Text = "账号或服务器已切换；请刷新目录后再恢复。";
            return;
        }

        if (SourcesList.SelectedItem is not CloudSourceRow row
            || !Guid.TryParse(row.Source.VersionId, out var versionId))
        {
            StatusText.Text = "请先选择一个有效的服务器材料版本。";
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"下载并恢复“{row.Source.Title}”的服务器固定版本到本机？\n\n"
            + "服务器材料会先下载到临时目录、校验 SHA-256，再创建新的本机笔记；不会覆盖现有笔记。",
            "恢复服务器笔记",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (!TryBeginAction(
                CloudNoteSourceAction.Restore,
                "正在处理服务器目录操作，请稍后再恢复。"))
        {
            return;
        }

        var restored = false;
        try
        {
            if (_operations is null)
            {
                restored = await RestoreAsync(versionId);
            }
            else
            {
                StatusText.Text = "正在等待本机笔记保存完成后恢复…";
                var started = await _operations.TryRunExclusiveAsync(
                    async () => restored = await RestoreAsync(versionId));
                if (!started)
                {
                    StatusText.Text = "正在结束并保存本机笔记，本次恢复未开始。";
                }
            }
        }
        finally
        {
            CompleteAction(CloudNoteSourceAction.Restore);
        }

        if (restored)
        {
            DialogResult = true;
        }
    }

    private async Task<bool> RestoreAsync(Guid versionId)
    {
        try
        {
            StatusText.Text = "正在下载并校验固定材料…";
            var result = await _cloud.RestoreVersionAsync(
                versionId,
                userConfirmedLocalCopy: true,
                expectedAccountScope: _loadedScope,
                expectedAccountGeneration: _loadedAccountGeneration);
            SelectedNoteId = result.Import.NoteId;
            StatusText.Text = "恢复完成；本机副本已创建。";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "恢复未完成；本机笔记库保持不变。";
            MessageBox.Show(
                this,
                ex.Message,
                "恢复服务器笔记失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void SelectCurrent()
    {
        if (_actions.IsBusy)
        {
            StatusText.Text = "正在处理服务器目录操作，请稍后再选择。";
            return;
        }

        if (!IsLoadedScopeCurrent())
        {
            StatusText.Text = "账号或服务器已切换；请刷新目录后再选择。";
            return;
        }

        if (SourcesList.SelectedItem is not CloudSourceRow row)
        {
            return;
        }

        if (!Guid.TryParse(row.Source.NoteId, out var noteId))
        {
            MessageBox.Show(
                this,
                "服务器返回的笔记标识无效；没有切换本机笔记。",
                "服务器目录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TryBeginAction(
                CloudNoteSourceAction.Select,
                "正在处理服务器目录操作，请稍后再选择。"))
        {
            return;
        }

        NativeNoteOperationLease? lease = null;
        var selected = false;
        try
        {
            if (_operations is not null)
            {
                lease = _operations.TryBeginOperation();
                if (lease is null)
                {
                    StatusText.Text = "正在结束并保存本机笔记，本次选择未生效。";
                    return;
                }
            }

            SelectedNoteId = noteId;
            selected = true;
        }
        finally
        {
            lease?.Dispose();
            CompleteAction(CloudNoteSourceAction.Select);
        }

        if (selected)
        {
            DialogResult = true;
        }
    }

    private bool TryBeginAction(CloudNoteSourceAction action, string busyMessage)
    {
        if (_closed || !_actions.TryBegin(action))
        {
            if (!_closed)
            {
                StatusText.Text = busyMessage;
            }

            return false;
        }

        IsEnabled = false;
        return true;
    }

    private void CompleteAction(CloudNoteSourceAction action)
    {
        if (_actions.Complete(action) && !_closed)
        {
            IsEnabled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_actions.IsRestoring)
        {
            return;
        }

        e.Cancel = true;
        StatusText.Text = "正在恢复服务器笔记，完成前请保持窗口打开。";
    }

    private void OnClosed(object? sender, EventArgs e) => _closed = true;

    private bool IsLoadedScopeCurrent()
        => _loadedScope is { } loadedScope
            && _loadedAccountGeneration == _cloud.AccountGeneration
            && Equals(loadedScope, _cloud.CurrentAccountScope);

    private bool IsCurrentRequest(
        long requestGeneration,
        CloudNoteAccountScope accountScope,
        long accountGeneration)
        => requestGeneration == _refreshGeneration
            && accountGeneration == _cloud.AccountGeneration
            && Equals(accountScope, _cloud.CurrentAccountScope);

    private static string Label(NoteAsrSourceDto source, int versions, int conflicts)
    {
        var state = source.IsCurrent ? "[当前]" : conflicts > 0 ? "[有冲突]" : "[历史]";
        return $"{state} {source.Title} · {versions} 个版本 · {source.Recordings} 段录音 · "
            + $"{Duration(source.TotalSamples, source.SampleRate)} · {source.CreatedAt.ToLocalTime():MM-dd HH:mm}";
    }

    private static string Duration(long samples, int sampleRate)
    {
        if (sampleRate <= 0)
        {
            return "00:00:00";
        }

        var seconds = Math.Max(0, samples) / (double)sampleRate;
        if (!double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return "超出范围";
        }

        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
    }

    private sealed record CloudSourceRow(
        NoteAsrSourceDto Source,
        int VersionCount,
        int ConflictCount,
        string Label);
}
