using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NoteTaskSuggestionServiceTests : IDisposable
{
    private const string AccountA = "11111111-1111-1111-1111-111111111111";
    private const string AccountB = "22222222-2222-2222-2222-222222222222";
    private const string SuggestionId = "33333333-3333-3333-3333-333333333333";
    private const string NoteId = "44444444-4444-4444-4444-444444444444";
    private const string ArtifactId = "55555555-5555-5555-5555-555555555555";
    private const string VersionId = "66666666-6666-6666-6666-666666666666";
    private const string Revision = "77777777-7777-7777-7777-777777777777";
    private const string NewRevision = "88888888-8888-8888-8888-888888888888";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pim-task-suggestions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConfirmAfterLostResponseReusesFrozenRequestAndVerifiesServerState()
    {
        var handler = new TaskSuggestionHandler { LoseFirstConfirmResponse = true };
        var service = CreateService(handler, () => new CloudNoteAccountScope(
            "http://127.0.0.1:5858", AccountA));
        var suggestion = Suggestion();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ConfirmAsync(suggestion, Revision));
        var frozen = Assert.Single(handler.ConfirmRequests);
        Assert.Equal(Revision, frozen.Revision);
        Assert.NotEqual(Guid.Empty, frozen.RequestId);
        Assert.NotNull(await service.GetPendingConfirmationAsync(Guid.Parse(SuggestionId)));

        var confirmed = await service.ConfirmAsync(suggestion, Revision);

        Assert.Equal(NoteTaskSuggestionStatusDto.Confirmed, confirmed.Status);
        Assert.Equal(2, handler.ConfirmRequests.Count);
        Assert.All(handler.ConfirmRequests, request =>
        {
            Assert.Equal(frozen.RequestId, request.RequestId);
            Assert.Equal(frozen.Revision, request.Revision);
        });
        Assert.Equal(1, handler.GetSuggestionRequests);
        Assert.Null(await service.GetPendingConfirmationAsync(Guid.Parse(SuggestionId)));
    }

    [Fact]
    public async Task PendingConfirmationReconcilesDraftAndBlocksEditDismissAndNewRevision()
    {
        var handler = new TaskSuggestionHandler
        {
            KeepDraftAfterLostConfirm = true,
            LoseFirstConfirmResponse = true,
        };
        var service = CreateService(handler, () => new CloudNoteAccountScope(
            "http://127.0.0.1:5858", AccountA));
        var suggestion = Suggestion();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ConfirmAsync(suggestion, Revision));
        var result = await service.ReconcilePendingConfirmationsAsync();

        Assert.Equal(new NoteTaskSuggestionReconciliationResult(1, 0, 1), result);
        var localDraft = new NoteTaskSuggestionEditDraft(
            Revision, "保留的编辑稿", "说明", 3, null, null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveEditAsync(suggestion, localDraft));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DismissAsync(suggestion, Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmAsync(Suggestion(revision: NewRevision), NewRevision));

        Assert.Single(handler.ConfirmRequests);
        Assert.Equal(1, handler.GetSuggestionRequests);
        Assert.NotNull(await service.GetPendingConfirmationAsync(Guid.Parse(SuggestionId)));
    }

    [Fact]
    public async Task StaleOpenSessionSendsNoRequestAndLateResponseCannotBeAccepted()
    {
        var handler = new TaskSuggestionHandler();
        var account = new CloudNoteAccountScope("http://127.0.0.1:5858", AccountA);
        long generation = 1;
        var service = CreateService(handler, () => account, () => generation);
        var fence = service.CaptureFence();
        account = account with { AccountId = AccountB };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetDraftSuggestionsAsync(
                Guid.Parse(NoteId), Source(), expectedFence: fence));
        Assert.Equal(0, handler.RequestCount);

        account = fence.Account;
        var currentFence = service.CaptureFence();
        handler.AfterRequest = () => generation++;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetDraftSuggestionsAsync(
                Guid.Parse(NoteId), Source(), expectedFence: currentFence));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SuggestionListRequiresFullFixedSourceIdentityAndCasFailureKeepsLocalDraft()
    {
        var handler = new TaskSuggestionHandler();
        var service = CreateService(handler, () => new CloudNoteAccountScope(
            "http://127.0.0.1:5858", AccountA));
        handler.ListItems =
        [
            Suggestion(),
            Suggestion(sourceContentHash: new string('b', 64)),
            Suggestion(artifactId: "99999999-9999-9999-9999-999999999999"),
        ];

        var drafts = await service.GetDraftSuggestionsAsync(Guid.Parse(NoteId), Source());

        Assert.Single(drafts);
        Assert.Equal(SuggestionId, drafts[0].Id);
        var edit = new NoteTaskSuggestionEditDraft(
            Revision, "本机保留稿", "服务器 CAS 冲突", 4, null, null, null);
        handler.RejectUpdate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveEditAsync(Suggestion(), edit));

        Assert.Equal(edit, await service.LoadDraftAsync(Guid.Parse(SuggestionId)));
        Assert.Equal(1, handler.UpdateRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftWritesCannotCrossPendingFenceWhileConfirmResponseIsBlocked(bool dismiss)
    {
        var handler = new TaskSuggestionHandler
        {
            BlockConfirmResponse = true,
            KeepDraftAfterLostConfirm = true,
            LoseFirstConfirmResponse = true,
        };
        var service = CreateService(handler, () => new CloudNoteAccountScope(
            "http://127.0.0.1:5858", AccountA));
        var suggestion = Suggestion();
        var edit = new NoteTaskSuggestionEditDraft(
            Revision, "串行编辑", "屏障测试", 2, null, null, null);

        var confirm = service.ConfirmAsync(suggestion, Revision);
        await handler.ConfirmStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = dismiss
            ? service.DismissAsync(suggestion, Revision)
            : service.SaveEditAsync(suggestion, edit);
        await Task.Yield();
        Assert.Equal(0, handler.UpdateRequests);
        Assert.Equal(0, handler.DismissRequests);

        handler.ReleaseConfirmResponse.TrySetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => confirm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => write);

        Assert.Equal(0, handler.UpdateRequests);
        Assert.Equal(0, handler.DismissRequests);
        Assert.Single(handler.ConfirmRequests);
        Assert.NotNull(await service.GetPendingConfirmationAsync(Guid.Parse(SuggestionId)));
    }

    private NoteTaskSuggestionService CreateService(
        TaskSuggestionHandler handler,
        Func<CloudNoteAccountScope?> accountProvider,
        Func<long>? generationProvider = null)
    {
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-task-test-token");
        return new NoteTaskSuggestionService(
            api,
            new LocalNoteStore(_root),
            accountProvider,
            generationProvider);
    }

    private static NoteTaskSuggestionSource Source()
        => new(
            Guid.Parse(NoteId),
            Guid.Parse(ArtifactId),
            Guid.Parse(VersionId),
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "generation-1");

    private static NoteTaskSuggestionDto Suggestion(
        string? revision = null,
        string? sourceContentHash = null,
        string? artifactId = null)
        => new()
        {
            ArtifactId = artifactId ?? ArtifactId,
            CreatedAt = DateTimeOffset.Parse("2026-11-02T00:00:00Z"),
            ExtractionId = "99999999-9999-9999-9999-999999999999",
            Id = SuggestionId,
            NoteId = NoteId,
            Ordinal = 1,
            Priority = 3,
            Revision = revision ?? Revision,
            SourceContentSha256 = sourceContentHash ?? new string('a', 64),
            SourceGenerationTaskId = "generation-1",
            SourceInputSha256 = new string('b', 64),
            SourceManifestSha256 = new string('c', 64),
            Status = NoteTaskSuggestionStatusDto.Draft,
            Title = "准备评审材料",
            UpdatedAt = DateTimeOffset.Parse("2026-11-02T00:00:00Z"),
            VersionId = VersionId,
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class TaskSuggestionHandler : HttpMessageHandler
    {
        public NoteTaskSuggestionDto Current { get; private set; } = Suggestion();
        public IReadOnlyList<NoteTaskSuggestionDto> ListItems { get; set; } = [Suggestion()];
        public bool LoseFirstConfirmResponse { get; set; }
        public bool KeepDraftAfterLostConfirm { get; set; }
        public bool RejectUpdate { get; set; }
        public bool BlockConfirmResponse { get; set; }
        public TaskCompletionSource ConfirmStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseConfirmResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        public int GetSuggestionRequests { get; private set; }
        public int UpdateRequests { get; private set; }
        public int DismissRequests { get; private set; }
        public List<(Guid RequestId, string Revision)> ConfirmRequests { get; } = [];
        public Action? AfterRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/confirm", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                var requestId = Guid.Parse(body.RootElement.GetProperty("requestId").GetString()!);
                var revision = body.RootElement.GetProperty("expectedRevision").GetString()!;
                ConfirmRequests.Add((requestId, revision));
                if (BlockConfirmResponse)
                {
                    ConfirmStarted.TrySetResult();
                    await ReleaseConfirmResponse.Task.WaitAsync(cancellationToken);
                }

                if (LoseFirstConfirmResponse)
                {
                    LoseFirstConfirmResponse = false;
                    if (!KeepDraftAfterLostConfirm)
                    {
                        Current = Copy(Current, NoteTaskSuggestionStatusDto.Confirmed,
                            taskId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
                    }

                    throw new HttpRequestException("synthetic response lost after server commit");
                }

                Current = Copy(Current, NoteTaskSuggestionStatusDto.Confirmed,
                    taskId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
                return JsonResponse(new NoteTaskSuggestionDtoApiResponse
                {
                    Code = 0, Data = Current, Message = "ok", Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith(
                    "/task-suggestions/" + SuggestionId, StringComparison.Ordinal))
            {
                GetSuggestionRequests++;
                AfterRequest?.Invoke();
                return JsonResponse(new NoteTaskSuggestionDtoApiResponse
                {
                    Code = 0, Data = Current, Message = "ok", Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/task-suggestions", StringComparison.Ordinal))
            {
                AfterRequest?.Invoke();
                return JsonResponse(new NoteTaskSuggestionListDtoApiResponse
                {
                    Code = 0,
                    Data = new NoteTaskSuggestionListDto { Items = ListItems.ToList(), Total = ListItems.Count },
                    Message = "ok",
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Put && path.EndsWith(
                    "/task-suggestions/" + SuggestionId, StringComparison.Ordinal))
            {
                UpdateRequests++;
                if (RejectUpdate)
                {
                    return JsonResponse(new NoteTaskSuggestionDtoApiResponse
                    {
                        Code = 409, Data = null, Message = "revision conflict", Timestamp = DateTimeOffset.UtcNow,
                    });
                }

                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var update = body.RootElement;
                Current = new NoteTaskSuggestionDto
                {
                    ArtifactId = Current.ArtifactId,
                    CalendarId = update.GetProperty("calendarId").ValueKind == JsonValueKind.Null
                        ? null : update.GetProperty("calendarId").GetString(),
                    CreatedAt = Current.CreatedAt,
                    Description = update.GetProperty("description").ValueKind == JsonValueKind.Null
                        ? null : update.GetProperty("description").GetString(),
                    ExtractionId = Current.ExtractionId,
                    Id = Current.Id,
                    NoteId = Current.NoteId,
                    Ordinal = Current.Ordinal,
                    Priority = update.GetProperty("priority").GetInt32(),
                    Revision = Current.Revision,
                    SourceContentSha256 = Current.SourceContentSha256,
                    SourceGenerationTaskId = Current.SourceGenerationTaskId,
                    SourceInputSha256 = Current.SourceInputSha256,
                    SourceManifestSha256 = Current.SourceManifestSha256,
                    Status = NoteTaskSuggestionStatusDto.Draft,
                    TaskBookId = update.GetProperty("taskBookId").ValueKind == JsonValueKind.Null
                        ? null : update.GetProperty("taskBookId").GetString(),
                    Title = update.GetProperty("title").GetString()!,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    VersionId = Current.VersionId,
                };
                return JsonResponse(new NoteTaskSuggestionDtoApiResponse
                {
                    Code = 0, Data = Current, Message = "ok", Timestamp = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/dismiss", StringComparison.Ordinal))
                DismissRequests++;

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static NoteTaskSuggestionDto Copy(
            NoteTaskSuggestionDto source, NoteTaskSuggestionStatusDto status, string? taskId = null)
            => new()
            {
                ArtifactId = source.ArtifactId,
                CalendarId = source.CalendarId,
                ConfirmedAt = status == NoteTaskSuggestionStatusDto.Confirmed ? DateTimeOffset.UtcNow : null,
                CreatedAt = source.CreatedAt,
                Description = source.Description,
                DismissedAt = source.DismissedAt,
                Due = source.Due,
                ExtractionId = source.ExtractionId,
                Id = source.Id,
                NoteId = source.NoteId,
                Ordinal = source.Ordinal,
                Priority = source.Priority,
                Revision = source.Revision,
                SourceContentSha256 = source.SourceContentSha256,
                SourceGenerationTaskId = source.SourceGenerationTaskId,
                SourceInputSha256 = source.SourceInputSha256,
                SourceManifestSha256 = source.SourceManifestSha256,
                Status = status,
                TaskBookId = source.TaskBookId,
                TaskId = taskId ?? source.TaskId,
                Title = source.Title,
                UpdatedAt = DateTimeOffset.UtcNow,
                VersionId = source.VersionId,
            };

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
