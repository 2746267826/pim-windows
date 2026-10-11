# Windows 客户端测试

这些测试原先位于主仓的 `tests/Pim.UnitTests/ClientWindows/`，随仓库拆分归入本仓。

## 当前状态：已在 CI 中实际执行

拆仓后它们一度只是"被搬过来的源文件"——没有任何工程引用，等于**不被任何地方执行**。
现已建立测试工程并接入 CI 门禁（`gate` 会检查，测试不过就红）：

| 工程 | TFM | 覆盖用例 | 运行环境 |
| --- | --- | --- | --- |
| `../Pim.Client.Tests` | `net8.0` | 28 个文件 | Linux（`test-client-core` job） |
| `../Pim.Client.App.Tests` | `net8.0-windows` | 12 个文件 | Windows（`test-client-app` job） |
| `../../src/client-shell-windows/Pim.Shell.Tests` | `net8.0` / `net8.0-windows` | 8 个文件（含本目录的 `ShellHealthContractWireTests.cs`） | Windows（`build-shell-windows.yml` 的 `dotnet test`） |

（上表的文件数由 `scripts/ci/check-audit-guards.mjs` 的 `test-inventory` 检查对照各 csproj 的
`Compile` 清单机械核对，改清单必须同步改这里。）

**为什么拆多个工程**：守护程序的核心逻辑（状态机、上报闸门、通道、keystats、契约线上字节等）
不依赖 WPF/WinForms，保持 `net8.0` 就能在 Linux runner 上跑——这些正是最容易回归的部分，
让它们在**每次 PR** 上都真正执行，比"只在 Windows 上跑"更有价值。依赖托盘图标、状态中心窗口、
单实例守卫等 UI 组件的用例才放进 `net8.0-windows` 工程。

**各工程的 `Compile` 项都是显式列的**：清单即范围。新增用例文件后必须显式加入工程，
否则它会静静地不参与编译与执行——这正是审计 F-10 要修掉的老问题。

## 孤儿用例（审计 F-10）已在 round5 处置完毕

round4 独立 review 确认分母是 **16 条用例 = 可移植 10 / 后端依赖 6**（按用例级归属，
不是按文件级）。round5 的处置：

- **可移植 10 条**已迁入参与编译的工程并真实执行（迁移对照表见
  `BACKEND-OWNED-CASES.md` §1）：`BusinessDayUtilsClientTests.cs`、
  `DaemonHeartbeatReporterClientTests.cs`、`PlannedOfflineReporterClientTests.cs`、
  `SuspendGapClientTests.cs`（后者的 2 条来自 `Wo398SuspendGapTests.cs` 行 66/93，
  它们只依赖客户端类型）。
- **后端依赖 6 条**（引用 `pim-api` 仓的 `Pim.Core` / `Pim.Core.Operations` /
  `Pim.Core.Invariants` / `Pim.Module.PcTracker`）在本仓无法编译，因此**不伪造客户端验证**：
  逐条登记归属与真实验证条件的 runbook 见 `BACKEND-OWNED-CASES.md`，状态保持 `NOT-VERIFIED`。
- 原 4 个孤儿文件已删除（不再有"不被编译的用例文件"）；`scripts/ci/test-orphan-test-coverage.mjs`
  会机械检查「本目录每个 `.cs` 都被某个工程编译」+「迁移后的 10 条以 `[Fact]` 方法存在于编译中的文件」。

## 契约线上字节用例（审计 F-03）

`ContractSchema.cs`（三个测试工程共用的契约读取/求值器）+
`ContractWireShapeTests.cs`（F-01/F-02 与 6 个 operation）+
`CoreEndpointContractWireTests.cs`（`auth/login`、`auth/refresh`、`pc/tracker/health`）+
`ShellHealthContractWireTests.cs`（`/health`）+ `DaemonUpdateCheckContractWireTests.cs`
（`/api/client/shell/latest`）覆盖 11 个生产 operation 的**全部**分母，判据是
「生产入口 → 记录型 handler 捕获线上 method/路径/字节 → 对照 `contract/openapi.json`」。
`scripts/ci/test-contract-coverage.mjs` 机械检查这个分母与覆盖。

## 宿主会话用例（审计 F-11）分布

F-11 的用例按“能在哪一层真实执行”分开，避免把纯函数绿写成平台通过：

| 文件 | 工程 / 环境 | 覆盖 |
| --- | --- | --- |
| `HostSessionRedTests.cs` | `Pim.Client.Tests`（Linux） | 规格的红→绿判据：常量标记脚本、wire 白名单、请求头决策、导航决策、协调器单飞/代次，以及**生产出口令牌 canary**（`EmbeddedWebViewHost` 不再有把令牌交给页面的形态） |
| `HostSessionBridgeTests.cs` | `Pim.Client.Tests`（Linux） | `HostSessionChannel` 的真实行为：来源校验、非法帧丢弃、`host.state` 字段、`page.logout`、刷新单飞与**真实 HTTP 一次 `/auth/refresh`**、代次作废、`AuthService.Logout` 通知宿主会话 |
| `WebViewCredentialBoundaryTests.cs` | `Pim.Client.Tests`（Linux） | 源比较 / API 路径 / wire 的纯函数边界 |
| `HostSessionWindowsHarnessTests.cs` | `Pim.Client.App.Tests`（**Windows runner**） | 真实 WebView2（隐藏离屏窗口 + 随机端口本地 fake API）：常量标记与页面无令牌、`fetch` 上的 Bearer 作用域、`host.state` 双向桥、`NavigationStarting` / `NewWindowRequested`、宿主退出通知、DPAPI `CurrentUser` 往返 |

只有最后一档能证明 WebView2 / DPAPI 的**运行时**行为；Linux 上的编译与纯函数证据不替代它。
托盘菜单与登录窗口的人机交互仍保持 NOT-VERIFIED（N-05）。
