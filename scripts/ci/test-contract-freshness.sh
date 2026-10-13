#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约新鲜度设施 · 行为测试（离线；唯一外部前置是 python3 + PyYAML）
#
# 覆盖（本仓实际产物 = contract/openapi.json + contract/provenance.json 两个；
# 本仓无 generated 产物，故不做也不假造"生成物新鲜度"分支）：
#   ① 同字节 → check 0 且仓内零写入          ② 合法 JSON 但内容不同 → 非 0
#   ③ 取源失败（不可达 / 404）→ 非 0          ④ payload HTML / 空体 → 非 0
#   ⑤ PIM_CONTRACT_REPO/REF 真实改变被访问路径（含 40-hex 直用不解析）
#   ⑥ check 全程零写入（git status + inode/mtime）  ⑦ provenance 缺失 / 被篡改 → 非 0
#   ⑧ refresh 的 provenance/bytes/hash/可复现性       ⑨ refresh 失败不写两个产物
#   ⑩ 上游非契约提交但字节相同 → PASS（provenance 允许滞后）
#   ⑪ CI 环境下测试旁路被拒（非静默放行）—— 旁路不能替代真实 source
#   ⑫ 原样抽取真实 CI YAML step 与 gate 检查函数，证明失败传播 + release 接线
#   ⑬ 负对照：旧 master 无 freshness 入口（同陈快照绿）/ 新入口同场景红
#
# 上游 fixture 一律是本机 git 仓 + 本机 HTTP 服务（127.0.0.1 随机端口），
# 生产旁路（本地文件当权威源）只在测试替身内可用，并被 ⑪ 证伪。
#
# 用法：bash scripts/ci/test-contract-freshness.sh
#   PIM_CONTRACT_TEST_KEEP=1     保留临时工作目录（默认删除）
#   PIM_CONTRACT_BASE_REF=<ref>  ⑬ 负对照所用的旧 master ref（默认为引入本门禁前的固定提交）
#   PYTHONDONTWRITEBYTECODE=1    由脚本自身强制设置
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=scripts/ci/contract-freshness-lib.sh
. "$HERE/contract-freshness-lib.sh"

CHECK="$HERE/check-contract-freshness.sh"
FETCH="$HERE/fetch-contract.sh"
CI_YML="$ROOT/.github/workflows/ci.yml"
# ⚠️ 负对照的基准必须固定在「引入本门禁之前」的提交上，**不能**取 origin/master。
#
# 原因（此前 master 一直红，就是踩了这个坑）：
#   这些负对照要证明的是「旧 master 上既没有 freshness 入口、也不会因快照陈旧变红」。
#   如果基准取 origin/master，那么一旦本门禁自己合进 master，origin/master 也就带上了
#   freshness 入口 —— 断言的前提永久失效，任何 base=master 的 PR（含 master 自身重跑）
#   都会红，且无法自愈。
#
#   cbfcbeffc93a1cc522eec1c522860e2881778b40 = 引入本门禁（PR #10）之前 master 的提交。
#   它已实测确认没有 scripts/ci/check-contract-freshness.sh 与 test-contract-freshness.sh。
#   该提交是 master 的祖先，不受 GC 影响；将来若仓库历史被重写，需同步更新此常量。
BASE_REF="${PIM_CONTRACT_BASE_REF:-cbfcbeffc93a1cc522eec1c522860e2881778b40}"

CHECK_STEP_NAME="Verify contract snapshot is up to date (real upstream)"
TEST_STEP_NAME="Contract freshness behavior tests (offline fixtures)"
OWNER_JOB="contract-freshness"

PASS=0; FAIL=0; NR=0
say()  { printf '%s\n' "$*"; }
sha()  { [ -f "$1" ] && sha256sum "$1" | cut -d' ' -f1 || echo ABSENT; }
fsig() { [ -e "$1" ] && stat -c '%i:%s:%Y' "$1" || echo ABSENT; }
record() { # record <name> <0=ok> <observed>
  if [ "$2" = "0" ]; then PASS=$((PASS+1)); say "PASS  $1 :: $3"
  else FAIL=$((FAIL+1)); say "FAIL  $1 :: $3"; fi
}
notrun() { NR=$((NR+1)); say "NOT-RUN  $1 :: $2"; }
section() { say ""; say "-- $* --"; }

if ! command -v python3 >/dev/null 2>&1; then
  say "缺少 python3（check/fetch 的 JSON 校验与 ⑫ 的 YAML 抽取都需要它）。"
  exit 2
fi
if ! python3 -c 'import yaml' 2>/dev/null; then
  say "缺少 PyYAML（用于原样抽取真实 CI step）；无法完成 ⑫。"
  exit 2
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-test.XXXXXX")"
SRV_PID=""
cleanup() {
  [ -n "$SRV_PID" ] && kill "$SRV_PID" 2>/dev/null
  if [ "${PIM_CONTRACT_TEST_KEEP:-0}" = "1" ]; then say "保留工作目录：$WORK"; else rm -rf "$WORK"; fi
}
trap cleanup EXIT

# =========================== fixtures ======================================
FIX="$WORK/fixtures"
mkdir -p "$FIX"

cat > "$FIX/openapi.master.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Pim.Api", "version": "1.0" },
  "paths": {
    "/api/v1/ping": {
      "get": {
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": { "schema": { "$ref": "#/components/schemas/PingApiResponse" } }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "PingApiResponse": {
        "type": "object",
        "properties": {
          "code": { "type": "integer", "format": "int32" },
          "message": { "type": "string" },
          "data": { "type": "string" }
        }
      }
    }
  }
}
JSON

cat > "$FIX/openapi.v0.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Pim.Api", "version": "1.0" },
  "paths": {
    "/api/v1/ping": {
      "get": {
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": { "schema": { "$ref": "#/components/schemas/PingApiResponse" } }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "PingApiResponse": {
        "type": "object",
        "properties": { "code": { "type": "integer", "format": "int32" }, "data": { "type": "string" } }
      }
    }
  }
}
JSON

# 异构 title 但合法：证明不锁 info.title / 不锁 repo 名
cat > "$FIX/openapi.othertitle.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Some-Other-Api", "version": "9.9" },
  "paths": { "/x": { "get": { "responses": { "200": { "description": "OK" } } } } }
}
JSON

printf '<html>404 Not Found</html>\n' > "$FIX/openapi.html"
: > "$FIX/openapi.empty"
printf '{"<html>404 Not Found</html>"}\n' > "$FIX/openapi.fakejson"

# ===================== 本机上游 git 仓（真实 40-hex SHA）====================
UPSTREAM_REPO="$WORK/upstream-src"
mkdir -p "$UPSTREAM_REPO/contract"
git -C "$UPSTREAM_REPO" init -q
git -C "$UPSTREAM_REPO" symbolic-ref HEAD refs/heads/master   # 不依赖宿主 init.defaultBranch
git -C "$UPSTREAM_REPO" config user.email fixture@local
git -C "$UPSTREAM_REPO" config user.name fixture
cp "$FIX/openapi.master.json" "$UPSTREAM_REPO/contract/openapi.json"
git -C "$UPSTREAM_REPO" add -A
git -C "$UPSTREAM_REPO" commit -qm 'c1: contract fixture'
SHA_C1="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"

