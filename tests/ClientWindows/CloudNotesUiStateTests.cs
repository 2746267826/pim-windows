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
}
