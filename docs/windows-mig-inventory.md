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
- 云端固定材料上传：按服务器 schema-v1 从本机固定版本生成 `note.json`、`document.md`、照片、手写墨迹/预览、录音清单与 16 kHz PCM 分片；使用已有 typed begin/part/complete/promote/sources/derived/manual-revision/select operations。
- 云端绑定恢复：按规范化服务器 URL + 当前账号持久化本机笔记与云端 Head 绑定；丢失绑定时从服务器已有版本恢复 `baseVersionId`，同版本重试复用首次请求并可跳过已上传分片。
- 服务器材料目录：`asr-tasks/sources?offset=&limit=` 以 `includeWithoutAudio=true` 展示当前账号已完成材料版本，按标题、版本数、录音段数、时长、当前/冲突状态分组；可跳回同 `noteId` 的本机笔记。
- 云端结果入口：Windows 笔记窗口可读取服务器固定版本/冲突状态、完整记录、独立分析、历史结果版本和人工修订；只读打开不会启动 ASR 或付费模型。

当前未覆盖：

- 原生云端笔记目录、笔记级标题/元数据分页与显式删除/回收站尚不存在；用户仍需从本机笔记进入对应稳定 `noteId`，不能浏览服务器上的全部笔记目录。
- 服务器实现 `GET /api/v1/native-notes/versions/{versionId}/files/{fileIndex}`，但 Windows 仓 canonical OpenAPI 的 200 content schema 缺失；尚不生成猜测的 typed 下载，因此云端回读是逐版本结果/材料元数据，不是跨端完整本地往返。
- 云端 ASR/派生生成启动需要管理员路由配置与显式计费/模型 consent；本批只读取已有结果并保存人工修订，不自动启动真实 ASR/模型，也不能把只读结果页当作主动记录迁移完成。
- 完整 M3 Markdown 富编辑仍缺；当前 WPF `TextBox` 只是保留的源码编辑基础，不能标记 Tiptap/ProseMirror 等价能力完成。
- 真实麦克风、声学、长时、睡眠/唤醒、设备切换与隐私权限路径未操作硬件，保持 `NOT-VERIFIED`。
- `.pimnote.zip` 是 Windows 本机固定版本交换格式；服务器 schema-v1 包由独立 builder 生成，不能把两者称为跨端往返完成。

## 契约缺口（需要父侧安排后端）

已核对 canonical `contract/openapi.json`。首次稳定 UUID 上传、分片、完成/提升、ASR sources、派生结果、人工修订与选择已有 typed 操作，**不构成当前上传/结果链硬阻塞**。仍缺以下用户体验能力：

1. `GET /api/v1/native-notes`：按当前账号分页列出笔记及当前版本/冲突摘要。
2. `GET /api/v1/native-notes/{noteId}`：读取笔记元数据与当前 Head；当前该路径只有 `DELETE`。
3. `PATCH` 或 `PUT /api/v1/native-notes/{noteId}`：更新标题/笔记级元数据；当前不存在。
4. `GET /api/v1/native-notes/versions/{versionId}/files/{fileIndex}`：路径已有、实现已有，但 OpenAPI 200 content schema 缺失；补 schema 后才能生成不猜测的 typed 下载。
5. 如 promote + 版本列表不足，需要显式笔记级冲突列表/解决操作；当前 Windows 用 `isCurrent/hasConflict` 与 `promote(expectedCurrentVersionId)` 展示并解决材料版本冲突。

已接入的版本材料与结果 typed 操作：

- `POST /api/v1/native-notes/versions`：`BeginNoteMaterialUploadRequest` 必填 `noteId`、`versionId`、`manifestJson`、`inputJson`、`storageConsent`，`baseVersionId` 可空；客户端可先生成稳定 `noteId/versionId`，首次上传时 `baseVersionId=null`，完成首版本后由服务器建立笔记 Head。
- `GET /api/v1/native-notes/{noteId}/versions`、`GET /api/v1/native-notes/versions/{versionId}`：读取笔记版本列表/上传版本状态。
- `POST /api/v1/native-notes/versions/{versionId}/complete`、`POST /api/v1/native-notes/versions/{versionId}/promote`：完成与提升版本；promote 已接受可空 `expectedCurrentVersionId`。
- `PUT/DELETE /api/v1/native-notes/versions/{versionId}/files/{fileIndex}/parts/{partIndex}`：原始 `application/octet-stream` 分片上传/删除；生成器已支持安全标量 query 与已登记二进制请求体。

当前服务器 `NoteMaterialUploadStore.BeginAsync` 直接接受客户端稳定 `noteId + versionId`，首次 `baseVersionId=null`；ledger 新增该版本，`Complete` 在无并发时建立 Head。因此无需 `POST /api/v1/native-notes` 才能建立首版本；目录/元数据 CRUD 只影响服务器侧浏览体验，不阻塞 Windows 上传。

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

这些结果接口不是笔记创建。`generate-csharp.py` 已扩展 path/query 参数合并、类型校验、URI 编码、required/unknown/duplicate 拒绝和笔记材料二进制请求体；上述 operations 已登记并从 contract 重生成。没有手改 generated。后端增加目录/元数据 API 后仍须刷新 snapshot → 登记 operation → 重新生成，不能使用裸 URL。

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
