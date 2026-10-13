#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约「一键刷新」：快照 + 生成物成组更新（writer；会写仓）。
#
# 为什么必须绑成一个脚本（工单 §七）：生成物里**嵌有契约快照的 sha256**，快照一变生成物就必须
# 重新生成，否则一致性门禁会红。契约刷新不是「改一个 JSON」，而是「快照 + 生成物」一组动作，
# 不能留给人工记（pim-android#12 真实踩过这个坑，CI 红过一次）。
#
# 本脚本 = scripts/ci/fetch-contract.sh（拉快照 + provenance）+ 生成器（重新生成 C# 产物），
# 并且是**全有或全无**：两步都先在仓外沙箱里跑完，全部成功后才一次性把 4 个产物安装进仓。
# 任一步失败 → 仓内一个文件都不会被改动（不会留下「新快照 + 旧生成物」的半成品状态）。
#
# 实现说明：fetch-contract.sh 只写仓内默认路径（它不认 PIM_CONTRACT_SNAPSHOT / PIM_CONTRACT_PROVENANCE
# —— 那两个 env 是 check 侧的测试旁路，见 contract-freshness-lib.sh 的 CI 旁路守卫，本脚本**不碰**它们）。
# 因此这里把 scripts/ 与 contract/ 复制到临时沙箱、在沙箱里完成「刷新 + 生成」，再安装回仓。
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

ARTIFACT_RELATIVES=(
  "contract/openapi.json"
  "contract/provenance.json"
  "src/client-windows/Pim.Client.Core/Contract/ContractModels.cs"
  "src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs"
)

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-refresh.XXXXXX")"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

SB="$TMP/sb"
mkdir -p "$SB"
cp -a "$ROOT/scripts" "$SB/scripts"
cp -a "$ROOT/contract" "$SB/contract"

echo "== ① 在沙箱里拉取契约快照（先不碰仓） =="
bash "$SB/scripts/ci/fetch-contract.sh"

echo ""
echo "== ② 在沙箱里用刚拉到的快照重新生成 C# 生成物（先不碰仓） =="
python3 "$SB/scripts/contract/generate-csharp.py" --out-dir "$SB"

echo ""
echo "== ③ 两步都成功了，才一次性安装进仓（任一步失败过，这里根本不会执行） =="
for relative in "${ARTIFACT_RELATIVES[@]}"; do
  install -D -m 644 "$SB/$relative" "$ROOT/$relative"
  echo "  安装 $relative"
done

echo ""
echo "== ④ 复核：快照与生成物必须一致（不一致就说明这一批没做完） =="
bash "$HERE/check-contract-csharp.sh"

echo ""
echo "刷新完成。请把 contract/ 与 src/client-windows/Pim.Client.Core/Contract/ 的改动**一起提交**。"
