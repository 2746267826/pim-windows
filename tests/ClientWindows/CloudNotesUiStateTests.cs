using Pim.Client.App;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class CloudNotesUiStateTests
{
    [Fact]
    public void DeclinedNoteSwitchCanRestoreTheCurrentEditorTicket()
    {
        var state = new CloudNotesUiState();
        var originalNoteTicket = state.BeginNoteSelection();
        var originalArtifactTicket = state.BeginArtifactSelection();
        var attemptedNoteTicket = state.BeginNoteSelection();
        var attemptedArtifactTicket = state.BeginArtifactSelection();

        Assert.False(state.IsResultEditorCurrent(originalNoteTicket, originalArtifactTicket));
        Assert.True(state.IsResultEditorCurrent(attemptedNoteTicket, attemptedArtifactTicket));
    }

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

    [Fact]
    public void LoadedTaskSourceRejectsAccountGenerationChangeEvenWhenAccountReturns()
    {
        var account = new CloudNoteAccountScope("https://pim.example", "owner-a");
        var fence = new LoadedCloudArtifactFence(
            SourceSelectionGeneration: 3,
            NoteGeneration: 7,
            NoteId: Guid.NewGuid(),
            ArtifactId: Guid.NewGuid(),
            VersionId: Guid.NewGuid(),
            Account: account,
            AccountGeneration: 12);

        Assert.True(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence, 3, 7, fence.NoteId, fence.ArtifactId, fence.VersionId, account, 12));
        Assert.False(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence,
            3,
            7,
            fence.NoteId,
            fence.ArtifactId,
            fence.VersionId,
            new CloudNoteAccountScope("https://pim.example", "owner-b"),
            12));
        Assert.False(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence, 3, 7, fence.NoteId, fence.ArtifactId, fence.VersionId, account, 14));
    }

    [Fact]
    public void LoadedTaskSourceRejectsChangedNoteVersionAndSelection()
    {
        var fence = new LoadedCloudArtifactFence(
            SourceSelectionGeneration: 3,
            NoteGeneration: 7,
            NoteId: Guid.NewGuid(),
            ArtifactId: Guid.NewGuid(),
            VersionId: Guid.NewGuid(),
            Account: new CloudNoteAccountScope("https://pim.example", "owner-a"),
            AccountGeneration: 12);

        Assert.False(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence, 4, 7, fence.NoteId, fence.ArtifactId, fence.VersionId, fence.Account, 12));
        Assert.False(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence, 3, 8, fence.NoteId, fence.ArtifactId, fence.VersionId, fence.Account, 12));
        Assert.False(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence, 3, 7, Guid.NewGuid(), fence.ArtifactId, fence.VersionId, fence.Account, 12));
        Assert.False(CloudNotesUiState.IsLoadedCloudArtifactCurrent(
            fence, 3, 7, fence.NoteId, fence.ArtifactId, Guid.NewGuid(), fence.Account, 12));
    }
}
