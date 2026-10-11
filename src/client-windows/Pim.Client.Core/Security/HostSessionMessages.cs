using System.Text;
using System.Text.Json;

namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · 宿主 → 页的出站帧构造（规格 §2）。
///
/// 只有三种帧，字段严格按 wire 表，**不插值**：<c>host.state</c> / <c>host.refresh.result</c> /
/// <c>host.session.ended</c>。任何令牌都不进来 —— 这里没有令牌参数。
///
/// 出站帧由宿主用 <c>PostWebMessageAsJson</c> 以 JSON **对象**发给页面（页 → 宿主方向是字符串）。
/// </summary>
public static class HostSessionMessages
{
    /// <summary><c>page.ready</c> 的回复：必须同时对上 requestId 与 clientNonce。</summary>
    public static string HostState(
        string requestId,
        string clientNonce,
        string handshakeId,
        string apiOrigin,
        bool authenticated)
        => Write(writer =>
        {
            writer.WriteNumber("v", HostSessionProtocol.Version);
            writer.WriteString("type", HostSessionProtocol.Types.HostState);
            writer.WriteString("requestId", requestId);
            writer.WriteString("clientNonce", clientNonce);
            writer.WriteString("handshakeId", handshakeId);
            writer.WriteString("product", HostChannelMarker.Product);
            writer.WriteString("apiOrigin", apiOrigin);
            writer.WriteBoolean("authenticated", authenticated);
        });

    /// <summary>
    /// <c>page.refresh</c> 的回复。规格 §2：<c>reason</c> 只在 <c>ok:false</c> 时出现，
    /// 且只允许 <c>invalid</c> / <c>transient</c> / <c>unauthenticated</c>。
    /// </summary>
    public static string RefreshResult(string requestId, bool ok, string? reason)
        => Write(writer =>
        {
            writer.WriteNumber("v", HostSessionProtocol.Version);
            writer.WriteString("type", HostSessionProtocol.Types.HostRefreshResult);
            writer.WriteString("requestId", requestId);
            writer.WriteBoolean("ok", ok);
            if (!ok && !string.IsNullOrEmpty(reason))
            {
                writer.WriteString("reason", reason);
            }
        });

    /// <summary>
    /// 会话结束帧。回答 <c>page.logout</c> 时带上该 <c>requestId</c>；
    /// 宿主自己发起的结束（如刷新确定失效、托盘退出）不带。
    /// </summary>
    public static string SessionEnded(string handshakeId, string reason, string? requestId = null)
        => Write(writer =>
        {
            writer.WriteNumber("v", HostSessionProtocol.Version);
            writer.WriteString("type", HostSessionProtocol.Types.HostSessionEnded);
            writer.WriteString("handshakeId", handshakeId);
            writer.WriteString("reason", reason);
            if (!string.IsNullOrEmpty(requestId))
            {
                writer.WriteString("requestId", requestId);
            }
        });

    private static string Write(Action<Utf8JsonWriter> build)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            build(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
