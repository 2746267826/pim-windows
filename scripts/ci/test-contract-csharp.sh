#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约 C# 生成物设施 · 行为测试（离线；唯一外部前置是 python3 + PyYAML）
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
#      readOnly 落在请求方向 / 内联根 schema / query 参数 → 一律拒绝产出且零写入
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

if ! command -v python3 >/dev/null 2>&1; then
  say "缺少 python3（生成器/校验器只用标准库）。"
  exit 2
fi
if ! python3 -c 'import yaml' 2>/dev/null; then
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
  ( cd "$1" && python3 scripts/contract/generate-csharp.py --out-dir "$1" ) > "$1.gen.log" 2>&1
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
python3 - "$SB/contract/openapi.json" <<'PY'
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
python3 - "$SB/scripts/contract/client-operations.json" <<'PY'
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
python3 - "$SB/scripts/contract/client-operations.json" <<'PY'
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
python3 - "$SB/contract/openapi.json" <<'PY'
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
CANDIDATES="$(python3 - "$ROOT/contract/openapi.json" "$ROOT/scripts/contract/client-operations.json" <<'PYEOF'
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
  python3 - "$1/scripts/contract/client-operations.json" "$2" <<'PYEOF'
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
python3 - "$CI_YML" "$STEP" "$TESTSTEP" "$GATE" "$GATE_ENV_MAP" "$CHECK_STEP_NAME" "$TEST_STEP_NAME" "$OWNER_JOB" <<'PY'
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
open(map_out, "w", encoding="utf-8").write(
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
COVERAGE="$(python3 - "$ROOT/scripts/ci/check-contract-csharp.sh" "$ROOT/src/client-windows/Pim.Client.Core/Contract" <<'PY'
import re, sys
from pathlib import Path
script = Path(sys.argv[1]).read_text(encoding="utf-8")
artifacts_dir = Path(sys.argv[2])
listed = set(re.findall(r'"(src/client-windows/Pim\.Client\.Core/Contract/[A-Za-z0-9_.]+\.cs)"', script))
present = {str(p.relative_to(artifacts_dir.parents[3])) for p in artifacts_dir.glob("*.cs")}
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
  python3 - "$dir/contract/openapi.json" <<PYEOF
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

reject_case "inline_enum" "属性上的内联 enum" \
  'schemas["LoginRequest"]["properties"]["username"]["enum"] = ["alice", "bob"]'
reject_case "non_identifier_enum_value" "枚举取值不是合法 C# 标识符（改名就是改线格式）" \
  'schemas["DaemonSourceState"]["enum"] = ["Unknown", "in-progress", "Available", "Unavailable", "Paused"]'
reject_case "integer_enum" "整数枚举（契约没给成员名）" \
  'schemas["DaemonSourceState"] = {"type": "integer", "format": "int32", "enum": [0, 1, 2]}'
reject_case "properties_plus_additional" "对象同时有 properties 与 additionalProperties" \
  'schemas["KeystatsUploadRequest"]["properties"]["keyPressCounts"] = {"type": "object", "properties": {"a": {"type": "string"}}, "additionalProperties": {"type": "integer", "format": "int32"}}'
reject_case "readonly_in_request_schema" "readOnly 字段出现在请求方向的 schema 里" \
  'schemas["LoginRequest"]["properties"]["username"]["readOnly"] = True'
reject_case "inline_root_request" "请求体根不是 \$ref（内联形状）" \
  'doc["paths"]["/api/v1/auth/login"]["post"]["requestBody"]["content"]["application/json"]["schema"] = {"type": "object", "properties": {"username": {"type": "string"}}}'
reject_case "query_parameter" "operation 带 query 参数（生成器只表达 path 参数）" \
  'doc["paths"]["/api/v1/endpoints"]["get"]["parameters"] = [{"name": "page", "in": "query", "schema": {"type": "integer"}}]'

section "⑭ 生成物固定 UTF-8 + LF（Windows 刷新与 Linux CI 复跑必须逐字节相同）"
CR_CHECK="$(python3 - "$ROOT/src/client-windows/Pim.Client.Core/Contract/ContractModels.cs" "$ROOT/src/client-windows/Pim.Client.Core/Contract/ContractOperations.cs" <<'PYEOF'
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
