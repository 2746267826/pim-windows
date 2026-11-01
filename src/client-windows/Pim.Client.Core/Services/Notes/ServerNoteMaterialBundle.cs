using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

        var blocks = new List<JsonObject>();
        var markdownBlocks = new List<string>();
        var outputs = new List<ServerNoteMaterialFile>();
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
            blocks.Add(new JsonObject
            {
                ["id"] = blockId.ToString("D"),
                ["kind"] = "photo",
                ["assetId"] = assetId.ToString("D"),
                ["mimeType"] = photo.MediaType,
                ["sha256"] = photo.ContentSha256,
                ["width"] = photo.Width,
                ["height"] = photo.Height,
            });
            markdownBlocks.Add($"![照片]({path})");
            outputs.Add(new ServerNoteMaterialFile(
                path,
                "Photo",
                photo.ContentSha256,
                photo.Length,
                null,
                materialById[photo.MaterialId].AbsolutePath,
                blockId: blockId.ToString("D")));
        }

        BuildHandwritingBlocks(
            version,
            materialById,
            blocks,
            markdownBlocks,
            outputs);

        var recordings = new List<JsonObject>();
        var recordingMaterials = version.Materials
            .Where(material => material.Kind == LocalNoteMaterialKind.Recording)
            .OrderBy(material => material.AddedAt)
            .ThenBy(material => material.MaterialId)
            .ToArray();
        foreach (var material in recordingMaterials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = materialById[material.MaterialId].AbsolutePath;
            var samples = await ValidatePcm16WaveAsync(source, cancellationToken);
            var recordingId = material.MaterialId;
            var recordingJson = new JsonObject
            {
                ["id"] = recordingId.ToString("D"),
                ["noteId"] = note.NoteId.ToString("D"),
                ["schemaVersion"] = SchemaVersion,
                ["createdAtMillis"] = material.AddedAt.ToUnixTimeMilliseconds(),
                ["currentSegment"] = 1,
                ["phase"] = "Completed",
                ["chunks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["segment"] = 1,
                        ["index"] = 1,
                        ["samples"] = samples,
                    },
                },
            };
            var recordingJsonText = recordingJson.ToJsonString(JsonOptions);
            var prefix = $"recordings/{recordingId:D}";
            recordings.Add(recordingJson);
            outputs.Add(Inline(
                $"{prefix}/manifest.json",
                "RecordingManifest",
                recordingJsonText,
                recordingId: recordingId.ToString("D")));
            outputs.Add(new ServerNoteMaterialFile(
                $"{prefix}/s0001-c000001.wav",
                "AudioChunk",
                material.ContentSha256,
                material.Length,
                null,
                source,
                recordingId: recordingId.ToString("D"),
                segment: 1,
                chunkIndex: 1,
                samples: samples));
        }

        var unsupported = version.Materials
            .Where(material => material.Kind is not (
                LocalNoteMaterialKind.Photo
                or LocalNoteMaterialKind.Handwriting
                or LocalNoteMaterialKind.HandwritingSource
                or LocalNoteMaterialKind.Recording))
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new LocalNoteStoreException(
                $"服务器材料格式暂不支持：{string.Join("、", unsupported.Select(item => item.DisplayName))}");
        }

        blocks.Insert(0, new JsonObject
        {
            ["id"] = StableId("markdown-block", version.VersionId).ToString("D"),
            ["kind"] = "markdown",
            ["text"] = version.Markdown,
        });
        markdownBlocks.Insert(0, version.Markdown);

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

    private static void BuildHandwritingBlocks(
        LocalNoteVersion version,
        IReadOnlyDictionary<Guid, LocalNoteMaterialFile> materialById,
        List<JsonObject> blocks,
        List<string> markdownBlocks,
        List<ServerNoteMaterialFile> outputs)
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
            blocks.Add(new JsonObject
            {
                ["id"] = blockId.ToString("D"),
                ["kind"] = "ink",
                ["ink"] = JsonNode.Parse(inkRaw),
            });
            markdownBlocks.Add($"![手写卡片]({path})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->");
            outputs.Add(new ServerNoteMaterialFile(
                path,
                "InkPreview",
                preview.ContentSha256,
                preview.Length,
                null,
                materialById[preview.MaterialId].AbsolutePath,
                blockId: blockId.ToString("D")));
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

    private static async Task ValidateImageSignatureAsync(
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

    private static async Task<long> ValidatePcm16WaveAsync(
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
