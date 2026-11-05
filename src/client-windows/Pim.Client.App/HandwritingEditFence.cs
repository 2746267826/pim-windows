using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

internal sealed record HandwritingEditFence(
    Guid NoteId,
    Guid VersionId,
    long NoteSelectionGeneration,
    CloudNoteAccountScope? Account,
    long AccountGeneration)
{
    public bool IsCurrent(
        Guid? currentNoteId,
        Guid? currentVersionId,
        long currentNoteSelectionGeneration,
        CloudNoteAccountScope? currentAccount,
        long currentAccountGeneration,
        bool isParentWindowClosed = false)
        => !isParentWindowClosed
           && NoteId != Guid.Empty
           && VersionId != Guid.Empty
           && NoteId == currentNoteId
           && VersionId == currentVersionId
           && NoteSelectionGeneration == currentNoteSelectionGeneration
           && Account == currentAccount
           && AccountGeneration == currentAccountGeneration;
}
