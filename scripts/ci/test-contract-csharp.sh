#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约 C# 生成物设施 · 行为测试（离线；唯一外部前置是 "$PY" + PyYAML）
#
# 覆盖：
#   ① 仓内生成物与快照一致 → check 0，且**零写入**（inode/size/mtime/hash 全等）
#   ② 手工改动生成物 → check 非 0（生成物不可手工修改）
#   ③ 生成物缺失 → check 非 0
#   ④ 契约变了但生成物没重新生成 → check 非 0（「快照变了产物没变」，工单验收②）
#   ⑤ 同一场景重新生成后 → check 0（证明 ②④ 不是「永远 PASS/永远 FAIL」的摆设）
#   ⑥ operation 清单声明了契约里不存在的路径 → 生成器拒绝产出（非 0），且不写仓
#   ⑦ operation 清单声明了契约里不存在的方法 → 生成器拒绝产出
#   ⑧ 契约里出现生成器尚未支持的形状（allOf）→ 生成器拒绝产出，不产出半个类型
#   ⑨ 生成器确定性：同输入两次运行逐字节相同
#   ⑩ 新增一个**契约里真实存在**的 operation → 生成物变化；未重新生成时 check 非 0，
#      重新生成后 check 0（说明门禁跟着清单走，不是空转）
#   ⑪ 原样抽取真实 CI YAML：job / step / gate 接线（needs + env 映射 + gate 检查行），
#      并真实执行抽取出来的 step 体与 gate 体，证明失败会传播
#   ⑫ 生成物与 operation 清单的对应关系：仓内每个生成文件都被 check 覆盖（无「没被校验的产物」）
#   ⑬ 生成器 fail-closed 负对照：内联 enum / 非标识符枚举值 / 整数枚举 / properties+additionalProperties /
#      readOnly 落在请求方向 / 内联根 schema / 未建模 query 形状 → 一律拒绝产出且零写入
#   ⑭ 生成物固定 UTF-8 + LF（Windows 与 Linux 逐字节相同的必要条件）
#
# 用法：bash scripts/ci/test-contract-csharp.sh
#   PIM_CONTRACT_TEST_KEEP=1  保留临时工作目录（默认删除）
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

CHECK="$HERE/check-contract-csharp.sh"
GENERATOR="$ROOT/scripts/contract/generate-csharp.py"
CI_YML="$ROOT/.github/workflows/ci.yml"

OWNER_JOB="contract-generated"
CHECK_STEP_NAME="Verify contract-generated C# artifacts are up to date"
TEST_STEP_NAME="Contract C# gate behavior tests (offline)"

PASS=0; FAIL=0; NR=0
say()  { printf '%s\n' "$*"; }
record() { # record <name> <0=ok> <observed>
  if [ "$2" = "0" ]; then PASS=$((PASS+1)); say "PASS  $1 :: $3"
  else FAIL=$((FAIL+1)); say "FAIL  $1 :: $3"; fi
}
notrun() { NR=$((NR+1)); say "NOT-RUN  $1 :: $2"; }
section() { say ""; say "-- $* --"; }

PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import yaml' 2>/dev/null; then PY="$candidate"; break; fi
done
if [ -z "$PY" ]; then
  say "缺少可执行的 python3/python + PyYAML（生成器/校验器/CI 接线测试需要）。"
  exit 2
