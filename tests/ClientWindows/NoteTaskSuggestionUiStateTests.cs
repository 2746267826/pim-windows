using Pim.Client.App;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class NoteTaskSuggestionUiStateTests
{
    private static readonly CloudNoteAccountScope AccountA =
        new("https://one.example.test/api/v1", "account-a");
    private static readonly CloudNoteAccountScope AccountB =
        new("https://two.example.test/api/v1", "account-b");

    [Fact]
    public void LateSelectionReceiptCannotMatchNewSelectionOrSource()
    {
        var state = new NoteTaskSuggestionUiState();
        var source = Source();
        var load = state.BeginLoad(
            source.NoteId, source.ArtifactId, source.VersionId, source, AccountA, 4);
        var suggestionA = Guid.NewGuid();
        var suggestionB = Guid.NewGuid();
        var selectionA = state.BeginSelection(load, suggestionA);

        Assert.True(state.IsCurrentSelection(selectionA, suggestionA, source, AccountA, 4));

        _ = state.BeginSelection(load, suggestionB);
        Assert.False(state.IsCurrentSelection(selectionA, suggestionA, source, AccountA, 4));
        Assert.False(state.IsCurrentSelection(selectionA, suggestionA, source, AccountB, 4));
        Assert.False(state.IsCurrentSelection(selectionA, suggestionA, source with
        {
            ContentSha256 = new string('b', 64),
        }, AccountA, 4));
    }

    [Fact]
    public void EmptySuggestionIdentityCannotProduceCurrentSelectionFence()
    {
        var state = new NoteTaskSuggestionUiState();
        var source = Source();
        var load = state.BeginLoad(
            source.NoteId, source.ArtifactId, source.VersionId, source, AccountA, 4);
        var selection = state.BeginSelection(load, Guid.Empty);

        Assert.False(state.IsCurrentSelection(selection, Guid.Empty, source, AccountA, 4));
    }

    [Fact]
    public void SessionGenerationAndInvalidationRejectOldWindowReceipts()
    {
        var state = new NoteTaskSuggestionUiState();
        var source = Source();
        var load = state.BeginLoad(
            source.NoteId, source.ArtifactId, source.VersionId, source, AccountA, 4);

        Assert.True(state.IsCurrent(load, source, AccountA, 4));
        Assert.False(state.IsCurrent(load, source, AccountA, 5));
        Assert.False(state.IsCurrent(load, source, AccountB, 4));

        state.Invalidate();

        Assert.False(state.IsCurrent(load, source, AccountA, 4));
    }

    [Fact]
    public void ServerWritesRequireCurrentDraftAndNoPendingOrBusyOperation()
    {
        Assert.True(NoteTaskSuggestionUiState.CanWriteServer(
            sessionCurrent: true,
            hasPendingConfirmation: false,
            isDraftSuggestion: true,
            isBusy: false));
        Assert.False(NoteTaskSuggestionUiState.CanWriteServer(true, true, true, false));
        Assert.False(NoteTaskSuggestionUiState.CanWriteServer(true, false, true, true));
        Assert.False(NoteTaskSuggestionUiState.CanWriteServer(true, false, false, false));
        Assert.False(NoteTaskSuggestionUiState.CanWriteServer(false, false, true, false));
    }

    [Fact]
    public void CloseOrSelectionCannotClearAChangedLocalDraftGeneration()
    {
        var state = new NoteTaskSuggestionUiState();
        var saveGeneration = state.MarkDraftDirtyAndRemember();
        state.MarkDraftDirtyAndRemember();

        Assert.False(state.TryClearDraft(saveGeneration));
        Assert.True(state.IsDraftDirty);
    }

    [Fact]
    public void SuggestionSelectionIsLockedWhileBusyOrLoading()
    {
        Assert.True(NoteTaskSuggestionUiState.CanChangeSelection(false, false, false));
        Assert.False(NoteTaskSuggestionUiState.CanChangeSelection(false, true, false));
        Assert.False(NoteTaskSuggestionUiState.CanChangeSelection(false, false, true));
        Assert.False(NoteTaskSuggestionUiState.CanChangeSelection(true, false, false));
    }

    [Fact]
    public void PendingConfirmationCanBeVerifiedButCannotStartAnotherWrite()
    {
        Assert.True(NoteTaskSuggestionUiState.CanVerifyPending(true, true, false));
        Assert.False(NoteTaskSuggestionUiState.CanVerifyPending(true, false, false));
        Assert.False(NoteTaskSuggestionUiState.CanVerifyPending(true, true, true));
        Assert.False(NoteTaskSuggestionUiState.CanVerifyPending(false, true, false));
        Assert.False(NoteTaskSuggestionUiState.CanWriteServer(true, true, true, false));
    }

    private static NoteTaskSuggestionSource Source()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "generation-task");
}
