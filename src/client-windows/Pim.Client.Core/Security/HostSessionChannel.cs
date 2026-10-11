namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · 宿主会话通道（规格 §2 / §6）：把 <c>WebMessageReceived</c> 收到的页面帧
/// 变成要回给页面的出站帧，并持有当前导航代次。
///
/// 生产调用点：<c>EmbeddedWebViewHost.OnWebMessageReceived</c>。
/// 它先确认来源（<c>e.Source</c> 的 origin 必须等于当前允许 origin），
/// 再把字符串帧交给 <see cref="HandlePageMessageAsync"/>，最后只把
/// <see cref="IsCurrentGeneration"/> 仍然成立的出站帧 <c>PostWebMessageAsJson</c> 出去。
///
/// 规格要求在这里落地的部分：
///   - 非法帧（类型/版本/字段/长度/来源不对）一律丢弃，不回复；
///   - 只有 <c>page.ready</c> 能建立/刷新会话代次，并只回复一次 <c>host.state</c>；
///   - <c>page.refresh</c> 的结果绑在**发起时的代次**上，导航之后不回放到新文档；
///   - <c>page.logout</c> 是唯一能触发 <c>Logout()</c> 的页面消息。
/// </summary>
public sealed class HostSessionChannel
{
    private static readonly IReadOnlyList<HostSessionOutbound> NoReply = Array.Empty<HostSessionOutbound>();

    private readonly HostSessionCoordinator _coordinator;
    private readonly IHostSessionBackend _backend;
    private readonly WebViewOriginPolicy _originPolicy;

    public HostSessionChannel(
        WebViewOriginPolicy originPolicy,
        HostSessionCoordinator coordinator,
        IHostSessionBackend backend)
    {
        _originPolicy = originPolicy;
        _coordinator = coordinator;
        _backend = backend;
    }

    /// <summary>
    /// <c>host.state.apiOrigin</c> 的值：文档 origin（<c>scheme://host[:port]</c>，默认端口不写）。
    /// 只发 origin，不带 path / query / hash / userinfo。
    /// </summary>
    public string ApiOrigin => _originPolicy.DocumentOrigin;

    /// <summary>当前导航代次；出站帧用它对照，旧代次的结果不再发出。</summary>
    public string HandshakeId => _coordinator.HandshakeId;

    /// <summary>给定代次是否仍是当前代次（旧帧的迟到结果必须丢弃）。</summary>
    public bool IsCurrentGeneration(string handshakeId) => _coordinator.IsCurrentHandshake(handshakeId);

    /// <summary>顶层文档再次导航：作废旧代次。</summary>
    public void OnTopLevelDocumentNavigation() => _coordinator.InvalidateHandshake();

    /// <summary>
    /// 消息来源是否可信：<c>WebMessageReceived</c> 的 <c>e.Source</c> 用同一套 origin 函数解析，
    /// origin 必须等于当前允许 origin（scheme + IDN host + 生效端口），否则丢弃且不回复。
    /// 这不看 <c>window.chrome.webview</c> 是否存在，也不是字段名黑名单。
    /// </summary>
    public bool IsTrustedSource(string? sourceUrl) => _originPolicy.IsSameOrigin(sourceUrl);

    /// <summary>
    /// 处理一条**页 → 宿主**的帧（字符串形式）。返回要发出的出站帧（可能为空 = 丢弃、不回复）。
    /// </summary>
    public async Task<IReadOnlyList<HostSessionOutbound>> HandlePageMessageAsync(
        string? sourceUrl,
        string? text,
        CancellationToken ct = default)
    {
        if (!IsTrustedSource(sourceUrl)) return NoReply;
        if (text is null) return NoReply;

        // 严格 UTF-8 重编码 + 字节级 4096 上限 + 字段白名单 + 必填/取值校验。
        if (!HostSessionProtocol.TryParseText(text, out var message) || message is null) return NoReply;
        if (!HostSessionProtocol.IsAllowedFromPage(message.Type)) return NoReply;

        switch (message.Type)
        {
            case HostSessionProtocol.Types.PageReady:
            {
                // 每次接受 page.ready 都生成新的 handshakeId（导航代次）。
                var handshakeId = _coordinator.AcceptReady();
                var json = HostSessionMessages.HostState(
                    message.RequestId!,
                    message.ClientNonce!,
                    handshakeId,
                    ApiOrigin,
                    _backend.HasAccessToken);

                return new[] { new HostSessionOutbound(handshakeId, json) };
            }

            case HostSessionProtocol.Types.PageRefresh:
            {
                // 结果绑在发起时的代次上：期间若发生顶层导航，代次已变，
                // 调用方（IsCurrentGeneration）不会把这条结果发给新文档。
                var handshakeId = _coordinator.HandshakeId;
                var reply = await HandleRefreshAsync(message.RequestId!, ct).ConfigureAwait(false);
                if (reply is null) return NoReply;

                var outbound = new List<HostSessionOutbound>(2)
                {
                    new(handshakeId, HostSessionMessages.RefreshResult(reply.RequestId, reply.Ok, reply.Reason)),
                };

                if (reply.SessionEnded && !string.IsNullOrEmpty(reply.SessionEndedReason))
                {
                    outbound.Add(new HostSessionOutbound(
                        handshakeId,
                        HostSessionMessages.SessionEnded(handshakeId, reply.SessionEndedReason!)));
                }

                return outbound;
            }

            case HostSessionProtocol.Types.PageLogout:
            {
                var handshakeId = _coordinator.HandshakeId;
                var handled = await _coordinator.HandleLogoutAsync(message.RequestId!, _backend, ct).ConfigureAwait(false);
                if (!handled) return NoReply;

                // 页面只有在收到匹配当前 handshakeId 的 host.session.ended 后才清用户。
                return new[]
                {
                    new HostSessionOutbound(
                        handshakeId,
                        HostSessionMessages.SessionEnded(handshakeId, HostSessionProtocol.EndedReasons.Logout, message.RequestId)),
                };
            }

            default:
                return NoReply;
        }
    }

    /// <summary>
    /// 宿主自己发起的会话结束（托盘/设置里的退出、或刷新确定失效）要发给当前文档的那一帧。
    /// 代次由调用方在发出前用 <see cref="IsCurrentGeneration"/> 复核。
    /// </summary>
    public HostSessionOutbound BuildHostInitiatedSessionEnded(HostSessionEndReason reason)
        => BuildSessionEnded(reason, requestId: null);

    private HostSessionOutbound BuildSessionEnded(HostSessionEndReason reason, string? requestId)
    {
        var handshakeId = _coordinator.HandshakeId;
        return new HostSessionOutbound(handshakeId, HostSessionMessages.SessionEnded(handshakeId, reason.ToWire(), requestId));
    }

    private Task<HostRefreshReply?> HandleRefreshAsync(string requestId, CancellationToken ct)
        => _coordinator.HandleRefreshAsync(requestId, _backend, ct);
}

/// <summary>
/// 一条宿主 → 页的出站帧，连同它所属的导航代次。
/// 调用方在 <c>PostWebMessageAsJson</c> 之前必须确认代次仍然有效。
/// </summary>
public sealed record HostSessionOutbound(string HandshakeId, string Json);
