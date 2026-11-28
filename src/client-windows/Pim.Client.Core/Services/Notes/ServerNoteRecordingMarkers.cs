namespace Pim.Client.Core.Services.Notes;

/// <summary>
/// Older uploads kept the local recording material id in Markdown. That id is
/// the recording id for a native recording, while restore now assigns a stable
/// id to every chunk. These aliases let old immutable server sources render and
/// re-upload with the same anchors as newly built bundles.
/// </summary>
internal static class ServerNoteRecordingMarkers
{
    public static IReadOnlyDictionary<Guid, Guid> BuildAliases(
        LocalNoteServerSource source,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materials);
        var aliases = new Dictionary<Guid, Guid>();
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
            var stableId = first.MaterialId
                ?? materials.FirstOrDefault(material =>
                    material.Kind == LocalNoteMaterialKind.RecordingSegment
                    && material.RecordingId == recordingId
                    && material.RecordingSegment == first.Segment
                    && material.RecordingChunkIndex == first.ChunkIndex)?.MaterialId
                ?? (first.Samples is > 0
                    ? ServerNoteMaterialStableId.ForRecordingChunk(
                        recordingId,
                        first.Segment!.Value,
                        first.ChunkIndex!.Value,
                        first.Samples.Value)
                    : (Guid?)null);
            if (stableId is not { } target || target == Guid.Empty)
            {
                continue;
            }

            aliases[recordingId] = target;
            foreach (var chunk in chunks)
            {
                if (chunk.MaterialId is { } chunkId && chunkId != Guid.Empty)
                {
                    aliases[chunkId] = target;
                }
            }
        }

        return aliases;
    }
}
