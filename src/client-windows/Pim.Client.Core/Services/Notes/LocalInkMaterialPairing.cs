namespace Pim.Client.Core.Services.Notes;

public static class LocalInkMaterialPairing
{
    public static LocalNoteMaterial ResolveSource(
        Guid previewMaterialId,
        IReadOnlyList<LocalNoteMaterial> versionMaterials)
    {
        ArgumentNullException.ThrowIfNull(versionMaterials);
        var previewIndex = -1;
        for (var index = 0; index < versionMaterials.Count; index++)
        {
            if (versionMaterials[index].MaterialId != previewMaterialId)
            {
                continue;
            }

            if (previewIndex >= 0)
            {
                throw InvalidPair();
            }

            previewIndex = index;
        }

        if (previewIndex < 0 || versionMaterials[previewIndex].Kind != LocalNoteMaterialKind.Handwriting
            || previewIndex + 1 >= versionMaterials.Count)
        {
            throw InvalidPair();
        }

        var preview = versionMaterials[previewIndex];
        var source = versionMaterials[previewIndex + 1];
        if (source.Kind != LocalNoteMaterialKind.HandwritingSource
            || preview.Width != source.Width
            || preview.Height != source.Height)
        {
            throw InvalidPair();
        }

        return source;
    }

    private static InvalidDataException InvalidPair()
        => new("固定版本中所选手写预览未紧邻匹配的笔迹源材料，已拒绝猜测配对。");
}
