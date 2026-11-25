using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Pim.Client.Core.Services;

/// <summary>服务器地址被拒绝的原因。UI 用它区分「格式不对」与「远程明文 HTTP」。</summary>
public enum ServerUrlRejection
{
    None = 0,
    Empty,
    Malformed,
    UnsupportedScheme,
    UserInfo,
    Query,
    Fragment,
    AmbiguousPath,
    RemoteCleartextHttp,
}

/// <summary>服务器地址校验结果。消息为简体中文，可直接展示给用户。</summary>
public readonly record struct ServerUrlValidation(
    bool IsValid,
    string NormalizedUrl,
    bool IsLoopback,
    bool IsHttps,
    ServerUrlRejection Rejection,
    string MessageZh);

/// <summary>
/// 服务器地址的唯一安全策略。
///
/// <para>
/// 远程地址只允许 HTTPS；只有本机回环（127.0.0.0/8、::1、localhost）允许 HTTP，
/// 以保留本地开发默认地址。地址中不允许 userinfo、query、fragment，也不允许
/// 编码分隔符/点段这类会让「同一个主机」解析出不同部署路径的写法。
/// </para>
///
/// <para>
/// 不写协议时：本机回环补 <c>http://</c>（旧本地地址兼容），其它主机补
/// <c>https://</c>（不再把远程地址静默降级为明文）。
/// </para>
/// </summary>
public static class ServerUrlPolicy
{
    public const string LocalDevelopmentUrl = "http://127.0.0.1:5858";

    public const string RemoteCleartextHttpMessage =
        "远程服务器地址不能使用明文 HTTP：登录密码、笔记正文和上传文件都可能被窃听或篡改。"
        + "请改用 https:// 地址；只有本机回环地址（127.0.0.1、::1、localhost）允许 HTTP。";

    private const string EmptyMessage = "请填写服务器地址。";
    private const string MalformedMessage =
        "服务器地址格式无效，请填写例如 https://pim.example.com 或 http://127.0.0.1:5858。";
    private const string UnsupportedSchemeMessage = "服务器地址只支持 http:// 或 https://。";
    private const string UserInfoMessage = "服务器地址不能包含用户名或密码。";
    private const string QueryMessage = "服务器地址不能包含查询参数。";
    private const string FragmentMessage = "服务器地址不能包含片段。";
    private const string AmbiguousPathMessage = "服务器地址路径包含不安全的转义或目录片段。";

    public static ServerUrlValidation Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Invalid(ServerUrlRejection.Empty, EmptyMessage);
        }

        var input = value.Trim();
        if (input.IndexOf('\\') >= 0
            || input.Any(char.IsControl)
            || input.Any(char.IsWhiteSpace))
        {
            return Invalid(ServerUrlRejection.Malformed, MalformedMessage);
        }

        var schemeSeparator = input.IndexOf("://", StringComparison.Ordinal);
        string candidate;
        if (schemeSeparator >= 0)
        {
            var scheme = input[..schemeSeparator];
            if (!scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(ServerUrlRejection.UnsupportedScheme, UnsupportedSchemeMessage);
            }

            candidate = input;
        }
        else
        {
            if (input.StartsWith('/')
                || input.StartsWith(Uri.UriSchemeHttp + ":", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith(Uri.UriSchemeHttps + ":", StringComparison.OrdinalIgnoreCase))
            {
                return Invalid(ServerUrlRejection.Malformed, MalformedMessage);
            }

            var authority = input.Split('/', 2)[0];
            var host = ExtractHost(authority);
            if (host is null)
            {
                return Invalid(ServerUrlRejection.Malformed, MalformedMessage);
            }

            candidate = (IsLoopbackHost(host) ? "http://" : "https://") + input;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return Invalid(ServerUrlRejection.Malformed, MalformedMessage);
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(ServerUrlRejection.UnsupportedScheme, UnsupportedSchemeMessage);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Invalid(ServerUrlRejection.UserInfo, UserInfoMessage);
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            return Invalid(ServerUrlRejection.Query, QueryMessage);
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return Invalid(ServerUrlRejection.Fragment, FragmentMessage);
        }

        if (uri.Port is <= 0 or > 65535)
        {
            return Invalid(ServerUrlRejection.Malformed, MalformedMessage);
        }

        if (!IsSafePath(uri.AbsolutePath))
        {
            return Invalid(ServerUrlRejection.AmbiguousPath, AmbiguousPathMessage);
        }

        var isLoopback = IsLoopbackHost(uri.Host);
        var isHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isLoopback)
        {
            return Invalid(ServerUrlRejection.RemoteCleartextHttp, RemoteCleartextHttpMessage);
        }

        return new ServerUrlValidation(
            IsValid: true,
            NormalizedUrl: BuildNormalizedUrl(uri),
            IsLoopback: isLoopback,
            IsHttps: isHttps,
            Rejection: ServerUrlRejection.None,
            MessageZh: string.Empty);
    }

    public static bool TryNormalize(string? value, out string normalizedUrl, out string error)
    {
        var result = Validate(value);
        if (result.IsValid)
        {
            normalizedUrl = result.NormalizedUrl;
            error = string.Empty;
            return true;
        }

        normalizedUrl = string.Empty;
        error = result.MessageZh;
        return false;
    }

    public static string Normalize(string? value)
    {
        var result = Validate(value);
        if (!result.IsValid)
        {
            throw new ArgumentException(result.MessageZh, nameof(value));
        }

        return result.NormalizedUrl;
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var candidate = host.Trim();
        if (candidate.Length >= 2
            && candidate[0] == '['
            && candidate[^1] == ']')
        {
            candidate = candidate[1..^1];
        }

        return candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(candidate, out var address)
                   && IPAddress.IsLoopback(address));
    }

    private static ServerUrlValidation Invalid(ServerUrlRejection rejection, string message) =>
        new(
            IsValid: false,
            NormalizedUrl: string.Empty,
            IsLoopback: false,
            IsHttps: false,
            Rejection: rejection,
            MessageZh: message);

    private static string? ExtractHost(string authority)
    {
        if (authority.Length == 0)
        {
            return null;
        }

        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            return close <= 1 ? null : authority[1..close];
        }

        var colon = authority.IndexOf(':');
        var host = colon >= 0 ? authority[..colon] : authority;
        return host.Length == 0 ? null : host;
    }

    private static bool IsSafePath(string absolutePath)
    {
        var current = absolutePath;
        for (var pass = 0; pass < 4; pass++)
        {
            if (HasUnsafeCharacters(current))
            {
                return false;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(current);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                break;
            }

            if (pass == 3)
            {
                return false;
            }

            current = decoded;
        }

        if (HasUnsafeCharacters(current)
            || current.IndexOf('?') >= 0
            || current.IndexOf('#') >= 0
            || current.Count(character => character == '/')
                != absolutePath.Count(character => character == '/'))
        {
            return false;
        }

        return current
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }

    private static bool HasUnsafeCharacters(string value) =>
        value.IndexOf('\\') >= 0
        || value.IndexOf('\0') >= 0
        || value.Any(char.IsControl);

    private static string BuildNormalizedUrl(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = "127.0.0.1";
        }

        var hostText = IPAddress.TryParse(host, out var address)
                       && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

        var port = uri.IsDefaultPort
            ? string.Empty
            : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/api/v1".Length].TrimEnd('/');
        }

        return $"{uri.Scheme}://{hostText}{port}{path}";
    }
}
