namespace Pim.Client.Core.Security;

/// <summary>一次出站请求上对 <c>Authorization</c> 的处理决定（规格 §4）。</summary>
public readonly record struct WebRequestHeaderDecision(WebRequestHeaderAction Action, string? HeaderValue)
{
    public static WebRequestHeaderDecision Leave() => new(WebRequestHeaderAction.Leave, null);

    public static WebRequestHeaderDecision Set(string headerValue) => new(WebRequestHeaderAction.Set, headerValue);
}

public enum WebRequestHeaderAction
{
    /// <summary>不动这个请求上已有的 <c>Authorization</c>（不 Set、也不 Remove）。</summary>
    Leave,

    /// <summary>用宿主当前的 access token 覆盖该请求的 <c>Authorization</c>。</summary>
    Set,
}

/// <summary>
/// 审计 F-11 · 请求头决策（规格 §4）。
///
/// 只对「允许 origin 且路径在 <c>/api/</c> 之下且宿主持有令牌」的请求 <c>Set</c> 宿主 Bearer；
/// 其余（第三方、非 API、解析失败、无令牌）一律 <c>Leave</c>，既不加也不删第三方原有的认证头。
///
/// 生产调用点：<c>EmbeddedWebViewHost.OnWebResourceRequested</c>（<c>WebResourceRequested</c> 事件）
/// **每个请求现读** <c>AuthService.CurrentAccessToken</c> 后调用本函数；只有 <see cref="WebRequestHeaderAction.Set"/>
/// 才对该请求 <c>SetHeader("Authorization", …)</c>。令牌只留在宿主内存与这一条出站请求头上。
/// </summary>
public static class WebViewRequestHeaderPolicy
{
    /// <summary>
    /// 对单个请求做决定。解析失败 / 非 http(s) / 有 userinfo / 非允许 origin / 非 <c>/api/</c> 前缀 /
    /// 无令牌 → <see cref="WebRequestHeaderAction.Leave"/>；否则 <see cref="WebRequestHeaderAction.Set"/>。
    /// </summary>
    public static WebRequestHeaderDecision Decide(string? url, string? allowedServerUrl, string? accessToken)
    {
        if (string.IsNullOrEmpty(accessToken)) return WebRequestHeaderDecision.Leave();
        if (!WebViewOriginPolicy.TryCreate(allowedServerUrl, out var policy)) return WebRequestHeaderDecision.Leave();

        return policy.TryBuildAuthorizationHeader(url, accessToken, out var headerValue)
            ? WebRequestHeaderDecision.Set(headerValue)
            : WebRequestHeaderDecision.Leave();
    }
}
