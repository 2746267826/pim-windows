#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/resolve-version.sh"
fail=0

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
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1042" $WORKDIR/rv-master.env
assert_eq release_tag "api-v26.07.1042" $WORKDIR/rv-master.env
assert_eq version_code "101042" $WORKDIR/rv-master.env
assert_eq is_release "true" $WORKDIR/rv-master.env
assert_eq git_sha_short "abcdef1" $WORKDIR/rv-master.env
assert_eq year_month "26.07" $WORKDIR/rv-master.env
assert_eq artifact_slug "26.07.1042" $WORKDIR/rv-master.env

# PR
run_case pr \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/pull/12/merge GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=pull_request GITHUB_REF_NAME=12/merge \
      PR_NUMBER=12 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1042-pr.12+abcdef1" $WORKDIR/rv-pr.env
assert_eq release_tag "api-v26.07.1042" $WORKDIR/rv-pr.env
assert_eq version_code "101042" $WORKDIR/rv-pr.env
assert_eq is_release "false" $WORKDIR/rv-pr.env
assert_eq artifact_slug "26.07.1042-pr.12-abcdef1" $WORKDIR/rv-pr.env

# dispatch non-master
run_case dev \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/codex/foo GITHUB_RUN_NUMBER=7 GITHUB_SHA=deadbeefcafebabe \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1007-dev+deadbee" $WORKDIR/rv-dev.env
assert_eq is_release "false" $WORKDIR/rv-dev.env

# 审计 F-22：发版只允许由 push 到 master 触发。
# 手动 workflow_dispatch 即使 ref 是 master 也不得发版 —— 手动重跑/演练不应产生发布。
run_case dispatch_master \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq is_release "false" $WORKDIR/rv-dispatch_master.env
assert_eq version "26.07.1042-dev+abcdef1" $WORKDIR/rv-dispatch_master.env

# 对照：同一个 REF 换成真正的 push 事件，必须发版。
run_case push_master \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq is_release "true" $WORKDIR/rv-push_master.env

# client patch on master（push 事件，仍须发版并带上 patch 后缀）
run_case patch \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push CLIENT_PATCH=android.1 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.1042+android.1" $WORKDIR/rv-patch.env
assert_eq is_release "true" $WORKDIR/rv-patch.env

# 手动触发带 CLIENT_PATCH：仍不得发版（同上，事件类型是硬条件）。
run_case patch_dispatch \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=workflow_dispatch CLIENT_PATCH=android.1 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq is_release "false" $WORKDIR/rv-patch_dispatch.env

# ---- 历史基线补偿：最小的运行序号也必须超过拆仓前的历史最大值 ----
# 拆仓前（monorepo 时代）客户端最后发到 v2026.10.815（versionCode 100815，
# 程序集/文件版本 2026.10.815.0）。取 GITHUB_RUN_NUMBER=1（新仓能出现的最小值）验证
# 补偿基数生效：显示版本最后一段 > 815、versionCode > 100815、
# 程序集版本第三段 > 815 —— 否则用户已装的旧版永远看不到更新。
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
# action.yml 里没声明的 output 不会透出给调用方，上游拿到的是空字符串。
# 症状是发版时报「GitHub Releases requires a tag」这类看不懂的错，而且直接跑
# 这个脚本是测不出来的（脚本本身输出正常）。这里把两者钉在一起。
ACTION_YML="$ROOT/.github/actions/resolve-version/action.yml"
missing=""
declared_keys=0
while IFS= read -r key; do
  [[ -z "$key" ]] && continue
  declared_keys=$((declared_keys + 1))
  if ! grep -qE "^  ${key}:" "$ACTION_YML"; then
    missing="$missing $key"
  fi
done < <(env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master \
             GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1 GITHUB_EVENT_NAME=push \
             bash "$SCRIPT" --date 2026-07-12 --print-env | sed 's/=.*//')
# 键数为 0 说明脚本没输出（例如它自己报错退出）—— 这同样是失败，不能当成"全都没漏"。
if [[ "$declared_keys" -eq 0 ]]; then
  echo "FAIL action-outputs-declared: 脚本没有输出任何键，检查本身失效"
  fail=1
elif [[ -n "$missing" ]]; then
  echo "FAIL action.yml 未声明脚本输出的键:$missing"
  fail=1
else
  echo "OK   action-outputs-declared ($declared_keys keys)"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "resolve-version tests failed"
  exit 1
fi
echo "resolve-version tests passed"