# master 前进一个「非契约」提交：契约字节完全不变
printf '# fixture upstream\n' > "$UPSTREAM_REPO/README.md"
git -C "$UPSTREAM_REPO" add -A
git -C "$UPSTREAM_REPO" commit -qm 'c2: non-contract change only'
SHA_MASTER="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"

mkbranch() { # mkbranch <name> <file> <msg> -> 打印该分支的 40-hex SHA
  local name="$1" file="$2" msg="$3"
  git -C "$UPSTREAM_REPO" checkout -q -b "$name" "$SHA_C1"
  cp "$file" "$UPSTREAM_REPO/contract/openapi.json"
  git -C "$UPSTREAM_REPO" commit -qam "$msg"
  git -C "$UPSTREAM_REPO" rev-parse HEAD
  git -C "$UPSTREAM_REPO" checkout -q master
}
SHA_V0="$(mkbranch v0 "$FIX/openapi.v0.json" 'v0: older contract')"
SHA_HTML="$(mkbranch html "$FIX/openapi.html" 'html: error page payload')"
SHA_EMPTY="$(mkbranch empty "$FIX/openapi.empty" 'empty: empty payload')"
SHA_FAKEJSON="$(mkbranch fakejson "$FIX/openapi.fakejson" 'fakejson: legal JSON, not a contract')"
SHA_OTHERTITLE="$(mkbranch othertitle "$FIX/openapi.othertitle.json" 'othertitle: heterogeneous but legal contract')"

# ===================== 本机 raw 源（静态目录树）=============================
SERVE="$WORK/serve"
lay() { # lay <repo> <path-segment> <file>  （同时铺 SHA 段与分支名段）
  mkdir -p "$SERVE/$1/$2/contract"
  cp "$3" "$SERVE/$1/$2/contract/openapi.json"
}
lay pim-api "$SHA_C1"            "$FIX/openapi.master.json"
lay pim-api "$SHA_MASTER"        "$FIX/openapi.master.json"
lay pim-api "$SHA_V0"            "$FIX/openapi.v0.json"
lay pim-api "$SHA_HTML"          "$FIX/openapi.html"
lay pim-api "$SHA_EMPTY"         "$FIX/openapi.empty"
lay pim-api "$SHA_FAKEJSON"      "$FIX/openapi.fakejson"
lay pim-api "$SHA_OTHERTITLE"    "$FIX/openapi.othertitle.json"
lay pim-api "master"             "$FIX/openapi.master.json"    # 旧脚本按分支名取源
lay pim-api "v0"                 "$FIX/openapi.v0.json"
lay pim-api "html"               "$FIX/openapi.html"
lay pim-api "empty"              "$FIX/openapi.empty"
lay pim-api "fakejson"           "$FIX/openapi.fakejson"
lay 2746267826/pim-api "$SHA_MASTER" "$FIX/openapi.master.json"   # REPO 覆写用例

PORT="$(python3 - <<'PY'
import socket
s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()
PY
)"
( cd "$SERVE" && exec python3 -u -m http.server "$PORT" --bind 127.0.0.1 ) > "$WORK/http-server.log" 2>&1 &
SRV_PID=$!
RAW="http://127.0.0.1:$PORT"
for _ in $(seq 1 60); do
  curl -fsS -o /dev/null "$RAW/pim-api/$SHA_MASTER/contract/openapi.json" 2>/dev/null && break
  sleep 0.1
done

# =========================== sandbox =======================================
sandbox() { # sandbox <dir>
  local dir="$1"
  rm -rf "$dir"
  mkdir -p "$dir/scripts/ci" "$dir/contract" "$dir/.github/workflows"
  cp "$HERE/contract-freshness-lib.sh" "$dir/scripts/ci/"
  cp "$CHECK" "$dir/scripts/ci/"
  cp "$FETCH" "$dir/scripts/ci/"
  cp "$CI_YML" "$dir/.github/workflows/ci.yml"
  printf 'ignored\n' > "$dir/.gitignore"
  git -C "$dir" init -q
  git -C "$dir" config user.email test@local
  git -C "$dir" config user.name test
  git -C "$dir" add -A
  git -C "$dir" commit -qm sandbox-base
}

