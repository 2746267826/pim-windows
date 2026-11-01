using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pim.Client.Core.Services.Notes;

public sealed record ServerNoteMaterialRestoreBlock(
    string Id,
    string Kind,
    string? AssetId,
    string? MimeType,
    string? Sha256,
    string? Text,
    string? InkJson,
    double? Width,
    double? Height);

public sealed record ServerNoteMaterialRestoreFile(
    string Path,
    string Kind,
    string Sha256,
    long SizeBytes,
    string? BlockId,
    string? RecordingId,
    int? Segment,
    int? ChunkIndex,
    long? Samples);

public sealed record ServerNoteMaterialRestorePackage(
    Guid NoteId,
    Guid VersionId,
    string Title,
    string Markdown,
    IReadOnlyList<ServerNoteMaterialRestoreBlock> Blocks,
    IReadOnlyList<ServerNoteMaterialRestoreFile> Files);

/// <summary>
/// Reads the server's schema-v1 input.json/manifest.json pair and checks the
/// immutable metadata relationships before any bytes are copied into the local
/// library. The manifest is an index, not a source of trust for file bytes.
/// </summary>
public static class ServerNoteMaterialRestoreParser
{
    private const int MaxMetadataCharacters = 8_388_608;
    private const int MaxDepth = 64;
    private static readonly HashSet<string> AllowedKinds =
    [
        "Document",
        "Markdown",
        "Photo",
        "InkPreview",
        "RecordingManifest",
        "AudioChunk",
    ];

    public static ServerNoteMaterialRestorePackage Parse(
        string inputJson,
        string manifestJson,
        string? markdown = null)
    {
        if (string.IsNullOrWhiteSpace(inputJson)
            || string.IsNullOrWhiteSpace(manifestJson)
            || Encoding.UTF8.GetByteCount(inputJson) > MaxMetadataCharacters
            || Encoding.UTF8.GetByteCount(manifestJson) > MaxMetadataCharacters)
        {
            throw new LocalNoteStoreException("服务器材料元数据为空或超过 8 MiB 上限。");
        }

        try
        {
            using var input = JsonDocument.Parse(inputJson, new JsonDocumentOptions { MaxDepth = MaxDepth });
            using var manifest = JsonDocument.Parse(
                manifestJson,
                new JsonDocumentOptions { MaxDepth = MaxDepth });
            var inputRoot = input.RootElement;
            var manifestRoot = manifest.RootElement;

            RequireInt(inputRoot, 1, "schemaVersion", "SchemaVersion");
            RequireInt(manifestRoot, 1, "schemaVersion", "SchemaVersion");
            var versionId = RequireGuid(inputRoot, "id", "Id");
            var note = RequiredObject(inputRoot, "note", "Note");
            var noteId = RequireGuid(note, "id", "Id");
            if (noteId != RequireGuid(manifestRoot, "noteId", "NoteId")
                || versionId != RequireGuid(manifestRoot, "versionId", "VersionId"))
            {
                throw new LocalNoteStoreException("服务器材料 input 与 manifest 的笔记或版本身份不一致。");
            }

            var inputSha = RequireSha256(manifestRoot, "inputSha256", "InputSha256");
            var actualInputSha = Hash(Encoding.UTF8.GetBytes(inputJson));
            if (!string.Equals(inputSha, actualInputSha, StringComparison.Ordinal))
            {
                throw new LocalNoteStoreException("服务器材料 inputJson 的 SHA-256 与 manifest 不一致。");
            }

            var title = RequiredString(note, 500, "title", "Title");
            var blocks = ParseBlocks(note);
            var files = ParseFiles(manifestRoot);
            ValidateReferences(blocks, files, markdown);
            return new ServerNoteMaterialRestorePackage(
                noteId,
                versionId,
                title,
                blocks.Single(block => block.Kind == "markdown").Text!,
                blocks,
                files);
        }
        catch (LocalNoteStoreException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new LocalNoteStoreException("服务器材料 schema-v1 元数据无效。", ex);
        }
    }

