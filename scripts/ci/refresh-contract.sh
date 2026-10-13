#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约「一键刷新」：快照 + 生成物成组更新（writer；会写仓）。
#
# 为什么必须绑成一个脚本（工单 §七）：生成物里**嵌有契约快照的 sha256**，快照一变生成物就必须
# 重新生成，否则一致性门禁会红。契约刷新不是「改一个 JSON」，而是「快照 + 生成物」一组动作，
# 不能留给人工记（pim-android#12 真实踩过这个坑，CI 红过一次）。
#
# 本脚本 = scripts/ci/fetch-contract.sh（拉快照 + provenance）+ scripts/ci/generate-contract-csharp.sh
# （重新生成 C# 产物）。任一步失败即整体失败，不做「快照更新了、生成物没跟上」的半成品。
#
# 只读校验请用 scripts/ci/check-contract-csharp.sh（CI 用的是它）。
#
# 用法：
#   bash scripts/ci/refresh-contract.sh                       # 默认 2746267826/pim-api@master
#   PIM_CONTRACT_REF=<40-hex|branch|tag> bash scripts/ci/refresh-contract.sh
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

if [ "$#" -ne 0 ]; then
  echo "refresh-contract.sh 不接受位置参数（收到：$*）。" >&2
  exit 2
fi

echo "== ① 刷新契约快照（contract/openapi.json + contract/provenance.json） =="
bash "$HERE/fetch-contract.sh"

echo ""
echo "== ② 重新生成 C# 生成物（与刚写入的快照同批） =="
bash "$HERE/generate-contract-csharp.sh"

echo ""
echo "== ③ 复核：快照与生成物必须一致（不一致就说明这一批没做完） =="
bash "$HERE/check-contract-csharp.sh"

echo ""
echo "刷新完成。请把 contract/ 与 src/client-windows/Pim.Client.Core/Contract/ 的改动**一起提交**。"
