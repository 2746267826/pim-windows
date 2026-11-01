namespace Pim.Client.App;

/// <summary>
/// Monotonic tickets for note and result selections. Async continuations may only
/// update the editor when their ticket is still the latest.
/// </summary>
internal sealed class CloudNotesUiState
{
    private long _noteGeneration;
    private long _artifactGeneration;

    public long NoteGeneration => Interlocked.Read(ref _noteGeneration);
    public long ArtifactGeneration => Interlocked.Read(ref _artifactGeneration);

    public long BeginNoteSelection()
        => Interlocked.Increment(ref _noteGeneration);

    public long BeginArtifactSelection()
        => Interlocked.Increment(ref _artifactGeneration);

    public bool IsNoteCurrent(long ticket, Guid expectedNoteId, Guid? currentNoteId, Guid? selectedNoteId)
        => ticket == NoteGeneration
           && currentNoteId == expectedNoteId
           && selectedNoteId == expectedNoteId;

    public bool IsArtifactCurrent(long ticket)
        => ticket == ArtifactGeneration;
}
