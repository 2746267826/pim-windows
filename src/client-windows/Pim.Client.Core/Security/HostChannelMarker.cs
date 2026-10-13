namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · 宿主通道标记（规格 §1）。
///
/// 嵌入宿主在每次文档创建前安装一条**常量**脚本；页面读一次这个标记来决定通道：
/// 没有标记 / product 不对 / 版本不是 1 → <see cref="HostChannelMode.Browser"/>，
/// 立即走今天的 localStorage 登录；否则进入 host 通道。
///
/// <c>window.chrome.webview</c> 的存在**不**选择通道：普通浏览器、Capacitor/Tauri、
/// 以及只设置 <c>__PIM_SHELL__</c> 的第二个壳都可能暴露它。标记本身不是身份认证。
///
/// 生产安装点：<c>EmbeddedWebViewHost.InitializeAsync</c> 的
/// <c>AddScriptToExecuteOnDocumentCreatedAsync</c>（脚本出口见
/// <see cref="EmbeddedWebViewScripts.BuildHostChannelMarkerScript"/>）。
/// </summary>
public static class HostChannelMarker
{
    /// <summary>产品常量。</summary>
    public const string Product = "pim-host-windows";

    /// <summary>标记挂载的全局名。</summary>
    public const string GlobalName = "__PIM_HOST_CHANNEL__";

    /// <summary>
    /// 文档创建脚本。**必须是常量、不允许插值**：里面没有 access token、refresh token、
    /// <c>Authorization</c>、URL 或用户信息。
    /// </summary>
    public const string Script = "window.__PIM_HOST_CHANNEL__={v:1,product:\"pim-host-windows\"};";

    /// <summary>
    /// 依标记内容决定通道。任何缺失/不匹配都回 <see cref="HostChannelMode.Browser"/>。
    /// </summary>
    public static HostChannelMode Resolve(string? product, int? version)
        => string.Equals(product, Product, StringComparison.Ordinal) && version == 1
            ? HostChannelMode.HostPending
            : HostChannelMode.Browser;

    /// <summary>
    /// 握手时限内没有对上 <c>requestId</c> + <c>clientNonce</c> 的合法 <c>host.state</c>。
    /// **绝不回退到 <see cref="HostChannelMode.Browser"/>**（那会把令牌写进 storage），
    /// 而是停在 host 通道的可重试错误态。
    /// </summary>
    public static HostChannelMode OnHandshakeTimeout()
        => HostChannelMode.HostUnavailable;
}

/// <summary>页面所处通道。</summary>
public enum HostChannelMode
{
    /// <summary>没有宿主标记：今天的行为（localStorage 登录 / 刷新 / 退出）。</summary>
    Browser,

    /// <summary>有标记但还没拿到合法 <c>host.state</c>：不显示账密框，不发 setTokens/clearTokens。</summary>
    HostPending,

    /// <summary>有标记但握手没能成功：可重试错误页，仍是 host 通道。</summary>
    HostUnavailable,
}
