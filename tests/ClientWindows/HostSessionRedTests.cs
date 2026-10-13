using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pim.Client.Core.Security;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计 F-11 · 宿主会话的**有效 RED**（规格：<c>remediation/host-session/reviewed-spec.md</c>）。
///
/// 这里只放会真实失败的断言：期望值来自规格，实际值来自今天的行为或**保持今天行为的接缝**。
/// 刻意不包含：编译错误、只有源码字符串的检查、以及“新类型已经返回安全结果所以是绿的”用例。
///
/// 生产接缝（不是只给测试用的类）：
///   - <see cref="EmbeddedWebViewScripts.BuildHostChannelMarkerScript"/> ←
///     EmbeddedWebViewHost.InitializeAsync 的 <c>AddScriptToExecuteOnDocumentCreatedAsync</c>
///   - <see cref="WebViewRequestHeaderPolicy.Decide"/> ← EmbeddedWebViewHost.OnWebResourceRequested
///   - <see cref="WebViewNavigationPolicy.DecideNewWindow"/> / <see cref="WebViewNavigationPolicy.DecideTopLevelNavigation"/>
///     ← EmbeddedWebViewHost.OnNewWindowRequested / OnNavigationStarting
///   - <see cref="HostSessionChannel"/> / <see cref="HostSessionCoordinator"/> ← EmbeddedWebViewHost 的消息桥
///
/// 令牌 canary：打在**真实生产出口**上（生产脚本出口 + 桥的出站帧），以及守卫
/// <c>webview-token-policy</c> 的真实退出码（不再有临时 <c>--exclude</c>）。
///
/// 平台边界：WebView2 的真实触发面（请求头是否罩住 fetch/XHR、导航重定向、postMessage、
/// e.Source、DPAPI）保持 NOT-VERIFIED —— Linux 上的纯函数不能把它们写成 PASS；
/// Windows runner 上的真实夹具在 <c>tests/ClientWindows/HostSessionWindowsHarnessTests.cs</c>。
/// </summary>
public sealed class HostSessionRedTests
{
    private const string ServerUrl = "https://pim.example.com";
    private const string CanaryAccessToken = "ACCESS-CANARY-9f3a1c";
    private const string CanaryRefreshToken = "REFRESH-CANARY-77b2e4";

    // ---- ① 令牌 canary：打在**真实生产出口**上（EmbeddedWebViewHost 安装的同一个脚本）----