fi
if [ -z "$PY" ]; then
  say "缺少 PyYAML（用于原样抽取真实 CI step 与 gate 函数）；无法完成 ⑪。"
  exit 2
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-csharp-test.XXXXXX")"
cleanup() {
  if [ "${PIM_CONTRACT_TEST_KEEP:-0}" = "1" ]; then say "保留工作目录：$WORK"; else rm -rf "$WORK"; fi
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# 沙箱：只拷贝校验/生成需要的部分（contract + scripts + 生成物），不含 .git
# ---------------------------------------------------------------------------
mksb() { # mksb <dir> [--empty-artifacts]
  local dir="$1"; shift || true
  rm -rf "$dir"
  mkdir -p "$dir/src/client-windows/Pim.Client.Core"
  cp -a "$ROOT/contract" "$dir/contract"
  cp -a "$ROOT/scripts" "$dir/scripts"
  cp -a "$ROOT/src/client-windows/Pim.Client.Core/Contract" "$dir/src/client-windows/Pim.Client.Core/Contract"
  if [ "${1:-}" = "--empty-artifacts" ]; then
    rm -rf "$dir/src/client-windows/Pim.Client.Core/Contract"
  fi
}

# 沙箱里跑的是**沙箱自己那份**脚本/生成器（相对路径），否则校验读的还是真实仓，
# 负对照会全部变成「假绿」。$CHECK/$GENERATOR 只用于存在性说明。
run_check() { # run_check <dir> -> CHECK_RC
  ( cd "$1" && bash scripts/ci/check-contract-csharp.sh ) > "$1.check.log" 2>&1
  CHECK_RC=$?
}

run_generator() { # run_generator <dir> -> GEN_RC
  ( cd "$1" && "$PY" scripts/contract/generate-csharp.py --out-dir "$1" ) > "$1.gen.log" 2>&1
  GEN_RC=$?
}

sb_sig() { # 沙箱内所有文件的 (inode,size,mtime,hash)，用于证明校验零写入
  ( cd "$1" && find . -type f -print0 | sort -z | xargs -0 stat -c '%n:%i:%s:%Y' && \
    ( cd "$1" && find . -type f -print0 | sort -z | xargs -0 sha256sum ) )
}

ARTIFACT_REL="src/client-windows/Pim.Client.Core/Contract/ContractModels.cs"

section "① 当前仓：生成物与契约快照一致，且校验零写入"
SB="$WORK/sb-baseline"; mksb "$SB"
SIG_BEFORE="$(sb_sig "$SB")"
run_check "$SB"
record "check_green_on_consistent_repo" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC on a faithful copy of the repo artifacts"
SIG_AFTER="$(sb_sig "$SB")"
record "check_writes_nothing" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "sandbox file signatures unchanged (inode/size/mtime/hash)"

section "② 手工改动生成物 → 门禁必须红"
SB="$WORK/sb-handedit"; mksb "$SB"
printf '\n// 手工加的一行\n' >> "$SB/$ARTIFACT_REL"
run_check "$SB"
record "check_red_on_hand_edited_artifact" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC after a hand edit"
grep -q "手工加的一行" "$SB.check.log" || grep -q "生成物与契约快照不一致" "$SB.check.log"
record "check_reports_hand_edit_diff" $([ $? -eq 0 ] && echo 0 || echo 1) "diff output points at the hand-edited file"

section "③ 生成物缺失 → 门禁必须红"
SB="$WORK/sb-missing"; mksb "$SB" --empty-artifacts
run_check "$SB"
record "check_red_on_missing_artifact" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with the generated artifacts removed"

section "④/⑤ 契约变了但生成物没重生成 → 红；重新生成后 → 绿（工单验收②）"
SB="$WORK/sb-contract-changed"; mksb "$SB"
"$PY" - "$SB/contract/openapi.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
schema = doc["components"]["schemas"]["KeystatsUploadRequest"]
# 契约侧改一个字段名（等价于后端把字段改名后刷新了快照）
schema["properties"]["peakKpsTouched"] = schema["properties"].pop("peakKps")
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2, sort_keys=True)
PY
run_check "$SB"
record "check_red_on_contract_changed_without_regen" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC when the contract moved but the artifact did not"
run_generator "$SB"
record "generator_succeeds_on_changed_contract" $([ "$GEN_RC" -eq 0 ] && echo 0 || echo 1) "rc=$GEN_RC regenerating from the changed contract"
grep -q "peakKpsTouched" "$SB/$ARTIFACT_REL"
record "regenerated_artifact_follows_the_contract" $([ $? -eq 0 ] && echo 0 || echo 1) "regenerated DTO carries the renamed contract field"
run_check "$SB"
record "check_green_after_regen" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC after regenerating (④'s red was staleness, not a permanently-red gate)"

section "⑥/⑦ operation 清单写了契约里不存在的东西 → 生成器拒绝产出"
SB="$WORK/sb-unknown-path"; mksb "$SB"
"$PY" - "$SB/scripts/contract/client-operations.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
doc["operations"].append({"apiClient": "InventedAsync", "method": "POST", "path": "/api/v1/invented/endpoint"})
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
SIG_BEFORE="$(sb_sig "$SB")"
run_generator "$SB"
record "generator_rejects_unknown_path" $([ "$GEN_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GEN_RC after declaring a path that is not in the contract"
SIG_AFTER="$(sb_sig "$SB")"
record "generator_refuses_and_writes_nothing_on_unknown_path" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "no half-a-contract artifact was written"
run_check "$SB"
record "check_red_on_unknown_path_config" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC (config error, not a silent pass)"

SB="$WORK/sb-wrong-method"; mksb "$SB"
"$PY" - "$SB/scripts/contract/client-operations.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
for entry in doc["operations"]:
    if entry["path"] == "/api/v1/auth/login":
        entry["method"] = "GET"
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
run_generator "$SB"
record "generator_rejects_wrong_method" $([ "$GEN_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GEN_RC after declaring GET on a POST-only contract path"

section "⑧ 契约出现生成器尚未支持的形状 → 拒绝产出（不产出半个类型）"
SB="$WORK/sb-allof"; mksb "$SB"
"$PY" - "$SB/contract/openapi.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
schema = doc["components"]["schemas"]["LoginRequest"]
schema["allOf"] = [{"type": "object", "properties": {"extra": {"type": "string"}}}]
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2, sort_keys=True)
PY
run_generator "$SB"
record "generator_rejects_unsupported_shape" $([ "$GEN_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GEN_RC with an allOf in the closure (fail-closed)"

section "⑨ 生成器确定性"
SB="$WORK/sb-det1"; mksb "$SB"
SB2="$WORK/sb-det2"; mksb "$SB2"
run_generator "$SB"; RC1=$GEN_RC
run_generator "$SB2"; RC2=$GEN_RC
if [ "$RC1" -eq 0 ] && [ "$RC2" -eq 0 ] \
   && cmp -s "$SB/$ARTIFACT_REL" "$SB2/$ARTIFACT_REL" \
   && cmp -s "$SB/src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs" \
             "$SB2/src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs"; then
  record "generator_is_deterministic" 0 "two runs are byte-identical"
else
  record "generator_is_deterministic" 1 "rc1=$RC1 rc2=$RC2 or outputs differ"
fi

section "⑩ 新增一个契约里真实存在的 operation → 产物变化，未重生成则门禁红"
# 候选必须落在契约基址 /api/v1 之下（客户端 BaseAddress 就是它，生成器对此 fail-closed）。
# 契约里一部分 operation 的 schema 形状生成器还不支持（会拒绝产出），因此按顺序试候选，
# 取第一个生成成功的 —— 这条断言证明的是「清单驱动边界」，不是「契约里每个 operation 都能生成」。
CANDIDATES="$("$PY" - "$ROOT/contract/openapi.json" "$ROOT/scripts/contract/client-operations.json" <<'PYEOF'
import json, sys
contract = json.load(open(sys.argv[1], encoding="utf-8"))
ops = json.load(open(sys.argv[2], encoding="utf-8"))
declared = {(o["method"].upper(), o["path"]) for o in ops["operations"]}
out = []
for path, item in sorted(contract["paths"].items()):
    if not path.startswith("/api/v1/"):
        continue
    for method in ("post", "get"):
        if method in item and (method.upper(), path) not in declared:
            out.append("%s %s" % (method.upper(), path))
print("\n".join(out[:40]))
PYEOF
)"

declare_operation() { # declare_operation <dir> <"METHOD path">
  "$PY" - "$1/scripts/contract/client-operations.json" "$2" <<'PYEOF'
import json, sys
path, spec = sys.argv[1], sys.argv[2]
method, op_path = spec.split(" ", 1)
doc = json.load(open(path, encoding="utf-8"))
doc["operations"].append({"apiClient": "SpareOperationAsync", "method": method, "path": op_path})
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PYEOF
}

NEW_OP=""
TRIED=0
while IFS= read -r candidate && [ "$TRIED" -lt 25 ]; do
  candidate="${candidate%$'\r'}"
  [ -n "$candidate" ] || continue
  TRIED=$((TRIED+1))
  PROBE="$WORK/sb-newop-probe-$TRIED"   # 探路用，可能会被生成器写脏，不作为断言现场
  mksb "$PROBE"
  declare_operation "$PROBE" "$candidate"
  run_generator "$PROBE"
  if [ "$GEN_RC" -eq 0 ]; then NEW_OP="$candidate"; break; fi
done <<< "$CANDIDATES"

if [ -n "$NEW_OP" ]; then
  say "   选用未被声明的契约 operation：$NEW_OP（第 $TRIED 个候选）"
  SB_OP="$WORK/sb-newop"               # 干净现场：生成物 = 仓内已提交的产物
  mksb "$SB_OP"
  declare_operation "$SB_OP" "$NEW_OP"

  grep -q "SpareOperation" "$SB_OP/src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs"
  record "new_operation_absent_before_regen" $([ $? -ne 0 ] && echo 0 || echo 1) "the committed artifact does not carry the new operation yet"

  # 未重新生成时：门禁按「配置错误」fail-closed（rc=4），绝不静默放行。
  run_check "$SB_OP"
  record "check_red_when_new_operation_not_regenerated" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC after adding a real operation without regenerating"

  run_generator "$SB_OP"
  record "generator_accepts_real_new_operation" $([ "$GEN_RC" -eq 0 ] && echo 0 || echo 1) "rc=$GEN_RC"
  grep -q "SpareOperation" "$SB_OP/src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs"
  record "new_operation_lands_in_the_generated_boundary" $([ $? -eq 0 ] && echo 0 || echo 1) "the new operation is emitted into ContractOperations.cs"
  run_check "$SB_OP"
  record "check_green_after_regenerating_new_operation" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC after regenerating"
else
  notrun "check_red_when_new_operation_not_regenerated" "契约 /api/v1 下没有生成器可接受的未声明 operation（试了 $TRIED 个候选）"
fi

section "⑪ 原样抽取真实 CI step 与 gate 检查函数，证明失败传播与接线"
STEP="$WORK/ci-step-body.sh"
TESTSTEP="$WORK/ci-teststep-body.sh"
GATE="$WORK/ci-gate-body.sh"
GATE_ENV_MAP="$WORK/gate-env-map.txt"
"$PY" - "$CI_YML" "$STEP" "$TESTSTEP" "$GATE" "$GATE_ENV_MAP" "$CHECK_STEP_NAME" "$TEST_STEP_NAME" "$OWNER_JOB" <<'PY'
import re, sys, yaml
src, step_out, teststep_out, gate_out, map_out, check_name, test_name, owner_hint = sys.argv[1:9]
doc = yaml.safe_load(open(src, encoding="utf-8"))
jobs = doc.get("jobs") or {}
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
elif owner != owner_hint:
    problems.append("check step owner job is %s, expected %s" % (owner, owner_hint))
if owner and towner != owner:
    problems.append("behavior test step is in job %s, not in %s" % (towner, owner))
if owner and cidx is not None and tidx is not None and tidx > cidx:
    problems.append("behavior test step does not run before the check step")
if cstep is not None:
    if "if" in cstep: problems.append("check step has if:")
    if cstep.get("continue-on-error") in (True, "true"): problems.append("check step has continue-on-error")
    if cstep.get("env"): problems.append("check step sets env: %s" % sorted(cstep["env"]))
if tstep is not None:
    if "if" in tstep: problems.append("test step has if:")
    if tstep.get("continue-on-error") in (True, "true"): problems.append("test step has continue-on-error")
if ospec is not None:
    if "if" in ospec: problems.append("owner job has if:")
    if ospec.get("continue-on-error") in (True, "true"): problems.append("owner job has continue-on-error")
    if not any(str(s.get("uses", "")).startswith("actions/setup-python") for s in (ospec.get("steps") or [])):
        problems.append("owner job has no explicit actions/setup-python step")

on = doc.get("on") or doc.get(True) or {}
triggers = sorted(str(k) for k in on.keys())
for want in ("push", "pull_request", "workflow_dispatch"):
    if want not in triggers:
        problems.append("%s trigger missing" % want)

gate = jobs.get("gate") or {}
needs = list(gate.get("needs") or [])
if owner_hint not in needs:
    problems.append("gate does not need %s (job is not on the required chain)" % owner_hint)
env_map = {}
gate_body = ""
for s in (gate.get("steps") or []):
    if "run" in s and ("check " in s["run"] or "CI Gate" in s["run"]):
        gate_body = s["run"]
    for var, expr in (s.get("env") or {}).items():
        m = re.search(r"needs\.([A-Za-z0-9_.-]+)\.result", str(expr))
        if m:
            env_map[var] = m.group(1)
owner_var = [v for v, j in env_map.items() if j == owner_hint]
if not owner_var:
    problems.append("gate has no env mapping to %s's result" % owner_hint)
else:
    var = owner_var[0]
    if ("check \"%s\"" % owner_hint) not in gate_body:
        problems.append("gate body does not check %s" % owner_hint)
    if ('"$%s"' % var) not in gate_body and ("${%s}" % var) not in gate_body:
        problems.append("gate body does not consume $%s" % var)

open(step_out, "w", encoding="utf-8").write(cstep["run"] if cstep else "")
open(teststep_out, "w", encoding="utf-8").write((tstep or {}).get("run", ""))
open(gate_out, "w", encoding="utf-8").write(gate_body)
open(map_out, "w", encoding="utf-8", newline="\n").write(
    "\n".join("%s %s" % (v, j) for v, j in sorted(env_map.items())) + "\n")
print("owner job=%s; step index=%s; gate needs %d jobs; problems=%s" % (
    owner, cidx, len(needs), problems or "none"))
raise SystemExit(1 if problems else 0)
PY
record "ci_job_and_gate_wiring_ok" $([ $? -eq 0 ] && echo 0 || echo 1) "job exists, step calls the check, gate needs it and consumes its result"

[ -s "$STEP" ] || record "yaml_step_extracted" 1 "cannot extract the check step body"
[ -s "$STEP" ] && record "yaml_step_extracted" 0 "check step body extracted verbatim from .github/workflows/ci.yml"
[ -s "$TESTSTEP" ] || record "yaml_test_step_extracted" 1 "cannot extract the behavior-test step body"
[ -s "$TESTSTEP" ] && record "yaml_test_step_extracted" 0 "behavior-test step body extracted verbatim"

# 真实执行抽取出来的 step 体：一致的沙箱 → 0；陈旧快照 → 非 0（GitHub 用 bash -e -o pipefail 调 run）
SB="$WORK/sb-step-green"; mksb "$SB"
( cd "$SB" && bash -e -o pipefail "$STEP" ) > "$WORK/step.green.log" 2>&1
STEP_RC=$?
record "ci_step_green_on_consistent_repo" $([ "$STEP_RC" -eq 0 ] && echo 0 || echo 1) "rc=$STEP_RC on the consistent sandbox"

SB="$WORK/sb-step-stale"; mksb "$SB"
printf '\n// 手工加的一行\n' >> "$SB/$ARTIFACT_REL"
( cd "$SB" && bash -e -o pipefail "$STEP" ) > "$WORK/step.stale.log" 2>&1
STEP_RC=$?
record "ci_step_red_on_hand_edited_artifact" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC on the hand-edited sandbox"

SB="$WORK/sb-step-missing"; mksb "$SB" --empty-artifacts
( cd "$SB" && bash -e -o pipefail "$STEP" ) > "$WORK/step.missing.log" 2>&1
STEP_RC=$?
record "ci_step_red_on_missing_artifact" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC on the artifact-less sandbox"

# gate 体：把 contract-generated 置为 failure / skipped，gate 必须非 0
gate_run() {
  local -a envs=()
  local var job res kv
  while read -r var job; do
    job="${job%$'\r'}"
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
record "gate_red_when_contract_generated_failed" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with $OWNER_JOB=failure (failure propagates to CI Gate)"
gate_run "$OWNER_JOB=skipped"
record "gate_red_when_contract_generated_skipped" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with $OWNER_JOB=skipped"
gate_run "test-client-core=failure"
record "gate_red_when_legacy_job_failed" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with test-client-core=failure (legacy jobs still enforced)"

section "⑫ 仓内每个生成文件都被门禁覆盖（没有「没被校验的产物」）"
COVERAGE="$("$PY" - "$ROOT/scripts/ci/check-contract-csharp.sh" "$ROOT/src/client-windows/Pim.Client.Core/Contract" <<'PY'
import re, sys
from pathlib import Path
script = Path(sys.argv[1]).read_text(encoding="utf-8")
artifacts_dir = Path(sys.argv[2])
listed = set(re.findall(r'"(src/client-windows/Pim\.Client\.Core/Contract/[A-Za-z0-9_.]+\.cs)"', script))
present = {p.relative_to(artifacts_dir.parents[3]).as_posix() for p in artifacts_dir.glob("*.cs")}
missing = sorted(present - listed)
print(",".join(missing))
PY
)"
record "every_generated_file_is_checked" $([ -z "$COVERAGE" ] && echo 0 || echo 1) \
  "unchecked generated files: '${COVERAGE:-none}'"


section "⑬ 生成器 fail-closed 负对照：契约里出现尚未支持的形状一律拒绝产出"
# 这些形状的共同危害是「静默丢契约约束」：生成器假装没看见，边界就会发出不合契约的请求。
# 每一条都要求生成器**非 0 退出**，且仓内产物一个字节都不动。

reject_case() { # reject_case <name> <描述> <python 变更片段>
  local name="$1" desc="$2" snippet="$3"
  local dir="$WORK/reject-$name"
  mksb "$dir"
  local before
  "$PY" - "$dir/contract/openapi.json" <<PYEOF
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
schemas = doc["components"]["schemas"]
$snippet
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2, sort_keys=True)
PYEOF
  # 先改契约、再取基线：这样「零写入」度量的只是生成器自己的副作用。
  before="$(sb_sig "$dir")"
  run_generator "$dir"
  local after
  after="$(sb_sig "$dir")"
  if [ "$GEN_RC" -ne 0 ] && [ "$before" = "$after" ]; then
    record "generator_rejects_$name" 0 "$desc (rc=$GEN_RC, 仓内零写入)"
  else
    record "generator_rejects_$name" 1 "$desc (rc=$GEN_RC, 写入=$([ "$before" = "$after" ] && echo no || echo YES))"
  fi
}

reject_case "inline_enum_with_unmodelled_constraints" "内联 enum 仍带生成器无法表达的属性约束" \
  'schemas["LoginRequest"]["properties"]["username"]["enum"] = ["alice", "bob"]'
reject_case "enum_wire_value_collision" "两个枚举 wire 值归一化后撞到同一 C# 成员" \
  'schemas["DaemonSourceState"]["enum"] = ["IN-PROCESS", "IN_PROCESS"]'
reject_case "integer_enum" "整数枚举（契约没给成员名）" \
  'schemas["DaemonSourceState"] = {"type": "integer", "format": "int32", "enum": [0, 1, 2]}'
reject_case "properties_plus_additional" "对象同时有 properties 与 additionalProperties" \
  'schemas["KeystatsUploadRequest"]["properties"]["keyPressCounts"] = {"type": "object", "properties": {"a": {"type": "string"}}, "additionalProperties": {"type": "integer", "format": "int32"}}'
reject_case "readonly_in_request_schema" "readOnly 字段出现在请求方向的 schema 里" \
  'schemas["LoginRequest"]["properties"]["username"]["readOnly"] = True'
reject_case "inline_root_request" "请求体根不是 \$ref（内联形状）" \
  'doc["paths"]["/api/v1/auth/login"]["post"]["requestBody"]["content"]["application/json"]["schema"] = {"type": "object", "properties": {"username": {"type": "string"}}}'
reject_case "root_additional_properties" "具名对象根上带 typed additionalProperties（允许任意额外键）" \
  'schemas["LoginRequest"]["additionalProperties"] = {"type": "string"}'
reject_case "path_parameter_enum" "path 参数自带 enum 约束（生成的边界表达不了）" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceId", "in": "path", "required": True, "schema": {"type": "string", "enum": ["ONLY-ALLOWED"]}}]'
reject_case "path_parameter_not_in_template" "声明的 path 参数不在路径模板里（会被静默丢掉）" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceID", "in": "path", "required": True, "schema": {"type": "string"}}]'
reject_case "path_parameter_ref" "path 参数用 \$ref 引用（生成器尚未解析 components.parameters）" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"$" "ref": "#/components/parameters/DeviceId"}]'
reject_case "path_parameter_schema_ref" "path 参数的 schema 用 \$ref 引用（约束会被静默丢掉）" \
  'doc["components"]["schemas"]["DeviceIdRef"] = {"type": "string", "enum": ["ONLY"]}; doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceId", "in": "path", "required": True, "schema": {"$" "ref": "#/components/schemas/DeviceIdRef"}}]'
reject_case "path_parameter_allof" "path 参数的 schema 用 allOf 组合约束" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceId", "in": "path", "required": True, "schema": {"allOf": [{"type": "string", "enum": ["ONLY"]}]}}]'
reject_case "path_parameter_not" "path 参数的 schema 用 not 排除约束" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceId", "in": "path", "required": True, "schema": {"not": {"enum": ["FORBIDDEN"]}}}]'
reject_case "path_parameter_pattern" "path 参数的 schema 带 pattern" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceId", "in": "path", "required": True, "schema": {"type": "string", "pattern": "^PC-[0-9]+$"}}]'

section "⑮ 真实 ASR 边界：path uuid/int32 + 两个 required header + audio/wav binary 200 + 12 响应头"
# 正向：真实清单本身已含 7 个 ASR operation（official 9e96 刷新后），生成必须成功；
# 先验证清单没有缺项/重复项（防止重复 apiClient 造成“假失败”），再检查产物元数据。
SB="$WORK/sb-asr-boundary"; mksb "$SB"
"$PY" - "$SB/scripts/contract/client-operations.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
ops = [(entry["apiClient"], entry["method"], entry["path"]) for entry in doc["operations"]]
assert len(ops) == len(set(ops)), "duplicated client-operations entries"
required = {
    ("RegisterNativeAsrNodeAsync", "POST", "/api/v1/native-notes/asr-routing/nodes/register"),
    ("HeartbeatNativeAsrNodeAsync", "POST", "/api/v1/native-notes/asr-routing/nodes/{nodeId}/heartbeat"),
    ("WithdrawNativeAsrNodeAsync", "POST", "/api/v1/native-notes/asr-routing/nodes/{nodeId}/withdraw"),
    ("ClaimNativeAsrLeaseAsync", "POST", "/api/v1/native-notes/asr-routing/leases/claim"),
    ("RenewNativeAsrLeaseAsync", "POST", "/api/v1/native-notes/asr-routing/leases/{leaseId}/renew"),
    ("GetNativeAsrSegmentContentAsync", "GET", "/api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content"),
    ("AckNativeAsrAttemptAsync", "POST", "/api/v1/native-notes/asr-routing/leases/{attemptId}/ack"),
}
missing = sorted(required - set(ops))
assert not missing, "client-operations missing ASR entries: %s" % missing
PY
run_generator "$SB"
record "generator_accepts_actual_asr_boundary" $([ "$GEN_RC" -eq 0 ] && echo 0 || echo 1) \
  "rc=$GEN_RC for the real 9e96 ASR boundary (path uuid/int32, 2 required headers, audio/wav)"
OPS_REL="src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs"
MODELS_REL="src/client-windows/Pim.Client.Core/Contract/ContractModels.cs"
grep -q "X-Pim-Lease-Token" "$SB/$OPS_REL" && grep -q "X-Pim-Fencing-Token" "$SB/$OPS_REL" \
  && grep -q "audio/wav" "$SB/$OPS_REL" && grep -q "X-Content-SHA256" "$SB/$OPS_REL"
record "asr_operation_metadata_is_typed" $([ $? -eq 0 ] && echo 0 || echo 1) \
  "generated operation carries header names, binary media type and response-header metadata"

# 负例：未知 format 参数必须继续 fail-closed，不能静默退化为 string。
reject_case "path_parameter_unknown_format" "path 参数带未知 format" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "deviceId", "in": "path", "required": True, "schema": {"type": "string", "format": "custom-id"}}]'

