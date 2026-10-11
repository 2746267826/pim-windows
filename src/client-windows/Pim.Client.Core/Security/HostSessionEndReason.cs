namespace Pim.Client.Core.Security;

/// <summary>
/// 宿主会话结束的原因（规格 §2：<c>host.session.ended.reason</c> 只允许
/// <c>logout</c> / <c>expired</c>）。
///
/// 用在 <c>AuthService.Logout</c>：现有的令牌失效路径（刷新 401 / 恢复令牌 401）是
/// <see cref="Expired"/>；用户主动退出（页面的 <c>page.logout</c>，或将来托盘/设置里的
/// 「退出登录」）是 <see cref="Logout"/>。
/// </summary>
public enum HostSessionEndReason
{
    /// <summary>用户主动退出：<c>logout</c>。</summary>
    Logout,

    /// <summary>令牌确定失效 / 会话过期：<c>expired</c>。</summary>
    Expired,
}

public static class HostSessionEndReasonExtensions
{
    /// <summary>wire 上的 <c>reason</c> 字符串。</summary>
    public static string ToWire(this HostSessionEndReason reason)
        => reason == HostSessionEndReason.Logout
            ? HostSessionProtocol.EndedReasons.Logout
            : HostSessionProtocol.EndedReasons.Expired;
}