    [Fact]
    public void HostChannelMarkerScript_IsAConstantWithoutAnyCredential()
    {
        var script = EmbeddedWebViewScripts.BuildHostChannelMarkerScript();

        Assert.Equal(HostChannelMarker.Script, script);
        Assert.Contains(HostChannelMarker.GlobalName, script, StringComparison.Ordinal);
        Assert.Contains(HostChannelMarker.Product, script, StringComparison.Ordinal);
        foreach (var canary in new[] { CanaryAccessToken, CanaryRefreshToken, "Authorization", "Bearer" })
        {
            Assert.DoesNotContain(canary, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionCanary_NoProductionExitHandsTokensToThePage()
    {
        // 生产出口：EmbeddedWebViewHost 安装的**唯一**文档创建脚本就是这条常量标记。
        // 旧的令牌注入 helper 已删除：令牌不再进入 ExecuteScriptAsync 参数或文档创建脚本（规格 §8）。
        // 只看代码行：注释里说明「删除过什么」不算执行路径（守卫 webview-token-policy 同口径）。
        var scriptExports = CodeLines(ReadRepoFile("src", "client-windows", "Pim.Client.Core", "Security", "EmbeddedWebViewScripts.cs"));
        var hostSource = CodeLines(ReadRepoFile("src", "client-windows", "Pim.Client.App", "EmbeddedWebViewHost.cs"));

        foreach (var forbidden in new[] { "__pimToken", "sessionStorage", "localStorage", "BuildLegacyTokenInjectionScript", "InjectAuthTokenAsync" })
        {
            Assert.False(
                scriptExports.Contains(forbidden, StringComparison.Ordinal),
                $"脚本出口的代码里仍然存在把令牌交给页面的形态：[{forbidden}]");
            Assert.False(
                hostSource.Contains(forbidden, StringComparison.Ordinal),
                $"嵌入宿主的代码里仍然存在把令牌交给页面的形态：[{forbidden}]");
        }

        // 宿主真的安装了常量标记脚本（而不是只在 Core 里有一个没人调用的常量）。
        Assert.Contains(
            "AddScriptToExecuteOnDocumentCreatedAsync(HostChannelMarker.Script)",
            hostSource,
            StringComparison.Ordinal);
    }

    /// <summary>去掉注释行（`//` / `///` / `/*` / `*`）后的源码文本。</summary>
    private static string CodeLines(string source)
        => string.Join('\n', source
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("/*", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("*", StringComparison.Ordinal)));

    private static string ReadRepoFile(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }

    [Fact]
    public void Protocol_RejectsExtraFieldsIncludingCredentialShapedOnes()
    {
        // 规格 §2：每种类型只允许列出的字段；多一个字段就丢弃并且不回复。
        // 今天的解析接缝仍接受额外字段（“未知字段仍被接受”），所以这些都是红的。
        foreach (var json in new[]
        {
            """{"v":1,"type":"page.ready","requestId":"req-ready-1","clientNonce":"nonce_12chars","token":"ACCESS-CANARY-9f3a1c"}""",
            """{"v":1,"type":"page.refresh","requestId":"req-ref-1","authorization":"Bearer x"}""",
            """{"v":1,"type":"page.logout","requestId":"req-logout-1","url":"https://evil.com"}""",
            """{"v":1,"type":"host.state","requestId":"req-ready-1","clientNonce":"nonce_12chars","handshakeId":"6f1e8c0a-1b2c-4d3e-8f90-123456789abc","product":"pim-host-windows","apiOrigin":"https://pim.example.com","authenticated":true,"refreshToken":"REFRESH-CANARY-77b2e4"}""",
        })
        {
            Assert.False(
                HostSessionProtocol.TryParse(Encoding.UTF8.GetBytes(json), out _),
                $"规格外的额外字段被接受：{json}");
        }
    }

    // ---- ② 请求头决策：允许 origin + /api/ + canary 时期望 Set（今天恒 Leave）----

    [Fact]
    public void RequestHeaderPolicy_SetsHostBearerOnAllowedApiRequests()
    {
        var decision = WebViewRequestHeaderPolicy.Decide(
            "https://pim.example.com/api/v1/pc/browser-tt/upload",
            ServerUrl,
            CanaryAccessToken);

        Assert.Equal(WebRequestHeaderAction.Set, decision.Action);
        Assert.Equal("Bearer " + CanaryAccessToken, decision.HeaderValue);
    }

    [Fact]
    public void RequestHeaderPolicy_LeavesForeignAndNonApiRequestsAlone()
    {
        // 规格 §4：第三方 / 非 API / 无令牌都不 Set、也不 Remove，原有 Authorization 保持不动。
        foreach (var url in new[]
        {
            "https://evil.com/api/v1/x",
            "https://pim.example.com/today",
            "https://pim.example.com/apiary/x",
            "https://pim.example.com/API/v1/x",
            "https://pim.example.com/api/../secret",
            "javascript:alert(1)",
        })
        {
            var decision = WebViewRequestHeaderPolicy.Decide(url, ServerUrl, CanaryAccessToken);
            Assert.Equal(WebRequestHeaderAction.Leave, decision.Action);
            Assert.Null(decision.HeaderValue);
        }
    }

    [Fact]
    public void RequestHeaderPolicy_LeavesRequestsAloneWhenHostHasNoToken()
    {
        var decision = WebViewRequestHeaderPolicy.Decide(
            "https://pim.example.com/api/v1/daemon/heartbeat",
            ServerUrl,
            null);

        Assert.Equal(WebRequestHeaderAction.Leave, decision.Action);
    }

    [Theory]
    // 规格 §3/§4：这些 URI 都不是「允许 origin + /api/ 前缀」的请求，必须 Leave。
    // 今天恒 Leave，所以它们是绿锁 —— 用来固定“实现不得给它们加宿主 Bearer”。
    [InlineData("https://user:pw@pim.example.com/api/v1/x")]      // userinfo
    [InlineData("https://pim.example.com:8443/api/v1/x")]         // 端口不同（:8443）
    [InlineData("https://pim.example.com/api/%2e%2e/secret")]     // 规范化后是 /secret，不带令牌
    [InlineData("https://pim.example.com./api/v1/x")]             // 尾部点主机
    public void RequestHeaderPolicy_LeavesUnnormalizedOrForeignOriginsAlone(string url)
    {
        var decision = WebViewRequestHeaderPolicy.Decide(url, ServerUrl, CanaryAccessToken);

        Assert.Equal(WebRequestHeaderAction.Leave, decision.Action);
        Assert.Null(decision.HeaderValue);
    }

    // ---- ③ 导航/新窗口：外部 http(s) 交给系统浏览器、危险 scheme 取消 ----

    [Theory]
    // 规格 §5 第 2 行：其他 http(s) → Handled，交给系统浏览器（不是 Block，
    // 也不是留在带凭据的 WebView 里）。Grok round3 I-1：这里必须精确期望
    // OpenInSystemBrowser，用 `≠ NavigateInPlace` 会把「一律 Block」的错实现放绿。
    [InlineData("https://other.example.org/today")]
    [InlineData("http://pim.example.com/today")]              // 不同 scheme → 不是允许 origin
    [InlineData("https://pim.example.com.evil.com/today")]    // 后缀伪域
    [InlineData("https://evil.com#pim.example.com")]          // 片段里带允许主机不算同源
    [InlineData("https://pim.example.com:8443/today")]        // 端口不同
    [InlineData("https://pim.example.com./today")]            // 尾部点主机
    public void NewWindowPolicy_OpensExternalHttpTargetsInTheSystemBrowser(string uri)
    {
        Assert.Equal(
            WebNavigationAction.OpenInSystemBrowser,
            WebViewNavigationPolicy.DecideNewWindow(uri, ServerUrl));
    }

    [Theory]
    // 规格 §5 第 3 行：危险 scheme、userinfo、解析失败 → 既不 Navigate 也不启动进程。
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("blob:https://pim.example.com/abc")]          // 路径文本里含允许 URL 也必须拒绝
    [InlineData("about:blank")]
    [InlineData("ms-appx:///assets/index.html")]
    [InlineData("https://pim.example.com@evil.com/today")]    // userinfo
    [InlineData("not a url")]                                 // 解析失败
    public void NewWindowPolicy_BlocksDangerousSchemesInsteadOfLaunchingThem(string uri)
    {
        // 规格 §5：危险 scheme / userinfo / 解析失败 → Handled=true，既不 Navigate 也不启动进程。
        Assert.Equal(WebNavigationAction.Block, WebViewNavigationPolicy.DecideNewWindow(uri, ServerUrl));
    }

    [Fact]
    public void NewWindowPolicy_KeepsTrustedSameOriginNewWindowsInTheEmbeddedWebView()
    {
        // 规格 §5 第 1 行：可信同源新窗口今天的行为必须保留（留在同一个 WebView）。
        Assert.Equal(
            WebNavigationAction.NavigateInPlace,
            WebViewNavigationPolicy.DecideNewWindow("https://pim.example.com/today", ServerUrl));
    }

    [Theory]
    // 规格 §5 第 4 行：顶层 NavigationStarting 指向不允许 origin 时 Cancel=true；
    // 若该 URI 是外部 http(s)，交给系统浏览器（精确期望，不是 Block）。
    [InlineData("https://evil.com/today")]
    [InlineData("https://pim.example.com.evil.com/today")]
    [InlineData("http://pim.example.com/today")]
    [InlineData("https://pim.example.com:8443/today")]
    [InlineData("https://pim.example.com./today")]
    public void TopLevelNavigationPolicy_SendsExternalHttpTargetsToTheSystemBrowser(string uri)
    {
        Assert.Equal(
            WebNavigationAction.OpenInSystemBrowser,
            WebViewNavigationPolicy.DecideTopLevelNavigation(uri, ServerUrl));
    }

    [Theory]
    // 规格 §5 第 4 行后半：危险 scheme / userinfo / 解析失败只取消，不启动进程。
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("blob:https://pim.example.com/abc")]
    [InlineData("about:blank")]
    [InlineData("ms-appx:///assets/index.html")]
    [InlineData("https://pim.example.com@evil.com/today")]
    [InlineData("not a url")]
    public void TopLevelNavigationPolicy_BlocksDangerousSchemes(string uri)
    {
        Assert.Equal(WebNavigationAction.Block, WebViewNavigationPolicy.DecideTopLevelNavigation(uri, ServerUrl));
    }

    [Fact]
    public void TopLevelNavigationPolicy_KeepsSameOriginNavigationInPlace()
    {
        // 规格 §5：允许 origin 的顶层导航不是「取消」目标 —— 实现不得把同源也 Block 掉。
        Assert.Equal(
            WebNavigationAction.NavigateInPlace,
            WebViewNavigationPolicy.DecideTopLevelNavigation("https://pim.example.com/today", ServerUrl));
    }

    // ---- ④ 通道标记：超时不回退 browser（对应“超时后不得写 storage”的规格要求）----

    [Fact]
    public void ChannelMarker_AbsentMarkerMeansBrowserChannel()
    {
        Assert.Equal(HostChannelMode.Browser, HostChannelMarker.Resolve(null, null));
        Assert.Equal(HostChannelMode.Browser, HostChannelMarker.Resolve("pim-shell", 1));
        Assert.Equal(HostChannelMode.Browser, HostChannelMarker.Resolve(HostChannelMarker.Product, 2));
    }

    [Fact]
    public void ChannelMarker_PresentMarkerMeansHostChannelWhilePending()
    {
        Assert.Equal(HostChannelMode.HostPending, HostChannelMarker.Resolve(HostChannelMarker.Product, 1));
    }

    [Fact]
    public void ChannelMarker_HandshakeTimeoutNeverFallsBackToBrowser()
    {
        // 规格 §1：标记存在但 500ms 内没有合法 host.state → host/unavailable，
        // **不进入 browser**（browser 会把令牌写进 storage）。
        var mode = HostChannelMarker.OnHandshakeTimeout();

        Assert.Equal(HostChannelMode.HostUnavailable, mode);
        Assert.NotEqual(HostChannelMode.Browser, mode);
        Assert.True(HostSessionProtocol.ReadyTimeout <= TimeSpan.FromMilliseconds(500));
        Assert.True(HostSessionProtocol.RefreshTimeout <= TimeSpan.FromSeconds(10));
        Assert.True(HostSessionProtocol.LogoutTimeout <= TimeSpan.FromSeconds(5));
    }

    // ---- ⑤ host.state → 真实 /auth/me（authenticated 只决定要不要调用）----

    [Fact]
    public void HostState_AuthenticatedTrue_CallsTheRealAuthMeOperation()
    {
        var coordinator = new HostSessionCoordinator();
        var decision = coordinator.OnHostState(authenticated: true);

        Assert.Equal(HostSessionAction.CallAuthMe, decision.Action);
        Assert.Equal("GET", decision.Method);
        Assert.Equal("/api/v1/auth/me", decision.Path);
    }

    [Fact]
    public void HostState_AuthenticatedFalse_DoesNotCallAuthMe()
    {
        var coordinator = new HostSessionCoordinator();
        var decision = coordinator.OnHostState(authenticated: false);

        Assert.Equal(HostSessionAction.ShowHostSignedOut, decision.Action);
        Assert.Null(decision.Path);
    }

    // ---- ⑥ 401 → 宿主单飞刷新：并发共用一次委托，每个 requestId 各得一份回复 ----

    [Fact]
    public async Task Refresh_ConcurrentPageRefreshRequests_DelegateExactlyOnce()
    {
        var coordinator = new HostSessionCoordinator();
        var backend = new FakeBackend(HostRefreshOutcome.Succeeded);

        var replies = await Task.WhenAll(Enumerable.Range(1, 5)
            .Select(i => coordinator.HandleRefreshAsync($"req-{i}", backend)));

        Assert.Equal(1, backend.RefreshCalls);
        Assert.Equal(1, coordinator.RefreshDelegateCount);
        Assert.All(replies, reply => Assert.NotNull(reply));
        Assert.Equal(
            Enumerable.Range(1, 5).Select(i => $"req-{i}").OrderBy(x => x, StringComparer.Ordinal),
            replies.Select(r => r!.RequestId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(replies, reply => Assert.True(reply!.Ok));
    }

    [Fact]
    public async Task Refresh_TransientFailure_KeepsTheHostSessionAlive()
    {
        var coordinator = new HostSessionCoordinator();
        var backend = new FakeBackend(HostRefreshOutcome.Transient);

        var reply = await coordinator.HandleRefreshAsync("req-transient", backend);

        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Equal("transient", reply.Reason);
        Assert.False(reply.SessionEnded);
        // 规格 §6.2：transient 不发 host.session.ended —— 实现不得恒填 expired。
        Assert.Null(reply.SessionEndedReason);
    }

    [Fact]
    public async Task Refresh_InvalidatedSession_EndsTheHostSessionWithExpired()
    {
        var coordinator = new HostSessionCoordinator();
        var backend = new FakeBackend(HostRefreshOutcome.Invalidated);

        var reply = await coordinator.HandleRefreshAsync("req-expired", backend);

        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Equal("invalid", reply.Reason);
        Assert.True(reply.SessionEnded);

        // 规格 §6.2：会话由**另一帧** host.session.ended 结束，reason 必须是 expired。
        // 只钉 SessionEnded 布尔值的话，wire 上的 expired 没有被任何断言约束
        // （Grok round3 I-1）。
        Assert.Equal("expired", reply.SessionEndedReason);
        Assert.Contains(reply.SessionEndedReason!, HostSessionProtocol.SessionEndedReasons);
    }

    [Fact]
    public async Task Refresh_WithoutAccessToken_DoesNotDelegateAndReportsUnauthenticated()
    {
        var coordinator = new HostSessionCoordinator();
        var backend = new FakeBackend(HostRefreshOutcome.NoToken, hasAccessToken: false);

        var reply = await coordinator.HandleRefreshAsync("req-no-token", backend);

        Assert.Equal(0, backend.RefreshCalls);
        Assert.NotNull(reply);
        Assert.False(reply!.Ok);
        Assert.Equal("unauthenticated", reply.Reason);
        Assert.False(reply.SessionEnded);
    }

    // ---- ⑦ page.logout 是页面可以发出的退出请求；其它类型不触发 Logout ----

    [Fact]
    public async Task Logout_PageLogoutRequest_DelegatesTheExistingLogoutOnce()
    {
        var coordinator = new HostSessionCoordinator();
        var backend = new FakeBackend(HostRefreshOutcome.Succeeded);

        var handled = await coordinator.HandleLogoutAsync("req-logout-1", backend);

        Assert.True(handled);
        Assert.Equal(1, backend.LogoutCalls);
        Assert.Equal(1, coordinator.LogoutDelegateCount);
    }

    [Fact]
    public void Logout_DirectionSets_OnlyTheHostCanEndTheSession()
    {
        // 页 → 宿主：page.ready / page.refresh / page.logout（规格 §2）。
        Assert.True(HostSessionProtocol.IsAllowedFromPage(HostSessionProtocol.Types.PageReady));
        Assert.True(HostSessionProtocol.IsAllowedFromPage(HostSessionProtocol.Types.PageRefresh));
        Assert.True(HostSessionProtocol.IsAllowedFromPage(HostSessionProtocol.Types.PageLogout));

        // 宿主 → 页：只有 host.*；session 结束只能由宿主宣告。
        Assert.True(HostSessionProtocol.IsAllowedFromHost(HostSessionProtocol.Types.HostSessionEnded));
        Assert.False(HostSessionProtocol.IsAllowedFromPage(HostSessionProtocol.Types.HostSessionEnded));
        Assert.False(HostSessionProtocol.IsAllowedFromHost(HostSessionProtocol.Types.PageRefresh));

        // 旧草稿的消息类型不再存在。
        foreach (var removed in new[] { "page.ack", "page.unauthorized", "host.error", "session.hello" })
        {
            Assert.DoesNotContain(removed, HostSessionProtocol.KnownTypes);
        }
    }

    // ---- ⑧ 导航代次：文档切换后旧 handshakeId 的迟到结果被丢弃 ----

    [Fact]
    public void HandshakeGeneration_IsRotatedOnNavigationAndStaleIdsAreRejected()
    {
        var coordinator = new HostSessionCoordinator();
        var first = coordinator.HandshakeId;

        Assert.True(coordinator.IsCurrentHandshake(first));
        Assert.True(HostSessionProtocol.IsLowercaseUuid(first));

        coordinator.InvalidateHandshake();
        var second = coordinator.HandshakeId;

        Assert.NotEqual(first, second);
        Assert.False(coordinator.IsCurrentHandshake(first));
        Assert.True(coordinator.IsCurrentHandshake(second));
    }

    private sealed class FakeBackend : IHostSessionBackend
    {
        private readonly HostRefreshOutcome _outcome;
        private int _refreshCalls;
        private int _logoutCalls;

        public FakeBackend(HostRefreshOutcome outcome, bool hasAccessToken = true)
        {
            _outcome = outcome;
            HasAccessToken = hasAccessToken;
        }

        public bool HasAccessToken { get; }

        public int RefreshCalls => Volatile.Read(ref _refreshCalls);

        public int LogoutCalls => Volatile.Read(ref _logoutCalls);

        public async Task<HostRefreshOutcome> RefreshAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _refreshCalls);
            await Task.Delay(30, ct);
            return _outcome;
        }

        public Task LogoutAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _logoutCalls);
            return Task.CompletedTask;
        }
    }
}
