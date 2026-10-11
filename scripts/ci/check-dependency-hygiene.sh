#!/usr/bin/env bash
# 审计整改（阶段 A）· REQ-COM-07 / REQ-WIN-02 的**真实命令**检查：
#   F-07  未使用的 Microsoft.Data.Sqlite 不得进入依赖图与 publish 产物
#   F-18  Release 构建不得产生 CS8602 可空性警告
#   F-17  AGENTS.md 里写的本地门禁命令必须真的能跑，且覆盖全部工程
#
# 用法：bash scripts/ci/check-dependency-hygiene.sh [--check F-07|F-18|F-17|all] [--out <dir>]
# 退出码：0 = 通过；1 = 存在违反；2 = 用法错误
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CHECK="all"
OUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --check) CHECK="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
done

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
fail=0

say() { echo "$*"; }
violation() { echo "FAIL [$1] $2"; fail=1; }
ok() { echo "OK   [$1] $2"; }

# ---------------------------------------------------------------------------
# F-07：依赖图与产物里不得有未使用的 SQLite 原生库
#
# 审计发现的形态是一个**空的** Pim.Client.Infrastructure 工程把
# Microsoft.Data.Sqlite 带进依赖图与 publish 产物。修法是删掉这个空工程，而不是
# 保留工程再升级包版本；因此这里同时守住两件事：
#   ① 空工程不允许回来（回来了就重新把未使用的 sqlite 拖进产物）；
#   ② 全仓确实没有任何代码使用 SQLite —— 若将来真的有代码要用，必须先开工单，
#      不能靠悄悄恢复一个空工程把依赖带回来。
# ---------------------------------------------------------------------------
check_f07() {
  say "--- F-07: 未使用的 SQLite 依赖 ---"

  local infra_dir="$ROOT/src/client-windows/Pim.Client.Infrastructure"
  if [[ -d "$infra_dir" ]]; then
    local cs_count
    cs_count="$(find "$infra_dir" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | wc -l)"
    violation F-07 "Pim.Client.Infrastructure 目录又出现了（含 $cs_count 个 .cs 文件）：它是把未使用的 Microsoft.Data.Sqlite 带进依赖图和 publish 产物的来源，已按审计删除。"
  else
    ok F-07 "空的 Pim.Client.Infrastructure 工程已删除。"
  fi

  local sqlite_users
  sqlite_users="$(grep -rln 'Microsoft\.Data\.Sqlite\|SQLitePCLRaw\|SqliteConnection' \
    --include='*.cs' --include='*.csproj' "$ROOT/src" "$ROOT/tests" 2>/dev/null \
    | grep -v '/bin/\|/obj/' || true)"
  if [[ -n "$sqlite_users" ]]; then
    violation F-07 "仓库里出现了实际使用 SQLite 的代码：$sqlite_users（若确实需要，另开工单评估，不能静默留着）。"
  else
    ok F-07 "全仓没有任何代码使用 SQLite（依赖纯属空工程带入）。"
  fi

  local restore_log="$WORKDIR/restore.log"
  dotnet restore "$ROOT/tests/Pim.Client.Tests/Pim.Client.Tests.csproj" --force >"$restore_log" 2>&1
  if grep -q 'NU1903\|NU1902\|NU1901' "$restore_log"; then
    violation F-07 "restore 报出已知漏洞依赖：$(grep -m1 -o "Package '[^']*' [^ ]* has a known [a-z ]* severity vulnerability" "$restore_log")"
  else
    ok F-07 "restore 没有报出已知漏洞依赖。"
  fi

  # 依赖图本身也不得再解析出 sqlite（只删目录、别处又直接引包时这里会红）。
  local assets="$ROOT/src/client-windows/Pim.Client.App/obj/project.assets.json"
  if [[ -f "$assets" ]] && grep -qi 'sqlite' "$assets"; then
    violation F-07 "Pim.Client.App 的 project.assets.json 里仍解析出 sqlite 依赖（依赖图没有真正变干净）。"
  else
    ok F-07 "App 的依赖图里没有 sqlite。"
  fi

  local pub="$WORKDIR/publish"
  dotnet publish "$ROOT/src/client-windows/Pim.Client.App/Pim.Client.App.csproj" \
    -c Release -o "$pub" --self-contained false -p:EnableWindowsTargeting=true \
    >"$WORKDIR/publish.log" 2>&1
  local publish_status=$?
  if [[ $publish_status -ne 0 ]]; then
    violation F-07 "publish 失败（退出码 $publish_status）：$(grep -m1 -E 'error' "$WORKDIR/publish.log" || true)"
  else
    local hits
    hits="$(find "$pub" -iname '*sqlite*' 2>/dev/null || true)"
    if [[ -n "$hits" ]]; then
      violation F-07 "publish 产物里仍含 SQLite 组件：$(echo "$hits" | tr '\n' ' ')"
    else
      ok F-07 "publish 产物内不含任何 sqlite 组件。"
    fi
  fi
}

