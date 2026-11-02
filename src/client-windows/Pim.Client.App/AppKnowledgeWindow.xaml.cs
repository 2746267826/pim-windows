using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

public partial class AppKnowledgeWindow : Window
{
    private static readonly Choice[] ProductivityChoices =
    [
        new("productive", "高效"),
        new("neutral", "中性"),
        new("distracting", "分心"),
    ];

    private static readonly Choice[] PatternChoices =
    [
        new("app-default", "应用默认"),
        new("domain", "域名"),
        new("title", "窗口标题"),
        new("url-path", "网址路径"),
        new("source-family", "来源类型"),
    ];

    private readonly AppKnowledgeService _knowledge;
    private readonly ApiClient _api;
    private AppKnowledgeUiFence? _categoryLoadedFence;
    private AppKnowledgeUiFence? _appsLoadedFence;
    private CategoryTreeNode? _selectedCategory;
    private CategoryTreeNode? _editCategory;
    private AppKnowledgeAppDto? _selectedApp;
    private AppKnowledgeContextDto? _selectedContext;
    private long _categoryViewGeneration;
    private long _appViewGeneration;
    private long _contextViewGeneration;
    private bool _staleDraftFrozen;
    private bool _loading;
    private bool _closed;
    private bool _allowClose;

    public AppKnowledgeWindow()
    {
        InitializeComponent();
        _knowledge = App.Services.GetRequiredService<AppKnowledgeService>();
        _api = App.Services.GetRequiredService<ApiClient>();
        _api.ExternalScopeChanged += OnExternalScopeChanged;
        ProductivityBox.ItemsSource = ProductivityChoices;
        PatternTypeBox.ItemsSource = PatternChoices;
        ProductivityBox.SelectedIndex = 1;
        PatternTypeBox.SelectedIndex = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshCategoriesAsync();
        await SearchAppsAsync();
    }

    private async void OnRefreshCategories(object sender, RoutedEventArgs e)
        => await RefreshCategoriesAsync();

    private async Task RefreshCategoriesAsync()
    {
        if (!PrepareDraftTransition("刷新分类树"))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _categoryViewGeneration);
        var fence = TryCapture(generation);
        if (fence is null)
        {
            ClearCategoryList("请先登录当前 PIM 账户后读取分类。");
            return;
        }

