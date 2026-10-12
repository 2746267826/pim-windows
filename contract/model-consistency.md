# Windows 手写 DTO ↔ 契约一致性报告（刷新后）

- 仓：`pim-windows`，分支 `deepseek-linux/contract-refresh-master`，base = 本次 fresh `origin/master` `cbfcbeffc93a1cc522eec1c522860e2881778b40`
- 契约：fresh `contract/openapi.json`（2746267826/pim-api@940fe42face873e48c7c36af030d8ca838791471，938271 B，sha256 `4dd7eafb4938…`）
- 本报告**只做核对**，不改任何产品逻辑；不涉及 CI/gate 实施。

## 0. 结论摘要

1. 快照 258542 B / `1cf26197…`（旧来源 `pim-api@2f56dbb3`）→ 938271 B / `4dd7eafb4938…`（来源 `pim-api@940fe42face8`）。
2. Windows 侧**实际调用的 operation 只有 13 个**，全部存在于契约中，无悬空路径。
3. `Models/AuthDtos.cs` 里的 `CalendarResponse` / `EventResponse` / `TaskResponse` / `SearchResult` / `PagedResult`
   **在整个仓里没有任何引用**（连测试也没有）→ 它们是**死声明**，不能按「客户端正在线收发」来判。
   其中 `EventResponse` 的 `[JsonPropertyName("rrule")]` 与契约 `rRule` 大小写不一致 ——
   **实测结论：`HttpContent.ReadFromJsonAsync` 的默认 options 大小写不敏感，读 `rRule` 能对上 `rrule`**，
   所以这不是（也不会成为）读侧故障；写侧服务端是否接受 `rrule` 未运行服务，标 `NOT-VERIFIED`。
4. 真正需要跟进的：`/pc/keystats/upload` 的 `peakKPS`/`peakCPS` 与契约 required 的 `peakKps`/`peakCps` 大小写不一致；
   以及 `EndpointStatusDto` 缺 `appVersion`、`SiteEventDto.at` 契约未声明。
5. **本版为 R1 修订版（`R1-correction`，不重置 review 轮数）**：按独立复核 `reviews/android-windows-models-r1.md`
   把 keystats 大小写差异从「字段缺失」改为 **`WIRE_NAME_CASE_MISMATCH`**（客户端确实发了 `peakKPS`/`peakCPS`）、
   把 `rawJson` 的 `TYPE_MISMATCH` 作为**等价误报排除**、补 `rrule` 死声明条目、写明 probe runtime 与产品 target 关系、
   并把 13 个真实 operation 的 method/path/请求体/容器/security 全部机读化。**只改报告，不改产品与契约**；
   快照字节仍为 938271 B / `4dd7eafb…`。

## 1. 核对方法与口径

- 操作映射来自**真实调用点**（`ApiClient.cs` 的三个具名包装 + 各 Service 里传入的字面 endpoint），不是模型名匹配。
- 客户端 wire 名规则：`[JsonPropertyName]` 原样；没有该特性时由 Web 命名策略转 camelCase（probe 实测确认）。
- `ApiClient.cs:218` 用 `response.Content.ReadFromJsonAsync<T>(ct)`（无显式 options）；`:142` 用 `PostAsJsonAsync`。
  openapi.json 无生成物，本仓 DTO 全手写。

### 1.1 序列化器实测（真实 probe，非推断）

```
[options] Web defaults: PropertyNameCaseInsensitive=True NamingPolicy=JsonCamelCaseNamingPolicy
[READ  server field 'rRule' -> client [JsonPropertyName("rrule")]]  RRule=FREQ=DAILY   <- 读侧大小写不敏感，能对上
      control: 'dtStart'/'source' 精确同名 -> bound=True/True
[READ  same JSON with JsonSerializerOptions.Default]               RRule=<null>       <- 只有显式 Default 才大小写敏感
[WRITE DaemonHeartbeatRequest] {"deviceId":"dev1","daemonKind":"windows",…,"collectionPaused":false,…}
[WRITE WinEventResponse]       {"id":"e1","rrule":"FREQ=DAILY","dtStart":"…","source":null}
[READ {"rrule":3}]    JsonException: The JSON value could not be converted to System.String
[READ {"rrule":true}] JsonException: 同上
```

