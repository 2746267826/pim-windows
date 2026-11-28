using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pim.Client.Core.Services.Notes;

public sealed class ServerNoteMaterialFile
{
    private readonly byte[]? _inlineContent;
    private readonly string? _sourcePath;

    internal ServerNoteMaterialFile(
        string path,
        string kind,
        string sha256,
        long sizeBytes,
        byte[]? inlineContent,
        string? sourcePath,
        string? blockId = null,
        string? recordingId = null,
        int? segment = null,
        int? chunkIndex = null,
        long? samples = null)
    {
        Path = path;
        Kind = kind;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
        _inlineContent = inlineContent;
        _sourcePath = sourcePath;
        BlockId = blockId;
        RecordingId = recordingId;
        Segment = segment;
        ChunkIndex = chunkIndex;
        Samples = samples;
    }

    public string Path { get; }
    public string Kind { get; }
    public string Sha256 { get; }
    public long SizeBytes { get; }
    public string? BlockId { get; }
    public string? RecordingId { get; }
    public int? Segment { get; }
    public int? ChunkIndex { get; }
    public long? Samples { get; }

    public Stream OpenRead()
    {
        if (_inlineContent is not null)
        {
            return new MemoryStream(_inlineContent, writable: false);
        }

        if (_sourcePath is null)
        {
            throw new InvalidOperationException("材料文件没有可读取来源。");
        }

        return new FileStream(
            _sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}

public sealed record ServerNoteMaterialBundle(
    Guid NoteId,
    Guid VersionId,
    string InputJson,
    string ManifestJson,
    IReadOnlyList<ServerNoteMaterialFile> Files);

/// <summary>
/// Converts one immutable Windows note version to the server's schema-v1 material format.
/// This is deliberately separate from the local .pimnote.zip exchange package.
/// </summary>
public sealed class ServerNoteMaterialBundleBuilder
{
    private const int SchemaVersion = 1;
    private const int MaxMetadataCharacters = 8_388_608;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private static readonly Regex MaterialMarkerPattern = new(
        @"<!--\s*pim-material:(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*-->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record ServerBlockMaterial(
        Guid MarkerMaterialId,
        JsonObject Block,
        string MarkdownProjection,
        ServerNoteMaterialFile File);

    private readonly LocalNoteStore _store;

    public ServerNoteMaterialBundleBuilder(LocalNoteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ServerNoteMaterialBundle> BuildAsync(
        Guid noteId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        var note = await _store.LoadAsync(noteId, cancellationToken);
        var version = await _store.LoadVersionAsync(noteId, versionId, cancellationToken);
        var files = await _store.ResolveVersionMaterialsAsync(noteId, versionId, cancellationToken);
        return await BuildAsync(note, version, files, cancellationToken);
    }

    internal async Task<ServerNoteMaterialBundle> BuildAsync(
        LocalNoteDocument note,
        LocalNoteVersion version,
        IReadOnlyList<LocalNoteMaterialFile> materials,
        CancellationToken cancellationToken = default)
    {
        if (note.NoteId != version.NoteId || version.VersionId == Guid.Empty)
        {
            throw new LocalNoteStoreException("固定版本不属于当前笔记。");
        }
        if (version.Title.Length > 500)
        {
            throw new LocalNoteStoreException("笔记标题超过服务器 500 字符上限；本机版本未修改。");
        }

        var materialById = materials.ToDictionary(item => item.Material.MaterialId);
        var referenced = version.Materials.Select(material => material.MaterialId).ToHashSet();
        if (referenced.Count != version.Materials.Count
            || referenced.Count != materialById.Count
            || referenced.Any(id => !materialById.ContainsKey(id)))
        {
            throw new LocalNoteStoreException("固定版本材料快照不完整。");
        }

        await VerifyMaterialsAsync(version.Materials, materialById, cancellationToken);
        if (version.ServerSource is { } serverSource)
        {
            ServerNoteSourceIntegrity.Validate(
                serverSource,
                version.Title,
                version.Markdown,
                version.Materials);
            return BuildFromServerSource(note, version, serverSource, materialById);
        }

        var blockMaterialOrder = new List<ServerBlockMaterial>();
        var blockMaterials = new Dictionary<Guid, ServerBlockMaterial>();
        var photos = version.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.Photo)
            .OrderBy(material => material.AddedAt)
            .ThenBy(material => material.MaterialId)
            .ToArray();
        foreach (var photo in photos)
        {
            var extension = photo.MediaType switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/webp" => "webp",
                _ => throw new LocalNoteStoreException($"照片格式不能进入服务器材料版本：{photo.DisplayName}"),
            };
            var blockId = StableId("photo-block", version.VersionId, photo.MaterialId);
            var assetId = photo.MaterialId;
            var path = $"assets/{assetId:D}.{extension}";
            var block = new JsonObject
            {
                ["id"] = blockId.ToString("D"),
                ["kind"] = "photo",
                ["assetId"] = assetId.ToString("D"),
                ["mimeType"] = photo.MediaType,
                ["sha256"] = photo.ContentSha256,
                ["width"] = photo.Width,
                ["height"] = photo.Height,
            };
            var file = new ServerNoteMaterialFile(
                path,
                "Photo",
                photo.ContentSha256,
                photo.Length,
                null,
                materialById[photo.MaterialId].AbsolutePath,
                blockId: blockId.ToString("D"));
            var descriptor = new ServerBlockMaterial(
                photo.MaterialId,
                block,
                $"![照片]({path})",
                file);
            blockMaterialOrder.Add(descriptor);
            blockMaterials.Add(photo.MaterialId, descriptor);
        }

        BuildHandwritingBlockMaterials(
            version,
            materialById,
            blockMaterialOrder,
            blockMaterials);

        var blocks = new List<JsonObject>();
        var markdownBlocks = new List<string>();
        var outputs = new List<ServerNoteMaterialFile>();
        var markdownText = new StringBuilder();
        var emittedBlockMaterials = new HashSet<Guid>();
        var markdownBlockIndex = 0;

        void FlushMarkdown()
        {
            var text = markdownText.ToString();
            markdownText.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            blocks.Add(new JsonObject
            {
                ["id"] = MarkdownBlockId(version.VersionId, markdownBlockIndex++).ToString("D"),
                ["kind"] = "markdown",
                ["text"] = text,
            });
            markdownBlocks.Add(text);
        }

        void AppendBlockMaterial(ServerBlockMaterial descriptor)
        {
            FlushMarkdown();
            blocks.Add(descriptor.Block);
            markdownBlocks.Add(descriptor.MarkdownProjection);
            outputs.Add(descriptor.File);
        }

        var layout = NativeNoteDocumentLayout.Parse(version.Markdown, version.Materials);
        foreach (var segment in layout.Segments)
        {
            if (segment.Kind == NativeNoteDocumentSegmentKind.Material
                && segment.Material is { } material
                && (material.Kind is LocalNoteMaterialKind.Photo
                    or LocalNoteMaterialKind.Handwriting)
                && blockMaterials.TryGetValue(material.MaterialId, out var descriptor))
            {
                if (emittedBlockMaterials.Add(descriptor.MarkerMaterialId))
                {
                    AppendBlockMaterial(descriptor);
                }

                continue;
            }

            // Recordings have no server block kind. Their local anchors are
            // rewritten below to the stable restored recording-chunk identity.
            markdownText.Append(segment.Text);
        }

        FlushMarkdown();
        foreach (var descriptor in blockMaterialOrder)
        {
            if (emittedBlockMaterials.Add(descriptor.MarkerMaterialId))
            {
                blocks.Add(descriptor.Block);
                markdownBlocks.Add(descriptor.MarkdownProjection);
                outputs.Add(descriptor.File);
            }
        }

        var recordings = new List<JsonObject>();
        var recordingMarkerIds = new Dictionary<Guid, Guid>();
        var recordingMaterials = version.Materials
            .Where(material => material.Kind is LocalNoteMaterialKind.Recording or LocalNoteMaterialKind.RecordingSegment)
            .OrderBy(material => material.AddedAt)
            .ThenBy(material => material.RecordingId)
            .ThenBy(material => material.RecordingSegment ?? 1)
            .ThenBy(material => material.RecordingChunkIndex ?? 1)
            .ThenBy(material => material.MaterialId)
            .ToArray();
        foreach (var recordingGroup in recordingMaterials.GroupBy(
                     material => material.RecordingId ?? material.MaterialId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordingId = recordingGroup.Key;
            var chunks = new List<JsonObject>();
            var recordingFiles = new List<(
                LocalNoteMaterial Material,
                string Path,
                long Samples,
                int Segment,
                int ChunkIndex)>();
            var currentSegment = 1;
            foreach (var material in recordingGroup)
            {
                var source = materialById[material.MaterialId].AbsolutePath;
                var samples = await ValidatePcm16WaveAsync(source, cancellationToken);
                if (material.RecordingSamples is { } expectedSamples && expectedSamples != samples)
                {
                    throw new LocalNoteStoreException($"录音 WAV 样本数与固定清单不一致：{material.DisplayName}");
                }

                var segment = material.RecordingSegment ?? 1;
                var index = material.RecordingChunkIndex ?? (chunks.Count + 1);
                if (segment <= 0 || index <= 0
                    || (chunks.Count > 0 && (segment < currentSegment
                        || segment == currentSegment && index <= chunks[^1]["index"]!.GetValue<int>())))
                {
                    throw new LocalNoteStoreException($"录音分片顺序或索引无效：{material.DisplayName}");
                }

                currentSegment = segment;
                chunks.Add(new JsonObject
                {
                    ["segment"] = segment,
                    ["index"] = index,
                    ["samples"] = samples,
                });
                recordingFiles.Add((material, source, samples, segment, index));
            }

            var markerMaterialId = ServerNoteMaterialStableId.ForRecordingChunk(
                recordingId,
                recordingFiles[0].Segment,
                recordingFiles[0].ChunkIndex,
                recordingFiles[0].Samples);
            foreach (var material in recordingGroup)
            {
                recordingMarkerIds[material.MaterialId] = markerMaterialId;
            }

            var recordingJson = new JsonObject
            {
                ["id"] = recordingId.ToString("D"),
                ["noteId"] = note.NoteId.ToString("D"),
                ["schemaVersion"] = SchemaVersion,
                ["createdAtMillis"] = recordingGroup.Min(material => material.AddedAt).ToUnixTimeMilliseconds(),
                ["currentSegment"] = currentSegment,
                ["phase"] = "Completed",
                ["chunks"] = new JsonArray(chunks.Cast<JsonNode>().ToArray()),
            };
            var recordingJsonText = recordingJson.ToJsonString(JsonOptions);
            var prefix = $"recordings/{recordingId:D}";
            recordings.Add(recordingJson);
            outputs.Add(Inline(
                $"{prefix}/manifest.json",
                "RecordingManifest",
                recordingJsonText,
                recordingId: recordingId.ToString("D")));
            foreach (var (material, source, samples, segment, index) in recordingFiles)
            {
                outputs.Add(new ServerNoteMaterialFile(
                    $"{prefix}/s{segment:D4}-c{index:D6}.wav",
                    "AudioChunk",
                    material.ContentSha256,
                    material.Length,
                    null,
                    source,
                    recordingId: recordingId.ToString("D"),
                    segment: segment,
                    chunkIndex: index,
                    samples: samples));
            }
        }

        var unsupported = version.Materials
            .Where(material => material.Kind is not (
                LocalNoteMaterialKind.Photo
                or LocalNoteMaterialKind.Handwriting
                or LocalNoteMaterialKind.HandwritingSource
                or LocalNoteMaterialKind.Recording
                or LocalNoteMaterialKind.RecordingSegment))
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new LocalNoteStoreException(
                $"服务器材料格式暂不支持：{string.Join("、", unsupported.Select(item => item.DisplayName))}");
        }

        if (!blocks.Any(block => string.Equals(
                block["kind"]?.GetValue<string>(),
                "markdown",
                StringComparison.Ordinal)))
        {
            blocks.Insert(0, new JsonObject
            {
                ["id"] = MarkdownBlockId(version.VersionId, 0).ToString("D"),
                ["kind"] = "markdown",
                ["text"] = string.Empty,
            });
            markdownBlocks.Insert(0, string.Empty);
        }

        RewriteRecordingMarkers(blocks, markdownBlocks, recordingMarkerIds);

        var noteJson = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = note.NoteId.ToString("D"),
            ["revision"] = version.CreatedAt.ToUnixTimeMilliseconds(),
            ["createdAtMillis"] = note.CreatedAt.ToUnixTimeMilliseconds(),
            ["title"] = version.Title,
            ["blocks"] = new JsonArray(blocks.ToArray()),
        }.ToJsonString(JsonOptions);
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var documentMarkdown = string.Join("\n\n", markdownBlocks);
        outputs.Insert(0, Inline("document.md", "Markdown", documentMarkdown));
        outputs.Insert(0, new ServerNoteMaterialFile(
            "note.json",
            "Document",
            Sha256(noteBytes),
            noteBytes.Length,
            noteBytes,
            null));

        var inputJson = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = version.VersionId.ToString("D"),
            ["createdAtMillis"] = version.CreatedAt.ToUnixTimeMilliseconds(),
            ["note"] = JsonNode.Parse(noteJson),
            ["recordings"] = new JsonArray(recordings.ToArray()),
        }.ToJsonString(JsonOptions);
        var manifestJson = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["versionId"] = version.VersionId.ToString("D"),
            ["noteId"] = note.NoteId.ToString("D"),
            ["revision"] = version.CreatedAt.ToUnixTimeMilliseconds(),
            ["inputSha256"] = Sha256(Encoding.UTF8.GetBytes(inputJson)),
            ["files"] = new JsonArray(outputs.Select(file => (JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["kind"] = file.Kind,
                ["sha256"] = file.Sha256,
                ["sizeBytes"] = file.SizeBytes,
                ["blockId"] = file.BlockId,
                ["recordingId"] = file.RecordingId,
                ["segment"] = file.Segment,
                ["chunkIndex"] = file.ChunkIndex,
                ["samples"] = file.Samples,
            }).ToArray()),
        }.ToJsonString(JsonOptions);
        if (inputJson.Length > MaxMetadataCharacters || manifestJson.Length > MaxMetadataCharacters)
        {
            throw new LocalNoteStoreException("服务器材料元数据超过 8 MiB 上限；本机版本未修改。");
        }

        if (outputs.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
        {
            throw new LocalNoteStoreException("服务器材料包存在重复路径；本机版本未修改。");
        }

        return new ServerNoteMaterialBundle(
            note.NoteId,
            version.VersionId,
            inputJson,
            manifestJson,
            outputs);
    }

    private static ServerNoteMaterialBundle BuildFromServerSource(
        LocalNoteDocument note,
        LocalNoteVersion version,
        LocalNoteServerSource source,
        IReadOnlyDictionary<Guid, LocalNoteMaterialFile> materialById)
    {
        if (!string.Equals(source.DocumentMarkdown, version.Markdown, StringComparison.Ordinal)
            || source.Files.Count > 100_000
            || source.Blocks.Count == 0
            || source.Blocks.Count > 100_000)
        {
            throw new LocalNoteStoreException("服务器混合材料 source 与固定版本不一致。");
        }

        ServerNoteSourceIntegrity.Validate(source, version.Title, version.Markdown, version.Materials);

        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new JsonArray();
        foreach (var sourceBlock in source.Blocks)
        {
            if (!blockIds.Add(sourceBlock.Id))
            {
                throw new LocalNoteStoreException("服务器混合材料 source 包含重复块身份。");
            }

            using var raw = JsonDocument.Parse(sourceBlock.RawJson);
            if (!string.Equals(
                    raw.RootElement.GetProperty("id").GetString(),
                    sourceBlock.Id,
                    StringComparison.Ordinal)
                || !string.Equals(
                    raw.RootElement.GetProperty("kind").GetString(),
                    sourceBlock.Kind,
                    StringComparison.Ordinal)
                || sourceBlock.MaterialIds.Any(id => !materialById.ContainsKey(id)))
            {
                throw new LocalNoteStoreException("服务器混合材料块身份或材料映射已损坏。");
            }

            blocks.Add(JsonNode.Parse(sourceBlock.RawJson)
                ?? throw new LocalNoteStoreException("服务器混合材料块 JSON 无效。"));
        }

        var noteNode = JsonNode.Parse(source.NoteJson) as JsonObject
            ?? throw new LocalNoteStoreException("服务器混合材料 note.json 无效。");
        var originalNoteBytes = Encoding.UTF8.GetBytes(source.NoteJson);
        var originalNoteFile = source.Files.SingleOrDefault(file => file.Kind == "Document");
        if (originalNoteFile is null
            || originalNoteFile.Path != "note.json"
            || originalNoteFile.InlineJson != source.NoteJson
            || originalNoteFile.SizeBytes != originalNoteBytes.LongLength
            || !string.Equals(originalNoteFile.Sha256, Sha256(originalNoteBytes), StringComparison.Ordinal)
            || Guid.Parse(noteNode["id"]!.GetValue<string>()) != source.SourceNoteId)
        {
            throw new LocalNoteStoreException("服务器混合材料 note.json source 摘要或原始身份无效。");
        }

        var originalInputNode = JsonNode.Parse(source.InputJson) as JsonObject
            ?? throw new LocalNoteStoreException("服务器混合材料 input.json 无效。");
        if (Guid.Parse(originalInputNode["id"]!.GetValue<string>()) != source.SourceVersionId
            || !JsonNode.DeepEquals(originalInputNode["note"], noteNode))
        {
            throw new LocalNoteStoreException("服务器混合材料 input 与原始 note source 不一致。");
        }

        noteNode["id"] = note.NoteId.ToString("D");
        noteNode["revision"] = version.CreatedAt.ToUnixTimeMilliseconds();
        noteNode["title"] = version.Title;
        noteNode["blocks"] = blocks;
        var noteJson = noteNode.ToJsonString(JsonOptions);
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);

        var recordingJsonById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var file in source.Files.Where(file => file.Kind == "RecordingManifest"))
        {
            if (string.IsNullOrWhiteSpace(file.RecordingId)
                || string.IsNullOrWhiteSpace(file.InlineJson)
                || !recordingJsonById.TryAdd(file.RecordingId, ParseRecordingManifest(file.InlineJson, note.NoteId)))
            {
                throw new LocalNoteStoreException("服务器录音 source manifest 缺失或重复。");
            }
        }

        var inputNode = originalInputNode;
        var recordingIds = inputNode["recordings"] as JsonArray
            ?? throw new LocalNoteStoreException("服务器混合材料 input 缺少 recordings 数组。");
        var recordings = new JsonArray();
        foreach (var oldRecording in recordingIds)
        {
            var id = oldRecording?["id"]?.GetValue<string>();
            if (id is null || !recordingJsonById.Remove(id, out var recording))
            {
                throw new LocalNoteStoreException("服务器录音 source 与 input 清单不一致。");
            }

            recordings.Add(recording);
        }

        if (recordingJsonById.Count != 0)
        {
            throw new LocalNoteStoreException("服务器材料包含 input 未引用的录音 manifest。");
        }

        inputNode["id"] = version.VersionId.ToString("D");
        inputNode["createdAtMillis"] = version.CreatedAt.ToUnixTimeMilliseconds();
        inputNode["note"] = JsonNode.Parse(noteJson);
        inputNode["recordings"] = recordings;
        var inputJson = inputNode.ToJsonString(JsonOptions);
        var outputs = new List<ServerNoteMaterialFile>(source.Files.Count);
        foreach (var file in source.Files)
        {
            switch (file.Kind)
            {
                case "Document":
                    if (file.Path != "note.json")
                    {
                        throw new LocalNoteStoreException("服务器 document 文件路径无效。");
                    }

                    outputs.Add(new ServerNoteMaterialFile(
                        file.Path, file.Kind, Sha256(noteBytes), noteBytes.Length, noteBytes, null));
                    break;
                case "Markdown":
                    if (file.Path != "document.md"
                        || file.InlineJson != source.DocumentMarkdown
                        || file.SizeBytes != Encoding.UTF8.GetByteCount(source.DocumentMarkdown)
                        || !string.Equals(
                            file.Sha256,
                            Sha256(Encoding.UTF8.GetBytes(source.DocumentMarkdown)),
                            StringComparison.Ordinal))
                    {
                        throw new LocalNoteStoreException("服务器 Markdown 文件路径无效。");
                    }

                    outputs.Add(Inline(file.Path, file.Kind, source.DocumentMarkdown));
                    break;
                case "RecordingManifest":
                    if (file.InlineJson is null
                        || file.SizeBytes != Encoding.UTF8.GetByteCount(file.InlineJson)
                        || !string.Equals(
                            file.Sha256,
                            Sha256(Encoding.UTF8.GetBytes(file.InlineJson)),
                            StringComparison.Ordinal))
                    {
                        throw new LocalNoteStoreException("服务器录音 manifest source 摘要无效。");
                    }

                    var recording = inputNode["recordings"]!.AsArray()
                        .FirstOrDefault(item => string.Equals(
                            item?["id"]?.GetValue<string>(), file.RecordingId, StringComparison.Ordinal)) as JsonObject;
                    if (recording is null)
                    {
                        throw new LocalNoteStoreException("服务器录音 manifest 缺少 input 对应项。");
                    }

                    outputs.Add(Inline(
                        file.Path,
                        file.Kind,
                        recording.ToJsonString(JsonOptions),
                        file.RecordingId));
                    break;
                case "Photo":
                case "InkPreview":
                case "AudioChunk":
                    if (file.MaterialId is not { } materialId
                        || !materialById.TryGetValue(materialId, out var localFile)
                        || !string.Equals(
                            localFile.Material.ContentSha256,
                            file.Sha256,
                            StringComparison.OrdinalIgnoreCase)
                        || localFile.Material.Length != file.SizeBytes)
                    {
                        throw new LocalNoteStoreException($"服务器材料 source 映射缺失或摘要不一致：{file.Path}");
                    }

                    outputs.Add(new ServerNoteMaterialFile(
                        file.Path,
                        file.Kind,
                        localFile.Material.ContentSha256,
                        localFile.Material.Length,
                        null,
                        localFile.AbsolutePath,
                        file.BlockId,
                        file.RecordingId,
                        file.Segment,
                        file.ChunkIndex,
                        file.Samples));
                    break;
                default:
                    throw new LocalNoteStoreException($"服务器 source 文件类型不受支持：{file.Kind}");
            }
        }

        if (outputs.Count != source.Files.Count
            || outputs.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
        {
            throw new LocalNoteStoreException("服务器混合材料 source 路径重复或不完整。");
        }

        var manifestJson = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["versionId"] = version.VersionId.ToString("D"),
            ["noteId"] = note.NoteId.ToString("D"),
            ["revision"] = version.CreatedAt.ToUnixTimeMilliseconds(),
            ["inputSha256"] = Sha256(Encoding.UTF8.GetBytes(inputJson)),
            ["files"] = new JsonArray(outputs.Select(file => (JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["kind"] = file.Kind,
                ["sha256"] = file.Sha256,
                ["sizeBytes"] = file.SizeBytes,
                ["blockId"] = file.BlockId,
                ["recordingId"] = file.RecordingId,
                ["segment"] = file.Segment,
                ["chunkIndex"] = file.ChunkIndex,
                ["samples"] = file.Samples,
            }).ToArray()),
        }.ToJsonString(JsonOptions);
        if (inputJson.Length > MaxMetadataCharacters || manifestJson.Length > MaxMetadataCharacters)
        {
            throw new LocalNoteStoreException("服务器材料元数据超过 8 MiB 上限。");
        }

        return new ServerNoteMaterialBundle(note.NoteId, version.VersionId, inputJson, manifestJson, outputs);
    }

    private static JsonObject ParseRecordingManifest(string json, Guid noteId)
    {
        var recording = JsonNode.Parse(json) as JsonObject
            ?? throw new LocalNoteStoreException("服务器录音 manifest 无效。");
        recording["noteId"] = noteId.ToString("D");
        return recording;
    }

    private static void RewriteRecordingMarkers(
        List<JsonObject> blocks,
        List<string> markdownBlocks,
        IReadOnlyDictionary<Guid, Guid> recordingMarkerIds)
    {
        if (recordingMarkerIds.Count == 0)
        {
            return;
        }

        var seenStableIds = new HashSet<Guid>();
        for (var index = 0; index < blocks.Count; index++)
        {
            if (!string.Equals(
                    blocks[index]["kind"]?.GetValue<string>(),
                    "markdown",
                    StringComparison.Ordinal)
                || blocks[index]["text"]?.GetValue<string>() is not { } text)
            {
                continue;
            }

            var rewritten = MaterialMarkerPattern.Replace(text, match =>
            {
                if (!Guid.TryParse(match.Groups["id"].Value, out var materialId)
                    || !recordingMarkerIds.TryGetValue(materialId, out var stableId))
                {
                    return match.Value;
                }

                return seenStableIds.Add(stableId)
                    ? NativeNoteDocumentLayout.CreateMarker(stableId)
                    : string.Empty;
            });
            if (string.Equals(rewritten, text, StringComparison.Ordinal))
            {
                continue;
            }

            blocks[index]["text"] = rewritten;
            markdownBlocks[index] = rewritten;
        }
    }

    private static void BuildHandwritingBlockMaterials(
        LocalNoteVersion version,
        IReadOnlyDictionary<Guid, LocalNoteMaterialFile> materialById,
        List<ServerBlockMaterial> blockMaterialOrder,
        Dictionary<Guid, ServerBlockMaterial> blockMaterials)
    {
        var previews = version.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.Handwriting)
            .OrderBy(material => material.AddedAt)
            .ThenBy(material => material.MaterialId)
            .ToArray();
        var sources = version.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.HandwritingSource)
            .OrderBy(material => material.AddedAt)
            .ThenBy(material => material.MaterialId)
            .ToArray();
        if (previews.Length != sources.Length)
        {
            throw new LocalNoteStoreException("手写预览与可编辑墨迹源数量不一致。");
        }

        var used = new HashSet<Guid>();
        foreach (var preview in previews)
        {
            var stem = RemoveKnownExtension(preview.DisplayName, ".png");
            var candidates = sources.Where(source =>
                    !used.Contains(source.MaterialId)
                    && source.Width == preview.Width
                    && source.Height == preview.Height
                    && string.Equals(
                        RemoveKnownExtension(source.DisplayName, ".ink.json"),
                        stem,
                        StringComparison.Ordinal))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new LocalNoteStoreException($"无法唯一配对可编辑手写源：{preview.DisplayName}");
            }

            var source = candidates[0];
            used.Add(source.MaterialId);
            var raw = ReadUtf8FileBounded(
                materialById[source.MaterialId].AbsolutePath,
                16 * 1024 * 1024);
            var ink = ConvertInkDocument(raw, preview, source);
            var inkRaw = ink.ToJsonString(JsonOptions);
            var inkHash = Sha256(Encoding.UTF8.GetBytes(inkRaw));
            var blockId = StableId("ink-block", version.VersionId, preview.MaterialId);
            var path = $"assets/{blockId:D}-{inkHash}.png";
            var block = new JsonObject
            {
                ["id"] = blockId.ToString("D"),
                ["kind"] = "ink",
                ["ink"] = JsonNode.Parse(inkRaw),
            };
            var file = new ServerNoteMaterialFile(
                path,
                "InkPreview",
                preview.ContentSha256,
                preview.Length,
                null,
                materialById[preview.MaterialId].AbsolutePath,
                blockId: blockId.ToString("D"));
            var descriptor = new ServerBlockMaterial(
                preview.MaterialId,
                block,
                $"![手写卡片]({path})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->",
                file);
            blockMaterialOrder.Add(descriptor);
            blockMaterials.Add(preview.MaterialId, descriptor);
        }
    }

    private static JsonObject ConvertInkDocument(
        string raw,
        LocalNoteMaterial preview,
        LocalNoteMaterial source)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "SchemaVersion");
            var width = Number(root, "width", "Width");
            var height = Number(root, "height", "Height");
            if (schemaVersion != SchemaVersion
                || width is < 80 or > 2048
                || height is < 80 or > 4096
                || (preview.Width is { } previewWidth && Math.Abs(previewWidth - width) > 1)
                || (preview.Height is { } previewHeight && Math.Abs(previewHeight - height) > 1)
                || (source.Width is { } sourceWidth && Math.Abs(sourceWidth - width) > 1)
                || (source.Height is { } sourceHeight && Math.Abs(sourceHeight - height) > 1))
            {
                throw new LocalNoteStoreException($"手写源尺寸或版本无效：{source.DisplayName}");
            }

            var strokes = new JsonArray();
            var rawStrokes = Property(root, "strokes", "Strokes");
            if (rawStrokes.ValueKind != JsonValueKind.Array || rawStrokes.GetArrayLength() == 0)
            {
                throw new LocalNoteStoreException($"手写源没有笔迹：{source.DisplayName}");
            }

            foreach (var rawStroke in rawStrokes.EnumerateArray())
            {
                var points = new JsonArray();
                foreach (var point in rawStroke.EnumerateArray())
                {
                    var x = Number(point, "x", "X");
                    var y = Number(point, "y", "Y");
                    var pressure = Number(point, "pressure", "Pressure");
                    if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(pressure)
                        || x < 0 || x > width || y < 0 || y > height
                        || pressure is < 0 or > 1)
                    {
                        throw new LocalNoteStoreException($"手写笔迹超出固定画布：{source.DisplayName}");
                    }

                    points.Add(new JsonObject
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["pressure"] = pressure,
                    });
                }

                if (points.Count == 0)
                {
                    throw new LocalNoteStoreException($"手写笔迹包含空笔画：{source.DisplayName}");
                }

                strokes.Add(new JsonObject
                {
                    ["width"] = 2.0,
                    ["points"] = points,
                });
            }

            return new JsonObject
            {
                ["width"] = width,
                ["height"] = height,
                ["strokes"] = strokes,
            };
        }
        catch (JsonException ex)
        {
            throw new LocalNoteStoreException($"手写源 JSON 损坏：{source.DisplayName}", ex);
        }
    }

    private static async Task VerifyMaterialsAsync(
        IReadOnlyList<LocalNoteMaterial> materials,
        IReadOnlyDictionary<Guid, LocalNoteMaterialFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var material in materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[material.MaterialId];
            await using var stream = new FileStream(
                file.AbsolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != material.Length)
            {
                throw new LocalNoteStoreException($"固定版本材料长度已变化：{material.DisplayName}");
            }

            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!string.Equals(hash, material.ContentSha256, StringComparison.Ordinal))
            {
                throw new LocalNoteStoreException($"固定版本材料摘要已变化：{material.DisplayName}");
            }

            if (material.Kind is LocalNoteMaterialKind.Photo or LocalNoteMaterialKind.Handwriting)
            {
                await ValidateImageSignatureAsync(stream, file.AbsolutePath, material.MediaType, cancellationToken);
            }
        }
    }

    internal static async Task ValidateImageSignatureAsync(
        Stream stream,
        string path,
        string mediaType,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var header = new byte[12];
        var read = await stream.ReadAsync(header, cancellationToken);
        var valid = mediaType switch
        {
            "image/png" => read >= 8
                && header.AsSpan(0, 8).SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => read >= 3
                && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            "image/webp" => read >= 12
                && header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
        if (!valid)
        {
            throw new LocalNoteStoreException($"图片文件签名与类型不一致：{Path.GetFileName(path)}");
        }
    }

    internal static async Task<long> ValidatePcm16WaveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var header = new byte[44];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            44,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < header.Length)
        {
            throw new LocalNoteStoreException($"录音 WAV 头部不完整：{Path.GetFileName(path)}");
        }

        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !header.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != stream.Length - 8
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)) != 1
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) != 16_000
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)) != 32_000
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)) != 2
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 16
            || !header.AsSpan(36, 4).SequenceEqual("data"u8))
        {
            throw new LocalNoteStoreException(
                $"录音必须是 16 kHz、单声道、16 位 PCM WAV：{Path.GetFileName(path)}");
        }

        var dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40));
        if (dataBytes != stream.Length - 44 || dataBytes == 0 || dataBytes % 2 != 0)
        {
            throw new LocalNoteStoreException(
                $"录音 WAV 长度与样本数不一致：{Path.GetFileName(path)}");
        }

        return dataBytes / 2;
    }

    private static ServerNoteMaterialFile Inline(
        string path,
        string kind,
        string text,
        string? recordingId = null)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new ServerNoteMaterialFile(
            path,
            kind,
            Sha256(bytes),
            bytes.Length,
            bytes,
            null,
            recordingId: recordingId);
    }

    private static string ReadUtf8FileBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new LocalNoteStoreException("手写源超过 16 MiB 上限。");
        }

        using var output = new MemoryStream((int)stream.Length);
        stream.CopyTo(output);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new LocalNoteStoreException("手写源不是有效 UTF-8。", ex);
        }
    }

    private static Guid StableId(string purpose, params Guid[] ids)
    {
        var input = purpose + ":" + string.Join(":", ids.Select(id => id.ToString("D")));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static Guid MarkdownBlockId(Guid versionId, int index)
    {
        if (index == 0)
        {
            return StableId("markdown-block", versionId);
        }

        return StableId(
            "markdown-block",
            versionId,
            new Guid(index, 0, 0, new byte[8]));
    }

    private static string RemoveKnownExtension(string value, string extension)
        => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? value[..^extension.Length]
            : value;

    private static JsonElement Property(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            if (node.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        throw new JsonException($"缺少属性 {string.Join("/", names)}。");
    }

    private static int Int(JsonElement node, params string[] names)
        => Property(node, names).GetInt32();

    private static double Number(JsonElement node, params string[] names)
        => Property(node, names).GetDouble();

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
