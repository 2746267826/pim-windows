using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

internal sealed record NoteTaskSuggestionLoadFence(
    long Generation,
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    string ContentSha256,
    string InputSha256,
    string ManifestSha256,
    string? GenerationTaskId,
    CloudNoteAccountScope Account,
    long AccountGeneration);

internal sealed record NoteTaskSuggestionSelectionFence(
    NoteTaskSuggestionLoadFence Load,
    long SelectionGeneration,
    Guid SuggestionId);

/// <summary>Prevents older source/account requests from changing the active editor.</summary>
internal sealed class NoteTaskSuggestionUiState
{
    private readonly object _draftSync = new();
    private long _generation;
    private long _selectionGeneration;
    private long _draftGeneration;
    private bool _draftDirty;

    public NoteTaskSuggestionLoadFence BeginLoad(
        Guid noteId,
        Guid artifactId,
        Guid versionId,
        NoteTaskSuggestionSource source,
        CloudNoteAccountScope account,
        long accountGeneration)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new NoteTaskSuggestionLoadFence(
            Interlocked.Increment(ref _generation),
            noteId,
            artifactId,
            versionId,
            source.ContentSha256,
            source.InputSha256,
            source.ManifestSha256,
            source.GenerationTaskId,
            account,
            accountGeneration);
    }

    public NoteTaskSuggestionSelectionFence BeginSelection(
        NoteTaskSuggestionLoadFence load,
        Guid suggestionId)
        => new(load, Interlocked.Increment(ref _selectionGeneration), suggestionId);

    public bool IsCurrent(
        NoteTaskSuggestionLoadFence fence,
        NoteTaskSuggestionSource source,
        CloudNoteAccountScope? account,
        long accountGeneration)
    {
        ArgumentNullException.ThrowIfNull(source);
        return fence.Generation == Volatile.Read(ref _generation)
           && fence.NoteId == source.NoteId
           && fence.ArtifactId == source.ArtifactId
           && fence.VersionId == source.VersionId
           && string.Equals(fence.ContentSha256, source.ContentSha256, StringComparison.OrdinalIgnoreCase)
           && string.Equals(fence.InputSha256, source.InputSha256, StringComparison.OrdinalIgnoreCase)
           && string.Equals(fence.ManifestSha256, source.ManifestSha256, StringComparison.OrdinalIgnoreCase)
           && string.Equals(fence.GenerationTaskId, source.GenerationTaskId, StringComparison.Ordinal)
           && fence.Account == account
           && fence.AccountGeneration == accountGeneration;
    }

    public bool IsCurrentSelection(
        NoteTaskSuggestionSelectionFence fence,
        Guid suggestionId,
        NoteTaskSuggestionSource source,
        CloudNoteAccountScope? account,
        long accountGeneration)
        => suggestionId != Guid.Empty
           && fence.SelectionGeneration == Volatile.Read(ref _selectionGeneration)
           && fence.SuggestionId == suggestionId
           && IsCurrent(
               fence.Load,
               source,
               account,
               accountGeneration);

    public long MarkDraftDirty() => MarkDraftDirtyAndRemember();

    public long DraftGeneration => Interlocked.Read(ref _draftGeneration);

    public bool IsDraftDirty
    {
        get
        {
            lock (_draftSync)
            {
                return _draftDirty;
            }
        }
    }

    public long MarkDraftDirtyAndRemember()
    {
        lock (_draftSync)
        {
            _draftDirty = true;
            return Interlocked.Increment(ref _draftGeneration);
        }
    }

    public void ClearDraft()
    {
        lock (_draftSync)
        {
            _draftDirty = false;
            Interlocked.Increment(ref _draftGeneration);
        }
    }

    /// <summary>Only a save that started for the current editor generation may clear the dirty flag.</summary>
    public bool TryClearDraft(long generation)
    {
        lock (_draftSync)
        {
            if (generation != Interlocked.Read(ref _draftGeneration))
            {
                return false;
            }

            _draftDirty = false;
            Interlocked.Increment(ref _draftGeneration);
            return true;
        }
    }

    public bool IsDraftCurrent(long generation) => generation == DraftGeneration;

    public static bool CanWriteServer(
        bool sessionCurrent,
        bool hasPendingConfirmation,
        bool isDraftSuggestion,
        bool isBusy)
        => sessionCurrent && !hasPendingConfirmation && isDraftSuggestion && !isBusy;

    public static bool CanChangeSelection(bool isClosed, bool isBusy, bool isLoading)
        => !isClosed && !isBusy && !isLoading;

    public static bool CanVerifyPending(bool sessionCurrent, bool hasPendingConfirmation, bool isBusy)
        => sessionCurrent && hasPendingConfirmation && !isBusy;

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _selectionGeneration);
        Interlocked.Increment(ref _draftGeneration);
    }
}
