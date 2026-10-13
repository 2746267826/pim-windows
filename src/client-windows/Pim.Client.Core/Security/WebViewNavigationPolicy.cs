namespace Pim.Client.Core.Security;

/// <summary>对导航/新窗口目标的处理决定（规格 §5）。</summary>
public enum WebNavigationAction
{
    /// <summary>在同一个嵌入 WebView 里导航（可信同源新窗口/同源顶层导航）。</summary>
    NavigateInPlace,

    /// <summary>交给系统浏览器（外部 http(s)；不把令牌放进 URL）。</summary>
    OpenInSystemBrowser,

    /// <summary>取消：不导航，也不启动进程（危险 scheme / userinfo / 解析失败）。</summary>
    Block,
}

/// <summary>
/// 审计 F-11 · 导航决策（规格 §5）。
///
/// 生产调用点：<c>EmbeddedWebViewHost.OnNewWindowRequested</c>（<c>NewWindowRequested</c>）与
/// <c>EmbeddedWebViewHost.OnNavigationStarting</c>（顶层 <c>NavigationStarting</c>）——
/// 两个事件都把目标 URI 交给本函数，按返回的动作真正 <c>Navigate</c> / <c>Process.Start</c> / <c>Cancel</c>。
///
/// 子资源（脚本、图片、字体）不是顶层导航，不因为 origin 不同被取消：本函数只用于上面两个事件。
///
/// 平台边界：重定向是否逐跳触发 <c>NavigationStarting</c>、<c>NewWindowRequested</c> 的真实触发面
/// 保持 NOT-VERIFIED（N-03），只能在 Windows runner 的 WebView2 上核。
/// </summary>
public static class WebViewNavigationPolicy
{
    /// <summary>
    /// 新窗口（<c>window.open</c> / <c>target=_blank</c>）目标：
    /// 允许 origin → 同一个 WebView 内导航；其他 http(s) → 系统浏览器；危险 scheme / userinfo / 解析失败 → 取消。
    /// </summary>
    public static WebNavigationAction DecideNewWindow(string? uri, string? allowedServerUrl)
        => Decide(uri, allowedServerUrl);

    /// <summary>
    /// 顶层导航目标：与 <see cref="DecideNewWindow"/> 同一套判定
    /// （允许 origin 的 http(s) 就地导航；外部 http(s) 交给系统浏览器；危险 scheme 只取消）。
    /// </summary>
    public static WebNavigationAction DecideTopLevelNavigation(string? uri, string? allowedServerUrl)
        => Decide(uri, allowedServerUrl);

    private static WebNavigationAction Decide(string? uri, string? allowedServerUrl)
    {
        // 配置本身不合法 → 不允许任何导航（fail-closed），也不启动进程。
        if (!WebViewOriginPolicy.TryCreate(allowedServerUrl, out var policy)) return WebNavigationAction.Block;

        if (policy.IsSameOrigin(uri)) return WebNavigationAction.NavigateInPlace;

        return IsSafeExternalHttpTarget(uri, out _)
            ? WebNavigationAction.OpenInSystemBrowser
            : WebNavigationAction.Block;
    }

    /// <summary>
    /// 是否为可以交给系统浏览器的外部目标：绝对 <c>http(s)</c>、有主机、无 userinfo。
    /// <c>blob:https://…</c> / <c>javascript:</c> / <c>data:</c> / <c>file:</c> / <c>about:</c> /
    /// <c>ms-appx:</c> 一律不是。
    /// </summary>
    public static bool IsSafeExternalHttpTarget(string? uri, out string? absoluteUri)
    {
        absoluteUri = null;
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (uri.Any(char.IsWhiteSpace) || uri.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrWhiteSpace(parsed.Host)) return false;
        if (parsed.UserInfo.Length > 0) return false;

        absoluteUri = parsed.AbsoluteUri;
        return true;
    }
}
