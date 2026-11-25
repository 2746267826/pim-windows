#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 发版链路护栏的静态断言。
#
# 这些文件一旦漏接一个字段，行为上可能仍然全绿：显示版本塞进更新比较、
# 旧 CI 重跑把正式 tag 降回 pre-release、promote_tag 跳过来源校验……
# 只有真发一次版才会暴露。这里把跨文件接线钉死，失败信息直接指向缺的行。
# ---------------------------------------------------------------------------
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

pass=0
fail=0
check() { # <label> <file> <literal>
  local label="$1" file="$2" literal="$3"
  if grep -Fq -- "$literal" "$ROOT/$file"; then
    echo "  ✅ $label"
    pass=$((pass + 1))
  else
    echo "  ❌ $label — $file 缺少 [$literal]"
    fail=$((fail + 1))
  fi
}

check_absent() { # <label> <file> <literal>
  local label="$1" file="$2" literal="$3"
  if grep -Fq -- "$literal" "$ROOT/$file"; then
    echo "  ❌ $label — $file 仍包含 [$literal]"
    fail=$((fail + 1))
  else
    echo "  ✅ $label"
    pass=$((pass + 1))
  fi
}

job_block() { # <file> <job-name>
  awk -v job="$2" '
    $0 ~ "^  " job ":$" { inside=1; next }
    inside && /^  [A-Za-z0-9_-]+:$/ { exit }
    inside { print }
  ' "$ROOT/$1"
}

check_in_job() { # <label> <file> <job> <literal>
  local label="$1" file="$2" job="$3" literal="$4"
  local block
  block="$(job_block "$file" "$job")"
  if grep -Fq -- "$literal" <<<"$block"; then
    echo "  ✅ $label"
    pass=$((pass + 1))
  else
    echo "  ❌ $label — $file 的 job [$job] 缺少 [$literal]"
    fail=$((fail + 1))
  fi
}

check_not_in_job() { # <label> <file> <job> <literal>
  local label="$1" file="$2" job="$3" literal="$4"
  local block
  block="$(job_block "$file" "$job")"
  if grep -Fq -- "$literal" <<<"$block"; then
    echo "  ❌ $label — $file 的 job [$job] 仍包含 [$literal]"
    fail=$((fail + 1))
  else
    echo "  ✅ $label"
    pass=$((pass + 1))
  fi
}

echo "== release workflow guards =="

# ---- CI 自动发布：旧 run 重跑不得降级、tag 必须钉住本次 commit ----
check "CI 有正式版守卫步骤" .github/workflows/ci.yml 'Guard existing formal release'
check "CI 发布守卫走行为测试脚本" .github/workflows/ci.yml 'release-publish-guard.sh'
check "CI 发布守卫传入 run attempt" .github/workflows/ci.yml 'RUN_ATTEMPT: ${{ github.run_attempt }}'
check "CI 发布守卫传入 commit sha" .github/workflows/ci.yml 'SHA: ${{ github.sha }}'
check "CI release job 持有共享发布锁" .github/workflows/ci.yml 'group: pim-release-mutation'
check_in_job "CI release job 使用 --target" .github/workflows/ci.yml release '--target "$SHA"'
check_in_job "CI release job 回读 targetCommitish" .github/workflows/ci.yml release 'target_commitish'
check_in_job "CI release job 回读四类资产" .github/workflows/ci.yml release 'PIM-Setup-v${V}.exe'
check_absent "CI release 不再允许缺安装器" .github/workflows/ci.yml '--allow-missing-installer'
check "发布守卫正式版不重发" scripts/ci/release-publish-guard.sh 'formal-exists'
check "发布守卫重跑只认当前 master HEAD" scripts/ci/release-publish-guard.sh 'commits/master'
check "发布守卫 API 错误 fail-closed" scripts/ci/release-publish-guard.sh '不把 API 错误当成『不存在』'
check "跳过发布有可见 summary" .github/workflows/ci.yml 'Release publication skipped'
check "发布 step 受 publish 守卫控制" .github/workflows/ci.yml "steps.existing.outputs.publish == 'true'"

