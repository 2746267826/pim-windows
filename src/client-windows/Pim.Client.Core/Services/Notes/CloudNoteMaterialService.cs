using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

public sealed record CloudNoteMaterialSyncResult(
    NoteMaterialUploadDto Upload,
    LocalNoteCloudBinding Binding,
    IReadOnlyList<NoteMaterialUploadDto> Versions);

public sealed record CloudNoteMaterialRestoreResult(
    NoteMaterialVersionDto ServerVersion,
    LocalNoteServerImport Import);

public sealed record CloudNoteAccountScope(string ServerUrl, string AccountId);

/// <summary>Uploads immutable local versions and reads owner-bound server versions and results.</summary>
public sealed class CloudNoteMaterialService
{
    private readonly ApiClient _api;
    private readonly LocalNoteStore _store;
    private readonly ServerNoteMaterialBundleBuilder _builder;
    private readonly Func<CloudNoteAccountScope?> _accountProvider;
    private readonly LocalNoteImportLimits _restoreLimits;
    private long _accountGeneration;

    public CloudNoteMaterialService(ApiClient api, AuthService auth, LocalNoteStore store)
        : this(api, store, () =>
        {
            if (!auth.IsAuthenticated || string.IsNullOrWhiteSpace(auth.CurrentUserId))
            {
                return null;
            }

            return new CloudNoteAccountScope(
                ApiClient.NormalizeServerUrl(auth.ServerUrl),
                Guid.TryParse(auth.CurrentUserId, out var accountId)
                    ? accountId.ToString("D")
                    : throw new InvalidDataException("当前登录账号标识无效。"));
        })
    {
    }

    public CloudNoteMaterialService(
        ApiClient api,
        LocalNoteStore store,
        Func<CloudNoteAccountScope?> accountProvider,
        LocalNoteImportLimits? restoreLimits = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _accountProvider = accountProvider ?? throw new ArgumentNullException(nameof(accountProvider));
        _builder = new ServerNoteMaterialBundleBuilder(store);
        _restoreLimits = restoreLimits ?? LocalNoteImportLimits.Default;
        _restoreLimits.Validate();
        _api.ExternalScopeChanged += OnExternalScopeChanged;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return _accountProvider() is not null;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
    }

