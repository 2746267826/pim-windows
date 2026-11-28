using System.Text.RegularExpressions;

namespace Pim.Client.Core.Services.Notes;

public enum NativeNoteDocumentSegmentKind
{
    Markdown,
    Material,
    UnresolvedMaterial,
}

public sealed record NativeNoteDocumentSegment(
    NativeNoteDocumentSegmentKind Kind,
    string Text,
    Guid? MaterialId,
    LocalNoteMaterial? Material);

public sealed record NativeNoteMarkerInsertion(string Markdown, int CaretIndex);

/// <summary>
/// The local document uses invisible HTML comments as stable material anchors.
/// Moving a marker moves the corresponding photo, handwriting or recording in
/// the continuous view and in the exported fixed version.
/// </summary>
public sealed record NativeNoteDocumentLayout(
    IReadOnlyList<NativeNoteDocumentSegment> Segments,
    IReadOnlyList<LocalNoteMaterial> UnplacedMaterials,
    IReadOnlyList<Guid> OrderedMaterialIds)
{
    private static readonly Regex MarkerPattern = new(
        @"<!--\s*pim-material:(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*-->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string CreateMarker(Guid materialId)
    {
        if (materialId == Guid.Empty)
        {
            throw new ArgumentException("材料标识不能为空。", nameof(materialId));
        }

        return $"<!-- pim-material:{materialId:D} -->";
    }

    public static NativeNoteMarkerInsertion InsertMarkers(
        string? markdown,
        int caretIndex,
        IEnumerable<Guid> materialIds)
    {
        ArgumentNullException.ThrowIfNull(materialIds);
        var text = markdown ?? string.Empty;
        var position = Math.Clamp(caretIndex, 0, text.Length);
        var markers = materialIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Select(CreateMarker)
            .ToArray();
        if (markers.Length == 0)
        {
            return new NativeNoteMarkerInsertion(text, position);
        }

        var markerBlock = string.Join("\n\n", markers);
        var prefix = position == 0
            ? string.Empty
            : text[position - 1] == '\n'
                ? position >= 2 && text[position - 2] == '\n' ? string.Empty : "\n"
                : "\n\n";
        var suffix = position == text.Length
            ? "\n"
            : text[position] == '\n'
                ? position + 1 < text.Length && text[position + 1] == '\n' ? string.Empty : "\n"
                : "\n\n";
        var insertion = prefix + markerBlock + suffix;
        return new NativeNoteMarkerInsertion(
            text.Insert(position, insertion),
            position + insertion.Length);
    }

    public static NativeNoteDocumentLayout Parse(
        string? markdown,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        var text = markdown ?? string.Empty;
        var materialOrder = materials.ToArray();
        var materialById = materialOrder
            .GroupBy(material => material.MaterialId)
            .ToDictionary(group => group.Key, group => group.First());
        var segments = new List<NativeNoteDocumentSegment>();
        var orderedIds = new List<Guid>();
        var orderedSet = new HashSet<Guid>();
        var placed = new HashSet<Guid>();
        var cursor = 0;
        foreach (Match match in MarkerPattern.Matches(text))
        {
            if (match.Index > cursor)
            {
                segments.Add(new NativeNoteDocumentSegment(
                    NativeNoteDocumentSegmentKind.Markdown,
                    text[cursor..match.Index],
                    null,
                    null));
            }

            if (Guid.TryParse(match.Groups["id"].Value, out var markerId)
                && materialById.TryGetValue(markerId, out var markerMaterial))
            {
                var displayMaterial = ResolveDisplayMaterial(
                    markerMaterial,
                    materialById,
                    materialOrder);
                AddFamilyToOrder(
                    displayMaterial,
                    materialOrder,
                    orderedIds,
                    orderedSet,
                    placed);
                segments.Add(new NativeNoteDocumentSegment(
                    NativeNoteDocumentSegmentKind.Material,
                    match.Value,
                    displayMaterial.MaterialId,
                    displayMaterial));
            }
            else
            {
                segments.Add(new NativeNoteDocumentSegment(
                    NativeNoteDocumentSegmentKind.UnresolvedMaterial,
                    match.Value,
                    Guid.TryParse(match.Groups["id"].Value, out var unresolvedId)
                        ? unresolvedId
                        : null,
                    null));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            segments.Add(new NativeNoteDocumentSegment(
                NativeNoteDocumentSegmentKind.Markdown,
                text[cursor..],
                null,
                null));
        }

        foreach (var material in materials)
        {
            if (orderedSet.Add(material.MaterialId))
            {
                orderedIds.Add(material.MaterialId);
            }
        }

        var unplaced = new List<LocalNoteMaterial>();
        var unplacedFamilies = new HashSet<Guid>();
        foreach (var material in materials)
        {
            if (placed.Contains(material.MaterialId))
            {
                continue;
            }

            var displayMaterial = ResolveDisplayMaterial(
                material,
                materialById,
                materialOrder);
            if (placed.Contains(displayMaterial.MaterialId)
                || !unplacedFamilies.Add(displayMaterial.MaterialId))
            {
                continue;
            }

            unplaced.Add(displayMaterial);
        }

        return new NativeNoteDocumentLayout(segments, unplaced, orderedIds);
    }

    public static IReadOnlyList<LocalNoteMaterial> OrderMaterials(
        string? markdown,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        var layout = Parse(markdown, materials);
        var materialById = materials
            .GroupBy(material => material.MaterialId)
            .ToDictionary(group => group.Key, group => group.First());
        return layout.OrderedMaterialIds
            .Where(materialById.ContainsKey)
            .Select(materialId => materialById[materialId])
            .ToArray();
    }

    public static IReadOnlySet<Guid> ReferencedMaterialIds(string? markdown)
    {
        var result = new HashSet<Guid>();
        foreach (Match match in MarkerPattern.Matches(markdown ?? string.Empty))
        {
            if (Guid.TryParse(match.Groups["id"].Value, out var materialId))
            {
                result.Add(materialId);
            }
        }

        return result;
    }

    /// <summary>
    /// Photo and ink blocks own their server-side position. Legacy material
    /// markers for those block-backed files are ignored in Markdown so the
    /// continuous view renders each file once. Recordings have no server block
    /// kind; their local Markdown anchors are the only positional signal.
    /// </summary>
    public static NativeNoteDocumentLayout ParseServerSource(
        LocalNoteServerSource source,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materials);
        var blockBackedMaterialIds = source.Blocks
            .Where(block => block.Kind is "photo" or "ink")
            .SelectMany(block => block.MaterialIds)
            .ToHashSet();
        var builder = new System.Text.StringBuilder();
        foreach (var block in source.Blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            if (block.Kind == "markdown")
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(block.RawJson);
                    if (document.RootElement.TryGetProperty("text", out var text)
                        && text.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        builder.Append(StripBlockBackedMarkers(
                            text.GetString() ?? string.Empty,
                            blockBackedMaterialIds));
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    builder.Append(StripBlockBackedMarkers(
                        block.RawJson,
                        blockBackedMaterialIds));
                }

                continue;
            }

            var materialId = block.Kind == "ink"
                ? block.MaterialIds.FirstOrDefault(id =>
                    materials.Any(material => material.MaterialId == id
                        && material.Kind == LocalNoteMaterialKind.Handwriting))
                : block.MaterialIds.FirstOrDefault(id =>
                    materials.Any(material => material.MaterialId == id
                        && material.Kind == LocalNoteMaterialKind.Photo));
            if (materialId != Guid.Empty)
            {
                builder.Append(CreateMarker(materialId));
            }
        }

        return Parse(builder.ToString(), materials);
    }

    public static IReadOnlyList<LocalNoteMaterial> OrderServerSourceMaterials(
        LocalNoteServerSource source,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materials);
        var materialById = materials
            .GroupBy(material => material.MaterialId)
            .ToDictionary(group => group.Key, group => group.First());
        var ordered = new List<LocalNoteMaterial>(materials.Count);
        var seen = new HashSet<Guid>();
        foreach (var materialId in ParseServerSource(source, materials).OrderedMaterialIds)
        {
            if (seen.Add(materialId) && materialById.TryGetValue(materialId, out var material))
            {
                ordered.Add(material);
            }
        }

        foreach (var material in materials)
        {
            if (seen.Add(material.MaterialId))
            {
                ordered.Add(material);
            }
        }

        return ordered;
    }

    private static string StripBlockBackedMarkers(
        string text,
        IReadOnlySet<Guid> blockBackedMaterialIds)
    {
        if (blockBackedMaterialIds.Count == 0)
        {
            return text;
        }

        return MarkerPattern.Replace(
            text,
            match => Guid.TryParse(match.Groups["id"].Value, out var materialId)
                && blockBackedMaterialIds.Contains(materialId)
                ? string.Empty
                : match.Value);
    }

    private static LocalNoteMaterial ResolveDisplayMaterial(
        LocalNoteMaterial material,
        IReadOnlyDictionary<Guid, LocalNoteMaterial> materialById,
        IReadOnlyList<LocalNoteMaterial> materialOrder)
    {
        if (material.Kind == LocalNoteMaterialKind.HandwritingSource)
        {
            var preview = FindHandwritingPreview(material, materialOrder);
            return preview ?? material;
        }

        if (material.Kind == LocalNoteMaterialKind.RecordingSegment)
        {
            var recordingKey = material.RecordingId ?? material.MaterialId;
            return materialOrder
                .Where(candidate => candidate.Kind == LocalNoteMaterialKind.Recording
                    && (candidate.RecordingId ?? candidate.MaterialId) == recordingKey)
                .OrderBy(candidate => candidate.AddedAt)
                .ThenBy(candidate => candidate.MaterialId)
                .FirstOrDefault()
                ?? materialOrder
                    .Where(candidate => candidate.Kind == LocalNoteMaterialKind.RecordingSegment
                        && (candidate.RecordingId ?? candidate.MaterialId) == recordingKey)
                    .OrderBy(candidate => candidate.RecordingSegment ?? 1)
                    .ThenBy(candidate => candidate.RecordingChunkIndex ?? 1)
                    .ThenBy(candidate => candidate.AddedAt)
                    .FirstOrDefault()
                ?? material;
        }

        return materialById.TryGetValue(material.MaterialId, out var current) ? current : material;
    }

    private static void AddFamilyToOrder(
        LocalNoteMaterial displayMaterial,
        IReadOnlyList<LocalNoteMaterial> materialOrder,
        List<Guid> orderedIds,
        HashSet<Guid> orderedSet,
        HashSet<Guid> placed)
    {
        foreach (var familyMember in GetFamily(displayMaterial, materialOrder))
        {
            placed.Add(familyMember.MaterialId);
            if (orderedSet.Add(familyMember.MaterialId))
            {
                orderedIds.Add(familyMember.MaterialId);
            }
        }
    }

    private static IEnumerable<LocalNoteMaterial> GetFamily(
        LocalNoteMaterial material,
        IReadOnlyList<LocalNoteMaterial> materialOrder)
    {
        if (material.Kind == LocalNoteMaterialKind.Handwriting)
        {
            yield return material;
            var source = FindHandwritingSource(material, materialOrder);
            if (source is not null)
            {
                yield return source;
            }

            yield break;
        }

        if (material.Kind is LocalNoteMaterialKind.Recording or LocalNoteMaterialKind.RecordingSegment)
        {
            var recordingKey = material.RecordingId ?? material.MaterialId;
            foreach (var candidate in materialOrder
                         .Where(candidate => (candidate.Kind is LocalNoteMaterialKind.Recording
                                 or LocalNoteMaterialKind.RecordingSegment)
                             && (candidate.RecordingId ?? candidate.MaterialId) == recordingKey)
                         .OrderBy(candidate => candidate.Kind == LocalNoteMaterialKind.Recording ? 0 : 1)
                         .ThenBy(candidate => candidate.RecordingSegment ?? 1)
                         .ThenBy(candidate => candidate.RecordingChunkIndex ?? 1)
                         .ThenBy(candidate => candidate.AddedAt))
            {
                yield return candidate;
            }

            yield break;
        }

        yield return material;
    }

    private static LocalNoteMaterial? FindHandwritingSource(
        LocalNoteMaterial preview,
        IReadOnlyList<LocalNoteMaterial> materialOrder)
    {
        var previewIndex = IndexOfMaterial(materialOrder, preview.MaterialId);
        if (previewIndex >= 0
            && previewIndex + 1 < materialOrder.Count
            && IsHandwritingPair(preview, materialOrder[previewIndex + 1]))
        {
            return materialOrder[previewIndex + 1];
        }

        var candidates = materialOrder
            .Where(candidate => IsHandwritingPair(preview, candidate))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static LocalNoteMaterial? FindHandwritingPreview(
        LocalNoteMaterial source,
        IReadOnlyList<LocalNoteMaterial> materialOrder)
    {
        var sourceIndex = IndexOfMaterial(materialOrder, source.MaterialId);
        if (sourceIndex > 0
            && IsHandwritingPair(materialOrder[sourceIndex - 1], source))
        {
            return materialOrder[sourceIndex - 1];
        }

        var candidates = materialOrder
            .Where(candidate => IsHandwritingPair(candidate, source))
            .Take(2)
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static int IndexOfMaterial(
        IReadOnlyList<LocalNoteMaterial> materials,
        Guid materialId)
    {
        for (var index = 0; index < materials.Count; index++)
        {
            if (materials[index].MaterialId == materialId)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsHandwritingPair(
        LocalNoteMaterial preview,
        LocalNoteMaterial source)
        => preview.Kind == LocalNoteMaterialKind.Handwriting
            && source.Kind == LocalNoteMaterialKind.HandwritingSource
            && preview.Width == source.Width
            && preview.Height == source.Height
            && string.Equals(
                RemoveExtension(preview.DisplayName, ".png"),
                RemoveExtension(source.DisplayName, ".ink.json"),
                StringComparison.Ordinal);

    private static string RemoveExtension(string value, string extension)
        => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? value[..^extension.Length]
            : value;
}
