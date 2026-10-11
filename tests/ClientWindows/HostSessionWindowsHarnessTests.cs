using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pim.Client.App;
using Pim.Client.Core.Models;
using Pim.Client.Core.Security;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// 审计 F-11 · **Windows 平台验收夹具**（规格 §1–§8）。
///
/// 这是本仓唯一**真实运行 WebView2** 的地方：Windows runner（<c>test-client-app</c> job）上，
/// 在一个离屏但已显示的隐藏窗口里创建生产 <see cref="EmbeddedWebViewHost"/>，
/// 用随机端口上的临时本地 fake API（不是 5858、不是生产、无真实账户）来观测：
///
///   1. <c>AddScriptToExecuteOnDocumentCreatedAsync</c> 安装的常量标记真的出现在页面里，
///      而页面里**没有任何令牌**（<c>__pimToken</c> / <c>sessionStorage</c> / <c>localStorage</c> 全空）；
///   2. 页 → 宿主的 <c>postMessage</c> JSON **字符串** 被 <c>TryGetWebMessageAsString</c> 收下，
///      <c>host.state</c> 以 JSON 对象回给页面，requestId / clientNonce / apiOrigin 全部对上；
///   3. <c>WebResourceRequested</c> 真的罩住页面的 <c>fetch</c>：受信任 <c>/api/</c> 请求带上
///      <c>Authorization: Bearer &lt;canary&gt;</c>，同源非 <c>/api/</c> 请求与**另一个 origin**
///      （另一个随机端口）的请求都不带；
///   4. <c>NavigationStarting</c> / <c>NewWindowRequested</c>：外部 http(s) 被取消并交给系统浏览器
///      （这里注入记录器，不真的拉起浏览器），同源新窗口留在同一个 WebView，危险 scheme 只取消；
///   5. 宿主侧退出（<c>AuthService.Logout</c>，即托盘/令牌失效那条既有路径）真的通知当前文档，
///      页面的 <c>page.logout</c> 也得到带 requestId 的 <c>host.session.ended</c>；
///   6. <c>token.json</c> 确实用 DPAPI <c>CurrentUser</c> 保护并可读回（冷启动恢复）。
///
/// 夹具不驱动托盘菜单与登录窗口（那些需要人机交互）：托盘/LoginWindow 的**交互**保持
/// NOT-VERIFIED（N-05），本文件只证明宿主会话在真实 WebView2 上的行为。
/// </summary>
public sealed class HostSessionWindowsHarnessTests
{
    private const string CanaryAccessToken = "ACCESS-CANARY-9f3a1c";
    private const string CanaryRefreshToken = "REFRESH-CANARY-77b2e4";
    private const string PageNonce = "nonce_12chars";
    private const string PageReadyRequestId = "req-ready-1";

