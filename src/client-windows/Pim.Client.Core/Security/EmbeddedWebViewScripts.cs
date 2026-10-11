namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · 宿主注入脚本的唯一生产出口（规格 §1 / §8）。
///
/// 只有一条**常量**标记脚本：
/// <c>window.__PIM_HOST_CHANNEL__={v:1,product:"pim-host-windows"};</c>
/// 它由 <c>EmbeddedWebViewHost.InitializeAsync</c> 通过
/// <c>AddScriptToExecuteOnDocumentCreatedAsync</c> 在任何文档创建之前安装。
/// 脚本是常量、不允许插值：里面没有 access token、refresh token、<c>Authorization</c>、
/// URL 或用户信息。
///
/// 旧令牌注入脚本（<c>window.__pimToken</c> + <c>sessionStorage.setItem('accessToken', …)</c>）
/// 已随 <c>EmbeddedWebViewHost.InjectAuthTokenAsync</c> 一起删除：令牌不再进入
/// <c>ExecuteScriptAsync</c> 参数、文档创建脚本、<c>localStorage</c>/<c>sessionStorage</c>、
/// Cookie、桥消息、外部浏览器 URL 或日志（规格 §8）。
/// </summary>
public static class EmbeddedWebViewScripts
{
    /// <summary>宿主标记脚本（常量；不含任何令牌）。</summary>
    public static string BuildHostChannelMarkerScript() => HostChannelMarker.Script;
}
