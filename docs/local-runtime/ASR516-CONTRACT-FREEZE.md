# #516 Windows 节点契约冻结清单 / Contract freeze checklist

状态：`BLOCKED`，等待 `pim-api` 唯一 OpenAPI 导出。本文是 Windows worker 的消费需求，不是契约、
不是已实现接口，也不授权在客户端手写路径、DTO 或枚举。只有在 `#539` 合并并正常合并 master 后，
由后端导出实际 OpenAPI；Windows 随后运行 `scripts/ci/refresh-contract.sh` 并通过生成物接入
`ApiClient`。

## 必须冻结的 worker 闭环

当前账号的 Windows worker 需要完成以下闭环；任何一环没有真实契约，都不接入正式网络调用：

1. 登记节点和已校验模型；
2. 周期心跳并提交资源快照；
3. 用稳定幂等键 claim；
4. 获取或续租 lease，并接收停止信号；
5. 在 lease/fence 边界内取得固定输入和分片清单；
6. 逐片回执、终态回执；
7. 节点撤回/账号切换后，旧能力与旧 lease 不可继续消费。

以下操作名与路径是给后端导出时对齐语义的候选，不是客户端契约常量。最终 HTTP 方法、路径、
operationId、字段名、可空性、枚举和错误码都必须来自唯一 OpenAPI。

| 语义操作 | 候选路径 | Windows 用途 |
| --- | --- | --- |
| `RegisterNode` | `POST /api/v1/native-notes/asr-routing/nodes/register` | 绑定当前账号，登记稳定节点身份、已验证模型和资源能力 |
| `HeartbeatNode` | `POST /api/v1/native-notes/asr-routing/nodes/{nodeId}/heartbeat` | 刷新当前账号节点能力、资源和可用时间；不得改变 owner |
| `WithdrawNode` | `POST /api/v1/native-notes/asr-routing/nodes/{nodeId}/withdraw` | 账号切换/退出前撤回 claimable 能力；活动 lease 保持 `execution-unknown`，不得自动重派 |
| `ClaimTask` | `POST /api/v1/native-notes/asr-routing/leases/claim` | 当前账号节点领取一个固定 attempt；重复幂等键返回同一 live lease |
| `RenewLease` | `POST /api/v1/native-notes/asr-routing/leases/{leaseId}/renew` | 校验 lease token + fence，续期并返回服务端停止/取消状态 |
| `GetAttempt` | `GET /api/v1/native-notes/asr-routing/attempts/{attemptId}` | 读取固定 attempt 快照、状态和 stop 标志 |
| `GetInputManifest` | `GET /api/v1/native-notes/asr-routing/attempts/{attemptId}/input` | 在 owner + node + lease + fence 边界内取得固定输入摘要与分片描述 |
| `GetInputSegment` | `GET /api/v1/native-notes/asr-routing/attempts/{attemptId}/input/segments/{segmentIndex}` | 在相同边界内流式下载指定音频分片；不接受任意本机/远端路径 |
| `Acknowledge` | `POST /api/v1/native-notes/asr-routing/attempts/{attemptId}/acks` | 逐片或终态回执；重复相同回执幂等，旧 fence 不得覆盖 |

若后端选择合并 input manifest 与 claim 响应，仍必须保留按分片独立、受 lease/fence 约束的内容读取，
避免把整段音频塞入 JSON 或暴露无需鉴权的 URL。

输入内容端点必须绑定 claim 返回的不可变 `attemptId + owner + node + version + segmentIndex +
sha256` 以及可信 API origin。即使服务端返回 `ContentEndpoint`/下载 URL，客户端也不能跟随任意
origin、任意 redirect 或外部 URL 并携带 API token；内容请求只允许生成或批准的 explicit endpoint，
响应摘要必须与 claim snapshot 一致。`ApiClient` 的显式 endpoint 边界必须保留，不增加裸 URL HTTP 入口。

## 必须进入 schemas 的不可变字段

### 节点身份与能力

- 稳定 `nodeId`、Windows 机器显示名、当前认证账号绑定的 owner（owner 由 token 决定，不接受客户端覆盖）。
- 账号边界必须是 `serverOrigin + ownerIdentity`，不能只比较用户 GUID。同一个 GUID 换 API server
  也必须撤销旧 origin 的运行、节点能力和 lease；旧 lease token 不得发送到新 origin。