    [Fact]
    public void Harness_RealWebView2_KeepsTokensOutOfThePageAndScopesBearerToTrustedApiRequests()
    {
        var outcome = Sta(async () =>
        {
            using var fixture = await HostFixture.CreateAsync(ProbePage);
            if (!await fixture.Auth.LoginAsync("harness", "secret"))
            {
                throw new Xunit.Sdk.XunitException("Fake login failed before the authenticated WebView2 probe");
            }
            await fixture.NavigateAsync("/today");

            var body = await fixture.Api.WaitForBodyAsync("/api/report", TimeSpan.FromSeconds(90));
            var report = JsonDocument.Parse(body).RootElement.Clone();

            var apiReq = await fixture.Api.WaitForRequestAsync("/api/v1/ping", TimeSpan.FromSeconds(30));
            var pageReq = await fixture.Api.WaitForRequestAsync("/today-marker", TimeSpan.FromSeconds(30));
            var extReq = await fixture.External.WaitForRequestAsync("/api/v1/ping", TimeSpan.FromSeconds(30));

            return new HarnessOutcome(
                Report: report,
                ApiAuth: apiReq?.Authorization,
                PageAuth: pageReq?.Authorization,
                ExtAuth: extReq?.Authorization,
                ApiObserved: apiReq is not null,
                PageObserved: pageReq is not null,
                ExtObserved: extReq is not null
            );
        });

        var report = outcome.Report;

        // 标记：常量脚本真的在文档创建前装上了。
        Assert.Equal("pim-host-windows", report.GetProperty("markerProduct").GetString());
        Assert.Equal(1, report.GetProperty("markerVersion").GetInt32());

        // 令牌：页面里读不到任何令牌。
        Assert.True(report.GetProperty("pimTokenUndefined").GetBoolean(), "页面里存在 window.__pimToken");
        Assert.Equal(0, report.GetProperty("sessionStorageKeys").GetInt32());
        Assert.Equal(0, report.GetProperty("localStorageKeys").GetInt32());
        Assert.False(report.GetProperty("pageSawCanary").GetBoolean(), "页面读到了 canary 令牌");

        // 握手：host.state 以对象形式到达，字段与页面的请求配对。
        Assert.True(report.GetProperty("stateReceived").GetBoolean(), "页面没有收到 host.state");
        Assert.True(report.GetProperty("stateRequestIdMatches").GetBoolean());
        Assert.True(report.GetProperty("stateNonceMatches").GetBoolean());
        Assert.True(report.GetProperty("stateProductMatches").GetBoolean());
        Assert.True(report.GetProperty("stateAuthenticated").GetBoolean());
        Assert.True(report.GetProperty("stateHandshakeIsLowercaseUuid").GetBoolean());
        Assert.StartsWith("http://127.0.0.1:", report.GetProperty("stateApiOrigin").GetString(), StringComparison.Ordinal);

        // 页面实际 fetch 成功执行（无 CORS 阻断）
        Assert.True(report.GetProperty("apiFetchOk").GetBoolean(), "页面 fetch /api/v1/ping 失败");
        Assert.True(report.GetProperty("pageFetchOk").GetBoolean(), "页面 fetch /today-marker 失败");
        Assert.True(report.GetProperty("externalFetchOk").GetBoolean(), "页面 fetch 外部 /api/v1/ping 失败");
        Assert.Equal("{\"status\":\"ok\"}", report.GetProperty("apiBody").GetString());
        Assert.Equal("marker", report.GetProperty("pageBody").GetString());
        Assert.Equal("{\"status\":\"ok\"}", report.GetProperty("externalBody").GetString());

        // 服务端 C# 直接观测断言：受信任 /api/ 请求带上宿主 Bearer；同源非 /api/ 与外站请求都确实到达且不带 Bearer。
        Assert.True(outcome.ApiObserved, "Fake API 未观测到 /api/v1/ping");
        Assert.Equal("Bearer " + CanaryAccessToken, outcome.ApiAuth);

        Assert.True(outcome.PageObserved, "Fake API 未观测到 /today-marker");
        Assert.Null(outcome.PageAuth);

        Assert.True(outcome.ExtObserved, "Fake External 未观测到 /api/v1/ping");
        Assert.Null(outcome.ExtAuth);
    }

    [Fact]
    public void Harness_RealWebView2_LoadsActualWebDistAndEstablishesHostSessionChain()
    {
        var distDir = ResolveWebDistDirectory();
        if (distDir is null || !Directory.Exists(distDir))
        {
            throw new Xunit.Sdk.XunitException(
                "真实 Web bundle 联调夹具需要有效的资源目录（PIM_WEB_DIST_DIR 未设置或目录不存在）。" +
                "等待 root 后续确定 pim-web PR head pinned sha 并将 web dist 产物传入 runner。");
        }

        var (authMeSeen, authMeAuth, userJsonMatch, tokenLeakedInStorage) = Sta(async () =>
        {
            using var fixture = await HostFixture.CreateAsync(pageTemplate: null, webDistDir: distDir);
            if (!await fixture.Auth.LoginAsync("harness", "secret"))
            {
                throw new Xunit.Sdk.XunitException("Fake login failed before the real web-dist probe");
            }
            await fixture.NavigateAsync("/today");

            // 等待页面启动并请求 /api/v1/auth/me
            var authMeReq = await fixture.Api.WaitForRequestAsync("/api/v1/auth/me", TimeSpan.FromSeconds(60));
            var authMeSeen = authMeReq is not null;
            var authMeAuth = authMeReq?.Authorization;

            // 检查 WebView2 脚本环境：验证无凭据泄露至 JS 可见存储
            var core = fixture.Host.View.CoreWebView2!;
            string? pageCheckJson = null;
            var pageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < pageDeadline)
            {
                pageCheckJson = await core.ExecuteScriptAsync("""
                    (function() {
                        const cPfx = 'ACCESS-';
                        const cSfx = 'CANARY';
                        const needle = cPfx + cSfx;
                        const html = document.documentElement.innerHTML;
                        const sessionHas = JSON.stringify(sessionStorage).indexOf(needle) >= 0;
                        const localHas = JSON.stringify(localStorage).indexOf(needle) >= 0;
                        const cookieHas = document.cookie.indexOf(needle) >= 0;
                        const pimTokenHas = (typeof window.__pimToken !== 'undefined') && String(window.__pimToken).indexOf(needle) >= 0;
                        return {
                            leaked: html.indexOf(needle) >= 0 || sessionHas || localHas || cookieHas || pimTokenHas,
                            userRendered: document.body.innerText.indexOf('Tester') >= 0,
                            onToday: location.pathname === '/today'
                        };
                    })()
                """);
                using var sample = JsonDocument.Parse(pageCheckJson);
                if (sample.RootElement.GetProperty("userRendered").GetBoolean()
                    && sample.RootElement.GetProperty("onToday").GetBoolean())
                {
                    break;
                }
                await Task.Delay(100);
            }

            Assert.NotNull(pageCheckJson);
            using var pageCheck = JsonDocument.Parse(pageCheckJson);
            var leaked = pageCheck.RootElement.GetProperty("leaked").GetBoolean();
            var userRendered = pageCheck.RootElement.GetProperty("userRendered").GetBoolean();
            var onToday = pageCheck.RootElement.GetProperty("onToday").GetBoolean();

            return (
                AuthMeSeen: authMeSeen,
                AuthMeAuth: authMeAuth,
                UserJsonMatch: userRendered && onToday,
                TokenLeakedInStorage: leaked
            );
        });

