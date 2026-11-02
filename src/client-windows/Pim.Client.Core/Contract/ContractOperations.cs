// ---------------------------------------------------------------------------
// 本文件由工具生成，请勿手工修改 / GENERATED FILE — DO NOT EDIT BY HAND.
//
//   契约快照      : contract/openapi.json (sha256 a6ac67bfcebad14e3c5d5e67e09f26883dc54e869786524a31b7bd1422287247)
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
public sealed record ContractHeader(string Name, string Kind);
public sealed record ContractQueryParameter(string Name, string Kind, bool Required, System.Collections.Generic.IReadOnlyList<string>? AllowedValues = null);

public sealed class ContractOperation
{
    private ContractOperation(
        string httpMethod,
        string pathTemplate,
        string relativePath,
        System.Collections.Generic.IReadOnlyList<ContractHeader> routeParameterKinds,
        System.Collections.Generic.IReadOnlyList<ContractQueryParameter> queryParameters,
        System.Collections.Generic.IReadOnlyList<ContractHeader> requestHeaders,
        string? requestMediaType,
        string responseMediaType,
        System.Collections.Generic.IReadOnlyList<ContractHeader> responseHeaders)
    {
        HttpMethod = httpMethod;
        PathTemplate = pathTemplate;
        RelativePath = relativePath;
        RouteParameters = ParseRouteParameters(pathTemplate);
        RouteParameterKinds = routeParameterKinds;
        QueryParameters = queryParameters;
        RequestHeaders = requestHeaders;
        RequestMediaType = requestMediaType;
        ResponseMediaType = responseMediaType;
        ResponseHeaders = responseHeaders;
    }

    /// <summary>HTTP 方法（契约里的方法，全大写）。</summary>
    public string HttpMethod { get; }

    /// <summary>契约里的完整路径（含 <c>/api/v1</c> 前缀）。</summary>
    public string PathTemplate { get; }

    /// <summary>相对 <c>/api/v1/</c> 基址的路径（实际请求用的就是它）。</summary>
    public string RelativePath { get; }

    /// <summary>路径模板里的占位符（按出现顺序）。</summary>
    public System.Collections.Generic.IReadOnlyList<string> RouteParameters { get; }

    /// <summary>路由参数的类型校验规则（uuid / int32 / int64 / plain-string）。</summary>
    public System.Collections.Generic.IReadOnlyList<ContractHeader> RouteParameterKinds { get; }

    /// <summary>契约声明的标量 query 参数，按契约顺序序列化。</summary>
    public System.Collections.Generic.IReadOnlyList<ContractQueryParameter> QueryParameters { get; }

    /// <summary>契约要求的必需请求 header。</summary>
    public System.Collections.Generic.IReadOnlyList<ContractHeader> RequestHeaders { get; }

    /// <summary>契约声明的请求体媒体类型；无请求体时为 null。</summary>
    public string? RequestMediaType { get; }

    /// <summary>2xx 响应的媒体类型；非 JSON（如 audio/wav）由客户端按流处理。</summary>
    public string ResponseMediaType { get; }