# ---------------------------------------------------------------------------
# F-18：Release 构建不得产生 CS8602
#
# 审计写「1 处」、阶段 A 的 `grep -c` 报「2 处」：那 2 条其实是同一行
# （ApiClient.cs(302,28)）在编译过程里打印一次、在 MSBuild 摘要里又出现一次。
# 因此这里按**唯一位置**（文件:行:列）计数，并额外用 MSBuild 自己汇总的
# `N Warning(s)` 交叉校验 —— 两者不一致说明日志解析方式变了，不能继续当通过。
# ---------------------------------------------------------------------------
check_f18() {
  say "--- F-18: 可空性警告 ---"
  local log="$WORKDIR/build-core.log"
  dotnet build "$ROOT/src/client-windows/Pim.Client.Core/Pim.Client.Core.csproj" \
    -c Release --no-incremental >"$log" 2>&1

  local positions
  positions="$(grep -oE '[^ ]+\.cs\([0-9]+,[0-9]+\): warning CS8602' "$log" | sort -u || true)"
  local unique=0
  [[ -n "$positions" ]] && unique="$(printf '%s\n' "$positions" | wc -l)"
  if [[ "$unique" -gt 0 ]]; then
    violation F-18 "Release 构建产生 $unique 处 CS8602（唯一 文件:行:列）：$(printf '%s' "$positions" | tr '\n' ' ')"
  else
    ok F-18 "Release 构建没有 CS8602。"
  fi

  local total
  total="$(grep -oE '[0-9]+ Warning\(s\)' "$log" | tail -n1 | grep -oE '[0-9]+' || echo '')"
  if [[ -z "$total" ]]; then
    violation F-18 "构建日志里没有 MSBuild 的 'N Warning(s)' 汇总行，无法交叉校验警告数（解析方式可能已变）。"
  elif [[ "$total" -ne "$unique" ]]; then
    violation F-18 "MSBuild 汇总 $total 个警告，但只找到 $unique 处 CS8602；存在其他类别的警告，需人工确认。"
  else
    ok F-18 "MSBuild 汇总的 $total 个警告全部是 CS8602，且与按位置计数的结果一致。"
  fi
}

# ---------------------------------------------------------------------------
# F-17：AGENTS.md 里写的本地门禁命令必须真的存在、也真的能跑
#
# 审计发现的形态是：AGENTS.md 把 `dotnet build src/client-windows/Pim.Client.Windows.slnx`
# 写成提交前门禁命令，而那条命令既覆盖不完整、在当前 SDK 上还直接 error MSB4068。
# 修法是**改文档**（改成 CI 真正在跑、本环境也真的能执行的命令），而不是为了圆文档
# 去扩 slnx。所以这里不再把某条命令写死，而是从 AGENTS.md 里读出 `dotnet ...` 命令
# 逐字执行：文档改了命令，检查跟着验新的那条；文档写错了，检查就红。
# ---------------------------------------------------------------------------
check_f17() {
  say "--- F-17: AGENTS.md 的本地门禁命令 ---"
  local agents="$ROOT/AGENTS.md"
  local cmds
  cmds="$(grep -oE '`dotnet [^`]+`' "$agents" | tr -d '`' | sort -u)"

  if [[ -z "$cmds" ]]; then
    violation F-17 "AGENTS.md 里找不到任何 dotnet 门禁命令，检查失效。"
    return
  fi

  local log="$WORKDIR/slnx.log"
  : > "$log"
  while IFS= read -r cmd; do
    [[ -z "$cmd" ]] && continue
    # 文档里可能带 -p:... 之类的附加开关，逐字执行。
    if ( cd "$ROOT" && eval "$cmd" ) >>"$log" 2>&1; then
      ok F-17 "AGENTS.md 的命令可执行：$cmd"
    else
      violation F-17 "AGENTS.md 的命令 '$cmd' 执行失败：$(grep -m1 -E 'error [A-Z]+[0-9]+|error:' "$log" | sed 's/^ *//' || true)"
    fi
  done <<< "$cmds"
}

case "$CHECK" in
  F-07) check_f07 ;;
  F-18) check_f18 ;;
  F-17) check_f17 ;;
  all) check_f07; check_f18; check_f17 ;;
  *) echo "未知 --check 值: $CHECK" >&2; exit 2 ;;
esac

if [[ -n "$OUT" ]]; then
  mkdir -p "$OUT"
  # 便于审查者复现：把原始日志留在指定目录（不含任何凭据）。
  cp -f "$WORKDIR"/*.log "$OUT"/ 2>/dev/null || true
fi

echo ""
if [[ "$fail" -ne 0 ]]; then
  echo "依赖/构建卫生检查未通过"
  exit 1
fi
echo "依赖/构建卫生检查通过"