    private static IReadOnlyList<ServerNoteMaterialRestoreBlock> ParseBlocks(JsonElement note)
    {
        var rawBlocks = RequiredArray(note, "blocks", "Blocks");
        if (rawBlocks.GetArrayLength() == 0 || rawBlocks.GetArrayLength() > 100_000)
        {
            throw new LocalNoteStoreException("服务器笔记块数量无效。");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var blocks = new List<ServerNoteMaterialRestoreBlock>(rawBlocks.GetArrayLength());
        foreach (var raw in rawBlocks.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                throw new LocalNoteStoreException("服务器笔记包含非对象块。");
            }

            var id = RequiredString(raw, 128, "id", "Id");
            if (!Guid.TryParse(id, out _) || !ids.Add(id))
            {
                throw new LocalNoteStoreException("服务器笔记块标识为空、重复或不是 GUID。");
            }

            var kind = RequiredString(raw, 32, "kind", "Kind");
            if (kind is not ("markdown" or "photo" or "ink"))
            {
                throw new LocalNoteStoreException($"服务器笔记块类型不受支持：{kind}");
            }

            if (kind == "markdown")
            {
                blocks.Add(new ServerNoteMaterialRestoreBlock(
                    id,
                    kind,
                    null,
                    null,
                    null,
                    RequiredString(raw, null, "text", "Text"),
                    null,
                    null,
                    null));
                continue;
            }

            if (kind == "photo")
            {
                var assetId = RequireGuid(raw, "assetId", "AssetId").ToString("D");
                var mimeType = RequiredString(raw, 128, "mimeType", "MimeType");
                if (mimeType is not ("image/png" or "image/jpeg" or "image/webp"))
                {
                    throw new LocalNoteStoreException($"服务器照片格式不受支持：{mimeType}");
                }

                blocks.Add(new ServerNoteMaterialRestoreBlock(
                    id,
                    kind,
                    assetId,
                    mimeType,
                    RequireSha256(raw, "sha256", "Sha256"),
                    null,
                    null,
                    OptionalNumber(raw, "width", "Width"),
                    OptionalNumber(raw, "height", "Height")));
                continue;
            }

            var ink = RequiredObject(raw, "ink", "Ink");
            var width = Number(ink, "width", "Width");
            var height = Number(ink, "height", "Height");
            if (width is < 80 or > 2048 || height is < 80 or > 4096)
            {
                throw new LocalNoteStoreException("服务器手写块尺寸无效。");
            }

            blocks.Add(new ServerNoteMaterialRestoreBlock(
                id,
                kind,
                null,
                "application/vnd.pim.ink+json",
                null,
                null,
                ink.GetRawText(),
                width,
                height));
        }

        return blocks;
    }

