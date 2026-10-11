namespace Pim.Client.Core.Security;

/// <summary>收到合法 <c>host.state</c> 后页面要做的动作（规格 §6.1）。</summary>
public enum HostSessionAction
{
    /// <summary>未接线（保留给不进入宿主会话的路径）。</summary>
    None,

    /// <summary><c>authenticated === true</c>：调用现有 <c>GET /api/v1/auth/me</c>，用户只来自它。</summary>
    CallAuthMe,

    /// <summary><c>authenticated === false</c>：不调用 me，显示宿主未登录说明。</summary>
    ShowHostSignedOut,
}

/// <summary>宿主会话动作 + 需要发起的真实请求（避免只断言一个布尔标志）。</summary>
public sealed record HostStateDecision(HostSessionAction Action, string? Method, string? Path)
{
    public static readonly HostStateDecision None = new(HostSessionAction.None, null, null);

    /// <summary>规格 §6.1：<c>authenticated</c> 只决定要不要调用现有 <c>GET /api/v1/auth/me</c>。</summary>
    public static readonly HostStateDecision CallMe = new(HostSessionAction.CallAuthMe, "GET", "/api/v1/auth/me");

    /// <summary>规格 §6.1：<c>authenticated === false</c> 不调用 me，用户保持 null。</summary>
    public static readonly HostStateDecision SignedOut = new(HostSessionAction.ShowHostSignedOut, null, null);
}

/// <summary>现有 <c>AuthService.RefreshAsync</c> 的结果分类（规格 §6.2）。</summary>
public enum HostRefreshOutcome
{
    /// <summary>刷新成功。</summary>
    Succeeded,

    /// <summary>返回 false 且令牌仍在（网络/瞬时失败）。</summary>
    Transient,

    /// <summary>返回 false 且令牌已被现有 401 分支清掉：会话确定失效。</summary>
    Invalidated,

    /// <summary>调用前就没有 access token。</summary>
    NoToken,
}

/// <summary>宿主侧刷新/退出的真实实现（生产由 <see cref="AuthServiceHostSessionBackend"/> 适配 AuthService）。</summary>
public interface IHostSessionBackend
{
    bool HasAccessToken { get; }

    Task<HostRefreshOutcome> RefreshAsync(CancellationToken ct);

    Task LogoutAsync(CancellationToken ct);
}

/// <summary>
/// 页面每个 <c>page.refresh</c> 收到的一份结果（requestId 各自对上）。
///
/// <paramref name="SessionEndedReason"/> 是**另一帧**：规格 §6.2 在刷新确定失效时，
/// 除了 <c>host.refresh.result</c>（<c>ok:false, reason:invalid</c>）还要发
/// <c>host.session.ended</c>（<c>reason:expired</c>）。
/// </summary>
public sealed record HostRefreshReply(
    string RequestId,
    bool Ok,
    string? Reason,
    bool SessionEnded,
    string? SessionEndedReason);

/// <summary>
/// 审计 F-11 · 宿主会话协调器（规格 §6）。
///
/// 行为：
///   - 接受一次 <c>page.ready</c> 时轮换 <c>handshakeId</c>（导航代次）；
///   - 顶层文档再次导航时作废旧代次，旧代次的迟到结果不再作用于新文档；
///   - 页面并发 401 共用**一次** <see cref="IHostSessionBackend.RefreshAsync"/>
///     （宿主侧再与 <c>AuthService.RefreshAsync</c> 自身的单飞合并）；
///   - 每个 <c>requestId</c> 各得一份回复；确定失效时另外发 <c>host.session.ended</c>（<c>expired</c>）；
///   - <c>page.logout</c> 委托一次现有 <c>Logout()</c>；其他页面消息不能触发 Logout。
///
/// 不额外加 1 秒冷却，也不加每分钟次数上限（规格 §6.2）。
/// </summary>
public sealed class HostSessionCoordinator
{
    private readonly object _sync = new();
    private string _handshakeId = NewHandshakeId();
    private Task<HostRefreshOutcome>? _refreshInFlight;

    /// <summary>当前导航代次 id（小写 UUID）。</summary>
    public string HandshakeId
    {
        get { lock (_sync) { return _handshakeId; } }
    }