# 负例：任意的 header 参数（不是已建模的两个 segment content header）必须拒绝。
reject_case "arbitrary_request_header" "其他 operation 带 required header" \
  'doc["paths"]["/api/v1/endpoints/{deviceId}/collection-quality"]["get"]["parameters"] = [{"name": "X-Custom", "in": "header", "required": True, "schema": {"type": "string"}}]'

# 负例：segment content 的 12 个响应头少一个或形状变化必须拒绝（不能悄悄少校验一个身份头）。
reject_case "asr_response_headers_missing" "segment content 200 响应头缺失" \
  'segment = doc["paths"].get("/api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content") or sys.exit(7)
assert segment.get("get"), "segment content GET missing"
headers = segment["get"]["responses"]["200"].get("headers") or sys.exit(8)
assert "X-Pim-Fencing-Token" in headers, "fencing header missing before mutation"
headers.pop("X-Pim-Fencing-Token")'

section "⑯ 真实 query / 二进制分片：正向生成与 fail-closed 边界"
SB="$WORK/sb-query-binary-boundary"; mksb "$SB"
run_generator "$SB"
record "generator_accepts_real_query_and_binary_operations" $([ "$GEN_RC" -eq 0 ] && echo 0 || echo 1) \
  "rc=$GEN_RC for required/optional query parameters and note material octet-stream upload"