    private static IReadOnlyList<ServerNoteMaterialRestoreFile> ParseFiles(JsonElement manifest)
    {
        var rawFiles = RequiredArray(manifest, "files", "Files");
        if (rawFiles.GetArrayLength() is < 2 or > 100_000)
        {
            throw new LocalNoteStoreException("服务器材料文件清单数量无效。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ServerNoteMaterialRestoreFile>(rawFiles.GetArrayLength());
        foreach (var raw in rawFiles.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object)
            {
                throw new LocalNoteStoreException("服务器材料清单包含非对象文件项。");
            }

            var path = RequiredString(raw, 2048, "path", "Path");
            if (!IsSafeRelativePath(path) || !paths.Add(path))
            {
                throw new LocalNoteStoreException($"服务器材料路径无效或重复：{path}");
            }

            var kind = RequiredString(raw, 64, "kind", "Kind");
            if (!AllowedKinds.Contains(kind))
            {
                throw new LocalNoteStoreException($"服务器材料类型不受支持：{kind}");
            }

            var sha = RequireSha256(raw, "sha256", "Sha256");
            var size = RequireNonNegativeInt64(raw, "sizeBytes", "SizeBytes");
            if (kind is not ("Document" or "Markdown") && size <= 0)
            {
                throw new LocalNoteStoreException($"服务器材料文件为空：{path}");
            }
            files.Add(new ServerNoteMaterialRestoreFile(
                path,
                kind,
                sha,
                size,
                OptionalString(raw, "blockId", "BlockId"),
                OptionalString(raw, "recordingId", "RecordingId"),
                OptionalInt32(raw, "segment", "Segment"),
                OptionalInt32(raw, "chunkIndex", "ChunkIndex"),
                OptionalInt64(raw, "samples", "Samples")));
        }

        var noteFiles = files.Where(file => file.Path == "note.json").ToArray();
        var markdownFiles = files.Where(file => file.Path == "document.md").ToArray();
        if (noteFiles.Length != 1
            || markdownFiles.Length != 1
            || noteFiles[0].Kind != "Document"
            || markdownFiles[0].Kind != "Markdown")
        {
            throw new LocalNoteStoreException("服务器材料清单缺少唯一的 note.json/document.md。");
        }

        return files;
    }

    private static void ValidateReferences(
        IReadOnlyList<ServerNoteMaterialRestoreBlock> blocks,
        IReadOnlyList<ServerNoteMaterialRestoreFile> files,
        string? markdown)
    {
        var markdownBlocks = blocks.Where(block => block.Kind == "markdown").ToArray();
        if (markdownBlocks.Length != 1)
        {
            throw new LocalNoteStoreException("服务器 note.json 必须包含唯一 Markdown 正文块。");
        }

        var byBlock = files
            .Where(file => !string.IsNullOrWhiteSpace(file.BlockId))
            .GroupBy(file => file.BlockId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var block in blocks.Where(block => block.Kind is "photo" or "ink"))
        {
            if (!byBlock.TryGetValue(block.Id, out var matches) || matches.Length != 1)
            {
                throw new LocalNoteStoreException($"服务器笔记块缺少唯一的材料文件：{block.Id}");
            }

            var expectedKind = block.Kind == "photo" ? "Photo" : "InkPreview";
            if (matches[0].Kind != expectedKind)
            {
                throw new LocalNoteStoreException($"服务器笔记块材料类型不一致：{block.Id}");
            }

            if (block.Kind == "photo"
                && (!string.Equals(matches[0].Sha256, block.Sha256, StringComparison.Ordinal)
                    || !matches[0].Path.StartsWith(
                        $"assets/{block.AssetId}.",
                        StringComparison.Ordinal)))
            {
                throw new LocalNoteStoreException($"服务器照片块与清单路径或摘要不一致：{block.Id}");
            }

            if (block.Kind == "photo")
            {
                var expectedExtension = block.MimeType switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    "image/webp" => ".webp",
                    _ => throw new LocalNoteStoreException("服务器照片 MIME 类型无效。"),
                };
                if (!matches[0].Path.EndsWith(expectedExtension, StringComparison.Ordinal))
                {
                    throw new LocalNoteStoreException($"服务器照片扩展名与 MIME 类型不一致：{block.Id}");
                }
            }
            else
            {
                var inkHash = Hash(Encoding.UTF8.GetBytes(block.InkJson!));
                if (!string.Equals(
                        matches[0].Path,
                        $"assets/{block.Id}-{inkHash}.png",
                        StringComparison.Ordinal))
                {
                    throw new LocalNoteStoreException($"服务器手写块路径与墨迹摘要不一致：{block.Id}");
                }
            }
        }

        foreach (var file in files.Where(file => file.Kind is "RecordingManifest" or "AudioChunk"))
        {
            if (!Guid.TryParse(file.RecordingId, out _))
            {
                throw new LocalNoteStoreException($"服务器录音材料缺少有效 recordingId：{file.Path}");
            }

            if (file.Kind == "AudioChunk" && file.Samples is null or <= 0)
            {
                throw new LocalNoteStoreException($"服务器录音分片缺少 samples：{file.Path}");
            }

            if (file.Kind == "RecordingManifest"
                && !string.Equals(
                    file.Path,
                    $"recordings/{file.RecordingId}/manifest.json",
                    StringComparison.Ordinal))
            {
                throw new LocalNoteStoreException($"服务器录音 manifest 路径与身份不一致：{file.Path}");
            }

            if (file.Kind == "AudioChunk"
                && (file.Segment is not > 0
                    || file.ChunkIndex is not > 0
                    || !string.Equals(
                        file.Path,
                        $"recordings/{file.RecordingId}/s{file.Segment:D4}-c{file.ChunkIndex:D6}.wav",
                        StringComparison.Ordinal)))
            {
                throw new LocalNoteStoreException($"服务器录音分片路径与索引不一致：{file.Path}");
            }
        }

        if (markdown is not null)
        {
            var blockFiles = files
                .Where(file => !string.IsNullOrWhiteSpace(file.BlockId))
                .ToDictionary(file => file.BlockId!, StringComparer.Ordinal);
            var documentSections = new List<string> { markdownBlocks[0].Text! };
            foreach (var block in blocks.Where(block => block.Kind is "photo" or "ink"))
            {
                var path = blockFiles[block.Id].Path;
                documentSections.Add(block.Kind == "photo"
                    ? $"![照片]({path})"
                    : $"![手写卡片]({path})\n<!-- 可编辑笔迹、身份和尺寸保存在同包 note.json 中 -->");
            }

            var expected = string.Join("\n\n", documentSections);
            if (!string.Equals(expected, markdown, StringComparison.Ordinal))
            {
                throw new LocalNoteStoreException("服务器 note.json、manifest 与 document.md 正文不一致。");
            }
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && !path.Contains('\\')
            && !path.Contains('\0')
            && !path.StartsWith("/", StringComparison.Ordinal)
            && !path.EndsWith("/", StringComparison.Ordinal)
            && path.Split('/').All(segment => segment is not ("" or "." or ".."));
    }

    private static string RequireSha256(JsonElement root, params string[] names)
    {
        var value = RequiredString(root, 64, names);
        if (value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new LocalNoteStoreException("服务器材料摘要不是小写 SHA-256。");
        }

        return value;
    }

    private static int RequireInt(JsonElement root, int expected, params string[] names)
    {
        var value = Property(root, names);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var actual)
            || actual != expected)
        {
            throw new LocalNoteStoreException("服务器材料 schemaVersion 不受支持。");
        }

