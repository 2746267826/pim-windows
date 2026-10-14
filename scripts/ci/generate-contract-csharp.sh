#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约 C# 生成物「重新生成」（writer；会写仓）。
#
# 从仓内契约快照 contract/openapi.json + scripts/contract/client-operations.json 生成：
#   src/client-windows/Pim.Client.Core/Contract/ContractModels.cs     （契约 DTO）
#   src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs （operation 与路径）
# 生成物随提交进仓，**不要手工修改** —— 手工改了会被 scripts/ci/check-contract-csharp.sh
# （CI）逐字节比对抓出来。
#
# 什么时候跑：
#   * 契约快照刷新之后 —— 正常情况下应该用 scripts/ci/refresh-contract.sh（快照 + 生成物同批）；
#   * 往 scripts/contract/client-operations.json 里加了客户端要用的 operation 之后。
#
# 与 scripts/ci/check-contract-csharp.sh 的分工：「谁写」= 本脚本（人工执行）；
# 「谁只读」= check-contract-csharp.sh（CI 调的就是它）。
#
# 用法：
#   bash scripts/ci/generate-contract-csharp.sh
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

if [ "$#" -ne 0 ]; then
  echo "generate-contract-csharp.sh 不接受位置参数（收到：$*）。" >&2
  exit 2
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "需要 python3（生成器只用标准库，不装任何依赖）。" >&2
  exit 2
fi

echo "== 从契约快照重新生成 C# DTO 与 operation 定义 =="
python3 "$ROOT/scripts/contract/generate-csharp.py" --out-dir "$ROOT"

echo ""
echo "已更新的文件："
git -C "$ROOT" status --short -- src/client-windows/Pim.Client.Core/Contract || true
echo ""
echo "请把生成物一并提交；CI 会用 scripts/ci/check-contract-csharp.sh 逐字节复核。"
