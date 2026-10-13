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
# 安装策略（全量预备份 → 预写临时文件 → 原子替换 → 复核通过才收尾）：
#   1) 为每个产物做**可校验**的备份：先写 `<name>.part` 再原子改名为 `<name>`，并记录 sha256；
#      （`cp` 写一半就失败时不会污染同名备份；回滚前还要用记录下来的 sha256 复核备份）
#   2) 各写一份同目录 `<name>.tmp-<pid>`（同目录 → rename 不跨文件系统）；
#   3) 逐个 `mv` 原子替换（这一步之后才可能改变仓内文件）；
#   4) 复核通过后才删备份。
# 任一步失败（含复核失败、以及可捕获的终止信号 INT/TERM/HUP）→ `rollback`：
#   * 只用**校验通过**的备份还原（备份不消费，可重试）；
#   * 刷新前不存在的产物 → 删掉本次装进去的（恢复「不存在」）；
#   * 绝不删除无法还原的文件，也不拿可疑备份覆盖正本；
#   * 清掉临时文件、保留未完成的备份目录并打印恢复指引，然后**立即以非 0 退出**。
# 已知限制：SIGKILL 无法捕获，此时会留下临时文件与备份目录（备份目录是刻意保留的救命数据）。
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
    rm -f "$BACKUP/$relative.part"
  done
}

was_missing_before() {
  local candidate
  for candidate in "${MISSING_BEFORE[@]:-}"; do
    [ "$candidate" = "$1" ] && return 0
  done
  return 1
}

rollback() {
  local failed_rc="${1:-$?}"
  [ "$ROLLBACK_DONE" -eq 1 ] && return 0
  ROLLBACK_DONE=1
  set +e
  local restored=0 failed=0 removed=0 relative
  for relative in "${ARTIFACT_RELATIVES[@]}"; do
    if [ -f "$BACKUP/$relative" ]; then
      if [ -f "$BACKUP/$relative.sha256" ] && ! ( cd "$BACKUP" && sha256sum -c --status "$relative.sha256" ); then
        failed=$((failed+1))
        echo "  ❌ 备份校验失败，拒绝用它覆盖正本：$relative（备份：$BACKUP/$relative）" >&2
        continue
      fi
      if cp -p "$BACKUP/$relative" "$ROOT/$relative"; then
        restored=$((restored+1))
      else
        failed=$((failed+1))
        echo "  ❌ 未能还原：$relative（备份仍在 $BACKUP/$relative）" >&2
      fi
    elif was_missing_before "$relative"; then
      # 刷新前不存在 → 恢复「不存在」，删掉本次装进去的
      if [ -e "$ROOT/$relative" ]; then
        rm -f "$ROOT/$relative"
        removed=$((removed+1))
      fi
    else
      failed=$((failed+1))
      echo "  ❌ 无备份可还原：$relative（备份未建立或已损坏；未改动该文件）" >&2
    fi
  done
  remove_temp_files
  if [ "$failed" -eq 0 ]; then
    echo "失败：已回滚 $restored 个产物、移除 $removed 个新增产物，仓内保持刷新前的状态。" >&2
  else
    ROLLBACK_FAILED=1
    echo "失败：$restored 个已还原、$removed 个新增已移除、$failed 个未能还原 —— 仓内可能处于混合状态，" >&2
    echo "     请用 $BACKUP 下的备份手工恢复（校验文件：*.sha256）。" >&2
    if [ "${#INSTALLED[@]}" -eq 0 ]; then
      echo "     注：失败发生在改名之前，本次未改动任何仓内文件。" >&2
    fi
  fi
  exit "$failed_rc"
}
trap 'rollback' ERR
trap 'echo "收到终止信号，回滚…" >&2; rollback 130' INT
trap 'echo "收到终止信号，回滚…" >&2; rollback 143' TERM HUP

# 1) 可校验的预备份：写 .part → 校验 sha256 → 原子改名 → 记录 sha256
#    （必须在任何改名之前完成；这一步失败时仓内一个文件都还没动）
for relative in "${ARTIFACT_RELATIVES[@]}"; do
  target="$ROOT/$relative"
  if [ ! -f "$target" ]; then
    MISSING_BEFORE+=("$relative")
    continue
  fi
  mkdir -p "$BACKUP/$(dirname "$relative")"
  cp -p "$target" "$BACKUP/$relative.part"
  # 写完立刻校验：cp 成功但内容可疑（例如磁盘写满被截断）时这里就会发现
  if ! cmp -s "$target" "$BACKUP/$relative.part"; then
    echo "备份内容与正本不一致（疑似写入失败）：$relative" >&2
    rm -f "$BACKUP/$relative.part"
    false
  fi
  mv -f "$BACKUP/$relative.part" "$BACKUP/$relative"
  ( cd "$BACKUP" && sha256sum "$relative" > "$relative.sha256" )
done

# 2) 预写临时文件
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
trap - ERR INT TERM HUP
remove_temp_files
rm -rf "$BACKUP"

if [ "${#MISSING_BEFORE[@]}" -ne 0 ]; then
  echo "提示：以下产物刷新前不存在，本次为新增：${MISSING_BEFORE[*]}"
fi

echo ""
echo "刷新完成。请把 contract/ 与 src/client-windows/Pim.Client.Core/Contract/ 的改动**一起提交**。"