        Assert.True(authMeSeen, "真实 Webbundle 加载后未观测到 GET /api/v1/auth/me 请求");
        Assert.Equal("Bearer " + CanaryAccessToken, authMeAuth);
        Assert.True(userJsonMatch, "Fake API 用户数据未对齐");
        Assert.False(tokenLeakedInStorage, "真实页面在 sessionStorage/localStorage/window.__pimToken 中泄露了访问令牌");
    }

    [Fact]
    public void Harness_RealWebView2_AppliesNavigationAndNewWindowPolicy()
    {
        var result = Sta(async () =>
        {
            using var fixture = await HostFixture.CreateAsync(ProbePage);
            await fixture.NavigateAsync("/today");
            await fixture.Api.WaitForBodyAsync("/api/report", TimeSpan.FromSeconds(90));

            var core = fixture.Host.View.CoreWebView2!;
            var startSource = core.Source;

            // ① 顶层导航到外部 origin：取消，并交给系统浏览器（记录器，不真的拉起浏览器）。
            await core.ExecuteScriptAsync($"location.href = '{fixture.External.Origin}/outside';");
            var externalOpened = await fixture.WaitForExternalOpenCountAsync(1, TimeSpan.FromSeconds(30));
            var sourceAfterCancel = core.Source;

            // ② 危险 scheme 的新窗口：只取消，不导航、不启动进程。
            await core.ExecuteScriptAsync("window.open('javascript:alert(1)');");
            await Task.Delay(500);

            // ③ 同源新窗口：留在同一个 WebView（真实导航发生）。
            await core.ExecuteScriptAsync($"window.open('{fixture.Api.Origin}/opened');");
            var navigatedInPlace = await fixture.WaitForSourceAsync($"{fixture.Api.Origin}/opened", TimeSpan.FromSeconds(30));

            // ④ 外部新窗口：交给系统浏览器。
            await core.ExecuteScriptAsync($"window.open('{fixture.External.Origin}/popup');");
            var popupOpened = await fixture.WaitForExternalOpenCountAsync(2, TimeSpan.FromSeconds(30));

            // ⑤ 被取消的外部导航**不得**作废当前文档的代次：同页再发一条 page.refresh 必须收到回复
            //    （宿主没有 access token → 按规格回 ok:false / reason:unauthenticated）。
            await core.ExecuteScriptAsync(
                "window.chrome.webview.postMessage(JSON.stringify({v:1,type:'page.refresh',requestId:'req-after-cancel'}));");
            var afterCancelRefresh = await fixture.Api.WaitForBodyAsync("/api/report-refresh", TimeSpan.FromSeconds(60));

            return new NavigationProbe(
                startSource,
                sourceAfterCancel,
                externalOpened,
                popupOpened,
                navigatedInPlace,
                fixture.ExternalOpens.ToArray(),
                fixture.External.Origin,
                JsonDocument.Parse(afterCancelRefresh).RootElement.Clone());
        });

        // 外部顶层导航被取消：文档没有离开受信任 origin，URL 交给系统浏览器。
        Assert.StartsWith(result.StartSource, result.SourceAfterCancel, StringComparison.Ordinal);
        Assert.True(result.ExternalOpened, "外部 http(s) 顶层导航没有交给系统浏览器");
        Assert.Contains(result.ExternalOpens, url => url == $"{result.ExternalOrigin}/outside");

        // 危险 scheme 不产生系统浏览器调用。
        Assert.DoesNotContain(result.ExternalOpens, url => url.Contains("javascript", StringComparison.OrdinalIgnoreCase));

        // 同源新窗口在同一个 WebView 内导航；外部新窗口交给系统浏览器。
        Assert.True(result.NavigatedInPlace, "同源新窗口没有留在同一个 WebView");
        Assert.True(result.PopupOpened, "外部新窗口没有交给系统浏览器");
        Assert.Contains(result.ExternalOpens, url => url == $"{result.ExternalOrigin}/popup");

        // 取消的导航没有换文档：同一页面的会话代次仍然有效，刷新请求仍能得到宿主回复。
        Assert.Equal("req-after-cancel", result.AfterCancelRefresh.GetProperty("requestId").GetString());
        Assert.False(result.AfterCancelRefresh.GetProperty("ok").GetBoolean());
        Assert.Equal(
            HostSessionProtocol.RefreshReasons.Unauthenticated,
            result.AfterCancelRefresh.GetProperty("reason").GetString());
    }

    [Fact]
    public void Harness_RealWebView2_EndsTheSessionForBothHostLogoutAndPageLogout()
    {
        var report = Sta(async () =>
        {
            using var fixture = await HostFixture.CreateAsync(EndedPage);
            await fixture.NavigateAsync("/today");
            // 等页面建立起代次（拿到 host.state），否则 ended 的 handshakeId 无从配对。
            await fixture.Api.WaitForBodyAsync("/api/report-ready", TimeSpan.FromSeconds(90));

            // ① 宿主侧退出（托盘 / 令牌失效那条既有路径）→ 当前 WebView 收到 ended(expired)。
            fixture.Auth.Logout();
            var hostEnded = await fixture.Api.WaitForBodyAsync("/api/report-host-ended", TimeSpan.FromSeconds(90));

            // ② 页面 page.logout → 宿主回 ended(logout) 并带上该 requestId。
            var core = fixture.Host.View.CoreWebView2!;
            await core.ExecuteScriptAsync(
                "window.chrome.webview.postMessage(JSON.stringify({v:1,type:'page.logout',requestId:'req-logout-1'}));");
            var pageEnded = await fixture.Api.WaitForBodyAsync("/api/report-page-ended", TimeSpan.FromSeconds(90));

            return (
                Host: JsonDocument.Parse(hostEnded).RootElement.Clone(),
                Page: JsonDocument.Parse(pageEnded).RootElement.Clone());
        });

        Assert.Equal(HostSessionProtocol.EndedReasons.Expired, report.Host.GetProperty("reason").GetString());
        Assert.True(report.Host.GetProperty("handshakeMatches").GetBoolean());
        Assert.False(report.Host.GetProperty("hasRequestId").GetBoolean());

        Assert.Equal(HostSessionProtocol.EndedReasons.Logout, report.Page.GetProperty("reason").GetString());
        Assert.Equal("req-logout-1", report.Page.GetProperty("requestId").GetString());
        Assert.True(report.Page.GetProperty("handshakeMatches").GetBoolean());
    }

    [Fact]
    public async Task Harness_DpapiTokenFileRoundTripsUnderCurrentUserScope()
    {
        const string dummyOrigin = "http://127.0.0.1:1";
        var dir = Path.Combine(Path.GetTempPath(), "pim-f11-dpapi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokenPath = Path.Combine(dir, "token.json");

        try
        {
            var apiClient = new ApiClient(() => new SingleResponseHandler(LoginJson()));
            apiClient.SetBaseUrl(dummyOrigin);
            var auth = new AuthService(apiClient, tokenPath);

            Assert.True(await auth.LoginAsync("harness", "secret"));
            Assert.True(File.Exists(tokenPath));

            var raw = await File.ReadAllBytesAsync(tokenPath);
            // DPAPI CurrentUser：本用户能解，且磁盘上不是明文 JSON。
            var unprotected = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(unprotected);
            Assert.Contains(CanaryAccessToken, json, StringComparison.Ordinal);
            Assert.Contains(CanaryRefreshToken, json, StringComparison.Ordinal);
            Assert.Contains("expiresAt", json, StringComparison.Ordinal);
            Assert.False(raw.Length > 0 && raw[0] == (byte)'{', "token.json 是明文 JSON，没有走 DPAPI");

            // 冷启动恢复：新的 AuthService 从同一个文件读回同一份令牌。
            var restoredClient = new ApiClient();
            restoredClient.SetBaseUrl(dummyOrigin);
            var restored = new AuthService(restoredClient, tokenPath);
            var result = await restored.TryRestoreTokenDetailedAsync();

            Assert.Equal(TokenRestoreResult.Success, result);
            Assert.Equal(CanaryAccessToken, restored.CurrentAccessToken);
            Assert.Equal("tester", restored.CurrentUsername);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------------------------------------------------------------------
    // 页面夹具：只通过 fake API 回报观测结果（不经过宿主，因此不依赖宿主转发未知类型）。
    // ---------------------------------------------------------------------

    private const string ProbePage = """
<!doctype html>
<html><head><meta charset="utf-8"><title>F-11 harness</title></head>
<body>
<script>
(async function () {
  const out = {
    markerProduct: null, markerVersion: 0, pimTokenUndefined: true,
    sessionStorageKeys: 0, localStorageKeys: 0, pageSawCanary: false,
    stateReceived: false, stateRequestIdMatches: false, stateNonceMatches: false,
    stateProductMatches: false, stateAuthenticated: false, stateHandshakeIsLowercaseUuid: false,
    stateApiOrigin: null, apiFetchOk: false, pageFetchOk: false, externalFetchOk: false,
    apiBody: null, pageBody: null, externalBody: null
  };
  try {
    const marker = window.__PIM_HOST_CHANNEL__;
    if (marker) { out.markerProduct = marker.product; out.markerVersion = marker.v; }
    out.pimTokenUndefined = (typeof window.__pimToken === 'undefined');
    out.sessionStorageKeys = Object.keys(sessionStorage).length;
    out.localStorageKeys = Object.keys(localStorage).length;

    const nonce = '__NONCE__';
    const requestId = '__READY_REQUEST_ID__';
    let state = null;
    const receivedFrames = [];
    window.chrome.webview.addEventListener('message', (e) => {
      const m = e.data;
      receivedFrames.push(m);
      if (m && m.type === 'host.state') { state = m; }
      if (m && m.type === 'host.refresh.result') {
        // 导航被取消之后同页再发 page.refresh 的回复（宿主 → 页方向的第二类帧）。
        fetch('/api/report-refresh', {
          method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(m)
        });
      }
    });
    window.chrome.webview.postMessage(JSON.stringify({ v: 1, type: 'page.ready', requestId: requestId, clientNonce: nonce }));

    for (let i = 0; i < 200 && !state; i++) { await new Promise(r => setTimeout(r, 50)); }
    if (state) {
      out.stateReceived = true;
      out.stateRequestIdMatches = (state.requestId === requestId);
      out.stateNonceMatches = (state.clientNonce === nonce);
      out.stateProductMatches = (state.product === 'pim-host-windows');
      out.stateAuthenticated = (state.authenticated === true);
      out.stateHandshakeIsLowercaseUuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(String(state.handshakeId));
      out.stateApiOrigin = state.apiOrigin;
    }

    // 同源页面请求（非 /api/）：宿主必须 Leave。
    try {
      const pageResp = await fetch('/today-marker', { cache: 'no-store' });
      out.pageFetchOk = pageResp.ok;
      out.pageBody = await pageResp.text();
    } catch (e) { out.pageFetchOk = false; }

    // 受信任 origin + /api/ 前缀：宿主必须 Set 宿主 Bearer。
    try {
      const apiResp = await fetch('/api/v1/ping', { cache: 'no-store' });
      out.apiFetchOk = apiResp.ok;
      out.apiBody = await apiResp.text();
    } catch (e) { out.apiFetchOk = false; }

    // 另一个 origin 的 /api/ 请求：宿主必须 Leave。
    try {
      const extResp = await fetch('__EXTERNAL_ORIGIN__/api/v1/ping', { cache: 'no-store' });
      out.externalFetchOk = extResp.ok;
      out.externalBody = await extResp.text();
    } catch (e) { out.externalFetchOk = false; }

    // 在所有握手与 fetch 流程完成后，严格采样页面环境是否出现 canary（拆分字面量避免脚本自身 innerHTML 自命中）
    const canaryPfx = 'ACCESS-';
    const canarySfx = 'CANARY';
    const canaryNeedle = canaryPfx + canarySfx;
    out.pageSawCanary = (document.documentElement.innerHTML.indexOf(canaryNeedle) >= 0)
      || (JSON.stringify(sessionStorage).indexOf(canaryNeedle) >= 0)
      || (JSON.stringify(localStorage).indexOf(canaryNeedle) >= 0)
      || (document.cookie.indexOf(canaryNeedle) >= 0)
      || (JSON.stringify(receivedFrames).indexOf(canaryNeedle) >= 0)
      || (String(window.__pimToken || '').indexOf(canaryNeedle) >= 0);

    await fetch('/api/report', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(out) });
  } catch (e) {
    await fetch('/api/report', {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ error: String(e), stack: String(e && e.stack) })
    });
  }
})();
</script>
</body></html>
""";
    private const string EndedPage = """
<!doctype html>
<html><head><meta charset="utf-8"><title>F-11 harness ended</title></head>
<body>
<script>
(async function () {
  const nonce = '__NONCE__';
  const requestId = '__READY_REQUEST_ID__';
  let handshakeId = null;
  let hostEndedReported = false;
  let pageEndedReported = false;

  function reportEnded(path, frame) {
    return fetch(path, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        type: frame.type, reason: frame.reason,
        requestId: (frame.requestId === undefined ? null : frame.requestId),
        hasRequestId: Object.prototype.hasOwnProperty.call(frame, 'requestId'),
        handshakeMatches: (frame.handshakeId === handshakeId)
      })
    });
  }

  window.chrome.webview.addEventListener('message', async (e) => {
    const m = e.data;
    if (m && m.type === 'host.state') {
      handshakeId = m.handshakeId;
      await fetch('/api/report-ready', {
        method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ requestId: m.requestId, clientNonce: m.clientNonce, handshakeId: m.handshakeId })
      });
      return;
    }
    if (m && m.type === 'host.session.ended' && !hostEndedReported) {
      hostEndedReported = true;
      await reportEnded('/api/report-host-ended', m);
      return;
    }
    if (m && m.type === 'host.session.ended' && !pageEndedReported) {
      pageEndedReported = true;
      await reportEnded('/api/report-page-ended', m);
    }
  });

  window.chrome.webview.postMessage(JSON.stringify({ v: 1, type: 'page.ready', requestId: requestId, clientNonce: nonce }));
})();
</script>
</body></html>
""";

    private sealed record HarnessOutcome(
        JsonElement Report,
        string? ApiAuth,
        string? PageAuth,
        string? ExtAuth,
        bool ApiObserved,
        bool PageObserved,
        bool ExtObserved);

    private static string? ResolveWebDistDirectory()
    {
        var env = Environment.GetEnvironmentVariable("PIM_WEB_DIST_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/ClientWindows/Fixtures/WebDist")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../web-audit/client-web/dist")),
            "/home/coder/workspace/pim-wt/web-audit/client-web/dist"
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private sealed record NavigationProbe(
        string StartSource,
        string SourceAfterCancel,
        bool ExternalOpened,
        bool PopupOpened,
        bool NavigatedInPlace,
        string[] ExternalOpens,
        string ExternalOrigin,
        System.Text.Json.JsonElement AfterCancelRefresh);

    // ---------------------------------------------------------------------
    // STA + 真实 WebView2 夹具
    // ---------------------------------------------------------------------

    /// <summary>在专用 STA 线程上跑完整个异步体（WPF/WebView2 要求 STA + 有消息泵）。</summary>
    private static T Sta<T>(Func<Task<T>> body, int timeoutMs = 300_000)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    result = await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.PushFrame(frame);
        })
        {
            IsBackground = true,
            Name = "pim-f11-webview2-sta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            throw new TimeoutException($"WebView2 夹具超过 {timeoutMs} ms 没有结束（真实运行时行为，需在 Windows runner 上看）。");
        }

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"WebView2 夹具失败：{failure}");
        }

        return result;
    }

    private sealed class HostFixture : IDisposable
    {
        private HostFixture(
            FakeApiServer api,
            FakeApiServer external,
            Window window,
            EmbeddedWebViewHost host,
            ApiClient apiClient,
            AuthService auth,
            List<string> externalOpens,
            string runtimeVersion)
        {
            Api = api;
            External = external;
            Window = window;
            Host = host;
            ApiClient = apiClient;
            Auth = auth;
            ExternalOpens = externalOpens;
            RuntimeVersion = runtimeVersion;
        }

        public FakeApiServer Api { get; }

        public FakeApiServer External { get; }

        public Window Window { get; }

        public EmbeddedWebViewHost Host { get; }

        public ApiClient ApiClient { get; }

        public AuthService Auth { get; }

        /// <summary>记录“本该交给系统浏览器”的 URL（注入记录器，不真的拉起浏览器）。</summary>
        public List<string> ExternalOpens { get; }

        public string RuntimeVersion { get; }

        public static async Task<HostFixture> CreateAsync(string? pageTemplate, string? webDistDir = null)
        {
            // WebView2 运行时（Edge WebView2 Runtime / Evergreen）必须存在；缺失时给出具体错误，
            // 而不是静默跳过（永久 skip 会让这条通道失去验收）。
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();

            var externalOpens = new List<string>();
            var api = new FakeApiServer(pageTemplate, webDistDir);
            var external = new FakeApiServer(pageTemplate, webDistDir);
            api.ExternalOriginForPage = external.Origin;
            external.ExternalOriginForPage = external.Origin;

            var userDataFolder = Path.Combine(Path.GetTempPath(), "pim-f11-webview2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataFolder);

            var apiClient = new ApiClient();
            apiClient.SetBaseUrl(api.Origin);
            var auth = new AuthService(apiClient, Path.Combine(userDataFolder, "token.json"));

            var host = new EmbeddedWebViewHost(apiClient, auth, url => externalOpens.Add(url));
            host.View.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder,
                // runner 上可能没有可用 GPU：用软件渲染，避免初始化失败被误读成产品缺陷。
                AdditionalBrowserArguments = "--disable-gpu",
            };

            var window = new Window
            {
                Title = "PIM F-11 harness",
                Width = 320,
                Height = 240,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = host.View,
            };
            window.Show();

            await host.InitializeAsync();

            return new HostFixture(api, external, window, host, apiClient, auth, externalOpens, runtimeVersion);
        }

        public Task NavigateAsync(string route) => Host.NavigateAsync(route);

        public async Task<bool> WaitForExternalOpenCountAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (ExternalOpens.Count >= expected) return true;
                await Task.Delay(100);
            }

            return ExternalOpens.Count >= expected;
        }

        public async Task<bool> WaitForSourceAsync(string expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (string.Equals(Host.View.CoreWebView2?.Source, expected, StringComparison.Ordinal)) return true;
                await Task.Delay(100);
            }

            return string.Equals(Host.View.CoreWebView2?.Source, expected, StringComparison.Ordinal);
        }

        public void Dispose()
        {
            try { Window.Close(); } catch { }
            Api.Dispose();
            External.Dispose();
        }
    }

    /// <summary>
    /// 随机端口上的临时本地 fake API：服务夹具页面、实际 Web 产物与受信任 <c>/api/</c> 路径，
    /// 把每个请求的路径、<c>Authorization</c> 头与页面回报的观测记录下来。
    /// 不使用 5858 / 生产地址 / 真实账户。
    /// </summary>
    private sealed class FakeApiServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<ReceivedRequest> _requests = new();
        private readonly ConcurrentDictionary<string, string> _bodies = new();

        public FakeApiServer(string? pageHtml, string? webDistDir = null)
        {
            PageHtml = pageHtml;
            WebDistDir = webDistDir;
            Port = FreePort();
            Origin = $"http://127.0.0.1:{Port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public string Origin { get; }

        public string? PageHtml { get; }

        public string? WebDistDir { get; }

        /// <summary>夹具页面里 <c>__EXTERNAL_ORIGIN__</c> 的替换值。</summary>
        public string ExternalOriginForPage { get; set; } = "http://127.0.0.1:9";

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var body = context.Request.HasEntityBody
                ? new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEnd()
                : null;
            var authorization = context.Request.Headers["Authorization"];

            if (body is not null)
            {
                _bodies[path] = body;
            }

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                context.Response.OutputStream.Close();
                _requests.Enqueue(new ReceivedRequest(path, authorization, body, 204));
                return;
            }

            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";

            string payload = "";
            string contentType = "text/plain";
            int status = 200;
            byte[]? rawBytes = null;

            if (path == "/api/v1/auth/login")
            {
                payload = LoginJson();
                contentType = "application/json";
                status = 200;
            }
            else if (path == "/api/v1/auth/me")
            {
                payload = """{"code":0,"message":"ok","data":{"id":"u-1","username":"tester","displayName":"Tester","role":"user"}}""";
                contentType = "application/json";
                status = 200;
            }
            else if (path.StartsWith("/api/report", StringComparison.Ordinal))
            {
                payload = "{\"ok\":true}";
                contentType = "application/json";
                status = 200;
            }
            else if (WebDistDir is not null && Directory.Exists(WebDistDir) && !path.StartsWith("/api/", StringComparison.Ordinal))
            {
                var relativePath = path.TrimStart('/');
                var candidate = Path.Combine(WebDistDir, relativePath);
                if (File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    rawBytes = File.ReadAllBytes(candidate);
                    contentType = GetMimeType(candidate);
                    status = 200;
                }
                else
                {
                    var indexPath = Path.Combine(WebDistDir, "index.html");
                    if (File.Exists(indexPath))
                    {
                        payload = File.ReadAllText(indexPath, Encoding.UTF8);
                        contentType = "text/html; charset=utf-8";
                        status = 200;
                    }
                    else
                    {
                        payload = "index.html not found";
                        status = 404;
                    }
                }
            }
            else if (path is "/today" or "/opened")
            {
                payload = (PageHtml ?? "")
                    .Replace("__EXTERNAL_ORIGIN__", ExternalOriginForPage, StringComparison.Ordinal)
                    .Replace("__NONCE__", PageNonce, StringComparison.Ordinal)
                    .Replace("__READY_REQUEST_ID__", PageReadyRequestId, StringComparison.Ordinal);
                (contentType, status) = ("text/html; charset=utf-8", 200);
            }
            else if (path == "/today-marker")
            {
                (payload, contentType, status) = ("marker", "text/plain", 200);
            }
            else if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                (payload, contentType, status) = ("{\"status\":\"ok\"}", "application/json", 200);
            }
            else
            {
                (payload, contentType, status) = ("not found", "text/plain", 404);
            }

            var bytes = rawBytes ?? Encoding.UTF8.GetBytes(payload);
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.OutputStream.Close();
            _requests.Enqueue(new ReceivedRequest(path, authorization, body, status));
        }

        private static string GetMimeType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".js" or ".mjs" => "application/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".woff2" => "font/woff2",
                ".woff" => "font/woff",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream",
            };
        }

        public async Task<string> WaitForBodyAsync(string path, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_bodies.TryGetValue(path, out var body)) return body;
                await Task.Delay(100);
            }

            throw new Xunit.Sdk.XunitException($"夹具页面没有回报 {path}（{timeout.TotalSeconds:0} 秒内没有观测数据）");
        }

        public async Task<ReceivedRequest?> WaitForRequestAsync(string path, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var req = _requests.Reverse().FirstOrDefault(r => r.Path == path);
                if (req is not null) return req;
                await Task.Delay(50);
            }
            return _requests.Reverse().FirstOrDefault(r => r.Path == path);
        }

        public string? GetObservedAuthorization(string path)
            => _requests.Reverse().FirstOrDefault(r => r.Path == path)?.Authorization;

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _cts.Dispose();
        }

        public sealed record ReceivedRequest(string Path, string? Authorization, string? Body, int StatusCode);
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly string _json;

        public SingleResponseHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private static string LoginJson()
        => "{\"code\":0,\"message\":\"ok\",\"data\":{\"accessToken\":\"" + CanaryAccessToken
            + "\",\"refreshToken\":\"" + CanaryRefreshToken
            + "\",\"expiresAt\":\"2099-01-01T00:00:00+00:00\","
            + "\"user\":{\"id\":\"u-1\",\"username\":\"tester\",\"displayName\":\"Tester\",\"role\":\"user\"}}}";
}
