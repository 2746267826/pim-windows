using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

internal sealed record AppKnowledgeUiFence(AppKnowledgeAccountScope Account, long ServiceGeneration, long ViewGeneration);

internal enum AppKnowledgeDraftKind
{
    Category,
    Context,
}

internal sealed class AppKnowledgeUiState
{
    private readonly HashSet<long> _busyOperations = [];
    private long _nextBusyOperation;
    private bool _draftDirty;
    private AppKnowledgeAccountScope? _draftAccount;
    private long _draftServiceGeneration;
    private AppKnowledgeDraftKind? _draftKind;

    public bool IsDraftDirty => _draftDirty;
    public AppKnowledgeDraftKind? DraftKind => _draftKind;
    public bool IsBusy => _busyOperations.Count > 0;

    public long BeginBusy()
    {
        var id = Interlocked.Increment(ref _nextBusyOperation);
        _busyOperations.Add(id);
        return id;
    }

    public bool EndBusy(long id)
    {
        _busyOperations.Remove(id);
        return IsBusy;
    }

    public bool IsDraftStale(AppKnowledgeAccountScope? account, long serviceGeneration)
        => _draftDirty && !CanSaveDraft(account, serviceGeneration);

    public static AppKnowledgeUiFence Capture(
        AppKnowledgeAccountScope? account,
        long serviceGeneration,
        long viewGeneration)
    {
        if (account is null)
        {
            throw new InvalidOperationException("请先登录 PIM 账户。");
        }

        return new AppKnowledgeUiFence(account, serviceGeneration, viewGeneration);
    }

    public static bool IsCurrent(
        AppKnowledgeUiFence fence,
        AppKnowledgeAccountScope? account,
        long serviceGeneration,
        long viewGeneration)
        => account == fence.Account
           && serviceGeneration == fence.ServiceGeneration
           && fence.ViewGeneration == viewGeneration;

    public static bool WouldCreateCategoryCycle(Guid? proposedParentId, CategoryTreeNode editedCategory)
        => proposedParentId is { } parentId && ContainsCategory(editedCategory, parentId);

    public void MarkDraftDirty(
        AppKnowledgeDraftKind kind,
        AppKnowledgeAccountScope? account,
        long serviceGeneration)
    {
        if (!_draftDirty)
        {
            _draftAccount = account;
            _draftServiceGeneration = serviceGeneration;
            _draftKind = kind;
        }

        _draftDirty = true;
    }

    public bool CanSaveDraft(AppKnowledgeAccountScope? account, long serviceGeneration)
        => !_draftDirty || (_draftAccount == account && _draftServiceGeneration == serviceGeneration);

    public void MarkDraftSaved()
    {
        _draftDirty = false;
        _draftAccount = null;
        _draftServiceGeneration = 0;
        _draftKind = null;
    }

    public bool TryDiscardDraft(bool confirmed)
    {
        if (!_draftDirty || !confirmed)
        {
            return !_draftDirty;
        }

        MarkDraftSaved();
        return true;
    }

    private static bool ContainsCategory(CategoryTreeNode node, Guid categoryId)
        => (Guid.TryParse(node.Id, out var id) && id == categoryId)
           || (node.Children ?? []).Any(child => ContainsCategory(child, categoryId));
}
