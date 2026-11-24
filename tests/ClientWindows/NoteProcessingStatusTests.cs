using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pim.Client.Core.Contract;
using Pim.Client.Core.Services;
using Pim.Client.Core.Services.Notes;
using Xunit;

namespace Pim.Client.Tests;

public sealed class NoteProcessingStatusTests
{
    private const string AccountId = "11111111-1111-1111-1111-111111111111";
    private static readonly Guid NoteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid VersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task StatusRequestUsesExactNoteVersionAndMapsBothChains()
    {
        var handler = new ProcessingStatusHandler(StatusJson());
        var api = new ApiClient(() => handler);
        api.SetAccessToken("synthetic-processing-token");
        var service = CreateService(api);

        var status = await service.GetProcessingStatusAsync(NoteId, VersionId);
        var view = NoteProcessingStatusView.From(status);

        Assert.Equal("/api/v1/native-notes/processing", handler.Path);
        Assert.Equal($"?noteId={NoteId:D}&versionId={VersionId:D}", handler.Query);
        Assert.Equal("Bearer synthetic-processing-token", handler.Authorization);
        Assert.Equal(NoteProcessingState.PartiallyCompleted, status.State);
        Assert.Equal(NoteProcessingChainState.Succeeded, status.Asr.State);
        Assert.Equal(NoteProcessingChainState.WaitingForAsr, status.Derived.State);
        Assert.Equal("总体：部分完成 · 自动处理开启", view.OverallText);
        Assert.Equal("录音转写：已完成 · 3/3 · 已生成结果", view.AsrText);
        Assert.Equal("笔记整理：等待录音转写", view.DerivedText);
        Assert.Equal("结果：完整记录、独立分析", view.ArtifactsText);
        Assert.Equal("需核对：录音已完成，整理任务仍在等待。", view.IssuesText);
        Assert.Equal("说明：录音转写已完成，笔记整理仍在进行。", view.HintText);
    }

    [Fact]
    public async Task StatusResponseCannotCrossTheSelectedNoteOrVersion()
    {
        var service = CreateService(new ApiClient(() => new ProcessingStatusHandler(StatusJson(
            noteId: Guid.Parse("44444444-4444-4444-4444-444444444444")))));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.GetProcessingStatusAsync(NoteId, VersionId));

        Assert.Contains("固定版本", error.Message);