要点：写侧 `[JsonPropertyName("rrule")]` **不会被命名策略改写**，所以线上确实发 `rrule`；
读侧大小写不敏感意味着**同一个小写声明能吃下契约的 `rRule`**。
另外 positional record（`DaemonHeartbeatRequest` 等无特性的位置记录）按 camelCase 输出，与契约同名一致（实测）。

> **runtime 版本口径（M-7）**：本 packet 的 probe 跑在 **.NET 9.0.0**，而产品实际 target 是
> **`net8.0` / `net8.0-windows`**。R1 在 **.NET 8.0.31** 上重跑同一探针，输出与 .NET 9 **逐字一致**
> （读侧大小写不敏感、写侧 `[JsonPropertyName]` 原样、无特性位置记录 camelCase），因此三条语义结论在两个 runtime 上都成立。
> 独立复现日志：`reviews/android-windows-models-r1.logs/05-windows-dotnet-probe.log`。

**required 的读取规则（本版新增，必须严格遵守）**：契约里的 `required` 不区分方向；当属主 schema 同时标了
`readOnly: true` 时按 OpenAPI 3.0 语义 `required` **只对响应生效**（对称地 `writeOnly: true` 只对请求生效）。
detail.json 里每条 finding 都带 `contractField.{required,readOnly,writeOnly,nullable,default,enum,ref,type}`
与 `requiredSemantics.appliesTo`。本仓本轮命中的差异里**没有** `readOnly`/`writeOnly` 字段，属于设备/状态机数据，
不做「客户端少发 = 客户端 bug」的反向推断。

## 2. 操作级核对表（13 个真实 operation，全部存在）

| # | 调用点 | 方法 路径 | 请求模型→契约 schema | 响应 | security |
|---|---|---|---|---|---|
| 1 | `AuthService.LoginAsync:69` | `POST /api/v1/auth/login` | 匿名对象 {username,password} → `LoginRequest` | `ApiResponse<AuthResponse>` → `AuthResponse` | anonymous |
| 2 | `AuthService.RegisterAsync:82` | `POST /api/v1/auth/register` | 匿名对象 → `RegisterRequest` | `ApiResponse<AuthResponse>` → `AuthResponse` | anonymous |
| 3 | `AuthService.RefreshCore:281` | `POST /api/v1/auth/refresh` | 匿名对象 → `RefreshRequest` | `ApiResponse<AuthResponse>` → `AuthResponse` | anonymous |
| 4 | `DaemonHeartbeatReporter:18` | `POST /api/v1/daemon/heartbeat` | `DaemonHeartbeatRequest` → `DaemonHeartbeatRequest` | 丢弃（`PostAsync<object>`） | bearerAuth |
| 5 | `PlannedOfflineReporter:16` | `POST /api/v1/daemon/planned-offline` | `PlannedOfflineRequest` → `PlannedOfflineRequest` | 丢弃 | bearerAuth |
| 6 | `ApiClient.cs:112` | `GET /api/v1/endpoints` | — | `ApiResponse<List<EndpointStatusDto>>` → `EndpointStatusDto` | bearerAuth |
| 7 | `ApiClient.cs:117` | `GET /api/v1/endpoints/{deviceId}/collection-quality` | — | → `EndpointCollectionQualityDto` | bearerAuth |
| 8 | `ApiClient.cs:125` | `POST /api/v1/endpoints/{deviceId}/notification-actions` | `EndpointNotificationActionRequestDto` → `EndpointNotificationActionRequest` | → `EndpointNotificationActionResponse` | bearerAuth |
| 9 | `NativeTrackerService.cs:252,692` | `POST /api/v1/pc/tracker/upload` | `TrackerEventsUploadRequest` → 同名 | `ApiResponse<int>` → `Int32ApiResponse` | bearerAuth |
| 10 | `NativeTrackerService.cs:790` | `POST /api/v1/pc/browser-tt/upload` | `SiteEventsUploadRequest` → 同名 | `ApiResponse<int>` | bearerAuth |
| 11 | `NativeTrackerService.cs:867` | `POST /api/v1/pc/tracker/health` | `TrackerHealthRequest` → 同名 | `ApiResponse<string>` → `StringApiResponse` | bearerAuth |
| 12 | `KeyStatsCollectorService.cs:136` | `POST /api/v1/pc/keystats/samples` | `KeystatsSampleUploadPayload` → `KeystatsSampleUploadRequest` | `ApiResponse<string>` | bearerAuth |
| 13 | `KeyStatsCollectorService.cs:137` | `POST /api/v1/pc/keystats/upload` | `KeyStatsSnapshot` → `KeystatsUploadRequest` | `ApiResponse<string>` | bearerAuth |

