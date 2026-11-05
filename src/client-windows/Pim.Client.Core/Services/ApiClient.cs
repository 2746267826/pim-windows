using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pim.Client.Core.Contract;

namespace Pim.Client.Core.Services;

/// <summary>
/// 与 <c>pim-api</c> 的 HTTP 边界。
///
/// <para>
/// 本类**只暴露显式方法签名**：每个方法对应一个契约里的 operation（方法、路径、请求体类型、
/// 响应类型全部来自 <see cref="ContractOperation"/> 与 <c>Pim.Client.Core.Contract</c> 里的生成类型）。
/// 裸字符串 endpoint / <c>object</c> 请求体的公共入口已经不存在 —— 那是「AI 随手编一个路径和
/// 对象塞进去、编译期完全发现不了」的来源。
/// </para>
///
/// <para>
/// 想加接口？按 <c>AGENTS.md</c>「接口契约与门禁」把需求交给后端：先让 <c>pim-api</c> 实现并更新
/// 契约，刷新本仓快照、在 <c>scripts/contract/client-operations.json</c> 里加一条、重新生成，
/// 再在这里加一个方法。<b>不要在本仓造路径、字段或类型。</b>
/// </para>
/// </summary>
public class ApiClient
{
    private readonly object _clientSync = new();
    private HttpClient _httpClient;
    private readonly Func<HttpMessageHandler> _messageHandlerFactory;
    private long _clientGeneration;
    private long _externalCredentialGeneration;
    private readonly AsyncLocal<ClientLease?> _operationLease = new();

    /// <summary>
    /// 令牌续期回调（主动到期前续期与 401 被动续期**共用**）。返回 true 表示续期成功。
    /// </summary>
    public Func<Task<bool>>? RenewTokenAsync { get; set; }

    /// <summary>
    /// 令牌是否已进入"到期前必须续期"的窗口（含已过期）。
    /// 每次发请求前先问一次：为真就先续期再发，不再依赖"先 401、再刷新、再重试"
    /// （WO-ISSUES-396-400-20261007 REQ-5 / issue #400）。
    /// </summary>
    public Func<bool>? IsTokenRenewalDue { get; set; }

    public event Action<string, long>? RequestTiming;

    /// <summary>
    /// Raised when an explicit deployment or external credential change invalidates
    /// long-running operation scopes. Natural token renewal does not raise it.
    /// </summary>
    public event Action? ExternalScopeChanged;

    /// <summary>续期串行闸门：同一时刻只允许一次真实续期，其余调用等待并复用结果。</summary>
    private readonly SemaphoreSlim _tokenRenewalGate = new(1, 1);

    /// <summary>续期请求自身发起的嵌套调用不得再触发续期（否则会无限递归）。</summary>
    private static readonly AsyncLocal<bool> InsideTokenRenewal = new();

    /// <summary>每次成功续期自增；用于判断"我这边的 401 是不是已经被别人续好了"。</summary>
    private long _tokenRenewalVersion;

    public ApiClient()
        : this(null)
    {
    }

    /// <param name="messageHandlerFactory">
    /// 传输层工厂。生产传 null（用 <see cref="HttpClientHandler"/>）；
    /// 测试注入桩 handler 以覆盖"刷新失败/401/重试"这类链路行为。
    /// 每次重建 HttpClient（<see cref="SetBaseUrl"/>）都会重新调用它。
    /// </param>
    public ApiClient(Func<HttpMessageHandler>? messageHandlerFactory)
    {
        _messageHandlerFactory = messageHandlerFactory ?? (() => new HttpClientHandler { UseProxy = false });
        _httpClient = new HttpClient(_messageHandlerFactory())
        {
            BaseAddress = new Uri($"{ClientDefaults.DefaultServerUrl}/api/v1/")
        };
    }

    public void SetBaseUrl(string baseUrl)
    {
        var normalized = NormalizeServerUrl(baseUrl);
        var uri = new Uri(normalized.TrimEnd('/') + "/api/v1/");
        var scopeChanged = false;
        lock (_clientSync)
        {
            var previous = _httpClient;

            // Build a fresh HttpClient so changing the deployment identity can
            // never redirect an already-started request or its auth retry.
            var newClient = new HttpClient(_messageHandlerFactory()) { BaseAddress = uri };

            // Credentials belong to one canonical API deployment, including its
            // PathBase. A host-only comparison would leak a token when the same
            // host serves another deployment under a different path.
            if (SameCanonicalApiRoot(previous.BaseAddress, uri)
                && previous.DefaultRequestHeaders.Authorization is { } auth)
            {
                newClient.DefaultRequestHeaders.Authorization = auth;
            }

            _httpClient = newClient;
            _clientGeneration++;
            scopeChanged = true;
            previous.Dispose();
        }

        if (scopeChanged)
        {
            ExternalScopeChanged?.Invoke();
        }
    }

    public string CurrentBaseUrl
    {
        get
        {
            lock (_clientSync)
            {
                return _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "";
            }
        }
    }

