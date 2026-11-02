using Pim.Client.App;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
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
}
