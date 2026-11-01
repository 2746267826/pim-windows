# #516 Windows 本地 ASR 生命周期与后台处理 / Windows local ASR lifecycle and background processing

> 发布验收记录，产品源码冻结在 `98f6295316a0989bc7cb318b00ba288b5637a9e9`。
> 契约已通过官方 `scripts/ci/refresh-contract.sh` 从已合并的 actual `pim-api` master
> `84ef802e7c85a1b6c3017c0c274a108fea57f9d2` 刷新，并已通过生成的 focused/full tests。
>
> Release evidence. Product source is frozen at
> `98f6295316a0989bc7cb318b00ba288b5637a9e9`. The contract was refreshed through
> `scripts/ci/refresh-contract.sh` from merged actual `pim-api` master
> `84ef802e7c85a1b6c3017c0c274a108fea57f9d2`, then the generated focused/full tests passed.

## 概述 / Overview

本 PR 为 Windows 客户端增加本地运行时/模型的安全安装与校验、受控子进程清退、资源退避、
内部 ASR worker 状态机和 WPF 后台处理入口。公共 HTTP 边界继续来自唯一 OpenAPI 契约生成物，
不手写路径或公共 DTO；应用不会自动下载或运行未知模型。

This PR adds safe local runtime/model installation and verification, owned-process cleanup,
resource backoff, an internal ASR worker state machine, and a WPF background-processing entry
for the Windows client. The public HTTP boundary remains generated from the single OpenAPI
contract; no paths or public DTOs are hand-written, and the app never auto-downloads or runs an
unknown model.

---

## 技术修改 / Technical changes

- 增加 manifest 驱动的 runtime/model 安装：HTTPS 来源、大小与 SHA-256 校验、原子发布、
  取消/重启恢复、账号归属、磁盘检查，以及 ZIP 路径逃逸、reparse/symlink、大小写冲突和
  zip-bomb 预算拒绝。 / Adds manifest-driven runtime/model installation with HTTPS source,
  size/SHA-256 checks, atomic publication, cancellation/restart recovery, account ownership,
  disk checks, and fail-closed ZIP extraction for traversal, reparse/symlink, case collisions,
  and zip-bomb budgets.
- 增加受控 Windows runner：`CREATE_SUSPENDED -> AssignJob -> ResumeThread`，仅继承标准输入/
  输出/错误句柄，按可验证 owned identity 清退进程树，并保留有界 pipe drain 与崩溃恢复证据。
  / Adds a controlled Windows runner using
  `CREATE_SUSPENDED -> AssignJob -> ResumeThread`, inheriting only standard I/O handles,
  cleaning only verifiably owned process trees, and retaining bounded pipe draining and crash
  recovery evidence.
- 增加可配置资源快照与退避：真实 CPU 连续采样、内存/VRAM/电源字段、required-but-unknown
  fail-closed、自动退避上限，以及手动运行仍经过资源检查。 / Adds configurable resource
  snapshots and backoff: real consecutive CPU sampling, memory/VRAM/power fields,
  fail-closed required-but-unknown resources, capped automatic backoff, and resource checks
  on manual runs.
- 增加内部 ASR worker 与 typed transport：claim/input/runtime/ACK、完整 server base address
  凭据隔离、fence/lease/token/attempt/version/index/source/content 校验、sparse segment、
  lease deadline、scope 切换取消/withdraw、重启 `ExecutionUnknown` 不盲目复用。 /
  Adds the internal ASR worker and typed transport: claim/input/runtime/ACK, credential
  isolation by full server base address, fence/lease/token/attempt/version/index/source/content
  validation, sparse segments, lease deadlines, scope-switch cancellation/withdrawal, and
  restart `ExecutionUnknown` without blind reuse.
- 增加安装实例级 node identity、catalog 冲突 fail-closed、显式模型选择、延迟构造真实
  coordinator/runner、DI 接线和 WPF 自动/手动处理入口；合法调度偏好跨重启保留，未登录或
  模型未安装时只显示不可用，不发网络请求。 / Adds installation-scoped node identity,
  fail-closed catalog conflict handling, explicit model selection, delayed concrete
  coordinator/runner construction, DI wiring, and WPF manual/automatic processing. Valid
  scheduler preferences survive restart; signed-out or uninstalled states stay disabled
  without network calls.
