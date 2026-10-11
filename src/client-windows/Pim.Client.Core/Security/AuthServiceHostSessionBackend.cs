using Pim.Client.Core.Services;

namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · 把现有 <see cref="AuthService"/> 适配成宿主会话后端（规格 §6.2）。
///
/// 生产调用点：<c>EmbeddedWebViewHost</c> 在每次 <c>NavigateAsync</c> 时用它构造
/// <see cref="HostSessionChannel"/>。刷新**共用** <see cref="AuthService.RefreshAsync"/>
/// 既有的单飞，不另发一条 <c>/auth/refresh</c>；
/// 也不在这里加冷却或次数上限。
///
/// 结果分类按规格 §6.2 的表：调用前没有 access token → <see cref="HostRefreshOutcome.NoToken"/>；
/// 返回 true → 成功；返回 false 且令牌仍在 → 瞬时失败；返回 false 且令牌已被现有 401 分支清掉
/// （即 <c>AuthService.Logout()</c> 已经跑过）→ 会话确定失效。
/// </summary>
public sealed class AuthServiceHostSessionBackend : IHostSessionBackend
{
    private readonly AuthService _authService;

    public AuthServiceHostSessionBackend(AuthService authService)
    {
        _authService = authService;
    }

    public bool HasAccessToken => !string.IsNullOrEmpty(_authService.CurrentAccessToken);

    public async Task<HostRefreshOutcome> RefreshAsync(CancellationToken ct)
    {
        if (!HasAccessToken) return HostRefreshOutcome.NoToken;

        var refreshed = await _authService.RefreshAsync().ConfigureAwait(false);
        if (refreshed) return HostRefreshOutcome.Succeeded;

        return HasAccessToken ? HostRefreshOutcome.Transient : HostRefreshOutcome.Invalidated;
    }

    public Task LogoutAsync(CancellationToken ct)
    {
        // 页面主动退出：reason 是 logout（区别于令牌失效路径的 expired）。
        _authService.Logout(HostSessionEndReason.Logout);
        return Task.CompletedTask;
    }
}
