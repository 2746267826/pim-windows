#!/usr/bin/env bash
#
# 审计 REQ-COM-03 / AC-COM-03：本仓的契约快照必须与**权威源**（公有 `pim-api` 仓）一致。
#
# 背景：`contract/openapi.json` 只是快照。快照落后不会让任何本地检查变红，
# 于是客户端可能按过期形状对接接口（审计明确记了这一点）。本检查把它变成可机械判定：
#   - 取权威 `pim-api` 的契约（公有 raw.githubusercontent.com，**需要网络，不需要凭据**）；
#   - 与仓内快照逐字节比较；
#   - 不一致即失败，并提示用 `scripts/ci/fetch-contract.sh` 刷新（本脚本**绝不**改写契约，
#     不手改 JSON 来遮蔽偏差）。
#
# 用法：bash scripts/ci/check-contract-freshness.sh
# 退出码：0 = 快照与权威源一致；1 = 不一致、或取不到权威源（fail-closed）。
#
# 仅用于测试的覆盖（不放进 CI）：
#   PIM_CONTRACT_AUTHORITATIVE_FILE=<path>  用本地文件当权威源（离线自检用）
#   PIM_CONTRACT_SNAPSHOT=<path>            改比对对象（离线自检用）

set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SNAPSHOT="${PIM_CONTRACT_SNAPSHOT:-$ROOT/contract/openapi.json}"

REPO="${PIM_CONTRACT_REPO:-2746267826/pim-api}"
REF="${PIM_CONTRACT_REF:-master}"
RAW_BASE="${PIM_CONTRACT_RAW_BASE:-https://raw.githubusercontent.com}"

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
AUTHORITATIVE="$WORKDIR/authoritative-openapi.json"

fail() {
  echo "contract-freshness: $*" >&2
  exit 1
}

sha() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

[[ -f "$SNAPSHOT" ]] || fail "仓内契约快照不存在：$SNAPSHOT"

if [[ -n "${PIM_CONTRACT_AUTHORITATIVE_FILE:-}" ]]; then
  [[ -f "$PIM_CONTRACT_AUTHORITATIVE_FILE" ]] \
    || fail "PIM_CONTRACT_AUTHORITATIVE_FILE 指向的文件不存在：$PIM_CONTRACT_AUTHORITATIVE_FILE"
  cp "$PIM_CONTRACT_AUTHORITATIVE_FILE" "$AUTHORITATIVE"
  SOURCE="local:$PIM_CONTRACT_AUTHORITATIVE_FILE"
else
  URL="$RAW_BASE/$REPO/$REF/contract/openapi.json"
  SOURCE="$REPO@$REF"
  if ! curl -fsSL "$URL" -o "$AUTHORITATIVE"; then
    fail "无法取得权威契约 $URL（需要网络；取不到即失败，不能当作“快照是新的”）"
  fi
fi

# 健全性：必须是 JSON 对象，别把 404/HTML 当契约。
if ! head -c 1 "$AUTHORITATIVE" | grep -q '{'; then
  fail "权威契约不是 JSON 对象（疑似错误页），来源：$SOURCE"
fi
if command -v jq >/dev/null 2>&1; then
  jq -e . "$AUTHORITATIVE" >/dev/null 2>&1 || fail "权威契约不是合法 JSON，来源：$SOURCE"
fi

if cmp -s "$SNAPSHOT" "$AUTHORITATIVE"; then
  echo "OK   契约快照与权威源一致（$SOURCE，sha256 $(sha "$SNAPSHOT")）"
  exit 0
fi

echo "FAIL 契约快照已过期：" >&2
echo "     快照   $SNAPSHOT sha256=$(sha "$SNAPSHOT")" >&2
echo "     权威源 $SOURCE sha256=$(sha "$AUTHORITATIVE")" >&2
if command -v diff >/dev/null 2>&1; then
  echo "     差异摘要（前 20 行）：" >&2
  diff <(cat "$SNAPSHOT") <(cat "$AUTHORITATIVE") | head -20 >&2 || true
fi
fail "请运行 scripts/ci/fetch-contract.sh 刷新快照并在 PR 里说明原因（不要手工编辑 JSON）"
