#!/usr/bin/env bash
#
# 审计 F-06 / AC-COM-04 的**行为测试**：build-release-assets.sh 必须真的
# 产出正确的资产名与 version.json，并且在产物缺失/输入非法时**失败**。
#
# 这些用例不需要任何发布权限，也不访问网络 —— 所以可以在每个 PR 上真实执行。
#
# 用法：bash scripts/ci/test-release-assets.sh
# 退出码：0 = 全部通过；1 = 有用例失败。
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# 默认测真实生产脚本；check-release-logic-tested.sh 用这个环境变量把测试指向
# 注入了故障的副本，从而证明测试对生产故障敏感（而不是空壳自证）。
SCRIPT="${PIM_RELEASE_ASSETS_SCRIPT:-$ROOT/scripts/ci/build-release-assets.sh}"
fail=0

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

check() {
  local label="$1" actual="$2" expected="$3"
  if [[ "$actual" == "$expected" ]]; then
    echo "OK   $label = $actual"
  else
    echo "FAIL $label: expected [$expected] got [$actual]"
    fail=1
  fi
}

# ---------------------------------------------------------------------------
# 用例 1：完整输入 —— 资产名、version.json 内容、url 都必须正确。
# ---------------------------------------------------------------------------
DIST="$WORKDIR/dist"; OUT="$WORKDIR/out"
mkdir -p "$DIST"
printf 'windows' > "$DIST/pim-windows-v26.10.42.zip"
printf 'shell'   > "$DIST/pim-shell-windows-v26.10.42.zip"
printf 'setup'   > "$DIST/PIM-Setup-v26.10.42.exe"

bash "$SCRIPT" --version 26.10.42 --tag pim-v26.10.42 \
  --repository 2746267826/pim-windows --dist "$DIST" --out "$OUT" 2>/dev/null
status=$?
check "完整输入退出码" "$status" "0"

for asset in pim-windows-v26.10.42.zip pim-shell-windows-v26.10.42.zip PIM-Setup-v26.10.42.exe version.json; do
  if [[ -f "$OUT/$asset" ]]; then echo "OK   产出资产 $asset"; else echo "FAIL 缺少产出资产 $asset"; fail=1; fi
done

check "version.json version" "$(jq -r '.version' "$OUT/version.json")" "26.10.42"
check "version.json windows url" \
  "$(jq -r '.assets.windows.url' "$OUT/version.json")" \
  "https://github.com/2746267826/pim-windows/releases/download/pim-v26.10.42/pim-windows-v26.10.42.zip"
check "version.json shellWindows url" \
  "$(jq -r '.assets.shellWindows.url' "$OUT/version.json")" \
  "https://github.com/2746267826/pim-windows/releases/download/pim-v26.10.42/pim-shell-windows-v26.10.42.zip"
check "version.json installer url" \
  "$(jq -r '.assets.installer.url' "$OUT/version.json")" \
  "https://github.com/2746267826/pim-windows/releases/download/pim-v26.10.42/PIM-Setup-v26.10.42.exe"
if jq -e '.releasedAt | type == "string"' "$OUT/version.json" >/dev/null; then
  echo "OK   version.json releasedAt 是字符串"
else
  echo "FAIL version.json releasedAt 缺失或类型不对"; fail=1
fi

# 用例 1b：真的没有安装器时，只有显式 --allow-missing-installer 才允许继续，
# 且不得写出 installer 键（否则客户端更新检查会拿到 404 链接）。
DIST_NO_SETUP="$WORKDIR/dist-nosetup"; OUT_NO_SETUP="$WORKDIR/out-nosetup"
mkdir -p "$DIST_NO_SETUP"
printf 'windows' > "$DIST_NO_SETUP/pim-windows-v26.10.42.zip"
printf 'shell'   > "$DIST_NO_SETUP/pim-shell-windows-v26.10.42.zip"

bash "$SCRIPT" --version 26.10.42 --tag pim-v26.10.42 \
  --repository 2746267826/pim-windows --dist "$DIST_NO_SETUP" --out "$OUT_NO_SETUP" \
  --allow-missing-installer 2>/dev/null
check "无安装器 + --allow-missing-installer 退出码" "$?" "0"
if [[ -f "$OUT_NO_SETUP/PIM-Setup-v26.10.42.exe" ]]; then
  echo "FAIL 无安装器时不应产出安装器资产"; fail=1
