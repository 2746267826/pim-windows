# 发版通道（显示版本号 / dev 版 / 正式版）

本仓与 `pim-api` / `pim-android` 用同一套规则：机器版本负责比较，显示版本只负责好看。

## 两个版本号

| 号 | 来源 | 用途 |
| --- | --- | --- |
| 显示版本 `x.y.z` | 仓根 `VERSION` 文件（一行） | 只给人看：托盘「关于 PIM」、关于框 |
| 机器版本 `26.11.1234` | `GITHUB_RUN_NUMBER`（叠历史补偿 +1000） | `AssemblyInformationalVersion` / `versionCode` / `version.json`：更新检查与端上报只认它 |

显示号通过 `-p:DisplayVersion=...` 编译成 `AssemblyMetadata("DisplayVersion")`（见
`src/Directory.Build.props`），界面读它；`AssemblyInformationalVersion` 保持机器号，
因为更新检查比较的是版本号**最后一段数字**，端上报心跳也用它。

## 两条通道

- **合并到 master** → 照常构建并发 release，但 `--prerelease`。GitHub 的 `/releases/latest`
  天生跳过 pre-release，所以服务端更新检查（`releases/latest/download/version.json`）
  始终指向最近一个正式版，开发期不会给用户推一堆"新版本"。
- **正式版** → `release.yml`（`Release (promote)`）：改 `VERSION` 开 PR →
  等 `CI Gate` 成功后在共享发布锁内直接 squash merge（不依赖仓库的
  `allow_auto_merge`）→ 等该合并提交的构建 → 重新校验来源后再把那次构建的
  pre-release 提升为正式版（`--prerelease=false` + `make_latest`）。提升不重新构建，
  正式版资产就是过完 CI 的那份 zip / 安装器。
- 只重试提升：`-f promote_tag=windows-v26.11.1234`。

自动发布（`ci.yml`）创建 tag 时必须传 `--target <本次 commit sha>`，否则 GitHub
可能把 tag 指向建 tag 时的 master HEAD，而不是这次真正构建的提交。正式版提升前，
`scripts/ci/validate-promote-target.sh` 会交叉验证：

- tag 指向的 commit 在当前 master 历史里，且该 commit 的仓根 `VERSION` 等于目标显示版本；
- tag ref 解析后的 commit（annotated tag 会先解到 commit）必须等于发布构建的 headSha；
- 该 commit 上有成功的 master push 构建，run 号与 tag 一致、headSha 就是 tag commit；
- release 的 `targetCommitish` 精确等于 tag commit；
- 四类资产齐全：`pim-windows-v*.zip`、`pim-shell-windows-v*.zip`、
  `PIM-Setup-v*.exe`、`version.json`，且 `version.json` 的机器版本与下载直链都绑定本 tag；
- 目标 CalVer 严格新于当前 `/releases/latest`；有显示版本时也不允许显示版本回退。

提升与 `make_latest` 都使用 REST 数字 `databaseId`，并在 PATCH 后回读 release 本体和
`/releases/latest`；只有两者都确认指向本 tag 才算成功。旧 CI run 重跑由
`scripts/ci/release-publish-guard.sh` 拦住：正式版绝不改写，过期/非当前 master 的重跑
不会补发或把正式版降回 pre-release。两个发布入口共用 `pim-release-mutation` 锁。

> 升级方向只在一种情况下要注意：用 `promote_tag` 追发**较旧的构建**时，它的
> `versionCode` 会小于之后合并产生的 dev 包；装了那些更晚 dev 版的机器会把它判成旧版，
> 更新检查不提示。正常走「改号 → 合并 → 提升」时，提升的就是最新那次构建、`versionCode`
> 最大，不存在这个问题。

## 发版前必须满足

- secret `PIM_RELEASE_TOKEN`（能触发 workflow 的 PAT / App 令牌）：`GITHUB_TOKEN`
  触发的 push / pull_request 不会启动任何 workflow，bump PR 会永远等不到 `CI Gate`。
- 发行说明由 `scripts/ci/validate-release-notes.sh` 机械校验：不能为空、不能留占位符、
  必须有列表项、引用的每个 `#PR` 都必须真实且已合并。

## 发版

```bash
gh workflow run release.yml --repo 2746267826/pim-windows --ref master \
  -f version=1.1.0 -f notes="$(cat notes.md)"

# 只重试提升
gh workflow run release.yml --repo 2746267826/pim-windows --ref master \
  -f version=1.1.0 -f promote_tag=windows-v26.11.1234 -f notes="$(cat notes.md)"
```