# ---- release.yml 的入口、自动 PR 与直接 merge ----
check "release 只能从 master 触发" .github/workflows/release.yml 'refs/heads/master'
check "bump PR 技术修改章节" .github/workflows/release.yml '## 技术修改 / Technical changes'
check "bump PR 功能变化章节" .github/workflows/release.yml '## 功能变化 / Feature changes'
check "bump PR 如何体验章节" .github/workflows/release.yml '## 如何体验 / How to try it'
check "bump PR 测试章节" .github/workflows/release.yml '## 测试 / Tests'
check "bump PR 需求验收映射" .github/workflows/release.yml '## 需求与验收映射 / Requirement & Acceptance Mapping'
check "未运行的 release 标 NOT-VERIFIED" .github/workflows/release.yml 'NOT-VERIFIED'
check "bump PR 映射含真实安装未验证" .github/workflows/release.yml '真实 Windows 安装/升级'
check "bump PR 映射含生产更新源未验证" .github/workflows/release.yml '生产更新源'
check "直接 merge 使用 --match-head-commit" .github/workflows/release.yml 'gh pr merge "$PR_NUM" --squash --match-head-commit "$HEAD_SHA"'
check "merge 前等待 CI Gate SUCCESS" .github/workflows/release.yml 'select(.name == "CI Gate")'
check "merge 命令失败明确失败" .github/workflows/release.yml '直接 squash merge 失败'
check_absent "release.yml 不再使用 --auto" .github/workflows/release.yml '--auto'
check "merge 后回读 PR 状态" .github/workflows/release.yml 'merge 命令返回成功，但回读 PR'
check "自动 bump 分支符合 pr-metadata" .github/workflows/release.yml 'BRANCH="release-bot-linux/v${V}"'
check_absent "bump 分支不再用 release/ 前缀" .github/workflows/release.yml 'BRANCH="release/v${V}"'

# ---- 来源证明 ----
check "promote_tag 走来源校验脚本" .github/workflows/release.yml 'scripts/ci/validate-promote-target.sh'
check "promote 校验 tag commit" scripts/ci/validate-promote-target.sh 'git merge-base --is-ancestor'
check "promote 校验 tag 的 VERSION" scripts/ci/validate-promote-target.sh 'VERSION_AT_COMMIT'
check "promote 校验当前 checkout VERSION" scripts/ci/validate-promote-target.sh 'HEAD_VERSION'
check "promote 校验 run headSha" scripts/ci/validate-promote-target.sh 'headSha'
check "promote 校验 master push" scripts/ci/validate-promote-target.sh 'headBranch == "master"'
check "promote 校验守护程序资产" scripts/ci/validate-promote-target.sh 'pim-windows-v'
check "promote 校验壳资产" scripts/ci/validate-promote-target.sh 'pim-shell-windows-v'
check "promote 校验安装器资产" scripts/ci/validate-promote-target.sh 'PIM-Setup-v'
check "promote 校验 version.json 资产" scripts/ci/validate-promote-target.sh 'version.json'
check "promote 校验 version.json 下载直链" scripts/ci/validate-promote-target.sh '下载直链'
check "promote 校验 latest 回退" scripts/ci/validate-promote-target.sh 'latest 回退'
check "promote 拒绝非 master 构建" scripts/ci/validate-promote-target.sh '不是 master 分支构建'
check "promote 拒绝失败构建" scripts/ci/validate-promote-target.sh '不是成功构建'
check "promote 校验从 REST 读 latest" scripts/ci/validate-promote-target.sh 'releases/latest'
check_absent "promote 校验不请求不存在的 isLatest 字段" scripts/ci/validate-promote-target.sh 'databaseId,tagName,isPrerelease,isDraft,targetCommitish,assets,isLatest'