grep -q 'new("sha256", "plain-string", true)' "$SB/$OPS_REL" \
  && grep -q 'new("offset", "int32", false), new("limit", "int32", false)' "$SB/$OPS_REL" \
  && grep -q 'new("start", "date-time", false)' "$SB/$OPS_REL" \
  && grep -q 'new("inbox", "boolean", false)' "$SB/$OPS_REL" \
  && grep -q '"application/octet-stream"' "$SB/$OPS_REL" \
  && grep -q "public string ResolveQuery" "$SB/$OPS_REL"
record "query_and_binary_metadata_is_typed" $([ $? -eq 0 ] && echo 0 || echo 1) \
  "generated metadata carries date-time/boolean query kinds and the exact binary media type"

# 真实契约里的任务状态是属性级内联 enum，wire 值含 '-'。生成器必须提升为具名 enum
# 并生成精确 wire converter，不能静默改写或 rejection 掉合法契约。
grep -q 'public enum CreateTaskRequestStatus' "$SB/$MODELS_REL" \
  && grep -q 'public enum UpdateTaskRequestStatus' "$SB/$MODELS_REL" \
  && grep -q '"NEEDS-ACTION" => CreateTaskRequestStatus.NEEDS_ACTION' "$SB/$MODELS_REL" \
  && grep -q 'CreateTaskRequestStatus.NEEDS_ACTION => "NEEDS-ACTION"' "$SB/$MODELS_REL"