else
  echo "OK   无安装器时不产出安装器资产"
fi
check "无安装器时 version.json 不含 installer 键" \
  "$(jq -r '.assets | has("installer")' "$OUT_NO_SETUP/version.json")" "false"

# ---------------------------------------------------------------------------
# 用例 2：缺必需产物必须失败，且**不得**留下 version.json。
# ---------------------------------------------------------------------------
missing_case() {
  local label="$1" keep="$2" drop="$3"
  local d="$WORKDIR/dist-$label" o="$WORKDIR/out-$label"
  rm -rf "$d" "$o"; mkdir -p "$d"
  printf 'x' > "$d/pim-windows-v9.9.9.zip"
  printf 'x' > "$d/pim-shell-windows-v9.9.9.zip"
  printf 'x' > "$d/PIM-Setup-v9.9.9.exe"
  rm -f "$d/$drop"
  bash "$SCRIPT" --version 9.9.9 --tag t --repository o/r --dist "$d" --out "$o" >/dev/null 2>&1
  local st=$?
  if [[ "$st" -eq 0 ]]; then
    echo "FAIL 缺少 $keep 时脚本仍然成功（应失败）"; fail=1
  else
    echo "OK   缺少 $keep 时脚本失败（退出码 $st）"
  fi
  if [[ -f "$o/version.json" ]]; then
    echo "FAIL 缺少 $keep 时仍生成了 version.json"; fail=1
  else
    echo "OK   缺少 $keep 时没有生成 version.json"
  fi
}

missing_case windows 'pim-windows-v9.9.9.zip' 'pim-windows-v9.9.9.zip'
missing_case shell 'pim-shell-windows-v9.9.9.zip' 'pim-shell-windows-v9.9.9.zip'
missing_case setup 'PIM-Setup-v9.9.9.exe' 'PIM-Setup-v9.9.9.exe'

# ---------------------------------------------------------------------------
# 用例 3：非法输入必须失败。
# ---------------------------------------------------------------------------
run_expect_fail() {
  local label="$1"; shift
  bash "$SCRIPT" "$@" >/dev/null 2>&1
  local st=$?
  if [[ "$st" -eq 0 ]]; then echo "FAIL $label 时脚本仍然成功（应失败）"; fail=1
  else echo "OK   $label 时脚本失败（退出码 $st）"; fi
}

run_expect_fail "缺少 --tag" --version 1.2.3 --repository o/r --dist "$DIST" --out "$WORKDIR/o1"
run_expect_fail "缺少 --repository" --version 1.2.3 --tag t --dist "$DIST" --out "$WORKDIR/o2"
run_expect_fail "dist 目录不存在" --version 1.2.3 --tag t --repository o/r --dist "$WORKDIR/nope" --out "$WORKDIR/o3"
run_expect_fail "版本号非法" --version 'v1.2' --tag t --repository o/r --dist "$DIST" --out "$WORKDIR/o4"
run_expect_fail "未知参数" --version 1.2.3 --tag t --repository o/r --dist "$DIST" --out "$WORKDIR/o5" --bogus

# ---------------------------------------------------------------------------
# 用例 4：产物不唯一必须失败（避免把随机一个 zip 当成发布资产）。
# ---------------------------------------------------------------------------
D4="$WORKDIR/dist-dup"; O4="$WORKDIR/out-dup"
rm -rf "$D4" "$O4"; mkdir -p "$D4"
printf 'a' > "$D4/pim-windows-v9.9.9.zip"
printf 'b' > "$D4/pim-windows-v9.9.9-extra.zip"
printf 'x' > "$D4/pim-shell-windows-v9.9.9.zip"
bash "$SCRIPT" --version 9.9.9 --tag t --repository o/r --dist "$D4" --out "$O4" \
  --allow-missing-installer >/dev/null 2>&1
st=$?
if [[ "$st" -eq 0 ]]; then echo "FAIL windows 产物不唯一时脚本仍然成功（应失败）"; fail=1
else echo "OK   windows 产物不唯一时脚本失败（退出码 $st）"; fi

echo ""
if [[ "$fail" -ne 0 ]]; then
  echo "release-assets 行为测试失败"
  exit 1
fi
echo "release-assets 行为测试通过"