        StateText.Text = "正在读取分类树…";
        try
        {
            var tree = await _knowledge.GetCategoryTreeAsync();
            if (!IsCategoryCurrent(fence))
            {
                return;
            }

            var rows = tree.Select(CategoryRow.From).ToArray();
            CategoryTree.ItemsSource = rows;
            _categoryLoadedFence = fence;
            _selectedCategory = null;
            _editCategory = null;
            ResetCategoryEditor();
            StateText.Text = rows.Length == 0 ? "分类树为空。" : $"已读取 {CountCategories(tree)} 个分类。";
        }
        catch (Exception ex)
        {
            if (IsCategoryCurrent(fence))
            {
                ClearCategoryList("读取分类树失败；未修改服务器数据。" + ScopeSuffix(fence));
                ShowFailure("读取分类树失败", ex);
            }
        }
    }

    private void OnCategorySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loading || e.NewValue is not CategoryRow row)
        {
            return;
        }

        if (!PrepareDraftTransition("切换分类"))
        {
            RestoreCategorySelection(_selectedCategory?.Id);
            return;
        }

        _selectedCategory = row.Node;
        _editCategory = row.Node;
        LoadCategoryEditor(row.Node);
    }

    private void OnNewCategory(object sender, RoutedEventArgs e)
    {
        if (!PrepareDraftTransition("新建分类"))
        {
            return;
        }

        _editCategory = null;
        _loading = true;
        CategoryNameBox.Clear();
        CategoryColorBox.Text = "#64748b";
        ProductivityBox.SelectedValue = "neutral";
        var parentRows = FilterCategoryParentCandidates(GetLoadedCategories(), editingCategory: null)
            .Select(node => FindRow(node.Id))
            .OfType<CategoryRow>()
            .ToArray();
        ParentCategoryBox.ItemsSource = parentRows;
        ParentCategoryBox.SelectedItem = parentRows.FirstOrDefault(row => row.Node.Id == _selectedCategory?.Id);
        BuiltinCategoryNotice.Text = "正在新建自定义分类。";
        CategoryNameBox.IsEnabled = true;
        CategoryColorBox.IsEnabled = true;
        ProductivityBox.IsEnabled = true;
        ParentCategoryBox.IsEnabled = true;
        SaveCategoryButton.IsEnabled = true;
        DeleteCategoryButton.IsEnabled = false;
        _loading = false;
        MarkDraftDirty(AppKnowledgeDraftKind.Category);
    }

    private async void OnSaveCategory(object sender, RoutedEventArgs e)
    {
        if (!CanWriteCurrentDraft(_categoryLoadedFence))
        {
            StateText.Text = "账号或服务器已变化；草稿仍保留在窗口中，未发送保存请求。";
            return;
        }

        var name = CategoryNameBox.Text.Trim();
        var color = CategoryColorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(color)
            || ProductivityBox.SelectedItem is not Choice productivity)
        {
            StateText.Text = "请填写分类名称、颜色并选择生产力分类。";
            return;
        }

        if (_editCategory?.IsBuiltin == true)
        {
            StateText.Text = "内置分类不可编辑。";
            return;
        }

        var parent = ParentCategoryBox.SelectedItem as CategoryRow;
        if (_editCategory is { } editedCategory && parent is not null
            && (!Guid.TryParse(parent.Node.Id, out var proposedParentId)
                || AppKnowledgeUiState.WouldCreateCategoryCycle(proposedParentId, editedCategory)))
        {
            StateText.Text = "不能将分类移动到自身或其子分类下。";
            return;
        }

        var generation = Interlocked.Increment(ref _categoryViewGeneration);
        var fence = TryCapture(generation, _categoryLoadedFence?.Account);
        if (fence is null)
        {
            StateText.Text = "账号或服务器已变化；未发送保存请求。";
            return;
        }

        var busyOperation = BeginBusy("正在保存分类…");
        try
        {
            await _knowledge.SaveCategoryAsync(new CategorySaveRequest
            {
                Id = _editCategory?.Id,
                ParentId = parent?.Node.Id,
                Name = name,
                Color = color,
                Icon = _editCategory?.Icon,
                Productivity = productivity.Value,
                SortOrder = _editCategory?.SortOrder ?? (GetLoadedCategories().Count == 0
                    ? 10
                    : GetLoadedCategories().Max(node => node.SortOrder) + 10),
            });
            if (!IsCategoryCurrent(fence))
            {
                return;
            }

            _uiState.MarkDraftSaved();
            StateText.Text = "分类已保存；正在刷新分类树…";
            await RefreshCategoriesAsync();
        }
        catch (Exception ex)
        {
            if (IsCategoryCurrent(fence))
            {
                StateText.Text = "保存分类失败；输入草稿仍保留。";
                ShowFailure("保存分类失败", ex);
            }
        }
        finally
        {
            EndBusy(busyOperation);
        }
    }

    private async void OnDeleteCategory(object sender, RoutedEventArgs e)
    {
        if (_editCategory is not { } category || !Guid.TryParse(category.Id, out var id))
        {
            StateText.Text = "请选择一个自定义分类。";
            return;
        }

        if (category.IsBuiltin)
        {
            StateText.Text = "内置分类不可删除。";
            return;
        }

        if (MessageBox.Show(this, $"确定删除分类“{category.Name}”？服务器仍可能因子分类或引用拒绝删除。",
                "删除分类", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_uiState.IsDraftDirty && !AskDiscardDraft("删除分类"))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _categoryViewGeneration);
        var fence = TryCapture(generation);
        if (fence is null || !CanMutateLoadedData(_categoryLoadedFence, fence))
        {
            StateText.Text = "分类列表已过期或账号已切换；未发送删除请求。";
            return;
        }

        var busyOperation = BeginBusy("正在删除分类…");
        try
        {
            await _knowledge.DeleteCategoryAsync(id);
            if (!IsCategoryCurrent(fence))
            {
                return;
            }

            _uiState.MarkDraftSaved();
            StateText.Text = "分类已删除，正在刷新…";
            await RefreshCategoriesAsync();
        }
        catch (Exception ex)
        {
            if (IsCategoryCurrent(fence))
            {
                StateText.Text = "删除分类失败；服务器数据未在本地乐观移除。";
                ShowFailure("删除分类失败", ex);
            }
        }
        finally
        {
            EndBusy(busyOperation);
        }
    }

    private async void OnSearchApps(object sender, RoutedEventArgs e)
        => await SearchAppsAsync();

    private async void OnAppSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAppsAsync();
        }
    }

    private async Task SearchAppsAsync()
    {
        if (!PrepareDraftTransition("搜索应用"))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _appViewGeneration);
        Interlocked.Increment(ref _contextViewGeneration);
        _selectedApp = null;
        _selectedContext = null;
        ContextsList.ItemsSource = null;
        SelectedAppText.Text = "正在搜索应用…";
        var fence = TryCapture(generation);
        if (fence is null)
        {
            AppsList.ItemsSource = null;
            _selectedApp = null;
            ClearContexts("请先登录当前 PIM 账户后搜索应用。");
            return;
        }

        StateText.Text = "正在搜索账户内应用…";
        try
        {
            var apps = await _knowledge.SearchAppsAsync(AppSearchBox.Text.Trim());
            if (!IsAppsCurrent(fence))
            {
                return;
            }

            var rows = apps.Select(AppRow.From).ToArray();
            AppsList.ItemsSource = rows;
            _appsLoadedFence = fence;
            _selectedApp = null;
            _selectedContext = null;
            ClearContexts(rows.Length == 0 ? "没有匹配的应用；可调整关键词后重试。" : "选择应用读取上下文规则。");
            StateText.Text = $"找到 {rows.Length} 个应用；列表仅来自当前账户。";
        }
        catch (Exception ex)
        {
            if (IsAppsCurrent(fence))
            {
                AppsList.ItemsSource = null;
                ClearContexts("应用搜索失败。" + ScopeSuffix(fence));
                ShowFailure("搜索应用失败", ex);
            }
        }
    }

    private async void OnAppSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppsList.SelectedItem is not AppRow row)
        {
            return;
        }

        if (!PrepareDraftTransition("切换应用"))
        {
            _loading = true;
            AppsList.SelectedItem = (AppsList.ItemsSource as IEnumerable<AppRow>)?
                .FirstOrDefault(item => item.App.Id == _selectedApp?.Id);
            _loading = false;
            return;
        }

        _selectedApp = row.App;
        SelectedAppText.Text = $"{row.App.DisplayName}\n{row.App.ProcessName}";
        ResetContextEditor();
        await RefreshContextsAsync();
    }

    private async void OnRefreshContexts(object sender, RoutedEventArgs e)
        => await RefreshContextsAsync();

    private async Task RefreshContextsAsync()
    {
        if (_selectedApp is not { } app || !Guid.TryParse(app.Id, out var appId))
        {
            ClearContexts("请先选择一个有效应用。" );
            return;
        }

        if (!PrepareDraftTransition("刷新上下文规则"))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _contextViewGeneration);
        var fence = TryCapture(generation);
        if (fence is null || !CanMutateLoadedData(_appsLoadedFence, fence))
        {
            ClearContexts("当前应用列表已过期或账号已切换；请重新搜索。" );
            return;
        }

        StateText.Text = "正在读取应用上下文…";
        try
        {
            var contexts = await _knowledge.GetContextsAsync(appId);
            if (!IsContextsCurrent(fence) || !ReferenceEquals(_selectedApp, app))
            {
                return;
            }

            ContextsList.ItemsSource = contexts.Select(ContextRow.From).ToArray();
            StateText.Text = $"已读取 {contexts.Count} 条规则。";
        }
        catch (Exception ex)
        {
            if (IsContextsCurrent(fence) && ReferenceEquals(_selectedApp, app))
            {
                ContextsList.ItemsSource = null;
                StateText.Text = "读取上下文失败；未修改服务器数据。" + ScopeSuffix(fence);
                ShowFailure("读取应用上下文失败", ex);
            }
        }
    }

    private void OnContextSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ContextsList.SelectedItem is not ContextRow row)
        {
            return;
        }

        if (!PrepareDraftTransition("切换上下文规则"))
        {
            _loading = true;
            ContextsList.SelectedItem = (ContextsList.ItemsSource as IEnumerable<ContextRow>)?
                .FirstOrDefault(item => item.Context.Id == _selectedContext?.Id);
            _loading = false;
            return;
        }

        _selectedContext = row.Context;
        LoadContextEditor(row.Context);
    }

    private void OnNewContext(object sender, RoutedEventArgs e)
    {
        if (_selectedApp is null)
        {
            StateText.Text = "请先搜索并选择应用。";
            return;
        }

        if (!PrepareDraftTransition("新建上下文规则"))
        {
            return;
        }

        _selectedContext = null;
        ResetContextEditor();
        PatternTypeBox.SelectedValue = "app-default";
        ContextEnabledBox.IsChecked = true;
        MarkDraftDirty(AppKnowledgeDraftKind.Context);
    }

    private async void OnSaveContext(object sender, RoutedEventArgs e)
    {
        if (_selectedApp is not { } app || !Guid.TryParse(app.Id, out var appId)
            || !Guid.TryParse(app.Id, out _))
        {
            StateText.Text = "应用标识无效；无法保存规则。";
            return;
        }

        if (!CanWriteCurrentDraft(_appsLoadedFence))
        {
            StateText.Text = "账号、服务器或已载入应用列表已变化；草稿仍保留，未发送保存请求。";
            return;
        }

        if (PatternTypeBox.SelectedItem is not Choice pattern || string.IsNullOrWhiteSpace(PatternValueBox.Text))
        {
            StateText.Text = "请选择匹配类型并填写匹配内容。";
            return;
        }

        var generation = Interlocked.Increment(ref _contextViewGeneration);
        var fence = TryCapture(generation);
        if (fence is null || !CanMutateLoadedData(_appsLoadedFence, fence))
        {
            StateText.Text = "应用列表已过期或账号已切换；未发送保存请求。";
            return;
        }

        var busyOperation = BeginBusy("正在保存上下文规则…");
        try
        {
            await _knowledge.SaveContextAsync(new SaveAppKnowledgeContextRequest
            {
                AppId = appId.ToString("D"),
                ProcessName = app.ProcessName,
                PatternType = pattern.Value,
                PatternValue = PatternValueBox.Text.Trim(),
                TargetCategoryName = NullIfBlank(TargetCategoryBox.Text),
                ProjectTag = NullIfBlank(ProjectTagBox.Text),
                Confidence = 1,
                Enabled = ContextEnabledBox.IsChecked == true,
            });
            if (!IsContextsCurrent(fence) || !ReferenceEquals(_selectedApp, app))
            {
                return;
            }

            _uiState.MarkDraftSaved();
            StateText.Text = "上下文规则已保存；正在刷新列表…";
            await RefreshContextsAsync();
        }
        catch (Exception ex)
        {
            if (IsContextsCurrent(fence) && ReferenceEquals(_selectedApp, app))
            {
                StateText.Text = "保存规则失败；输入草稿仍保留。";
                ShowFailure("保存上下文规则失败", ex);
            }
        }
        finally
        {
            EndBusy(busyOperation);
        }
    }

    private async void OnDeleteContext(object sender, RoutedEventArgs e)
    {
        if (_selectedContext is not { } context || !Guid.TryParse(context.Id, out var id))
        {
            StateText.Text = "请选择一条上下文规则。";
            return;
        }

        if (MessageBox.Show(this, $"确定删除规则“{context.ScopeSummary}”？", "删除上下文规则",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (_uiState.IsDraftDirty && !AskDiscardDraft("删除上下文规则"))
        {
            return;
        }

        var app = _selectedApp;
        var generation = Interlocked.Increment(ref _contextViewGeneration);
        var fence = TryCapture(generation);
        if (fence is null || !CanMutateLoadedData(_appsLoadedFence, fence))
        {
            StateText.Text = "应用列表已过期或账号已切换；未发送删除请求。";
            return;
        }

        var busyOperation = BeginBusy("正在删除上下文规则…");
        try
        {
            await _knowledge.DeleteContextAsync(id);
            if (!IsContextsCurrent(fence) || !ReferenceEquals(_selectedApp, app))
            {
                return;
            }

            _selectedContext = null;
            _uiState.MarkDraftSaved();
            ResetContextEditor();
            StateText.Text = "规则已删除；正在刷新列表…";
            await RefreshContextsAsync();
        }
        catch (Exception ex)
        {
            if (IsContextsCurrent(fence) && ReferenceEquals(_selectedApp, app))
            {
                StateText.Text = "删除规则失败；列表保持原样。";
                ShowFailure("删除上下文规则失败", ex);
            }
        }
        finally
        {
            EndBusy(busyOperation);
        }
    }

    private void OnCategoryDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && !_uiState.IsDraftStale(_knowledge.CurrentAccount, _knowledge.Generation))
        {
            MarkDraftDirty(AppKnowledgeDraftKind.Category);
        }
    }

    private void OnCategoryDraftSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && !_uiState.IsDraftStale(_knowledge.CurrentAccount, _knowledge.Generation))
        {
            MarkDraftDirty(AppKnowledgeDraftKind.Category);
        }
    }

    private void OnContextDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && !_uiState.IsDraftStale(_knowledge.CurrentAccount, _knowledge.Generation))
        {
            MarkDraftDirty(AppKnowledgeDraftKind.Context);
        }
    }

    private void OnContextDraftSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && !_uiState.IsDraftStale(_knowledge.CurrentAccount, _knowledge.Generation))
        {
            MarkDraftDirty(AppKnowledgeDraftKind.Context);
        }
    }

    private void OnContextDraftCheckChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading && !_uiState.IsDraftStale(_knowledge.CurrentAccount, _knowledge.Generation))
        {
            MarkDraftDirty(AppKnowledgeDraftKind.Context);
        }
    }

    private void LoadCategoryEditor(CategoryTreeNode category)
    {
        _loading = true;
        CategoryNameBox.Text = category.Name;
        CategoryColorBox.Text = category.Color;
        ProductivityBox.SelectedValue = category.Productivity;
        var rows = FilterCategoryParentCandidates(GetLoadedCategories(), category)
            .Select(node => FindRow(node.Id))
            .OfType<CategoryRow>()
            .ToArray();
        ParentCategoryBox.ItemsSource = rows;
        ParentCategoryBox.SelectedItem = rows.FirstOrDefault(row => row.Node.Id == category.ParentId);
        BuiltinCategoryNotice.Text = category.IsBuiltin
            ? "内置分类：只读，不允许编辑或删除。"
            : $"自定义分类 · {category.Id}";
        CategoryNameBox.IsEnabled = !category.IsBuiltin;
        CategoryColorBox.IsEnabled = !category.IsBuiltin;
        ProductivityBox.IsEnabled = !category.IsBuiltin;
        ParentCategoryBox.IsEnabled = !category.IsBuiltin;
        SaveCategoryButton.IsEnabled = !category.IsBuiltin;
        DeleteCategoryButton.IsEnabled = !category.IsBuiltin;
        _loading = false;
    }

    private void LoadContextEditor(AppKnowledgeContextDto context)
    {
        _loading = true;
        PatternTypeBox.SelectedValue = context.PatternType;
        PatternValueBox.Text = context.PatternValue;
        TargetCategoryBox.Text = context.TargetCategoryName ?? string.Empty;
        ProjectTagBox.Text = context.ProjectTag ?? string.Empty;
        ContextEnabledBox.IsChecked = context.Enabled;
        PatternTypeBox.IsEnabled = false;
        PatternValueBox.IsEnabled = false;
        TargetCategoryBox.IsEnabled = true;
        ProjectTagBox.IsEnabled = true;
        ContextEnabledBox.IsEnabled = true;
        SaveContextButton.IsEnabled = true;
        DeleteContextButton.IsEnabled = true;
        _loading = false;
        _uiState.MarkDraftSaved();
    }

    private void ResetCategoryEditor()
    {
        _loading = true;
        CategoryNameBox.Clear();
        CategoryColorBox.Text = "#64748b";
        ProductivityBox.SelectedValue = "neutral";
        ParentCategoryBox.ItemsSource = Flatten(GetLoadedCategories());
        ParentCategoryBox.SelectedItem = null;
        BuiltinCategoryNotice.Text = "选择现有分类编辑，或新建自定义分类。";
        CategoryNameBox.IsEnabled = true;
        CategoryColorBox.IsEnabled = true;
        ProductivityBox.IsEnabled = true;
        ParentCategoryBox.IsEnabled = true;
        SaveCategoryButton.IsEnabled = false;
        DeleteCategoryButton.IsEnabled = false;
        _loading = false;
    }

    private void ResetContextEditor()
    {
        _loading = true;
        PatternTypeBox.SelectedValue = "app-default";
        PatternValueBox.Clear();
        TargetCategoryBox.Clear();
        ProjectTagBox.Clear();
        ContextEnabledBox.IsChecked = true;
        PatternTypeBox.IsEnabled = true;
        PatternValueBox.IsEnabled = true;
        TargetCategoryBox.IsEnabled = true;
        ProjectTagBox.IsEnabled = true;
        ContextEnabledBox.IsEnabled = true;
        SaveContextButton.IsEnabled = _selectedApp is not null;
        DeleteContextButton.IsEnabled = false;
        _loading = false;
    }

    private void MarkDraftDirty(AppKnowledgeDraftKind kind)
    {
        _uiState.MarkDraftDirty(kind, _knowledge.CurrentAccount, _knowledge.Generation);
        if (StateText is not null)
        {
            StateText.Text = "有未保存草稿；切换条目、刷新或关闭时将先确认。";
        }
    }

    private bool PrepareDraftTransition(string action)
    {
        if (!_uiState.IsDraftDirty)
        {
            return true;
        }

        if (!AskDiscardDraft(action))
        {
            return false;
        }

        DiscardDraft();
        return true;
    }

    private bool AskDiscardDraft(string action)
        => MessageBox.Show(this, $"当前有未保存草稿。{action}会丢弃窗口中的草稿，是否继续？",
            "未保存的应用知识草稿", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private bool CanWriteCurrentDraft(AppKnowledgeUiFence? loadedFence)
        => !_staleDraftFrozen
            && IsLoadedScopeCurrent(loadedFence)
            && _uiState.CanSaveDraft(_knowledge.CurrentAccount, _knowledge.Generation);

    private bool CanMutateLoadedData(AppKnowledgeUiFence? loadedFence, AppKnowledgeUiFence currentFence)
        => IsLoadedScopeCurrent(loadedFence)
            && loadedFence!.Account == currentFence.Account
            && loadedFence.ServiceGeneration == currentFence.ServiceGeneration;

    private bool IsLoadedScopeCurrent(AppKnowledgeUiFence? loadedFence)
        => loadedFence is not null
            && loadedFence.Account == _knowledge.CurrentAccount
            && loadedFence.ServiceGeneration == _knowledge.Generation;

    private AppKnowledgeUiFence? TryCapture(long viewGeneration, AppKnowledgeAccountScope? expected = null)
    {
        var account = _knowledge.CurrentAccount;
        if (account is null || (expected is not null && account != expected))
        {
            return null;
        }

        return AppKnowledgeUiState.Capture(account, _knowledge.Generation, viewGeneration);
    }

    private bool IsCurrent(AppKnowledgeUiFence? fence, long viewGeneration)
        => !_closed && fence is not null
            && AppKnowledgeUiState.IsCurrent(fence, _knowledge.CurrentAccount, _knowledge.Generation, viewGeneration);

    private bool IsCategoryCurrent(AppKnowledgeUiFence? fence)
        => IsCurrent(fence, Interlocked.Read(ref _categoryViewGeneration));

    private bool IsAppsCurrent(AppKnowledgeUiFence? fence)
        => IsCurrent(fence, Interlocked.Read(ref _appViewGeneration));

    private bool IsContextsCurrent(AppKnowledgeUiFence? fence)
        => IsCurrent(fence, Interlocked.Read(ref _contextViewGeneration));

    private void OnExternalScopeChanged()
    {
        Interlocked.Increment(ref _categoryViewGeneration);
        Interlocked.Increment(ref _appViewGeneration);
        Interlocked.Increment(ref _contextViewGeneration);
        if (_closed)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_closed)
            {
                return;
            }

            _categoryLoadedFence = null;
            _appsLoadedFence = null;
            var staleKind = _uiState.IsDraftStale(_knowledge.CurrentAccount, _knowledge.Generation)
                ? _uiState.DraftKind
                : null;
            _staleDraftFrozen = staleKind is not null;
            CategoryTree.ItemsSource = null;
            AppsList.ItemsSource = null;
            ContextsList.ItemsSource = null;
            if (staleKind != AppKnowledgeDraftKind.Category)
            {
                _selectedCategory = null;
                _editCategory = null;
                ResetCategoryEditor();
            }
            if (staleKind != AppKnowledgeDraftKind.Context)
            {
                _selectedApp = null;
                _selectedContext = null;
                ResetContextEditor();
                SelectedAppText.Text = "账号或服务器已变化；请重新搜索当前账户数据。";
            }

            DiscardStaleDraftButton.Visibility = _staleDraftFrozen ? Visibility.Visible : Visibility.Collapsed;
            SetDraftEditorsEnabled(!_staleDraftFrozen);
            StateText.Text = _staleDraftFrozen
                ? "账号或服务器已变化；旧会话草稿保留在编辑器中并已冻结。确认丢弃后才能继续编辑。"
                : "账号或服务器已变化；请刷新当前账户数据。";
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_uiState.IsBusy)
        {
            e.Cancel = true;
            StateText.Text = "正在提交服务器操作；请等待操作完成后再关闭窗口。";
            return;
        }

        if (_allowClose || !_uiState.IsDraftDirty)
        {
            return;
        }

        if (!AskDiscardDraft("关闭窗口"))
        {
            e.Cancel = true;
            return;
        }

        DiscardDraft();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        Interlocked.Increment(ref _categoryViewGeneration);
        Interlocked.Increment(ref _appViewGeneration);
        Interlocked.Increment(ref _contextViewGeneration);
        _api.ExternalScopeChanged -= OnExternalScopeChanged;
    }

    private void ClearCategoryList(string state)
    {
        CategoryTree.ItemsSource = null;
        _categoryLoadedFence = null;
        _selectedCategory = null;
        _editCategory = null;
        ResetCategoryEditor();
        StateText.Text = state;
    }

    private void ClearContexts(string state)
    {
        ContextsList.ItemsSource = null;
        _selectedContext = null;
        SelectedAppText.Text = state;
    }

    private long BeginBusy(string state)
    {
        var id = _uiState.BeginBusy();
        KnowledgeTabs.IsEnabled = false;
        DiscardStaleDraftButton.IsEnabled = false;
        StateText.Text = state;
        return id;
    }

    private void EndBusy(long id)
    {
        if (!_uiState.EndBusy(id))
        {
            KnowledgeTabs.IsEnabled = !_staleDraftFrozen;
            DiscardStaleDraftButton.IsEnabled = true;
        }
    }

    private void SetDraftEditorsEnabled(bool enabled)
    {
        CategoryNameBox.IsEnabled = enabled && _editCategory?.IsBuiltin != true;
        CategoryColorBox.IsEnabled = enabled && _editCategory?.IsBuiltin != true;
        ProductivityBox.IsEnabled = enabled && _editCategory?.IsBuiltin != true;
        ParentCategoryBox.IsEnabled = enabled && _editCategory?.IsBuiltin != true;
        SaveCategoryButton.IsEnabled = enabled && _categoryLoadedFence is not null
            && _editCategory?.IsBuiltin != true;
        DeleteCategoryButton.IsEnabled = enabled && _categoryLoadedFence is not null
            && _editCategory is { IsBuiltin: false };
        PatternTypeBox.IsEnabled = enabled && _selectedContext is null;
        PatternValueBox.IsEnabled = enabled && _selectedContext is null;
        TargetCategoryBox.IsEnabled = enabled;
        ProjectTagBox.IsEnabled = enabled;
        ContextEnabledBox.IsEnabled = enabled;
        SaveContextButton.IsEnabled = enabled && _selectedApp is not null;
        DeleteContextButton.IsEnabled = enabled && _selectedContext is not null;
    }

    private void OnDiscardStaleDraft(object sender, RoutedEventArgs e)
    {
        if (!_staleDraftFrozen || !AskDiscardDraft("确认丢弃旧会话草稿"))
        {
            return;
        }

        DiscardDraft();
        StateText.Text = "旧会话草稿已按确认丢弃；刷新当前账户数据后可继续操作。";
    }

    private void DiscardDraft()
    {
        var kind = _uiState.DraftKind;
        _uiState.TryDiscardDraft(confirmed: true);
        _staleDraftFrozen = false;
        DiscardStaleDraftButton.Visibility = Visibility.Collapsed;
        if (kind == AppKnowledgeDraftKind.Category)
        {
            _selectedCategory = null;
            _editCategory = null;
            ResetCategoryEditor();
        }
        else if (kind == AppKnowledgeDraftKind.Context)
        {
            _selectedContext = null;
            ResetContextEditor();
        }
        SetDraftEditorsEnabled(enabled: true);
    }

    private void RestoreCategorySelection(string? id)
    {
        if (!Guid.TryParse(id, out var target))
        {
            return;
        }

        _loading = true;
        var row = FlattenRows(CategoryTree.ItemsSource as IEnumerable<CategoryRow>)
            .FirstOrDefault(item => Guid.TryParse(item.Node.Id, out var rowId) && rowId == target);
        if (row is not null)
        {
            var container = FindTreeContainer(CategoryTree, row);
            if (container is not null)
            {
                container.IsSelected = true;
            }
        }
        _loading = false;
    }

    private static TreeViewItem? FindTreeContainer(ItemsControl parent, CategoryRow target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
            {
                continue;
            }

            if (ReferenceEquals(item, target))
            {
                return container;
            }

            var nested = FindTreeContainer(container, target);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private CategoryRow? FindRow(string? id)
        => Guid.TryParse(id, out var target)
            ? FlattenRows(CategoryTree.ItemsSource as IEnumerable<CategoryRow>)
                .FirstOrDefault(item => Guid.TryParse(item.Node.Id, out var rowId) && rowId == target)
            : null;

    private CategoryRow? FindRow(Guid? id)
        => id is null ? null : FindRow(id.Value.ToString("D"));

    private List<CategoryTreeNode> GetLoadedCategories()
        => FlattenNodes(CategoryTree.ItemsSource as IEnumerable<CategoryRow>).ToList();

    internal static IReadOnlyList<CategoryTreeNode> FilterCategoryParentCandidates(
        IEnumerable<CategoryTreeNode> categories,
        CategoryTreeNode? editingCategory)
    {
        var loaded = categories.ToArray();
        if (editingCategory is null)
        {
            return loaded;
        }

        return loaded.Where(node =>
                Guid.TryParse(node.Id, out var parentId)
                && !AppKnowledgeUiState.WouldCreateCategoryCycle(parentId, editingCategory))
            .ToArray();
    }

    private static IReadOnlyList<CategoryRow> Flatten(IEnumerable<CategoryTreeNode> nodes)
        => nodes.Select(CategoryRow.From).ToArray();

    private static IEnumerable<CategoryRow> Flatten(IEnumerable<CategoryRow> rows)
        => rows.SelectMany(row => new[] { row }.Concat(Flatten(row.Children)));

    private static IEnumerable<CategoryRow> FlattenRows(IEnumerable<CategoryRow>? rows)
        => rows is null ? [] : Flatten(rows);

    private static IEnumerable<CategoryTreeNode> FlattenNodes(IEnumerable<CategoryRow>? rows)
        => rows is null ? [] : rows.SelectMany(row => new[] { row.Node }.Concat(FlattenNodes(row.Children)));

    private static int CountCategories(IEnumerable<CategoryTreeNode> nodes)
        => nodes.Sum(node => 1 + CountCategories(node.Children ?? []));

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string ScopeSuffix(AppKnowledgeUiFence fence)
        => $"\n作用域：{fence.Account.ServerUrl} · {fence.Account.OwnerId}";

    private void ShowFailure(string title, Exception exception)
    {
        var message = exception is HttpRequestException
            ? "无法连接服务器。请检查网络、服务器地址与登录状态。"
            : exception.Message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed record Choice(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class CategoryRow(CategoryTreeNode node)
    {
        public CategoryTreeNode Node { get; } = node;
        public string Label => $"{(Node.IsBuiltin ? "[内置] " : string.Empty)}{Node.Name}";
        public IReadOnlyList<CategoryRow> Children { get; } = (node.Children ?? []).Select(From).ToArray();
        public static CategoryRow From(CategoryTreeNode node) => new(node);
    }

    private sealed record AppRow(AppKnowledgeAppDto App)
    {
        public string Label => App.DisplayName;
        public string Detail => $"{App.ProcessName} · {App.ContextCount} 条规则";
        public static AppRow From(AppKnowledgeAppDto app) => new(app);
    }

    private sealed record ContextRow(AppKnowledgeContextDto Context)
    {
        public string Label => $"{Context.PatternType}：{Context.PatternValue}";
        public string Detail => $"{Context.ScopeSummary} · {Context.TargetCategoryName ?? "未指定分类"} · "
            + $"{(Context.Enabled ? "启用" : "停用")} · 匹配 {Context.AffectedRecordCount} 次";
        public static ContextRow From(AppKnowledgeContextDto context) => new(context);
    }

    private readonly AppKnowledgeUiState _uiState = new();
}
