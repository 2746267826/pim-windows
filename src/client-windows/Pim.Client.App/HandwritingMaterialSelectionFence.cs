using Pim.Client.Core.Services.Notes;

namespace Pim.Client.App;

internal readonly record struct HandwritingMaterialSelectionFence(
    Guid PreviewMaterialId,
    long SelectionGeneration)
{
    public bool IsCurrent(
        LocalNoteMaterial? selectedMaterial,
        long currentSelectionGeneration)
        => PreviewMaterialId != Guid.Empty
           && SelectionGeneration == currentSelectionGeneration
           && selectedMaterial is { Kind: LocalNoteMaterialKind.Handwriting }
           && selectedMaterial.MaterialId == PreviewMaterialId;
}

internal sealed class HandwritingMaterialSelectionState
{
    private long _generation;

    public long Generation => Interlocked.Read(ref _generation);

    public HandwritingMaterialSelectionFence Capture(Guid previewMaterialId)
        => new(previewMaterialId, Generation);

    public void MarkChanged() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(
        HandwritingMaterialSelectionFence fence,
        LocalNoteMaterial? selectedMaterial)
        => fence.IsCurrent(selectedMaterial, Generation);
}
