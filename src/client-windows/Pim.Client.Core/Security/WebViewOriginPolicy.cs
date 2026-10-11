using System.Diagnostics.CodeAnalysis;

namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · REQ-COM-07 / AC-COM-07：嵌入页凭据边界的**纯策略**。
///
/// 背景：整改前 <c>EmbeddedWebViewHost</c> 把 access token 写进页面脚本环境，
/// 与 <c>AGENTS.md</c> B5「Tokens live in memory or encrypted storage only — never in WebView」冲突。
/// 现在的形态是：令牌只留在宿主内存 / DPAPI 文件，由宿主在传输层给**允许的 origin + 允许的
/// API 路径**附加 <c>Authorization</c> 头，页面永远拿不到令牌。
///
/// 本类是那个形态里可以脱离 WebView2 单独验证的部分：源比较与请求头决策。
/// 它**不**依赖 Microsoft.Web.WebView2，因此能在 Linux/CI 上真实执行 ——
/// WebView2 的真实导航/请求头行为仍然只能在 Windows runner 上验证
/// （<c>Pim.Client.App.Tests/HostSessionWindowsHarnessTests</c>）。
///
/// 设计要点：
///   - 比较的是**解析后**的 scheme + host + effective port，不是字符串前缀；
///   - 只接受 http/https；拒绝 userinfo、非 http(s) 协议、后缀伪域、缺 host、
///     「字符串里夹着合法 URL」这类输入；
///   - 只有目标 origin 且路径在 <c>/api/</c> 下时才注入 Authorization；
///   - 导航只允许目标 origin 下的路径，站外与新窗口一律交给系统浏览器或取消。
/// </summary>
public sealed class WebViewOriginPolicy
{
    /// <summary>允许注入 Authorization 的 API 路径前缀。</summary>
    public const string ApiPathPrefix = "/api/";

    private readonly string _scheme;
    private readonly string _host;
    private readonly int _port;

    private WebViewOriginPolicy(string scheme, string host, int port)
    {
        _scheme = scheme;
        _host = host;
        _port = port;
    }

    /// <summary>
    /// 解析后的规范 origin（总是带端口，如 <c>https://pim.example.com:8443</c>）。
    /// IPv6 主机带方括号。用于相等比较，不是 wire 上的值。
    /// </summary>
    public string Origin => _host.Contains(':', StringComparison.Ordinal) ? $"{_scheme}://[{_host}]:{_port}" : $"{_scheme}://{_host}:{_port}";

    /// <summary>
    /// **文档 origin**（规格 §2 的 <c>host.state.apiOrigin</c>）：<c>scheme://host[:port]</c>，
    /// **默认端口不写**；IPv6 主机带方括号（<c>http://[::1]:8080</c>）。
    /// 比较仍用同一套 origin 函数（<see cref="IsSameOrigin"/>），缺省端口 <c>:443</c> / <c>:80</c> 判等。
    /// </summary>
    public string DocumentOrigin
    {
        get
        {
            var displayHost = _host.Contains(':', StringComparison.Ordinal) ? $"[{_host}]" : _host;
            var defaultPort = _scheme == Uri.UriSchemeHttps ? 443 : 80;
            return _port == defaultPort
                ? $"{_scheme}://{displayHost}"
                : $"{_scheme}://{displayHost}:{_port}";
        }
    }

    /// <summary>
    /// 从配置的服务端地址构造策略。失败即 <c>false</c>（fail-closed），
    /// 调用方不得在解析失败时退化成“允许一切”。
    /// </summary>
    public static bool TryCreate(string? serverUrl, [NotNullWhen(true)] out WebViewOriginPolicy? policy)
    {
        policy = null;
        if (string.IsNullOrWhiteSpace(serverUrl)) return false;

        // 显式拒绝含 userinfo 的地址：`https://evil.com@good.com/` 这类输入
        // 在按字符串比较时会被误判成 good.com。
        var trimmed = serverUrl.Trim();
        if (trimmed.Contains('@', StringComparison.Ordinal)) return false;
        if (HasControlOrWhitespace(trimmed)) return false;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        // 只接受“纯 origin/路径”的配置：查询串与片段不属于服务端源。
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;

        var port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        // 规格 §3.3：两边都取 IdnHost 再做序数比较（Unicode 主机与对应 punycode 因此相等；
        // IPv6 的 Host 带方括号而 IdnHost 不带）。
        policy = new WebViewOriginPolicy(uri.Scheme, uri.IdnHost.ToLowerInvariant(), port);
        return true;
    }

    /// <summary>给定 URL 是否与配置的服务端 origin 完全一致（scheme + host + 生效端口）。</summary>
    public bool IsSameOrigin(string? url)
    {
        if (!TryParseAbsoluteHttp(url, out var uri)) return false;
        if (uri.UserInfo.Length > 0) return false;

        var port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80) : uri.Port;
        return uri.Scheme == _scheme
            && string.Equals(uri.IdnHost, _host, StringComparison.Ordinal)
            && port == _port;
    }

    /// <summary>
    /// 是否允许把配置里的 origin 导航到这个地址：只允许同源，且必须是绝对 http(s) URL。
    /// 站外地址、`javascript:` / `data:` / `file:` 等一律拒绝。
    /// </summary>
    public bool IsAllowedNavigation(string? url) => IsSameOrigin(url);

    /// <summary>
    /// 是否为允许携带凭据的 API 请求：同源 **且** 路径在 <c>/api/</c> 之下。
    /// 站外请求与非 API 路径都不带令牌（避免把凭据漏给同源的静态资源/第三方脚本）。
    /// </summary>
    public bool IsAllowedApiRequest(string? url)
    {
        if (!TryParseAbsoluteHttp(url, out var uri)) return false;
        if (uri.UserInfo.Length > 0) return false;
        if (!IsSameOrigin(url)) return false;
        return uri.AbsolutePath.StartsWith(ApiPathPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// 决策结果：是否给这个请求附加 Authorization，以及附加什么。
    /// header 值只在宿主内存里拼装，绝不进入页面脚本环境。
    /// </summary>
    public bool TryBuildAuthorizationHeader(string? url, string? accessToken, [NotNullWhen(true)] out string? headerValue)
    {
        headerValue = null;
        if (string.IsNullOrEmpty(accessToken)) return false;
        if (!IsAllowedApiRequest(url)) return false;

        headerValue = "Bearer " + accessToken;
        return true;
    }

    private static bool TryParseAbsoluteHttp(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (HasControlOrWhitespace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrWhiteSpace(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    private static bool HasControlOrWhitespace(string text)
        => text.Any(char.IsWhiteSpace) || text.Any(char.IsControl);

}