record "inline_wire_enum_is_named_and_exact" $([ $? -eq 0 ] && echo 0 || echo 1) \
  "real inline task statuses become named enums with exact hyphenated wire mapping"

# operation 级参数覆盖 Path Item 级的同名同位置参数；合并后不能重复声明。
SB="$WORK/sb-query-override"; mksb "$SB"
"$PY" - "$SB/contract/openapi.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
doc["paths"]["/api/v1/endpoints"]["parameters"] = [
    {"name": "page", "in": "query", "required": False, "schema": {"type": "integer", "format": "int32"}},
]
doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [
    {"name": "page", "in": "query", "required": True, "schema": {"type": "integer", "format": "int64"}},
]
json.dump(doc, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2, sort_keys=True)
PY
run_generator "$SB"
record "generator_merges_query_parameter_override" $([ "$GEN_RC" -eq 0 ] && echo 0 || echo 1) \
  "rc=$GEN_RC when operation-level query replaces the Path Item declaration"
grep -q 'new("page", "int64", true)' "$SB/$OPS_REL"
record "query_parameter_override_keeps_operation_shape" $([ $? -eq 0 ] && echo 0 || echo 1) \
  "operation-level required/type wins without emitting a duplicate"

reject_case "query_parameter_unknown_format" "query 参数带未知 format" \
  'doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [{"name": "page", "in": "query", "schema": {"type": "string", "format": "custom-page"}}]'