- 契约改动只通过 generator 机械生成：支持实际 7 个 ASR operation、typed header/binary
  response metadata、PathBase 和安全 redirect/content endpoint 边界；没有改 workflow，
  没有手改 generated 文件。 / Contract changes are generated mechanically: actual seven ASR
  operations, typed header/binary response metadata, PathBase, and safe redirect/content
  endpoint boundaries. No workflow is changed and no generated file is hand-edited.

## 功能变化 / Feature changes

- Windows 状态中心可在“本地模型”区域刷新候选目录、显式安装/校验/卸载 runtime 与 model，
  并为当前账号选择已安装且新鲜的本地 ASR 模型。 / In the Windows status center, users can
  refresh the local catalog, explicitly install/verify/uninstall runtime and model artifacts,
  and select a fresh installed ASR model for the current account.
- 新增 ASR 后台处理区：自动处理开关、立即处理、停止并清退、可见状态与失败熔断；账号或
  server/PathBase 切换会先停止旧 scope。 / Adds the ASR background-processing section with
  automatic toggle, run-once, stop-and-cleanup, visible state, and failure circuit breaker;
  account or server/PathBase changes stop the old scope first.
- 默认不自动下载任何大模型，不运行未知 binary。未选择/未安装模型时显示明确的不可用原因，
  不会启动真实推理。 / The default behavior downloads no large model and runs no unknown
  binary. Missing selection/installation shows an explicit unavailable reason and starts no
  real inference.

## 如何体验 / How to try it

1. 登录 Windows 客户端，打开状态中心的“本地模型”区域并刷新候选目录。 / Sign in to the
   Windows client, open the Local Models section in Status Center, and refresh the catalog.
2. 对目录中的 runtime/model 执行显式安装与校验；应用不会在后台自动下载。 / Explicitly
   install and verify a catalog runtime/model; nothing is downloaded automatically.
3. 选中模型并点击“用于本地 ASR”。当前账号已完成安装证明且 ASR 服务可用时会注册节点；
   否则显示配置或连接不可用原因。 / Select the model and choose “Use for local ASR”. A node
   registers only when the current account has fresh installed proof and the ASR service is
   available; otherwise an explicit configuration/connection reason is shown.
4. 在“ASR 后台处理”区手动处理一次或启用自动处理。停止、退出、切换账号或 server/PathBase
   时，旧运行和任务凭据会被取消/撤回。 / Use run-once or enable automatic processing in the
   ASR background section. Stop, exit, account switch, or server/PathBase change
   cancels/withdraws the old run and task credentials.

真实模型、真实音频、真实硬件性能和真实设备端到端推理尚未验证；本 PR 不声称开箱离线可用。
Real models, audio, hardware performance, and device end-to-end inference are not verified;
this PR does not claim out-of-box offline availability.

## 测试 / Tests

下列结果均来自产品源码冻结提交 `98f6295316a0989bc7cb318b00ba288b5637a9e9`、并在官方刷新
actual `pim-api` master `84ef802e7c85a1b6c3017c0c274a108fea57f9d2` 后的 Windows 执行：
All results below were executed on Windows after the official refresh from actual `pim-api`
master `84ef802e7c85a1b6c3017c0c274a108fea57f9d2`; the product source commit is
`98f6295316a0989bc7cb318b00ba288b5637a9e9`:

- App tests：`92 passed / 0 failed`。 / App tests: `92 passed / 0 failed`.
- Core tests：`295 passed / 0 failed`。 / Core tests: `295 passed / 0 failed`.
- Runner/coordinator/backlog/catalog focused tests：`22 passed / 0 failed`。 / Focused
  runner/coordinator/backlog/catalog tests: `22 passed / 0 failed`.
- Contract boundary focused tests：`14 passed / 0 failed`。 / Contract boundary focused tests:
  `14 passed / 0 failed`.
- Windows Release daemon publish：
  `dotnet publish src/client-windows/Pim.Client.App/Pim.Client.App.csproj -c Release --self-contained false`
  成功，产出 `Pim.Client.App.exe`；未启动应用。 / Windows Release daemon publish succeeded
  and produced `Pim.Client.App.exe`; the app was not launched.
- 官方 `scripts/ci/check-contract-csharp.sh` 成功；`ContractModels.cs` 与
  `ContractOperations.cs` 均与 `contract/openapi.json` + operation 清单逐字节一致。 /
  Official `scripts/ci/check-contract-csharp.sh` passed; both generated artifacts match the
  contract snapshot and operation manifest byte-for-byte.