    /// <summary>被委托到 <see cref="IHostSessionBackend.RefreshAsync"/> 的次数（单飞验证用）。</summary>
    public int RefreshDelegateCount { get; private set; }

    /// <summary>被委托到 <see cref="IHostSessionBackend.LogoutAsync"/> 的次数。</summary>
    public int LogoutDelegateCount { get; private set; }

    /// <summary>新的小写 UUID 代次。</summary>
    public static string NewHandshakeId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    /// <summary>规格 §2：接受一次合法的 <c>page.ready</c>，生成新的 <c>handshakeId</c> 并返回。</summary>
    public string AcceptReady()
    {
        InvalidateHandshake();
        return HandshakeId;
    }

    /// <summary>
    /// 收到合法 <c>host.state</c> 后的动作：<c>authenticated</c> 只决定要不要调用现有
    /// <c>GET /api/v1/auth/me</c>（用户对象只来自那次调用的成功结果）。
    /// </summary>
    public HostStateDecision OnHostState(bool authenticated)
        => authenticated ? HostStateDecision.CallMe : HostStateDecision.SignedOut;

    /// <summary>
    /// 处理一条 <c>page.refresh</c>：先看宿主持不持有 access token（没有就不委托、回
    /// <c>unauthenticated</c>），否则把并发的这些请求合并到同一次
    /// <see cref="IHostSessionBackend.RefreshAsync"/>，每个 <c>requestId</c> 各得一份结果。
    /// </summary>
    public async Task<HostRefreshReply?> HandleRefreshAsync(
        string requestId,
        IHostSessionBackend backend,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requestId)) return null;

        // 规格 §6.2 第一行：调用前没有 access token → 不委托，ok:false / reason:unauthenticated，
        // 不发 host.session.ended，也不调用 Logout。
        if (!backend.HasAccessToken)
        {
            return new HostRefreshReply(requestId, false, HostSessionProtocol.RefreshReasons.Unauthenticated, false, null);
        }

        Task<HostRefreshOutcome> refresh;
        lock (_sync)
        {
            if (_refreshInFlight is { IsCompleted: false })
            {
                refresh = _refreshInFlight;
            }
            else
            {
                RefreshDelegateCount++;
                refresh = _refreshInFlight = backend.RefreshAsync(ct);
            }
        }

        HostRefreshOutcome outcome;
        try
        {
            outcome = await refresh.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = HostRefreshOutcome.Transient;
        }
        catch
        {
            // 刷新自身抛错按瞬时失败处理：保持宿主会话，页面这一次请求失败但可重试。
            outcome = HostRefreshOutcome.Transient;
        }

        return outcome switch
        {
            HostRefreshOutcome.Succeeded => new HostRefreshReply(requestId, true, null, false, null),
            HostRefreshOutcome.NoToken => new HostRefreshReply(requestId, false, HostSessionProtocol.RefreshReasons.Unauthenticated, false, null),
            HostRefreshOutcome.Invalidated => new HostRefreshReply(
                requestId,
                false,
                HostSessionProtocol.RefreshReasons.Invalid,
                true,
                HostSessionProtocol.EndedReasons.Expired),
            _ => new HostRefreshReply(requestId, false, HostSessionProtocol.RefreshReasons.Transient, false, null),
        };
    }

    /// <summary>
    /// 处理一条 <c>page.logout</c>：委托一次现有 <c>Logout()</c>。
    /// 其它页面消息不会走到这里（<see cref="HostSessionProtocol.IsAllowedFromPage"/> 先过滤）。
    /// </summary>
    public async Task<bool> HandleLogoutAsync(
        string requestId,
        IHostSessionBackend backend,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requestId)) return false;

        lock (_sync)
        {
            LogoutDelegateCount++;
        }

        await backend.LogoutAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>顶层文档再次导航：作废旧代次（旧 id 的迟到结果不再作用于新文档）。</summary>
    public void InvalidateHandshake()
    {
        lock (_sync)
        {
            _handshakeId = NewHandshakeId();
        }
    }

    /// <summary>给定代次是否仍是当前代次（旧代次的回复必须被丢弃）。</summary>
    public bool IsCurrentHandshake(string handshakeId)
    {
        if (string.IsNullOrEmpty(handshakeId)) return false;
        lock (_sync)
        {
            return string.Equals(_handshakeId, handshakeId, StringComparison.Ordinal);
        }
    }
}