reject_case "query_parameter_ref" "query 参数用 \$ref 引用（生成器尚未解析 components.parameters）" \
  'doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [{"$" "ref": "#/components/parameters/Page"}]'
reject_case "query_parameter_required_not_boolean" "query 参数的 required 不是布尔值" \
  'doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [{"name": "page", "in": "query", "required": "yes", "schema": {"type": "integer"}}]'
reject_case "query_parameter_duplicate" "同一层重复声明同名 query 参数" \
  'doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [{"name": "page", "in": "query", "schema": {"type": "integer"}}, {"name": "page", "in": "query", "schema": {"type": "integer"}}]'
reject_case "cookie_parameter" "带 cookie 参数（生成器未建模 cookie）" \
  'doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [{"name": "session", "in": "cookie", "schema": {"type": "string"}}]'
reject_case "unsupported_binary_request_body" "非笔记材料分片 operation 使用二进制请求体" \
  'doc["paths"]["/api/v1/auth/login"]["post"]["requestBody"]["content"] = {"application/octet-stream": {"schema": {"type": "string", "format": "binary"}}}'

section "⑰ 真实笔记材料文件下载：octet-stream + 精确响应头 + SHA pattern"
SB="$WORK/sb-note-material-download"; mksb "$SB"
"$PY" - "$SB/scripts/contract/client-operations.json" <<'PY'
import json, sys
path = sys.argv[1]
doc = json.load(open(path, encoding="utf-8"))
required = {
    ("DownloadNoteMaterialFileAsync", "GET",
     "/api/v1/native-notes/versions/{versionId}/files/{fileIndex}"),
}
declared = {
    (entry["apiClient"], entry["method"], entry["path"])
    for entry in doc["operations"]
}
assert required <= declared, required - declared
PY
run_generator "$SB"
record "generator_accepts_note_material_binary_download" $([ "$GEN_RC" -eq 0 ] && echo 0 || echo 1) \
  "rc=$GEN_RC for the development contract shape"
