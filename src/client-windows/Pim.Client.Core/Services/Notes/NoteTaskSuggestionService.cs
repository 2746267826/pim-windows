using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services.Notes;

public sealed record NoteTaskSuggestionEditDraft(
    string ExpectedRevision,
    string Title,
    string? Description,
    int Priority,
    DateTimeOffset? Due,
    string? TaskBookId,
    string? CalendarId);

public sealed record NoteTaskSuggestionSource(
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    string ContentSha256,
    string InputSha256,
    string ManifestSha256,
    string? GenerationTaskId);

public sealed record NoteTaskSuggestionOperationFence(
    CloudNoteAccountScope Account,
    long AccountGeneration);

public sealed record NoteTaskSuggestionPendingConfirmation(
    Guid SuggestionId,
    string ExpectedRevision,
    Guid RequestId,
    Guid NoteId,
    Guid ArtifactId,
    Guid VersionId,
    string ContentSha256,
    string InputSha256,
    string ManifestSha256,
    string? GenerationTaskId);

public sealed record NoteTaskSuggestionReconciliationResult(
    int Checked,
    int Cleared,
    int StillPending);

/// <summary>Owner-bound native workflow for extracting and confirming note tasks.</summary>
public sealed class NoteTaskSuggestionService
{
    private const int MaxSourceCharacters = 180_000;
    private const int SuggestionWriteGateCount = 64;
    private static readonly SemaphoreSlim[] SuggestionWriteGates = Enumerable.Range(0, SuggestionWriteGateCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly ApiClient _api;
    private readonly LocalNoteTaskSuggestionStore _local;
    private readonly Func<CloudNoteAccountScope?> _accountProvider;
    private readonly Func<long> _accountGenerationProvider;

    public NoteTaskSuggestionService(
        ApiClient api,
        LocalNoteStore notes,
        Func<CloudNoteAccountScope?> accountProvider,
        Func<long>? accountGenerationProvider = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        ArgumentNullException.ThrowIfNull(notes);
        _accountProvider = accountProvider ?? throw new ArgumentNullException(nameof(accountProvider));
        _accountGenerationProvider = accountGenerationProvider ?? (() => 0);
        _local = new LocalNoteTaskSuggestionStore(Path.Combine(notes.Root, "task-suggestions"));
    }

    public NoteTaskSuggestionOperationFence CaptureFence()
        => new(RequireAccount(), _accountGenerationProvider());

    public async Task<NoteTaskSuggestionExtractionDto> StartExtractionAsync(
        Guid expectedNoteId,
        NoteDerivedArtifactDto artifact,
        NoteDerivedVersionSummaryDto versionSummary,
        NoteDerivedVersionDto version,
        NoteDerivedDestinationDto destination,
        bool userConfirmedPossibleCharge,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        if (!userConfirmedPossibleCharge)
            throw new InvalidOperationException("提取前必须明确确认模型目标与可能产生的费用。");
        var source = ValidateSource(expectedNoteId, artifact, versionSummary, version);
        ValidateDestination(destination);

        using var operation = BeginOperation(expectedFence);
        var request = await _local.GetOrCreateRequestAsync(
            operation.Account,
            expectedNoteId,
            Guid.Parse(artifact.ArtifactId),
            Guid.Parse(version.VersionId),
            source.ContentSha256,
            destination,
            cancellationToken).ConfigureAwait(false);
        RequireCurrent(operation);

        var response = await _api.StartNoteTaskSuggestionExtractionAsync(
                new StartNoteTaskSuggestionExtractionRequest
                {
                    AcknowledgePossibleCharge = true,
                    ArtifactId = artifact.ArtifactId,
                    Consent = new NoteDerivedGenerationConsent
                    {
                        Confirmed = true,
                        Endpoint = destination.Endpoint,
                        ModelId = destination.ModelId,
                        ModelRevision = destination.ModelRevision,
                    },
                    ExpectedContentSha256 = source.ContentSha256,
                    ExpectedUserId = operation.Account.AccountId,
                    NoteId = expectedNoteId.ToString("D"),
                    RequestId = request.ToString("D"),
                    VersionId = version.VersionId,
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回任务建议提取记录。");
        RequireCurrent(operation);
        var extraction = RequireResponse(response.Code, response.Message, response.Data);
        RequireExtractionIdentity(extraction, source, request);

        return extraction;
    }

    public async Task<IReadOnlyList<NoteDerivedDestinationDto>> GetDestinationsAsync(
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        using var operation = BeginOperation(expectedFence);
        var response = await _api.GetNoteDerivedDestinationsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回可用文本模型目录。");
        RequireCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data).Items;
    }

    public async Task<IReadOnlyList<TaskBookDto>> GetTaskBooksAsync(
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        using var operation = BeginOperation(expectedFence);
        var response = await _api.GetTaskBooksAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回任务本目录。");
        RequireCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data) ?? [];
    }

    public async Task<IReadOnlyList<CalendarResponse>> GetCalendarsAsync(
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        using var operation = BeginOperation(expectedFence);
        var response = await _api.GetCalendarsAsync(ct: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回日历目录。");
        RequireCurrent(operation);
        return RequireResponse(response.Code, response.Message, response.Data) ?? [];
    }

    public async Task<NoteTaskSuggestionExtractionDto> GetExtractionAsync(
        Guid extractionId,
        NoteTaskSuggestionSource source,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var operation = BeginOperation(expectedFence);
        var response = await _api.GetNoteTaskSuggestionExtractionAsync(extractionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回提取状态。");
        RequireCurrent(operation);
        var extraction = RequireResponse(response.Code, response.Message, response.Data);
        RequireExtractionIdentity(extraction, source, Guid.Parse(extraction.RequestId));
        return extraction;
    }

    public async Task<IReadOnlyList<NoteTaskSuggestionDto>> GetDraftSuggestionsAsync(
        Guid noteId,
        NoteTaskSuggestionSource source,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.NoteId != noteId)
            throw new ArgumentException("任务建议来源与笔记不一致。", nameof(source));
        using var operation = BeginOperation(expectedFence);
        var response = await _api.GetNoteTaskSuggestionsAsync(
                noteId,
                NoteTaskSuggestionStatusDto.Draft,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回任务建议。");
        RequireCurrent(operation);
        var list = RequireResponse(response.Code, response.Message, response.Data);
        return list.Items
            .Where(item => Matches(item.NoteId, noteId)
                           && item.Status == NoteTaskSuggestionStatusDto.Draft
                           && Matches(item.ArtifactId, source.ArtifactId)
                           && Matches(item.VersionId, source.VersionId)
                           && SameHash(item.SourceContentSha256, source.ContentSha256)
                           && SameHash(item.SourceInputSha256, source.InputSha256)
                           && SameHash(item.SourceManifestSha256, source.ManifestSha256)
                           && string.Equals(item.SourceGenerationTaskId, source.GenerationTaskId, StringComparison.Ordinal))
            .ToArray();
    }

    public async Task<NoteTaskSuggestionEditDraft?> LoadDraftAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        using var operation = BeginOperation(expectedFence);
        var draft = await _local.LoadDraftAsync(
            operation.Account,
            suggestionId,
            cancellationToken).ConfigureAwait(false);
        RequireCurrent(operation);
        return draft;
    }

    public async Task SaveLocalDraftAsync(
        Guid suggestionId,
        NoteTaskSuggestionEditDraft draft,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("任务建议标识不能为空。", nameof(suggestionId));
        ValidateDraft(draft);

        // Local drafts deliberately continue to work after an account switch or
        // while a confirmation is awaiting GET verification. The file remains
        // scoped to the account/server captured when the editor was opened.
        var account = expectedFence?.Account ?? RequireAccount();
        await _local.SaveDraftAsync(account, suggestionId, draft, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<NoteTaskSuggestionPendingConfirmation?> GetPendingConfirmationAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("任务建议标识不能为空。", nameof(suggestionId));
        using var operation = BeginOperation(expectedFence);
        var pending = await _local.GetPendingConfirmationAsync(
                operation.Account,
                suggestionId,
                cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        return pending;
    }

    public async Task<NoteTaskSuggestionReconciliationResult> ReconcilePendingConfirmationsAsync(
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        using var operation = BeginOperation(expectedFence);
        var pendingConfirmations = await _local.ListPendingConfirmationsAsync(
                operation.Account,
                cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);

        var cleared = 0;
        var stillPending = 0;
        foreach (var pending in pendingConfirmations)
        {
            var response = await _api.GetNoteTaskSuggestionAsync(
                    pending.SuggestionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "无法核实待确认的任务建议；确认凭据仍保留，不会生成新的确认请求。");
            RequireCurrent(operation);
            var suggestion = RequireResponse(response.Code, response.Message, response.Data);
            RequirePendingSuggestionIdentity(suggestion, pending);

            if (suggestion.Status == NoteTaskSuggestionStatusDto.Draft)
            {
                if (!string.Equals(
                        suggestion.Revision,
                        pending.ExpectedRevision,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "待确认请求冻结的修订已被服务器替换；确认凭据仍保留，需人工核对。");
                }

                stillPending++;
                continue;
            }

            if (suggestion.Status == NoteTaskSuggestionStatusDto.Confirmed)
            {
                if (!Guid.TryParse(suggestion.TaskId, out _))
                    throw new InvalidDataException(
                        "服务器已确认建议但没有有效任务标识；确认凭据仍保留。");
            }
            else if (suggestion.Status != NoteTaskSuggestionStatusDto.Dismissed)
            {
                throw new InvalidDataException("服务器返回了不受支持的任务建议状态。");
            }

            RequireCurrent(operation);
            await _local.DeleteConfirmationRequestAsync(
                    operation.Account,
                    pending.SuggestionId,
                    pending.ExpectedRevision,
                    pending.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireCurrent(operation);
            await _local.DeleteDraftAsync(
                    operation.Account,
                    pending.SuggestionId,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireCurrent(operation);
            cleared++;
        }

        return new NoteTaskSuggestionReconciliationResult(
            pendingConfirmations.Count,
            cleared,
            stillPending);
    }

    public async Task DeleteLocalDraftAsync(
        Guid suggestionId,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("任务建议标识不能为空。", nameof(suggestionId));
        using var operation = BeginOperation(expectedFence);
        await _local.DeleteDraftAsync(operation.Account, suggestionId, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
    }

    public async Task<NoteTaskSuggestionDto> SaveEditAsync(
        NoteTaskSuggestionDto suggestion,
        NoteTaskSuggestionEditDraft draft,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ValidateDraft(draft);
        if (suggestion.Status != NoteTaskSuggestionStatusDto.Draft
            || !Guid.TryParse(suggestion.Id, out var suggestionId))
            throw new InvalidOperationException("只有有效的草稿建议可以编辑。");
        if (!string.Equals(RequireRevision(draft.ExpectedRevision), suggestion.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("任务建议已产生新修订；请刷新后再编辑，原草稿仍保留。");

        using var operation = BeginOperation(expectedFence);
        using var suggestionGate = await AcquireSuggestionWriteGateAsync(suggestionId, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        await EnsureNoPendingConfirmationAsync(operation, suggestionId, cancellationToken).ConfigureAwait(false);
        await _local.SaveDraftAsync(operation.Account, suggestionId, draft, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        var response = await _api.UpdateNoteTaskSuggestionAsync(
                suggestionId,
                new UpdateNoteTaskSuggestionRequest
                {
                    CalendarId = draft.CalendarId,
                    Description = draft.Description,
                    Due = draft.Due,
                    ExpectedRevision = draft.ExpectedRevision,
                    Priority = draft.Priority,
                    TaskBookId = draft.TaskBookId,
                    Title = draft.Title,
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回编辑后的任务建议；本机草稿仍保留。");
        RequireCurrent(operation);
        var updated = RequireResponse(response.Code, response.Message, response.Data);
        RequireSuggestionIdentity(updated, suggestion, NoteTaskSuggestionStatusDto.Draft);
        RequireRevision(updated.Revision);
        RequireCurrent(operation);
        await _local.DeleteDraftIfMatchesAsync(
                operation.Account,
                suggestionId,
                draft,
                cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        return updated;
    }

    public async Task<NoteTaskSuggestionDto> DismissAsync(
        NoteTaskSuggestionDto suggestion,
        string expectedRevision,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.Status != NoteTaskSuggestionStatusDto.Draft
            || !Guid.TryParse(suggestion.Id, out var suggestionId))
            throw new InvalidOperationException("只有有效的草稿建议可以放弃。");
        expectedRevision = RequireMatchingRevision(expectedRevision, suggestion);

        using var operation = BeginOperation(expectedFence);
        using var suggestionGate = await AcquireSuggestionWriteGateAsync(suggestionId, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        await EnsureNoPendingConfirmationAsync(operation, suggestionId, cancellationToken).ConfigureAwait(false);
        var response = await _api.DismissNoteTaskSuggestionAsync(
                suggestionId,
                new DismissNoteTaskSuggestionRequest { ExpectedRevision = RequireRevision(expectedRevision) },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回放弃结果。");
        RequireCurrent(operation);
        var dismissed = RequireResponse(response.Code, response.Message, response.Data);
        RequireSuggestionIdentity(dismissed, suggestion, NoteTaskSuggestionStatusDto.Dismissed);
        RequireRevision(dismissed.Revision);
        RequireCurrent(operation);
        await _local.DeleteDraftAsync(operation.Account, suggestionId, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        return dismissed;
    }

    public async Task<NoteTaskSuggestionDto> ConfirmAsync(
        NoteTaskSuggestionDto suggestion,
        string expectedRevision,
        CancellationToken cancellationToken = default,
        NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.Status != NoteTaskSuggestionStatusDto.Draft
            || !Guid.TryParse(suggestion.Id, out var suggestionId))
            throw new InvalidOperationException("只有有效的草稿建议可以确认并写入任务本。");
        expectedRevision = RequireMatchingRevision(expectedRevision, suggestion);

        using var operation = BeginOperation(expectedFence);
        using var suggestionGate = await AcquireSuggestionWriteGateAsync(suggestionId, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        var pending = await _local.GetPendingConfirmationAsync(
            operation.Account,
            suggestionId,
            cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            RequirePendingMatchesSuggestion(pending, suggestion, expectedRevision);
        }

        var requestId = pending?.RequestId ?? await _local.GetOrCreateConfirmationRequestAsync(
                operation.Account,
                suggestion,
                expectedRevision,
                cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        var response = await _api.ConfirmNoteTaskSuggestionAsync(
                suggestionId,
                new ConfirmNoteTaskSuggestionRequest
                {
                    ExpectedRevision = expectedRevision,
                    RequestId = requestId.ToString("D"),
                },
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("服务器没有返回确认结果；可安全重试同一幂等请求。");
        RequireCurrent(operation);
        var confirmed = RequireResponse(response.Code, response.Message, response.Data);
        RequireSuggestionIdentity(confirmed, suggestion, NoteTaskSuggestionStatusDto.Confirmed);
        if (!Guid.TryParse(confirmed.TaskId, out _))
            throw new InvalidDataException("服务器确认了建议，但没有返回实际任务标识。");
        var verifiedResponse = await _api.GetNoteTaskSuggestionAsync(suggestionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("确认回执已收到但无法读取服务器状态；确认凭据仍保留，可用同一请求安全重试。");
        RequireCurrent(operation);
        var verified = RequireResponse(verifiedResponse.Code, verifiedResponse.Message, verifiedResponse.Data);
        RequireSuggestionIdentity(verified, suggestion, NoteTaskSuggestionStatusDto.Confirmed);
        if (!string.Equals(verified.TaskId, confirmed.TaskId, StringComparison.Ordinal))
            throw new InvalidDataException("服务器确认状态的任务标识与确认回执不一致；确认凭据仍保留。");
        RequireCurrent(operation);
        await _local.DeleteConfirmationRequestAsync(
                operation.Account,
                suggestionId,
                expectedRevision,
                requestId,
                cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        await _local.DeleteDraftAsync(operation.Account, suggestionId, cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        return confirmed;
    }

    private static async ValueTask<IDisposable> AcquireSuggestionWriteGateAsync(
        Guid suggestionId,
        CancellationToken cancellationToken)
    {
        var gate = SuggestionWriteGates[(suggestionId.GetHashCode() & int.MaxValue) % SuggestionWriteGates.Length];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreLease(gate);
    }

    private sealed class SemaphoreLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private Operation BeginOperation(NoteTaskSuggestionOperationFence? expectedFence = null)
    {
        var account = RequireAccount();
        var generation = _accountGenerationProvider();
        if (expectedFence is not null
            && (expectedFence.Account != account || expectedFence.AccountGeneration != generation))
        {
            throw new InvalidOperationException(
                "任务建议窗口来自已切换的账号或服务器；请刷新后重试，未发送旧会话请求。");
        }

        return new Operation(account, generation, _api.BeginOperationScope());
    }

    private void RequireCurrent(Operation operation)
    {
        var current = RequireAccount();
        if (current != operation.Account || _accountGenerationProvider() != operation.AccountGeneration)
            throw new InvalidOperationException("账号或服务器已切换；本次任务建议操作已中止，未采用旧回执。");
        try
        {
            _api.EnsureOperationScopeCurrent();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("登录会话或服务器已改变；本次操作已中止，原草稿保留。", ex);
        }
    }

    private CloudNoteAccountScope RequireAccount()
        => _accountProvider()
           ?? throw new InvalidOperationException("任务建议需要先登录；本机笔记仍可离线使用。");

    private static NoteTaskSuggestionSource ValidateSource(
        Guid noteId,
        NoteDerivedArtifactDto artifact,
        NoteDerivedVersionSummaryDto summary,
        NoteDerivedVersionDto version)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(version);
        if (noteId == Guid.Empty
            || version.ContentMarkdown is null
            || !Matches(artifact.NoteId, noteId)
            || !Guid.TryParse(artifact.ArtifactId, out var artifactId)
            || !Matches(version.ArtifactId, artifactId)
            || !Guid.TryParse(version.VersionId, out var versionId)
            || !Matches(summary.VersionId, versionId)
            || !artifact.Versions.Any(item =>
                Matches(item.VersionId, versionId)
                && SameHash(item.ContentSha256, summary.ContentSha256))
            || !SameHash(version.ContentSha256, summary.ContentSha256)
            || !SameHash(version.ContentSha256, HashText(version.ContentMarkdown))
            || !IsSha256(version.ContentSha256)
            || !IsSha256(version.InputSha256)
            || !IsSha256(version.ManifestSha256))
        {
            throw new InvalidDataException("任务建议来源必须是所选笔记中的完整固定结果版本。");
        }

        if (version.ContentMarkdown.Length > MaxSourceCharacters)
            throw new InvalidDataException("结果正文超过任务提取上限；不会静默截断来源。");

        return new NoteTaskSuggestionSource(
            noteId,
            artifactId,
            versionId,
            version.ContentSha256,
            version.InputSha256,
            version.ManifestSha256,
            version.GenerationTaskId);
    }

    private static void ValidateDestination(NoteDerivedDestinationDto destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (string.IsNullOrWhiteSpace(destination.ModelId)
            || string.IsNullOrWhiteSpace(destination.ModelRevision)
            || string.IsNullOrWhiteSpace(destination.Endpoint)
            || destination.Capabilities is null
            || !destination.Capabilities.Contains("Text", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目录缺少有效的文本模型、修订或目标地址。");
    }

    private static void ValidateDraft(NoteTaskSuggestionEditDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _ = RequireRevision(draft.ExpectedRevision);
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Trim().Length > 255)
            throw new ArgumentException("任务标题必须为 1 到 255 个字符。", nameof(draft));
        if (draft.Description?.Length > 20_000)
            throw new ArgumentException("任务描述不能超过 20000 个字符。", nameof(draft));
        if (draft.Priority is < 1 or > 9)
            throw new ArgumentOutOfRangeException(nameof(draft), "任务优先级必须在 1 到 9 之间。");
        ValidateOptionalId(draft.TaskBookId, nameof(draft.TaskBookId));
        ValidateOptionalId(draft.CalendarId, nameof(draft.CalendarId));
    }

    private static void ValidateOptionalId(string? value, string name)
    {
        if (value is not null && !Guid.TryParse(value, out _))
            throw new ArgumentException("任务目标标识无效。", name);
    }

    private static string RequireRevision(string value)
        => Guid.TryParse(value, out _) ? value : throw new ArgumentException("任务建议修订标识无效。", nameof(value));

    private static string RequireMatchingRevision(string value, NoteTaskSuggestionDto suggestion)
    {
        var revision = RequireRevision(value);
        if (!string.Equals(revision, suggestion.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("任务建议已产生新修订；请刷新后再操作，未发送旧 CAS 请求。");
        return revision;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool SameHash(string? left, string? right)
        => IsSha256(left) && IsSha256(right)
           && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string? value, Guid expected)
        => Guid.TryParse(value, out var actual) && actual == expected;

    private static void RequireExtractionIdentity(
        NoteTaskSuggestionExtractionDto actual,
        NoteTaskSuggestionSource expected,
        Guid requestId)
    {
        if (!Guid.TryParse(actual.RequestId, out var actualRequestId)
            || actualRequestId != requestId
            || !Matches(actual.NoteId, expected.NoteId)
            || !Matches(actual.ArtifactId, expected.ArtifactId)
            || !Matches(actual.VersionId, expected.VersionId)
            || !SameHash(actual.SourceContentSha256, expected.ContentSha256)
            || !SameHash(actual.SourceInputSha256, expected.InputSha256)
            || !SameHash(actual.SourceManifestSha256, expected.ManifestSha256)
            || !string.Equals(actual.SourceGenerationTaskId, expected.GenerationTaskId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("服务器返回的提取记录与已确认的固定来源不一致。");
        }
    }

    private static void RequireSuggestionIdentity(
        NoteTaskSuggestionDto actual,
        NoteTaskSuggestionDto expected,
        NoteTaskSuggestionStatusDto status)
    {
        if (!Guid.TryParse(expected.Id, out var expectedId)
            || !Guid.TryParse(expected.NoteId, out var expectedNoteId)
            || !Guid.TryParse(expected.ArtifactId, out var expectedArtifactId)
            || !Guid.TryParse(expected.VersionId, out var expectedVersionId)
            || !Matches(actual.Id, expectedId)
            || !Matches(actual.NoteId, expectedNoteId)
            || !Matches(actual.ArtifactId, expectedArtifactId)
            || !Matches(actual.VersionId, expectedVersionId)
            || !SameHash(actual.SourceContentSha256, expected.SourceContentSha256)
            || !SameHash(actual.SourceInputSha256, expected.SourceInputSha256)
            || !SameHash(actual.SourceManifestSha256, expected.SourceManifestSha256)
            || !string.Equals(actual.SourceGenerationTaskId, expected.SourceGenerationTaskId, StringComparison.Ordinal)
            || actual.Status != status)
            throw new InvalidDataException("服务器返回的任务建议身份或状态与本次操作不一致。");
    }

    private static void RequirePendingMatchesSuggestion(
        NoteTaskSuggestionPendingConfirmation pending,
        NoteTaskSuggestionDto suggestion,
        string expectedRevision)
    {
        if (!string.Equals(pending.ExpectedRevision, expectedRevision, StringComparison.Ordinal)
            || !Matches(suggestion.Id, pending.SuggestionId)
            || !Matches(suggestion.NoteId, pending.NoteId)
            || !Matches(suggestion.ArtifactId, pending.ArtifactId)
            || !Matches(suggestion.VersionId, pending.VersionId)
            || !SameHash(suggestion.SourceContentSha256, pending.ContentSha256)
            || !SameHash(suggestion.SourceInputSha256, pending.InputSha256)
            || !SameHash(suggestion.SourceManifestSha256, pending.ManifestSha256)
            || !string.Equals(
                suggestion.SourceGenerationTaskId,
                pending.GenerationTaskId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "已有确认请求绑定到另一笔记来源或修订；请先核实服务器状态，不能生成新的确认请求。");
        }
    }

    private static void RequirePendingSuggestionIdentity(
        NoteTaskSuggestionDto actual,
        NoteTaskSuggestionPendingConfirmation pending)
    {
        if (!Matches(actual.Id, pending.SuggestionId)
            || !Matches(actual.NoteId, pending.NoteId)
            || !Matches(actual.ArtifactId, pending.ArtifactId)
            || !Matches(actual.VersionId, pending.VersionId)
            || !SameHash(actual.SourceContentSha256, pending.ContentSha256)
            || !SameHash(actual.SourceInputSha256, pending.InputSha256)
            || !SameHash(actual.SourceManifestSha256, pending.ManifestSha256)
            || !string.Equals(
                actual.SourceGenerationTaskId,
                pending.GenerationTaskId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "服务器返回的待确认任务建议来源与本机冻结来源不一致；确认凭据仍保留。");
        }
    }

    private static T RequireResponse<T>(int code, string message, T? data)
        where T : class
    {
        if (code != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "服务器操作失败。" : message);
        return data ?? throw new InvalidDataException("服务器响应没有数据。");
    }

    private async Task EnsureNoPendingConfirmationAsync(
        Operation operation,
        Guid suggestionId,
        CancellationToken cancellationToken)
    {
        var pending = await _local.GetPendingConfirmationAsync(
                operation.Account,
                suggestionId,
                cancellationToken)
            .ConfigureAwait(false);
        RequireCurrent(operation);
        if (pending is not null)
        {
            throw new InvalidOperationException(
                "该建议仍有未核实的确认请求；请先刷新服务器状态，确认请求核实前不能编辑或放弃。");
        }
    }

    private sealed record Operation(
        CloudNoteAccountScope Account,
        long AccountGeneration,
        ApiClient.ApiOperationScope Scope) : IDisposable
    {
        public void Dispose() => Scope.Dispose();
    }
}

internal sealed class LocalNoteTaskSuggestionStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LocalNoteTaskSuggestionStore(string root) => _root = Path.GetFullPath(root);

    public async Task<Guid> GetOrCreateRequestAsync(
        CloudNoteAccountScope account,
        Guid noteId,
        Guid artifactId,
        Guid versionId,
        string contentHash,
        NoteDerivedDestinationDto destination,
        CancellationToken cancellationToken)
    {
        var key = Hash(string.Join("\n", account.ServerUrl, account.AccountId, noteId, artifactId,
            versionId, contentHash.ToLowerInvariant(), destination.Endpoint, destination.ModelId,
            destination.ModelRevision));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(AccountDirectory(account), $"extract-{key}.json");
            if (File.Exists(path))
            {
                var stored = await ReadAsync<StoredExtraction>(path, cancellationToken).ConfigureAwait(false);
                if (stored.SchemaVersion != 1
                    || stored.AccountId != account.AccountId
                    || stored.ServerUrl != account.ServerUrl
                    || stored.Fingerprint != key
                    || stored.RequestId == Guid.Empty)
                    throw new InvalidDataException("本机任务提取幂等记录与当前账号或来源不匹配；原记录未覆盖。");
                return stored.RequestId;
            }

            var requestId = Guid.NewGuid();
            await WriteAtomicAsync(path, new StoredExtraction(1, account.ServerUrl, account.AccountId, key, requestId), cancellationToken)
                .ConfigureAwait(false);
            return requestId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Guid> GetOrCreateConfirmationRequestAsync(
        CloudNoteAccountScope account,
        NoteTaskSuggestionDto suggestion,
        string revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (!Guid.TryParse(suggestion.Id, out var suggestionId)
            || !Guid.TryParse(suggestion.NoteId, out var noteId)
            || !Guid.TryParse(suggestion.ArtifactId, out var artifactId)
            || !Guid.TryParse(suggestion.VersionId, out var versionId)
            || !Guid.TryParse(revision, out _))
        {
            throw new InvalidDataException("任务建议身份或来源摘要无效；不会写入确认凭据。");
        }

        if (!IsSha256(suggestion.SourceContentSha256)
            || !IsSha256(suggestion.SourceInputSha256)
            || !IsSha256(suggestion.SourceManifestSha256))
        {
            throw new InvalidDataException("任务建议身份或来源摘要无效；不会写入确认凭据。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(AccountDirectory(account), $"confirm-{suggestionId:D}-{revision}.json");
            if (File.Exists(path))
            {
                var stored = await ReadAsync<StoredConfirmation>(path, cancellationToken).ConfigureAwait(false);
                if (stored.SchemaVersion != 2 || stored.AccountId != account.AccountId
                    || stored.ServerUrl != account.ServerUrl || stored.SuggestionId != suggestionId
                    || stored.Revision != revision || stored.RequestId == Guid.Empty
                    || stored.NoteId != noteId || stored.ArtifactId != artifactId
                    || stored.VersionId != versionId
                    || !string.Equals(
                        stored.ContentSha256,
                        suggestion.SourceContentSha256,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        stored.InputSha256,
                        suggestion.SourceInputSha256,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        stored.ManifestSha256,
                        suggestion.SourceManifestSha256,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        stored.GenerationTaskId,
                        suggestion.SourceGenerationTaskId,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("本机确认幂等记录无效；不会改用新的请求标识。");
                return stored.RequestId;
            }

            var requestId = Guid.NewGuid();
            await WriteAtomicAsync(path,
                new StoredConfirmation(
                    2,
                    account.ServerUrl,
                    account.AccountId,
                    suggestionId,
                    noteId,
                    artifactId,
                    versionId,
                    suggestion.SourceContentSha256,
                    suggestion.SourceInputSha256,
                    suggestion.SourceManifestSha256,
                    suggestion.SourceGenerationTaskId,
                    revision,
                    requestId),
                cancellationToken).ConfigureAwait(false);
            return requestId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NoteTaskSuggestionPendingConfirmation?> GetPendingConfirmationAsync(
        CloudNoteAccountScope account,
        Guid suggestionId,
        CancellationToken cancellationToken)
    {
        if (suggestionId == Guid.Empty)
            throw new ArgumentException("任务建议标识不能为空。", nameof(suggestionId));

        var directory = AccountDirectory(account);
        if (!Directory.Exists(directory))
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Directory.EnumerateFiles(directory, $"confirm-{suggestionId:D}-*.json")
                .ToArray();
            if (paths.Length > 1)
                throw new InvalidDataException("同一任务建议存在多个冻结确认请求；不会自动选择其中一个。");

            foreach (var path in paths)
            {
                var stored = await ReadAsync<StoredConfirmation>(path, cancellationToken).ConfigureAwait(false);
                ValidateStoredConfirmation(stored, account);
                return ToPendingConfirmation(stored);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NoteTaskSuggestionPendingConfirmation>> ListPendingConfirmationsAsync(
        CloudNoteAccountScope account,
        CancellationToken cancellationToken)
    {
        var directory = AccountDirectory(account);
        if (!Directory.Exists(directory))
            return [];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = Directory.EnumerateFiles(directory, "confirm-*.json")
                .Take(257)
                .ToArray();
            if (paths.Length > 256)
                throw new InvalidDataException("本机待确认任务记录超过安全上限；不会自动重试。");

            var result = new List<NoteTaskSuggestionPendingConfirmation>(paths.Length);
            foreach (var path in paths)
            {
                var stored = await ReadAsync<StoredConfirmation>(path, cancellationToken)
                    .ConfigureAwait(false);
                ValidateStoredConfirmation(stored, account);
                result.Add(ToPendingConfirmation(stored));
            }

            if (result.GroupBy(item => item.SuggestionId).Any(group => group.Count() != 1))
                throw new InvalidDataException("同一任务建议存在多个冻结确认请求；不会自动选择其中一个。");

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteConfirmationRequestAsync(
        CloudNoteAccountScope account,
        Guid suggestionId,
        string revision,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        if (suggestionId == Guid.Empty || requestId == Guid.Empty || !Guid.TryParse(revision, out _))
            throw new ArgumentException("确认幂等记录身份无效。", nameof(revision));
        var directory = AccountDirectory(account);
        if (!Directory.Exists(directory)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, $"confirm-{suggestionId:D}-*.json"))
            {
                var stored = await ReadAsync<StoredConfirmation>(path, cancellationToken).ConfigureAwait(false);
                if (stored.SuggestionId == suggestionId
                    && stored.Revision == revision
                    && stored.RequestId == requestId
                    && stored.AccountId == account.AccountId
                    && stored.ServerUrl == account.ServerUrl)
                {
                    File.Delete(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NoteTaskSuggestionEditDraft?> LoadDraftAsync(
        CloudNoteAccountScope account, Guid suggestionId, CancellationToken cancellationToken)
    {
        var path = DraftPath(account, suggestionId);
        if (!File.Exists(path)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await ReadAsync<StoredDraft>(path, cancellationToken).ConfigureAwait(false);
            if (stored.SchemaVersion != 1 || stored.AccountId != account.AccountId
                || stored.ServerUrl != account.ServerUrl || stored.SuggestionId != suggestionId)
                throw new InvalidDataException("本机任务编辑草稿的账号绑定无效；草稿仍保留。");
            return stored.Draft;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveDraftAsync(
        CloudNoteAccountScope account,
        Guid suggestionId,
        NoteTaskSuggestionEditDraft draft,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(DraftPath(account, suggestionId),
                new StoredDraft(1, account.ServerUrl, account.AccountId, suggestionId, draft),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteDraftAsync(
        CloudNoteAccountScope account, Guid suggestionId, CancellationToken cancellationToken)
    {
        var path = DraftPath(account, suggestionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteDraftIfMatchesAsync(
        CloudNoteAccountScope account,
        Guid suggestionId,
        NoteTaskSuggestionEditDraft expectedDraft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedDraft);
        var path = DraftPath(account, suggestionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return;

            var stored = await ReadAsync<StoredDraft>(path, cancellationToken)
                .ConfigureAwait(false);
            if (stored.SchemaVersion != 1
                || stored.AccountId != account.AccountId
                || stored.ServerUrl != account.ServerUrl
                || stored.SuggestionId != suggestionId)
            {
                throw new InvalidDataException("本机任务编辑草稿的账号绑定无效；草稿仍保留。");
            }

            if (stored.Draft == expectedDraft)
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string DraftPath(CloudNoteAccountScope account, Guid suggestionId)
        => Path.Combine(AccountDirectory(account), $"draft-{suggestionId:D}.json");

    private string AccountDirectory(CloudNoteAccountScope account)
    {
        var identity = Hash(account.ServerUrl + "\n" + account.AccountId);
        return Path.Combine(_root, identity);
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, _json, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("本机任务建议状态文件为空。");
    }

    private async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record StoredExtraction(
        int SchemaVersion, string ServerUrl, string AccountId, string Fingerprint, Guid RequestId);

    private static void ValidateStoredConfirmation(
        StoredConfirmation stored,
        CloudNoteAccountScope account)
    {
        if (stored.SchemaVersion != 2
            || stored.AccountId != account.AccountId
            || stored.ServerUrl != account.ServerUrl
            || stored.SuggestionId == Guid.Empty
            || stored.NoteId == Guid.Empty
            || stored.ArtifactId == Guid.Empty
            || stored.VersionId == Guid.Empty
            || stored.RequestId == Guid.Empty
            || !Guid.TryParse(stored.Revision, out _)
            || !IsSha256(stored.ContentSha256)
            || !IsSha256(stored.InputSha256)
            || !IsSha256(stored.ManifestSha256))
        {
            throw new InvalidDataException("本机确认幂等记录无效；不会绕过未核实的确认请求。");
        }
    }

    private static NoteTaskSuggestionPendingConfirmation ToPendingConfirmation(
        StoredConfirmation stored)
        => new(
            stored.SuggestionId,
            stored.Revision,
            stored.RequestId,
            stored.NoteId,
            stored.ArtifactId,
            stored.VersionId,
            stored.ContentSha256,
            stored.InputSha256,
            stored.ManifestSha256,
            stored.GenerationTaskId);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record StoredConfirmation(
        int SchemaVersion,
        string ServerUrl,
        string AccountId,
        Guid SuggestionId,
        Guid NoteId,
        Guid ArtifactId,
        Guid VersionId,
        string ContentSha256,
        string InputSha256,
        string ManifestSha256,
        string? GenerationTaskId,
        string Revision,
        Guid RequestId);

    private sealed record StoredDraft(
        int SchemaVersion, string ServerUrl, string AccountId, Guid SuggestionId, NoteTaskSuggestionEditDraft Draft);
}