refresh_sandbox() { # refresh_sandbox <dir> <ref>  -> rc；不 commit
  local dir="$1" ref="$2"
  ( cd "$dir" && PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" \
      bash scripts/ci/fetch-contract.sh ) > "$dir.refresh.log" 2>&1
}

commit_sandbox() { local dir="$1"; git -C "$dir" add -A; git -C "$dir" commit -qm "$2"; }

run_check() { # run_check <dir> <ref> [raw_base] -> 设置 CHECK_RC / CHECK_LOG
  local dir="$1" ref="$2" base="${3:-$RAW}"
  ( cd "$dir" && PIM_CONTRACT_RAW_BASE="$base" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" \
      bash scripts/ci/check-contract-freshness.sh ) > "$dir.check.log" 2>&1
  CHECK_RC=$?
  CHECK_LOG="$dir.check.log"
}
run_check_env() { # run_check_env <dir> <ref> <env...>  额外 env 前缀（用于旁路/CI 用例）
  local dir="$1" ref="$2"; shift 2
  ( cd "$dir" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" "$@" \
      bash scripts/ci/check-contract-freshness.sh ) > "$dir.check.log" 2>&1
  CHECK_RC=$?
  CHECK_LOG="$dir.check.log"
}
artifacts_sig() { # 两个产物的 inode:size:mtime
  printf '%s | %s' "$(fsig "$1/contract/openapi.json")" "$(fsig "$1/contract/provenance.json")"
}
artifacts_sha() {
  printf '%s | %s' "$(sha "$1/contract/openapi.json")" "$(sha "$1/contract/provenance.json")"
}

# raw 服务的访问日志：按「调用前 mark → 调用后切片」断言真实请求路径
ACCESS_LOG="$WORK/http-server.log"
log_mark()  { wc -l < "$ACCESS_LOG" | tr -d ' '; }
log_since() { tail -n "+$(( $1 + 1 ))" "$ACCESS_LOG" 2>/dev/null; }
has_req()   { case "$(log_since "$1")" in *"$2"*) return 0;; *) return 1;; esac; }

say "== pim-windows 契约新鲜度行为测试（离线 fixture 上游）=="
say "仓          : $ROOT"
say "fixture 上游: $UPSTREAM_REPO  (master=$SHA_MASTER c1=$SHA_C1 v0=$SHA_V0)"
say "fixture raw : $RAW"
say "工作目录    : $WORK"

# ======================= ① 一致 → 0，且零写入 ==============================
section "① 同字节 → check 0 且仓内零写入"
sandbox "$WORK/c1"
refresh_sandbox "$WORK/c1" master >/dev/null 2>&1; R_RC=$?
commit_sandbox "$WORK/c1" 'synced at master'
record "refresh_synced_sandbox" $([ "$R_RC" -eq 0 ] && echo 0 || echo 1) "refresh rc=$R_RC"
SIG_BEFORE="$(artifacts_sig "$WORK/c1")"
STATUS_BEFORE="$(git -C "$WORK/c1" status --porcelain)"
run_check "$WORK/c1" master
SIG_AFTER="$(artifacts_sig "$WORK/c1")"
STATUS_AFTER="$(git -C "$WORK/c1" status --porcelain)"
say "   rc=$CHECK_RC  artifacts $SIG_BEFORE -> $SIG_AFTER"
record "check_consistent_exit_zero" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC on identical upstream bytes"
record "check_consistent_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"
record "check_consistent_clean_tree" $([ "$STATUS_BEFORE" = "$STATUS_AFTER" ] && echo 0 || echo 1) "git status --porcelain unchanged ('${STATUS_AFTER}')"
record "check_consistent_clean_tree_empty" $([ -z "$STATUS_AFTER" ] && echo 0 || echo 1) "git status --porcelain is empty"

# ======================= ② 合法 JSON 但内容不同 → 非 0 =====================
section "② 合法 JSON、内容不同（陈旧快照）→ 非 0"
sandbox "$WORK/c2"
refresh_sandbox "$WORK/c2" v0 >/dev/null 2>&1
commit_sandbox "$WORK/c2" 'synced at v0'
SIG_BEFORE="$(artifacts_sig "$WORK/c2")"
run_check "$WORK/c2" master
SIG_AFTER="$(artifacts_sig "$WORK/c2")"
say "   rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "check_stale_snapshot_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with a legal-but-different snapshot"
record "check_stale_snapshot_exit_1" $([ "$CHECK_RC" -eq 1 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented stale code = 1)"
record "check_stale_snapshot_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# ======================= ③ 取源失败 → 非 0（fail-closed）===================
section "③ 取不到权威源 → 非 0（fail-closed；不因事件类型放行）"
sandbox "$WORK/c3"
refresh_sandbox "$WORK/c3" master >/dev/null 2>&1
commit_sandbox "$WORK/c3" 'synced at master'
SIG_BEFORE="$(artifacts_sig "$WORK/c3")"
run_check "$WORK/c3" master "http://127.0.0.1:1"
SIG_AFTER="$(artifacts_sig "$WORK/c3")"
say "   unreachable rc=$CHECK_RC"
record "check_unreachable_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC when the raw base is unreachable"
record "check_unreachable_exit_3" $([ "$CHECK_RC" -eq 3 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented fetch-failure code = 3)"
record "check_unreachable_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# 无法解析的 ref（REMOTE 不可用 + api.github.com 对该 ref 也取不到）
( cd "$WORK/c3" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$WORK/does-not-exist.git" PIM_CONTRACT_REF="no-such-ref-$$" \
    bash scripts/ci/check-contract-freshness.sh ) > "$WORK/c3.check-ref.log" 2>&1
REF_RC=$?
record "check_unresolvable_ref_exit_nonzero" $([ "$REF_RC" -ne 0 ] && echo 0 || echo 1) "rc=$REF_RC when the ref cannot be resolved"
record "check_unresolvable_ref_exit_3" $([ "$REF_RC" -eq 3 ] && echo 0 || echo 1) "rc=$REF_RC (documented fetch-failure code = 3)"

# 404：源上根本没有该 SHA 的字节
mkdir -p "$SERVE/pim-api/0000000000000000000000000000000000000000/contract"
rm -f "$SERVE/pim-api/0000000000000000000000000000000000000000/contract/openapi.json"
SIG_BEFORE="$(artifacts_sig "$WORK/c3")"
run_check "$WORK/c3" "0000000000000000000000000000000000000000"
SIG_AFTER="$(artifacts_sig "$WORK/c3")"
say "   404 rc=$CHECK_RC"
record "check_http404_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC when the source returns 404"
record "check_http404_exit_3" $([ "$CHECK_RC" -eq 3 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented fetch-failure code = 3)"
record "check_http404_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# ======================= ④ payload HTML / 空体 / 伪 JSON → 非 0 ============
section "④ 权威源返回 HTML / 空体 / 合法 JSON 但非契约 → 非 0（不得当成契约比对）"
for kind in html empty fakejson; do
  case "$kind" in
    html)     REFCASE="$SHA_HTML";     EXPECT=4;;
    empty)    REFCASE="$SHA_EMPTY";    EXPECT=4;;
    fakejson) REFCASE="$SHA_FAKEJSON"; EXPECT=4;;
  esac
  SIG_BEFORE="$(artifacts_sig "$WORK/c3")"
  run_check "$WORK/c3" "$REFCASE"
  SIG_AFTER="$(artifacts_sig "$WORK/c3")"
  say "   $kind rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
  record "check_payload_${kind}_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC for a $kind payload"
  record "check_payload_${kind}_exit_${EXPECT}" $([ "$CHECK_RC" -eq "$EXPECT" ] && echo 0 || echo 1) "rc=$CHECK_RC (documented payload code = $EXPECT)"
  record "check_payload_${kind}_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"
done

# 异构 title / cross-repo 合法契约：不得被判 payload 非法（rc=4），只能按字节判新旧
sandbox "$WORK/c4"
( cd "$WORK/c4" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$SHA_OTHERTITLE" \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c4.refresh.log" 2>&1
OT_RC=$?
commit_sandbox "$WORK/c4" 'synced at othertitle'
run_check "$WORK/c4" "$SHA_OTHERTITLE"
record "heterogeneous_contract_accepted_not_config_error" $([ "$OT_RC" -eq 0 ] && [ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "refresh rc=$OT_RC / check rc=$CHECK_RC for a legal OpenAPI 3.x with info.title=Some-Other-Api (no title lock)"

# ======================= ⑤ REPO/REF 真实改变被访问路径 =====================
section "⑤ PIM_CONTRACT_REPO / REF 真实改变被访问路径（不是「变量存在」）"
ACCESS_LOG="$WORK/http-server.log"
sandbox "$WORK/c5"
refresh_sandbox "$WORK/c5" master >/dev/null 2>&1
commit_sandbox "$WORK/c5" 'synced at master'

M="$(log_mark)"
run_check "$WORK/c5" master                       # REF=master -> SHA_MASTER
if has_req "$M" "GET /pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "ref_master_accesses_resolved_sha_path" 0 "REF=master requested /pim-api/$SHA_MASTER/contract/openapi.json (rc=$CHECK_RC)"
else
  record "ref_master_accesses_resolved_sha_path" 1 "no such request in the raw-server log"
fi

M="$(log_mark)"
run_check "$WORK/c5" "$SHA_MASTER"                # REF=40-hex 直接使用
if has_req "$M" "GET /pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "ref_pinned_sha_accesses_same_path" 0 "REF=<40-hex> reused verbatim in the request path (rc=$CHECK_RC)"
else
  record "ref_pinned_sha_accesses_same_path" 1 "no such request in the raw-server log"
fi

# REF 换成 40-hex 时必须不再走 ls-remote：把 REMOTE 指向不存在的路径，仍应成功
( cd "$WORK/c5" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$WORK/does-not-exist.git" PIM_CONTRACT_REF="$SHA_MASTER" \
    bash scripts/ci/check-contract-freshness.sh ) > "$WORK/c5.check.log" 2>&1
PIN_RC=$?
record "ref_pinned_skips_ls_remote" $([ "$PIN_RC" -eq 0 ] && echo 0 || echo 1) "rc=$PIN_RC with an unusable PIM_CONTRACT_REMOTE (ls-remote not called)"

# 换 REF -> v0，请求路径随解析出的 SHA 改变（沙箱快照也换成 v0 才可能一致）
sandbox "$WORK/c5b"
refresh_sandbox "$WORK/c5b" v0 >/dev/null 2>&1
commit_sandbox "$WORK/c5b" 'synced at v0'
M="$(log_mark)"
run_check "$WORK/c5b" v0
if has_req "$M" "GET /pim-api/$SHA_V0/contract/openapi.json"; then
  record "ref_change_changes_access_path" 0 "REF=v0 requested /pim-api/$SHA_V0/contract/openapi.json (rc=$CHECK_RC)"
else
  record "ref_change_changes_access_path" 1 "no such request in the raw-server log"
fi
if has_req "$M" "GET /pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "ref_change_not_using_master_path" 1 "REF=v0 still requested the master SHA path"
else
  record "ref_change_not_using_master_path" 0 "REF=v0 did not request the master SHA path"
fi

# 换 REPO -> 2746267826/pim-api，请求路径随 REPO 改变
sandbox "$WORK/c5c"
( cd "$WORK/c5c" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=2746267826/pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=master \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c5c.refresh.log" 2>&1
commit_sandbox "$WORK/c5c" 'synced with overridden repo'
M="$(log_mark)"
( cd "$WORK/c5c" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=2746267826/pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=master \
    bash scripts/ci/check-contract-freshness.sh ) > "$WORK/c5c.check.log" 2>&1
REPO_RC=$?
if has_req "$M" "GET /2746267826/pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "repo_change_changes_access_path" 0 "REPO=2746267826/pim-api requested /2746267826/pim-api/$SHA_MASTER/... (rc=$REPO_RC)"
else
  record "repo_change_changes_access_path" 1 "no such request in the raw-server log (rc=$REPO_RC)"
fi

# ======================= ⑥ check 静态只读守卫 ==============================
section "⑥ check 只读（运行时零写入已由 ①②③④ 覆盖；此处补静态守卫）"
if grep -nE "(^|[^a-zA-Z_])cp[[:space:]].*(contract/openapi\.json|provenance\.json)|>[[:space:]]*[\"']?\S*(contract/openapi\.json|provenance\.json)|mv[[:space:]].*(contract/openapi\.json|provenance\.json)" "$CHECK" >/dev/null 2>&1; then
  record "check_script_has_no_repo_write_ops" 1 "found a write-looking operation in check-contract-freshness.sh"
else
  record "check_script_has_no_repo_write_ops" 0 "no cp/>/mv targeting the two artifacts in check-contract-freshness.sh"
fi
ALL_STATUS="$(git -C "$WORK/c1" status --porcelain; git -C "$WORK/c2" status --porcelain; git -C "$WORK/c3" status --porcelain; git -C "$WORK/c4" status --porcelain; git -C "$WORK/c5" status --porcelain)"
record "no_sandbox_dirtied_by_any_check_run" $([ -z "$ALL_STATUS" ] && echo 0 || echo 1) "all sandboxes still clean after every check invocation"

# ======================= ⑦ provenance 缺失 / 被篡改 → 非 0 ==================
section "⑦ provenance 缺失或被篡改 → 非 0（形状 + 与仓内快照一致性）"
sandbox "$WORK/c7a"
refresh_sandbox "$WORK/c7a" master >/dev/null 2>&1
rm -f "$WORK/c7a/contract/provenance.json"
commit_sandbox "$WORK/c7a" 'provenance missing'
SIG_BEFORE="$(artifacts_sig "$WORK/c7a")"
run_check "$WORK/c7a" master
SIG_AFTER="$(artifacts_sig "$WORK/c7a")"
say "   missing-provenance rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "check_missing_provenance_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with contract/provenance.json absent"
record "check_missing_provenance_exit_1" $([ "$CHECK_RC" -eq 1 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented stale code = 1)"
record "check_missing_provenance_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

sandbox "$WORK/c7b"
refresh_sandbox "$WORK/c7b" master >/dev/null 2>&1
python3 - "$WORK/c7b/contract/provenance.json" <<'PY'
import json, sys
p = sys.argv[1]
doc = json.load(open(p, encoding="utf-8"))
doc["sha256"] = "0" * 64
json.dump(doc, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
commit_sandbox "$WORK/c7b" 'provenance sha256 tampered'
run_check "$WORK/c7b" master
say "   tampered-provenance rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "check_tampered_provenance_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC when provenance.sha256 disagrees with the committed snapshot"
record "check_tampered_provenance_exit_1" $([ "$CHECK_RC" -eq 1 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented stale code = 1)"

# ======================= ⑧ refresh 产物与可复现性 ==========================
section "⑧ refresh：provenance / bytes / hash / 可复现（两个产物）"
sandbox "$WORK/c8"
refresh_sandbox "$WORK/c8" master; R_RC=$?
record "refresh_exit_zero" $([ "$R_RC" -eq 0 ] && echo 0 || echo 1) "refresh rc=$R_RC against the fixture upstream"
PROV="$WORK/c8/contract/provenance.json"
SNAP="$WORK/c8/contract/openapi.json"
record "refresh_writes_provenance" $([ -f "$PROV" ] && echo 0 || echo 1) "contract/provenance.json present"
record "refresh_writes_snapshot_bytes" \
  $([ "$(sha "$SNAP")" = "$(sha "$FIX/openapi.master.json")" ] && echo 0 || echo 1) \
  "snapshot bytes == upstream master bytes"
if [ -f "$PROV" ]; then
  EFFECTIVE="$(python3 - "$PROV" "$SNAP" "$SHA_MASTER" <<'PY'
import json, sys, hashlib, re
prov, snap_path, expect_sha = sys.argv[1:4]
doc = json.load(open(prov, encoding="utf-8"))
snap = open(snap_path, "rb").read()
problems = []
if doc.get("schemaVersion") != 1: problems.append("schemaVersion")
if doc.get("repo") != "pim-api": problems.append("repo=%r" % doc.get("repo"))
if doc.get("ref") != "master": problems.append("ref=%r" % doc.get("ref"))
if not re.match(r"^[0-9a-f]{40}$", str(doc.get("resolvedRef"))): problems.append("resolvedRef")
if not doc.get("resolvedRefSource"): problems.append("resolvedRefSource")
if doc.get("sourcePath") != "contract/openapi.json": problems.append("sourcePath")
if doc.get("bytes") != len(snap): problems.append("bytes")
if doc.get("sha256") != hashlib.sha256(snap).hexdigest(): problems.append("sha256")
for key in doc:
    if re.search(r"(AtUtc|timestamp|Timestamp|fetchedAt|generatedAt|date)", key):
        problems.append("non-deterministic field %s" % key)
print("|".join(problems) if problems else "OK")
PY
)"
  record "refresh_provenance_shape_ok" $([ "$EFFECTIVE" = "OK" ] && echo 0 || echo 1) "provenance fields: $EFFECTIVE"
  RESOLVED="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["resolvedRef"])' "$PROV")"
  record "refresh_provenance_resolved_ref" $([ "$RESOLVED" = "$SHA_MASTER" ] && echo 0 || echo 1) "resolvedRef=$RESOLVED == fixture master=$SHA_MASTER"
  PROV_BYTE_HASH="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["sha256"])' "$PROV")"
  record "refresh_provenance_local_byte_hash" \
    $([ "$PROV_BYTE_HASH" = "$(sha "$SNAP")" ] && echo 0 || echo 1) \
    "provenance.sha256 == sha256(committed snapshot bytes)"
else
  record "refresh_provenance_shape_ok" 1 "provenance.json missing"
  record "refresh_provenance_resolved_ref" 1 "provenance.json missing"
  record "refresh_provenance_local_byte_hash" 1 "provenance.json missing"
fi

# 可复现：再刷一次，两个产物必须逐字节相同
SIG1="$(artifacts_sha "$WORK/c8")"
refresh_sandbox "$WORK/c8" master >/dev/null 2>&1; R2_RC=$?
SIG2="$(artifacts_sha "$WORK/c8")"
record "refresh_deterministic_second_run" $([ "$SIG1" = "$SIG2" ] && echo 0 || echo 1) "second refresh rc=$R2_RC left both artifacts byte-identical"
say "   artifacts sha: $SIG2"

# ======================= ⑨ refresh 失败不写两个产物 ========================
section "⑨ refresh 失败：两个产物一个字节都不写（含「本不存在则不得被创建」）"
sandbox "$WORK/c9"
refresh_sandbox "$WORK/c9" master >/dev/null 2>&1  # sanity: 成功刷新不改字节
rm -f "$WORK/c9/contract/provenance.json"          # 让 provenance 处于「不存在」状态
SIG_BEFORE="$(artifacts_sha "$WORK/c9")"
record "refresh_failure_precondition_provenance_absent" $([ ! -e "$WORK/c9/contract/provenance.json" ] && echo 0 || echo 1) "provenance.json removed before the failing refreshes"
( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="http://127.0.0.1:1" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=master \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail1.log" 2>&1
F1RC=$?
SIG_AFTER="$(artifacts_sha "$WORK/c9")"
record "refresh_fetch_failure_nonzero" $([ "$F1RC" -ne 0 ] && echo 0 || echo 1) "rc=$F1RC when the source is unreachable"
record "refresh_fetch_failure_writes_nothing" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "snapshot unchanged and provenance still absent"

( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=html \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail2.log" 2>&1
F2RC=$?
record "refresh_bad_payload_nonzero" $([ "$F2RC" -ne 0 ] && echo 0 || echo 1) "rc=$F2RC for an HTML payload"
record "refresh_bad_payload_writes_nothing" $([ "$(artifacts_sha "$WORK/c9")" = "$SIG_BEFORE" ] && echo 0 || echo 1) "snapshot unchanged and provenance still absent"

( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=fakejson \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail3.log" 2>&1
F3RC=$?
record "refresh_noncontract_json_nonzero" $([ "$F3RC" -ne 0 ] && echo 0 || echo 1) "rc=$F3RC for legal JSON that is not an OpenAPI 3.x contract"
record "refresh_noncontract_json_writes_nothing" $([ "$(artifacts_sha "$WORK/c9")" = "$SIG_BEFORE" ] && echo 0 || echo 1) "snapshot unchanged and provenance still absent"

( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=empty \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail4.log" 2>&1
F4RC=$?
record "refresh_empty_payload_nonzero" $([ "$F4RC" -ne 0 ] && echo 0 || echo 1) "rc=$F4RC for an empty payload"
record "refresh_empty_payload_writes_nothing" $([ "$(artifacts_sha "$WORK/c9")" = "$SIG_BEFORE" ] && echo 0 || echo 1) "snapshot unchanged and provenance still absent"

# 未知位置参数必须报错且不写（Minor M1 加固）
( cd "$WORK/c9" && bash scripts/ci/fetch-contract.sh --check ) > "$WORK/c9.fail5.log" 2>&1
F5RC=$?
record "refresh_unknown_arg_nonzero" $([ "$F5RC" -eq 2 ] && echo 0 || echo 1) "rc=$F5RC for the unknown arg '--check'"
record "refresh_unknown_arg_writes_nothing" $([ "$(artifacts_sha "$WORK/c9")" = "$SIG_BEFORE" ] && echo 0 || echo 1) "snapshot unchanged and provenance still absent"

# ======================= ⑩ 非契约提交但字节相同 → PASS =====================
section "⑩ 上游出现非契约提交、契约字节不变 → 必须 PASS，且 provenance 允许滞后"
sandbox "$WORK/c10"
refresh_sandbox "$WORK/c10" "$SHA_C1" >/dev/null 2>&1      # provenance.resolvedRef = c1
commit_sandbox "$WORK/c10" 'synced at c1'
PROV_SHA="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["resolvedRef"])' "$WORK/c10/contract/provenance.json" 2>/dev/null || echo missing)"
run_check "$WORK/c10" master                               # 上游 HEAD 已是 c2（非契约提交）
say "   rc=$CHECK_RC  provenance.resolvedRef=$PROV_SHA  今天解析出的上游=$SHA_MASTER"
record "noncontract_commit_snapshot_unchanged_pass" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC although upstream HEAD moved c1->c2"
record "noncontract_commit_head_really_moved" $([ "$SHA_C1" != "$SHA_MASTER" ] && echo 0 || echo 1) "c1=$SHA_C1 != c2=$SHA_MASTER"
record "provenance_may_lag_head" $([ "$PROV_SHA" = "$SHA_C1" ] && echo 0 || echo 1) "provenance.resolvedRef stayed $SHA_C1 (lag allowed, not compared to today's HEAD)"

# resolvedRef 换成另一个合法 40-hex（但 provenance sha256/bytes 仍与快照一致）→ 仍 PASS
sandbox "$WORK/c10b"
refresh_sandbox "$WORK/c10b" master >/dev/null 2>&1
python3 - "$WORK/c10b/contract/provenance.json" <<'PY'
import json, sys
p = sys.argv[1]
doc = json.load(open(p, encoding="utf-8"))
doc["resolvedRef"] = "1" * 40
doc["resolvedRefSource"] = "ref-pinned"
json.dump(doc, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
commit_sandbox "$WORK/c10b" 'provenance resolvedRef lags (another legal sha)'
run_check "$WORK/c10b" master
record "provenance_lagging_resolved_ref_pass" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with provenance.resolvedRef=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["resolvedRef"])' "$WORK/c10b/contract/provenance.json")"

# ======================= ⑪ CI 环境下测试旁路被拒 ===========================
section "⑪ CI 环境下把仓内文件当权威源的测试旁路必须被拒（不得静默变绿；旁路≠真实 source）"
sandbox "$WORK/c11"
refresh_sandbox "$WORK/c11" v0 >/dev/null 2>&1
commit_sandbox "$WORK/c11" 'synced at v0 (stale vs master)'
# 快照本身是陈旧的；若旁路生效会「自己和自己比」而假绿。
run_check_env "$WORK/c11" master CI=true PIM_CONTRACT_TEST_MODE=1 \
  PIM_CONTRACT_TEST_SOURCE_FILE="$WORK/c11/contract/openapi.json"
say "   CI+bypass rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "ci_refuses_test_bypass_exit_4" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC (config error, not a silent pass)"
record "ci_refuses_test_bypass_not_zero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC != 0"

run_check_env "$WORK/c11" master GITHUB_ACTIONS=true PIM_CONTRACT_TEST_SOURCE_FILE="$WORK/c11/contract/openapi.json"
record "gha_refuses_source_file_without_test_mode" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC when GITHUB_ACTIONS=true"

# CI 下用环境变量把「仓内产物路径」指向别的文件也必须被拒（否则等于换掉被校验对象）
run_check_env "$WORK/c11" master CI=true PIM_CONTRACT_SNAPSHOT="$WORK/c11/contract/openapi.json"
record "ci_refuses_snapshot_path_override" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC with PIM_CONTRACT_SNAPSHOT set under CI"
run_check_env "$WORK/c11" master PIM_CONTRACT_STRICT=1 PIM_CONTRACT_PROVENANCE="$WORK/c11/contract/provenance.json"
record "strict_mode_refuses_provenance_path_override" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC with PIM_CONTRACT_STRICT=1 and a provenance path override"

# 非 CI 下、且显式声明测试模式时旁路才可用（测试替身自身的正面对照）
# 本子调用模拟「非 CI 的测试替身」环境：只对这一条子进程清掉宿主 runner 恒设的 CI/GITHUB_ACTIONS 标记（默认 CI guard 与其负对照一字不动）。
run_check_env "$WORK/c11" master env -u CI -u GITHUB_ACTIONS PIM_CONTRACT_TEST_MODE=1 \
  PIM_CONTRACT_TEST_SOURCE_FILE="$WORK/c11/contract/openapi.json"
record "test_stub_seam_reachable_only_in_test_mode" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with PIM_CONTRACT_TEST_MODE=1 outside CI"

# ======================= ⑫ 真实 CI YAML step + gate 传播 ===================
section "⑫ 原样抽取真实 CI step 与 gate 检查函数，证明失败传播与 release 接线"
STEP="$WORK/ci-step-body.sh"
TESTSTEP="$WORK/ci-teststep-body.sh"
GATE="$WORK/ci-gate-body.sh"
GATE_ENV_MAP="$WORK/gate-env-map.txt"
python3 - "$CI_YML" "$STEP" "$TESTSTEP" "$GATE" "$GATE_ENV_MAP" "$CHECK_STEP_NAME" "$TEST_STEP_NAME" <<'PY'
import re, sys, yaml
src, step_out, teststep_out, gate_out, map_out, check_name, test_name = sys.argv[1:8]
doc = yaml.safe_load(open(src, encoding="utf-8"))
jobs = doc.get("jobs") or {}
def find_step(name):
    for job, spec in jobs.items():
        for idx, step in enumerate(spec.get("steps") or []):
            if step.get("name") == name:
                return job, idx, step
    return None, None, None
job, idx, step = find_step(check_name)
if step is None:
    print("check step not found", file=sys.stderr); sys.exit(1)
open(step_out, "w", encoding="utf-8").write(step["run"])
tjob, tidx, tstep = find_step(test_name)
open(teststep_out, "w", encoding="utf-8").write((tstep or {}).get("run", ""))
gate = jobs["gate"]
body = None
for s in (gate.get("steps") or []):
    if "run" in s:
        body = s["run"]
open(gate_out, "w", encoding="utf-8").write(body or "")
# gate 的 env: 变量 -> 被消费的 job（从 ${{ needs.<job>.result }} 解析）
pairs = []
for s in (gate.get("steps") or []):
    for var, expr in (s.get("env") or {}).items():
        m = re.search(r"needs\.([A-Za-z0-9_.-]+)\.result", str(expr))
        if m:
            pairs.append("%s %s" % (var, m.group(1)))
open(map_out, "w", encoding="utf-8").write("\n".join(pairs) + "\n")
print("extracted step '%s' from job '%s' (step index %d); test step job=%s" % (check_name, job, idx, tjob))
PY
[ -s "$STEP" ] || record "yaml_step_extracted" 1 "cannot extract CI step body"
[ -s "$STEP" ] && record "yaml_step_extracted" 0 "check step body extracted verbatim from .github/workflows/ci.yml"
[ -s "$TESTSTEP" ] || record "yaml_test_step_extracted" 1 "cannot extract the behavior-test step body"
[ -s "$TESTSTEP" ] && record "yaml_test_step_extracted" 0 "behavior-test step body extracted verbatim"

# 结构断言：owner job 被 gate 消费；无 if / continue-on-error / env 覆盖；setup-python 显式存在；
# 行为测试 step 在 check step 之前；release 依赖 freshness。
STRUCT="$(python3 - "$CI_YML" "$CHECK_STEP_NAME" "$TEST_STEP_NAME" "$OWNER_JOB" <<'PY'
import sys, yaml
src, check_name, test_name, owner_hint = sys.argv[1:5]
doc = yaml.safe_load(open(src, encoding="utf-8"))
jobs = doc["jobs"]
problems = []

def find_step(name):
    for job, spec in jobs.items():
        for idx, step in enumerate(spec.get("steps") or []):
            if step.get("name") == name:
                return job, idx, step, spec
    return None, None, None, None

owner, cidx, cstep, ospec = find_step(check_name)
towner, tidx, tstep, tspec = find_step(test_name)
if owner is None:
    problems.append("check step missing")
if owner and owner != owner_hint:
    problems.append("check step owner job is %s, expected %s" % (owner, owner_hint))
if owner and towner != owner:
    problems.append("behavior test step is in job %s, not in %s" % (towner, owner))
if owner and tstep is not None and tidx is not None and cidx is not None and tidx > cidx:
    problems.append("behavior test step does not run before the check step")
if cstep is not None:
    if "if" in cstep: problems.append("check step has if:")
    if cstep.get("continue-on-error") in (True, "true"): problems.append("check step has continue-on-error")
    if cstep.get("env"): problems.append("check step sets env: %s" % sorted(cstep["env"]))
    if "PIM_CONTRACT_" in (cstep.get("run") or ""): problems.append("check step body sets a PIM_CONTRACT_* override")
if tstep is not None:
    if "if" in tstep: problems.append("test step has if:")
    if tstep.get("continue-on-error") in (True, "true"): problems.append("test step has continue-on-error")
if ospec is not None:
    if "if" in ospec: problems.append("owner job has if:")
    if ospec.get("continue-on-error") in (True, "true"): problems.append("owner job has continue-on-error")
    if not any(str(s.get("uses","")).startswith("actions/setup-python") for s in (ospec.get("steps") or [])):
        problems.append("owner job has no explicit actions/setup-python step")
    if any("github.event_name" in str(s.get("if","")) for s in (ospec.get("steps") or [])):
        problems.append("owner job has an event-conditional step (非 PR exit 0 类旁路)")
    if any("github.event_name" in (s.get("run") or "") for s in (ospec.get("steps") or [])):
        problems.append("owner job script branches on github.event_name (non-PR bypass)")

on = doc.get("on") or doc.get(True) or {}
triggers = sorted(str(k) for k in on.keys())
for want in ("push", "pull_request", "workflow_dispatch"):
    if want not in triggers: problems.append("%s trigger missing" % want)

gate = jobs["gate"]
needs = list(gate.get("needs") or [])
if owner and owner not in needs: problems.append("owner job %s not in gate.needs" % owner)
gate_body = " ".join(s.get("run","") for s in gate["steps"] if "run" in s)
for job in needs:
    if ('check "%s"' % job) not in gate_body:
        problems.append("gate does not call check \"%s\"" % job)
legacy = ["resolve-version", "build-windows", "build-shell-windows",
          "build-browser-extension", "build-browser-extension-firefox",
          "test-client-core", "test-client-app"]
for job in legacy:
    if job not in needs: problems.append("legacy mandatory job %s missing from gate.needs" % job)

# 既有客户端测试不得被加过滤 / 加 if / 加 continue-on-error（本轮不改测试范围）
for job, needle in (("test-client-core", "Pim.Client.Tests"), ("test-client-app", "Pim.Client.App.Tests")):
    spec = jobs.get(job)
    if spec is None:
        problems.append("%s job missing" % job)
        continue
    if "if" in spec: problems.append("%s job has if:" % job)
    if spec.get("continue-on-error") in (True, "true"): problems.append("%s job has continue-on-error" % job)
    runs = " ".join((s.get("run") or "") for s in (spec.get("steps") or []))
    if needle not in runs:
        problems.append("%s no longer runs %s" % (job, needle))
    if "--filter" in runs:
        problems.append("%s runs with a --filter (test scope reduced)" % job)

rel = jobs["release"]
rneed = list(rel.get("needs") or [])
if owner and owner not in rneed:
    problems.append("release.needs does not include %s" % owner)
if owner and ("needs.%s" % owner) not in str(rel.get("if","")):
    problems.append("release.if does not reference needs.%s" % owner)

print("|".join(problems) if problems else "OK")
PY
)"
record "ci_step_wiring_ok" $([ "$STRUCT" = "OK" ] && echo 0 || echo 1) "structure: $STRUCT"
say "   CI triggers: $(python3 -c 'import yaml,sys;d=yaml.safe_load(open(sys.argv[1]));print(sorted(str(k) for k in (d.get("on") or d.get(True)).keys()))' "$CI_YML")"
say "   gate env->job map:"; sed 's/^/     /' "$GATE_ENV_MAP"

# gate.needs 上的每个 job 都必须能通过 gate 的 env 映射被观测到（漏 env 等于漏 check）
NEEDS_UNCOVERED="$(python3 - "$CI_YML" "$GATE_ENV_MAP" <<'PY'
import sys, yaml
doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
needs = list(doc["jobs"]["gate"].get("needs") or [])
mapped = {line.split()[1] for line in open(sys.argv[2], encoding="utf-8") if line.strip()}
missing = [j for j in needs if j not in mapped]
print(",".join(missing))
PY
)"
record "gate_env_covers_every_need" $([ -z "$NEEDS_UNCOVERED" ] && echo 0 || echo 1) "gate env mapping covers all needs (uncovered: '${NEEDS_UNCOVERED}')"

# ⑬ 的负对照读一个**历史提交**，因此 contract-freshness job 的 checkout 必须保留完整历史。
# 这条不能只靠注释：若有人把 fetch-depth 改小或删掉，⑬ 会因对象不可达而失效
# （CI 下已改为 fail-closed，但这里提前把「配置被改」本身也拦下来）。
FD_BAD="$(python3 - "$CI_YML" <<'PY'
import sys, yaml
doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
job = (doc.get("jobs") or {}).get("contract-freshness")
bad = []
if not job:
    bad.append("no contract-freshness job")
else:
    for s in (job.get("steps") or []):
        if str(s.get("uses") or "").startswith("actions/checkout"):
            fd = (s.get("with") or {}).get("fetch-depth")
            if str(fd) != "0":
                bad.append("checkout fetch-depth=%r (must be 0)" % fd)
            break
    else:
        bad.append("no actions/checkout step")
print("; ".join(bad))
PY
)"
record "contract_freshness_job_keeps_full_history" $([ -z "$FD_BAD" ] && echo 0 || echo 1) \
  "contract-freshness checkout must use fetch-depth: 0 (${FD_BAD:-ok})"

# 步骤体在三个失败分支都必须非 0（GitHub 用 bash -e -o pipefail 调 run）
step_run() { # step_run <dir> <ref> [raw_base]
  local dir="$1" ref="$2" base="${3:-$RAW}"
  ( cd "$dir" && env PIM_CONTRACT_RAW_BASE="$base" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" \
      bash -e -o pipefail "$STEP" ) > "$dir.step.log" 2>&1
  STEP_RC=$?
}
step_run "$WORK/c1" master
record "ci_step_green_on_consistent_repo" $([ "$STEP_RC" -eq 0 ] && echo 0 || echo 1) "rc=$STEP_RC on the consistent sandbox"
step_run "$WORK/c2" master
record "ci_step_red_on_stale_snapshot" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC when the committed snapshot is stale"
step_run "$WORK/c7a" master
record "ci_step_red_on_missing_provenance" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC when provenance.json is missing"
step_run "$WORK/c1" master "http://127.0.0.1:1"
record "ci_step_red_on_fetch_failure" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC when the source is unreachable"

# gate：把 contract-freshness 置为 failure / skipped，gate 必须非 0（失败传播到 required check）
gate_run() { # gate_run <job=result> ...  未列出的 job 默认 success
  local -a envs=()
  local var job res kv
  while read -r var job; do
    [ -n "${var:-}" ] || continue
    res="success"
    for kv in "$@"; do
      case "$kv" in "$job="*) res="${kv#*=}";; esac
    done
    envs+=("$var=$res")
  done < "$GATE_ENV_MAP"
  ( env "${envs[@]}" bash -e -o pipefail "$GATE" ) > "$WORK/gate.log" 2>&1
  GATE_RC=$?
}
gate_run
record "gate_green_when_all_jobs_success" $([ "$GATE_RC" -eq 0 ] && echo 0 || echo 1) "rc=$GATE_RC with every required job success"
gate_run "$OWNER_JOB=failure"
record "gate_red_when_contract_freshness_failed" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with $OWNER_JOB=failure (freshness failure propagates)"
gate_run "$OWNER_JOB=skipped"
record "gate_red_when_contract_freshness_skipped" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with $OWNER_JOB=skipped"
gate_run "build-windows=failure"
record "gate_red_when_legacy_job_failed" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with build-windows=failure (legacy jobs still enforced)"

# ======================= ⑬ 负对照：旧 master vs 新入口 =====================
section "⑬ 负对照：旧 master 无 freshness 入口（同陈快照绿）而新入口失败"
if git -C "$ROOT" rev-parse --verify --quiet "$BASE_REF^{commit}" >/dev/null 2>&1; then
  OLD_FETCH="$WORK/old-fetch-contract.sh"
  OLD_CI="$WORK/old-ci.yml"
  git -C "$ROOT" show "$BASE_REF:scripts/ci/fetch-contract.sh" > "$OLD_FETCH" 2>/dev/null
  git -C "$ROOT" show "$BASE_REF:.github/workflows/ci.yml" > "$OLD_CI" 2>/dev/null
  OLD_BASE_SHA="$(git -C "$ROOT" rev-parse "$BASE_REF")"
  say "   旧 master ref=$BASE_REF ($OLD_BASE_SHA)"

  # (a) 旧树没有任何 freshness 入口（结构性，取自真实旧 ref 的 git 对象）
  OLD_STRUCT="$(python3 - "$OLD_CI" <<'PY'
import sys, yaml
doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
hits = []
for job, spec in (doc.get("jobs") or {}).items():
    for s in (spec.get("steps") or []):
        blob = ((s.get("name") or "") + " " + (s.get("run") or "") + " " + (s.get("uses") or ""))
        if "check-contract-freshness" in blob or "contract-freshness" in blob or "provenance" in blob:
            hits.append("%s/%s" % (job, s.get("name")))
print(",".join(hits) if hits else "NONE")
PY
)"
  record "old_master_has_no_freshness_entry_in_workflow" $([ "$OLD_STRUCT" = "NONE" ] && echo 0 || echo 1) "base workflow freshness surface: $OLD_STRUCT"
  if git -C "$ROOT" cat-file -e "$BASE_REF:scripts/ci/check-contract-freshness.sh" 2>/dev/null; then
    record "old_master_has_no_check_script" 1 "base ref already contains scripts/ci/check-contract-freshness.sh"
  else
    record "old_master_has_no_check_script" 0 "scripts/ci/check-contract-freshness.sh does not exist at $BASE_REF"
  fi

  # (b) 旧 refresh 脚本真的会把「合法 JSON 但不是契约」的 payload 安装进仓（rc=0），新入口拒绝
  OLD_SB="$WORK/c13"
  sandbox "$OLD_SB"
  # 旧脚本按自身 BASH_SOURCE 推导 ROOT，必须放进沙箱内执行，避免写到真实工作树。
  cp "$OLD_FETCH" "$OLD_SB/scripts/ci/old-fetch-contract.sh"
  ( cd "$OLD_SB" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REF=fakejson bash scripts/ci/old-fetch-contract.sh ) > "$WORK/c13.old-refresh.log" 2>&1
  OLD_REFRESH_RC=$?
  OLD_INSTALLED_SHA="$(sha "$OLD_SB/contract/openapi.json")"
  record "old_refresh_accepts_noncontract_json" \
    $([ "$OLD_REFRESH_RC" -eq 0 ] && [ "$OLD_INSTALLED_SHA" = "$(sha "$FIX/openapi.fakejson")" ] && echo 0 || echo 1) \
    "old fetch-contract.sh rc=$OLD_REFRESH_RC and installed the non-contract payload (sha=$(echo "$OLD_INSTALLED_SHA" | cut -c1-12))"
  commit_sandbox "$OLD_SB" 'old-style refresh installed a non-contract payload'
  run_check "$OLD_SB" fakejson
  record "new_entry_rejects_noncontract_json" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "new check rc=$CHECK_RC on the very payload the old entry installed"

  # (c) 同一「陈旧快照」沙箱：旧树没有任何**字节比较**动作 → 不可能变红；新入口 rc=1
  #
  # 注意与上面第 1 条的区别：第 1 条只看「有没有 freshness 相关的 step 名」，
  # 这一条直接扫旧 ci.yml 全量 run 体里**有没有字节比较命令**（cmp/diff/git diff -q 等）。
  # 两者曾用同一谓词（等价于重复断言、零独立信息量），此处改为独立判据。
  OLD_BYTECMP="$(python3 - "$OLD_CI" <<'PY'
import re, sys, yaml
doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
pat = re.compile(r"(^|[^\w-])(cmp|diff|git\s+diff|sha256sum\s+-c)([^\w-]|$)")
hits = []
for job, spec in (doc.get("jobs") or {}).items():
    for s in (spec.get("steps") or []):
        body = (s.get("run") or "")
        if pat.search(body):
            hits.append("%s/%s" % (job, s.get("name")))
print(",".join(hits) if hits else "NONE")
PY
)"
  record "old_master_stale_snapshot_cannot_turn_red" $([ "$OLD_BYTECMP" = "NONE" ] && echo 0 || echo 1) \
    "no step in the base workflow performs a byte comparison (surfaces: $OLD_BYTECMP), so the stale snapshot below would stay green on old master"
  run_check "$WORK/c2" master
  record "new_entry_red_on_same_stale_snapshot" $([ "$CHECK_RC" -eq 1 ] && echo 0 || echo 1) "new check rc=$CHECK_RC on the same stale snapshot the old master accepted"
else
  # ⚠️ fail-closed：基准 ref 解析不到时，**不能**在 CI 里静默跳过。
  #
  # 为什么：⑬ 的负对照依赖一个历史提交。浅克隆、把 fetch-depth 改小、或仓库历史被
  # 重写，都会让该对象不可达。若此时只记 NOT-RUN 而退出码仍为 0，门禁就会「悄无声息地
  # 失去覆盖」——这与本门禁要拦的那类静默漂移同族，不能留。
  #
  # 本地开发环境（非 CI）允许 NOT-RUN，便于在浅克隆里跑其余断言；
  # CI 环境一律判 FAIL，逼使 checkout 保留完整历史。
  if [ "${CI:-}" = "true" ] || [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    record "old_master_negative_control_base_resolvable" 1 \
      "base ref '$BASE_REF' is not resolvable in $ROOT under CI —— 负对照无法执行，按 fail-closed 判失败（浅克隆 / fetch-depth 过小 / 历史改写都会触发；请确保 checkout 使用 fetch-depth: 0）"
  else
    notrun "old_master_negative_control" "base ref '$BASE_REF' is not resolvable in $ROOT (set PIM_CONTRACT_BASE_REF); 旧 master 负对照未执行（本地非 CI，允许跳过）"
  fi
fi

# ============================== 汇总 =======================================
say ""
say "== summary: PASS=$PASS FAIL=$FAIL NOT-RUN=$NR =="
if [ "$FAIL" -eq 0 ]; then
  say "RESULT: 全部断言通过（exit 0）$( [ "$NR" -gt 0 ] && echo "；另有 $NR 条 NOT-RUN（见上）" )"
  exit 0
fi
say "RESULT: 存在失败断言（exit 1）"
exit 1
