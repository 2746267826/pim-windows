using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pim.Client.Core.Services.Notes;

internal static class ServerNoteSourceIntegrity
{
    private const int MaxMetadataBytes = 8 * 1024 * 1024;
    private const int MaxDocumentBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void RequireCurrentSnapshot(LocalNoteDocument note, LocalNoteVersion version)
    {
        if (!string.Equals(note.Title, version.Title, StringComparison.Ordinal)
            || !string.Equals(note.Markdown, version.Markdown, StringComparison.Ordinal)
            || (note.ServerSource is null) != (version.ServerSource is null)
            || note.ServerSource is not null
                && !JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(note.ServerSource, JsonOptions),
                    JsonSerializer.SerializeToNode(version.ServerSource, JsonOptions)))
        {
            throw InvalidSource();
        }
    }

    public static void Validate(
        LocalNoteServerSource source,
        string title,
        string markdown,
        IReadOnlyList<LocalNoteMaterial> materials)
    {
        if (source.SourceNoteId == Guid.Empty
            || source.SourceVersionId == Guid.Empty
            || string.IsNullOrWhiteSpace(source.InputJson)
            || string.IsNullOrWhiteSpace(source.ManifestJson)
            || string.IsNullOrWhiteSpace(source.NoteJson)
            || source.Blocks is null
            || source.Files is null
            || source.Blocks.Any(block => block is null)
            || source.Files.Any(file => file is null)
            || source.Blocks.Count is < 1 or > 100_000
            || source.Files.Count is < 2 or > 100_000
            || Encoding.UTF8.GetByteCount(source.InputJson) > MaxMetadataBytes
            || Encoding.UTF8.GetByteCount(source.ManifestJson) > MaxMetadataBytes
            || Encoding.UTF8.GetByteCount(source.NoteJson) > MaxMetadataBytes
            || Encoding.UTF8.GetByteCount(source.DocumentMarkdown) > MaxDocumentBytes
            || !string.Equals(source.DocumentMarkdown, markdown, StringComparison.Ordinal))
        {
            throw InvalidSource();
        }

        try
        {
            using var input = JsonDocument.Parse(source.InputJson);
            using var originalNote = JsonDocument.Parse(source.NoteJson);
            var inputRoot = input.RootElement;
            var originalNoteRoot = originalNote.RootElement;
            var inputNote = inputRoot.GetProperty("note");
            if (!JsonNode.DeepEquals(
                    JsonNode.Parse(inputNote.GetRawText()),
                    JsonNode.Parse(originalNoteRoot.GetRawText()))
                || ReadGuid(inputRoot, "id") != source.SourceVersionId
                || ReadGuid(originalNoteRoot, "id") != source.SourceNoteId
                || !string.Equals(ReadString(originalNoteRoot, "title"), title, StringComparison.Ordinal)
                || !string.Equals(ReadString(inputNote, "title"), title, StringComparison.Ordinal))
            {
                throw InvalidSource();
            }

            var noteBlocks = inputNote.GetProperty("blocks");
            if (noteBlocks.ValueKind != JsonValueKind.Array || noteBlocks.GetArrayLength() != source.Blocks.Count)
            {
                throw InvalidSource();
            }

            long sourceBlockBytes = 0;
            for (var index = 0; index < source.Blocks.Count; index++)
            {
                var storedBlock = source.Blocks[index];
                if (storedBlock is null || storedBlock.RawJson is null || storedBlock.MaterialIds is null)
                {
                    throw InvalidSource();
                }

                sourceBlockBytes = checked(sourceBlockBytes + Encoding.UTF8.GetByteCount(storedBlock.RawJson));
                if (sourceBlockBytes > MaxMetadataBytes)
                {
                    throw InvalidSource();
                }

                var noteBlock = noteBlocks[index];
                using var blockJson = JsonDocument.Parse(storedBlock.RawJson);
                if (!JsonNode.DeepEquals(
                        JsonNode.Parse(noteBlock.GetRawText()),
                        JsonNode.Parse(blockJson.RootElement.GetRawText()))
                    || !string.Equals(ReadString(blockJson.RootElement, "id"), storedBlock.Id, StringComparison.Ordinal)
                    || !string.Equals(ReadString(blockJson.RootElement, "kind"), storedBlock.Kind, StringComparison.Ordinal)
                    || storedBlock.MaterialIds is null
                    || storedBlock.MaterialIds.Distinct().Count() != storedBlock.MaterialIds.Count)
                {
                    throw InvalidSource();
                }
            }

            var inputRecordings = ReadInputRecordings(inputRoot);
            var parsed = ServerNoteMaterialRestoreParser.Parse(
                source.InputJson,
                source.ManifestJson,
                source.DocumentMarkdown);
            if (parsed.NoteId != source.SourceNoteId
                || parsed.VersionId != source.SourceVersionId
                || !string.Equals(parsed.Title, title, StringComparison.Ordinal)
                || parsed.Blocks.Count != source.Blocks.Count
                || parsed.Files.Count != source.Files.Count)
            {
                throw InvalidSource();
            }

            for (var index = 0; index < source.Blocks.Count; index++)
            {
                var storedBlock = source.Blocks[index];
                var parsedBlock = parsed.Blocks[index];
                if (!string.Equals(storedBlock.Id, parsedBlock.Id, StringComparison.Ordinal)
                    || !string.Equals(storedBlock.Kind, parsedBlock.Kind, StringComparison.Ordinal)
                    || !JsonNode.DeepEquals(
                        JsonNode.Parse(storedBlock.RawJson),
                        JsonNode.Parse(parsedBlock.RawJson)))
                {
                    throw InvalidSource();
                }
            }

            for (var index = 0; index < source.Files.Count; index++)
            {
                var storedFile = source.Files[index];
                var parsedFile = parsed.Files[index];
                if (!string.Equals(storedFile.Path, parsedFile.Path, StringComparison.Ordinal)
                    || !string.Equals(storedFile.Kind, parsedFile.Kind, StringComparison.Ordinal)
                    || !string.Equals(storedFile.Sha256, parsedFile.Sha256, StringComparison.Ordinal)
                    || storedFile.SizeBytes != parsedFile.SizeBytes
                    || !string.Equals(storedFile.BlockId, parsedFile.BlockId, StringComparison.Ordinal)
                    || !string.Equals(storedFile.RecordingId, parsedFile.RecordingId, StringComparison.Ordinal)
                    || storedFile.Segment != parsedFile.Segment
                    || storedFile.ChunkIndex != parsedFile.ChunkIndex
                    || storedFile.Samples != parsedFile.Samples)
                {
                    throw InvalidSource();
                }
            }

            var materialById = materials.ToDictionary(material => material.MaterialId);
            if (materialById.Count != materials.Count)
            {
                throw InvalidSource();
            }

            var filesByBlockId = new Dictionary<string, List<LocalNoteServerSourceFile>>(StringComparer.Ordinal);
            var audioByRecordingId = new Dictionary<
                Guid,
                Dictionary<(int Segment, int ChunkIndex, long Samples), LocalNoteServerSourceFile>>();
            foreach (var file in source.Files)
            {
                if (!string.IsNullOrWhiteSpace(file.BlockId))
                {
                    if (!filesByBlockId.TryGetValue(file.BlockId, out var blockFiles))
                    {
                        blockFiles = [];
                        filesByBlockId.Add(file.BlockId, blockFiles);
                    }

                    blockFiles.Add(file);
                }

                if (file.Kind == "AudioChunk")
                {
                    if (!Guid.TryParse(file.RecordingId, out var recordingId)
                        || file.Segment is not { } segment
                        || file.ChunkIndex is not { } chunkIndex
                        || file.Samples is not { } samples)
                    {
                        throw InvalidSource();
                    }

                    if (!audioByRecordingId.TryGetValue(recordingId, out var chunks))
                    {
                        chunks = [];
                        audioByRecordingId.Add(recordingId, chunks);
                    }

                    if (!chunks.TryAdd((segment, chunkIndex, samples), file))
                    {
                        throw InvalidSource();
                    }
                }
            }

            if (source.Files.Select(file => file.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != source.Files.Count)
            {
                throw InvalidSource();
            }

            var noteFile = source.Files.Single(file => file.Kind == "Document");
            RequireInlineFile(noteFile, "note.json", source.NoteJson);
            var markdownFile = source.Files.Single(file => file.Kind == "Markdown");
            RequireInlineFile(markdownFile, "document.md", source.DocumentMarkdown);

            var manifestRecordingIds = new HashSet<Guid>();
            long inlineSourceBytes = 0;
            foreach (var file in source.Files.Where(file => file.Kind == "RecordingManifest"))
            {
                if (!Guid.TryParse(file.RecordingId, out var recordingId)
                    || file.InlineJson is null
                    || !manifestRecordingIds.Add(recordingId))
                {
                    throw InvalidSource();
                }

                RequireInlineFile(file, $"recordings/{recordingId:D}/manifest.json", file.InlineJson);
                inlineSourceBytes = checked(inlineSourceBytes + Encoding.UTF8.GetByteCount(file.InlineJson));
                if (inlineSourceBytes > MaxMetadataBytes)
                {
                    throw InvalidSource();
                }

                using var recordingJson = JsonDocument.Parse(file.InlineJson);
                if (!inputRecordings.Remove(recordingId, out var inputRecording)
                    || !JsonNode.DeepEquals(
                        JsonNode.Parse(inputRecording.GetRawText()),
                        JsonNode.Parse(recordingJson.RootElement.GetRawText()))
                    || ReadGuid(recordingJson.RootElement, "id") != recordingId
                    || ReadGuid(recordingJson.RootElement, "noteId") != source.SourceNoteId)
                {
                    throw InvalidSource();
                }

                if (!audioByRecordingId.TryGetValue(recordingId, out var audioFiles))
                {
                    audioFiles = [];
                }

                ValidateRecordingChunks(recordingJson.RootElement, audioFiles, recordingId, source.SourceNoteId);
            }

            if (inputRecordings.Count != 0)
            {
                throw InvalidSource();
            }

            var mappedMaterialIds = new HashSet<Guid>();
            var parsedBlocksById = parsed.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
            foreach (var block in source.Blocks)
            {
                var blockFiles = filesByBlockId.TryGetValue(block.Id, out var indexedBlockFiles)
                    ? indexedBlockFiles
                    : [];
                if (!parsedBlocksById.TryGetValue(block.Id, out var parsedBlock))
                {
                    throw InvalidSource();
                }

                switch (block.Kind)
                {
                    case "markdown":
                        if (block.MaterialIds.Count != 0 || blockFiles.Count != 0)
                        {
                            throw InvalidSource();
                        }

                        break;
                    case "photo":
                    {
                        var file = blockFiles.SingleOrDefault(item => item.Kind == "Photo");
                        if (file?.MaterialId is not { } photoId
                            || blockFiles.Count != 1
                            || block.MaterialIds.Count != 1
                            || block.MaterialIds[0] != photoId
                            || !Guid.TryParse(parsedBlock.AssetId, out var assetId)
                            || assetId != photoId)
                        {
                            throw InvalidSource();
                        }

                        if (!mappedMaterialIds.Add(photoId))
                        {
                            throw InvalidSource();
                        }

                        break;
                    }
                    case "ink":
                    {
                        var file = blockFiles.SingleOrDefault(item => item.Kind == "InkPreview");
                        if (file?.MaterialId is not { } previewId
                            || blockFiles.Count != 1
                            || block.MaterialIds.Count != 2
                            || block.MaterialIds[1] != previewId
                            || !materialById.TryGetValue(block.MaterialIds[0], out var vector)
                            || vector.Kind != LocalNoteMaterialKind.HandwritingSource
                            || !mappedMaterialIds.Add(block.MaterialIds[0]))
                        {
                            throw InvalidSource();
                        }

                        var expectedInk = CloudNoteMaterialService.ConvertServerInkToLocalSource(
                            parsedBlock.InkJson!,
                            parsedBlock.Width,
                            parsedBlock.Height);
                        var expectedInkBytes = Encoding.UTF8.GetBytes(expectedInk);
                        if (vector.Length != expectedInkBytes.LongLength
                            || !string.Equals(vector.ContentSha256, Hash(expectedInkBytes), StringComparison.Ordinal)
                            || vector.MediaType != "application/vnd.pim.ink+json")
                        {
                            throw InvalidSource();
                        }

                        if (!mappedMaterialIds.Add(previewId))
                        {
                            throw InvalidSource();
                        }

                        break;
                    }
                    default:
                        throw InvalidSource();
                }
            }

            var binaryMaterialIds = new HashSet<Guid>();
            foreach (var file in source.Files.Where(file => file.Kind is "Photo" or "InkPreview" or "AudioChunk"))
            {
                if (file.MaterialId is not { } materialId
                    || !materialById.TryGetValue(materialId, out var material)
                    || !binaryMaterialIds.Add(materialId)
                    || !string.Equals(material.ContentSha256, file.Sha256, StringComparison.Ordinal)
                    || material.Length != file.SizeBytes
                    || file.Kind != "AudioChunk" && !mappedMaterialIds.Contains(materialId)
                    || file.Kind == "AudioChunk" && !mappedMaterialIds.Add(materialId))
                {
                    throw InvalidSource();
                }

                var expectedKind = file.Kind switch
                {
                    "Photo" => LocalNoteMaterialKind.Photo,
                    "InkPreview" => LocalNoteMaterialKind.Handwriting,
                    _ => LocalNoteMaterialKind.RecordingSegment,
                };
                if (material.Kind != expectedKind
                    || (file.Kind is "Photo" or "InkPreview")
                        && (!parsedBlocksById.TryGetValue(file.BlockId!, out var fileBlock)
                            || material.MediaType != (file.Kind == "Photo" ? fileBlock.MimeType : "image/png")
                            || material.Width != ToPixel(fileBlock.Width)
                            || material.Height != ToPixel(fileBlock.Height))
                    || material.RecordingId?.ToString("D") != file.RecordingId
                    || material.RecordingSegment != file.Segment
                    || material.RecordingChunkIndex != file.ChunkIndex
                    || material.RecordingSamples != file.Samples)
                {
                    throw InvalidSource();
                }
            }

            var expectedMaterialIds = source.Blocks.SelectMany(block => block.MaterialIds)
                .Concat(source.Files.Where(file => file.Kind == "AudioChunk")
                    .Select(file => file.MaterialId ?? Guid.Empty))
                .ToHashSet();
            if (expectedMaterialIds.Contains(Guid.Empty)
                || expectedMaterialIds.Count != materials.Count
                || materials.Any(material => !expectedMaterialIds.Contains(material.MaterialId)))
            {
                throw InvalidSource();
            }
        }
        catch (LocalNoteStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or ArgumentException or KeyNotFoundException or InvalidDataException)
        {
            throw new LocalNoteStoreException("服务器笔记 source 与固定版本或材料映射不一致。", ex);
        }
    }

    private static Dictionary<Guid, JsonElement> ReadInputRecordings(JsonElement inputRoot)
    {
        var result = new Dictionary<Guid, JsonElement>();
        foreach (var recording in inputRoot.GetProperty("recordings").EnumerateArray())
        {
            var id = ReadGuid(recording, "id");
            if (!result.TryAdd(id, recording))
            {
                throw InvalidSource();
            }
        }

        return result;
    }

    private static void ValidateRecordingChunks(
        JsonElement recording,
        IReadOnlyDictionary<(int Segment, int ChunkIndex, long Samples), LocalNoteServerSourceFile> audioFiles,
        Guid recordingId,
        Guid sourceNoteId)
    {
        var chunks = recording.GetProperty("chunks");
        if (chunks.ValueKind != JsonValueKind.Array || chunks.GetArrayLength() == 0)
        {
            throw InvalidSource();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previousSegment = 0;
        var previousIndex = 0;
        foreach (var chunk in chunks.EnumerateArray())
        {
            var segment = chunk.GetProperty("segment").GetInt32();
            var index = chunk.GetProperty("index").GetInt32();
            var samples = chunk.GetProperty("samples").GetInt64();
            if (segment <= 0 || index <= 0 || samples <= 0
                || previousSegment == 0 && (segment != 1 || index != 1)
                || segment < previousSegment
                || segment == previousSegment && index != previousIndex + 1
                || segment > previousSegment && (segment != previousSegment + 1 || index != 1)
                || !audioFiles.TryGetValue((segment, index, samples), out var audioFile)
                || !seen.Add(audioFile.Path))
            {
                throw InvalidSource();
            }

            previousSegment = segment;
            previousIndex = index;
        }

        if (seen.Count != audioFiles.Count
            || recording.GetProperty("currentSegment").GetInt32() != previousSegment
            || !string.Equals(ReadString(recording, "phase"), "Completed", StringComparison.Ordinal)
            || ReadGuid(recording, "noteId") != sourceNoteId
            || recording.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw InvalidSource();
        }
    }

    private static void RequireInlineFile(LocalNoteServerSourceFile file, string path, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (file.Path != path
            || file.InlineJson != value
            || file.SizeBytes != bytes.LongLength
            || !string.Equals(file.Sha256, Hash(bytes), StringComparison.Ordinal))
        {
            throw InvalidSource();
        }
    }

    private static Guid ReadGuid(JsonElement element, string name)
    {
        var value = ReadString(element, name);
        return Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw InvalidSource();
    }

    private static int? ToPixel(double? value) => value is { } number
        ? checked((int)Math.Round(number, MidpointRounding.AwayFromZero))
        : null;

    private static string ReadString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw InvalidSource();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static LocalNoteStoreException InvalidSource() =>
        new("服务器笔记 source 与原始 note/input、块顺序、正文投影或材料清单不一致。");
}
