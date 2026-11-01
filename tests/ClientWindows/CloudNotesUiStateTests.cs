using Pim.Client.App;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class CloudNotesUiStateTests
{
    [Fact]
    public void StaleNoteAndArtifactRequestsCannotUpdateTheCurrentSelection()
    {
        var state = new CloudNotesUiState();
        var noteA = Guid.NewGuid();
        var noteB = Guid.NewGuid();
        var ticketA = state.BeginNoteSelection();
        var artifactA = state.BeginArtifactSelection();

        var ticketB = state.BeginNoteSelection();
        var artifactB = state.BeginArtifactSelection();

        Assert.False(state.IsNoteCurrent(ticketA, noteA, noteB, noteB));
        Assert.False(state.IsArtifactCurrent(artifactA));
        Assert.True(state.IsNoteCurrent(ticketB, noteB, noteB, noteB));
        Assert.True(state.IsArtifactCurrent(artifactB));
    }

    [Fact]
    public void SameNoteButDifferentSelectedRowStillRejectsTheOldRequest()
    {
        var state = new CloudNotesUiState();
        var noteId = Guid.NewGuid();
        var ticket = state.BeginNoteSelection();

        Assert.False(state.IsNoteCurrent(ticket, noteId, noteId, Guid.NewGuid()));
    }

    [Fact]
    public void StaleResultRequestCannotEnableTheEditorAfterSelectionChanges()
    {
        var state = new CloudNotesUiState();
        var noteTicket = state.BeginNoteSelection();
        var artifactTicket = state.BeginArtifactSelection();

        state.BeginArtifactSelection();

        Assert.False(state.IsResultEditorCurrent(noteTicket, artifactTicket));
    }

    [Fact]
    public void FailedNoteTransitionDoesNotClearAnExistingDraft()
    {
        var state = new CloudNotesUiState();
        var draftTicket = state.MarkDraftDirty();

        _ = state.BeginNoteSelection();

        Assert.True(state.IsDraftDirty);
        Assert.True(state.IsDraftCurrent(draftTicket));
    }

    [Fact]
    public void NewEditInvalidatesAnInFlightSaveTicket()
    {
        var state = new CloudNotesUiState();
        var saveTicket = state.MarkDraftDirty();
        state.MarkDraftDirty();

        Assert.False(state.IsDraftCurrent(saveTicket));
        Assert.True(state.IsDraftDirty);

        state.ClearDraft();
        Assert.False(state.IsDraftDirty);
        Assert.False(state.IsDraftCurrent(saveTicket));
    }
}