        return actual;
    }

    private static Guid RequireGuid(JsonElement root, params string[] names)
    {
        var value = RequiredString(root, 64, names);
        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
        {
            throw new LocalNoteStoreException("服务器材料 GUID 无效。");
        }

        return result;
    }

    private static long RequireNonNegativeInt64(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result)
            || result < 0)
        {
            throw new LocalNoteStoreException("服务器材料长度必须为非负整数。");
        }

        return result;
    }

    private static int? OptionalInt32(JsonElement root, params string[] names)
    {
        var value = PropertyOrNull(root, names);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.Value.TryGetInt32(out var result)
            ? result
            : throw new LocalNoteStoreException("服务器材料整数字段无效。");
    }

    private static long? OptionalInt64(JsonElement root, params string[] names)
    {
        var value = PropertyOrNull(root, names);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.Value.TryGetInt64(out var result)
            ? result
            : throw new LocalNoteStoreException("服务器材料长整数字段无效。");
    }

    private static double? Number(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
            ? result
            : throw new LocalNoteStoreException("服务器材料尺寸无效。");
    }

    private static double? OptionalNumber(JsonElement root, params string[] names)
    {
        var value = PropertyOrNull(root, names);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var result)
            ? result
            : throw new LocalNoteStoreException("服务器可选材料尺寸无效。");
    }

    private static string RequiredString(JsonElement root, int? maxLength, params string[] names)
    {
        var value = Property(root, names);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
            || (maxLength is { } max && value.GetString()!.Length > max))
        {
            throw new LocalNoteStoreException("服务器材料字符串字段无效。");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, params string[] names)
    {
        var value = PropertyOrNull(root, names);
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()
            : throw new LocalNoteStoreException("服务器材料可选字符串字段无效。");
    }

    private static JsonElement RequiredObject(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new LocalNoteStoreException("服务器材料对象字段无效。");
    }

    private static JsonElement RequiredArray(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new LocalNoteStoreException("服务器材料数组字段无效。");
    }

    private static JsonElement Property(JsonElement root, params string[] names)
        => PropertyOrNull(root, names)
            ?? throw new LocalNoteStoreException($"服务器材料缺少字段：{names[0]}");

    private static JsonElement? PropertyOrNull(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
