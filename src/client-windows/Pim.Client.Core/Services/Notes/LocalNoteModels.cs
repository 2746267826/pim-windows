using System.Text.Json.Serialization;

namespace Pim.Client.Core.Services.Notes;

[JsonConverter(typeof(JsonStringEnumConverter<LocalNoteMaterialKind>))]
public enum LocalNoteMaterialKind
{
    Photo,
    Handwriting,
    HandwritingSource,
    Recording,
    RecordingSegment,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<LocalNoteCheckpointReason>))]
public enum LocalNoteCheckpointReason
{
    Initial,
    ManualSave,
    MaterialAdded,
    RecordingAdded,
    ImportedPackage,
}

public sealed record LocalNoteMaterial
{
    public required Guid MaterialId { get; init; }
    public required LocalNoteMaterialKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string ContentSha256 { get; init; }
    public required string RelativePath { get; init; }
    public required string MediaType { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset AddedAt { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMilliseconds { get; init; }
    public Guid? RecordingId { get; init; }
    public int? RecordingSegment { get; init; }
    public int? RecordingChunkIndex { get; init; }
    public long? RecordingSamples { get; init; }
}

public sealed record LocalNoteDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid NoteId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required Guid CurrentVersionId { get; init; }
    public required string Title { get; init; }
    public required string Markdown { get; init; }
    public required List<LocalNoteMaterial> Materials { get; init; }
    public LocalNoteServerSource? ServerSource { get; init; }

    [JsonIgnore]
    public bool IsServerSourceReadOnly => ServerSource is not null;
}

public sealed record LocalNoteVersion
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid NoteId { get; init; }
    public required Guid VersionId { get; init; }
    public Guid? ParentVersionId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required LocalNoteCheckpointReason Reason { get; init; }
    public required string Title { get; init; }
    public required string Markdown { get; init; }
    public required string DocumentSha256 { get; init; }
    public required List<Guid> MaterialIds { get; init; }
    public IReadOnlyList<LocalNoteMaterial> Materials { get; init; } = [];
    public LocalNoteServerSource? ServerSource { get; init; }
}

public sealed record LocalNoteServerSource(
    Guid SourceNoteId,
    Guid SourceVersionId,
    string InputJson,
    string ManifestJson,
    string NoteJson,
    string DocumentMarkdown,
    IReadOnlyList<LocalNoteServerSourceBlock> Blocks,
    IReadOnlyList<LocalNoteServerSourceFile> Files);

public sealed record LocalNoteServerSourceBlock(
    string Id,
    string Kind,
    string RawJson,
    IReadOnlyList<Guid> MaterialIds);

public sealed record LocalNoteServerSourceFile(
    string Path,
    string Kind,
    string Sha256,
    long SizeBytes,
    string? BlockId,
    string? RecordingId,
    int? Segment,
    int? ChunkIndex,
    long? Samples,
    Guid? MaterialId,
    string? InlineJson);

public sealed record LocalNoteSummary(
    Guid NoteId,
    string Title,
    string Preview,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MaterialCount,
    int RecordingCount,
    Guid CurrentVersionId);

public sealed record LocalNoteMaterialInput(
    Stream Content,
    string DisplayName,
    LocalNoteMaterialKind Kind,
    string? MediaType = null,
    int? Width = null,
    int? Height = null,
    long? DurationMilliseconds = null);

public sealed record LocalNoteBundleExport(
    Guid NoteId,
    Guid VersionId,
    string Path,
    string Sha256,
    long Length,
    IReadOnlyList<LocalNoteMaterial> Materials);

public sealed record LocalNotePackageImport(
    Guid NoteId,
    Guid VersionId,
    Guid SourceVersionId,
    string Title,
    string PackageSha256,
    long PackageLength,
    IReadOnlyList<LocalNoteMaterial> Materials);

public sealed record LocalNoteServerMaterialInput(
    Guid MaterialId,
    LocalNoteMaterialKind Kind,
    string DisplayName,
    string ContentSha256,
    string MediaType,
    long Length,
    string SourcePath,
    int? Width = null,
    int? Height = null,
    long? DurationMilliseconds = null,
    long? ExpectedSamples = null,
    Guid? RecordingId = null,
    int? RecordingSegment = null,
    int? RecordingChunkIndex = null);

public sealed record LocalNoteServerRestorePackage(
    Guid SourceNoteId,
    Guid SourceVersionId,
    string Title,
    string Markdown,
    IReadOnlyList<LocalNoteServerMaterialInput> Materials,
    LocalNoteServerSource ServerSource);

public sealed record LocalNoteServerImport(
    Guid NoteId,
    Guid VersionId,
    Guid SourceNoteId,
    Guid SourceVersionId,
    string Title,
    IReadOnlyList<LocalNoteMaterial> Materials);

public sealed record LocalNoteMaterialFile(
    LocalNoteMaterial Material,
    string AbsolutePath);

public sealed record LocalNoteCloudBinding
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid NoteId { get; init; }
    public required string ServerUrl { get; init; }
    public required string AccountId { get; init; }
    public Guid? BaseVersionId { get; init; }
    public Guid? CurrentVersionId { get; init; }
    public required Guid LastVersionId { get; init; }
    public required string Phase { get; init; }
    public bool HasConflict { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record LocalNoteImportLimits
{
    public static LocalNoteImportLimits Default { get; } = new();

    public int MaxArchiveEntries { get; init; } = 2_048;
    public long MaxMetadataEntryBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxDocumentEntryBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxMaterialEntryBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaxTotalUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxArchiveEntries <= 0
            || MaxMetadataEntryBytes <= 0
            || MaxDocumentEntryBytes <= 0
            || MaxMaterialEntryBytes <= 0
            || MaxTotalUncompressedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(LocalNoteImportLimits), "笔记包导入限制必须为正数。");
        }
    }
}

public sealed class LocalNoteStoreException : Exception
{
    public LocalNoteStoreException(string message) : base(message)
    {
    }

    public LocalNoteStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
