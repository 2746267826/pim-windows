namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Older uploads kept the local recording material id in Markdown. That id is
/// the recording id for a native recording, while restore now assigns a stable
/// id to every chunk. These aliases let old immutable server sources render and
/// re-upload with the same anchors as newly built bundles.
/// </summary>
internal static class ServerNoteRecordingMarkers
{
    public static IReadOnlyDictionary<Guid, Guid> BuildStableAliases(
        LocalNoteServerSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return BuildAliases(source, materials: null, preferStableTarget: true);
    }

    public static IReadOnlyDictionary<Guid, Guid> BuildAliases(
        LocalNoteServerSource source,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materials);
        return BuildAliases(source, materials, preferStableTarget: false);
    }

    private static IReadOnlyDictionary<Guid, Guid> BuildAliases(
        LocalNoteServerSource source,
        IReadOnlyList<LocalNoteMaterial>? materials,
        bool preferStableTarget)
    {
        var aliases = new Dictionary<Guid, Guid>();
        var materialIds = materials?
            .Select(material => material.MaterialId)
            .ToHashSet();
        foreach (var group in source.Files
                     .Where(file => file.Kind == "AudioChunk")
                     .GroupBy(file => file.RecordingId, StringComparer.Ordinal))
        {
            if (!Guid.TryParse(group.Key, out var recordingId) || recordingId == Guid.Empty)
            {
                continue;
            }

            var chunks = group
                .Where(file => file.Segment is > 0 && file.ChunkIndex is > 0)
                .OrderBy(file => file.Segment)
                .ThenBy(file => file.ChunkIndex)
                .ToArray();
            if (chunks.Length == 0)
            {
                continue;
            }

            var first = chunks[0];
            var stableId = first.Samples is > 0
                ? ServerNoteMaterialStableId.ForRecordingChunk(
                    recordingId,
                    first.Segment!.Value,
                    first.ChunkIndex!.Value,
                    first.Samples.Value)
                : (Guid?)null;
            var localId = first.MaterialId is { } firstMaterialId
                    && firstMaterialId != Guid.Empty
                ? firstMaterialId
                : materials?.FirstOrDefault(material =>
                    material.Kind == LocalNoteMaterialKind.RecordingSegment
                    && material.RecordingId == recordingId
                    && material.RecordingSegment == first.Segment
                    && material.RecordingChunkIndex == first.ChunkIndex)?.MaterialId;
            if (localId == Guid.Empty)
            {
                localId = null;
            }

            var target = preferStableTarget
                ? stableId ?? localId
                : stableId is { } stable && (materialIds is null || materialIds.Contains(stable))
                    ? stable
                    : localId ?? stableId;
            if (target is not { } targetId || targetId == Guid.Empty)
            {
                continue;
            }

            aliases[recordingId] = targetId;
            foreach (var chunk in chunks)
            {
                if (chunk.MaterialId is { } chunkId && chunkId != Guid.Empty)
                {
                    aliases[chunkId] = targetId;
                }
            }
        }

        return aliases;
    }
}
