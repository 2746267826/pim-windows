# 本地运行时目录 / Local runtime catalog

客户端只读取用户本机的 `%LOCALAPPDATA%\PIM\local-runtime\catalog.json`。仓库不内置可执行条目，
也不会自动下载、解包、安装或运行任何运行时。用户必须显式选择“安装”并再次确认。

## 目录结构 / Shape

`schemaVersion` 当前为 `1`。运行时和模型条目是独立列表；模型通过 `runtimeId` 关联运行时。

```json
{
  "schemaVersion": 1,
  "runtimes": [
    {
      "runtimeId": "example-runtime",
      "displayName": "Example runtime",
      "artifactFileName": "runtime.zip",
      "sourceUrl": "https://example.com/runtime.zip",
      "licenseId": "MIT",
      "licenseUrl": "https://example.com/LICENSE",
      "revision": "pinned-tag-or-commit",
      "sha256": "<runtime-archive-sha256>",
      "sizeBytes": 123456,
      "archiveKind": "zip",
      "executablePath": "bin/runner.exe",
      "files": [
        {
          "path": "bin/runner.exe",
          "sizeBytes": 45678,
          "sha256": "<exe-sha256>"
        },
        {
          "path": "bin/runner.dll",
          "sizeBytes": 78901,
          "sha256": "<dll-sha256>"
        }
      ],
      "execution": {
        "arguments": [
          "--model", "{modelPath}",
          "--file", "{inputPath}",
          "--language", "{language}"
        ],
        "timeoutSeconds": 300,
        "outputFormat": "whisper.cpp-json"
      }
    }
  ],
  "models": [
    {
      "modelId": "example-model",
      "displayName": "Example model",
      "runtimeId": "example-runtime",
      "licenseId": "MIT",
      "licenseUrl": "https://example.com/LICENSE",
      "sourceUrl": "https://example.com/model.bin",
      "revision": "pinned-revision",
      "sha256": "<64 lowercase hex>",
      "sizeBytes": 123456789,
      "artifactFileName": "model.bin",
      "resources": {
        "cpuCores": 4,
        "memoryBytes": 4294967296,
        "gpuRequired": false,
        "requireAcPower": false
      }
    }
  ]
}
```

兼容的 `archiveKind` 只有：

- `zip`：运行时解包后必须与 `files` 完全一致；EXE 与依赖 DLL 都应逐项登记。
- `file`：归档本身就是单个受摘要校验的 EXE；此时 `files` 只能有一项，且路径同时等于
  `executablePath` 与 `artifactFileName`。

`execution.arguments` 作为独立参数传给进程，不经过 shell。仅允许 `{modelPath}`、
`{inputPath}`、`{language}` 三个占位符。路径、参数和输出格式由目录固定，不允许运行时任意拼接命令行。
`outputFormat` 当前支持 `whisper.cpp-json` 与 `plain-text`。

`execution.allowCwdFallbackWhenInvalid` 默认 `false`。只有明确确认运行时没有任何相对路径依赖时，
目录作者才可显式设为 `true`；仅当原始工作目录为绝对超长路径且 Windows 返回
`ERROR_DIRECTORY (267)` 时，客户端才会在子进程创建前放弃原超长工作目录并继承父进程工作目录。
普通路径、相对路径和其他启动错误始终 fail-closed。

## 安全规则 / Safety rules

- 运行时 ZIP 的大小和 SHA-256 在解包前校验。每个 EXE/DLL 还必须在 `files` 中登记大小和 SHA-256，
  解包后逐项复核；任何缺失、额外、同名大小写冲突或摘要不符都会阻止发布。
- 拒绝绝对路径、`..`、Windows 保留设备名、符号链接、reparse point、目录逃逸、重复路径和未声明文件。
- 解包在账号隔离的受控 staging 中完成，并同时执行文件数、单项字节和累计字节上限，防止 ZIP bomb。
- 全部文件校验通过后才移动为内容摘要命名的版本目录；崩溃恢复只登记完整版本，取消或失败不留半 runtime。
- 代码只接受不含 userinfo 的绝对 `https://` 下载源；每一跳重定向都重新检查 scheme、主机与私网地址，
  下载客户端不携带 API 登录凭据。
- 所有安装文件按登录账号隔离。卸载仅允许删除解析后仍位于该 store 的 `runtime-accounts` 下的目录；
  遇到 reparse point 或无法证明归属的树时拒绝递归删除。
- 模型按登录账号隔离存放；切换账号会取消该账号的运行并撤回节点能力快照。
- CPU 型模型不会仅因没有 GPU 被拒绝；声明为必需但无法测量的资源会 fail-closed。
- 资源失败自动退避有上限；达到上限后只能手动触发，且手动触发仍会重新检查资源。
- 仓库不提供可信运行的默认条目。目录中缺少真实 revision、许可和 SHA-256 的条目只能展示，不能安装或执行。

## 首发候选调查 / Candidate research (not shipped)

官方元数据调查和尚未验证的事项见
[`ASR516-WHISPER-CANDIDATE.md`](ASR516-WHISPER-CANDIDATE.md)。候选记录不构成可安装目录条目：
当前没有从官方 manifest 获得 ZIP 内每个 EXE/DLL 的独立大小与 SHA-256，且用户尚未批准首发模型和硬件。

MiMo 音频理解文档入口：<https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/multimodal-understanding/audio-understanding>
