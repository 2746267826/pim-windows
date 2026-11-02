using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Pim.Client.App;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Application = System.Windows.Application;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class AppKnowledgeUiStateTests
{
    private static readonly AppKnowledgeAccountScope AccountA =
        new("https://one.example.test/api/v1", "account-a");
    private static readonly AppKnowledgeAccountScope AccountB =
        new("https://two.example.test/api/v1", "account-b");

    [Fact]
    public void LateViewReceiptsAreRejectedAfterASelectionOrAccountChange()
    {
        var category = AppKnowledgeUiState.Capture(AccountA, 3, 10);
        var app = AppKnowledgeUiState.Capture(AccountA, 3, 20);
        var context = AppKnowledgeUiState.Capture(AccountA, 3, 30);

        Assert.True(AppKnowledgeUiState.IsCurrent(category, AccountA, 3, 10));
        Assert.False(AppKnowledgeUiState.IsCurrent(category, AccountA, 3, 11));
        Assert.False(AppKnowledgeUiState.IsCurrent(app, AccountA, 3, 21));
        Assert.False(AppKnowledgeUiState.IsCurrent(context, AccountA, 3, 31));
        Assert.False(AppKnowledgeUiState.IsCurrent(category, AccountB, 4, 10));
        Assert.False(AppKnowledgeUiState.IsCurrent(app, AccountA, 4, 20));
    }

    [Fact]
    public void DirtyDraftStaysFrozenAndCannotBeDiscardedWithoutConfirmation()
    {
        var state = new AppKnowledgeUiState();
        state.MarkDraftDirty(AppKnowledgeDraftKind.Context, AccountA, 7);

        Assert.True(state.IsDraftStale(AccountB, 8));
        Assert.False(state.CanSaveDraft(AccountB, 8));
        Assert.False(state.TryDiscardDraft(confirmed: false));
        Assert.True(state.IsDraftDirty);
        Assert.Equal(AppKnowledgeDraftKind.Context, state.DraftKind);

        Assert.True(state.TryDiscardDraft(confirmed: true));
        Assert.False(state.IsDraftDirty);
        Assert.Null(state.DraftKind);
    }

    [Fact]
    public void BusyOperationsRemainActiveUntilEachOperationEnds()
    {
        var state = new AppKnowledgeUiState();
        var first = state.BeginBusy();
        var second = state.BeginBusy();

        Assert.True(state.IsBusy);
        Assert.True(state.EndBusy(first));
        Assert.True(state.IsBusy);
        Assert.False(state.EndBusy(second));
        Assert.False(state.IsBusy);
    }

    [Fact]
    public void CategoryParentRuleAllowsAncestorAndRejectsSelfOrDescendant()
    {
        var ancestorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var editedId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var descendantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var edited = Category(editedId, [Category(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), [Category(descendantId)])]);

        Assert.False(AppKnowledgeUiState.WouldCreateCategoryCycle(ancestorId, edited));
        Assert.True(AppKnowledgeUiState.WouldCreateCategoryCycle(editedId, edited));
        Assert.True(AppKnowledgeUiState.WouldCreateCategoryCycle(descendantId, edited));
        Assert.False(AppKnowledgeUiState.WouldCreateCategoryCycle(null, edited));
    }

    [Fact]
    public void CreatingChildKeepsSelectedCategoryAsParentButEditingExcludesSelfAndDescendants()
    {
        var ancestor = Category(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var selected = Category(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var descendant = Category(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        var sibling = Category(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));
        selected.Children = [descendant];
        var loaded = new[] { ancestor, selected, descendant, sibling };

        var createParents = AppKnowledgeWindow.FilterCategoryParentCandidates(loaded, editingCategory: null);
        Assert.Contains(selected, createParents);

        var editParents = AppKnowledgeWindow.FilterCategoryParentCandidates(loaded, selected);
        Assert.Contains(ancestor, editParents);
        Assert.Contains(sibling, editParents);
        Assert.DoesNotContain(selected, editParents);
        Assert.DoesNotContain(descendant, editParents);
    }

    [Fact]
    public void BuiltinCategoryIsReadonlyThenNewCategoryIsEditableWithBuiltinParentWithoutNetwork()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Pim.Client.App;component/Styles/Theme.xaml"),
                });

                var handler = new NoNetworkHandler();
                var api = new ApiClient(() => handler);
                var knowledge = new AppKnowledgeService(api, () => null);
                using var provider = new ServiceCollection()
                    .AddSingleton(api)
                    .AddSingleton(knowledge)
                    .BuildServiceProvider();
                var servicesProperty = typeof(App).GetProperty(nameof(App.Services), BindingFlags.Public | BindingFlags.Static)!;
                var previousServices = servicesProperty.GetValue(null);
                servicesProperty.SetValue(null, provider);

                try
                {
                    var window = new AppKnowledgeWindow();
                    var builtin = Category(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));
                    builtin.IsBuiltin = true;
                    var rowType = typeof(AppKnowledgeWindow).GetNestedType("CategoryRow", BindingFlags.NonPublic)!;
                    var fromNode = rowType.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!;
                    var row = fromNode.Invoke(null, [builtin])!;
                    var tree = Assert.IsType<TreeView>(window.FindName("CategoryTree"));
                    tree.ItemsSource = new[] { row };

                    typeof(AppKnowledgeWindow).GetMethod("OnCategorySelected", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(window, [tree, new RoutedPropertyChangedEventArgs<object>(null!, row)]);

                    Assert.False(Assert.IsType<TextBox>(window.FindName("CategoryNameBox")).IsEnabled);
                    Assert.False(Assert.IsType<TextBox>(window.FindName("CategoryColorBox")).IsEnabled);
                    Assert.False(Assert.IsType<ComboBox>(window.FindName("ProductivityBox")).IsEnabled);
                    Assert.False(Assert.IsType<ComboBox>(window.FindName("ParentCategoryBox")).IsEnabled);

                    typeof(AppKnowledgeWindow).GetMethod("OnNewCategory", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(window, [null, new RoutedEventArgs()]);

                    Assert.True(Assert.IsType<TextBox>(window.FindName("CategoryNameBox")).IsEnabled);
                    Assert.True(Assert.IsType<TextBox>(window.FindName("CategoryColorBox")).IsEnabled);
                    Assert.True(Assert.IsType<ComboBox>(window.FindName("ProductivityBox")).IsEnabled);
                    var parentBox = Assert.IsType<ComboBox>(window.FindName("ParentCategoryBox"));
                    Assert.True(parentBox.IsEnabled);
                    Assert.Same(row, parentBox.SelectedItem);
                    Assert.Equal(0, handler.RequestCount);

                    typeof(AppKnowledgeWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(window, true);
                    window.Close();
                }
                finally
                {
                    servicesProperty.SetValue(null, previousServices);
                    knowledge.Dispose();
                    app.Shutdown();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static CategoryTreeNode Category(Guid id, List<CategoryTreeNode>? children = null)
        => new()
        {
            Id = id.ToString("D"),
            Name = id.ToString("N"),
            ParentId = null,
            Color = "#64748b",
            Productivity = "neutral",
            Icon = null,
            SortOrder = 10,
            IsBuiltin = false,
            Children = children ?? [],
        };

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("The UI regression test must not send HTTP requests.");
        }
    }
}
