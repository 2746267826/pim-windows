# pim-windows

PIM 的 Windows 端：守护程序采集前台应用、窗口与键鼠活跃度；浏览器扩展同步活动标签页。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/client-windows/` | 守护程序（WPF / .NET，解决方案 `Pim.Client.Windows.slnx`；产品工程，不含测试工程） |
| `src/client-shell-windows/` | 旧 WPF 壳——**V2 的 Tauri 壳上线后退役** |
| `browser-extension/` | 浏览器扩展（WebExtension；Chrome MV3 与 Firefox MV2 两份产物均随发版构建） |
| `publish/`、`installer/`、`branding/` | 安装脚本、Inno Setup 打包、品牌资源 |
| `build-daemon.ps1`、`openserver.bat`、`scripts/*.ps1` | 构建与运维脚本 |
| `.github/workflows/` | `ci.yml`（CI 门禁 + 发版）、`build-windows.yml`、`build-shell-windows.yml`、`build-browser-extension.yml` |

## CI 门禁（`ci.yml`）

`gate`（CI Gate）汇总下列必需 job，任何一个非 success 都算失败；`release` 依赖全部必需 job 与 `gate`，
且 failure / skipped / cancelled 任一结果都不发版：

| job | 作用 |
| --- | --- |
| `build-*` / `test-client-*` | 平台构建与客户端测试 |
| `release-assets` | 发版纯逻辑（资产名 + `version.json`）在 PR 上真实执行 |
| `browser-extension-lint` | 浏览器插件 Prettier 检查（原来写在永不执行的嵌套目录里） |
| `contract-freshness` | 契约快照与权威 `pim-api` 对比（需要网络，不需要凭据） |
| `contract-generated` | 契约 C# 生成物重新生成后逐字节比对（离线；接口边界的第二道门禁） |
| `codeql` | CodeQL 静态安全扫描（fork PR 的 upload 为 never，作为发版前置参与门禁） |
| `pr-metadata` | 只在 `pull_request` 上校验双语四段与分支命名（`push` 到 master 不要求 PR body） |

```bash
# 本地等价命令（与 CI 里各 job 的步骤一一对应）
bash scripts/ci/test-release-assets.sh
bash scripts/ci/check-release-logic-tested.sh
bash scripts/ci/test-resolve-version.sh

# pr-metadata：pull_request 事件校验真实 payload；其它事件（push / schedule / dispatch）步骤自身 exit 0。
# 不带 env 直接跑会按“标题/正文/分支名全空”判定为不合规并 exit 1，那不是本地误报，而是缺 payload。
EVENT_NAME=push node scripts/ci/check-pr-metadata.mjs
PR_TITLE='…' PR_BODY='…' HEAD_REF='…' node scripts/ci/check-pr-metadata.mjs

bash scripts/ci/test-contract-freshness.sh

# contract-generated：门禁自身的行为测试（离线；含「契约变了但生成物没重生成」等负对照）
bash scripts/ci/test-contract-csharp.sh

# 接口边界生成物：重新生成 / 只读校验（CI 调的是后者）
bash scripts/ci/generate-contract-csharp.sh
bash scripts/ci/check-contract-csharp.sh
```

> 尚未纳入本门禁的审计守卫（`check-audit-guards.mjs` / `test-audit-guards.mjs`、
> `check-dependency-hygiene.sh`、`test-contract-coverage.mjs`、`test-orphan-test-coverage.mjs`、
> `test-probe-page-syntax.mjs`）依赖尚未落地的客户端改动，落地后随 `ci-scripts` job 一并提交；
> 在此之前**不删除、不降级、不加 `--exclude`**。

## 接口边界（契约生成 + 编译期约束）

`pim-api` 的接口形状由 `contract/openapi.json` 决定，客户端侧不再手写接口类型：

| 产物 | 内容 | 谁生成 |
| --- | --- | --- |
| `src/client-windows/Pim.Client.Core/Contract/ContractModels.cs` | 契约 DTO（含枚举） | `scripts/contract/generate-csharp.py` |
| `src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs` | 用到的 operation（方法 + 路径） | 同上 |

- 用到的 operation 登记在 `scripts/contract/client-operations.json`；生成器会逐个核对它在契约里存在，
  声明了契约里没有的 METHOD/path 会让生成**直接失败**。
- `ApiClient` 只暴露显式方法签名，路径只能取自生成的 `ContractOperation`（构造函数私有），
  请求/响应类型都是生成的 DTO —— 写错路径、字段名、枚举值**编译期就报错**。
- 契约更新后一键刷新：`bash scripts/ci/refresh-contract.sh`（快照 + 生成物同批）。
- 详见 [`contract/README.md`](contract/README.md) 与 `AGENTS.md`「接口契约与门禁」。

## 构建与测试

```bash
# 跨平台核心测试（与 CI 的 test-client-core 同一工程，Linux/Windows 都可跑）
dotnet build tests/Pim.Client.Tests/Pim.Client.Tests.csproj
dotnet test tests/Pim.Client.Tests/Pim.Client.Tests.csproj

# Windows 专属测试（WPF / WinForms UI 组件：托盘、状态中心、单实例守卫等，只能在 Windows runner 上跑）
dotnet test tests/Pim.Client.App.Tests/Pim.Client.App.Tests.csproj

# 浏览器扩展
cd browser-extension && npm install && npm run build
```

```powershell
# Windows 专属：托盘 / WebView2 / 安装包相关的工程与解决方案需要 Windows 工具链
dotnet build src/client-windows/Pim.Client.Windows.slnx
```

## 相关仓库

| 仓库 | 角色 |
| --- | --- |
| [pim-api](https://github.com/2746267826/pim-api) | 后端 + MCP + 部署 + 契约出口 |
| [pim-web](https://github.com/2746267826/pim-web) | Web 前端 + 双壳 |
| [pim-android](https://github.com/2746267826/pim-android) | 安卓采集端 |
| [pim-docs](https://github.com/2746267826/pim-docs) | 文档与归档（private） |

## 贡献约定

所有改动走分支 + Pull Request；提交信息与 PR 描述双语（英文 + 简体中文）。详见 [`AGENTS.md`](AGENTS.md)。
