# Windows 笔记切片与 MIG-01 至 MIG-13 复用盘点

日期：2026-11-01
基线：`origin/master` = `1270bf6a093b1777646b2710d01a4432795adc32`
分支：`codex-win/windows-notes-milestones-20261101`

本文只记录 Windows 仓当前可复用的原生能力与本批笔记切片边界。WebView 能打开某个路由，不等于该业务已完成原生化迁移。

## 本批已落地的 Windows 笔记纵向能力

- 本机笔记库：列表、搜索、新建、打开、标题/Markdown 编辑、自动保存；数据位于 `%LOCALAPPDATA%\PIM\Notes`。
- 固定版本：正文保存、材料加入、手写、录音都会生成不可变本机版本；可从版本下拉列表浏览并恢复为新的当前版本。
- 混合材料：照片、PNG 手写、手写向量 JSON、WAV 音频；材料以 SHA-256 内容寻址保存。
- 主动录音：WinMM 麦克风采集，实际 PCM 计算时长/峰值；录音服务注册为应用级单例，关闭笔记窗口不停止采集。
- 本机入口：主 Shell、托盘可打开笔记；托盘可直接开始/停止笔记录音。
- 本机导出/导入：`.pimnote.zip` 固定正文、版本清单、材料原件、逐文件 SHA-256；导入时校验版本、正文摘要、材料长度与摘要，拒绝篡改包。
- 旧快速记录兼容入口：主 Shell 增加“旧快速记录（Web 兼容）”，导航 `/quick-notes`；旧数据继续由 Web 承载，未被新本机笔记库删除或冒充迁移完成。

当前未覆盖：

- 原生云端笔记目录、创建及正文更新接口尚不存在，客户端不能诚实地提交本机笔记为新云端笔记。
- 云端 ASR/完整记录/独立分析/人工修订/冲突的真实 Windows 产品入口尚未接入；现有 Web 只读结果页不能算 Windows 主动记录或迁移完成。
- 真实麦克风、声学、长时、睡眠/唤醒、设备切换与隐私权限路径未操作硬件，保持 `NOT-VERIFIED`。
- `.pimnote.zip` 目前是 Windows 本机固定版本交换格式；Android/Web 包互导仍未验收。

## 契约缺口（需要父侧安排后端）

已核对 canonical `contract/openapi.json`，没有以下原生笔记目录能力：

1. `GET /api/v1/native-notes`：按当前账号分页列出笔记及当前版本/冲突摘要。
2. `POST /api/v1/native-notes`：创建笔记并返回稳定的 `noteId`，避免客户端猜测服务端身份。
3. `GET /api/v1/native-notes/{noteId}`：读取笔记元数据；当前该路径只有 `DELETE`。
4. `PATCH` 或 `PUT /api/v1/native-notes/{noteId}`：更新标题、当前版本指针或冲突选择；当前不存在。

现有版本材料上传链已经有这些操作：

- `POST /api/v1/native-notes/versions`：`BeginNoteMaterialUploadRequest` 必填 `noteId`、`versionId`、`manifestJson`、`inputJson`、`storageConsent`，`baseVersionId` 可空；客户端可先生成稳定 `noteId/versionId`，首次上传时 `baseVersionId=null`，完成首版本后由服务器建立笔记 Head。
- `GET /api/v1/native-notes/{noteId}/versions`、`GET /api/v1/native-notes/versions/{versionId}`：读取笔记版本列表/上传版本状态。
- `POST /api/v1/native-notes/versions/{versionId}/complete`、`POST /api/v1/native-notes/versions/{versionId}/promote`：完成与提升版本；promote 已接受可空 `expectedCurrentVersionId`。
- `GET /api/v1/native-notes/versions/{versionId}/files/{fileIndex}` 与 `PUT/DELETE .../parts/{partIndex}`：读取材料文件并提供分片上传/删除；版本与文件元数据遵循现有 upload contract。

这些上传操作不能替代笔记目录创建：`POST /api/v1/native-notes` 与 GET 目录不存在；不能先上传材料再假设服务端会自动建笔记。版本链虽有 `expectedCurrentVersionId`，尚没有笔记标题更新/目录冲突的读写语义。

已有、可复用的云端结果契约包括：

