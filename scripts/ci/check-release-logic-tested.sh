#!/usr/bin/env bash
#
# 审计 F-06 / AC-COM-04 · **非自证**检查：发版纯逻辑的行为测试必须能对
# **生产脚本本身的真实故障**变红。
#
# 为什么需要这一步：只检查“某个 job 有没有执行 test-release-assets.sh”是字符串检查 ——
# 把测试脚本换成 `exit 0` 就能让守卫变绿（windows-red-grok I-3）。这里改为
# **实际执行**：
#   ① 真实生产脚本 + 行为测试 → 必须通过；
#   ② 往生产脚本注入真实故障（不产出任何资产 / 资产名写错）后，
#      行为测试**必须失败**。
#
# ②是关键：如果测试脚本是空壳（例如被替换成 `exit 0`），它对生产故障不会变红，
# 本检查就会报失败 —— 测试无法“自证”，必须真的打在发版产物上。
#
# 用法：bash scripts/ci/check-release-logic-tested.sh
# 退出码：0 = 生产故障可被检出；1 = 测试对生产故障不敏感（或自身失败）。

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROD="$ROOT/scripts/ci/build-release-assets.sh"
TEST="$ROOT/scripts/ci/test-release-assets.sh"

fail=0

for f in "$PROD" "$TEST"; do
  if [[ ! -f "$f" ]]; then
    echo "FAIL 缺少 $f" >&2
    exit 1
  fi
done

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

echo "==> ① 真实生产脚本 + 行为测试必须通过"
if bash "$TEST" >"$WORKDIR/baseline.log" 2>&1; then
  echo "OK   基线：行为测试通过（退出码 0）"
else
  echo "FAIL 基线：行为测试对真实生产脚本失败（退出码 $?）—— 先修脚本或测试" >&2
  tail -20 "$WORKDIR/baseline.log" >&2
  fail=1
fi

# 对生产脚本的一处注入，跑一次行为测试；期望“变红”。
expect_red() {
  local label="$1" mutated="$2"
  if PIM_RELEASE_ASSETS_SCRIPT="$mutated" bash "$TEST" >"$WORKDIR/$label.log" 2>&1; then
    echo "FAIL 生产故障[$label]没有被行为测试检出（测试是空壳 / 自证）" >&2
    fail=1
  else
    echo "OK   生产故障[$label]被行为测试检出（退出码非 0）"
  fi
}

echo "==> ② 生产脚本故障必须让行为测试变红"

# 故障 A：脚本什么都不产出就成功返回（“静默成功 / 缺产物不报错”这一类）。
MUT_A="$WORKDIR/mutated-empty.sh"
{ echo '#!/usr/bin/env bash'; echo 'exit 0'; tail -n +2 "$PROD"; } > "$MUT_A"
expect_red 'produces-nothing' "$MUT_A"

# 故障 B：资产名写错（version.json 与产物名不再一致）。
MUT_B="$WORKDIR/mutated-asset-name.sh"
sed 's/^WINDOWS_ASSET="pim-windows-v/WINDOWS_ASSET="pim-windows-X/' "$PROD" > "$MUT_B"
if cmp -s "$PROD" "$MUT_B"; then
  echo "FAIL 无法注入资产名故障（生产脚本里找不到 WINDOWS_ASSET 赋值锚点）" >&2
  fail=1
else
  expect_red 'asset-name-drift' "$MUT_B"
fi

# 故障 C：version.json 自检被删掉且写入错误的 version 字段。
MUT_C="$WORKDIR/mutated-version-field.sh"
sed 's/--arg v "\$VERSION"/--arg v "0.0.0"/' "$PROD" > "$MUT_C"
if cmp -s "$PROD" "$MUT_C"; then
  echo "FAIL 无法注入 version 字段故障" >&2
  fail=1
else
  expect_red 'wrong-version-field' "$MUT_C"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "" >&2
  echo "release-logic-not-self-certifying: 失败（行为测试没有真正约束生产脚本）" >&2
  exit 1
fi

echo ""
echo "OK   发版纯逻辑的行为测试对生产故障敏感（不是自证）"
