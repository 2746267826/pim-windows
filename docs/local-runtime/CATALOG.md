# 本地运行时目录 / Local runtime catalog

客户端只读取 **用户本机** 的 `%LOCALAPPDATA%\PIM\local-runtime\catalog.json`。
仓库不内置任何可执行模型条目，也不会自动下载或运行模型。

## 目录结构 / Shape

```json
{
  "schemaVersion": 1,
  "runtimes": [
    {
      "runtimeId": "example-runtime",
      "displayName": "Example runtime",
      "executableFileName": "runner.exe",
      "sourceUrl": "https://example.com/runner.exe",
      "licenseId": "MIT",
      "licenseUrl": "https://example.com/LICENSE",
      "revision": "pinned-tag-or-commit",
      "sha256": "<64 lowercase hex>"
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

## 安全规则 / Safety rules

- `sha256` 为空或不是 64 位小写十六进制：条目仍可展示，但**不可安装、不可执行**。
- 代码只接受 `https://` 下载源；`artifactFileName` 必须是单段文件名，不能包含路径。
- 安装前检查磁盘空间；下载到临时文件，校验大小与 SHA-256 后才原子替换目标文件。
- 取消、磁盘不足、摘要不符、重启中断、损坏都会留下可解释状态，不会把半成品当成功。
- 模型按登录账号隔离存放；切换账号会取消该账号的运行并撤回节点能力快照。
- CPU 型模型不会仅因没有 GPU 被拒绝；声明为必需但无法测量的资源（例如可用 VRAM）会 fail-closed。
- 资源失败自动退避有上限；达到上限后只能手动触发，且手动触发仍会重新检查资源。

## 首发候选调查 / Candidate research (not shipped)

CPU 优先的 `whisper.cpp` 是候选方向，以下仅记录官方入口，**没有登记摘要，因此本仓不提供可安装条目**：

- 上游仓库（MIT）：<https://github.com/ggml-org/whisper.cpp>
- 上游模型文件入口：<https://huggingface.co/ggerganov/whisper.cpp>
- MiMo 音频理解文档：<https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/multimodal-understanding/audio-understanding>

若要启用首发模型，需要用户确认具体模型、目标硬件与资源阈值，并由维护者提供**可复核的官方 revision 与 SHA-256**；
在此之前不会下载、不会执行、也不会声称已完成真实推理验收。
