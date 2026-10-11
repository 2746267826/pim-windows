#!/usr/bin/env bash
#
# 审计 REQ-COM-03 / AC-COM-03 的**行为测试**：契约新鲜度检查必须能对
# “已提交的旧快照”变红，而不是恒绿。
#
# 全程离线：用 PIM_CONTRACT_AUTHORITATIVE_FILE / PIM_CONTRACT_SNAPSHOT
# 把比对对象换成临时文件，因此不需要网络，也不会改到仓内契约。
#
# 用法：bash scripts/ci/test-contract-freshness.sh
# 退出码：0 = 全部用例通过；1 = 有用例失败。

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CHECK="$ROOT/scripts/ci/check-contract-freshness.sh"
SNAPSHOT="$ROOT/contract/openapi.json"

fail=0
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# 用例 1：快照 == 权威源 → 必须通过。
if PIM_CONTRACT_AUTHORITATIVE_FILE="$SNAPSHOT" \
   PIM_CONTRACT_SNAPSHOT="$SNAPSHOT" \
   bash "$CHECK" >"$WORKDIR/same.log" 2>&1; then
  echo "OK   快照与权威源一致时通过"
else
  echo "FAIL 快照与权威源一致时仍然失败"; cat "$WORKDIR/same.log"; fail=1
fi

# 用例 2：快照是旧的（少了一个 operation）→ 必须失败。
STALE="$WORKDIR/stale-openapi.json"
if command -v jq >/dev/null 2>&1; then
  jq 'del(.paths["/api/v1/daemon/heartbeat"])' "$SNAPSHOT" > "$STALE"
else
  # 没有 jq 时用 node 做等价处理（两种都产生“内容确实不同”的旧快照）。
  node -e '
    const fs = require("node:fs");
    const doc = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
    delete doc.paths["/api/v1/daemon/heartbeat"];
    fs.writeFileSync(process.argv[2], JSON.stringify(doc));
  ' "$SNAPSHOT" "$STALE"
fi

if PIM_CONTRACT_AUTHORITATIVE_FILE="$SNAPSHOT" \
   PIM_CONTRACT_SNAPSHOT="$STALE" \
   bash "$CHECK" >"$WORKDIR/stale.log" 2>&1; then
  echo "FAIL 已提交的旧快照没有被检出（检查恒绿）"; fail=1
else
  echo "OK   已提交的旧快照被检出（退出码非 0）"
fi

# 用例 3：取不到权威源时必须 fail-closed（不能把“拿不到”当成“是新的”）。
if PIM_CONTRACT_AUTHORITATIVE_FILE="$WORKDIR/does-not-exist.json" \
   PIM_CONTRACT_SNAPSHOT="$SNAPSHOT" \
   bash "$CHECK" >"$WORKDIR/missing.log" 2>&1; then
  echo "FAIL 权威源取不到时检查仍然通过（fail-open）"; fail=1
else
  echo "OK   权威源取不到时检查失败（fail-closed）"
fi

# 用例 4：权威源内容不是 JSON 对象 → 必须失败（避免把错误页当契约比对）。
printf '<html>404</html>' > "$WORKDIR/notjson.json"
if PIM_CONTRACT_AUTHORITATIVE_FILE="$WORKDIR/notjson.json" \
   PIM_CONTRACT_SNAPSHOT="$SNAPSHOT" \
   bash "$CHECK" >"$WORKDIR/notjson.log" 2>&1; then
  echo "FAIL 权威源不是 JSON 时检查仍然通过"; fail=1
else
  echo "OK   权威源不是 JSON 时检查失败"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "" >&2
  echo "contract-freshness 行为测试失败" >&2
  exit 1
fi

echo ""
echo "contract-freshness 行为测试通过"
