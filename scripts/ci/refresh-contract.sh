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
echo "== ③ 两步都成功了，才开始安装（任一步失败过，这里根本不会执行） =="
# 安装策略（预备份 → 预写临时文件 → 原子替换 → 复核通过才收尾）：
#   1) 为 4 个产物全部预备份到仓外（**先于任何改名**，且刷新前不存在的产物也登记下来）；
#   2) 各写一份同目录 `<name>.tmp-<pid>`；
#   3) 逐个 `mv` 原子替换（POSIX rename；这一步之后才可能改变仓内文件）；
#   4) 复核（check-contract-csharp.sh）通过后才删备份。
# 任一步失败（含复核失败）→ `rollback`：用备份 `cp` 还原（备份**不消费**，可重试），
# 清掉临时文件，**立即以非 0 退出**（不返回主流程 —— 否则会带着半成品继续往下走）。
# 若还原本身也失败：保留备份目录、打印未还原清单，绝不删除无法还原的文件。
BACKUP="$TMP/backup"
mkdir -p "$BACKUP"
INSTALLED=()
MISSING_BEFORE=()
ROLLBACK_DONE=0
ROLLBACK_FAILED=0

cleanup() {
  if [ "$ROLLBACK_FAILED" -eq 0 ]; then
    rm -rf "$TMP"
  else
    echo "⚠️ 回滚未完整成功：备份**保留**在 $BACKUP" >&2
  fi
}
trap cleanup EXIT

remove_temp_files() {
  local relative
  for relative in "${ARTIFACT_RELATIVES[@]}"; do
    rm -f "$ROOT/$relative.tmp-$$"
  done
}

rollback() {
  local failed_rc=$?
  [ "$ROLLBACK_DONE" -eq 1 ] && return 0
  ROLLBACK_DONE=1
  set +e
  local restored=0 failed=0 missing=0 relative
  for relative in "${ARTIFACT_RELATIVES[@]}"; do
    if [ -f "$BACKUP/$relative" ]; then
      # 用 cp 还原（不消费备份，失败还能重试）；失败的记下来。
      if cp -p "$BACKUP/$relative" "$ROOT/$relative"; then
        restored=$((restored+1))
      else
        failed=$((failed+1))
        echo "  ❌ 未能还原：$relative（备份仍在 $BACKUP/$relative）" >&2
      fi
    else
      # 没有备份 = 刷新前不存在。**不删除**任何东西（可能是备份阶段就失败了，
      # 那个文件是刷新前的正本）；只如实报告。
      missing=$((missing+1))
      echo "  ⚠️ 无备份可还原：$relative（刷新前不存在，或备份未建立）" >&2
    fi
  done
  remove_temp_files
  if [ "$failed" -eq 0 ]; then
    echo "失败：已回滚 $restored 个产物，仓内保持刷新前的状态。" >&2
  else
    ROLLBACK_FAILED=1
    echo "失败：$restored 个已还原、$failed 个未能还原、$missing 个无备份 —— 仓内可能处于混合状态，" >&2
    echo "     请用 $BACKUP 下的备份手工恢复。" >&2
  fi
  exit "$failed_rc"
}
trap 'rollback' ERR

# 1) 预备份：必须在任何改名之前完成（这一步失败 → 仓内一个文件都还没动）
for relative in "${ARTIFACT_RELATIVES[@]}"; do
  target="$ROOT/$relative"
  if [ -f "$target" ]; then
    mkdir -p "$BACKUP/$(dirname "$relative")"
    cp -p "$target" "$BACKUP/$relative"
  else
    MISSING_BEFORE+=("$relative")
  fi
done

# 2) 预写临时文件（同目录，保证 rename 不跨文件系统）
for relative in "${ARTIFACT_RELATIVES[@]}"; do
  mkdir -p "$(dirname "$ROOT/$relative")"
  cp -p "$SB/$relative" "$ROOT/$relative.tmp-$$"
done

# 3) 原子替换
for relative in "${ARTIFACT_RELATIVES[@]}"; do
  mv -f "$ROOT/$relative.tmp-$$" "$ROOT/$relative"
  INSTALLED+=("$relative")
  echo "  安装 $relative"
done

echo ""
echo "== ④ 复核：快照与生成物必须一致（不一致就回滚，不留脏文件） =="
bash "$HERE/check-contract-csharp.sh"

# 复核通过：解除回滚保护、清掉备份与任何临时文件
trap - ERR
remove_temp_files
rm -rf "$BACKUP"

if [ "${#MISSING_BEFORE[@]}" -ne 0 ]; then
  echo "提示：以下产物刷新前不存在，本次为新增：${MISSING_BEFORE[*]}"
fi

echo ""
echo "刷新完成。请把 contract/ 与 src/client-windows/Pim.Client.Core/Contract/ 的改动**一起提交**。"
