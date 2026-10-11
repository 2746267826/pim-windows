using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Pim.Client.App.Services;
using Pim.Client.Core.Security;
using Pim.Client.Core.Services;

namespace Pim.Client.App;

/// <summary>
/// 审计 F-11 · 嵌入 WebView 宿主（规格：<c>remediation/host-session/reviewed-spec.md</c>）。
///
/// 四件事在**同一个修订**里完成（规格 §7）：
///   1. 不再把 access token 交给页面脚本环境：<c>InjectAuthTokenAsync</c> 与旧的令牌注入脚本已删除；
///   2. 每个文档创建前安装**常量**标记 <c>window.__PIM_HOST_CHANNEL__</c>（无令牌、不插值）；
///   3. 接入消息桥：页 → 宿主是 JSON **字符串**（<c>TryGetWebMessageAsString</c> → 严格解析），
///      宿主 → 页是 <c>PostWebMessageAsJson</c> 的 JSON 对象；
///   4. 接入请求头过滤：只对允许 origin + <c>/api/</c> 前缀 + 宿主当前有令牌的请求
///      <c>SetHeader("Authorization", "Bearer …")</c>，其余 <c>Leave</c>（不删第三方原有的头）。
///
/// 令牌只出现在宿主内存、DPAPI 的 token.json、以及允许的 <c>/api/</c> 请求头上：
/// 不进脚本、不进 storage/cookie、不进桥消息、不进外部浏览器 URL、不进日志。
///
/// 平台边界（保持 NOT-VERIFIED，N-03）：<c>WebResourceRequested</c> 是否真的覆盖页面的
/// <c>fetch</c>/XHR、<c>NavigationStarting</c> 的重定向逐跳行为、<c>NewWindowRequested</c> 的真实
/// 触发面，只能在 Windows runner 的 WebView2 上核（见 <c>Pim.Client.App.Tests</c> 的宿主夹具）。
/// </summary>
public sealed class EmbeddedWebViewHost
{
    private readonly ApiClient _apiClient;
    private readonly AuthService _authService;
    private readonly Action<string> _externalNavigator;
    private volatile HostSessionChannel? _channel;
    private string? _channelOrigin;
    private bool _initialized;
    private volatile bool _handlingPageMessage;

    public EmbeddedWebViewHost(ApiClient apiClient, AuthService authService, Action<string>? externalNavigator = null)
    {
        _apiClient = apiClient;
        _authService = authService;
        // 外部 http(s) 走系统浏览器；测试夹具可注入记录器（默认就是原来的 Process.Start 行为）。
        _externalNavigator = externalNavigator ?? OpenInSystemBrowser;
        View = new WebView2();

        // 规格 §6.2：宿主会话结束（令牌失效 / 托盘退出）要通知当前文档。
        _authService.LoggedOut += OnHostLoggedOut;
    }

