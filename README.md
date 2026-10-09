# pim-windows

PIM 的 Windows 端：守护程序采集前台应用、窗口与键鼠活跃度；浏览器扩展同步活动标签页。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/client-windows/` | 守护程序（WPF / .NET，解决方案 `Pim.Client.Windows.slnx`） |
| `src/client-shell-windows/` | 旧 WPF 壳——**V2 的 Tauri 壳上线后退役** |
| `browser-extension/` | 浏览器扩展（Chrome / Edge，WebExtension，独立打包） |
| `publish/`、`installer/`、`branding/` | 安装脚本、Inno Setup 打包、品牌资源 |
| `build-daemon.ps1`、`openserver.bat`、`scripts/*.ps1` | 构建与运维脚本 |
| `.github/workflows/` | `build-windows.yml`、`build-shell-windows.yml`、`build-browser-extension.yml` |

## 构建

```powershell
dotnet build src/client-windows/Pim.Client.Windows.slnx
# 浏览器扩展
cd browser-extension && npm install && npm run build
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