        var otherVersion = CreateService(new ApiClient(() => new ProcessingStatusHandler(StatusJson(
            versionId: Guid.Parse("55555555-5555-5555-5555-555555555555")))));
        error = await Assert.ThrowsAsync<InvalidDataException>(
            () => otherVersion.GetProcessingStatusAsync(NoteId, VersionId));
        Assert.Contains("固定版本", error.Message);
    }

    [Fact]
    public async Task StatusRequestCannotCompleteAfterTheOwnerAccountChanges()
    {
        CloudNoteAccountScope? current = new("http://127.0.0.1:5858", AccountId);
        var handler = new ProcessingStatusHandler(
            StatusJson(),
            () => current = new CloudNoteAccountScope(
                "http://127.0.0.1:5858",
                "other-owner"));
        var service = new CloudNoteMaterialService(
            new ApiClient(() => handler),
            new LocalNoteStore(Path.Combine(
                Path.GetTempPath(),
                "pim-processing-status-tests-" + Guid.NewGuid().ToString("N"))),
            () => current);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProcessingStatusAsync(NoteId, VersionId));

        Assert.Contains("切换", error.Message);
    }

    [Fact]
    public void EveryContractStateHasAUserVisibleDistinctLabel()
    {
        var overall = Enum.GetValues<NoteProcessingState>()
            .Select(NoteProcessingStatusView.OverallStateLabel)
            .ToArray();
        var chains = Enum.GetValues<NoteProcessingChainState>()
            .Select(NoteProcessingStatusView.ChainStateLabel)
            .ToArray();

        Assert.Equal(overall.Length, overall.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(chains.Length, chains.Distinct(StringComparer.Ordinal).Count());
        Assert.All(overall.Concat(chains), label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.DoesNotContain(overall.Concat(chains), label =>
            Enum.GetNames<NoteProcessingState>().Contains(label, StringComparer.Ordinal)
            || Enum.GetNames<NoteProcessingChainState>().Contains(label, StringComparer.Ordinal));
    }

    [Fact]
    public void LateStatusResponseIsFencedByAccountNoteVersionAndGeneration()
    {
        var account = new CloudNoteAccountScope("https://pim.example.test", AccountId);
        var otherAccount = new CloudNoteAccountScope("https://pim.example.test", "other-owner");
        var request = new NoteProcessingStatusRequest(7, NoteId, VersionId, account, 3);

        Assert.True(request.IsCurrent(7, NoteId, VersionId, account, 3));
        Assert.False(request.IsCurrent(8, NoteId, VersionId, account, 3));
        Assert.False(request.IsCurrent(7, Guid.NewGuid(), VersionId, account, 3));
        Assert.False(request.IsCurrent(7, NoteId, Guid.NewGuid(), account, 3));
        Assert.False(request.IsCurrent(7, NoteId, VersionId, otherAccount, 3));
        Assert.False(request.IsCurrent(7, NoteId, VersionId, account, 4));
    }

    [Fact]
    public async Task ChainErrorsAreShownAsIssuesWithoutLeakingTechnicalCodes()
    {
        var service = CreateService(new ApiClient(
            () => new ProcessingStatusHandler(StatusJson(
                asrError: "转写服务超时 TaskId=77777777-7777-7777-7777-777777777777 "
                    + "ChangeKey=change-key-value GraphEventId=graph-event-value"))));

        var status = await service.GetProcessingStatusAsync(NoteId, VersionId);
        var view = NoteProcessingStatusView.From(status);

        Assert.Contains("转写服务超时", view.IssuesText);
        Assert.DoesNotContain("77777777", view.IssuesText);
        Assert.DoesNotContain("change-key-value", view.IssuesText);
        Assert.DoesNotContain("graph-event-value", view.IssuesText);
        Assert.DoesNotContain("TaskId", view.IssuesText);
        Assert.DoesNotContain("ChangeKey", view.IssuesText);
        Assert.DoesNotContain("GraphEventId", view.IssuesText);
        Assert.DoesNotContain("processing_pending", view.IssuesText);
        Assert.DoesNotContain("NeedsAttention", view.IssuesText);
    }

    [Fact]
    public async Task AsrFailureShowsTheBlockedDerivedChainAndRetryRecovery()
    {
        var service = CreateService(new ApiClient(
            () => new ProcessingStatusHandler(StatusJson(
                state: "NeedsAttention",
                asrState: "NeedsAttention",
                asrFailedItems: 1,
                asrError: "转写服务超时",
                derivedState: "WaitingForAsr",
                issueCode: "asr_failed",
                issueMessage: "录音转写失败，整理任务已阻断。"))));

        var status = await service.GetProcessingStatusAsync(NoteId, VersionId);
        var view = NoteProcessingStatusView.From(status);

        Assert.Equal("总体：需核对 · 自动处理开启", view.OverallText);
        Assert.Contains("录音转写：需核对", view.AsrText);
        Assert.Contains("失败 1", view.AsrText);
        Assert.Equal("笔记整理：等待录音转写", view.DerivedText);
        Assert.Contains("录音转写失败或已停止", view.HintText);
        Assert.Contains("笔记整理已被阻断", view.HintText);
        Assert.Contains("其他客户端重试", view.HintText);
        Assert.Contains("录音转写失败，整理任务已阻断。", view.IssuesText);
        Assert.DoesNotContain("asr_failed", view.IssuesText);
    }

    [Fact]
    public async Task RawStepKeyErrorsAreNotShownToUsers()
    {
        var service = CreateService(new ApiClient(
            () => new ProcessingStatusHandler(StatusJson(
                asrError: "derived.complete_record",
                issueMessage: null))));

        var status = await service.GetProcessingStatusAsync(NoteId, VersionId);
        var view = NoteProcessingStatusView.From(status);

        Assert.DoesNotContain("derived.complete_record", view.IssuesText);
        Assert.Equal("需核对：服务端返回了处理错误，请在处理页查看详情。", view.IssuesText);
    }

    [Fact]
    public async Task LegacyPayloadWithoutOptionalFieldsStillRenders()
    {
        var service = CreateService(new ApiClient(
            () => new ProcessingStatusHandler(StatusJson(
                includeOptionalFields: false,
                derivedState: "PendingStart",
                issueMessage: null))));

        var status = await service.GetProcessingStatusAsync(NoteId, VersionId);
        var view = NoteProcessingStatusView.From(status);

        Assert.Equal("总体：部分完成 · 自动处理开启", view.OverallText);
        Assert.Equal("录音转写：已完成 · 3/3 · 已生成结果", view.AsrText);
        Assert.Equal("笔记整理：待发起", view.DerivedText);
        Assert.Equal(string.Empty, view.IssuesText);
        Assert.Equal("说明：录音转写已完成，笔记整理仍在进行。", view.HintText);
    }

    [Fact]
    public async Task RestoredAndUploadedEntriesUseTheSameServerIdentityWithoutExposingIds()
    {
        var service = CreateService(new ApiClient(
            () => new ProcessingStatusHandler(StatusJson())));
        var status = await service.GetProcessingStatusAsync(NoteId, VersionId);
        var source = new LocalNoteServerSource(
            NoteId,
            VersionId,
            InputJson: "{}",
            ManifestJson: "{}",
            NoteJson: "{}",
            DocumentMarkdown: string.Empty,
            Blocks: [],
            Files: []);
        var restored = NoteProcessingStatusTarget.FromServerSource(source);
        var uploaded = NoteProcessingStatusTarget.ForUploadedVersion(NoteId, NoteId, VersionId);

        Assert.NotNull(restored);
        Assert.NotNull(uploaded);
        Assert.Equal(uploaded.NoteId, restored.NoteId);
        Assert.Equal(uploaded.VersionId, restored.VersionId);
        Assert.Equal("服务器恢复的只读版本", restored.Label);
        Assert.Equal("已上传的服务器固定版本", uploaded.Label);

        var view = NoteProcessingStatusView.From(status);
        Assert.Equal("总体：部分完成 · 自动处理开启", view.OverallText);
        Assert.Equal("录音转写：已完成 · 3/3 · 已生成结果", view.AsrText);
        Assert.DoesNotContain(NoteId.ToString("N")[..8], view.OverallText);
        Assert.DoesNotContain(VersionId.ToString("N")[..8], view.AsrText);
    }

    [Fact]
    public void ServerRestoreUsesImmutableSourceIdentityBeforeAnyCloudSelection()
    {
        var source = new LocalNoteServerSource(
            NoteId,
            VersionId,
            InputJson: "{}",
            ManifestJson: "{}",
            NoteJson: "{}",
            DocumentMarkdown: string.Empty,
            Blocks: [],
            Files: []);

        var target = NoteProcessingStatusTarget.FromServerSource(source);

        var resolved = Assert.IsType<NoteProcessingStatusTarget>(target);
        Assert.Equal(NoteId, resolved.NoteId);
        Assert.Equal(VersionId, resolved.VersionId);
        Assert.True(resolved.IsServerSource);
        Assert.Equal("服务器恢复的只读版本", resolved.Label);
        Assert.DoesNotContain(NoteId.ToString("N")[..8], resolved.Label);
        Assert.DoesNotContain(VersionId.ToString("N")[..8], resolved.Label);
    }

    [Fact]
    public void UploadedVersionTargetRejectsAnotherNotesIdentity()
    {
        Assert.Null(NoteProcessingStatusTarget.ForUploadedVersion(
            NoteId,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            VersionId));
        Assert.Null(NoteProcessingStatusTarget.ForUploadedVersion(
            NoteId,
            NoteId,
            Guid.Empty));

        var target = NoteProcessingStatusTarget.ForUploadedVersion(NoteId, NoteId, VersionId);

        var resolved = Assert.IsType<NoteProcessingStatusTarget>(target);
        Assert.Equal(NoteId, resolved.NoteId);
        Assert.Equal(VersionId, resolved.VersionId);
        Assert.False(resolved.IsServerSource);
    }

    [Fact]
    public void NativeNotesWindowHasANativeStatusRefreshEntryWithoutAWebFallback()
    {
        var xaml = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml"));
        var code = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.App", "NativeNotesWindow.xaml.cs"));
        var targetCode = File.ReadAllText(RepoPath(
            "src", "client-windows", "Pim.Client.Core", "Services", "Notes",
            "NoteProcessingStatusView.cs"));

        Assert.Contains("服务端处理状态", xaml);
        Assert.Contains("ProcessingOverallText", xaml);
        Assert.Contains("ProcessingAsrText", xaml);
        Assert.Contains("ProcessingDerivedText", xaml);
        Assert.Contains("RefreshProcessingStatusButton", xaml);
        Assert.Contains("Click=\"OnRefreshProcessingStatus\"", xaml);
        Assert.Contains("GetProcessingStatusAsync", code);
        Assert.Contains("NoteProcessingStatusRequest", code);
        Assert.Contains("FromServerSource", code);
        Assert.Contains("ForUploadedVersion", code);
        Assert.Contains("SourceNoteId", targetCode);
        Assert.Contains("SourceVersionId", targetCode);
        Assert.Contains("RefreshProcessingStatusAsync", code);
        Assert.DoesNotContain("WebView", code);
        Assert.DoesNotContain("EmbeddedWebViewHost", code);
    }

    private static CloudNoteMaterialService CreateService(ApiClient api)
        => new(
            api,
            new LocalNoteStore(Path.Combine(
                Path.GetTempPath(),
                "pim-processing-status-tests-" + Guid.NewGuid().ToString("N"))),
            () => new CloudNoteAccountScope("http://127.0.0.1:5858", AccountId));

    private static string StatusJson(
        Guid? noteId = null,
        Guid? versionId = null,
        string? asrError = null,
        string state = "PartiallyCompleted",
        string asrState = "Succeeded",
        int asrFailedItems = 0,
        string derivedState = "WaitingForAsr",
        string? pendingReason = null,
        string? issueCode = "processing_pending",
        string? issueMessage = "录音已完成，整理任务仍在等待。",
        bool includeOptionalFields = true)
    {
        var actualNoteId = noteId ?? NoteId;
        var actualVersionId = versionId ?? VersionId;
        var asrErrors = new JsonArray();
        if (asrError is not null)
        {
            asrErrors.Add(asrError);
        }

        var asr = new JsonObject
        {
            ["kind"] = "Asr",
            ["state"] = asrState,
            ["taskCreated"] = true,
            ["totalItems"] = 3,
            ["completedItems"] = 3,
            ["failedItems"] = asrFailedItems,
            ["hasResult"] = true,
            ["errors"] = asrErrors,
        };
        if (includeOptionalFields)
        {
            asr["taskId"] = "77777777-7777-7777-7777-777777777777";
            asr["revision"] = "88888888-8888-8888-8888-888888888888";
            asr["phase"] = asrState;
            asr["waitingReason"] = null;
        }

        var derived = new JsonObject
        {
            ["kind"] = "Derived",
            ["state"] = derivedState,
            ["taskCreated"] = true,
            ["totalItems"] = 0,
            ["completedItems"] = 0,
            ["failedItems"] = 0,
            ["hasResult"] = false,
            ["errors"] = new JsonArray(),
        };
        if (includeOptionalFields)
        {
            derived["taskId"] = "99999999-9999-9999-9999-999999999999";
            derived["revision"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
            derived["phase"] = derivedState;
            derived["waitingReason"] = "等待录音转写";
        }

        var issues = new JsonArray();
        if (issueMessage is not null)
        {
            var issue = new JsonObject
            {
                ["code"] = issueCode ?? "processing_pending",
                ["message"] = issueMessage,
            };
            if (includeOptionalFields)
            {
                issue["task"] = "Derived";
            }

            issues.Add(issue);
        }

        var data = new JsonObject
        {
            ["processingId"] = "66666666-6666-6666-6666-666666666666",
            ["noteId"] = actualNoteId.ToString("D"),
            ["versionId"] = actualVersionId.ToString("D"),
            ["state"] = state,
            ["autoProcessingEnabled"] = true,
            ["autoStarted"] = true,
            ["manualStartAvailable"] = false,
            ["scope"] = "FullVersion",
            ["incrementalScopeSupported"] = false,
            ["asr"] = asr,
            ["derived"] = derived,
            ["artifacts"] = new JsonArray
            {
                new JsonObject
                {
                    ["artifactId"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    ["kind"] = "CompleteRecord",
                    ["headVersionId"] = "cccccccc-cccc-cccc-cccc-cccccccccccc",
                },
                new JsonObject
                {
                    ["artifactId"] = "dddddddd-dddd-dddd-dddd-dddddddddddd",
                    ["kind"] = "IndependentAnalysis",
                    ["headVersionId"] = null,
                },
            },
            ["issues"] = issues,
            ["createdAt"] = "2026-11-23T08:00:00Z",
            ["updatedAt"] = "2026-11-23T08:05:00Z",
        };
        if (includeOptionalFields)
        {
            data["pendingReason"] = pendingReason;
        }

        return new JsonObject
        {
            ["code"] = 0,
            ["data"] = data,
            ["message"] = "ok",
            ["timestamp"] = "2026-11-23T08:05:00Z",
        }.ToJsonString();
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }

    private sealed class ProcessingStatusHandler(string json, Action? onRequest = null) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            Authorization = request.Headers.Authorization?.ToString();
            onRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