另有**不走 ApiClient** 的链路：`GET /health`（`StatusWindow.xaml.cs:217-230`，静态无鉴权 `Http`）与内嵌 SPA 页面，契约均为匿名 ✅。

## 3. 逐条差异

### 3.1 `EventResponse.rrule` 大小写 —— 死声明，读侧实测无影响

- 契约 `EventResponse.properties.rRule` = `{type: string, nullable: true}`；契约里不存在 `rrule`。
- 客户端 `Models/AuthDtos.cs:88-89` `[JsonPropertyName("rrule")] public string? RRule`。
- **关键校正**：`EventResponse` 在本仓（含 `Pim.Client.App`、测试）**没有任何使用点**，
  且 Windows 没有任何 calendar operation（`/calendar/*` 零调用）。所以这是**死声明**，
  不是「web 那种正在收发用错名」。
- probe 实测：真实读路径（`HttpContent.ReadFromJsonAsync`）**大小写不敏感**，`rRule` 能绑定到 `rrule` 声明。
- 判定：字段名声明与权威契约不一致（确定性文字差异），但**无实测运行时影响**；
  写侧是否被服务端接受 → `NOT-VERIFIED(runtime)`。风险类别：命名偏差 + unused declaration 观察。

### 3.2 `/pc/keystats/upload`：`peakKPS`/`peakCPS` vs 契约 required 的 `peakKps`/`peakCps`

- 客户端 `KeyStatsCollectorService.cs:203-250` 的 `KeyStatsSnapshot` 用
  `[JsonPropertyName("peakKPS")]` / `[JsonPropertyName("peakCPS")]` / `formattedMouseDistance` / `formattedScrollDistance`。
- 契约 `KeystatsUploadRequest.required` 含 **`peakKps` / `peakCps`**（camel），且**没有** `formattedMouseDistance`/`formattedScrollDistance`。
- 同一个客户端向 `/pc/keystats/samples` 发的 `KeystatsSampleUploadPayload` 用的却是契约里的 **`peakKPS`/`peakCPS`**
  —— 即**契约自己在两个端点间不一致**（`KeystatsSampleUploadRequest` 用大写，`KeystatsUploadRequest` 用小写）。
- 判定：**确定命名不一致**；服务端 ASP.NET Core 默认大小写不敏感绑定 → 倾向可用，但未运行服务，标 `NOT-VERIFIED(runtime)`。
- 风险类别：确定形状不符（文字层）/ 需 runtime NV（是否 400）。
- **机读归类订正（R1 M-3/M-4）**：旧版 detail.json 把这两条记成 `CONTRACT_FIELD_ABSENT_IN_CLIENT` + `client: null` +
  `request-omits-field`，与事实不符 —— 客户端**确实发送** `peakKPS`/`peakCPS`（`KeyStatsCollectorService.cs:244,247`
  的 `[JsonPropertyName]`），字段本身存在，只是 wire 名大小写不同。本版改为 **`WIRE_NAME_CASE_MISMATCH`**，
  并把该字段的契约侧（`peakKps`/`peakCps`，required）与客户端侧（`peakKPS`/`peakCPS`）写在同一条 finding 里；
  「大小写不同」与「字段缺失」不再混为一谈。原条目保留在 `detail.json.operations[...originalAsPublishedFindings]`。

### 3.3 `EndpointStatusDto` 缺 `appVersion`

- 契约有 `appVersion`（nullable string，非 required）；客户端 `EndpointDtos.cs:5-24` 未声明。
- 判定：响应 subset 观察（服务端多返回被忽略，不破坏）。

### 3.4 `SiteEventDto.at` 契约未声明

