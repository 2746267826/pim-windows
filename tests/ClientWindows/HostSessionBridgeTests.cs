using System.Net;
using System.Net.Http;
using System.Text;
using Pim.Client.Core.Security;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计 F-11 · 宿主会话**真实实现**的行为测试（规格：<c>remediation/host-session/reviewed-spec.md</c>）。
///
/// 覆盖生产接线里可以在 Linux 上真实执行的部分：
///   - <see cref="HostSessionChannel"/>：来源校验、wire 合法性、握手代次、page.logout、刷新单飞；
///   - 出站帧与请求头决策上的**令牌 canary**：令牌只出现在宿主内存、DPAPI 文件与允许的
///     <c>/api/</c> 请求头里，不出现在任何桥消息中；
///   - <see cref="AuthServiceHostSessionBackend"/> 对现有 <c>AuthService.RefreshAsync</c> 的
///     结果分类（no-token / transient / invalid / success）与**真实单飞**（HTTP 层只发一次
///     <c>/auth/refresh</c>）；
///   - <c>AuthService.Logout</c> 真的会通知宿主会话（令牌失效 / 托盘退出 → 当前 WebView）。
///
/// 不可在此执行（保持 NOT-VERIFIED，N-03）：WebView2 的 <c>WebMessageReceived</c>/<c>e.Source</c>、
/// <c>WebResourceRequested</c> 是否罩住页面 <c>fetch</c>/XHR、<c>NavigationStarting</c> 重定向、
/// <c>NewWindowRequested</c>、DPAPI 冷启动 —— 那些在 Windows runner 的
/// <c>HostSessionWindowsHarnessTests</c>（Pim.Client.App.Tests）里跑。
/// </summary>
public sealed class HostSessionBridgeTests
{
    private const string ServerUrl = "https://pim.example.com";
    private const string CanaryAccessToken = "ACCESS-CANARY-9f3a1c";
    private const string CanaryRefreshToken = "REFRESH-CANARY-77b2e4";

    private const string ReadyFrame =
        """{"v":1,"type":"page.ready","requestId":"req-ready-1","clientNonce":"nonce_12chars"}""";

    // ---- ① 握手：page.ready → 一次 host.state，字段全部来自规格 ----

    [Fact]
    public async Task Channel_PageReadyEstablishesOneHandshakeAndRepliesWithHostState()
    {
        var channel = CreateChannel(out var backend);

        var outbound = await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame);

        var message = AssertSingle(outbound);
        Assert.Equal(channel.HandshakeId, message.HandshakeId);