grep -q 'DownloadNoteMaterialFile =' "$SB/$OPS_REL" \
  && grep -q '"application/octet-stream"' "$SB/$OPS_REL" \
  && grep -q 'new("X-Content-SHA256", "sha256")' "$SB/$OPS_REL" \
  && grep -q 'new("Content-Disposition", "plain-string")' "$SB/$OPS_REL" \
  && grep -q 'new("X-Content-Type-Options", "plain-string")' "$SB/$OPS_REL"
record "note_material_binary_metadata_is_typed" $([ $? -eq 0 ] && echo 0 || echo 1) \
  "generated operation carries the binary media type and exact header kinds"

reject_case "note_material_binary_header_missing" "笔记材料下载缺少 nosniff 响应头" \
  'download = doc["paths"]["/api/v1/native-notes/versions/{versionId}/files/{fileIndex}"]["get"]
headers = download["responses"]["200"]["headers"]
headers.pop("X-Content-Type-Options")'
reject_case "note_material_binary_header_extra" "笔记材料下载出现未建模响应头" \
  'download = doc["paths"]["/api/v1/native-notes/versions/{versionId}/files/{fileIndex}"]["get"]
download["responses"]["200"]["headers"]["X-Unmodelled"] = {"schema": {"type": "string"}}'
reject_case "note_material_binary_sha_pattern" "笔记材料下载 SHA 响应头绕过契约 pattern" \
  'download = doc["paths"]["/api/v1/native-notes/versions/{versionId}/files/{fileIndex}"]["get"]
