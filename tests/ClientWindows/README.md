# Windows 客户端测试（待启用）

这些测试原先位于主仓的 `tests/Pim.UnitTests/ClientWindows/`，用于验证 Windows 守护程序与壳的行为，
随仓库拆分归入本仓。

**当前状态**：仅迁入源文件，尚未建立测试工程。启用需要：

1. 新建测试项目（xUnit），引用本仓的 `src/client-windows/Pim.Client.Core`、`Pim.Client.App`、
   `Pim.Client.Infrastructure`；
2. 其中 4 个用例还引用了后端程序集（`Pim.Core`、`Pim.Module.PcTracker`）——需要后端以 NuGet 包形式
   提供共享契约（`Pim.Contracts` 之类）后，这些用例才能恢复运行；在此之前它们会被排除在编译之外。

在测试工程建立之前，本目录不参与任何构建。
