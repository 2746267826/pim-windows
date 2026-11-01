using System.Globalization;
using System.Security.Cryptography;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

public sealed record CloudNoteMaterialSyncResult(
    NoteMaterialUploadDto Upload,
    LocalNoteCloudBinding Binding,
    IReadOnlyList<NoteMaterialUploadDto> Versions);

public sealed record CloudNoteAccountScope(string ServerUrl, string AccountId);

/// <summary>Uploads immutable local versions and reads owner-bound server versions and results.</summary>
public sealed class CloudNoteMaterialService
{
    private readonly ApiClient _api;
    private readonly LocalNoteStore _store;
    private readonly ServerNoteMaterialBundleBuilder _builder;
    private readonly Func<CloudNoteAccountScope?> _accountProvider;
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
        Func<CloudNoteAccountScope?> accountProvider)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _accountProvider = accountProvider ?? throw new ArgumentNullException(nameof(accountProvider));
        _builder = new ServerNoteMaterialBundleBuilder(store);
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