# ---- 提升步骤：databaseId、make_latest、回读 ----
check "提升后显式 make_latest" .github/workflows/release.yml 'make_latest=true'
check "make_latest 使用 REST 数字 databaseId" .github/workflows/release.yml '--json databaseId'
check "databaseId 必须为纯数字" .github/workflows/release.yml '"$RELEASE_ID" =~ ^[0-9]+$'
check "release 写操作使用 REST databaseId" .github/workflows/release.yml 'gh api -X PATCH "repos/${REPO}/releases/${RELEASE_ID}"'
check_absent "提升步骤不再按 tag 写 release" .github/workflows/release.yml 'gh release edit "$TAG"'
check "提升可重入（已正式版不再早退）" .github/workflows/release.yml 'IS_PRE="$(gh release view'
check "提升重入后继续收敛 latest" .github/workflows/release.yml '本次重入只收敛正文与 latest'
check_absent "提升步骤不再使用 GraphQL 节点 id" .github/workflows/release.yml '--json id'
check "提升后回读 /releases/latest" .github/workflows/release.yml 'releases/latest'
check "提升后回读 release 本体" .github/workflows/release.yml 'FINAL_PRE'
check "apply 作业同步到当前 master" .github/workflows/release.yml 'git fetch --prune --tags origin master'
check "prepare 作业不持有发布锁" .github/workflows/release.yml 'prepare:'
check_not_in_job "prepare 作业不占用 pim-release-mutation" .github/workflows/release.yml prepare 'group: pim-release-mutation'
check_in_job "apply 作业持有共享发布锁" .github/workflows/release.yml apply 'group: pim-release-mutation'
check_in_job "apply 作业在锁内校验来源" .github/workflows/release.yml apply 'Validate promote target'
check_in_job "apply 作业在锁内提升" .github/workflows/release.yml apply 'Promote to formal release'
check_in_job "prepare 作业等待 master 构建" .github/workflows/release.yml prepare 'Wait for the master build'

# ---- 双轨版本号与 Windows 载荷 / 手工入口 ----
check "build-windows 手工入口有 display_version" .github/workflows/build-windows.yml "display_version:"
check "build-windows 手工空 display_version 回读 VERSION" .github/workflows/build-windows.yml 'head -n 1 "$GITHUB_WORKSPACE/VERSION"'
check "build-windows 仍是 daemon-only 载荷" .github/workflows/build-windows.yml 'Verify Windows payload manifest'
check_absent "build-windows 不再发布旧壳" .github/workflows/build-windows.yml 'name: Publish Shell'
check "build-windows 清空 publish 目录" .github/workflows/build-windows.yml 'Remove-Item -LiteralPath publish'
check "resolve-version 测试动态读取 VERSION" scripts/ci/test-resolve-version.sh 'REPO_DISPLAY_VERSION'
check "resolve-version 测试覆盖 1.1.0 夹具" scripts/ci/test-resolve-version.sh 'VERSION-1.1.0'
check "resolve-version 测试覆盖非法 VERSION" scripts/ci/test-resolve-version.sh 'VERSION-bad'
check "CI 跑发布守卫行为测试" .github/workflows/ci.yml 'test-release-publish-guard.sh'
check "CI 跑 promote target 行为测试" .github/workflows/ci.yml 'test-validate-promote-target.sh'
check "CI 跑 release workflow 静态测试" .github/workflows/ci.yml 'test-release-workflow-guards.sh'
check "CI 发布后校验 tag ref SHA" .github/workflows/ci.yml 'git/ref/tags/${TAG}'
check "CI 发布后解析 annotated tag" .github/workflows/ci.yml 'git/tags/${TAG_SHA}'
check "TrayIcon 机器版本不拼接显示号" src/client-windows/Pim.Client.App/TrayIcon.BuildMenu.cs 'ClientVersion.ReadMachineVersion'
check "TrayIcon 显示版本来自 AssemblyMetadata" src/client-windows/Pim.Client.App/TrayIcon.BuildMenu.cs 'ClientVersion.ReadDisplayVersion'
check "更新检查只接收机器版本" src/client-windows/Pim.Client.App/TrayIcon.cs 'CheckUpdateAsync(machineVersion)'

if [[ "$fail" -ne 0 ]]; then
  echo "release workflow guard tests failed: ${fail} missing, ${pass} passed" >&2
  exit 1
fi
echo "release workflow guard tests passed (${pass} checks)"
