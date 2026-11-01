using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pim.Client.Core.Services.Notes;

public sealed class LocalNoteStore
{
    private const int SchemaVersion = 1;
    private readonly string _root;
    private readonly LocalNoteImportLimits _importLimits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LocalNoteStore(string root, LocalNoteImportLimits? importLimits = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("笔记库路径不能为空。", nameof(root));
        }

        _root = Path.GetFullPath(root);
        _importLimits = importLimits ?? LocalNoteImportLimits.Default;
        _importLimits.Validate();
    }

    public string Root => _root;

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectoriesLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteDocument> CreateAsync(
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectoriesLocked();
            var now = DateTimeOffset.UtcNow;
            var noteId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            var normalizedTitle = NormalizeTitle(title);
            var note = new LocalNoteDocument
            {
                NoteId = noteId,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentVersionId = versionId,
                Title = normalizedTitle,
                Markdown = string.Empty,
                Materials = [],
            };

            var version = CreateVersion(
                note,
                versionId,
                parentVersionId: null,
                now,
                LocalNoteCheckpointReason.Initial,
                note.Materials);
            await WriteVersionLockedAsync(noteId, version, cancellationToken);
            await WriteNoteLockedAsync(note, cancellationToken);
            return note;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalNoteSummary>> ListAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectoriesLocked();
            var result = new List<LocalNoteSummary>();
            foreach (var notePath in Directory.EnumerateFiles(NotesRootLocked(), "note.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LocalNoteDocument note;
                try
                {
                    note = await ReadJsonAsync<LocalNoteDocument>(notePath, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or JsonException or LocalNoteStoreException)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(query)
                    && !note.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !note.Markdown.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new LocalNoteSummary(
                    note.NoteId,
                    note.Title,
                    BuildPreview(note.Markdown),
                    note.CreatedAt,
                    note.UpdatedAt,
                    note.Materials.Count,
                    note.Materials.Count(material => material.Kind == LocalNoteMaterialKind.Recording),
                    note.CurrentVersionId));
            }

            return result
                .OrderByDescending(note => note.UpdatedAt)
                .ThenBy(note => note.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteDocument> LoadAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadNoteLockedAsync(noteId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteDocument> SaveAsync(
        Guid noteId,
        string title,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var note = await LoadNoteLockedAsync(noteId, cancellationToken);
            var normalizedTitle = NormalizeTitle(title);
            var normalizedMarkdown = markdown ?? string.Empty;
            if (string.Equals(note.Title, normalizedTitle, StringComparison.Ordinal)
                && string.Equals(note.Markdown, normalizedMarkdown, StringComparison.Ordinal))
            {
                return note;
            }

            return await CreateCheckpointLockedAsync(
                note,
                normalizedTitle,
                normalizedMarkdown,
                note.Materials,
                LocalNoteCheckpointReason.ManualSave,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteDocument> AddMaterialsAsync(
        Guid noteId,
        IReadOnlyList<LocalNoteMaterialInput> inputs,
        CancellationToken cancellationToken = default)
    {
        if (inputs.Count == 0)
        {
            return await LoadAsync(noteId, cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var note = await LoadNoteLockedAsync(noteId, cancellationToken);
            var materials = new List<LocalNoteMaterial>(note.Materials);
            foreach (var input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                materials.Add(await AddMaterialLockedAsync(input, cancellationToken));
            }

            var reason = inputs.Any(input => input.Kind == LocalNoteMaterialKind.Recording)
                ? LocalNoteCheckpointReason.RecordingAdded
                : LocalNoteCheckpointReason.MaterialAdded;
            return await CreateCheckpointLockedAsync(
                note,
                note.Title,
                note.Markdown,
                materials,
                reason,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteDocument> AddHandwritingAsync(
        Guid noteId,
        Stream png,
        string vectorJson,
        string displayName,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vectorJson))
        {
            throw new ArgumentException("手写源数据不能为空。", nameof(vectorJson));
        }

        var vectorBytes = Encoding.UTF8.GetBytes(vectorJson);
        using var vectorStream = new MemoryStream(vectorBytes, writable: false);
        var inputs = new[]
        {
            new LocalNoteMaterialInput(
                png,
                displayName + ".png",
                LocalNoteMaterialKind.Handwriting,
                "image/png",
                width,
                height),
            new LocalNoteMaterialInput(
                vectorStream,
                displayName + ".ink.json",
                LocalNoteMaterialKind.HandwritingSource,
                "application/vnd.pim.ink+json",
                width,
                height),
        };
        return await AddMaterialsAsync(noteId, inputs, cancellationToken);
    }

    public async Task<LocalNoteVersion> LoadVersionAsync(
        Guid noteId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadVersionLockedAsync(noteId, versionId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteDocument> RestoreVersionAsync(
        Guid noteId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var note = await LoadNoteLockedAsync(noteId, cancellationToken);
            var source = await LoadVersionLockedAsync(noteId, versionId, cancellationToken);
            var restoredMaterials = ResolveVersionMaterials(note, source);
            return await CreateCheckpointLockedAsync(
                note,
                NormalizeTitle(source.Title),
                source.Markdown,
                restoredMaterials,
                LocalNoteCheckpointReason.ManualSave,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalNoteVersion>> ListVersionsAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeIdentifier(noteId);
            var versionDirectory = VersionDirectoryLocked(noteId);
            if (!Directory.Exists(versionDirectory))
            {
                return [];
            }

            var versions = new List<LocalNoteVersion>();
            foreach (var path in Directory.EnumerateFiles(versionDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var version = await ReadJsonAsync<LocalNoteVersion>(path, cancellationToken);
                    if (version.NoteId != noteId
                        || version.SchemaVersion != SchemaVersion
                        || version.VersionId == Guid.Empty
                        || !string.Equals(
                            Path.GetFileNameWithoutExtension(path),
                            version.VersionId.ToString("N"),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    versions.Add(version);
                }
                catch (Exception ex) when (ex is IOException or JsonException or LocalNoteStoreException)
                {
                }
            }

            return versions.OrderByDescending(version => version.CreatedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteMaterialFile> ResolveMaterialFileAsync(
        Guid noteId,
        Guid materialId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var note = await LoadNoteLockedAsync(noteId, cancellationToken);
            var material = note.Materials.SingleOrDefault(item => item.MaterialId == materialId)
                ?? throw new LocalNoteStoreException("笔记材料不存在。");
            var path = Path.GetFullPath(Path.Combine(_root, material.RelativePath));
            EnsureUnderRootLocked(path);
            if (!File.Exists(path))
            {
                throw new LocalNoteStoreException($"笔记材料文件缺失：{material.DisplayName}");
            }

            return new LocalNoteMaterialFile(material, path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocalNoteMaterialFile>> ResolveVersionMaterialsAsync(
        Guid noteId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var note = await LoadNoteLockedAsync(noteId, cancellationToken);
            var version = await LoadVersionLockedAsync(noteId, versionId, cancellationToken);
            var result = new List<LocalNoteMaterialFile>();
            foreach (var material in ResolveVersionMaterials(note, version))
            {
                var path = Path.GetFullPath(Path.Combine(_root, material.RelativePath));
                EnsureUnderRootLocked(path);
                if (!File.Exists(path))
                {
                    throw new LocalNoteStoreException($"固定版本材料文件缺失：{material.DisplayName}");
                }

                result.Add(new LocalNoteMaterialFile(material, path));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteCloudBinding?> LoadCloudBindingAsync(
        Guid noteId,
        string serverUrl,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSafeIdentifier(noteId);
            var bindings = await ReadCloudBindingsLockedAsync(cancellationToken);
            return bindings.SingleOrDefault(binding =>
                binding.NoteId == noteId
                && string.Equals(binding.ServerUrl, serverUrl, StringComparison.Ordinal)
                && string.Equals(binding.AccountId, accountId, StringComparison.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCloudBindingAsync(
        LocalNoteCloudBinding binding,
        CancellationToken cancellationToken = default)
    {
        if (binding.SchemaVersion != 1
            || binding.NoteId == Guid.Empty
            || binding.LastVersionId == Guid.Empty
            || string.IsNullOrWhiteSpace(binding.ServerUrl)
            || string.IsNullOrWhiteSpace(binding.AccountId)
            || string.IsNullOrWhiteSpace(binding.Phase))
        {
            throw new LocalNoteStoreException("云端笔记绑定数据无效。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectoriesLocked();
            var bindings = await ReadCloudBindingsLockedAsync(cancellationToken);
            bindings.RemoveAll(item =>
                item.NoteId == binding.NoteId
                && string.Equals(item.ServerUrl, binding.ServerUrl, StringComparison.Ordinal)
                && string.Equals(item.AccountId, binding.AccountId, StringComparison.Ordinal));
            bindings.Add(binding);
            await AtomicWriteJsonAsync(
                CloudBindingsPathLocked(),
                bindings.OrderBy(item => item.NoteId)
                    .ThenBy(item => item.ServerUrl, StringComparer.Ordinal)
                    .ThenBy(item => item.AccountId, StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RemoveCloudBindingAsync(
        LocalNoteCloudBinding expected,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectoriesLocked();
            var bindings = await ReadCloudBindingsLockedAsync(cancellationToken);
            var removed = bindings.RemoveAll(item =>
                item.NoteId == expected.NoteId
                && string.Equals(item.ServerUrl, expected.ServerUrl, StringComparison.Ordinal)
                && string.Equals(item.AccountId, expected.AccountId, StringComparison.Ordinal)
                && item.LastVersionId == expected.LastVersionId
                && item.UpdatedAt == expected.UpdatedAt);
            if (removed == 0)
            {
                return;
            }

            await AtomicWriteJsonAsync(
                CloudBindingsPathLocked(),
                bindings.OrderBy(item => item.NoteId)
                    .ThenBy(item => item.ServerUrl, StringComparer.Ordinal)
                    .ThenBy(item => item.AccountId, StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalNoteBundleExport> ExportAsync(
        Guid noteId,
        Guid? versionId,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("导出路径不能为空。", nameof(outputPath));
        }

        await _gate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            var note = await LoadNoteLockedAsync(noteId, cancellationToken);
            var selectedVersionId = versionId ?? note.CurrentVersionId;
            var version = await LoadVersionLockedAsync(noteId, selectedVersionId, cancellationToken);
            var selectedMaterials = ResolveVersionMaterials(note, version);

            var fullOutputPath = Path.GetFullPath(outputPath);
            var outputDirectory = Path.GetDirectoryName(fullOutputPath)
                ?? throw new ArgumentException("导出路径缺少目录。", nameof(outputPath));
            Directory.CreateDirectory(outputDirectory);
            temporaryPath = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var archiveMaterials = selectedMaterials
                .Select(material => new ExportMaterial(
                    material.MaterialId,
                    material.Kind,
                    material.DisplayName,
                    material.ContentSha256,
                    material.MediaType,
                    material.Length,
                    material.DurationMilliseconds,
                    material.Width,
                    material.Height,
                    UniqueMaterialEntryName(material, usedNames)))
                .ToArray();
            var manifest = new ExportManifest(
                3,
                note.NoteId,
                version.VersionId,
                note.CreatedAt,
                version.CreatedAt,
                version.Reason,
                version.Title,
                version.DocumentSha256,
                archiveMaterials);

            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
                await WriteZipTextAsync(archive, "note.md", version.Markdown, cancellationToken);
                await WriteZipJsonAsync(archive, "version.json", version, cancellationToken);
                await WriteZipJsonAsync(archive, "bundle-manifest.json", manifest, cancellationToken);
                await WriteZipTextAsync(
                    archive,
                    "README.txt",
                    "PIM 笔记导出包\r\n\r\nnote.md 是正文，version.json 是固定版本，materials/ 保存该版本引用的原始材料。\r\n所有材料均以 SHA-256 内容寻址；bundle-manifest.json 记录文件摘要与对应关系。\r\n",
                    cancellationToken);

                var exportMaterialsById = selectedMaterials.ToDictionary(material => material.MaterialId);
                foreach (var archiveMaterial in archiveMaterials)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var material = exportMaterialsById[archiveMaterial.MaterialId];
                    var sourcePath = Path.GetFullPath(Path.Combine(_root, material.RelativePath));
                    EnsureUnderRootLocked(sourcePath);
                    if (!File.Exists(sourcePath))
                    {
                        throw new LocalNoteStoreException($"导出失败，材料文件缺失：{material.DisplayName}");
                    }

                    var actualSha = await ComputeSha256Async(sourcePath, cancellationToken);
                    if (!string.Equals(actualSha, material.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LocalNoteStoreException($"导出失败，材料摘要不一致：{material.DisplayName}");
                    }

                    var entry = archive.CreateEntry(archiveMaterial.ArchivePath!, CompressionLevel.Optimal);
                    await using var target = entry.Open();
                    await using var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await source.CopyToAsync(target, cancellationToken);
                }
            }

            var length = new FileInfo(temporaryPath).Length;
            var archiveSha = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            temporaryPath = null;
            return new LocalNoteBundleExport(
                note.NoteId,
                version.VersionId,
                fullOutputPath,
                archiveSha,
                length,
                selectedMaterials);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new LocalNoteStoreException("导出笔记包失败。", ex);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }

            _gate.Release();
        }
    }

    public async Task<LocalNotePackageImport> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("笔记包路径不能为空。", nameof(packagePath));
        }

        await _gate.WaitAsync(cancellationToken);
        var importedTemporaryFiles = new List<string>();
        var importedTargetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guid? importingNoteId = null;
        var importCommitted = false;
        try
        {
            EnsureDirectoriesLocked();
            var fullPackagePath = Path.GetFullPath(packagePath);
            if (!File.Exists(fullPackagePath))
            {
                throw new LocalNoteStoreException("笔记包不存在。");
            }

            var packageLength = new FileInfo(fullPackagePath).Length;
            var packageSha = await ComputeSha256Async(fullPackagePath, cancellationToken);
            await using var package = new FileStream(
                fullPackagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchiveEntries(archive);

            long totalUncompressedBytes = 0;
            var versionResult = await ReadRequiredZipJsonAsync<LocalNoteVersion>(
                archive,
                "version.json",
                _importLimits.MaxMetadataEntryBytes,
                _importLimits.MaxTotalUncompressedBytes - totalUncompressedBytes,
                cancellationToken);
            var version = versionResult.Value;
            totalUncompressedBytes = checked(totalUncompressedBytes + versionResult.Length);
            var manifestResult = await ReadRequiredZipJsonAsync<ExportManifest>(
                archive,
                "bundle-manifest.json",
                _importLimits.MaxMetadataEntryBytes,
                _importLimits.MaxTotalUncompressedBytes - totalUncompressedBytes,
                cancellationToken);
            var manifest = manifestResult.Value;
            totalUncompressedBytes = checked(totalUncompressedBytes + manifestResult.Length);
            var markdownResult = await ReadRequiredZipTextAsync(
                archive,
                "note.md",
                _importLimits.MaxDocumentEntryBytes,
                _importLimits.MaxTotalUncompressedBytes - totalUncompressedBytes,
                cancellationToken);
            var noteMarkdown = markdownResult.Value;
            totalUncompressedBytes = checked(totalUncompressedBytes + markdownResult.Length);
            ValidateImportedPackage(version, manifest, noteMarkdown);

            var importedMaterials = new List<LocalNoteMaterial>(manifest.Materials.Count);
            foreach (var materialId in version.MaterialIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifestMaterial = manifest.Materials.SingleOrDefault(
                    material => material.MaterialId == materialId)
                    ?? throw new LocalNoteStoreException($"笔记包缺少材料清单：{materialId:N}");
                var entry = ResolveMaterialEntry(archive, manifestMaterial);
                var temporaryPath = Path.Combine(
                    TemporaryRootLocked(),
                    Guid.NewGuid().ToString("N") + ".import");
                importedTemporaryFiles.Add(temporaryPath);

                var imported = await CopyImportedMaterialAsync(
                    entry,
                    temporaryPath,
                    manifestMaterial.Length,
                    _importLimits.MaxMaterialEntryBytes,
                    _importLimits.MaxTotalUncompressedBytes - totalUncompressedBytes,
                    cancellationToken);
                totalUncompressedBytes = checked(totalUncompressedBytes + imported.Length);
                if (!string.Equals(imported.Sha256, manifestMaterial.ContentSha256, StringComparison.OrdinalIgnoreCase)
                    || imported.Length != manifestMaterial.Length)
                {
                    throw new LocalNoteStoreException($"笔记包材料摘要或长度不一致：{manifestMaterial.DisplayName}");
                }

                var displayName = NormalizeDisplayName(manifestMaterial.DisplayName);
                var extension = ResolveExtension(displayName, manifestMaterial.MediaType);
                var relativePath = MaterialRelativePath(imported.Sha256, extension);
                var targetPath = Path.Combine(_root, relativePath);
                EnsureUnderRootLocked(Path.GetFullPath(targetPath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                if (File.Exists(targetPath))
                {
                    var existingSha = await ComputeSha256Async(targetPath, cancellationToken);
                    if (!string.Equals(existingSha, imported.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LocalNoteStoreException(
                            $"本机内容寻址材料已损坏，已拒绝复用：{manifestMaterial.DisplayName}");
                    }
                }
                else
                {
                    File.Move(temporaryPath, targetPath);
                    importedTemporaryFiles.Remove(temporaryPath);
                    importedTargetFiles.Add(targetPath);
                }

                importedMaterials.Add(new LocalNoteMaterial
                {
                    MaterialId = manifestMaterial.MaterialId,
                    Kind = manifestMaterial.Kind,
                    DisplayName = displayName,
                    ContentSha256 = imported.Sha256,
                    RelativePath = relativePath,
                    MediaType = string.IsNullOrWhiteSpace(manifestMaterial.MediaType)
                        ? InferMediaType(extension)
                        : manifestMaterial.MediaType,
                    Length = imported.Length,
                    AddedAt = DateTimeOffset.UtcNow,
                    Width = manifestMaterial.Width,
                    Height = manifestMaterial.Height,
                    DurationMilliseconds = manifestMaterial.DurationMilliseconds,
                });
            }

            var now = DateTimeOffset.UtcNow;
            var noteId = Guid.NewGuid();
            importingNoteId = noteId;
            var versionId = Guid.NewGuid();
            var note = new LocalNoteDocument
            {
                NoteId = noteId,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentVersionId = versionId,
                Title = NormalizeTitle(version.Title),
                Markdown = version.Markdown ?? string.Empty,
                Materials = importedMaterials,
            };
            var importedVersion = CreateVersion(
                note,
                versionId,
                parentVersionId: null,
                now,
                LocalNoteCheckpointReason.ImportedPackage,
                importedMaterials);
            await WriteVersionLockedAsync(noteId, importedVersion, cancellationToken);
            await WriteNoteLockedAsync(note, cancellationToken);
            importCommitted = true;
            return new LocalNotePackageImport(
                noteId,
                versionId,
                version.VersionId,
                note.Title,
                packageSha,
                packageLength,
                importedMaterials);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            throw new LocalNoteStoreException("导入笔记包失败；原笔记库未切换。", ex);
        }
        finally
        {
            foreach (var temporaryPath in importedTemporaryFiles)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }

            if (!importCommitted)
            {
                foreach (var targetPath in importedTargetFiles)
                {
                    try
                    {
                        File.Delete(targetPath);
                    }
                    catch
                    {
                    }
                }

                if (importingNoteId is { } noteId)
                {
                    try
                    {
                        var noteDirectory = NoteDirectoryLocked(noteId);
                        if (Directory.Exists(noteDirectory))
                        {
                            Directory.Delete(noteDirectory, recursive: true);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            _gate.Release();
        }
    }

    private async Task<LocalNoteDocument> CreateCheckpointLockedAsync(
        LocalNoteDocument note,
        string title,
        string markdown,
        IReadOnlyList<LocalNoteMaterial> materials,
        LocalNoteCheckpointReason reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        var updated = note with
        {
            UpdatedAt = now,
            CurrentVersionId = versionId,
            Title = title,
            Markdown = markdown,
            Materials = materials.ToList(),
        };
        var version = CreateVersion(
            updated,
            versionId,
            note.CurrentVersionId == Guid.Empty ? null : note.CurrentVersionId,
            now,
            reason,
            materials);
        await WriteVersionLockedAsync(note.NoteId, version, cancellationToken);
        await WriteNoteLockedAsync(updated, cancellationToken);
        return updated;
    }

    private LocalNoteVersion CreateVersion(
        LocalNoteDocument note,
        Guid versionId,
        Guid? parentVersionId,
        DateTimeOffset createdAt,
        LocalNoteCheckpointReason reason,
        IReadOnlyList<LocalNoteMaterial> materials) =>
        new()
        {
            NoteId = note.NoteId,
            VersionId = versionId,
            ParentVersionId = parentVersionId,
            CreatedAt = createdAt,
            Reason = reason,
            Title = note.Title,
            Markdown = note.Markdown,
            DocumentSha256 = ComputeDocumentSha256(note.Title, note.Markdown),
            MaterialIds = materials.Select(material => material.MaterialId).ToList(),
            Materials = materials.ToArray(),
        };

    private static LocalNoteMaterial[] ResolveVersionMaterials(
        LocalNoteDocument note,
        LocalNoteVersion version)
    {
        var availableMaterials = version.Materials.Count > 0 ? version.Materials : note.Materials;
        var materialsById = availableMaterials
            .GroupBy(material => material.MaterialId)
            .ToDictionary(group => group.Key, group => group.First());
        return version.MaterialIds
            .Select(materialId => materialsById.TryGetValue(materialId, out var material)
                ? material
                : throw new LocalNoteStoreException($"固定版本引用的材料缺失：{materialId:N}"))
            .ToArray();
    }

    private async Task<LocalNoteMaterial> AddMaterialLockedAsync(
        LocalNoteMaterialInput input,
        CancellationToken cancellationToken)
    {
        if (input.Content is null)
        {
            throw new ArgumentException("材料内容不能为空。", nameof(input));
        }

        var displayName = NormalizeDisplayName(input.DisplayName);
        var temporaryPath = Path.Combine(
            TemporaryRootLocked(),
            Guid.NewGuid().ToString("N") + ".part");
        Directory.CreateDirectory(TemporaryRootLocked());

        string sha;
        long length;
        try
        {
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                length = 0;
                while (true)
                {
                    var read = await input.Content.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }

                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (length == 0)
            {
                throw new LocalNoteStoreException($"材料为空，未保存：{displayName}");
            }

            var extension = ResolveExtension(displayName, input.MediaType);
            var relativePath = MaterialRelativePath(sha, extension);
            var targetPath = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (!File.Exists(targetPath))
            {
                File.Move(temporaryPath, targetPath);
                temporaryPath = string.Empty;
            }

            return new LocalNoteMaterial
            {
                MaterialId = Guid.NewGuid(),
                Kind = input.Kind,
                DisplayName = displayName,
                ContentSha256 = sha,
                RelativePath = relativePath,
                MediaType = string.IsNullOrWhiteSpace(input.MediaType)
                    ? InferMediaType(extension)
                    : input.MediaType,
                Length = length,
                AddedAt = DateTimeOffset.UtcNow,
                Width = input.Width,
                Height = input.Height,
                DurationMilliseconds = input.DurationMilliseconds,
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<LocalNoteDocument> LoadNoteLockedAsync(
        Guid noteId,
        CancellationToken cancellationToken)
    {
        EnsureSafeIdentifier(noteId);
        var notePath = NotePathLocked(noteId);
        if (!File.Exists(notePath))
        {
            throw new LocalNoteStoreException("笔记不存在。");
        }

        var note = await ReadJsonAsync<LocalNoteDocument>(notePath, cancellationToken);
        if (note.SchemaVersion != SchemaVersion || note.NoteId != noteId)
        {
            throw new LocalNoteStoreException("笔记元数据版本或标识不匹配。");
        }

        var versionPath = VersionPathLocked(noteId, note.CurrentVersionId);
        if (!File.Exists(versionPath))
        {
            throw new LocalNoteStoreException("笔记当前固定版本缺失，已拒绝静默加载。");
        }

        await LoadVersionLockedAsync(noteId, note.CurrentVersionId, cancellationToken);

        return note with
        {
            Title = note.Title ?? string.Empty,
            Markdown = note.Markdown ?? string.Empty,
            Materials = note.Materials ?? [],
        };
    }

    private async Task<List<LocalNoteCloudBinding>> ReadCloudBindingsLockedAsync(
        CancellationToken cancellationToken)
    {
        var path = CloudBindingsPathLocked();
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return await ReadJsonAsync<List<LocalNoteCloudBinding>>(path, cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or LocalNoteStoreException)
        {
            throw new LocalNoteStoreException("本机云端笔记绑定数据损坏；没有静默覆盖。", ex);
        }
    }

    private string CloudBindingsPathLocked()
    {
        var path = Path.Combine(_root, "cloud-bindings.json");
        EnsureUnderRootLocked(path);
        return path;
    }

    private async Task WriteNoteLockedAsync(
        LocalNoteDocument note,
        CancellationToken cancellationToken)
    {
        var directory = NoteDirectoryLocked(note.NoteId);
        Directory.CreateDirectory(directory);
        await AtomicWriteJsonAsync(NotePathLocked(note.NoteId), note, cancellationToken);
    }

    private async Task<LocalNoteVersion> LoadVersionLockedAsync(
        Guid noteId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        EnsureSafeIdentifier(noteId);
        EnsureSafeIdentifier(versionId);
        var path = VersionPathLocked(noteId, versionId);
        if (!File.Exists(path))
        {
            throw new LocalNoteStoreException("固定版本不存在。");
        }

        var version = await ReadJsonAsync<LocalNoteVersion>(path, cancellationToken);
        if (version.NoteId != noteId
            || version.VersionId != versionId
            || version.SchemaVersion != SchemaVersion)
        {
            throw new LocalNoteStoreException("固定版本不属于当前笔记或版本不受支持。");
        }

        if (!string.Equals(
                version.DocumentSha256,
                ComputeDocumentSha256(version.Title, version.Markdown),
                StringComparison.OrdinalIgnoreCase)
            || version.MaterialIds is null
            || version.MaterialIds.Distinct().Count() != version.MaterialIds.Count
            || version.Materials is null
            || (version.Materials.Count > 0
                && (version.Materials.Select(material => material.MaterialId).Distinct().Count() != version.Materials.Count
                    || version.Materials.Count != version.MaterialIds.Count
                    || !version.MaterialIds.All(id => version.Materials.Any(material => material.MaterialId == id)))))
        {
            throw new LocalNoteStoreException("固定版本正文摘要或材料快照无效。");
        }

        return version;
    }

    private async Task WriteVersionLockedAsync(
        Guid noteId,
        LocalNoteVersion version,
        CancellationToken cancellationToken)
    {
        var directory = VersionDirectoryLocked(noteId);
        Directory.CreateDirectory(directory);
        await AtomicWriteJsonAsync(VersionPathLocked(noteId, version.VersionId), version, cancellationToken);
    }

    private async Task AtomicWriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken)
                ?? throw new LocalNoteStoreException($"文件内容为空：{Path.GetFileName(path)}");
        }
        catch (JsonException ex)
        {
            throw new LocalNoteStoreException($"笔记数据损坏：{Path.GetFileName(path)}", ex);
        }
    }

    private async Task<(T Value, long Length)> ReadRequiredZipJsonAsync<T>(
        ZipArchive archive,
        string entryName,
        long entryLimit,
        long remainingPackageBudget,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedZipEntryAsync(
            archive,
            entryName,
            entryLimit,
            remainingPackageBudget,
            cancellationToken);
        var value = JsonSerializer.Deserialize<T>(bytes, _jsonOptions)
            ?? throw new LocalNoteStoreException($"笔记包 {entryName} 内容为空。");
        return (value, bytes.LongLength);
    }

    private async Task<(string Value, long Length)> ReadRequiredZipTextAsync(
        ZipArchive archive,
        string entryName,
        long entryLimit,
        long remainingPackageBudget,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedZipEntryAsync(
            archive,
            entryName,
            entryLimit,
            remainingPackageBudget,
            cancellationToken);
        try
        {
            var value = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
            return (value, bytes.LongLength);
        }
        catch (DecoderFallbackException ex)
        {
            throw new LocalNoteStoreException($"笔记包 {entryName} 不是有效的 UTF-8 文本。", ex);
        }
    }

    private void ValidateArchiveEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > _importLimits.MaxArchiveEntries)
        {
            throw new LocalNoteStoreException(
                $"笔记包文件项过多：{archive.Entries.Count}，上限 {_importLimits.MaxArchiveEntries}。");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (!IsSafeArchivePath(name))
            {
                throw new LocalNoteStoreException($"笔记包包含无效文件路径：{name}");
            }

            if (!names.Add(name))
            {
                throw new LocalNoteStoreException($"笔记包包含重复文件项：{name}");
            }
        }
    }

    private async Task<byte[]> ReadBoundedZipEntryAsync(
        ZipArchive archive,
        string entryName,
        long entryLimit,
        long remainingPackageBudget,
        CancellationToken cancellationToken)
    {
        var entry = GetRequiredEntry(archive, entryName);
        var maxBytes = Math.Min(entryLimit, remainingPackageBudget);
        if (maxBytes <= 0)
        {
            throw new LocalNoteStoreException("笔记包解压总量超过安全上限。");
        }

        if (entry.Length > maxBytes)
        {
            throw new LocalNoteStoreException($"笔记包文件项超过读取上限：{entryName}");
        }

        await using var stream = entry.Open();
        using var output = new MemoryStream(
            capacity: (int)Math.Clamp(entry.Length, 0, 64 * 1024));
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            length = checked(length + read);
            if (length > maxBytes)
            {
                throw new LocalNoteStoreException($"笔记包文件项读取超过上限：{entryName}");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static ZipArchiveEntry GetRequiredEntry(ZipArchive archive, string entryName) =>
        archive.Entries.SingleOrDefault(entry =>
            string.Equals(entry.FullName, entryName, StringComparison.Ordinal))
        ?? throw new LocalNoteStoreException($"笔记包缺少 {entryName}。");

    private static ZipArchiveEntry ResolveMaterialEntry(
        ZipArchive archive,
        ExportMaterial material)
    {
        if (!string.IsNullOrWhiteSpace(material.ArchivePath))
        {
            if (material.ArchivePath.StartsWith("materials/", StringComparison.Ordinal)
                && IsSafeArchivePath(material.ArchivePath)
                && archive.Entries.SingleOrDefault(entry =>
                    string.Equals(entry.FullName, material.ArchivePath, StringComparison.Ordinal)) is { } exactEntry)
            {
                return exactEntry;
            }

            throw new LocalNoteStoreException($"笔记包材料路径无效：{material.DisplayName}");
        }

        var hashPrefix = material.ContentSha256[..12];
        var candidates = archive.Entries
            .Where(entry => entry.FullName.StartsWith("materials/", StringComparison.Ordinal)
                && entry.FullName.Contains(hashPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : throw new LocalNoteStoreException($"无法唯一定位笔记包材料：{material.DisplayName}");
    }

    private static async Task<(string Sha256, long Length)> CopyImportedMaterialAsync(
        ZipArchiveEntry entry,
        string temporaryPath,
        long expectedLength,
        long materialEntryLimit,
        long remainingPackageBudget,
        CancellationToken cancellationToken)
    {
        if (expectedLength <= 0)
        {
            throw new LocalNoteStoreException($"笔记包材料长度无效：{entry.FullName}");
        }

        var maxBytes = Math.Min(materialEntryLimit, remainingPackageBudget);
        if (expectedLength > maxBytes)
        {
            throw new LocalNoteStoreException($"笔记包材料超过安全上限：{entry.FullName}");
        }

        if (entry.Length > maxBytes)
        {
            throw new LocalNoteStoreException($"笔记包材料声明长度超过安全上限：{entry.FullName}");
        }

        await using var source = entry.Open();
        await using var target = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            length = checked(length + read);
            if (length > expectedLength || length > maxBytes)
            {
                throw new LocalNoteStoreException($"笔记包材料读取超过清单或安全上限：{entry.FullName}");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
        }

        await target.FlushAsync(cancellationToken);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static void ValidateImportedPackage(
        LocalNoteVersion version,
        ExportManifest manifest,
        string noteMarkdown)
    {
        if (manifest.SchemaVersion is not (1 or 2 or 3)
            || version.SchemaVersion != SchemaVersion
            || manifest.NoteId != version.NoteId
            || manifest.VersionId != version.VersionId
            || manifest.Materials is null
            || version.MaterialIds is null
            || !IsSha256Hex(version.DocumentSha256)
            || !IsSha256Hex(manifest.DocumentSha256)
            || manifest.Materials.Any(material => material is null || !IsSha256Hex(material.ContentSha256)))
        {
            throw new LocalNoteStoreException("笔记包版本或清单结构不受支持，或摘要格式无效。");
        }

        if (!string.Equals(noteMarkdown, version.Markdown ?? string.Empty, StringComparison.Ordinal))
        {
            throw new LocalNoteStoreException("笔记包 note.md 与 version.json 正文不一致。");
        }

        var computedDocumentSha = ComputeDocumentSha256(version.Title, version.Markdown ?? string.Empty);
        if (!string.Equals(computedDocumentSha, version.DocumentSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(version.DocumentSha256, manifest.DocumentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalNoteStoreException("笔记包正文摘要不一致。");
        }

        var manifestIds = manifest.Materials.Select(material => material.MaterialId).ToArray();
        if (version.MaterialIds.Count != manifestIds.Length
            || version.MaterialIds.Distinct().Count() != version.MaterialIds.Count
            || manifestIds.Distinct().Count() != manifestIds.Length
            || manifestIds.Any(materialId => materialId == Guid.Empty)
            || !version.MaterialIds.All(materialId => manifestIds.Contains(materialId)))
        {
            throw new LocalNoteStoreException("笔记包固定版本与材料清单不一致。");
        }
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static bool IsSafeArchivePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value[0] == '/'
            || value.Contains('\\')
            || value.Contains('\0')
            || value.EndsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        return !value.Split('/').Any(segment => segment is "" or "." or "..");
    }

    private async Task WriteZipTextAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private async Task WriteZipJsonAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken);
    }

    private void EnsureDirectoriesLocked()
    {
        Directory.CreateDirectory(NotesRootLocked());
        Directory.CreateDirectory(MaterialsRootLocked());
        Directory.CreateDirectory(TemporaryRootLocked());
    }

    private void EnsureUnderRootLocked(string path)
    {
        var rootWithSeparator = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalNoteStoreException("路径越出本地笔记库。");
        }
    }

    private string NotesRootLocked() => Path.Combine(_root, "notes");
    private string MaterialsRootLocked() => Path.Combine(_root, "materials");
    private string TemporaryRootLocked() => Path.Combine(_root, "tmp");
    private string NoteDirectoryLocked(Guid noteId) => Path.Combine(NotesRootLocked(), noteId.ToString("N"));
    private string NotePathLocked(Guid noteId) => Path.Combine(NoteDirectoryLocked(noteId), "note.json");
    private string VersionDirectoryLocked(Guid noteId) => Path.Combine(NoteDirectoryLocked(noteId), "versions");
    private string VersionPathLocked(Guid noteId, Guid versionId) =>
        Path.Combine(VersionDirectoryLocked(noteId), versionId.ToString("N") + ".json");

    private static string MaterialRelativePath(string sha, string extension) =>
        Path.Combine("materials", sha[..2], sha + extension);

    private static void EnsureSafeIdentifier(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("标识不能为空。", nameof(id));
        }
    }

    private static string ComputeDocumentSha256(string title, string markdown)
    {
        var bytes = Encoding.UTF8.GetBytes((title ?? string.Empty) + "\n" + (markdown ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string UniqueMaterialEntryName(
        LocalNoteMaterial material,
        HashSet<string> usedNames)
    {
        var extension = Path.GetExtension(material.DisplayName);
        var baseName = Path.GetFileNameWithoutExtension(material.DisplayName);
        var safeBase = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? material.MaterialId.ToString("N") : baseName);
        var candidate = $"materials/{material.Kind}/{safeBase}__{material.ContentSha256[..12]}{extension}";
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"materials/{material.Kind}/{safeBase}__{material.ContentSha256[..12]}_{suffix++}{extension}";
        }

        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "material" : result;
    }

    private static string NormalizeTitle(string? title)
    {
        var normalized = string.IsNullOrWhiteSpace(title) ? "未命名笔记" : title.Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    private static string NormalizeDisplayName(string displayName)
    {
        var normalized = string.IsNullOrWhiteSpace(displayName) ? "material" : Path.GetFileName(displayName.Trim());
        normalized = SanitizeFileName(normalized);
        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private static string BuildPreview(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "空白笔记";
        }

        var line = markdown
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim() ?? "空白笔记";
        return line.Length <= 80 ? line : line[..80] + "...";
    }

    private static string ResolveExtension(string displayName, string? mediaType)
    {
        var extension = Path.GetExtension(displayName).ToLowerInvariant();
        if (extension.Length is > 0 and <= 12 && extension.Skip(1).All(char.IsLetterOrDigit))
        {
            return extension;
        }

        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "audio/wav" or "audio/x-wav" => ".wav",
            "application/vnd.pim.ink+json" => ".ink.json",
            _ => ".bin",
        };
    }

    private static string InferMediaType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".wav" => "audio/wav",
        ".json" or ".ink.json" => "application/json",
        _ => "application/octet-stream",
    };

    private sealed record ExportManifest(
        int SchemaVersion,
        Guid NoteId,
        Guid VersionId,
        DateTimeOffset NoteCreatedAt,
        DateTimeOffset VersionCreatedAt,
        LocalNoteCheckpointReason Reason,
        string Title,
        string DocumentSha256,
        IReadOnlyList<ExportMaterial> Materials);

    private sealed record ExportMaterial(
        Guid MaterialId,
        LocalNoteMaterialKind Kind,
        string DisplayName,
        string ContentSha256,
        string MediaType,
        long Length,
        long? DurationMilliseconds,
        int? Width,
        int? Height,
        string? ArchivePath);
}