- 客户端 `TrackerModels.cs:174` `[JsonPropertyName("at")] public long? At`；契约 `SiteEventUploadDto` 只有 `kind/host/startMs/endMs/durationMs/date`。
- 判定：客户端多发一个字段（服务端忽略未知字段即可）；风险类别：容错范围不同 / 观察项。

### 3.5 `DaemonSourceState` 枚举：客户端只发 3 个值

- 契约 `DaemonSourceState = {enum: [Unknown, Available, Unavailable, Paused]}`。
- Windows 全仓只会产生 `Unknown`/`Available`/`Unavailable`（`DaemonHeartbeatReporter.cs:29-30`、`KeyStatsHealthProbe.cs`、`StatusWindow.xaml.cs:96-101`、`App.xaml.cs:302-306`），**从不产生 `Paused`**。
- 字段是 `string`，收到 `Paused` 不会解码失败；但 `StatusCenterEvaluator.Rate` 只把 `Available` 当正常，`Paused` 会落到「部分异常」。
- 判定：覆盖缺口（非破坏）。**字符串枚举 vs String 本身不算违规**。

### 3.6 其余为结构性等价，已排除

- `AuthResponse.ExpiresAt`（C# `DateTimeOffset`）↔ 契约 `date-time` string：probe 实测可正确解析（`dtStart=2026-10-12T10:00:00+00:00`），**等价**。
- `ApiResponse<T>` 无 `timestamp`：服务端多返回 `timestamp`，读侧忽略未知属性，**等价**。
- `TrackerEventForUpload` ↔ 契约 `TrackerEventDto`：**22/22 字段名、类型、可空性完全一致**。
  - 其中 `events[].rawJson`：契约是 `{"nullable": true}`、**没有 `type`**（untyped，等价于任意 JSON），
    客户端 `object?` 与之**功能等价**。旧版 detail.json 把它记成 `TYPE_MISMATCH` 是**误报**，
    且与本条「22/22 完全一致」自相矛盾；本版移入 `detail.json.excludedFindings`（保留原 finding 以便追溯）。
- rrule 死声明条目：本版在 detail.json 补了 `AuthDtos dead declarations (unreferenced)` 组（`EventResponse.rRule`
  ↔ 客户端 `rrule` 的 wire 名大小写差异 + 0 引用事实），使**只读 JSON 的下游也能看到 WIN-1**（R1 M-10）。
- `DaemonHeartbeatRequest` / `PlannedOfflineRequest` / `TrackerHealthRequest` / `SiteEventsUploadRequest` / `Endpoint*` 的位置记录与特性命名，probe 实测输出与契约同名（camelCase）。

## 4. 安全维度（逐 operation）

- Windows 只有**一个** `HttpClient`，`ApiClient.SetAccessToken` 把 Bearer 写到 `DefaultRequestHeaders`（`ApiClient.cs:100-104`），`ClearAccessToken` 清掉（`:106-109`，由 `AuthService.Logout` 调用）。
- 每个 operation 都走同一个 `SendWithAuthRetryAsync`，因此**没有任何 per-operation 的匿名/Bearer 切换**：
  - `login` / `register` / `refresh`：契约 `security: null`；客户端仍可能带上**上一次登录遗留的** Bearer（无实际危害，服务端忽略）。
  - 其余 10 个 operation：契约全部 `bearerAuth`，客户端在 `SetAccessToken` 之后自动带 Bearer；未登录时无 header。
- `GET /health`：`StatusWindow` 用静态 `Http`（无鉴权头），契约匿名 ✅。
- `opsKey`（`X-PIM-Ops-Key`）：Windows 全仓**零引用**，无调用即无偏差。
- **header origin 边界观察（需用户决策，不在本任务修复）**：`ApiClient.SetBaseUrl`（`ApiClient.cs:57-73`）
  **会保留旧的 `DefaultRequestHeaders.Authorization`**，而切换服务器（`AuthService.ServerUrl` setter / `StatusWindow.xaml.cs:376` / `MainShellWindow.xaml.cs:77`）并没有强制登出。
  即：换服务器后，旧服务器的 Bearer 可能被发往新服务器，直到重新登录/登出。
  证据为代码路径，未做网络抓包 → 风险类别：header origin boundary（`NOT-VERIFIED(runtime)`，但代码路径明确）。