- 官方 `scripts/ci/check-contract-freshness.sh` 成功；快照逐字节匹配 actual API master，
  `407` operations、`705` component schemas，`1202774` 字节，SHA-256
  `e5c555e2864e182f98242b0bbe50b156f755e9ea18dd5c57724518edd6bd8d76`。 /
  Official `scripts/ci/check-contract-freshness.sh` passed; the snapshot matches actual API
  master byte-for-byte with 407 operations, 705 component schemas, 1,202,774 bytes, and
  SHA-256 `e5c555e2864e182f98242b0bbe50b156f755e9ea18dd5c57724518edd6bd8d76`.
- Actual API merge provenance：PR `#549` head `14efd083ea42dd2042f833ffc5e59b33f4d02968`，
  merge `84ef802e7c85a1b6c3017c0c274a108fea57f9d2`，CI `41223048668` 的
  Web/API/Docker/Gate 均成功。 / Actual API merge provenance: PR `#549` at head
  `14efd083ea42dd2042f833ffc5e59b33f4d02968` merged as
  `84ef802e7c85a1b6c3017c0c274a108fea57f9d2`; CI `41223048668` passed
  Web/API/Docker/Gate.
- `git diff --name-only origin/master...HEAD` 未见 `bin/`、`obj/`、`TestResults/`、
  `.codex-tmp/`、credentials、`.env` 或 workflow 变更。 / The tracked diff from master
  contains no `bin/`, `obj/`, `TestResults/`, `.codex-tmp/`, credentials, `.env`, or workflow
  changes.

## 需求与验收映射 / Requirement & Acceptance Mapping

| 编号 | 实现（文件/函数） | 验证方式 | 结果 | 证据 |
|---|---|---|---|---|
| #516 runtime/model manifest 与安全安装 | `RuntimeInstallStore`、`ModelInstallStore`、`LocalRuntimeCatalogParser` | 合成 manifest/ZIP/摘要/恢复/取消 negative tests | PASS | App 92/92、Core 295/295；`RuntimeInstallStoreTests`、`ModelInstallStoreTests` |
| #516 受控进程与 owned cleanup | `ControlledProcessRunner`、`WindowsSuspendedProcessLauncher`、`WindowsJobObject` | helper 进程成功/失败/超时/取消/立即 spawn/root exit/身份不匹配/深安装路径 | PASS | `WindowsControlledProcessRunnerTests`、`ControlledProcessRunnerCoreTests` |
| #516 CPU/内存/VRAM 资源门禁与有界退避 | `CpuLoadSampler`、`ResourceRequirementEvaluator`、`RuntimeBackoff` | CPU 采样、GPU-only gate、required unknown、退避上限 | PASS | `CpuLoadSamplerTests`、`LocalRuntimeResourceBackoffTests`、`WindowsResourceSnapshotProviderTests` |
| #516 内部 ASR worker 状态机与 typed transport | `LocalAsrWorkerEngine`、`LocalAsrApiTransport`、`LocalAsrApiTransportTests` | sparse/fence/token/lease/content hash/scope switch/restart/redirect/header/audio-wav | PASS | Core 295/295；boundary 14/14 |
| #516 Windows coordinator、DI 与 WPF 后台处理 | `LocalAsrWorkerCoordinator`、`Startup`、`LocalAsrBacklogViewModel`、状态中心 | non-null composition、未安装 `InvalidConfiguration`、installation 隔离、偏好恢复、关闭 guard、dispose/scope race | PASS | App 92/92；focused 22/22 |
| #516 官方契约生成一致性与 Windows 发布构建 | generator/check、`Pim.Client.App.csproj` | official `check-contract-csharp.sh`、Release publish | PASS | 校验逐字节一致；`.codex-tmp/release-build/publish-final-84ef802e/Pim.Client.App.exe` |
| #516 真实首发模型、真实音频与设备端推理 | 保持 gated，无真实模型执行 | 未运行真实模型/音频/设备 | NOT-VERIFIED | 等待用户选定模型、已核验来源/hash、硬件与实机验收 |
| #516 actual merged API master 最终刷新 | `scripts/ci/refresh-contract.sh` | actual PR `#549` merge `84ef802e`；official generated + freshness checks | PASS | `contract/provenance.json`；快照 `1202774` bytes / `e5c555e...`；407 operations / 705 schemas |