download["responses"]["200"]["headers"]["X-Content-SHA256"]["schema"].pop("pattern")'
reject_case "note_material_binary_media_type" "笔记材料下载媒体类型变化" \
  'download = doc["paths"]["/api/v1/native-notes/versions/{versionId}/files/{fileIndex}"]["get"]
response = download["responses"]["200"]
response["content"]["text/plain"] = response["content"].pop("application/octet-stream")'

section "⑭ 生成物固定 UTF-8 + LF（Windows 刷新与 Linux CI 复跑必须逐字节相同）"
CR_CHECK="$("$PY" - "$ROOT/src/client-windows/Pim.Client.Core/Contract/ContractModels.cs" "$ROOT/src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs" <<'PYEOF'
import sys
bad = []
for path in sys.argv[1:]:
    with open(path, "rb") as fh:
        data = fh.read()
    if b"\r" in data:
        bad.append(path)
    try:
        data.decode("utf-8")
    except UnicodeDecodeError:
        bad.append(path + " (not utf-8)")
print(",".join(bad))
PYEOF
)"
record "generated_artifacts_are_utf8_lf" $([ -z "$CR_CHECK" ] && echo 0 || echo 1) \
  "no CR / valid UTF-8 in generated artifacts (offenders: '${CR_CHECK:-none}')"

say ""
say "== summary: PASS=$PASS FAIL=$FAIL NOT-RUN=$NR =="
if [ "$FAIL" -ne 0 ]; then
  say "RESULT: 存在失败断言（exit 1）"
  exit 1
fi
if [ "$NR" -ne 0 ]; then
  # 本地允许留 NOT-RUN 供人看，但 CI 里不许：必需负对照没跑到 = 门禁没有被验证过，
  # 不能因为「没跑」就拿到绿（fail-closed）。
  if [ "${CI:-}" = "true" ]; then
    say "RESULT: CI 下存在未执行的断言（NOT-RUN=$NR，exit 1）"
    exit 1
  fi
  say "注意：本地有 $NR 条断言未执行（CI 下会判失败）。"
fi
say "RESULT: 全部断言通过（exit 0）"
exit 0