- **计数口径（本版显式化，数值未改）**：13 个真实 operation = **10 `bearerAuth` + 3 匿名**（`login`/`register`/`refresh`），
  与契约逐条一致 —— R1 独立复算确认，**本轮不改**。机读：`detail.json.security` 与 `detail.json.operationAudit`（逐 op method/path/security/callSite）。
- **不冒称授权已验收**：以上均为源码静态连线，未跑 API/DB/真机 → 授权链路 `NOT-VERIFIED(runtime)`。

## 5. 覆盖清单

计数口径（可独立复算）：

- `handwrittenModelClasses = 43` = `src/client-windows/Pim.Client.Core/` 下的手写 DTO 类。
- `mappedToAnOperation = 15` = 能通过某个**真实调用点**映射到契约 schema 的类。
- `notMappedToAnyOperation = 28`；**`43 = 15 + 28` 自洽**（R1 独立复算确认，windows 侧无 android 那种算术问题）。
- 机器可读的逐 op 事实（method/path/callSite/请求体/容器/security）见 `detail.json.operationAudit`。

没有任何 operation 指向的 28 个（按文件位置分两类）：

**(a) 位于 `Pim.Client.Core/Models/` 但无 operation 使用 —— 死声明（全仓 grep 确认，连测试都没引用）：**

  - `CalendarResponse` （`src/client-windows/Pim.Client.Core/Models/AuthDtos.cs`）
  - `EventResponse` （`src/client-windows/Pim.Client.Core/Models/AuthDtos.cs`）
  - `TaskResponse` （`src/client-windows/Pim.Client.Core/Models/AuthDtos.cs`）
  - `SearchResult` （`src/client-windows/Pim.Client.Core/Models/AuthDtos.cs`）
  - `PagedResult` （`src/client-windows/Pim.Client.Core/Models/AuthDtos.cs`）

**(b) 本机状态 / 本地服务 / 服务内部类型（不是 pim-api wire DTO，不在契约核对范围，显式列出不冒充已审）：**

  - `ApiClient` （`src/client-windows/Pim.Client.Core/Services/ApiClient.cs`）
  - `ApiResponse` （`src/client-windows/Pim.Client.Core/Models/AuthDtos.cs`）
  - `AuthService` （`src/client-windows/Pim.Client.Core/Services/AuthService.cs`）
  - `BrowserConnection` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）
  - `BrowserHeartbeat` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）
  - `EndpointOperationBoundaryResult` （`src/client-windows/Pim.Client.Core/Services/EndpointCollectionBoundaryService.cs`）
  - `KeyStatsConvergencePlan` （`src/client-windows/Pim.Client.Core/Services/KeyStatsProcessManager.cs`）
  - `KeyStatsCounterSnapshot` （`src/client-windows/Pim.Client.Core/Models/KeyStatsHealthModels.cs`）
  - `KeyStatsFixResult` （`src/client-windows/Pim.Client.Core/Models/KeyStatsFixModels.cs`）
  - `KeyStatsFixSuggestion` （`src/client-windows/Pim.Client.Core/Models/KeyStatsFixModels.cs`）
  - `KeyStatsHealthResult` （`src/client-windows/Pim.Client.Core/Models/KeyStatsHealthModels.cs`）
  - `KeyStatsProcessInfo` （`src/client-windows/Pim.Client.Core/Models/KeyStatsHealthModels.cs`）
  - `KeyStatsStopResult` （`src/client-windows/Pim.Client.Core/Models/KeyStatsFixModels.cs`）
  - `NotificationActionRoute` （`src/client-windows/Pim.Client.Core/Services/NotificationActionRouter.cs`）
  - `PersistedToken` （`src/client-windows/Pim.Client.Core/Services/AuthService.cs`）
  - `SiteEventsRequest` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）
  - `StatsDto` （`src/client-windows/Pim.Client.Core/Services/KeyStatsLocalStatsClient.cs`）
  - `TrackerConfig` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）
  - `TrackerPageVisit` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）
  - `TrackerPersistentState` （`src/client-windows/Pim.Client.Core/Services/TrackerStateManager.cs`）
  - `TrackerSession` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）
  - `TrackerWindowInfo` （`src/client-windows/Pim.Client.Core/Models/TrackerModels.cs`）