- `GET /api/v1/native-notes/derived-tasks/sources?offset=&limit=`
- `GET /api/v1/native-notes/derived-tasks?noteId=`
- `GET /api/v1/native-notes/derived-tasks/{taskId}`
- `GET /api/v1/native-notes/derived-artifacts?noteId=`
- `GET /api/v1/native-notes/derived-artifacts/{artifactId}`
- `GET /api/v1/native-notes/derived-artifacts/{artifactId}/versions/{versionId}`
- `POST /api/v1/native-notes/derived-artifacts/{artifactId}/manual-revisions`
- `POST /api/v1/native-notes/derived-artifacts/{artifactId}/select`
- ASR task/segment/retry/stop 现有任务接口

这些结果接口不是笔记创建。另有一个客户端生成器限制：`generate-csharp.py` 当前拒绝 query 参数，因此上述带 `offset/limit/noteId` 的契约尚未进入 `ApiClient`。必须扩展生成器并正规重建 typed operation，不能手改 `ContractModels.cs` / `ContractOperations.cs`。

后端增加目录与正文版本 API 后，Windows 客户端接入顺序应为：刷新 OpenAPI snapshot → 扩展 generator 对 query 参数的类型化序列化/URI 编码和测试 → 在 `client-operations.json` 登记具体 operation → 重生成 C# typed operation → 再接目录、修订和冲突 UI。不要在客户端用裸 URL 或未生成 DTO 绕过缺口。

## MIG-01 至 MIG-13 轻量盘点

| 编号 | Windows 当前可复用事实 | 本批判断 |
| --- | --- | --- |
| MIG-01 今日、工作台 | `MainShellWindow` 复用登录态和 WebView `/today`；无原生聚合/业务动作 | Web 承载，非迁移完成 |
| MIG-02 日历、任务、日历本/任务本 | Shell 可打开 `/calendar`、`/tasks`；客户端 operation 清单没有日历/任务 CRUD | WebView-only |
| MIG-03 确认中心、提醒、习惯、报告、数据中心 | Shell 路由 `/confirmations`、`/reports`、`/data-center`；`NotificationActionRouter` 可发端点通知动作，但业务页面仍 Web | 混合，业务未原生化 |
| MIG-04 快速记录、文件 | 旧快速记录有显式 Web 兼容入口 `/quick-notes`；旧附件仍由服务端/Web；本机新笔记独立保存，二者未合并 | 旧入口保留；迁移未完成 |
| MIG-05 电脑记录、浏览器使用、电脑明细 | `NativeTrackerService`、`TrackerLogger`、`TrackerSessionManager`、`BrowserBridgeService`、KeyStats 采集与上传均原生保留；StatusWindow 显示运行状态 | 采集强复用，查询/分类/明细 UI 仍 Web |
| MIG-06 手机记录、历史位置 | Windows 无手机采集、地图/轨迹业务 UI | WebView-only |
| MIG-07 应用知识库、分类树 | 无 Windows 原生业务页面或 typed operation | WebView-only |
| MIG-08 设备列表、详情 | `GET /api/v1/endpoints`、端点心跳/通知 typed 边界已存在；无完整设备管理 UI | 少量基础能力，可复用但未完成 |
| MIG-09 登录、初始化、服务器、账号安全 | `LoginWindow`、`AuthService`、DPAPI token 存储、服务器地址保存均原生；用户/管理员管理仍 Web | 原生复用强；管理员边界未迁移 |
| MIG-10 Outlook、Drive、AI、MCP 设置 | 对应设置路由由 Web 承载；Windows 本地模型/运行时有独立原生状态 UI，但不等同完整第三方设置业务 | 基础组件可复用，业务 WebView-only |
| MIG-11 系统状态、可靠性、数据/日历管理、回收站、审计 | `StatusWindow`、Bootstrap/daemon 日志、采集状态、本地模型状态为原生；数据修复/回收站/审计仍 Web | 状态诊断部分复用，其余未迁移 |
| MIG-12 端点/壳入口、嵌入页、旧路由别名 | `MainShellWindow`、`EmbeddedWebViewHost`、认证与通知动作路由可复用；本批只补 `/quick-notes` 兼容入口 | 宿主复用，全量路由/占位盘点未完成 |
| MIG-13 安装更新、托盘/通知、采集启停、队列同步、设置 | 托盘、通知路由、自动启动、更新检查、Tracker/BrowserBridge/KeyStats、守护进程退出路径均保留；`WindowsRecordingService` 为 DI 单例，关闭笔记窗口只解绑 UI 事件，不停止录音 | 生命周期复用强；仍需真实安装/升级与设备/长时验收 |

结论：本批新增的是 Windows 本机主动笔记纵向能力，以及 MIG-04/MIG-13 的兼容入口和生命周期证据；没有任何 MIG-01 至 MIG-13 因本批自动销项。
