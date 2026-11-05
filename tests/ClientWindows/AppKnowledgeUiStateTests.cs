using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Pim.Client.App;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using Xunit;

namespace Pim.Client.App.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class AppKnowledgeUiStateTests(WpfApplicationFixture wpf)
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
    public async Task ExpiredAccessTokenWithRefreshSessionStillExposesAccountScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "pim-app-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var auth = new AuthService(
                new ApiClient(() => new ExpiredSessionHandler()),
                Path.Combine(root, "token.json"));

            Assert.True(await auth.LoginAsync("user", "password"));
            Assert.False(auth.IsAuthenticated);
            Assert.True(auth.IsAccessTokenRenewalDue);

            var scope = Startup.CurrentAppKnowledgeScope(auth);

            Assert.NotNull(scope);
            Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), Guid.Parse(scope.OwnerId));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
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
        wpf.Run(() =>
        {
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
            AppKnowledgeWindow? window = null;

            try
            {
                window = new AppKnowledgeWindow();
                var draftState = (AppKnowledgeUiState)typeof(AppKnowledgeWindow)
                    .GetField("_uiState", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(window)!;
                Assert.False(draftState.IsDraftDirty);
                var builtin = Category(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));
                builtin.IsBuiltin = true;
                var rowType = typeof(AppKnowledgeWindow).GetNestedType("CategoryRow", BindingFlags.NonPublic)!;
                var fromNode = rowType.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!;
                var row = fromNode.Invoke(null, [builtin])!;
                var rows = Array.CreateInstance(rowType, 1);
                rows.SetValue(row, 0);
                var tree = Assert.IsType<TreeView>(window.FindName("CategoryTree"));
                tree.ItemsSource = rows;

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
            }
            finally
            {
                if (window is not null)
                {
                    typeof(AppKnowledgeWindow).GetField("_allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .SetValue(window, true);
                    window.Close();
                }
                servicesProperty.SetValue(null, previousServices);
                knowledge.Dispose();
            }
        });
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

    private sealed class ExpiredSessionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string json =
                """
                {
                  "code": 0,
                  "message": "",
                  "timestamp": "2026-11-05T00:00:00Z",
                  "data": {
                    "accessToken": "expired-access-token",
                    "refreshToken": "valid-refresh-token",
                    "expiresAt": "2020-01-01T00:00:00Z",
                    "user": {
                      "id": "44444444-4444-4444-4444-444444444444",
                      "username": "user",
                      "displayName": "User",
                      "role": "user"
                    }
                  }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
