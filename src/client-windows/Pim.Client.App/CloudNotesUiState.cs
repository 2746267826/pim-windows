using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

internal sealed record LoadedCloudArtifactFence(
    long SourceSelectionGeneration,
    long NoteGeneration,
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    CloudNoteAccountScope Account,
    long AccountGeneration);

/// <summary>
/// Monotonic tickets for note and result selections. Async continuations may only
/// update the editor when their ticket is still the latest.
/// </summary>
internal sealed class CloudNotesUiState
{
    private long _noteGeneration;
    private long _artifactGeneration;
    private long _draftGeneration;
    private int _draftDirty;

    public long NoteGeneration => Interlocked.Read(ref _noteGeneration);
    public long ArtifactGeneration => Interlocked.Read(ref _artifactGeneration);
    public long DraftGeneration => Interlocked.Read(ref _draftGeneration);
    public bool IsDraftDirty => Volatile.Read(ref _draftDirty) != 0;

    public long BeginNoteSelection()
        => Interlocked.Increment(ref _noteGeneration);

    public long BeginArtifactSelection()
        => Interlocked.Increment(ref _artifactGeneration);

    public long MarkDraftDirty()
    {
        Volatile.Write(ref _draftDirty, 1);
        return Interlocked.Increment(ref _draftGeneration);
    }

    public long ClearDraft()
    {
        Volatile.Write(ref _draftDirty, 0);
        return Interlocked.Increment(ref _draftGeneration);
    }

    public bool IsDraftCurrent(long ticket)
        => ticket == DraftGeneration;

    public bool IsResultEditorCurrent(long noteTicket, long artifactTicket)
        => noteTicket == NoteGeneration && artifactTicket == ArtifactGeneration;

    public bool IsNoteCurrent(long ticket, Guid expectedNoteId, Guid? currentNoteId, Guid? selectedNoteId)
        => ticket == NoteGeneration
           && currentNoteId == expectedNoteId
           && selectedNoteId == expectedNoteId;

    public bool IsArtifactCurrent(long ticket)
        => ticket == ArtifactGeneration;

    public static bool IsLoadedCloudArtifactCurrent(
        LoadedCloudArtifactFence fence,
        long sourceSelectionGeneration,
        long noteGeneration,
        Guid currentNoteId,
        Guid currentArtifactId,
        Guid currentVersionId,
        CloudNoteAccountScope? currentAccount,
        long currentAccountGeneration)
        => fence.SourceSelectionGeneration == sourceSelectionGeneration
           && fence.NoteGeneration == noteGeneration
           && fence.NoteId == currentNoteId
           && fence.ArtifactId == currentArtifactId
           && fence.VersionId == currentVersionId
           && fence.Account == currentAccount
           && fence.AccountGeneration == currentAccountGeneration;
}