**(c) 嵌套对象（在 operation 的字段级递归里已核对，不是没审）：**

- `KeyStatsAppStats` ↔ 契约 `AppStatEntry`：9/9 字段名与类型一致（`appName/displayName/keyPresses/leftClicks/rightClicks/middleClicks/sideBackClicks/sideForwardClicks/scrollDistance`），
  契约 required 的 9 项客户端全部具备。
- `TrackerEventForUpload` ↔ `TrackerEventDto`：22/22 完全一致；`SiteEventDto` ↔ `SiteEventUploadDto`：6/6 一致（客户端多 `at`）。
- `ApiResponse<T>` 是唯一的响应信封，已按结构逐字段核对（`code/message/data` ↔ 契约信封）。

> 归类说明：`KeyStatsAppStats` 已按字段级核对（上条），但它是**嵌套对象、不是任何 operation 的 root**，因此 `detail.json.coverage.notMappedToAnyOperation` 仍把它计入 28 —— 即 **28 = 5 (a) + 22 (b) + 1（`KeyStatsAppStats`）**，`43 = 15 + 28` 口径不变。

**未审名单（不是 wire DTO）已全部列出；无「应审未审」遗漏。**

## 6. 未验证 / 阻塞

| 项 | 状态 | 原因 |
|---|---|---|
| `/pc/keystats/upload` 的 `peakKPS` 是否被服务端绑定（3.2） | `NOT-VERIFIED(runtime)` | 未运行 API |
| 写侧 `rrule` 是否被服务端接受（3.1） | `NOT-VERIFIED(runtime)` | 无调用点 + 未运行 API |
| 换服务器后旧 Bearer 是否真的发出（4.） | `NOT-VERIFIED(runtime)` | 未做抓包/真机 |
| `/pc/tracker/health` 等是否对额外 required 敏感 | `NOT-VERIFIED(runtime)` | 同上 |

## 7. 来源哈希清单（source hash inventory）

```
repo                         pim-windows
fresh origin/master          cbfcbeffc93a1cc522eec1c522860e2881778b40
branch                       deepseek-linux/contract-refresh-master  (base = fresh origin/master)
worktree (isolated clone)    /home/coder/workspace/pim-wt/win-contract
upstream repo                2746267826/pim-api
upstream ref                 master
upstream resolved commit     940fe42face873e48c7c36af030d8ca838791471
ref resolution command       git ls-remote https://github.com/2746267826/pim-api.git refs/heads/master
snapshot before              258542 B  sha256 1cf261973833a0690b09d2f2eda916de382359636259ab8ed55a8bb38f1aec98
snapshot before git blob     fd3e464c73b8766de10984553e36e9c2ea2b3018
snapshot before declared src pim-api@master 2f56dbb3 (contract/README.md, stale)
snapshot after               938271 B  sha256 4dd7eafb4938818e13766e7177e1d05f45a4090643f9fe6dcd8d108c4f82af53
refresh command              (cd /home/coder/workspace/pim-wt/win-contract && bash scripts/ci/fetch-contract.sh)                       rc=0
refresh command (pinned)     (cd /home/coder/workspace/pim-wt/win-contract && PIM_CONTRACT_REF=940fe42face873e48c7c36af030d8ca838791471 bash scripts/ci/fetch-contract.sh)  rc=0
pinned == default bytes      true (both 938271 B / same sha256)
```

> 上游 `master` 仍会被后续移动。**未来 check 不应硬锁 940fe42…**；本次 refresh 固定它是为了留下可复现的 provenance。

---

机读详表：[`contract/model-consistency.detail.json`](model-consistency.detail.json)。
复现日志：`remediation/contract-refresh-20261012/android-windows-refresh-models/commandsrawlogs/`。
本版修订证据（R1 I/M 闭环）：`remediation/contract-refresh-20261012/android-windows-models-corrected/`。

**本报告只描述现状，不修产品逻辑；未提交、未推送、未开 PR。**
本版为 **R1 修订版（`R1-correction`）**，不重置 review 轮数；按
`remediation/contract-refresh-20261012/reviews/android-windows-models-r1.md` 的 I-1（口径）/M-3/M-4/M-7/M-10 修订，产品代码与 `contract/openapi.json` 一字未改。
