// ---------------------------------------------------------------------------
// 本文件由工具生成，请勿手工修改 / GENERATED FILE — DO NOT EDIT BY HAND.
//
//   契约快照      : contract/openapi.json (sha256 840b6ee622966e09026637f3c9058f61f93881bfd5488dc1b3b048287472cce7)
//   operation 清单: scripts/contract/client-operations.json
//   生成器        : scripts/contract/generate-csharp.py
//   重新生成      : bash scripts/ci/generate-contract-csharp.sh
//   一键刷新      : bash scripts/ci/refresh-contract.sh（快照 + 生成物同批更新）
//   一致性门禁    : bash scripts/ci/check-contract-csharp.sh（CI 逐字节比对，改了就红）
//
// 只覆盖 operation 清单声明的 operation 的 schema 闭包；需要新接口时先让后端在
// pim-api 实现并刷新契约，再往清单里加一条 —— 不要在客户端造类型。
// ---------------------------------------------------------------------------
namespace Pim.Client.Core.Contract;

/// <summary>
/// 契约 operation 定义：客户端允许调用的全部接口。
///
/// 路径只能从这里取 —— 构造函数是私有的，因此**无法在别处编造一个路径**；
/// 而本文件由 <c>scripts/contract/generate-csharp.py</c> 从契约快照生成、
/// 并由 <c>scripts/ci/check-contract-csharp.sh</c> 在 CI 里逐字节复核。
/// 需要契约里没有的接口时：不要在这里加，按 AGENTS.md「接口契约与门禁」把需求交给后端。
/// </summary>
public sealed class ContractOperation
{
    private ContractOperation(string httpMethod, string pathTemplate, string relativePath)
    {
        HttpMethod = httpMethod;
        PathTemplate = pathTemplate;
        RelativePath = relativePath;
        RouteParameters = ParseRouteParameters(pathTemplate);
    }

    /// <summary>HTTP 方法（契约里的方法，全大写）。</summary>
    public string HttpMethod { get; }

    /// <summary>契约里的完整路径（含 <c>/api/v1</c> 前缀）。</summary>
    public string PathTemplate { get; }

    /// <summary>相对 <c>/api/v1/</c> 基址的路径（实际请求用的就是它）。</summary>
    public string RelativePath { get; }

    /// <summary>路径模板里的占位符（按出现顺序）。</summary>
    public System.Collections.Generic.IReadOnlyList<string> RouteParameters { get; }

    /// <summary>
    /// 用路由值填充路径模板。
    /// 占位符与传入的名字必须**完全一致**（多传、少传、写错名字都会抛异常），
    /// 值按 RFC 3986 做转义 —— 避免把未转义的 deviceId 拼进 URL。
    /// </summary>
    public string Resolve(params (string Name, string Value)[] routeValues)
    {
        var supplied = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var (name, value) in routeValues ?? System.Array.Empty<(string, string)>())
        {
            if (!supplied.TryAdd(name, value))
            {
                throw new System.ArgumentException($"路由值重复：{name}", nameof(routeValues));
            }
        }

        if (supplied.Count != RouteParameters.Count)
        {
            throw new System.ArgumentException(
                $"路由值数量不符：契约路径 {PathTemplate} 需要 [{string.Join(", ", RouteParameters)}]，" +
                $"收到 [{string.Join(", ", supplied.Keys)}]",
                nameof(routeValues));
        }

        var resolved = RelativePath;
        foreach (var parameter in RouteParameters)
        {
            if (!supplied.TryGetValue(parameter, out var value))
            {
                throw new System.ArgumentException(
                    $"缺少路由值 {parameter}（契约路径 {PathTemplate}）", nameof(routeValues));
            }

            resolved = resolved.Replace("{" + parameter + "}", System.Uri.EscapeDataString(value), System.StringComparison.Ordinal);
        }

        return resolved;
    }

    private static System.Collections.Generic.IReadOnlyList<string> ParseRouteParameters(string pathTemplate)
    {
        var parameters = new System.Collections.Generic.List<string>();
        var index = 0;
        while (true)
        {
            var start = pathTemplate.IndexOf('{', index);
            if (start < 0)
            {
                break;
            }

            var end = pathTemplate.IndexOf('}', start + 1);
            if (end < 0)
            {
                throw new System.ArgumentException($"契约路径模板的占位符没有闭合：{pathTemplate}", nameof(pathTemplate));
            }

            parameters.Add(pathTemplate[(start + 1)..end]);
            index = end + 1;
        }

        return parameters;
    }

    public override string ToString() => $"{HttpMethod} {PathTemplate}";

    public static readonly ContractOperation Login =
        new("POST", "/api/v1/auth/login", "auth/login");
    public static readonly ContractOperation Register =
        new("POST", "/api/v1/auth/register", "auth/register");
    public static readonly ContractOperation RefreshToken =
        new("POST", "/api/v1/auth/refresh", "auth/refresh");
    public static readonly ContractOperation ReportDaemonHeartbeat =
        new("POST", "/api/v1/daemon/heartbeat", "daemon/heartbeat");
    public static readonly ContractOperation ReportPlannedOffline =
        new("POST", "/api/v1/daemon/planned-offline", "daemon/planned-offline");
    public static readonly ContractOperation GetEndpointStatuses =
        new("GET", "/api/v1/endpoints", "endpoints");
    public static readonly ContractOperation GetEndpointCollectionQuality =
        new("GET", "/api/v1/endpoints/{deviceId}/collection-quality", "endpoints/{deviceId}/collection-quality");
    public static readonly ContractOperation ReportEndpointHeartbeat =
        new("POST", "/api/v1/endpoints/{deviceId}/heartbeat", "endpoints/{deviceId}/heartbeat");
    public static readonly ContractOperation SendEndpointNotificationAction =
        new("POST", "/api/v1/endpoints/{deviceId}/notification-actions", "endpoints/{deviceId}/notification-actions");
    public static readonly ContractOperation UploadTrackerEvents =
        new("POST", "/api/v1/pc/tracker/upload", "pc/tracker/upload");
    public static readonly ContractOperation UploadSiteEvents =
        new("POST", "/api/v1/pc/browser-tt/upload", "pc/browser-tt/upload");
    public static readonly ContractOperation ReportTrackerHealth =
        new("POST", "/api/v1/pc/tracker/health", "pc/tracker/health");
    public static readonly ContractOperation UploadKeystatsSample =
        new("POST", "/api/v1/pc/keystats/samples", "pc/keystats/samples");
    public static readonly ContractOperation UploadKeystatsSnapshot =
        new("POST", "/api/v1/pc/keystats/upload", "pc/keystats/upload");
    public static readonly ContractOperation GetClientShellLatest =
        new("GET", "/api/client/shell/latest", "../client/shell/latest");
}