    public static string NormalizeServerUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return trimmed;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/')
            : trimmed;
    }

    public void SetAccessToken(string token)
    {
        var scopeChanged = false;
        lock (_clientSync)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            if (!InsideTokenRenewal.Value)
            {
                _externalCredentialGeneration++;
                scopeChanged = true;
            }
        }

        if (scopeChanged)
        {
            ExternalScopeChanged?.Invoke();
        }
    }

    public void ClearAccessToken()
    {
        var scopeChanged = false;
        lock (_clientSync)
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
            if (!InsideTokenRenewal.Value)
            {
                _externalCredentialGeneration++;
                scopeChanged = true;
            }
        }

        if (scopeChanged)
        {
            ExternalScopeChanged?.Invoke();
        }
    }

    // -----------------------------------------------------------------------
    // 接口边界：每个方法 = 契约里的一个 operation
    //
    // 路径来自 ContractOperations（生成物，CI 逐字节对照契约复核），请求/响应类型来自
    // Contract 命名空间下的生成类型。方法名与 operation 的对应关系登记在
    // scripts/contract/client-operations.json，tests/ClientWindows/ContractBoundaryTests.cs 会核对。
    // -----------------------------------------------------------------------

    /// <summary><c>POST /api/v1/auth/login</c>（匿名）。</summary>
    public Task<AuthResponseApiResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default)
        => SendJsonAsync<AuthResponseApiResponse>(ContractOperation.Login, request, ct);

    /// <summary><c>POST /api/v1/auth/register</c>（匿名）。</summary>
    public Task<AuthResponseApiResponse?> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
        => SendJsonAsync<AuthResponseApiResponse>(ContractOperation.Register, request, ct);

    /// <summary>
    /// <c>POST /api/v1/auth/refresh</c>（匿名）。
    /// 与其它方法一样不触发任何令牌续期：否则会出现
    /// 「续期 → 发 /auth/refresh → 令牌仍临期 → 触发续期 → 单飞复用自己 → 自等待」的死锁
    /// （WO-ISSUES-396-400-20261007 REQ-5 / issue #400）。
    /// </summary>
    public Task<AuthResponseApiResponse?> RefreshTokenAsync(RefreshRequest request, CancellationToken ct = default)
        => SendJsonAsync<AuthResponseApiResponse>(ContractOperation.RefreshToken, request, ct, allowTokenRenewal: false);

    /// <summary><c>POST /api/v1/daemon/heartbeat</c>。响应体由服务端回执，本仓不作处理。</summary>
    public Task<DaemonHeartbeatDtoApiResponse?> ReportDaemonHeartbeatAsync(DaemonHeartbeatRequest request, CancellationToken ct = default)
        => SendJsonAsync<DaemonHeartbeatDtoApiResponse>(ContractOperation.ReportDaemonHeartbeat, request, ct);

    /// <summary><c>POST /api/v1/daemon/planned-offline</c>。响应体由服务端回执，本仓不作处理。</summary>
    public Task<DaemonHeartbeatDtoApiResponse?> ReportPlannedOfflineAsync(PlannedOfflineRequest request, CancellationToken ct = default)
        => SendJsonAsync<DaemonHeartbeatDtoApiResponse>(ContractOperation.ReportPlannedOffline, request, ct);

    /// <summary><c>GET /api/v1/endpoints</c>。</summary>
    public Task<EndpointStatusDtoIReadOnlyListApiResponse?> GetEndpointStatusesAsync(CancellationToken ct = default)
        => SendAsync<EndpointStatusDtoIReadOnlyListApiResponse>(ContractOperation.GetEndpointStatuses, null, null, ct);

    /// <summary><c>GET /api/v1/endpoints/{deviceId}/collection-quality</c>。</summary>
    public Task<EndpointCollectionQualityDtoApiResponse?> GetEndpointCollectionQualityAsync(
        string deviceId,
        CancellationToken ct = default)
        => SendAsync<EndpointCollectionQualityDtoApiResponse>(
            ContractOperation.GetEndpointCollectionQuality,
            [("deviceId", deviceId)],
            null,
            ct);

    /// <summary>
    /// <c>POST /api/v1/endpoints/{deviceId}/heartbeat</c>。
    /// 端点页（<c>GET /api/v1/endpoints</c>）的唯一数据来源；<c>last_heartbeat_at</c> 由服务端按收到时刻写入，
    /// 本仓不发送时间戳 —— 客户端无法、也不应该伪造"最后活着的时间"。
    /// </summary>
    public Task<EndpointStatusDtoApiResponse?> ReportEndpointHeartbeatAsync(
        string deviceId,
        EndpointHeartbeatRequest request,
        CancellationToken ct = default)
        => SendAsync<EndpointStatusDtoApiResponse>(
            ContractOperation.ReportEndpointHeartbeat,
            [("deviceId", deviceId)],
            request,
            ct);

    /// <summary><c>POST /api/v1/endpoints/{deviceId}/notification-actions</c>。</summary>
    public Task<EndpointNotificationActionResponseApiResponse?> SendEndpointNotificationActionAsync(
        string deviceId,
        EndpointNotificationActionRequest request,
        CancellationToken ct = default)
        => SendAsync<EndpointNotificationActionResponseApiResponse>(
            ContractOperation.SendEndpointNotificationAction,
            [("deviceId", deviceId)],
            request,
            ct);

    /// <summary><c>GET /api/v1/calendar/calendars</c>。</summary>
    public Task<CalendarResponseListApiResponse?> GetCalendarsAsync(
        string? kind = null,
        CancellationToken ct = default)
        => SendAsync<CalendarResponseListApiResponse>(
            ContractOperation.GetCalendars,
            null,
            null,
            ct,
            queryValues: [("kind", kind)]);

    /// <summary><c>GET /api/v1/calendar/events</c>。</summary>
    public Task<EventResponsePagedResultApiResponse?> GetCalendarEventsAsync(
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        string? search = null,
        Guid? calendarId = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        return SendAsync<EventResponsePagedResultApiResponse>(
            ContractOperation.GetCalendarEvents,
            null,
            null,
            ct,
            queryValues:
            [
                ("start", FormatQueryDateTime(start)),
                ("end", FormatQueryDateTime(end)),
                ("search", search),
                ("calendarId", calendarId?.ToString("D")),
                ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)),
            ]);
    }

    /// <summary><c>POST /api/v1/calendar/events</c>。</summary>
    public Task<EventResponseApiResponse?> CreateCalendarEventAsync(
        CreateEventRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<EventResponseApiResponse>(
            ContractOperation.CreateCalendarEvent,
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary><c>GET /api/v1/calendar/task-books</c>。</summary>
    public Task<TaskBookDtoIReadOnlyListApiResponse?> GetTaskBooksAsync(
        CancellationToken ct = default)
        => SendAsync<TaskBookDtoIReadOnlyListApiResponse>(
            ContractOperation.GetTaskBooks,
            null,
            null,
            ct);

    /// <summary><c>GET /api/v1/calendar/tasks</c>。</summary>
    public Task<TaskResponsePagedResultApiResponse?> GetTasksAsync(
        bool? inbox = null,
        string? search = null,
        Guid? calendarId = null,
        string? status = null,
        int? priority = null,
        DateTimeOffset? plannedFrom = null,
        DateTimeOffset? plannedTo = null,
        DateTimeOffset? dueFrom = null,
        DateTimeOffset? dueTo = null,
        int page = 1,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        return SendAsync<TaskResponsePagedResultApiResponse>(
            ContractOperation.GetTasks,
            null,
            null,
            ct,
            queryValues:
            [
                ("inbox", inbox?.ToString().ToLowerInvariant()),
                ("search", search),
                ("calendarId", calendarId?.ToString("D")),
                ("status", status),
                ("priority", priority?.ToString(CultureInfo.InvariantCulture)),
                ("plannedFrom", FormatQueryDateTime(plannedFrom)),
                ("plannedTo", FormatQueryDateTime(plannedTo)),
                ("dueFrom", FormatQueryDateTime(dueFrom)),
                ("dueTo", FormatQueryDateTime(dueTo)),
                ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)),
            ]);
    }

    /// <summary><c>POST /api/v1/calendar/tasks</c>。</summary>
    public Task<TaskResponseApiResponse?> CreateTaskAsync(
        CreateTaskRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<TaskResponseApiResponse>(
            ContractOperation.CreateTask,
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary><c>PUT /api/v1/calendar/tasks/{id}</c>。</summary>
    public Task<TaskResponseApiResponse?> UpdateTaskAsync(
        Guid id,
        UpdateTaskRequest request,
        CancellationToken ct = default)
        => SendAsync<TaskResponseApiResponse>(
            ContractOperation.UpdateTask,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary><c>POST /api/v1/pc/tracker/upload</c>。</summary>
    public Task<Int32ApiResponse?> UploadTrackerEventsAsync(TrackerEventsUploadRequest request, CancellationToken ct = default)
        => SendJsonAsync<Int32ApiResponse>(ContractOperation.UploadTrackerEvents, request, ct);

    /// <summary><c>GET /api/v1/pc/detail</c>.</summary>
    public Task<TypedDetailQueryResponseApiResponse?> GetPcDetailAsync(
        string? dateFrom = null,
        string? dateTo = null,
        string? dimension = null,
        string? deviceId = null,
        string? appName = null,
        string? categoryName = null,
        string? keyName = null,
        string? eventType = null,
        string? sortBy = null,
        string? sortDir = null,
        string? domain = null,
        string? title = null,
        string? urlFilter = null,
        string? view = null,
        int page = 1,
        int pageSize = 100,
        CancellationToken ct = default)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        return SendAsync<TypedDetailQueryResponseApiResponse>(
            ContractOperation.GetPcDetail,
            null,
            null,
            ct,
            queryValues:
            [
                ("dateFrom", dateFrom),
                ("dateTo", dateTo),
                ("dimension", dimension),
                ("deviceId", deviceId),
                ("appName", appName),
                ("categoryName", categoryName),
                ("keyName", keyName),
                ("eventType", eventType),
                ("sortBy", sortBy),
                ("sortDir", sortDir),
                ("domain", domain),
                ("title", title),
                ("url", urlFilter),
                ("view", view),
                ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)),
            ]);
    }

    /// <summary><c>POST /api/v1/pc/browser-tt/upload</c>。</summary>
    public Task<Int32ApiResponse?> UploadSiteEventsAsync(SiteEventsUploadRequest request, CancellationToken ct = default)
        => SendJsonAsync<Int32ApiResponse>(ContractOperation.UploadSiteEvents, request, ct);

    /// <summary><c>POST /api/v1/pc/tracker/health</c>。</summary>
    public Task<StringApiResponse?> ReportTrackerHealthAsync(TrackerHealthRequest request, CancellationToken ct = default)
        => SendJsonAsync<StringApiResponse>(ContractOperation.ReportTrackerHealth, request, ct);

    /// <summary><c>POST /api/v1/pc/keystats/samples</c>。</summary>
    public Task<StringApiResponse?> UploadKeystatsSampleAsync(KeystatsSampleUploadRequest request, CancellationToken ct = default)
        => SendJsonAsync<StringApiResponse>(ContractOperation.UploadKeystatsSample, request, ct);

    /// <summary><c>POST /api/v1/pc/keystats/upload</c>。</summary>
    public Task<StringApiResponse?> UploadKeystatsSnapshotAsync(KeystatsUploadRequest request, CancellationToken ct = default)
        => SendJsonAsync<StringApiResponse>(ContractOperation.UploadKeystatsSnapshot, request, ct);

    /// <summary><c>GET /api/v1/pc/categories/tree</c>。</summary>
    public Task<CategoryTreeNodeListApiResponse?> GetPcCategoryTreeAsync(CancellationToken ct = default)
        => SendAsync<CategoryTreeNodeListApiResponse>(
            ContractOperation.GetPcCategoryTree,
            null,
            null,
            ct);

    /// <summary><c>POST /api/v1/pc/categories</c>：新增或更新分类树节点。</summary>
    public Task<CategoryTreeNodeApiResponse?> SavePcCategoryAsync(
        CategorySaveRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<CategoryTreeNodeApiResponse>(
            ContractOperation.SavePcCategory,
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary><c>DELETE /api/v1/pc/categories/{id}</c>。</summary>
    public Task<StringApiResponse?> DeletePcCategoryAsync(Guid id, CancellationToken ct = default)
        => SendAsync<StringApiResponse>(
            ContractOperation.DeletePcCategory,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            null,
            ct);

    /// <summary><c>GET /api/v1/pc/app-knowledge/apps?search=...</c>。</summary>
    public Task<AppKnowledgeAppDtoListApiResponse?> SearchAppKnowledgeAppsAsync(
        string? search = null,
        CancellationToken ct = default)
        => SendAsync<AppKnowledgeAppDtoListApiResponse>(
            ContractOperation.SearchAppKnowledgeApps,
            null,
            null,
            ct,
            queryValues: [("search", search)]);

    /// <summary><c>GET /api/v1/pc/app-knowledge/apps/{appId}/contexts</c>。</summary>
    public Task<AppKnowledgeContextDtoListApiResponse?> GetAppKnowledgeContextsAsync(
        Guid appId,
        CancellationToken ct = default)
        => SendAsync<AppKnowledgeContextDtoListApiResponse>(
            ContractOperation.GetAppKnowledgeContexts,
            [("appId", RequireGuid(appId, nameof(appId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>POST /api/v1/pc/app-knowledge/contexts</c>。</summary>
    public Task<AppKnowledgeContextDtoApiResponse?> SaveAppKnowledgeContextAsync(
        SaveAppKnowledgeContextRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<AppKnowledgeContextDtoApiResponse>(
            ContractOperation.SaveAppKnowledgeContext,
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary><c>DELETE /api/v1/pc/app-knowledge/contexts/{id}</c>。</summary>
    public Task<StringApiResponse?> DeleteAppKnowledgeContextAsync(Guid id, CancellationToken ct = default)
        => SendAsync<StringApiResponse>(
            ContractOperation.DeleteAppKnowledgeContext,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            null,
            ct);

    /// <summary>
    /// <c>GET /api/client/shell/latest</c>（匿名；注意它在 <c>/api/v1</c> 之外，走 host 根路径）。
    /// 守护程序的「检查更新」用它。
    ///
    /// <para>
    /// `allowTokenRenewal: false`：契约里该端点 `security: null`，**与登录状态无关**。若它跟着走
    /// 「临期先续期」那条路，令牌失效/断网时会在请求发出前就抛异常 —— 一个不需要认证的更新检查
    /// 会因为本地令牌状态而失败（首轮 review 的 Important）。
    /// 注意请求仍会带上共享客户端的 Bearer（服务端忽略；与 login/register/refresh 的既有情形一致）。
    /// </para>
    /// </summary>
    public Task<ClientShellLatestResponse?> GetClientShellLatestAsync(CancellationToken ct = default)
        => SendAsync<ClientShellLatestResponse>(
            ContractOperation.GetClientShellLatest, null, null, ct, allowTokenRenewal: false);

    /// <summary><c>POST /api/v1/native-notes/asr-routing/nodes/register</c>。</summary>
    public Task<NoteAsrNodeDtoApiResponse?> RegisterNativeAsrNodeAsync(
        RegisterNoteAsrNodeRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<NoteAsrNodeDtoApiResponse>(
            ContractOperation.RegisterNativeAsrNode,
            request,
            ct);

    /// <summary><c>POST /api/v1/native-notes/asr-routing/nodes/{nodeId}/heartbeat</c>。</summary>
    public Task<NoteAsrNodeDtoApiResponse?> HeartbeatNativeAsrNodeAsync(
        string nodeId,
        NoteAsrNodeHeartbeatRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteAsrNodeDtoApiResponse>(
            ContractOperation.HeartbeatNativeAsrNode,
            [("nodeId", nodeId)],
            request,
            ct);

    /// <summary><c>POST /api/v1/native-notes/asr-routing/nodes/{nodeId}/withdraw</c>（无请求体）。</summary>
    public Task<NoteAsrNodeDtoApiResponse?> WithdrawNativeAsrNodeAsync(
        string nodeId,
        CancellationToken ct = default)
        => SendAsync<NoteAsrNodeDtoApiResponse>(
            ContractOperation.WithdrawNativeAsrNode,
            [("nodeId", nodeId)],
            null,
            ct);

    /// <summary><c>POST /api/v1/native-notes/asr-routing/leases/claim</c>。</summary>
    public Task<NoteAsrClaimDtoApiResponse?> ClaimNativeAsrLeaseAsync(
        ClaimNoteAsrTaskRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<NoteAsrClaimDtoApiResponse>(
            ContractOperation.ClaimNativeAsrLease,
            request,
            ct);

    /// <summary><c>POST /api/v1/native-notes/asr-routing/leases/{leaseId}/renew</c>。</summary>
    public Task<NoteAsrLeaseDtoApiResponse?> RenewNativeAsrLeaseAsync(
        string leaseId,
        NoteAsrLeaseHeartbeatRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteAsrLeaseDtoApiResponse>(
            ContractOperation.RenewNativeAsrLease,
            [("leaseId", leaseId)],
            request,
            ct);

    /// <summary>
    /// <c>GET /api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content</c>。
    /// The returned response owns a bounded, response-header-read stream and must be disposed.
    /// </summary>
    public async Task<NativeAsrSegmentDownload?> GetNativeAsrSegmentContentAsync(
        string leaseId,
        int segmentIndex,
        string leaseToken,
        long fencingToken,
        long maxContentBytes,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(leaseId, out var parsedLeaseId) || parsedLeaseId == Guid.Empty)
            throw new ArgumentException("leaseId must be a non-empty UUID", nameof(leaseId));
        if (segmentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        if (!Guid.TryParse(leaseToken, out var parsedLeaseToken) || parsedLeaseToken == Guid.Empty)
            throw new ArgumentException("leaseToken must be a non-empty UUID", nameof(leaseToken));
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        if (maxContentBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxContentBytes));

        var relativePath = ContractOperation.GetNativeAsrSegmentContent.Resolve(
            ("leaseId", parsedLeaseId.ToString("D")),
            ("segmentIndex", segmentIndex.ToString(CultureInfo.InvariantCulture)));

        var clientLease = CaptureClientLease();
        var response = await SendResponseWithAuthRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
                request.Headers.TryAddWithoutValidation(
                    "X-Pim-Lease-Token",
                    parsedLeaseToken.ToString("D"));
                request.Headers.TryAddWithoutValidation(
                    "X-Pim-Fencing-Token",
                    fencingToken.ToString(CultureInfo.InvariantCulture));
                return clientLease.Client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);
            },
            ct,
            allowTokenRenewal: true,
            clientLease: clientLease).ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "audio/wav", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "native ASR segment response must use audio/wav content type");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0 && declaredLength.Value > maxContentBytes)
            {
                throw new InvalidDataException(
                    "native ASR segment declared content length exceeds the configured limit");
            }

            var headers = ParseNativeAsrSegmentHeaders(response);
            var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bounded = new BoundedHttpResponseStream(
                content,
                response,
                maxContentBytes,
                ownsResponse: true);
            return new NativeAsrSegmentDownload(headers, bounded);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary><c>POST /api/v1/native-notes/asr-routing/leases/{attemptId}/ack</c>。</summary>
    public Task<NoteAsrAckDtoApiResponse?> AckNativeAsrAttemptAsync(
        string attemptId,
        NoteAsrAckRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteAsrAckDtoApiResponse>(
            ContractOperation.AckNativeAsrAttempt,
            [("attemptId", attemptId)],
            request,
            ct);

    /// <summary><c>POST /api/v1/native-notes/versions</c>：首次上传可用空 baseVersionId。</summary>
    public Task<NoteMaterialUploadDtoApiResponse?> BeginNoteMaterialUploadAsync(
        BeginNoteMaterialUploadRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<NoteMaterialUploadDtoApiResponse>(
            ContractOperation.BeginNoteMaterialUpload,
            request,
            ct);

    /// <summary><c>GET /api/v1/native-notes/{noteId}/versions</c>。</summary>
    public Task<NoteMaterialUploadDtoIReadOnlyListApiResponse?> GetNoteMaterialVersionsAsync(
        Guid noteId,
        CancellationToken ct = default)
        => SendAsync<NoteMaterialUploadDtoIReadOnlyListApiResponse>(
            ContractOperation.GetNoteMaterialVersions,
            [("noteId", RequireGuid(noteId, nameof(noteId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>GET /api/v1/native-notes/versions/{versionId}</c>。</summary>
    public Task<NoteMaterialVersionDtoApiResponse?> GetNoteMaterialVersionAsync(
        Guid versionId,
        CancellationToken ct = default)
        => SendAsync<NoteMaterialVersionDtoApiResponse>(
            ContractOperation.GetNoteMaterialVersion,
            [("versionId", RequireGuid(versionId, nameof(versionId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>POST /api/v1/native-notes/versions/{versionId}/complete</c>。</summary>
    public Task<NoteMaterialUploadDtoApiResponse?> CompleteNoteMaterialUploadAsync(
        Guid versionId,
        CancellationToken ct = default)
        => SendAsync<NoteMaterialUploadDtoApiResponse>(
            ContractOperation.CompleteNoteMaterialUpload,
            [("versionId", RequireGuid(versionId, nameof(versionId)).ToString("D"))],
            null,
            ct);

    /// <summary>
    /// <c>PUT /api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}</c>。
    /// 请求体是原始 application/octet-stream；调用方负责给出该分片字节的 SHA-256。
    /// </summary>
    public async Task<StringApiResponse?> UploadNoteMaterialPartAsync(
        Guid versionId,
        int fileIndex,
        int partIndex,
        string sha256,
        Stream content,
        CancellationToken ct = default)
    {
        RequireGuid(versionId, nameof(versionId));
        if (fileIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        if (partIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(partIndex));
        var digest = RequireSha256Digest(sha256, nameof(sha256));
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek)
            throw new ArgumentException("材料分片流必须可读且可定位。", nameof(content));

        var initialPosition = content.Position;
        var relativePath = ContractOperation.UploadNoteMaterialPart.Resolve(
                ("versionId", versionId.ToString("D")),
                ("fileIndex", fileIndex.ToString(CultureInfo.InvariantCulture)),
                ("partIndex", partIndex.ToString(CultureInfo.InvariantCulture)))
            + ContractOperation.UploadNoteMaterialPart.ResolveQuery(("sha256", digest));
        var clientLease = CaptureClientLease();
        return await SendWithAuthRetryAsync<StringApiResponse>(
            () => SendNoteMaterialPartHttpAsync(
                clientLease.Client,
                relativePath,
                content,
                initialPosition,
                ct),
            ct,
            allowTokenRenewal: true,
            clientLease).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /api/v1/native-notes/versions/{versionId}/files/{fileIndex}</c>.
    /// The returned response owns a bounded, response-header-read stream and must be disposed.
    /// </summary>
    public async Task<NoteMaterialFileDownload?> DownloadNoteMaterialFileAsync(
        Guid versionId,
        int fileIndex,
        long maxContentBytes,
        CancellationToken ct = default)
    {
        RequireGuid(versionId, nameof(versionId));
        if (fileIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }
        if (maxContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContentBytes));
        }

        var relativePath = ContractOperation.DownloadNoteMaterialFile.Resolve(
            ("versionId", versionId.ToString("D")),
            ("fileIndex", fileIndex.ToString(CultureInfo.InvariantCulture)));
        var clientLease = CaptureClientLease();
        var response = await SendResponseWithAuthRetryAsync(
            () => clientLease.Client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, relativePath),
                HttpCompletionOption.ResponseHeadersRead,
                ct),
            ct,
            allowTokenRenewal: true,
            clientLease: clientLease).ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "note material response must use application/octet-stream content type");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0 && declaredLength.Value > maxContentBytes)
            {
                throw new InvalidDataException(
                    "note material declared content length exceeds the configured limit");
            }

            var headers = ParseNoteMaterialFileHeaders(response);
            var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var bounded = new BoundedHttpResponseStream(
                content,
                response,
                maxContentBytes,
                ownsResponse: true,
                scope: "note material");
            return new NoteMaterialFileDownload(headers, bounded);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary><c>DELETE /api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}</c>。</summary>
    public Task<StringApiResponse?> DeleteNoteMaterialPartAsync(
        Guid versionId,
        int fileIndex,
        int partIndex,
        CancellationToken ct = default)
        => SendAsync<StringApiResponse>(
            ContractOperation.DeleteNoteMaterialPart,
            [
                ("versionId", RequireGuid(versionId, nameof(versionId)).ToString("D")),
                ("fileIndex", fileIndex.ToString(CultureInfo.InvariantCulture)),
                ("partIndex", partIndex.ToString(CultureInfo.InvariantCulture)),
            ],
            null,
            ct);

    /// <summary><c>POST /api/v1/native-notes/versions/{versionId}/promote</c>。</summary>
    public Task<NoteMaterialUploadDtoApiResponse?> PromoteNoteMaterialVersionAsync(
        Guid versionId,
        PromoteNoteMaterialVersionRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteMaterialUploadDtoApiResponse>(
            ContractOperation.PromoteNoteMaterialVersion,
            [("versionId", RequireGuid(versionId, nameof(versionId)).ToString("D"))],
            request,
            ct);

    /// <summary><c>GET /api/v1/native-notes/asr-tasks/sources</c>。</summary>
    public Task<NoteAsrSourcesDtoApiResponse?> GetNoteAsrSourcesAsync(
        int offset = 0,
        int limit = 20,
        CancellationToken ct = default)
        => SendAsync<NoteAsrSourcesDtoApiResponse>(
            ContractOperation.GetNoteAsrSources,
            null,
            null,
            ct,
            queryValues:
            [
                ("offset", offset.ToString(CultureInfo.InvariantCulture)),
                ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ]);

    /// <summary><c>POST /api/v1/native-notes/asr-tasks</c>。</summary>
    public Task<NoteAsrTaskDtoApiResponse?> StartNoteAsrTaskAsync(
        StartNoteAsrRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<NoteAsrTaskDtoApiResponse>(
            ContractOperation.StartNoteAsrTask,
            request,
            ct);

    /// <summary><c>GET /api/v1/native-notes/asr-tasks</c>。</summary>
    public Task<NoteAsrTaskDtoIReadOnlyListApiResponse?> GetNoteAsrTasksAsync(
        CancellationToken ct = default)
        => SendAsync<NoteAsrTaskDtoIReadOnlyListApiResponse>(
            ContractOperation.GetNoteAsrTasks,
            null,
            null,
            ct);

    /// <summary><c>GET /api/v1/native-notes/asr-tasks/{taskId}</c>。</summary>
    public Task<NoteAsrTaskDtoApiResponse?> GetNoteAsrTaskAsync(
        Guid taskId,
        CancellationToken ct = default)
        => SendAsync<NoteAsrTaskDtoApiResponse>(
            ContractOperation.GetNoteAsrTask,
            [("taskId", RequireGuid(taskId, nameof(taskId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>GET /api/v1/native-notes/asr-tasks/{taskId}/segments</c>。</summary>
    public Task<NoteAsrSegmentsDtoApiResponse?> GetNoteAsrTaskSegmentsAsync(
        Guid taskId,
        int offset = 0,
        int limit = 10,
        CancellationToken ct = default)
        => SendAsync<NoteAsrSegmentsDtoApiResponse>(
            ContractOperation.GetNoteAsrTaskSegments,
            [("taskId", RequireGuid(taskId, nameof(taskId)).ToString("D"))],
            null,
            ct,
            queryValues:
            [
                ("offset", offset.ToString(CultureInfo.InvariantCulture)),
                ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ]);

    /// <summary><c>GET /api/v1/native-notes/derived-tasks/sources</c>。</summary>
    public Task<NoteAsrSourcesDtoApiResponse?> GetNoteDerivedSourcesAsync(
        int offset = 0,
        int limit = 20,
        CancellationToken ct = default)
        => SendAsync<NoteAsrSourcesDtoApiResponse>(
            ContractOperation.GetNoteDerivedSources,
            null,
            null,
            ct,
            queryValues:
            [
                ("offset", offset.ToString(CultureInfo.InvariantCulture)),
                ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ]);

    /// <summary><c>GET /api/v1/native-notes/derived-tasks?noteId=</c>。</summary>
    public Task<NoteDerivedTaskDtoIReadOnlyListApiResponse?> GetNoteDerivedTasksAsync(
        Guid? noteId = null,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedTaskDtoIReadOnlyListApiResponse>(
            ContractOperation.GetNoteDerivedTasks,
            null,
            null,
            ct,
            queryValues: noteId is { } value
                ? [("noteId", RequireGuid(value, nameof(noteId)).ToString("D"))]
                : []);

    /// <summary><c>POST /api/v1/native-notes/derived-tasks</c>。</summary>
    public Task<NoteDerivedTaskDtoApiResponse?> StartNoteDerivedTaskAsync(
        StartNoteDerivedTaskRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<NoteDerivedTaskDtoApiResponse>(
            ContractOperation.StartNoteDerivedTask,
            request,
            ct);

    /// <summary><c>GET /api/v1/native-notes/derived-tasks/{taskId}</c>。</summary>
    public Task<NoteDerivedTaskDtoApiResponse?> GetNoteDerivedTaskAsync(
        Guid taskId,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedTaskDtoApiResponse>(
            ContractOperation.GetNoteDerivedTask,
            [("taskId", RequireGuid(taskId, nameof(taskId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>GET /api/v1/native-notes/derived-tasks/{taskId}/steps</c>。</summary>
    public Task<NoteDerivedStepsDtoApiResponse?> GetNoteDerivedTaskStepsAsync(
        Guid taskId,
        int offset = 0,
        int limit = 10,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedStepsDtoApiResponse>(
            ContractOperation.GetNoteDerivedTaskSteps,
            [("taskId", RequireGuid(taskId, nameof(taskId)).ToString("D"))],
            null,
            ct,
            queryValues:
            [
                ("offset", offset.ToString(CultureInfo.InvariantCulture)),
                ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ]);

    /// <summary><c>POST /api/v1/native-notes/derived-tasks/{taskId}/retry</c>。</summary>
    public Task<NoteDerivedTaskDtoApiResponse?> RetryNoteDerivedTaskAsync(
        Guid taskId,
        RetryNoteDerivedTaskRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedTaskDtoApiResponse>(
            ContractOperation.RetryNoteDerivedTask,
            [("taskId", RequireGuid(taskId, nameof(taskId)).ToString("D"))],
            request,
            ct);

    /// <summary><c>POST /api/v1/native-notes/derived-tasks/{taskId}/stop</c>。</summary>
    public Task<NoteDerivedTaskDtoApiResponse?> StopNoteDerivedTaskAsync(
        Guid taskId,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedTaskDtoApiResponse>(
            ContractOperation.StopNoteDerivedTask,
            [("taskId", RequireGuid(taskId, nameof(taskId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>GET /api/v1/native-notes/derived-artifacts?noteId=</c>。</summary>
    public Task<NoteDerivedArtifactDtoIReadOnlyListApiResponse?> GetNoteDerivedArtifactsAsync(
        Guid noteId,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedArtifactDtoIReadOnlyListApiResponse>(
            ContractOperation.GetNoteDerivedArtifacts,
            null,
            null,
            ct,
            queryValues:
            [
                ("noteId", RequireGuid(noteId, nameof(noteId)).ToString("D")),
            ]);

    /// <summary><c>GET /api/v1/native-notes/derived-artifacts/{artifactId}</c>。</summary>
    public Task<NoteDerivedArtifactDtoApiResponse?> GetNoteDerivedArtifactAsync(
        Guid artifactId,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedArtifactDtoApiResponse>(
            ContractOperation.GetNoteDerivedArtifact,
            [("artifactId", RequireGuid(artifactId, nameof(artifactId)).ToString("D"))],
            null,
            ct);

    /// <summary><c>GET /api/v1/native-notes/derived-artifacts/{artifactId}/versions/{versionId}</c>。</summary>
    public Task<NoteDerivedVersionDtoApiResponse?> GetNoteDerivedArtifactVersionAsync(
        Guid artifactId,
        Guid versionId,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedVersionDtoApiResponse>(
            ContractOperation.GetNoteDerivedArtifactVersion,
            [
                ("artifactId", RequireGuid(artifactId, nameof(artifactId)).ToString("D")),
                ("versionId", RequireGuid(versionId, nameof(versionId)).ToString("D")),
            ],
            null,
            ct);

    /// <summary><c>POST /api/v1/native-notes/derived-artifacts/{artifactId}/manual-revisions</c>。</summary>
    public Task<NoteDerivedMutationDtoApiResponse?> SaveNoteDerivedManualRevisionAsync(
        Guid artifactId,
        SaveNoteDerivedManualRevisionRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedMutationDtoApiResponse>(
            ContractOperation.SaveNoteDerivedManualRevision,
            [("artifactId", RequireGuid(artifactId, nameof(artifactId)).ToString("D"))],
            request,
            ct);

    /// <summary><c>POST /api/v1/native-notes/derived-artifacts/{artifactId}/select</c>。</summary>
    public Task<NoteDerivedArtifactDtoApiResponse?> SelectNoteDerivedArtifactVersionAsync(
        Guid artifactId,
        SelectNoteDerivedVersionRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteDerivedArtifactDtoApiResponse>(
            ContractOperation.SelectNoteDerivedArtifactVersion,
            [("artifactId", RequireGuid(artifactId, nameof(artifactId)).ToString("D"))],
            request,
            ct);

    /// <summary>列出契约批准的笔记派生任务目标目录。</summary>
    public Task<NoteDerivedDestinationsDtoApiResponse?> GetNoteDerivedDestinationsAsync(
        CancellationToken ct = default)
        => SendAsync<NoteDerivedDestinationsDtoApiResponse>(
            ContractOperation.GetNoteDerivedDestinations,
            null,
            null,
            ct);

    /// <summary>读取当前账号任务建议提取记录。</summary>
    public Task<NoteTaskSuggestionExtractionDtoApiResponse?> StartNoteTaskSuggestionExtractionAsync(
        StartNoteTaskSuggestionExtractionRequest request,
        CancellationToken ct = default)
        => SendJsonAsync<NoteTaskSuggestionExtractionDtoApiResponse>(
            ContractOperation.StartNoteTaskSuggestionExtraction,
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary>读取任务建议提取状态。</summary>
    public Task<NoteTaskSuggestionExtractionDtoApiResponse?> GetNoteTaskSuggestionExtractionAsync(
        Guid id,
        CancellationToken ct = default)
        => SendAsync<NoteTaskSuggestionExtractionDtoApiResponse>(
            ContractOperation.GetNoteTaskSuggestionExtraction,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            null,
            ct);

    /// <summary>按笔记和可选状态读取任务建议。</summary>
    public Task<NoteTaskSuggestionListDtoApiResponse?> GetNoteTaskSuggestionsAsync(
        Guid? noteId = null,
        NoteTaskSuggestionStatusDto? status = null,
        CancellationToken ct = default)
        => SendAsync<NoteTaskSuggestionListDtoApiResponse>(
            ContractOperation.GetNoteTaskSuggestions,
            null,
            null,
            ct,
            queryValues:
            [
                ("noteId", noteId is { } value
                    ? RequireGuid(value, nameof(noteId)).ToString("D")
                    : null),
                ("status", status?.ToString()),
            ]);

    /// <summary>读取单条任务建议。</summary>
    public Task<NoteTaskSuggestionDtoApiResponse?> GetNoteTaskSuggestionAsync(
        Guid id,
        CancellationToken ct = default)
        => SendAsync<NoteTaskSuggestionDtoApiResponse>(
            ContractOperation.GetNoteTaskSuggestion,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            null,
            ct);

    /// <summary>以 revision CAS 更新任务建议草稿。</summary>
    public Task<NoteTaskSuggestionDtoApiResponse?> UpdateNoteTaskSuggestionAsync(
        Guid id,
        UpdateNoteTaskSuggestionRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteTaskSuggestionDtoApiResponse>(
            ContractOperation.UpdateNoteTaskSuggestion,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary>以稳定 requestId 幂等确认任务建议。</summary>
    public Task<NoteTaskSuggestionDtoApiResponse?> ConfirmNoteTaskSuggestionAsync(
        Guid id,
        ConfirmNoteTaskSuggestionRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteTaskSuggestionDtoApiResponse>(
            ContractOperation.ConfirmNoteTaskSuggestion,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    /// <summary>按 revision CAS 放弃任务建议。</summary>
    public Task<NoteTaskSuggestionDtoApiResponse?> DismissNoteTaskSuggestionAsync(
        Guid id,
        DismissNoteTaskSuggestionRequest request,
        CancellationToken ct = default)
        => SendAsync<NoteTaskSuggestionDtoApiResponse>(
            ContractOperation.DismissNoteTaskSuggestion,
            [("id", RequireGuid(id, nameof(id)).ToString("D"))],
            request ?? throw new ArgumentNullException(nameof(request)),
            ct);

    // -----------------------------------------------------------------------
    // 内部实现：只接受生成物里的 operation，平静态类型化的请求体
    // -----------------------------------------------------------------------

    private Task<TResponse?> SendJsonAsync<TResponse>(
        ContractOperation operation,
        object body,
        CancellationToken ct,
        bool allowTokenRenewal = true)
        => SendAsync<TResponse>(operation, null, body, ct, allowTokenRenewal);

    private async Task<TResponse?> SendAsync<TResponse>(
        ContractOperation operation,
        (string Name, string Value)[]? routeValues,
        object? body,
        CancellationToken ct,
        bool allowTokenRenewal = true,
        (string Name, string? Value)[]? queryValues = null)
    {
        var relativePath = operation.Resolve(routeValues ?? [])
            + operation.ResolveQuery(queryValues ?? []);

        var clientLease = CaptureClientLease();
        return await SendWithAuthRetryAsync<TResponse>(
            () => SendHttpAsync(clientLease.Client, operation, relativePath, body, ct),
            ct,
            allowTokenRenewal,
            clientLease);
    }

    private Task<HttpResponseMessage> SendHttpAsync(
        HttpClient client,
        ContractOperation operation,
        string relativePath,
        object? body,
        CancellationToken ct)
        => operation.HttpMethod switch
        {
            "GET" => client.GetAsync(relativePath, ct),
            "POST" => body is null
                ? client.PostAsync(relativePath, null, ct)
                : client.PostAsJsonAsync(relativePath, body, ct),
            "PUT" => client.PutAsJsonAsync(relativePath, body, ct),
            "DELETE" => client.DeleteAsync(relativePath, ct),
            _ => throw new NotSupportedException(
                $"{operation.HttpMethod} {operation.PathTemplate}：本仓的 HTTP 边界尚未支持该方法"),
        };

    private static async Task<HttpResponseMessage> SendNoteMaterialPartHttpAsync(
        HttpClient client,
        string relativePath,
        Stream content,
        long initialPosition,
        CancellationToken ct)
    {
        content.Position = initialPosition;
        using var request = new HttpRequestMessage(HttpMethod.Put, relativePath);
        var body = new StreamContent(new LeaveOpenReadStream(content));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content = body;
        return await client.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<T?> SendWithAuthRetryAsync<T>(
        Func<Task<HttpResponseMessage>> request,
        CancellationToken ct,
        bool allowTokenRenewal,
        ClientLease clientLease)
    {
        using var response = await SendResponseWithAuthRetryAsync(
            request,
            ct,
            allowTokenRenewal,
            clientLease).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendResponseWithAuthRetryAsync(
        Func<Task<HttpResponseMessage>> request,
        CancellationToken ct,
        bool allowTokenRenewal,
        ClientLease clientLease)
    {
        // 到期前主动续期（REQ-5）：令牌进入临期窗口就先换新，再发本次请求。
        if (allowTokenRenewal)
        {
            await EnsureTokenRenewedBeforeRequestAsync(ct);
        }

        var sw = Stopwatch.StartNew();
        // 记下**发送前**的续期代数：收到 401 时用它判断"这次 401 之后有没有别人已经换过令牌"。
        // 不能用"收到 401 时"的代数 —— 那样后来者会误判为"还没人换过"再续期一次，
        // 把前一个请求刚拿到的新令牌又轮换掉，导致它的重试撞上 401（并发下的连锁失败）。
        var versionAtSend = Interlocked.Read(ref _tokenRenewalVersion);
        HttpResponseMessage? response = null;
        try
        {
            response = await StartPinnedRequest(clientLease, request).ConfigureAwait(false);
            var firstHopMs = sw.ElapsedMilliseconds;

            if (allowTokenRenewal
                && response.StatusCode == HttpStatusCode.Unauthorized
                && RenewTokenAsync is not null)
            {
                // 被动兜底：仍然保留"401 → 续期 → 重试一次"，但并发到达的多个 401 只会产生**一次**
                // 真实续期（旧实现用 volatile bool 直接跳过后来者，被跳过的请求拿着旧令牌继续走，
                // 最终 EnsureSuccessStatusCode 抛 401 —— 上传循环把它当"客户端错误"丢掉整批数据）。
                if (await RenewTokenAfterUnauthorizedAsync(versionAtSend, ct))
                {
                    response.Dispose();
                    response = null;
                    sw.Restart();
                    response = await StartPinnedRequest(clientLease, request).ConfigureAwait(false);
                    RequestTiming?.Invoke($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} (after refresh)", sw.ElapsedMilliseconds);
                }
                else
                {
                    RequestTiming?.Invoke($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}", firstHopMs);
                }
            }
            else
            {
                RequestTiming?.Invoke($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}", firstHopMs);
            }

            return response;
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private ClientLease CaptureClientLease()
    {
        if (_operationLease.Value is { } operationLease)
        {
            EnsureLeaseCurrent(operationLease);
            return operationLease;
        }

        lock (_clientSync)
        {
            return new ClientLease(
                _httpClient,
                _clientGeneration,
                _externalCredentialGeneration);
        }
    }

    /// <summary>
    /// Pins all API calls in the current async flow to one deployment and external
    /// credential snapshot. A natural token refresh may update the bearer token on
    /// the pinned HttpClient; an account/server switch fails before every request.
    /// </summary>
    internal ApiOperationScope BeginOperationScope()
    {
        ClientLease lease;
        lock (_clientSync)
        {
            lease = new ClientLease(
                _httpClient,
                _clientGeneration,
                _externalCredentialGeneration);
        }

        var previous = _operationLease.Value;
        _operationLease.Value = lease;
        return new ApiOperationScope(this, previous);
    }

    internal void EnsureOperationScopeCurrent()
    {
        if (_operationLease.Value is { } lease)
        {
            EnsureLeaseCurrent(lease);
        }
    }

    private void EnsureLeaseCurrent(ClientLease lease)
    {
        lock (_clientSync)
        {
            if (_clientGeneration != lease.Generation
                || !ReferenceEquals(_httpClient, lease.Client))
            {
                throw new HttpRequestException(
                    "API deployment changed while the operation was in flight; no further request was sent");
            }

            if (_externalCredentialGeneration != lease.ExternalCredentialGeneration)
            {
                throw new HttpRequestException(
                    "API credentials changed while the operation was in flight; no further request was sent");
            }
        }
    }

    private Task<HttpResponseMessage> StartPinnedRequest(
        ClientLease lease,
        Func<Task<HttpResponseMessage>> request)
    {
        lock (_clientSync)
        {
            if (_clientGeneration != lease.Generation
                || !ReferenceEquals(_httpClient, lease.Client))
            {
                throw new HttpRequestException(
                    "API deployment changed while the request was in flight; the request was not sent");
            }

            if (_externalCredentialGeneration != lease.ExternalCredentialGeneration)
            {
                throw new HttpRequestException(
                    "API credentials changed while the request was in flight; the request was not sent");
            }

            return request();
        }
    }

    internal sealed class ApiOperationScope : IDisposable
    {
        private readonly ApiClient _owner;
        private readonly ClientLease? _previous;
        private bool _disposed;

        internal ApiOperationScope(ApiClient owner, ClientLease? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._operationLease.Value = _previous;
        }
    }

    /// <summary>
    /// 发请求前的到期前续期。
    /// 续期失败时**不发**这次请求，而是抛一个不带状态码的 <see cref="HttpRequestException"/>：
    /// 拿已经过期的令牌去请求必然得到 401，而调用方把 401 视为"数据不合格"会丢掉待上报队列
    /// （REQ-5 反面行为要求不得丢队列、不得登出）。无状态码的传输层异常走的是"稍后重试"那条路。
    /// </summary>
    private async Task EnsureTokenRenewedBeforeRequestAsync(CancellationToken ct)
    {
        if (RenewTokenAsync is null || IsTokenRenewalDue?.Invoke() != true)
        {
            return;
        }

        if (InsideTokenRenewal.Value)
        {
            // 续期请求自身：不再触发续期。
            return;
        }

        var versionBefore = Interlocked.Read(ref _tokenRenewalVersion);
        await _tokenRenewalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _tokenRenewalVersion) != versionBefore)
            {
                // 排队期间别人已经续期成功，直接发请求。
                return;
            }

            bool renewed;
            InsideTokenRenewal.Value = true;
            try
            {
                renewed = await RenewTokenAsync().ConfigureAwait(false);
            }
            finally
            {
                InsideTokenRenewal.Value = false;
            }

            if (renewed)
            {
                Interlocked.Increment(ref _tokenRenewalVersion);
                return;
            }

            throw new HttpRequestException(
                "access token renewal failed before request; request was not sent so the pending queue is kept for a later retry");
        }
        finally
        {
            _tokenRenewalGate.Release();
        }
    }

    /// <summary>
    /// 收到 401 之后的续期：串行化 + 复用结果。
    /// <paramref name="versionAtSend"/> 是**发出这个请求之前**的续期代数：
    /// 只要它变了，就说明已经有人换好了令牌，直接重试即可，不再打第二次 /auth/refresh。
    /// 返回 true 表示调用方可以重试本次请求。
    /// </summary>
    private async Task<bool> RenewTokenAfterUnauthorizedAsync(long versionAtSend, CancellationToken ct)
    {
        if (InsideTokenRenewal.Value)
        {
            // 续期请求自身收到 401（refresh token 已失效）：不再递归续期。
            return false;
        }

        await _tokenRenewalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _tokenRenewalVersion) != versionAtSend)
            {
                // 本次请求发出之后已有请求续期成功：重试即可，不再打第二次 refresh。
                return true;
            }

            InsideTokenRenewal.Value = true;
            try
            {
                var renewTokenAsync = RenewTokenAsync;
                if (renewTokenAsync is null
                    || !await renewTokenAsync().ConfigureAwait(false))
                {
                    return false;
                }
            }
            finally
            {
                InsideTokenRenewal.Value = false;
            }

            Interlocked.Increment(ref _tokenRenewalVersion);
            return true;
        }
        finally
        {
            _tokenRenewalGate.Release();
        }
    }

    private static bool SameCanonicalApiRoot(Uri? previous, Uri next)
    {
        if (previous is null)
        {
            return false;
        }

        return string.Equals(previous.Scheme, next.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(previous.IdnHost, next.IdnHost, StringComparison.OrdinalIgnoreCase)
               && previous.Port == next.Port
               && string.Equals(
                   ApiRootPath(previous.AbsolutePath),
                   ApiRootPath(next.AbsolutePath),
                   StringComparison.Ordinal);
    }

    private static string ApiRootPath(string absolutePath)
    {
        const string apiSuffix = "/api/v1";
        var path = (absolutePath == "/" ? string.Empty : absolutePath).TrimEnd('/');
        if (path.EndsWith(apiSuffix, StringComparison.Ordinal))
        {
            path = path[..^apiSuffix.Length];
        }

        return path.Length == 0 ? "/" : path;
    }

    private static NativeAsrSegmentHeaders ParseNativeAsrSegmentHeaders(
        HttpResponseMessage response)
    {
        var attemptId = ParseGuidHeader(response, "X-Pim-Attempt-Id");
        var versionId = ParseGuidHeader(response, "X-Pim-Version-Id");
        var leaseId = ParseGuidHeader(response, "X-Pim-Lease-Id");
        var nodeId = ParseGuidHeader(response, "X-Pim-Node-Id");
        var ownerId = RequireSingleHeader(response, "X-Pim-Owner-Id");
        var fencingToken = ParseInt64Header(response, "X-Pim-Fencing-Token");
        if (fencingToken <= 0)
        {
            throw new InvalidDataException("X-Pim-Fencing-Token must be positive");
        }

        var segmentIndex = ParseInt32Header(response, "X-Pim-Segment-Index");
        if (segmentIndex < 0)
        {
            throw new InvalidDataException("X-Pim-Segment-Index must be non-negative");
        }

        var sourceFileIndex = ParseInt32Header(response, "X-Pim-Source-File-Index");
        if (sourceFileIndex < 0)
        {
            throw new InvalidDataException("X-Pim-Source-File-Index must be non-negative");
        }

        var sourceOffsetSamples = ParseInt64Header(response, "X-Pim-Source-Offset-Samples");
        if (sourceOffsetSamples < 0)
        {
            throw new InvalidDataException("X-Pim-Source-Offset-Samples must be non-negative");
        }

        var samples = ParseInt64Header(response, "X-Pim-Samples");
        if (samples <= 0)
        {
            throw new InvalidDataException("X-Pim-Samples must be positive");
        }

        var sourceSha256 = ParseSha256Header(response, "X-Pim-Source-SHA256");
        var contentSha256 = ParseLowercaseSha256Header(response, "X-Content-SHA256");

        return new NativeAsrSegmentHeaders(
            attemptId,
            versionId,
            leaseId,
            nodeId,
            ownerId,
            fencingToken,
            segmentIndex,
            sourceFileIndex,
            sourceOffsetSamples,
            samples,
            sourceSha256,
            contentSha256);
    }

    private static NoteMaterialFileHeaders ParseNoteMaterialFileHeaders(
        HttpResponseMessage response)
    {
        var cacheControl = RequireSingleHeader(response, "Cache-Control");
        var cacheDirectives = cacheControl
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directive => directive.Split('=', 2)[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!cacheDirectives.Contains("private") || !cacheDirectives.Contains("no-store"))
        {
            throw new InvalidDataException(
                "note material response Cache-Control must be private, no-store");
        }

        var dispositionValue = RequireSingleHeader(response, "Content-Disposition");
        if (!ContentDispositionHeaderValue.TryParse(dispositionValue, out var disposition)
            || !string.Equals(disposition.DispositionType, "attachment", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "note material response Content-Disposition must be an attachment");
        }

        var rawFileName = (disposition.FileNameStar ?? disposition.FileName)?.Trim().Trim('"');
        var fileName = RequireSafeDownloadFileName(rawFileName);
        var contentSha256 = ParseLowercaseSha256Header(response, "X-Content-SHA256");
        var contentTypeOptions = RequireSingleHeader(response, "X-Content-Type-Options");
        if (!string.Equals(contentTypeOptions, "nosniff", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "note material response X-Content-Type-Options must be nosniff");
        }

        return new NoteMaterialFileHeaders(
            cacheControl,
            dispositionValue,
            fileName,
            contentSha256);
    }

    private static string RequireSafeDownloadFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 255
            || value is "." or ".."
            || value.Contains('/')
            || value.Contains('\\')
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "note material response Content-Disposition has an unsafe filename");
        }

        return value;
    }

    private static string RequireSingleHeader(HttpResponseMessage response, string name)
    {
        // NonValidated preserves the wire value. Typed parsing may split a
        // syntactically single Cache-Control field into multiple elements.
        var materialized = RawHeaderValues(response.Headers, name)
            .Concat(RawHeaderValues(response.Content.Headers, name))
            .ToArray();
        if (materialized.Length != 1 || string.IsNullOrWhiteSpace(materialized[0]))
        {
            throw new InvalidDataException(
                $"response must contain exactly one {name}");
        }

        return materialized[0].Trim();
    }

    private static IEnumerable<string> RawHeaderValues(HttpHeaders headers, string name)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values))
        {
            return [];
        }

        return values.ToArray();
    }

    private static Guid ParseGuidHeader(HttpResponseMessage response, string name)
    {
        var value = RequireSingleHeader(response, name);
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new InvalidDataException($"{name} is not a non-empty UUID");
        }

        return parsed;
    }

    private static long ParseInt64Header(HttpResponseMessage response, string name)
    {
        var value = RequireSingleHeader(response, name);
        if (!long.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException($"{name} is not a valid Int64");
        }

        return parsed;
    }

    private static int ParseInt32Header(HttpResponseMessage response, string name)
    {
        var value = RequireSingleHeader(response, name);
        if (!int.TryParse(
                value,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException($"{name} is not a valid Int32");
        }

        return parsed;
    }

    private static string ParseSha256Header(HttpResponseMessage response, string name)
    {
        var value = RequireSingleHeader(response, name);
        if (value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{name} is not a SHA-256 digest");
        }

        return value.ToLowerInvariant();
    }

    private static string ParseLowercaseSha256Header(HttpResponseMessage response, string name)
    {
        var value = RequireSingleHeader(response, name);
        if (value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{name} is not a lowercase SHA-256 digest");
        }

        return value;
    }

    private static Guid RequireGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("UUID 不能为空。", parameterName);
        }

        return value;
    }

    private static string RequireSha256Digest(string value, string parameterName)
    {
        if (value is not { Length: 64 }
            || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("SHA-256 必须是 64 位十六进制字符串。", parameterName);
        }

        return value.ToLowerInvariant();
    }

    private static string? FormatQueryDateTime(DateTimeOffset? value)
        => value?.ToString("O", CultureInfo.InvariantCulture);

    internal sealed record ClientLease(
        HttpClient Client,
        long Generation,
        long ExternalCredentialGeneration);
}

internal sealed class LeaveOpenReadStream : Stream
{
    private readonly Stream _inner;

    public LeaveOpenReadStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        // The API client borrows the caller's material stream for each auth retry.
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record NativeAsrSegmentHeaders(
    Guid AttemptId,
    Guid VersionId,
    Guid LeaseId,
    Guid NodeId,
    string OwnerIdentity,
    long FencingToken,
    int SegmentIndex,
    int SourceFileIndex,
    long SourceOffsetSamples,
    long Samples,
    string SourceSha256,
    string ContentSha256);

/// <summary>
/// Owned native ASR segment response. Disposing the stream also disposes the
/// underlying HTTP response; callers must not keep the stream after disposal.
/// </summary>
public sealed class NativeAsrSegmentDownload : IAsyncDisposable, IDisposable
{
    private int _disposed;

    internal NativeAsrSegmentDownload(NativeAsrSegmentHeaders headers, Stream content)
    {
        Headers = headers;
        Content = content;
    }

    public NativeAsrSegmentHeaders Headers { get; }

    public Stream Content { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Content.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Content.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record NoteMaterialFileHeaders(
    string CacheControl,
    string ContentDisposition,
    string FileName,
    string Sha256);

/// <summary>
/// Owned note material response. Disposing the stream also disposes the
/// underlying HTTP response; callers must not keep the stream after disposal.
/// </summary>
public sealed class NoteMaterialFileDownload : IAsyncDisposable, IDisposable
{
    private int _disposed;

    internal NoteMaterialFileDownload(NoteMaterialFileHeaders headers, Stream content)
    {
        Headers = headers;
        Content = content;
    }

    public NoteMaterialFileHeaders Headers { get; }

    public Stream Content { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Content.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Content.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class BoundedHttpResponseStream : Stream
{
    private readonly Stream _inner;
    private HttpResponseMessage? _response;
    private readonly long _maxBytes;
    private readonly string _scope;
    private long _bytesRead;
    private int _disposed;

    public BoundedHttpResponseStream(
        Stream inner,
        HttpResponseMessage response,
        long maxBytes,
        bool ownsResponse,
        string scope = "native ASR segment")
    {
        _inner = inner;
        _response = ownsResponse ? response : null;
        _maxBytes = maxBytes;
        _scope = scope;
    }

    public override bool CanRead => !IsDisposed && _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        var read = _inner.Read(buffer, offset, count);
        Account(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCoreAsync(disposing: true).AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync(disposing: true).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeCoreAsync(bool disposing)
    {
        if (!disposing || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _response, null)?.Dispose();
        }
    }

    private void Account(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        _bytesRead = checked(_bytesRead + bytes);
        if (_bytesRead > _maxBytes)
        {
            throw new InvalidDataException(
                $"{_scope} response exceeded the configured content limit");
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}