    /// <summary>契约声明的响应 header（用于下载流的身份/完整性绑定）。</summary>
    public System.Collections.Generic.IReadOnlyList<ContractHeader> ResponseHeaders { get; }

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
        var kinds = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var kind in RouteParameterKinds)
        {
            kinds[kind.Name] = kind.Kind;
        }

        foreach (var parameter in RouteParameters)
        {
            if (!supplied.TryGetValue(parameter, out var value))
            {
                throw new System.ArgumentException(
                    $"缺少路由值 {parameter}（契约路径 {PathTemplate}）", nameof(routeValues));
            }

            switch (kinds.TryGetValue(parameter, out var kind) ? kind : "plain-string")
            {
                case "uuid":
                    if (!System.Guid.TryParse(value, out _))
                    {
                        throw new System.ArgumentException($"路由值 {parameter} 不是 UUID", nameof(routeValues));
                    }

                    break;
                case "int32":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new System.ArgumentException($"路由值 {parameter} 不是 int32", nameof(routeValues));
                    }

                    break;
                case "int64":
                    if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new System.ArgumentException($"路由值 {parameter} 不是 int64", nameof(routeValues));
                    }

                    break;
                case "date-time":
                    if (!System.DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
                    {
                        throw new System.ArgumentException($"路由值 {parameter} 不是 ISO date-time", nameof(routeValues));
                    }

                    break;
                case "boolean":
                    if (!bool.TryParse(value, out _))
                    {
                        throw new System.ArgumentException($"路由值 {parameter} 不是 boolean", nameof(routeValues));
                    }

                    break;
            }

            resolved = resolved.Replace("{" + parameter + "}", System.Uri.EscapeDataString(value), System.StringComparison.Ordinal);
        }

        return resolved;
    }

    /// <summary>
    /// 按契约声明的顺序构造 query string（含前导 <c>?</c>，无参数时返回空串）。
    /// 名字必须与契约完全一致；required 缺席、重复、未知名字或类型不符都会抛异常。
    /// </summary>
    public string ResolveQuery(params (string Name, string? Value)[] queryValues)
    {
        var supplied = new System.Collections.Generic.Dictionary<string, string?>(System.StringComparer.Ordinal);
        foreach (var (name, value) in queryValues ?? System.Array.Empty<(string, string?)>())
        {
            if (!supplied.TryAdd(name, value))
            {
                throw new System.ArgumentException($"Query 值重复：{name}", nameof(queryValues));
            }
        }

        foreach (var name in supplied.Keys)
        {
            if (!QueryParameters.Any(parameter => parameter.Name == name))
            {
                throw new System.ArgumentException($"契约未声明 Query 参数 {name}（{PathTemplate}）", nameof(queryValues));
            }
        }

        var parts = new System.Collections.Generic.List<string>();
        foreach (var parameter in QueryParameters)
        {
            if (!supplied.TryGetValue(parameter.Name, out var value))
            {
                if (parameter.Required)
                {
                    throw new System.ArgumentException(
                        $"缺少必需 Query 参数 {parameter.Name}（契约路径 {PathTemplate}）", nameof(queryValues));
                }

                continue;
            }

            if (value is null)
            {
                if (parameter.Required)
                {
                    throw new System.ArgumentException(
                        $"必需 Query 参数 {parameter.Name} 不能为空（契约路径 {PathTemplate}）", nameof(queryValues));
                }

                continue;
            }

            switch (parameter.Kind)
            {
                case "uuid":
                    if (!System.Guid.TryParse(value, out _))
                    {
                        throw new System.ArgumentException($"Query 参数 {parameter.Name} 不是 UUID", nameof(queryValues));
                    }

                    break;
                case "int32":
                    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new System.ArgumentException($"Query 参数 {parameter.Name} 不是 int32", nameof(queryValues));
                    }

                    break;
                case "int64":
                    if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new System.ArgumentException($"Query 参数 {parameter.Name} 不是 int64", nameof(queryValues));
                    }

                    break;
                case "date-time":
                    if (!System.DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
                    {
                        throw new System.ArgumentException($"Query 参数 {parameter.Name} 不是 ISO date-time", nameof(queryValues));
                    }

                    break;
                case "boolean":
                    if (!bool.TryParse(value, out _))
                    {
                        throw new System.ArgumentException($"Query 参数 {parameter.Name} 不是 boolean", nameof(queryValues));
                    }

                    break;
                case "enum":
                    if (parameter.AllowedValues is null || !parameter.AllowedValues.Contains(value, System.StringComparer.Ordinal))
                    {
                        throw new System.ArgumentException($"Query 参数 {parameter.Name} 不是契约声明的枚举值", nameof(queryValues));
                    }

                    break;
            }

            parts.Add(
                System.Uri.EscapeDataString(parameter.Name) + "=" + System.Uri.EscapeDataString(value));
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
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
        new("POST", "/api/v1/auth/login", "auth/login",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation Register =
        new("POST", "/api/v1/auth/register", "auth/register",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation RefreshToken =
        new("POST", "/api/v1/auth/refresh", "auth/refresh",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation ReportDaemonHeartbeat =
        new("POST", "/api/v1/daemon/heartbeat", "daemon/heartbeat",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation ReportPlannedOffline =
        new("POST", "/api/v1/daemon/planned-offline", "daemon/planned-offline",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetEndpointStatuses =
        new("GET", "/api/v1/endpoints", "endpoints",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetEndpointCollectionQuality =
        new("GET", "/api/v1/endpoints/{deviceId}/collection-quality", "endpoints/{deviceId}/collection-quality",
            new ContractHeader[] { new("deviceId", "plain-string") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetCalendars =
        new("GET", "/api/v1/calendar/calendars", "calendar/calendars",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("kind", "plain-string", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetCalendarEvents =
        new("GET", "/api/v1/calendar/events", "calendar/events",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("start", "date-time", false), new("end", "date-time", false), new("search", "plain-string", false), new("calendarId", "uuid", false), new("page", "int32", false), new("pageSize", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation CreateCalendarEvent =
        new("POST", "/api/v1/calendar/events", "calendar/events",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetTaskBooks =
        new("GET", "/api/v1/calendar/task-books", "calendar/task-books",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetTasks =
        new("GET", "/api/v1/calendar/tasks", "calendar/tasks",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("inbox", "boolean", false), new("search", "plain-string", false), new("calendarId", "uuid", false), new("status", "plain-string", false), new("priority", "int32", false), new("plannedFrom", "date-time", false), new("plannedTo", "date-time", false), new("dueFrom", "date-time", false), new("dueTo", "date-time", false), new("page", "int32", false), new("pageSize", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation CreateTask =
        new("POST", "/api/v1/calendar/tasks", "calendar/tasks",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UpdateTask =
        new("PUT", "/api/v1/calendar/tasks/{id}", "calendar/tasks/{id}",
            new ContractHeader[] { new("id", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation ReportEndpointHeartbeat =
        new("POST", "/api/v1/endpoints/{deviceId}/heartbeat", "endpoints/{deviceId}/heartbeat",
            new ContractHeader[] { new("deviceId", "plain-string") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation SendEndpointNotificationAction =
        new("POST", "/api/v1/endpoints/{deviceId}/notification-actions", "endpoints/{deviceId}/notification-actions",
            new ContractHeader[] { new("deviceId", "plain-string") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UploadTrackerEvents =
        new("POST", "/api/v1/pc/tracker/upload", "pc/tracker/upload",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetPcDetail =
        new("GET", "/api/v1/pc/detail", "pc/detail",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("dateFrom", "plain-string", false), new("dateTo", "plain-string", false), new("dimension", "plain-string", false), new("deviceId", "plain-string", false), new("appName", "plain-string", false), new("categoryName", "plain-string", false), new("keyName", "plain-string", false), new("eventType", "plain-string", false), new("sortBy", "plain-string", false), new("sortDir", "plain-string", false), new("domain", "plain-string", false), new("title", "plain-string", false), new("url", "plain-string", false), new("view", "plain-string", false), new("page", "int32", false), new("pageSize", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UploadSiteEvents =
        new("POST", "/api/v1/pc/browser-tt/upload", "pc/browser-tt/upload",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation ReportTrackerHealth =
        new("POST", "/api/v1/pc/tracker/health", "pc/tracker/health",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UploadKeystatsSample =
        new("POST", "/api/v1/pc/keystats/samples", "pc/keystats/samples",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UploadKeystatsSnapshot =
        new("POST", "/api/v1/pc/keystats/upload", "pc/keystats/upload",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetClientShellLatest =
        new("GET", "/api/client/shell/latest", "../client/shell/latest",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation RegisterNativeAsrNode =
        new("POST", "/api/v1/native-notes/asr-routing/nodes/register", "native-notes/asr-routing/nodes/register",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation HeartbeatNativeAsrNode =
        new("POST", "/api/v1/native-notes/asr-routing/nodes/{nodeId}/heartbeat", "native-notes/asr-routing/nodes/{nodeId}/heartbeat",
            new ContractHeader[] { new("nodeId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation WithdrawNativeAsrNode =
        new("POST", "/api/v1/native-notes/asr-routing/nodes/{nodeId}/withdraw", "native-notes/asr-routing/nodes/{nodeId}/withdraw",
            new ContractHeader[] { new("nodeId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation ClaimNativeAsrLease =
        new("POST", "/api/v1/native-notes/asr-routing/leases/claim", "native-notes/asr-routing/leases/claim",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation RenewNativeAsrLease =
        new("POST", "/api/v1/native-notes/asr-routing/leases/{leaseId}/renew", "native-notes/asr-routing/leases/{leaseId}/renew",
            new ContractHeader[] { new("leaseId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNativeAsrSegmentContent =
        new("GET", "/api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content", "native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content",
            new ContractHeader[] { new("leaseId", "uuid"), new("segmentIndex", "int32") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] { new("X-Pim-Lease-Token", "uuid"), new("X-Pim-Fencing-Token", "int64") },
            null,
            "audio/wav",
            new ContractHeader[] { new("X-Content-SHA256", "plain-string"), new("X-Pim-Attempt-Id", "plain-string"), new("X-Pim-Fencing-Token", "int64"), new("X-Pim-Lease-Id", "plain-string"), new("X-Pim-Node-Id", "plain-string"), new("X-Pim-Owner-Id", "plain-string"), new("X-Pim-Samples", "int64"), new("X-Pim-Segment-Index", "int32"), new("X-Pim-Source-File-Index", "int32"), new("X-Pim-Source-Offset-Samples", "int64"), new("X-Pim-Source-SHA256", "plain-string"), new("X-Pim-Version-Id", "plain-string") });
    public static readonly ContractOperation AckNativeAsrAttempt =
        new("POST", "/api/v1/native-notes/asr-routing/leases/{attemptId}/ack", "native-notes/asr-routing/leases/{attemptId}/ack",
            new ContractHeader[] { new("attemptId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation BeginNoteMaterialUpload =
        new("POST", "/api/v1/native-notes/versions", "native-notes/versions",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteMaterialVersions =
        new("GET", "/api/v1/native-notes/{noteId}/versions", "native-notes/{noteId}/versions",
            new ContractHeader[] { new("noteId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteMaterialVersion =
        new("GET", "/api/v1/native-notes/versions/{versionId}", "native-notes/versions/{versionId}",
            new ContractHeader[] { new("versionId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation CompleteNoteMaterialUpload =
        new("POST", "/api/v1/native-notes/versions/{versionId}/complete", "native-notes/versions/{versionId}/complete",
            new ContractHeader[] { new("versionId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UploadNoteMaterialPart =
        new("PUT", "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}", "native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}",
            new ContractHeader[] { new("versionId", "uuid"), new("fileIndex", "int32"), new("partIndex", "int32") },
            new ContractQueryParameter[] { new("sha256", "plain-string", true) },
            new ContractHeader[] {  },
            "application/octet-stream",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation DownloadNoteMaterialFile =
        new("GET", "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}", "native-notes/versions/{versionId}/files/{fileIndex}",
            new ContractHeader[] { new("versionId", "uuid"), new("fileIndex", "int32") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/octet-stream",
            new ContractHeader[] { new("Cache-Control", "plain-string"), new("Content-Disposition", "plain-string"), new("X-Content-SHA256", "sha256"), new("X-Content-Type-Options", "plain-string") });
    public static readonly ContractOperation DeleteNoteMaterialPart =
        new("DELETE", "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}", "native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}",
            new ContractHeader[] { new("versionId", "uuid"), new("fileIndex", "int32"), new("partIndex", "int32") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation PromoteNoteMaterialVersion =
        new("POST", "/api/v1/native-notes/versions/{versionId}/promote", "native-notes/versions/{versionId}/promote",
            new ContractHeader[] { new("versionId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteAsrSources =
        new("GET", "/api/v1/native-notes/asr-tasks/sources", "native-notes/asr-tasks/sources",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("offset", "int32", false), new("limit", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation StartNoteAsrTask =
        new("POST", "/api/v1/native-notes/asr-tasks", "native-notes/asr-tasks",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteAsrTasks =
        new("GET", "/api/v1/native-notes/asr-tasks", "native-notes/asr-tasks",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteAsrTask =
        new("GET", "/api/v1/native-notes/asr-tasks/{taskId}", "native-notes/asr-tasks/{taskId}",
            new ContractHeader[] { new("taskId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteAsrTaskSegments =
        new("GET", "/api/v1/native-notes/asr-tasks/{taskId}/segments", "native-notes/asr-tasks/{taskId}/segments",
            new ContractHeader[] { new("taskId", "uuid") },
            new ContractQueryParameter[] { new("offset", "int32", false), new("limit", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedSources =
        new("GET", "/api/v1/native-notes/derived-tasks/sources", "native-notes/derived-tasks/sources",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("offset", "int32", false), new("limit", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedTasks =
        new("GET", "/api/v1/native-notes/derived-tasks", "native-notes/derived-tasks",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("noteId", "uuid", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation StartNoteDerivedTask =
        new("POST", "/api/v1/native-notes/derived-tasks", "native-notes/derived-tasks",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedTask =
        new("GET", "/api/v1/native-notes/derived-tasks/{taskId}", "native-notes/derived-tasks/{taskId}",
            new ContractHeader[] { new("taskId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedTaskSteps =
        new("GET", "/api/v1/native-notes/derived-tasks/{taskId}/steps", "native-notes/derived-tasks/{taskId}/steps",
            new ContractHeader[] { new("taskId", "uuid") },
            new ContractQueryParameter[] { new("offset", "int32", false), new("limit", "int32", false) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation RetryNoteDerivedTask =
        new("POST", "/api/v1/native-notes/derived-tasks/{taskId}/retry", "native-notes/derived-tasks/{taskId}/retry",
            new ContractHeader[] { new("taskId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation StopNoteDerivedTask =
        new("POST", "/api/v1/native-notes/derived-tasks/{taskId}/stop", "native-notes/derived-tasks/{taskId}/stop",
            new ContractHeader[] { new("taskId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedArtifacts =
        new("GET", "/api/v1/native-notes/derived-artifacts", "native-notes/derived-artifacts",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("noteId", "uuid", true) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedArtifact =
        new("GET", "/api/v1/native-notes/derived-artifacts/{artifactId}", "native-notes/derived-artifacts/{artifactId}",
            new ContractHeader[] { new("artifactId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedArtifactVersion =
        new("GET", "/api/v1/native-notes/derived-artifacts/{artifactId}/versions/{versionId}", "native-notes/derived-artifacts/{artifactId}/versions/{versionId}",
            new ContractHeader[] { new("artifactId", "uuid"), new("versionId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation SaveNoteDerivedManualRevision =
        new("POST", "/api/v1/native-notes/derived-artifacts/{artifactId}/manual-revisions", "native-notes/derived-artifacts/{artifactId}/manual-revisions",
            new ContractHeader[] { new("artifactId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation SelectNoteDerivedArtifactVersion =
        new("POST", "/api/v1/native-notes/derived-artifacts/{artifactId}/select", "native-notes/derived-artifacts/{artifactId}/select",
            new ContractHeader[] { new("artifactId", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteDerivedDestinations =
        new("GET", "/api/v1/native-notes/derived-tasks/destinations", "native-notes/derived-tasks/destinations",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation StartNoteTaskSuggestionExtraction =
        new("POST", "/api/v1/native-notes/task-suggestions/extractions", "native-notes/task-suggestions/extractions",
            new ContractHeader[] {  },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteTaskSuggestionExtraction =
        new("GET", "/api/v1/native-notes/task-suggestions/extractions/{id}", "native-notes/task-suggestions/extractions/{id}",
            new ContractHeader[] { new("id", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteTaskSuggestions =
        new("GET", "/api/v1/native-notes/task-suggestions", "native-notes/task-suggestions",
            new ContractHeader[] {  },
            new ContractQueryParameter[] { new("noteId", "uuid", false), new("status", "enum", false, new string[] { "Draft", "Confirmed", "Dismissed" }) },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation GetNoteTaskSuggestion =
        new("GET", "/api/v1/native-notes/task-suggestions/{id}", "native-notes/task-suggestions/{id}",
            new ContractHeader[] { new("id", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            null,
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation UpdateNoteTaskSuggestion =
        new("PUT", "/api/v1/native-notes/task-suggestions/{id}", "native-notes/task-suggestions/{id}",
            new ContractHeader[] { new("id", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation ConfirmNoteTaskSuggestion =
        new("POST", "/api/v1/native-notes/task-suggestions/{id}/confirm", "native-notes/task-suggestions/{id}/confirm",
            new ContractHeader[] { new("id", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
    public static readonly ContractOperation DismissNoteTaskSuggestion =
        new("POST", "/api/v1/native-notes/task-suggestions/{id}/dismiss", "native-notes/task-suggestions/{id}/dismiss",
            new ContractHeader[] { new("id", "uuid") },
            new ContractQueryParameter[] {  },
            new ContractHeader[] {  },
            "application/json",
            "application/json",
            new ContractHeader[] {  });
}
