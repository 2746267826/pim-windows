# whisper.cpp CPU 首发候选元数据 / Candidate metadata

状态：`METADATA-VERIFIED` / `INSTALL-NOT-VERIFIED` / `NOT-SHIPPED`

本文只记录 2026-10-31 从 GitHub、Hugging Face 官方 API/HEAD 响应复核到的公开元数据，不包含模型或
ZIP 下载，也不代表真实推理、性能、硬件兼容性已验收。用户尚未选择首发模型和目标硬件，因此仓库
不提供可直接安装的默认 runtime catalog。

## 运行时 release 候选 / Runtime release candidate

- 上游：`ggml-org/whisper.cpp`
- Licence：MIT，官方仓库 `LICENSE`
- Release：`b5454`
- Release 类型：**prerelease**，不能标记为稳定版
- Published：`2026-10-06T16:51:27Z`
- Target commit：`d1be6fde11ac6e0407606b4e42fe72d34add8037`
- Asset：`whisper-bin-x64.zip`
- Asset size：`8928640` bytes
- Asset SHA-256：`6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb`
- 官方 URL：
  <https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip>
- Release 元数据：
  <https://api.github.com/repos/ggml-org/whisper.cpp/releases/tags/b5454>
- MIT licence：
  <https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE>

尚未验证：

- ZIP 内 EXE/DLL 的实际文件名、逐文件大小和逐文件 SHA-256；
- `whisper-cli.exe` 在目标版本上的完整参数兼容性；
- ZIP 内是否还需要额外 VC++ runtime；
- x64 Windows 最低版本和 CPU 指令集要求。

因此 `b5454` 不能直接形成 `LocalRuntimeManifest.HasVerifiedRuntime == true` 的条目。

## 模型文件候选 / Model artifact candidate

- 仓库：`ggerganov/whisper.cpp`
- Pinned revision：`5359861c739e955e79d9a303bcbc70fb988958b1`
- Licence：MIT（仓库卡与仓库 LICENSE 均为 MIT）
- 文件：`ggml-base.bin`
- Size：`147951465` bytes
- SHA-256 / LFS object ETag：
  `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`
- Pinned URL：
  <https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin>
- 仓库元数据：
  <https://huggingface.co/api/models/ggerganov/whisper.cpp/revision/5359861c739e955e79d9a303bcbc70fb988958b1>

该摘要来自官方 resolve URL 的 HEAD 响应（`Content-Length` 与 LFS object ETag），没有下载或读取模型
正文。模型许可与文件元数据已固定，但资源阈值、CPU/内存下限和真实推理输出仍未验证。

## 启用前门 / Enablement gates

1. 用户明确选择首发模型、目标 Windows 硬件和可接受的质量/时延；
2. 从官方 manifest 或经授权的受控校验流程取得 ZIP 内每个 EXE/DLL 的大小与 SHA-256；
3. 添加固定 `files`、`executablePath`、无 shell 参数模板和输出解析验收；
4. 使用合成音频完成安装、摘要、取消、损坏、账号切换和受控进程树测试；
5. 真实音频、付费调用和硬件 benchmark 必须另行获得授权，不能由本元数据调查推定 `PASS`。

## 2026-10-31 追加更正 / Additive metadata correction

上文“尚未验证 ZIP 内逐文件元数据”的记录已被后续只读复核更新：仓库内的显式手动候选
`src/client-windows/Pim.Client.App/LocalRuntime/whisper-cpp-b5454-cpu-x64.json` 记录了
`whisper-bin-x64.zip` 的 41 个文件条目、逐文件大小与 SHA-256，并继续固定 archive 大小/摘要和
`b5454@d1be6fde11ac6e0407606b4e42fe72d34add8037`。该候选标记为 prerelease，只有用户显式手动安装
才会下载和解包；不会自动下载、自动执行或自动登记模型。

此更正只覆盖 ZIP 文件级元数据。模型列表仍为空，真实模型下载/推理、参数兼容性、VC++ 依赖、
CPU 指令集、性能、资源基线和目标硬件仍未验证。