        var parsed = Parse(message.Json);
        Assert.Equal(HostSessionProtocol.Types.HostState, parsed.Type);
        Assert.Equal("req-ready-1", parsed.RequestId);
        Assert.Equal("nonce_12chars", parsed.ClientNonce);
        Assert.Equal(channel.HandshakeId, parsed.HandshakeId);
        Assert.Equal(HostChannelMarker.Product, parsed.Product);
        // apiOrigin 只发文档 origin：默认端口不写。
        Assert.Equal("https://pim.example.com", parsed.ApiOrigin);
        Assert.True(parsed.Authenticated);
        Assert.True(HostSessionProtocol.IsLowercaseUuid(parsed.HandshakeId!));
        Assert.True(channel.IsCurrentGeneration(parsed.HandshakeId!));
        // 握手不触发刷新/退出。
        Assert.Equal(0, backend.RefreshCalls);
        Assert.Equal(0, backend.LogoutCalls);
    }

    [Fact]
    public async Task Channel_PageReadyWithoutHostTokenReportsAuthenticatedFalse()
    {
        var channel = CreateChannel(out _, accessToken: null);

        var outbound = await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame);

        var parsed = Parse(AssertSingle(outbound).Json);
        Assert.False(parsed.Authenticated);
    }

    [Fact]
    public async Task Channel_NonDefaultPortApiOriginKeepsThePort()
    {
        var channel = CreateChannel(out _, serverUrl: "https://pim.example.com:8443");

        var outbound = await channel.HandlePageMessageAsync("https://pim.example.com:8443/today", ReadyFrame);

        Assert.Equal("https://pim.example.com:8443", Parse(AssertSingle(outbound).Json).ApiOrigin);
    }

    // ---- ② 来源：e.Source 的 origin 必须等于当前允许 origin ----

    [Theory]
    [InlineData("https://evil.com/today")]
    [InlineData("https://pim.example.com.evil.com/today")]
    [InlineData("https://pim.example.com@evil.com/today")]
    [InlineData("http://pim.example.com/today")]
    [InlineData("https://pim.example.com:8443/today")]
    [InlineData("https://pim.example.com./today")]
    [InlineData("")]
    [InlineData("not a url")]
    public async Task Channel_UntrustedSourceIsDroppedWithoutReply(string source)
    {
        var channel = CreateChannel(out var backend);

        var outbound = await channel.HandlePageMessageAsync(source, ReadyFrame);

        Assert.Empty(outbound);
        Assert.Equal(0, backend.RefreshCalls);
        Assert.Equal(0, backend.LogoutCalls);
    }

    [Fact]
    public async Task Channel_TrustedSourceWithIdnHostAndDefaultPortMatches()
    {
        var channel = CreateChannel(out _);

        var outbound = await channel.HandlePageMessageAsync("https://PIM.example.com:443/today", ReadyFrame);

        AssertSingle(outbound);
    }

    // ---- ③ wire：非法帧一律丢弃且不回复 ----

    [Theory]
    // 规格外字段（白名单，不是字段名黑名单）
    [InlineData("""{"v":1,"type":"page.ready","requestId":"req-1","clientNonce":"nonce_12chars","token":"ACCESS-CANARY-9f3a1c"}""")]
    [InlineData("""{"v":1,"type":"page.refresh","requestId":"req-1","authorization":"Bearer x"}""")]
    [InlineData("""{"v":1,"type":"page.logout","requestId":"req-1","url":"https://evil.com"}""")]
    // 版本 / 类型
    [InlineData("""{"v":"1","type":"page.ready","requestId":"req-1","clientNonce":"nonce_12chars"}""")]
    [InlineData("""{"v":2,"type":"page.ready","requestId":"req-1","clientNonce":"nonce_12chars"}""")]
    [InlineData("""{"v":1,"type":"host.state","requestId":"req-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://pim.example.com","authenticated":true}""")]
    [InlineData("""{"v":1,"type":"page.ack","requestId":"req-1"}""")]
    // 必填 / 标识符格式
    [InlineData("""{"v":1,"type":"page.ready","requestId":"req-1"}""")]
    [InlineData("""{"v":1,"type":"page.ready","requestId":"req-1","clientNonce":"short"}""")]
    // 非对象 / 非 JSON / 空
    [InlineData("""["page.ready"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Channel_IllegalFramesAreDroppedWithoutReply(string frame)
    {
        var channel = CreateChannel(out var backend);

        var outbound = await channel.HandlePageMessageAsync(Origin("/today"), frame);

        Assert.Empty(outbound);
        Assert.Equal(0, backend.RefreshCalls);
        Assert.Equal(0, backend.LogoutCalls);
    }

    [Fact]
    public async Task Channel_OversizedFrameIsDropped()
    {
        var channel = CreateChannel(out _);
        var filler = new string('x', HostSessionProtocol.MaxFrameBytes);
        var frame = "{\"v\":1,\"type\":\"page.logout\",\"requestId\":\"req-1\",\"reason\":\"" + filler + "\"}";

        var outbound = await channel.HandlePageMessageAsync(Origin("/today"), frame);

        Assert.Empty(outbound);
    }

    [Fact]
    public async Task Channel_NullPageMessageIsDropped()
    {
        var channel = CreateChannel(out _);

        Assert.Empty(await channel.HandlePageMessageAsync(Origin("/today"), null));
        Assert.False(channel.IsTrustedSource(null));
    }

    // ---- ④ 令牌 canary：桥的任何出站帧都不含令牌 ----

    [Fact]
    public async Task Channel_NoOutboundFrameCarriesTheAccessToken()
    {
        var channel = CreateChannel(out var backend, accessToken: CanaryAccessToken);
        backend.NextOutcome = HostRefreshOutcome.Invalidated;

        var frames = new List<string>();
        foreach (var frame in new[]
        {
            ReadyFrame,
            """{"v":1,"type":"page.refresh","requestId":"req-ref-1"}""",
            """{"v":1,"type":"page.logout","requestId":"req-logout-1"}""",
        })
        {
            foreach (var outbound in await channel.HandlePageMessageAsync(Origin("/today"), frame))
            {
                frames.Add(outbound.Json);
            }
        }

        frames.Add(channel.BuildHostInitiatedSessionEnded(HostSessionEndReason.Expired).Json);

        Assert.NotEmpty(frames);
        foreach (var json in frames)
        {
            Assert.DoesNotContain(CanaryAccessToken, json, StringComparison.Ordinal);
            Assert.DoesNotContain(CanaryRefreshToken, json, StringComparison.Ordinal);
            Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", json, StringComparison.Ordinal);
        }

        // 令牌只出现在**允许的**出口：宿主内存与允许的 /api/ 请求头。
        var header = WebViewRequestHeaderPolicy.Decide(
            "https://pim.example.com/api/v1/pc/browser-tt/upload", ServerUrl, CanaryAccessToken);
        Assert.Equal(WebRequestHeaderAction.Set, header.Action);
        Assert.Equal("Bearer " + CanaryAccessToken, header.HeaderValue);
    }

    // ---- ⑤ 刷新：并发共用一次委托，每个 requestId 各得一份回复 ----

    [Fact]
    public async Task Channel_ConcurrentPageRefreshDelegatesOnceAndAnswersEachRequestId()
    {
        var channel = CreateChannel(out var backend);
        await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame);

        var results = await Task.WhenAll(Enumerable.Range(1, 5).Select(i =>
            channel.HandlePageMessageAsync(Origin("/today"), $$"""{"v":1,"type":"page.refresh","requestId":"req-{{i}}"}""")));

        Assert.Equal(1, backend.RefreshCalls);
        Assert.All(results, frames =>
        {
            var parsed = Parse(AssertSingle(frames).Json);
            Assert.Equal(HostSessionProtocol.Types.HostRefreshResult, parsed.Type);
            Assert.True(parsed.Ok);
        });
        Assert.Equal(
            Enumerable.Range(1, 5).Select(i => $"req-{i}").OrderBy(x => x, StringComparer.Ordinal),
            results.Select(frames => Parse(frames[0].Json).RequestId!).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Channel_RefreshWithoutHostTokenReportsUnauthenticatedAndDoesNotEndTheSession()
    {
        var channel = CreateChannel(out var backend, accessToken: null);

        var frames = await channel.HandlePageMessageAsync(Origin("/today"), """{"v":1,"type":"page.refresh","requestId":"req-ref-1"}""");

        var parsed = Parse(AssertSingle(frames).Json);
        Assert.Equal(HostSessionProtocol.RefreshReasons.Unauthenticated, parsed.Reason);
        Assert.False(parsed.Ok);
        Assert.Equal(0, backend.RefreshCalls);
    }

    [Fact]
    public async Task Channel_RefreshTransientFailureKeepsTheSessionAlive()
    {
        var channel = CreateChannel(out var backend);
        backend.NextOutcome = HostRefreshOutcome.Transient;

        var frames = await channel.HandlePageMessageAsync(Origin("/today"), """{"v":1,"type":"page.refresh","requestId":"req-ref-1"}""");

        var parsed = Parse(AssertSingle(frames).Json);
        Assert.False(parsed.Ok);
        Assert.Equal(HostSessionProtocol.RefreshReasons.Transient, parsed.Reason);
    }

    [Fact]
    public async Task Channel_RefreshInvalidationSendsExpiredSessionEndedAsASecondFrame()
    {
        var channel = CreateChannel(out var backend);
        var ready = AssertSingle(await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame));
        backend.NextOutcome = HostRefreshOutcome.Invalidated;

        var frames = await channel.HandlePageMessageAsync(Origin("/today"), """{"v":1,"type":"page.refresh","requestId":"req-ref-1"}""");

        Assert.Equal(2, frames.Count);
        var result = Parse(frames[0].Json);
        Assert.Equal(HostSessionProtocol.RefreshReasons.Invalid, result.Reason);
        var ended = Parse(frames[1].Json);
        Assert.Equal(HostSessionProtocol.Types.HostSessionEnded, ended.Type);
        Assert.Equal(HostSessionProtocol.EndedReasons.Expired, ended.Reason);
        Assert.Equal(ready.HandshakeId, ended.HandshakeId);
        Assert.Equal(ready.HandshakeId, frames[1].HandshakeId);
    }

    // ---- ⑥ 导航代次：旧代次的迟到结果不再作用于新文档 ----

    [Fact]
    public async Task Channel_LateRefreshResultAfterNavigationIsStaleAndNotDeliverable()
    {
        var channel = CreateChannel(out var backend);
        var ready = AssertSingle(await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame));
        backend.NextOutcome = HostRefreshOutcome.Succeeded;

        var refreshTask = channel.HandlePageMessageAsync(Origin("/today"), """{"v":1,"type":"page.refresh","requestId":"req-ref-1"}""");
        // 顶层文档再次导航：旧代次作废（新文档自己会再发一条 page.ready）。
        channel.OnTopLevelDocumentNavigation();
        var frames = await refreshTask;

        Assert.False(channel.IsCurrentGeneration(ready.HandshakeId));
        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.False(channel.IsCurrentGeneration(frame.HandshakeId)));
    }

    [Fact]
    public async Task Channel_NewDocumentHandshakeIsIndependentFromTheOldOne()
    {
        var channel = CreateChannel(out _);
        var first = AssertSingle(await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame));

        channel.OnTopLevelDocumentNavigation();
        var second = AssertSingle(await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame));

        Assert.NotEqual(first.HandshakeId, second.HandshakeId);
        Assert.False(channel.IsCurrentGeneration(first.HandshakeId));
        Assert.True(channel.IsCurrentGeneration(second.HandshakeId));
    }

    // ---- ⑦ logout：只有 page.logout 触发，且 ended 带上该 requestId ----

    [Fact]
    public async Task Channel_PageLogoutDelegatesOnceAndEndsTheSessionWithTheRequestId()
    {
        var channel = CreateChannel(out var backend);
        var ready = AssertSingle(await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame));

        var frames = await channel.HandlePageMessageAsync(Origin("/today"), """{"v":1,"type":"page.logout","requestId":"req-logout-1"}""");

        Assert.Equal(1, backend.LogoutCalls);
        var ended = Parse(AssertSingle(frames).Json);
        Assert.Equal(HostSessionProtocol.Types.HostSessionEnded, ended.Type);
        Assert.Equal(HostSessionProtocol.EndedReasons.Logout, ended.Reason);
        Assert.Equal("req-logout-1", ended.RequestId);
        Assert.Equal(ready.HandshakeId, ended.HandshakeId);
    }

    [Theory]
    [InlineData("""{"v":1,"type":"page.refresh","requestId":"req-1"}""")]
    [InlineData("""{"v":1,"type":"page.ready","requestId":"req-1","clientNonce":"nonce_12chars"}""")]
    public async Task Channel_OtherPageMessagesNeverTriggerLogout(string frame)
    {
        var channel = CreateChannel(out var backend);

        await channel.HandlePageMessageAsync(Origin("/today"), frame);

        Assert.Equal(0, backend.LogoutCalls);
    }

    [Fact]
    public async Task Channel_HostInitiatedSessionEndedUsesTheSpecReasons()
    {
        var channel = CreateChannel(out _);
        var ready = AssertSingle(await channel.HandlePageMessageAsync(Origin("/today"), ReadyFrame));

        foreach (var (reason, expected) in new[]
        {
            (HostSessionEndReason.Expired, HostSessionProtocol.EndedReasons.Expired),
            (HostSessionEndReason.Logout, HostSessionProtocol.EndedReasons.Logout),
        })
        {
            Assert.Contains(expected, HostSessionProtocol.SessionEndedReasons);
            var message = channel.BuildHostInitiatedSessionEnded(reason);
            var parsed = Parse(message.Json);
            Assert.Equal(HostSessionProtocol.Types.HostSessionEnded, parsed.Type);
            Assert.Equal(expected, parsed.Reason);
            Assert.Equal(ready.HandshakeId, parsed.HandshakeId);
            Assert.Null(parsed.RequestId);
        }
    }

    // ---- ⑧ AuthService 适配：结果分类与真实单飞 ----

    [Fact]
    public async Task Backend_MapsRefreshFailureToTransientWhenTokensRemain()
    {
        var auth = await CreateLoggedInAuthServiceAsync(HttpStatusCode.InternalServerError);
        var backend = new AuthServiceHostSessionBackend(auth);

        var outcome = await backend.RefreshAsync(CancellationToken.None);

        Assert.Equal(HostRefreshOutcome.Transient, outcome);
        Assert.True(backend.HasAccessToken);
    }

    [Fact]
    public async Task Backend_MapsServerRejectedRefreshToInvalidatedAndExistingLogoutStillRuns()
    {
        var auth = await CreateLoggedInAuthServiceAsync(HttpStatusCode.Unauthorized);
        var reasons = new List<HostSessionEndReason>();
        auth.LoggedOut += reasons.Add;
        var backend = new AuthServiceHostSessionBackend(auth);

        var outcome = await backend.RefreshAsync(CancellationToken.None);

        Assert.Equal(HostRefreshOutcome.Invalidated, outcome);
        Assert.False(backend.HasAccessToken);
        // 现有 AuthService 的 401 分支仍然 Logout（行为保留），并通知宿主会话（reason=expired）。
        Assert.Equal(new[] { HostSessionEndReason.Expired }, reasons);
    }

    [Fact]
    public async Task Backend_MapsSuccessfulRefreshAndLeavesNoTokenCaseUndelegated()
    {
        var auth = await CreateLoggedInAuthServiceAsync(HttpStatusCode.OK);
        var backend = new AuthServiceHostSessionBackend(auth);

        Assert.Equal(HostRefreshOutcome.Succeeded, await backend.RefreshAsync(CancellationToken.None));

        var loggedOut = new AuthService(new ApiClient(), tokenPath: Path.Combine(Path.GetTempPath(), $"pim-f11-{Guid.NewGuid():N}.json"));
        var backendWithoutToken = new AuthServiceHostSessionBackend(loggedOut);
        Assert.False(backendWithoutToken.HasAccessToken);
        Assert.Equal(HostRefreshOutcome.NoToken, await backendWithoutToken.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Backend_PageLogoutUsesTheExistingLogoutOnceWithLogoutReason()
    {
        var auth = await CreateLoggedInAuthServiceAsync(HttpStatusCode.OK);
        var reasons = new List<HostSessionEndReason>();
        auth.LoggedOut += reasons.Add;
        var backend = new AuthServiceHostSessionBackend(auth);

        await backend.LogoutAsync(CancellationToken.None);

        Assert.False(backend.HasAccessToken);
        Assert.Equal(new[] { HostSessionEndReason.Logout }, reasons);
    }

    [Fact]
    public async Task Backend_ConcurrentRefreshesShareOneRealHttpRefreshCall()
    {
        var handler = new StubHandler(RefreshOk, refreshDelay: TimeSpan.FromMilliseconds(50));
        var auth = await CreateLoggedInAuthServiceAsync(handler);
        var backend = new AuthServiceHostSessionBackend(auth);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => backend.RefreshAsync(CancellationToken.None)));

        Assert.Equal(1, handler.RefreshCalls);
        Assert.All(outcomes, outcome => Assert.Equal(HostRefreshOutcome.Succeeded, outcome));
    }

    [Fact]
    public async Task AuthService_LogoutNotifiesHostSessionWithTheGivenReason()
    {
        var auth = await CreateLoggedInAuthServiceAsync(HttpStatusCode.OK);
        var reasons = new List<HostSessionEndReason>();
        auth.LoggedOut += reasons.Add;

        auth.Logout();
        auth.Logout(HostSessionEndReason.Logout);

        Assert.Equal(new[] { HostSessionEndReason.Expired, HostSessionEndReason.Logout }, reasons);
    }

    // ---- helpers ----

    private static string Origin(string path) => ServerUrl + path;

    private static HostSessionChannel CreateChannel(
        out FakeBackend backend,
        string serverUrl = ServerUrl,
        string? accessToken = CanaryAccessToken)
    {
        Assert.True(WebViewOriginPolicy.TryCreate(serverUrl, out var policy));
        backend = new FakeBackend(accessToken);
        return new HostSessionChannel(policy, new HostSessionCoordinator(), backend);
    }

    private static HostSessionOutbound AssertSingle(IReadOnlyList<HostSessionOutbound> outbound)
    {
        Assert.Single(outbound);
        return outbound[0];
    }

    private static HostSessionProtocol.Message Parse(string json)
    {
        Assert.True(HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(json), out var message), $"宿主出站帧不是合法 wire：{json}");
        Assert.NotNull(message);
        Assert.Empty(message!.ExtraFields);
        return message;
    }

    private static HttpResponseMessage RefreshOk(HttpRequestMessage request)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(LoginJson("new-access", "new-refresh"), Encoding.UTF8, "application/json"),
        };

    private static string LoginJson(string accessToken, string refreshToken)
        => "{\"code\":0,\"message\":\"ok\",\"data\":{\"accessToken\":\"" + accessToken
            + "\",\"refreshToken\":\"" + refreshToken
            + "\",\"expiresAt\":\"2099-01-01T00:00:00+00:00\","
            + "\"user\":{\"id\":\"u-1\",\"username\":\"tester\",\"displayName\":\"Tester\",\"role\":\"user\"}}}";

    private static async Task<AuthService> CreateLoggedInAuthServiceAsync(HttpStatusCode refreshStatus)
        => await CreateLoggedInAuthServiceAsync(refreshStatus == HttpStatusCode.OK
            ? new StubHandler(RefreshOk)
            : new StubHandler(_ => new HttpResponseMessage(refreshStatus)));

    private static async Task<AuthService> CreateLoggedInAuthServiceAsync(StubHandler handler)
    {
        var apiClient = new ApiClient(() => handler);
        var tokenPath = Path.Combine(Path.GetTempPath(), $"pim-f11-auth-{Guid.NewGuid():N}.json");
        var auth = new AuthService(apiClient, tokenPath);
        auth.ServerUrl = "http://127.0.0.1:5859";

        var loggedIn = await auth.LoginAsync("tester", "secret");
        Assert.True(loggedIn, "测试夹具登录失败");
        return auth;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private readonly TimeSpan _refreshDelay;
        private int _refreshCalls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan? refreshDelay = null)
        {
            _respond = respond;
            _refreshDelay = refreshDelay ?? TimeSpan.Zero;
        }

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/auth/login", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(LoginJson(CanaryAccessToken, CanaryRefreshToken), Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/auth/refresh", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refreshCalls);
                if (_refreshDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_refreshDelay, cancellationToken);
                }
            }

            return _respond(request);
        }
    }

    private sealed class FakeBackend : IHostSessionBackend
    {
        private int _refreshCalls;
        private int _logoutCalls;

        public FakeBackend(string? accessToken) => AccessToken = accessToken;

        public string? AccessToken { get; set; }

        public HostRefreshOutcome NextOutcome { get; set; } = HostRefreshOutcome.Succeeded;

        public bool HasAccessToken => !string.IsNullOrEmpty(AccessToken);

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public int LogoutCalls => Volatile.Read(ref _logoutCalls);

        public async Task<HostRefreshOutcome> RefreshAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _refreshCalls);
            await Task.Delay(25, ct);
            if (NextOutcome == HostRefreshOutcome.Invalidated) AccessToken = null;
            return NextOutcome;
        }

        public Task LogoutAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _logoutCalls);
            AccessToken = null;
            return Task.CompletedTask;
        }
    }
}