    public async Task<CloudNoteMaterialSyncResult> UploadVersionAsync(
        Guid noteId,
        Guid versionId,
        bool userConfirmedStorage,
        CancellationToken cancellationToken = default)
    {
        if (!userConfirmedStorage)
        {
            throw new InvalidOperationException("上传前必须由用户明确确认本次固定材料存入当前服务器。");
        }

        using var operation = BeginOperation();
        var account = operation.Account;
        var bundle = await _builder.BuildAsync(noteId, versionId, cancellationToken);
        RequireOperationCurrent(operation);
        var binding = await _store.LoadCloudBindingAsync(
            noteId,
            account.ServerUrl,
            account.AccountId,
            cancellationToken);
        RequireOperationCurrent(operation);
        Guid? baseVersionId;
        if (binding?.LastVersionId == versionId)
        {
            baseVersionId = binding.BaseVersionId;
        }
        else if (binding is not null)
        {
            baseVersionId = binding.CurrentVersionId;
        }
        else
        {
            // Recover the owner/server head after a restart or binding-file loss.
            var existing = await LoadVersionsAsync(noteId, operation, cancellationToken)
                .ConfigureAwait(false);
            var sameVersion = existing.SingleOrDefault(item =>
                string.Equals(item.VersionId, versionId.ToString("D"), StringComparison.Ordinal));
            baseVersionId = sameVersion is not null
                ? ParseOptionalGuid(sameVersion.BaseVersionId)
                : ParseOptionalGuid(existing.SingleOrDefault(item => item.IsCurrent)?.VersionId);
        }
        var request = new BeginNoteMaterialUploadRequest
        {
            BaseVersionId = baseVersionId?.ToString("D"),
            InputJson = bundle.InputJson,
            ManifestJson = bundle.ManifestJson,
            NoteId = bundle.NoteId.ToString("D"),
            StorageConsent = true,
            VersionId = bundle.VersionId.ToString("D"),
        };
        RequireOperationCurrent(operation);
        var response = await _api.BeginNoteMaterialUploadAsync(request, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回材料上传状态。");
        RequireOperationCurrent(operation);
        var upload = RequireResponse(response.Code, response.Message, response.Data);
        RequireUploadIdentity(upload, bundle.NoteId, bundle.VersionId);

        var partSize = upload.Limits.PartSizeBytes;
        if (partSize is < 1 or > 1_048_576)
        {
            throw new InvalidDataException("服务器返回了不受支持的材料分片大小；本机固定版本保持不变。");
        }

        if (upload.Files.Count != bundle.Files.Count
            || upload.Files.Select(file => file.Index).Distinct().Count() != bundle.Files.Count)
        {
            throw new InvalidDataException("服务器材料文件数量或索引与本机固定清单不一致。");
        }

        foreach (var serverFile in upload.Files.OrderBy(file => file.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (serverFile.Index < 0 || serverFile.Index >= bundle.Files.Count)
            {
                throw new InvalidDataException("服务器材料文件索引与固定清单不匹配。");
            }

            var localFile = bundle.Files[serverFile.Index];
            if (!string.Equals(localFile.Path, serverFile.Path, StringComparison.Ordinal)
                || !string.Equals(localFile.Kind, serverFile.Kind, StringComparison.Ordinal)
                || !string.Equals(localFile.Sha256, serverFile.Sha256, StringComparison.Ordinal)
                || localFile.SizeBytes != serverFile.SizeBytes)
            {
                throw new InvalidDataException("服务器材料索引与本机固定清单不一致；没有覆盖服务器内容。");
            }

            var expectedParts = localFile.SizeBytes == 0
                ? 0
                : checked((int)((localFile.SizeBytes + partSize - 1) / partSize));
            if (expectedParts != serverFile.PartCount)
            {
                throw new InvalidDataException("服务器材料分片数量与本机固定清单不一致。");
            }

            var uploaded = serverFile.UploadedParts.ToHashSet();
            if (uploaded.Any(part => part < 0 || part >= expectedParts)
                || uploaded.Count != serverFile.UploadedParts.Count)
            {
                throw new InvalidDataException("服务器返回了无效或重复的已完成分片索引。");
            }

            for (var partIndex = 0; partIndex < expectedParts; partIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (uploaded.Contains(partIndex))
                {
                    continue;
                }

                var offset = checked((long)partIndex * partSize);
                var length = checked((int)Math.Min(partSize, localFile.SizeBytes - offset));
                await using var source = localFile.OpenRead();
                source.Position = offset;
                var bytes = new byte[length];
                await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                RequireOperationCurrent(operation);
                var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                await using var part = new MemoryStream(bytes, writable: false);
                var partResponse = await _api.UploadNoteMaterialPartAsync(
                        bundle.VersionId,
                        serverFile.Index,
                        partIndex,
                        digest,
                        part,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("服务器没有返回材料分片状态。");
                RequireOperationCurrent(operation);
                RequireSuccess(partResponse.Code, partResponse.Message);
            }
        }

        var completeResponse = await _api.CompleteNoteMaterialUploadAsync(
                bundle.VersionId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回材料完成状态。");
        RequireOperationCurrent(operation);
        upload = RequireResponse(completeResponse.Code, completeResponse.Message, completeResponse.Data);
        RequireUploadIdentity(upload, bundle.NoteId, bundle.VersionId);
        if (string.Equals(upload.Phase, "Uploading", StringComparison.Ordinal))
        {
            throw new InvalidDataException("服务器仍将材料版本标记为上传中；本机未生成完成绑定。");
        }

        var versions = await LoadVersionsAsync(noteId, operation, cancellationToken)
            .ConfigureAwait(false);
        var updatedBinding = new LocalNoteCloudBinding
        {
            NoteId = noteId,
            ServerUrl = account.ServerUrl,
            AccountId = account.AccountId,
            BaseVersionId = baseVersionId,
            CurrentVersionId = ParseOptionalGuid(upload.CurrentVersionId),
            LastVersionId = bundle.VersionId,
            Phase = upload.Phase,
            HasConflict = upload.HasConflict,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        RequireOperationCurrent(operation);
        await _store.SaveCloudBindingAsync(updatedBinding, cancellationToken);
        try
        {
            RequireOperationCurrent(operation);
        }
        catch
        {
            try
            {
                await _store.RemoveCloudBindingAsync(updatedBinding, CancellationToken.None);
            }
            catch
            {
                // The binding is still scoped to the original owner/server, never the new session.
            }

            throw;
        }

        return new CloudNoteMaterialSyncResult(upload, updatedBinding, versions);
    }

    public async Task<IReadOnlyList<NoteMaterialUploadDto>> GetVersionsAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        return await LoadVersionsAsync(noteId, operation, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one immutable owner/server version using the typed binary
    /// operation and imports it as a new local note. Account/deployment changes
    /// cancel pending I/O and are fenced again immediately before local commit.
    /// </summary>
    public async Task<CloudNoteMaterialRestoreResult> RestoreVersionAsync(
        Guid versionId,
        bool userConfirmedLocalCopy,
        CancellationToken cancellationToken = default,
        CloudNoteAccountScope? expectedAccountScope = null,
        long? expectedAccountGeneration = null)
    {
        if (!userConfirmedLocalCopy)
        {
            throw new InvalidOperationException("恢复云端材料前必须由用户明确确认生成本机副本。");
        }
        if (versionId == Guid.Empty)
        {
            throw new ArgumentException("版本身份不能为空。", nameof(versionId));
        }

        using var operation = BeginOperation();
        if (expectedAccountScope is not null
            && (!string.Equals(
                    expectedAccountScope.ServerUrl,
                    operation.Account.ServerUrl,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedAccountScope.AccountId,
                    operation.Account.AccountId,
                    StringComparison.Ordinal)
                || expectedAccountGeneration != AccountGeneration))
        {
            throw new InvalidOperationException(
                "所选服务器笔记来自已切换的账号或服务器；请刷新目录后重试。");
        }

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnScopeChanged() => linkedCancellation.Cancel();
        _api.ExternalScopeChanged += OnScopeChanged;
        string? stagingDirectory = null;
        try
        {
            RequireOperationCurrent(operation);
            var response = await _api.GetNoteMaterialVersionAsync(
                    versionId,
                    linkedCancellation.Token)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("服务器没有返回固定版本详情。");
            RequireOperationCurrent(operation);
            var serverVersion = RequireResponse(response.Code, response.Message, response.Data);
            ValidateServerVersion(serverVersion, versionId);

            var inputBytes = Encoding.UTF8.GetByteCount(serverVersion.InputJson);
            var manifestBytes = Encoding.UTF8.GetByteCount(serverVersion.ManifestJson);
            if (inputBytes > _restoreLimits.MaxMetadataEntryBytes
                || manifestBytes > _restoreLimits.MaxMetadataEntryBytes)
            {
                throw new InvalidDataException("服务器材料元数据超过客户端单项容量上限。");
            }

            var metadata = ServerNoteMaterialRestoreParser.Parse(
                serverVersion.InputJson,
                serverVersion.ManifestJson);
            if (metadata.NoteId != Guid.Parse(serverVersion.Upload.NoteId)
                || metadata.VersionId != versionId)
            {
                throw new InvalidDataException("服务器版本详情与材料 input 的身份不一致。");
            }

            if (metadata.Files.Count > _restoreLimits.MaxArchiveEntries)
            {
                throw new InvalidDataException("服务器恢复材料文件项超过客户端上限。");
            }

            var totalBytes = metadata.Files.Aggregate(
                checked((long)inputBytes + manifestBytes),
                (total, file) => checked(total + file.SizeBytes));
            if (totalBytes > _restoreLimits.MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException("服务器恢复材料总量超过客户端上限。");
            }

            stagingDirectory = Path.Combine(
                Path.GetTempPath(),
                "pim-server-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            var uploadFiles = ValidateUploadFiles(serverVersion.Upload, metadata.Files);
            var remainingBytes = _restoreLimits.MaxTotalUncompressedBytes - inputBytes - manifestBytes;
            foreach (var uploadFile in uploadFiles)
            {
                RequireOperationCurrent(operation);
                var manifestFile = metadata.Files.Single(file =>
                    string.Equals(file.Path, uploadFile.Path, StringComparison.Ordinal));
                var perFileLimit = manifestFile.Kind switch
                {
                    "Document" => _restoreLimits.MaxMetadataEntryBytes,
                    "Markdown" => _restoreLimits.MaxDocumentEntryBytes,
                    "RecordingManifest" => _restoreLimits.MaxMetadataEntryBytes,
                    _ => _restoreLimits.MaxMaterialEntryBytes,
                };
                if (manifestFile.SizeBytes > perFileLimit
                    || manifestFile.SizeBytes > remainingBytes)
                {
                    throw new InvalidDataException(
                        $"服务器材料文件超过客户端预算：{manifestFile.Path}");
                }

                var maxBytes = Math.Max(1, manifestFile.SizeBytes);
                await using var download = await _api.DownloadNoteMaterialFileAsync(
                        versionId,
                        uploadFile.Index,
                        maxBytes,
                        linkedCancellation.Token)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"服务器没有返回材料文件：{manifestFile.Path}");
                RequireOperationCurrent(operation);
                if (!string.Equals(
                        download.Headers.Sha256,
                        manifestFile.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"服务器材料响应摘要与固定清单不一致：{manifestFile.Path}");
                }
                if (!string.Equals(
                        download.Headers.FileName,
                        Path.GetFileName(manifestFile.Path),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"服务器材料响应文件名与固定清单不一致：{manifestFile.Path}");
                }

                var stagedPath = Path.Combine(
                    stagingDirectory,
                    stagedFiles.Count.ToString("D6", CultureInfo.InvariantCulture) + ".bin");
                var copied = await CopyDownloadToFileAsync(
                        download,
                        stagedPath,
                        manifestFile.SizeBytes,
                        maxBytes,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                RequireOperationCurrent(operation);
                if (copied.Length != manifestFile.SizeBytes
                    || !string.Equals(copied.Sha256, manifestFile.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"服务器材料实际字节与固定清单不一致：{manifestFile.Path}");
                }

                stagedFiles.Add(manifestFile.Path, stagedPath);
                remainingBytes = checked(remainingBytes - copied.Length);
            }

            var noteJson = await ReadBoundedUtf8Async(
                    stagedFiles["note.json"],
                    _restoreLimits.MaxMetadataEntryBytes,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            var markdown = await ReadBoundedUtf8Async(
                    stagedFiles["document.md"],
                    _restoreLimits.MaxDocumentEntryBytes,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            RequireOperationCurrent(operation);
            var parsed = ServerNoteMaterialRestoreParser.Parse(
                serverVersion.InputJson,
                serverVersion.ManifestJson,
                markdown);
            var restorePackage = await BuildRestorePackageAsync(
                    parsed,
                    noteJson,
                    serverVersion.InputJson,
                    stagedFiles,
                    stagingDirectory,
                    _restoreLimits,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            RequireOperationCurrent(operation);
            var import = await _store.ImportServerRestoreAsync(
                    restorePackage,
                    beforeCommit: () =>
                    {
                        RequireOperationCurrent(operation);
                        return Task.CompletedTask;
                    },
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            RequireOperationCurrent(operation);
            return new CloudNoteMaterialRestoreResult(serverVersion, import);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && linkedCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "云端笔记账号或服务器在恢复期间已切换；本次恢复已中止，未提交旧账号副本。");
        }
        finally
        {
            _api.ExternalScopeChanged -= OnScopeChanged;
            if (stagingDirectory is not null)
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    public async Task<NoteMaterialUploadDto> PromoteVersionAsync(
        Guid noteId,
        Guid versionId,
        Guid? expectedCurrentVersionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        var account = operation.Account;
        RequireOperationCurrent(operation);
        var response = await _api.PromoteNoteMaterialVersionAsync(
                versionId,
                new PromoteNoteMaterialVersionRequest
                {
                    ExpectedCurrentVersionId = expectedCurrentVersionId?.ToString("D"),
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回版本选择结果。");
        RequireOperationCurrent(operation);
        var upload = RequireResponse(response.Code, response.Message, response.Data);
        RequireUploadIdentity(upload, noteId, versionId);
        var updatedBinding = new LocalNoteCloudBinding
        {
            NoteId = noteId,
            ServerUrl = account.ServerUrl,
            AccountId = account.AccountId,
            BaseVersionId = ParseOptionalGuid(upload.BaseVersionId),
            CurrentVersionId = ParseOptionalGuid(upload.CurrentVersionId),
            LastVersionId = versionId,
            Phase = upload.Phase,
            HasConflict = upload.HasConflict,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        RequireOperationCurrent(operation);
        await _store.SaveCloudBindingAsync(updatedBinding, cancellationToken);
        try
        {
            RequireOperationCurrent(operation);
        }
        catch
        {
            try
            {
                await _store.RemoveCloudBindingAsync(updatedBinding, CancellationToken.None);
            }
            catch
            {
                // The binding remains owner/server scoped and cannot be consumed by the new session.
            }

            throw;
        }

        return upload;
    }

    public async Task<IReadOnlyList<NoteDerivedArtifactDto>> GetDerivedArtifactsAsync(
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        RequireOperationCurrent(operation);
        var response = await _api.GetNoteDerivedArtifactsAsync(noteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回派生结果。");
        RequireOperationCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    public async Task<NoteDerivedVersionDto> GetDerivedVersionAsync(
        Guid artifactId,
        Guid versionId,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        RequireOperationCurrent(operation);
        var response = await _api.GetNoteDerivedArtifactVersionAsync(
                artifactId,
                versionId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回派生版本。");
        RequireOperationCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    public async Task<NoteDerivedMutationDto> SaveManualRevisionAsync(
        Guid artifactId,
        Guid? expectedHeadVersionId,
        string expectedHeadRevision,
        string contentMarkdown,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        var request = new SaveNoteDerivedManualRevisionRequest
        {
            ContentMarkdown = contentMarkdown ?? string.Empty,
            ExpectedHeadRevision = expectedHeadRevision,
            ExpectedHeadVersionId = expectedHeadVersionId?.ToString("D"),
            RevisionId = Guid.NewGuid().ToString("D"),
        };
        RequireOperationCurrent(operation);
        var response = await _api.SaveNoteDerivedManualRevisionAsync(artifactId, request, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回人工修订结果。");
        RequireOperationCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    public async Task<NoteDerivedArtifactDto> SelectDerivedVersionAsync(
        Guid artifactId,
        Guid versionId,
        string expectedHeadRevision,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        var response = await _api.SelectNoteDerivedArtifactVersionAsync(
                artifactId,
                new SelectNoteDerivedVersionRequest
                {
                    ExpectedHeadRevision = expectedHeadRevision,
                    VersionId = versionId.ToString("D"),
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回版本选择结果。");
        RequireOperationCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    public async Task<IReadOnlyList<NoteAsrSourceDto>> GetMaterialSourcesAsync(
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        if (pageSize is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var result = new List<NoteAsrSourceDto>();
        for (var offset = 0; ; offset = checked(offset + pageSize))
        {
            RequireOperationCurrent(operation);
            var response = await _api.GetNoteAsrSourcesAsync(offset, pageSize, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("服务器没有返回材料版本目录。");
            RequireOperationCurrent(operation);
            var page = RequireResponse(response.Code, response.Message, response.Data);
            result.AddRange(page.Items);
            if (result.Count >= page.Total || page.Items.Count == 0)
            {
                return result;
            }
        }
    }

    public async Task<IReadOnlyList<NoteDerivedTaskDto>> GetDerivedTasksAsync(
        Guid? noteId = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation();
        var response = await _api.GetNoteDerivedTasksAsync(noteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回派生任务。");
        RequireOperationCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    public long AccountGeneration => Interlocked.Read(ref _accountGeneration);

    public CloudNoteAccountScope? CurrentAccountScope
    {
        get
        {
            try
            {
                return _accountProvider();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }

    private async Task<IReadOnlyList<NoteMaterialUploadDto>> LoadVersionsAsync(
        Guid noteId,
        CloudOperation operation,
        CancellationToken cancellationToken)
    {
        RequireOperationCurrent(operation);
        var response = await _api.GetNoteMaterialVersionsAsync(noteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回固定版本列表。");
        RequireOperationCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data);
    }

    private static void ValidateServerVersion(NoteMaterialVersionDto version, Guid versionId)
    {
        if (version.Upload is null
            || version.Upload.Files is null
            || !Guid.TryParse(version.Upload.NoteId, out var noteId)
            || noteId == Guid.Empty
            || !Guid.TryParse(version.Upload.VersionId, out var actualVersionId)
            || actualVersionId != versionId
            || string.Equals(version.Upload.Phase, "Uploading", StringComparison.OrdinalIgnoreCase)
            || version.Upload.Files.Count == 0)
        {
            throw new InvalidDataException("服务器固定版本尚未完成或身份无效。");
        }
    }

    private static IReadOnlyList<NoteMaterialUploadFileDto> ValidateUploadFiles(
        NoteMaterialUploadDto upload,
        IReadOnlyList<ServerNoteMaterialRestoreFile> manifestFiles)
    {
        if (upload.Files.Count != manifestFiles.Count
            || upload.Files.Select(file => file.Index).Distinct().Count() != manifestFiles.Count)
        {
            throw new InvalidDataException("服务器固定版本文件索引与 manifest 数量不一致。");
        }

        var byPath = manifestFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var ordered = upload.Files.OrderBy(file => file.Index).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var file = ordered[index];
            if (file.Index != index
                || !byPath.TryGetValue(file.Path, out var manifest)
                || !string.Equals(file.Kind, manifest.Kind, StringComparison.Ordinal)
                || !string.Equals(file.Sha256, manifest.Sha256, StringComparison.Ordinal)
                || file.SizeBytes != manifest.SizeBytes)
            {
                throw new InvalidDataException(
                    $"服务器固定版本文件索引与 manifest 不一致：{file.Path}");
            }
        }

        return ordered;
    }

    private static async Task<(string Sha256, long Length)> CopyDownloadToFileAsync(
        NoteMaterialFileDownload download,
        string path,
        long expectedLength,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var target = new FileStream(
            path,
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
            var read = await download.Content.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length = checked(length + read);
            if (length > expectedLength || length > maxBytes)
            {
                throw new InvalidDataException("服务器材料响应超过固定版本声明长度。");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static async Task<string> ReadBoundedUtf8Async(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length > maxBytes)
        {
            throw new InvalidDataException("服务器文本材料超过客户端上限。");
        }

        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(length, 64 * 1024));
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException("服务器文本材料读取超过客户端上限。");
            }

            output.Write(buffer, 0, read);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(output.ToArray());
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("服务器文本材料不是有效 UTF-8。", ex);
        }
    }

    private static async Task<LocalNoteServerRestorePackage> BuildRestorePackageAsync(
        ServerNoteMaterialRestorePackage parsed,
        string noteJson,
        string inputJson,
        IReadOnlyDictionary<string, string> stagedFiles,
        string stagingDirectory,
        LocalNoteImportLimits restoreLimits,
        CancellationToken cancellationToken)
    {
        using var document = System.Text.Json.JsonDocument.Parse(noteJson);
        using var input = System.Text.Json.JsonDocument.Parse(inputJson);
        var note = document.RootElement;
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(note.GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(
                    input.RootElement.GetProperty("note").GetRawText())))
        {
            throw new InvalidDataException("服务器 note.json 与 input.note 内容不一致。");
        }
        if (!note.TryGetProperty("blocks", out var _))
        {
            throw new InvalidDataException("服务器 note.json 缺少 blocks。");
        }

        var fileByBlock = parsed.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.BlockId))
            .ToDictionary(file => file.BlockId!, StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal)
        {
            "note.json",
            "document.md",
        };
        var materials = new List<LocalNoteServerMaterialInput>();
        var ids = new HashSet<Guid>();
        var photoIndex = 0;
        var inkIndex = 0;
        foreach (var block in parsed.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Kind == "markdown")
            {
                continue;
            }

            var materialFile = fileByBlock[block.Id];
            consumed.Add(materialFile.Path);
            if (block.Kind == "photo")
            {
                var extension = block.MimeType switch
                {
                    "image/png" => "png",
                    "image/jpeg" => "jpg",
                    "image/webp" => "webp",
                    _ => throw new InvalidDataException("服务器照片媒体类型无效。"),
                };
                var materialId = Guid.Parse(block.AssetId!);
                if (!ids.Add(materialId))
                {
                    throw new InvalidDataException("服务器照片块重复引用同一材料标识。");
                }

                photoIndex++;
                materials.Add(new LocalNoteServerMaterialInput(
                    materialId,
                    LocalNoteMaterialKind.Photo,
                    $"照片-{photoIndex:D3}.{extension}",
                    block.Sha256!,
                    block.MimeType!,
                    materialFile.SizeBytes,
                    stagedFiles[materialFile.Path],
                    ToPixel(block.Width),
                    ToPixel(block.Height)));
                continue;
            }

            inkIndex++;
            var ink = block.InkJson
                ?? throw new InvalidDataException("服务器手写块缺少笔迹 JSON。");
            var localInk = ConvertServerInkToLocalSource(ink, block.Width, block.Height);
            var inkPath = Path.Combine(stagingDirectory, $"ink-{inkIndex:D3}.json");
            await File.WriteAllTextAsync(
                    inkPath,
                    localInk,
                    new UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
            var inkBytes = Encoding.UTF8.GetBytes(localInk);
            var inkSha = Convert.ToHexString(SHA256.HashData(inkBytes)).ToLowerInvariant();
            materials.Add(new LocalNoteServerMaterialInput(
                Guid.NewGuid(),
                LocalNoteMaterialKind.HandwritingSource,
                $"手写-{inkIndex:D3}.ink.json",
                inkSha,
                "application/vnd.pim.ink+json",
                inkBytes.Length,
                inkPath,
                ToPixel(block.Width),
                ToPixel(block.Height)));
            materials.Add(new LocalNoteServerMaterialInput(
                Guid.NewGuid(),
                LocalNoteMaterialKind.Handwriting,
                $"手写-{inkIndex:D3}.png",
                materialFile.Sha256,
                "image/png",
                materialFile.SizeBytes,
                stagedFiles[materialFile.Path],
                ToPixel(block.Width),
                ToPixel(block.Height)));
        }

        var recordingManifests = parsed.Files
            .Where(file => file.Kind == "RecordingManifest")
            .ToArray();
        if (!input.RootElement.TryGetProperty("recordings", out var rawRecordings)
            || rawRecordings.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new InvalidDataException("服务器 input 缺少 recordings 数组。");
        }
        var inputRecordings = rawRecordings.EnumerateArray()
            .ToDictionary(
                recording => Guid.Parse(recording.GetProperty("id").GetString()!),
                recording => recording);
        if (inputRecordings.Count != recordingManifests.Length
            || parsed.Files.Count(file => file.Kind == "AudioChunk") != recordingManifests.Length)
        {
            throw new InvalidDataException("服务器录音 input/manifest/音频分片数量不一致。");
        }
        foreach (var recordingManifest in recordingManifests)
        {
            consumed.Add(recordingManifest.Path);
            var chunks = parsed.Files
                .Where(file => file.Kind == "AudioChunk"
                    && string.Equals(
                        file.RecordingId,
                        recordingManifest.RecordingId,
                        StringComparison.Ordinal))
                .ToArray();
            if (chunks.Length != 1)
            {
                throw new InvalidDataException(
                    "当前客户端只支持 schema-v1 单分片录音恢复；未保存不完整副本。");
            }

            var chunk = chunks[0];
            consumed.Add(chunk.Path);
            var recordingId = Guid.Parse(recordingManifest.RecordingId!);
            if (!ids.Add(recordingId))
            {
                throw new InvalidDataException("服务器录音材料标识重复。");
            }

            if (!inputRecordings.TryGetValue(recordingId, out var recordingJson))
            {
                throw new InvalidDataException("服务器录音 input 缺少对应 manifest 身份。");
            }
            var recordingManifestText = await ReadBoundedUtf8Async(
                    stagedFiles[recordingManifest.Path],
                    restoreLimits.MaxMetadataEntryBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            using var downloadedRecording = System.Text.Json.JsonDocument.Parse(recordingManifestText);
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(downloadedRecording.RootElement.GetRawText()),
                    System.Text.Json.Nodes.JsonNode.Parse(recordingJson.GetRawText())))
            {
                throw new InvalidDataException("服务器 recording manifest 与 input 记录不一致。");
            }

            if (Guid.Parse(recordingJson.GetProperty("noteId").GetString()!) != parsed.NoteId
                || recordingJson.GetProperty("schemaVersion").GetInt32() != 1
                || !string.Equals(
                    recordingJson.GetProperty("phase").GetString(),
                    "Completed",
                    StringComparison.Ordinal)
                || !recordingJson.TryGetProperty("chunks", out var recordingChunks)
                || recordingChunks.ValueKind != System.Text.Json.JsonValueKind.Array
                || recordingChunks.GetArrayLength() != 1
                || recordingChunks[0].GetProperty("samples").GetInt64() != chunk.Samples)
            {
                throw new InvalidDataException("服务器录音 input 与单分片材料元数据不一致。");
            }

            materials.Add(new LocalNoteServerMaterialInput(
                recordingId,
                LocalNoteMaterialKind.Recording,
                $"录音-{materials.Count + 1:D3}.wav",
                chunk.Sha256,
                "audio/wav",
                chunk.SizeBytes,
                stagedFiles[chunk.Path],
                ExpectedSamples: chunk.Samples));
        }

        if (consumed.Count != parsed.Files.Count)
        {
            throw new InvalidDataException("服务器材料清单包含客户端无法映射的文件。");
        }

        return new LocalNoteServerRestorePackage(
            parsed.NoteId,
            parsed.VersionId,
            parsed.Title,
            parsed.Markdown,
            materials);
    }

    private static string ConvertServerInkToLocalSource(
        string inkJson,
        double? blockWidth,
        double? blockHeight)
    {
        using var document = System.Text.Json.JsonDocument.Parse(inkJson);
        var root = document.RootElement;
        var width = root.GetProperty("width").GetDouble();
        var height = root.GetProperty("height").GetDouble();
        if (blockWidth is { } expectedWidth && Math.Abs(width - expectedWidth) > 1
            || blockHeight is { } expectedHeight && Math.Abs(height - expectedHeight) > 1
            || !root.TryGetProperty("strokes", out var strokes)
            || strokes.ValueKind != System.Text.Json.JsonValueKind.Array
            || strokes.GetArrayLength() == 0)
        {
            throw new InvalidDataException("服务器手写块尺寸或笔迹无效。");
        }

        var localStrokes = new List<List<object>>();
        foreach (var stroke in strokes.EnumerateArray())
        {
            if (!stroke.TryGetProperty("points", out var points)
                || points.ValueKind != System.Text.Json.JsonValueKind.Array
                || points.GetArrayLength() == 0)
            {
                throw new InvalidDataException("服务器手写块包含空笔画。");
            }

            var localPoints = new List<object>();
            foreach (var point in points.EnumerateArray())
            {
                var x = point.GetProperty("x").GetDouble();
                var y = point.GetProperty("y").GetDouble();
                var pressure = point.GetProperty("pressure").GetDouble();
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(pressure)
                    || x < 0 || x > width || y < 0 || y > height || pressure is < 0 or > 1)
                {
                    throw new InvalidDataException("服务器手写笔迹点超出画布或压力范围。");
                }

                localPoints.Add(new { x, y, pressure });
            }

            localStrokes.Add(localPoints);
        }

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            width,
            height,
            strokes = localStrokes,
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private static int? ToPixel(double? value)
        => value is null
            ? null
            : checked((int)Math.Round(value.Value, MidpointRounding.AwayFromZero));

    private CloudOperation BeginOperation()
    {
        var account = RequireAccount();
        return new CloudOperation(account, _api.BeginOperationScope());
    }

    private void OnExternalScopeChanged()
        => Interlocked.Increment(ref _accountGeneration);

    private void RequireOperationCurrent(CloudOperation operation)
    {
        CloudNoteAccountScope current;
        try
        {
            current = RequireAccount();
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                "云端笔记账号或服务器在操作期间已切换；本次操作已中止，未写入旧账号回执。");
        }

        if (!string.Equals(current.ServerUrl, operation.Account.ServerUrl, StringComparison.Ordinal)
            || !string.Equals(current.AccountId, operation.Account.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "云端笔记账号或服务器在操作期间已切换；本次操作已中止，未写入旧账号回执。");
        }

        try
        {
            _api.EnsureOperationScopeCurrent();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                "云端笔记账号或服务器在操作期间已切换；本次操作已中止，未写入旧账号回执。",
                ex);
        }
    }

    private CloudNoteAccountScope RequireAccount()
    {
        return _accountProvider()
            ?? throw new InvalidOperationException("云端笔记需要先登录；本机固定版本仍可离线使用。");
    }

    private static Guid? ParseOptionalGuid(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Guid.Parse(value);

    private static void RequireUploadIdentity(
        NoteMaterialUploadDto upload,
        Guid expectedNoteId,
        Guid expectedVersionId)
    {
        if (!Guid.TryParse(upload.NoteId, out var noteId)
            || !Guid.TryParse(upload.VersionId, out var versionId)
            || noteId != expectedNoteId
            || versionId != expectedVersionId)
        {
            throw new InvalidDataException("服务器返回的材料版本身份与本次固定版本不一致。");
        }
    }

    private static void RequireSuccess(int code, string message)
    {
        if (code != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "服务器操作失败。" : message);
        }
    }

    private static T RequireResponse<T>(int code, string message, T? data)
        where T : class
    {
        RequireSuccess(code, message);

        return data ?? throw new InvalidDataException("服务器响应没有数据。");
    }

    private sealed class CloudOperation(
        CloudNoteAccountScope account,
        ApiClient.ApiOperationScope apiScope) : IDisposable
    {
        public CloudNoteAccountScope Account { get; } = account;
        public void Dispose() => apiScope.Dispose();
    }
}
