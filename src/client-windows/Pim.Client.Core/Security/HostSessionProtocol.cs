using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pim.Client.Core.Security;

/// <summary>
/// 审计 F-11 · 宿主会话 wire（唯一规格：<c>remediation/host-session/reviewed-spec.md</c>）。
///
/// 只有下面六种消息类型；旧的五消息草稿（<c>page.ack</c> / <c>page.unauthorized</c> /
/// <c>host.error</c> / <c>session.*</c>）已被本规格取代，不再存在：
///   页 → 宿主：<c>page.ready</c>、<c>page.refresh</c>、<c>page.logout</c>
///   宿主 → 页：<c>host.state</c>、<c>host.refresh.result</c>、<c>host.session.ended</c>
///
/// 解析是 fail-closed 的：非 JSON 对象、版本不是整数 1、未知类型、缺必填、
/// **多一个允许集合外的字段**、字段类型/取值不合法、超过 4096 字节，一律丢弃且不回复。
/// 非法帧不产生错误帧。
///
/// 页 → 宿主的帧是 **JSON 字符串**：Windows 侧用 <c>TryGetWebMessageAsString</c> 取回字符串后
/// 交给 <see cref="TryParseText"/>（严格 UTF-8 重编码），宿主 → 页用 <c>PostWebMessageAsJson</c> 发对象。
///
/// 平台边界：本类不依赖 WebView2，Linux 可跑；真实的
/// <c>WebMessageReceived</c> / <c>e.Source</c> / 导航行为仍保持 NOT-VERIFIED（N-03）。
/// </summary>
public static class HostSessionProtocol
{
    /// <summary>协议版本；必须是整数 <c>1</c>。</summary>
    public const int Version = 1;

    /// <summary>一帧的最大字节数；超过即丢弃。</summary>
    public const int MaxFrameBytes = 4096;

    /// <summary>宿主等待合法 <c>host.state</c> 回复的时限（规格 §1）。</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>页面等待 <c>host.refresh.result</c> 的时限（规格 §6.2）。</summary>
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(8);

    /// <summary>页面等待 <c>host.session.ended</c>（logout 回复）的时限（规格 §6.2）。</summary>
    public static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(2);

    /// <summary>宿主通道标记里的产品常量（与 <see cref="HostChannelMarker"/> 同源）。</summary>
    public const string Product = HostChannelMarker.Product;

    public static class Types
    {
        public const string PageReady = "page.ready";
        public const string PageRefresh = "page.refresh";
        public const string PageLogout = "page.logout";
        public const string HostState = "host.state";
        public const string HostRefreshResult = "host.refresh.result";
        public const string HostSessionEnded = "host.session.ended";
    }