- `runtimeId`、`runtimeVersion`、`modelId`、`modelRevision`、`modelSha256`、`modelVerified`。
- 能力集合必须区分 ASR、语言/格式等实际支持项，不能只用任意自由文本声称可用。
- 资源快照：逻辑 CPU 核数、CPU load（未知必须可表达）、总/可用内存、GPU 是否必需、
  总/可用 VRAM（未知必须可表达）、电源状态和采样时间。
- `lastHeartbeatAt`、`serverNow`、`claimable`、不可领取原因码。

`modelVerified=true` 必须对应已通过目录 SHA-256 校验的模型；仅存在文件或用户填写布尔值不算验证。

### Claim 与 lease

- `attemptId`、`taskId`、`versionId`、固定 `inputSha256`、`route=local`、语言、分片数量和分片清单。
- 不可变路由快照：`configRevision`、`consentVersion`、`localModelId`、`localModelRevision`、
  `localModelSha256`（如后端允许空值，必须明确兼容旧记录）。
- `leaseId`、不透明 `leaseToken`、单调 `fencingToken`、`issuedAt`、`expiresAt`、`serverNow`。
- 无任务、节流、节点不可领取、lease 过期均需可区分的状态与原因，不得用空成功混淆。
- 同一 owner + node + 幂等键重放必须返回相同 live lease；新幂等键不得在已有活动 lease 时铸造第二个 lease。
- 服务端不得因为 lease 过期就宣称本机进程已停止或自动重派；attempt 进入 `Unknown`/`execution-unknown`，
  只有后续可审计核对才能恢复。

### 分片回执

逐片回执至少需要：

- `leaseId`、`leaseToken`、`fencingToken`、`attemptId`、`segmentIndex`；
- 结果、识别文本、分片起止样本/时间、输出摘要（如后端不接收文本，则必须指定受鉴权的文本上传接口）；
- provider handle/运行耗时（可选）、稳定错误码、可展示错误消息、客户端完成时间。

终态回执必须携带同一 lease/fence 身份和 `complete|fail`。服务端必须 CAS：
错误 owner、旧 fence、过期 lease、迟到回执、同一分片不同内容、重复终态均不得覆盖新 attempt/result。
完成前必须验证全部分片成功；失败与未知不得被折叠成成功。

### 账号切换 / 节点撤回

- 新账号不能读取、续租、claim 或 ack 旧账号节点/lease。
- 节点 ID 已被其他 owner 占用时返回稳定冲突错误，不静默改绑。
- withdraw 成功后节点立即不可 claim；已有活动 lease 必须保持可审计状态，不能借旧 lease 让新账号继续消费。
- Windows 在发出 withdraw/退出前先停止本工具签发的进程树，再撤回能力；网络失败则保留本地证据并阻止新账号运行。

账号边界测试至少覆盖：

- 相同 user GUID、不同 server origin：旧 run 被停止，旧节点撤销，旧 lease 不能续租或 ack；
- 旧 origin 的 token 不得被发送到新 origin，反之亦然；
- claim snapshot 的 attempt/version/segment/hash 任一不匹配时拒绝运行和回执；
- 服务端返回外部 `ContentEndpoint`、跨 origin redirect 或可携带 token 的非批准 URL 时 fail-closed；
- 旧 owner/fence 的迟到 ack、重复但不同内容 ack 不能覆盖新结果。

## 幂等、时钟和错误边界

- `serverNow` / `expiresAt` 使用 UTC 且以服务端时钟为准；客户端只做保守提前续租。
- claim 和 ack 的幂等范围必须明确为 `owner + node + operation + idempotencyKey`（ack 还含 attempt/segment）。
- 必须冻结以下区分的错误语义：未认证、owner 不匹配、节点不存在、lease 不存在、fence 冲突、
  lease 过期、重复但内容不同、节点不可领取、输入摘要不匹配。
- 任何 2xx 都不能仅凭 HTTP 成功表示“任务成功”；客户端同时校验业务 envelope、状态、attempt/fence 和摘要。

## Windows 接入门

1. `#539` 合并并正常合并 master。
2. `pim-api` 导出唯一 OpenAPI，提供不可变 source commit 与 SHA-256。
3. Windows 更新契约快照，在 `scripts/contract/client-operations.json` 只登记实际使用的 operations。
4. 运行 `scripts/ci/refresh-contract.sh`，禁止手改生成物。
5. 用生成 DTO/operation 实现 register → heartbeat → claim → renew → input → ack → withdraw。
6. 合成 API 测试通过且 `CI Gate` 红项全部真实修复后才允许合并。

本清单不授权下载真实模型、启动真实推理、发送真实/批量音频或产生任何付费调用；相关能力继续保持
`NOT-VERIFIED`。
