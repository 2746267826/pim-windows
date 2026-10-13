# Windows 客户端测试

这些测试原先位于主仓的 `tests/Pim.UnitTests/ClientWindows/`，随仓库拆分归入本仓。

## 当前状态：已在 CI 中实际执行

拆仓后它们一度只是"被搬过来的源文件"——没有任何工程引用，等于**不被任何地方执行**。
现已建立两个测试工程并接入 CI 门禁（`gate` 会检查，测试不过就红）：

| 工程 | TFM | 覆盖用例 | 运行环境 |
| --- | --- | --- | --- |
| `../Pim.Client.Tests` | `net8.0` | 18 个文件 / 127 个用例 | Linux（`test-client-core` job） |
| `../Pim.Client.App.Tests` | `net8.0-windows` | 9 个文件 | Windows（`test-client-app` job） |

**为什么拆两个工程**：守护程序的核心逻辑（状态机、上报闸门、通道、keystats 等）不依赖
WPF/WinForms，保持 `net8.0` 就能在 Linux runner 上跑——这些正是最容易回归的部分，
让它们在**每次 PR** 上都真正执行，比"只在 Windows 上跑"更有价值。依赖托盘图标、
状态中心窗口、单实例守卫等 UI 组件的用例才放进 `net8.0-windows` 工程。

**两个工程的 `Compile` 项都是显式列的**：清单即范围。新增用例文件后必须显式加入工程，
否则它会静静地不参与编译与执行——这正是本次要修掉的老问题。

`ContractBoundaryTests.cs`（接口边界契约化）在 `Pim.Client.Tests` 里：它用桩传输层真实装配 `ApiClient`，
逐个 operation 核对「HTTP 方法 + 路径 + 请求体字段集合」与 `contract/openapi.json` 一致，
并用反射/源码形态断言 `ApiClient` 不再暴露裸字符串 endpoint 或 `object` 请求体。

## 尚未纳入的 4 个文件

以下用例引用了后端程序集（`Pim.Core` / `Pim.Module.PcTracker`，位于 `pim-api` 仓），
在本仓无法编译：

- `BusinessDayUtilsTests.cs`
- `DaemonHeartbeatReporterTests.cs`
- `PlannedOfflineReporterTests.cs`
- `Wo398SuspendGapTests.cs`

恢复它们需要后端以 NuGet 包的形式提供共享契约（如 `Pim.Contracts`），或把这几条
用例改为只针对客户端侧行为断言。在那之前它们被排除在编译之外。