    public WebView2 View { get; }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await View.EnsureCoreWebView2Async();
        if (View.CoreWebView2 is not null)
        {
            var core = View.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;

            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.WebMessageReceived += OnWebMessageReceived;

            // 请求头决策要看到每个出站请求（含页面 fetch/XHR）：先登记过滤器，再挂处理器。
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;

            // 规格 §1/§7：常量标记必须在任何文档创建之前安装。脚本里没有令牌、没有 URL。
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HostChannelMarker.Script);
        }

        _initialized = true;
    }

    public async Task NavigateAsync(string route)
    {
        await InitializeAsync();
        EnsureChannel();
        // 这次导航会换文档：先作废旧代次，新文档自己再发 page.ready。
        _channel?.OnTopLevelDocumentNavigation();
        View.CoreWebView2?.Navigate(BuildWebUrl(route));
    }

    public string BuildWebUrl(string route)
    {
        var root = _apiClient.CurrentBaseUrl.TrimEnd('/');
        if (root.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^"/api/v1".Length];
        }

        if (string.IsNullOrWhiteSpace(route))
        {
            route = "/today";
        }

        return $"{root}/{route.TrimStart('/')}";
    }

    /// <summary>
    /// 依当前服务器地址（规格 §3：取导航实际使用的绝对 URL，去掉 <c>/api/v1</c>）建立通道。
    /// 配置解析失败即没有通道（fail-closed）：不信任任何消息来源，也不加任何请求头。
    /// 同一个服务器 origin 复用既有通道（保留当前代次与刷新单飞）；origin 变了才重建。
    /// </summary>
    private void EnsureChannel()
    {
        if (!WebViewOriginPolicy.TryCreate(_apiClient.CurrentBaseUrl, out var policy))
        {
            _channel = null;
            _channelOrigin = null;
            return;
        }

        if (_channel is not null
            && string.Equals(_channelOrigin, policy.DocumentOrigin, StringComparison.Ordinal))
        {
            return;
        }

        _channel = new HostSessionChannel(
            policy,
            new HostSessionCoordinator(),
            new AuthServiceHostSessionBackend(_authService));
        _channelOrigin = policy.DocumentOrigin;
    }

    /// <summary>
    /// 顶层文档导航：按规格 §5 处理外部 http(s)（取消并交给系统浏览器）与危险 scheme（只取消）；
    /// **只有真的会在本 WebView 里换文档的导航**才作废旧代次（被取消的导航没有新文档，
    /// 不能把当前页面的会话代次作废）。
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        switch (WebViewNavigationPolicy.DecideTopLevelNavigation(e.Uri, _apiClient.CurrentBaseUrl))
        {
            case WebNavigationAction.OpenInSystemBrowser:
                e.Cancel = true;
                OpenExternal(e.Uri);
                return;
            case WebNavigationAction.Block:
                e.Cancel = true;
                return;
        }

        // 新文档会自己再发 page.ready；旧代次的迟到结果不再作用于它。
        _channel?.OnTopLevelDocumentNavigation();
    }

    /// <summary>
    /// 新窗口：可信同源留在同一个 WebView；其他 http(s) 交给系统浏览器；危险 scheme / userinfo /
    /// 解析失败只 <c>Handled</c> 不打开。
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 webView)
        {
            return;
        }

        e.Handled = true;
        switch (WebViewNavigationPolicy.DecideNewWindow(e.Uri, _apiClient.CurrentBaseUrl))
        {
            case WebNavigationAction.NavigateInPlace:
                webView.Navigate(e.Uri);
                break;
            case WebNavigationAction.OpenInSystemBrowser:
                OpenExternal(e.Uri);
                break;
        }
    }

    /// <summary>
    /// 页 → 宿主：只接受**字符串**帧（严格解析），来源 origin 必须等于当前允许 origin；
    /// 非法一律丢弃、不回复。宿主 → 页用 <c>PostWebMessageAsJson</c> 发对象，
    /// 且只发仍然属于当前导航代次的帧。
    /// </summary>
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var channel = _channel;
        if (channel is null) return;

        string? text;
        try
        {
            // 规格 §2：页 → 宿主是 JSON **字符串**。WebView2 在消息是别的 JS 类型时抛
            // ArgumentException —— 对象/数字/数组都不是合法帧，直接丢弃。
            text = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        if (text is null) return;

        IReadOnlyList<HostSessionOutbound> outbound;
        _handlingPageMessage = true;
        try
        {
            outbound = await channel.HandlePageMessageAsync(e.Source, text);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Host session message handling failed: {ex.Message}");
            return;
        }
        finally
        {
            _handlingPageMessage = false;
        }

        var core = View.CoreWebView2;
        if (core is null) return;

        try
        {
            foreach (var message in outbound)
            {
                // 旧代次（导航已发生）的迟到结果不回放到新文档。
                if (!channel.IsCurrentGeneration(message.HandshakeId)) continue;
                core.PostWebMessageAsJson(message.Json);
            }
        }
        catch (Exception ex)
        {
            // 窗口在发送过程中被关闭等：丢弃这一批出站帧，不让 async void 处理器抛出未观察异常。
            Logger.Warn($"Posting host session message failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 出站请求头：**每个请求现读**宿主当前 access token；只有允许 origin + <c>/api/</c> 前缀 +
    /// 有令牌才覆盖该请求的 <c>Authorization</c>；其余不动（不 Set、不 Remove 第三方原有的头）。
    /// </summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var decision = WebViewRequestHeaderPolicy.Decide(
            e.Request.Uri,
            _apiClient.CurrentBaseUrl,
            _authService.CurrentAccessToken);

        if (decision.Action != WebRequestHeaderAction.Set || decision.HeaderValue is null)
        {
            return;
        }

        e.Request.Headers.SetHeader("Authorization", decision.HeaderValue);
    }

    /// <summary>
    /// 宿主自己发起的会话结束（令牌失效 / 托盘退出）。页面发起的 <c>page.logout</c> 由
    /// 通道自己回帧，不在这里重复发。
    /// </summary>
    private void OnHostLoggedOut(HostSessionEndReason reason)
    {
        if (_handlingPageMessage) return;

        var channel = _channel;
        if (channel is null) return;

        var dispatcher = View.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        if (dispatcher.CheckAccess())
        {
            PostHostSessionEnded(channel, reason);
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() => PostHostSessionEnded(channel, reason)));
        }
    }

    private void PostHostSessionEnded(HostSessionChannel channel, HostSessionEndReason reason)
    {
        var core = View.CoreWebView2;
        if (core is null) return;

        var message = channel.BuildHostInitiatedSessionEnded(reason);
        if (!channel.IsCurrentGeneration(message.HandshakeId)) return;

        core.PostWebMessageAsJson(message.Json);
    }

    private void OpenExternal(string uri)
    {
        if (!WebViewNavigationPolicy.IsSafeExternalHttpTarget(uri, out var absoluteUri) || absoluteUri is null)
        {
            return;
        }

        _externalNavigator(absoluteUri);
    }

    /// <summary>
    /// 默认的外部打开方式：系统浏览器，参数只是该绝对 http(s) URI（不带令牌）。
    /// 外部浏览器不经过宿主的请求头过滤器。
    /// </summary>
    private static void OpenInSystemBrowser(string absoluteUri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(absoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Opening external URL in the system browser failed: {ex.Message}");
        }
    }
}