    public static IReadOnlySet<string> KnownTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Types.PageReady,
        Types.PageRefresh,
        Types.PageLogout,
        Types.HostState,
        Types.HostRefreshResult,
        Types.HostSessionEnded,
    };

    /// <summary>页 → 宿主 只允许这三种。</summary>
    private static readonly IReadOnlySet<string> FromPage = new HashSet<string>(StringComparer.Ordinal)
    {
        Types.PageReady,
        Types.PageRefresh,
        Types.PageLogout,
    };

    /// <summary>宿主 → 页 只允许这三种。</summary>
    private static readonly IReadOnlySet<string> FromHost = new HashSet<string>(StringComparer.Ordinal)
    {
        Types.HostState,
        Types.HostRefreshResult,
        Types.HostSessionEnded,
    };

    /// <summary>每种消息允许出现的字段（规格 §2 的 wire 表）。</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Types.PageReady] = new[] { "v", "type", "requestId", "clientNonce" },
        [Types.PageRefresh] = new[] { "v", "type", "requestId" },
        [Types.PageLogout] = new[] { "v", "type", "requestId" },
        [Types.HostState] = new[] { "v", "type", "requestId", "clientNonce", "handshakeId", "product", "apiOrigin", "authenticated" },
        [Types.HostRefreshResult] = new[] { "v", "type", "requestId", "ok", "reason" },
        [Types.HostSessionEnded] = new[] { "v", "type", "handshakeId", "reason", "requestId" },
    };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Types.PageReady] = new[] { "requestId", "clientNonce" },
        [Types.PageRefresh] = new[] { "requestId" },
        [Types.PageLogout] = new[] { "requestId" },
        [Types.HostState] = new[] { "requestId", "clientNonce", "handshakeId", "product", "apiOrigin", "authenticated" },
        [Types.HostRefreshResult] = new[] { "requestId", "ok" },
        [Types.HostSessionEnded] = new[] { "handshakeId", "reason" },
    };

    public static IReadOnlySet<string> RefreshFailureReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RefreshReasons.Invalid, RefreshReasons.Transient, RefreshReasons.Unauthenticated,
    };

    public static IReadOnlySet<string> SessionEndedReasons { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        EndedReasons.Logout, EndedReasons.Expired,
    };

    /// <summary><c>host.refresh.result.reason</c> 的合法取值（规格 §2）。</summary>
    public static class RefreshReasons
    {
        public const string Invalid = "invalid";
        public const string Transient = "transient";
        public const string Unauthenticated = "unauthenticated";
    }

    /// <summary><c>host.session.ended.reason</c> 的合法取值（规格 §2）。</summary>
    public static class EndedReasons
    {
        public const string Logout = "logout";
        public const string Expired = "expired";
    }

    /// <summary>解析后的一帧。字段集合原样保留。</summary>
    public sealed record Message(
        string Type,
        int Version,
        IReadOnlySet<string> Fields,
        string? RequestId,
        string? ClientNonce,
        string? HandshakeId,
        string? Product,
        string? ApiOrigin,
        bool? Authenticated,
        bool? Ok,
        string? Reason)
    {
        /// <summary>
        /// 本帧里不属于该类型允许集合的字段。解析成功即恒为空集
        /// （规格要求「多一个字段就丢弃」，已由 <see cref="TryParse"/> 强制）；
        /// 保留该属性以便测试直接断言「合法帧没有规格外字段」。
        /// </summary>
        public IReadOnlySet<string> ExtraFields
            => Fields.Where(f => !AllowedFields[Type].Contains(f, StringComparer.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
    }

    public static bool IsAllowedFromPage(string type) => FromPage.Contains(type);

    public static bool IsAllowedFromHost(string type) => FromHost.Contains(type);

    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// 页 → 宿主的生产入口：<c>WebMessageReceived</c> 用 <c>TryGetWebMessageAsString</c> 取回的**字符串**
    /// 先按**严格 UTF-8** 重编码（不成对代理项等非法编码直接丢弃），再做一次与
    /// <see cref="TryParse"/> 相同的字节级校验（含 4096 字节上限）。
    /// </summary>
    public static bool TryParseText(string? text, out Message? message)
    {
        message = null;
        if (text is null) return false;

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return TryParse(bytes, out message);
    }

    /// <summary>
    /// 解析一帧。非 JSON 对象、版本不是整数 1、未知类型、缺必填字段、出现允许集合外的字段、
    /// 字段类型/取值不合法一律返回 false（fail-closed）。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out Message? message)
    {
        message = null;
        if (utf8Json.Length == 0 || utf8Json.Length > MaxFrameBytes) return false;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            // 规格：数组、字符串、非 JSON 都丢弃 —— 只有 JSON 对象算帧。
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
            {
                return false;
            }

            if (version != Version) return false;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) return false;

            var type = typeElement.GetString()!;
            if (!KnownTypes.Contains(type)) return false;

            var fields = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var required in RequiredFields[type])
            {
                if (!fields.Contains(required)) return false;
            }

            // 规格 §2：每种类型只允许列出的字段；多一个字段就丢弃并且不回复。
            // 这是白名单（不是字段名黑名单），因此 token / authorization / url 之类
            // 任何规格外字段都会被这一条挡住。
            if (fields.Any(f => !AllowedFields[type].Contains(f, StringComparer.Ordinal))) return false;

            var requestId = StringField(root, "requestId", IsValidRequestId);
            var clientNonce = StringField(root, "clientNonce", IsValidClientNonce);
            var handshakeId = StringField(root, "handshakeId", IsLowercaseUuid);
            var product = StringField(root, "product", _ => true);
            var apiOrigin = StringField(root, "apiOrigin", IsOrigin);
            var authenticated = BoolField(root, "authenticated");
            var ok = BoolField(root, "ok");
            var reason = StringField(root, "reason", _ => true);

            // 出现的字段必须真的能解析；解析不出来说明类型不对。
            if (fields.Contains("requestId") && requestId is null) return false;
            if (fields.Contains("clientNonce") && clientNonce is null) return false;
            if (fields.Contains("handshakeId") && handshakeId is null) return false;
            if (fields.Contains("authenticated") && authenticated is null) return false;
            if (fields.Contains("ok") && ok is null) return false;
            if (fields.Contains("apiOrigin") && apiOrigin is null) return false;

            if (type == Types.HostState && !string.Equals(product, Product, StringComparison.Ordinal)) return false;

            if (type == Types.HostRefreshResult)
            {
                if (reason is not null && !RefreshFailureReasons.Contains(reason)) return false;
            }

            if (type == Types.HostSessionEnded)
            {
                if (reason is null || !SessionEndedReasons.Contains(reason)) return false;
            }

            message = new Message(type, version, fields, requestId, clientNonce, handshakeId, product, apiOrigin, authenticated, ok, reason);
            return true;
        }
    }

    private static string? StringField(JsonElement root, string name, Func<string, bool> validate)
    {
        if (!root.TryGetProperty(name, out var element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;
        var value = element.GetString();
        return value is not null && validate(value) ? value : null;
    }

    private static bool? BoolField(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) return null;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>1..64 个 <c>[A-Za-z0-9_-]</c>。</summary>
    public static bool IsValidRequestId(string value)
        => value.Length is >= 1 and <= 64 && value.All(IsIdChar);

    /// <summary>8..64 个 <c>[A-Za-z0-9_-]</c>。</summary>
    public static bool IsValidClientNonce(string value)
        => value.Length is >= 8 and <= 64 && value.All(IsIdChar);

    private static bool IsIdChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

    /// <summary>小写 UUID 字符串（规格要求 <c>handshakeId</c> 为小写 UUID）。</summary>
    public static bool IsLowercaseUuid(string value)
        => Guid.TryParseExact(value, "D", out _) && value == value.ToLowerInvariant();

    /// <summary>
    /// <c>apiOrigin</c> 必须是文档 origin：<c>scheme://host[:port]</c>，
    /// 默认端口不写；比较用同一套 origin 规则，而不是子串比较。
    /// </summary>
    public static bool IsOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;
        if (uri.UserInfo.Length > 0) return false;

        // 不能带路径/查询/片段：apiOrigin 只表示 origin。
        var hostPart = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{uri.IdnHost}]"
            : uri.IdnHost;
        var expected = uri.IsDefaultPort
            ? $"{uri.Scheme}://{hostPart}"
            : $"{uri.Scheme}://{hostPart}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
        return string.Equals(expected, value, StringComparison.OrdinalIgnoreCase);
    }
}
