#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/resolve-version.sh"
fail=0

# 这个测试不能把显示版本号写死成仓库当时的 1.0.0：发版就是改 VERSION，
# 写死会让「VERSION 已经 bump、测试还没改」看起来像产品坏了。期望值一律
# 从仓根 VERSION / 测试夹具读取，夹具另外显式覆盖 1.1.0。
REPO_DISPLAY_VERSION="$(head -n 1 "$ROOT/VERSION" | tr -d '\r' | tr -d '[:space:]')"
if [[ ! "$REPO_DISPLAY_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "FAIL 仓根 VERSION 不是 x.y.z：[$REPO_DISPLAY_VERSION]"
  exit 1
fi

# Per-run scratch dir: a fixed /tmp path collides with other users/agents that
# run this test (the second run then fails with "Permission denied").
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

run_case() {
  local name="$1"; shift
  local out
  out="$( "$@" )"
  echo "$out" > "$WORKDIR/rv-$name.env"
}

assert_eq() {
  local key="$1" expected="$2" file="$3"
  local actual
  actual="$(grep "^${key}=" "$file" | cut -d= -f2-)"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL $file $key: expected [$expected] got [$actual]"
    fail=1
  else
    echo "OK $key=$actual"
  fi
}

# 历史基线floor：拆仓前客户端序号最大到 815（tag v2026.10.815，versionCode 100815，
# 程序集/文件版本 2026.10.815.0）。拆仓后序号从 1 重新计数，必须靠补偿基数抬到 floor
# 之上，否则用户机器上已装的旧版会大于新版：更新检查只比较版本号最后一段数字
# （旧客户端永远看不到更新）。
HISTORICAL_VERSION_FLOOR=815
HISTORICAL_VERSION_CODE_FLOOR=100815

assert_gt() {
  local key="$1" floor="$2" file="$3"
  local actual
  actual="$(grep "^${key}=" "$file" | cut -d= -f2-)"
  if [[ ! "$actual" =~ ^[0-9]+$ ]] || (( actual <= floor )); then
    echo "FAIL $file $key: expected > $floor got [$actual]"
    fail=1
  else
    echo "OK $key=$actual (> $floor)"
  fi
}

# 第 index 段（1 起算）必须 > floor。程序集版本是四段（2026.10.1042.0），可比的那段是
# 第三段，别拿最后一段（恒为 0）去比。
assert_nth_segment_gt() {
  local key="$1" index="$2" floor="$3" file="$4"
  local actual seg
  local -a parts
  actual="$(grep "^${key}=" "$file" | cut -d= -f2-)"
  IFS='.' read -r -a parts <<< "$actual"
  seg="${parts[$((index - 1))]:-}"
  if [[ ! "$seg" =~ ^[0-9]+$ ]] || (( seg <= floor )); then
    echo "FAIL $file $key=$actual: 第 $index 段 [$seg] 未超过 $floor"
    fail=1
  else
    echo "OK $key=$actual 第 $index 段 $seg > $floor"
  fi
}

# master
run_case master \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1042" $WORKDIR/rv-master.env
assert_eq display_version "$REPO_DISPLAY_VERSION" $WORKDIR/rv-master.env
assert_eq release_tag "windows-v26.07.1042" $WORKDIR/rv-master.env
assert_eq version_code "101042" $WORKDIR/rv-master.env
assert_eq is_release "true" $WORKDIR/rv-master.env
assert_eq git_sha_short "abcdef1" $WORKDIR/rv-master.env
assert_eq year_month "26.07" $WORKDIR/rv-master.env
assert_eq artifact_slug "26.07.1042" $WORKDIR/rv-master.env

# PR
run_case pr \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/pull/12/merge GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=pull_request GITHUB_REF_NAME=12/merge \
      PR_NUMBER=12 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1042-pr.12+abcdef1" $WORKDIR/rv-pr.env
assert_eq display_version "${REPO_DISPLAY_VERSION}-pr.12.abcdef1" $WORKDIR/rv-pr.env
assert_eq release_tag "windows-v26.07.1042" $WORKDIR/rv-pr.env
assert_eq version_code "101042" $WORKDIR/rv-pr.env
assert_eq is_release "false" $WORKDIR/rv-pr.env
assert_eq artifact_slug "26.07.1042-pr.12-abcdef1" $WORKDIR/rv-pr.env

# dispatch non-master
run_case dev \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/codex/foo GITHUB_RUN_NUMBER=7 GITHUB_SHA=deadbeefcafebabe \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1007-dev+deadbee" $WORKDIR/rv-dev.env
assert_eq display_version "${REPO_DISPLAY_VERSION}-dev+deadbee" $WORKDIR/rv-dev.env
assert_eq is_release "false" $WORKDIR/rv-dev.env

# 审计 F-22：发版只允许由 push 到 master 触发。
# 手动 workflow_dispatch 即使 ref 是 master 也不得发版 —— 手动重跑/演练不应产生发布。
run_case dispatch_master \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq is_release "false" $WORKDIR/rv-dispatch_master.env
assert_eq version "26.07.1042-dev+abcdef1" $WORKDIR/rv-dispatch_master.env
assert_eq display_version "${REPO_DISPLAY_VERSION}-dev+abcdef1" $WORKDIR/rv-dispatch_master.env

# 对照：同一个 REF 换成真正的 push 事件，必须发版。
run_case push_master \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq is_release "true" $WORKDIR/rv-push_master.env

# client patch on master（push 事件，仍须发版并带上 patch 后缀）
run_case patch \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push CLIENT_PATCH=windows.1 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1042+windows.1" $WORKDIR/rv-patch.env
assert_eq display_version "$REPO_DISPLAY_VERSION" $WORKDIR/rv-patch.env
assert_eq is_release "true" $WORKDIR/rv-patch.env

# ---- 显式覆盖 VERSION=1.1.0 的四个显示号分支 ----
# 正式版 bump 就是把这个文件从 1.0.0 改成 1.1.0。四条路径（master / PR /
# dev / client patch）都必须立刻跟着变，而不是只测仓库当前碰巧的那个值。
printf '1.1.0\n' > "$WORKDIR/VERSION-1.1.0"

run_case display-110-master \
  env PIM_VERSION_FILE="$WORKDIR/VERSION-1.1.0" GITHUB_REPOSITORY=2746267826/pim-windows \
      GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq display_version "1.1.0" $WORKDIR/rv-display-110-master.env

run_case display-110-pr \
  env PIM_VERSION_FILE="$WORKDIR/VERSION-1.1.0" GITHUB_REPOSITORY=2746267826/pim-windows \
      GITHUB_REF=refs/pull/12/merge GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=pull_request GITHUB_REF_NAME=12/merge PR_NUMBER=12 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq display_version "1.1.0-pr.12.abcdef1" $WORKDIR/rv-display-110-pr.env

run_case display-110-dev \
  env PIM_VERSION_FILE="$WORKDIR/VERSION-1.1.0" GITHUB_REPOSITORY=2746267826/pim-windows \
      GITHUB_REF=refs/heads/codex/foo GITHUB_RUN_NUMBER=7 GITHUB_SHA=deadbeefcafebabe \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq display_version "1.1.0-dev+deadbee" $WORKDIR/rv-display-110-dev.env

run_case display-110-patch \
  env PIM_VERSION_FILE="$WORKDIR/VERSION-1.1.0" GITHUB_REPOSITORY=2746267826/pim-windows \
      GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push CLIENT_PATCH=windows.1 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq display_version "1.1.0" $WORKDIR/rv-display-110-patch.env

# ---- VERSION 文件：可覆盖、缺失/格式错误必须硬失败 ----
# 显示号只在发版时改一次，写错了最理想的时机就是构建时立刻炸；
# 静默回落会让正式版带着一个错号发布出去，事后只能靠人眼发现。
printf '2.3.4\n' > "$WORKDIR/VERSION-ok"
printf '1.0\n'   > "$WORKDIR/VERSION-bad"
printf '\n'       > "$WORKDIR/VERSION-empty"

run_case override \
  env PIM_VERSION_FILE="$WORKDIR/VERSION-ok" GITHUB_REPOSITORY=2746267826/pim-windows \
      GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq display_version "2.3.4" $WORKDIR/rv-override.env

expect_fail() {
  local name="$1" pattern="$2" file="$3"
  local out rc
  set +e
  out="$( env PIM_VERSION_FILE="$file" GITHUB_REPOSITORY=2746267826/pim-windows \
            GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
            GITHUB_EVENT_NAME=push \
            bash "$SCRIPT" --date 2026-07-12 --print-env 2>&1 )"
  rc=$?
  set -e
  if [[ "$rc" -eq 0 ]]; then
    echo "FAIL $name: expected non-zero exit, got 0 (output: $out)"
    fail=1
  elif ! grep -q "$pattern" <<<"$out"; then
    echo "FAIL $name: expected error containing [$pattern], got [$out]"
    fail=1
  else
    echo "OK   $name (rc=$rc)"
  fi
}

expect_fail version-file-missing "VERSION file missing or empty" "$WORKDIR/does-not-exist"
expect_fail version-file-empty "VERSION file missing or empty" "$WORKDIR/VERSION-empty"
expect_fail version-file-bad-format "Invalid display version" "$WORKDIR/VERSION-bad"

# ---- 历史基线补偿：最小的运行序号也必须超过拆仓前的历史最大值 ----
run_case baseline \
  env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=1 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-10-14 --print-env
assert_eq version "26.10.1001" $WORKDIR/rv-baseline.env
assert_eq version_code "101001" $WORKDIR/rv-baseline.env
assert_eq assembly_version "2026.10.1001.0" $WORKDIR/rv-baseline.env
assert_nth_segment_gt version 3 "$HISTORICAL_VERSION_FLOOR" $WORKDIR/rv-baseline.env
assert_gt version_code "$HISTORICAL_VERSION_CODE_FLOOR" $WORKDIR/rv-baseline.env
assert_nth_segment_gt assembly_version 3 "$HISTORICAL_VERSION_FLOOR" $WORKDIR/rv-baseline.env
assert_eq release_tag "windows-v26.10.1001" $WORKDIR/rv-baseline.env

# ---- composite action 必须声明脚本输出的每一个键 ----
ACTION_YML="$ROOT/.github/actions/resolve-version/action.yml"
missing=""
declared_keys=0
while IFS= read -r key; do
  [[ -z "$key" ]] && continue
  declared_keys=$((declared_keys + 1))
  if ! grep -qE "^  ${key}:" "$ACTION_YML"; then
    missing="$missing $key"
  fi
done < <(env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master \
             GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1 GITHUB_EVENT_NAME=push \
             bash "$SCRIPT" --date 2026-07-12 --print-env | sed 's/=.*//')
if [[ "$declared_keys" -eq 0 ]]; then
  echo "FAIL action-outputs-declared: 脚本没有输出任何键，检查本身失效"
  fail=1
elif [[ -n "$missing" ]]; then
  echo "FAIL action.yml 未声明脚本输出的键:$missing"
  fail=1
else
  echo "OK   action-outputs-declared ($declared_keys keys)"
fi

# ---- ci.yml 的 resolve-version job 必须把脚本输出透出去 ----
CI_YML="$ROOT/.github/workflows/ci.yml"
missing_job=""
job_keys=0
while IFS= read -r key; do
  [[ -z "$key" ]] && continue
  job_keys=$((job_keys + 1))
  if ! grep -qE "steps\.ver\.outputs\.${key}\b" "$CI_YML"; then
    missing_job="$missing_job $key"
  fi
done < <(env GITHUB_REPOSITORY=2746267826/pim-windows GITHUB_REF=refs/heads/master \
             GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1 GITHUB_EVENT_NAME=push \
             bash "$SCRIPT" --date 2026-07-12 --print-env | sed 's/=.*//')
if [[ "$job_keys" -eq 0 ]]; then
  echo "FAIL ci.yml-job-outputs: 脚本没有输出任何键，检查本身失效"
  fail=1
elif [[ -n "$missing_job" ]]; then
  echo "FAIL ci.yml 的 resolve-version job 没透出:$missing_job"
  fail=1
else
  echo "OK   ci.yml-job-outputs ($job_keys keys)"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "resolve-version tests failed"
  exit 1
fi
echo "resolve-version tests passed"
