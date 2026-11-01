using System.Text.Json.Serialization;

namespace Pim.Client.Core.Services.Notes;

[JsonConverter(typeof(JsonStringEnumConverter<LocalNoteMaterialKind>))]
public enum LocalNoteMaterialKind
{
    Photo,
    Handwriting,
    HandwritingSource,
    Recording,
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
}

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

public sealed record LocalNoteMaterialFile(
    LocalNoteMaterial Material,
    string AbsolutePath);

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
