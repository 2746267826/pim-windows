namespace Pim.Client.Core.Services.Notes;

public sealed record NativeNoteLibraryViewportSnapshot(
    Guid? AnchorNoteId,
    double AnchorOffsetPixels,
    Guid? SelectedNoteId,
    bool SelectedVisible);

public sealed record NativeNoteLibraryViewportPlan(
    Guid? ScrollTargetNoteId,
    double ScrollTargetOffsetPixels,
    Guid? EnsureVisibleNoteId);

/// <summary>
/// Captures the first visible library row before a refresh replaces the rows.
/// The row identity, not a raw pixel offset alone, is the durable anchor when
/// a search returns a different list.
/// </summary>
public sealed class NativeNoteLibraryViewState
{
    private NativeNoteLibraryViewportSnapshot? _snapshot;

    public bool HasSnapshot => _snapshot is not null;

    public void Capture(
        IReadOnlyList<Guid> visibleOrder,
        Guid? selectedNoteId,
        Guid? anchorNoteId,
        double anchorOffsetPixels,
        bool selectedVisible)
    {
        ArgumentNullException.ThrowIfNull(visibleOrder);
        if (visibleOrder.Count == 0)
        {
            _snapshot = null;
            return;
        }

        var available = visibleOrder.ToHashSet();
        var selected = selectedNoteId is { } selectedId && available.Contains(selectedId)
            ? selectedId
            : (Guid?)null;
        var anchor = anchorNoteId is { } anchorId && available.Contains(anchorId)
            ? anchorId
            : selected ?? visibleOrder[0];
        _snapshot = new NativeNoteLibraryViewportSnapshot(
            anchor,
            double.IsFinite(anchorOffsetPixels) ? anchorOffsetPixels : 0,
            selected,
            selectedVisible && selected is not null);
    }

    public NativeNoteLibraryViewportPlan? CreateRestorePlan(
        IReadOnlyList<Guid> visibleOrder,
        Guid? selectedNoteId)
    {
        ArgumentNullException.ThrowIfNull(visibleOrder);
        if (visibleOrder.Count == 0)
        {
            return null;
        }

        var available = visibleOrder.ToHashSet();
        var selected = selectedNoteId is { } selectedId && available.Contains(selectedId)
            ? selectedId
            : (Guid?)null;
        if (_snapshot is not { } snapshot)
        {
            return selected is { } selectedWithoutHistory
                ? new NativeNoteLibraryViewportPlan(
                    selectedWithoutHistory,
                    0,
                    selectedWithoutHistory)
                : null;
        }

        var anchor = snapshot.AnchorNoteId is { } anchorId && available.Contains(anchorId)
            ? anchorId
            : (Guid?)null;
        var selectedStillVisible = selected == snapshot.SelectedNoteId && snapshot.SelectedVisible;
        var target = selected is not null && !selectedStillVisible
            ? selected
            : anchor ?? selected ?? visibleOrder[0];
        return new NativeNoteLibraryViewportPlan(
            target,
            target == anchor ? snapshot.AnchorOffsetPixels : 0,
            selected);
    }

    public void Clear() => _snapshot = null;
}
