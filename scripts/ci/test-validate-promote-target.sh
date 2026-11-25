#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：promote_tag 的来源校验。
#
# 重点：tag 不能只凭名字就提升。必须同时证明
#   * tag 指向的 commit 在当前 master 历史里；
#   * 该 commit 的 VERSION 就是输入的显示版本；
#   * tag 里的 run 号能在该 commit 上找到一个成功的 master push 构建；
#   * release 的 targetCommitish / 四类 Windows 资产 / version.json 都绑定到这个
#     commit、run 和 tag；
#   * 不会把 /releases/latest 指回旧 CalVer 或低显示版本。
#
# 这里的 gh stub 按 **真实 gh 2.98 / REST 字段形状** 校验 --json 字段名：
# release view 没有 isLatest。脚本若再次请求该字段，正例会直接失败。
# ---------------------------------------------------------------------------
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/validate-promote-target.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-promote-target.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
ok() { echo "  ✅ $1"; pass=$((pass + 1)); }
bad() { echo "  ❌ $1"; fail=$((fail + 1)); }

mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail

not_found() {
  printf '{"message":"Not Found","status":"404"}\n'
  echo "gh: Not Found (HTTP 404)" >&2
  exit 1
}

cmd="${1:-}"; shift || true
case "$cmd" in
  release)
    sub="${1:-}"; shift || true
    case "$sub" in
      view)
        fields=""
        while [[ $# -gt 0 ]]; do
          case "$1" in
            --json) fields="$2"; shift 2 ;;
            --repo) shift 2 ;;
            *) shift ;;
          esac
        done
        # gh 2.98 `gh release view --help` 的真实 JSON 字段。
        IFS=',' read -ra FIELDS <<< "$fields"
        for f in "${FIELDS[@]}"; do
          case "$f" in
            apiUrl|assets|author|body|createdAt|databaseId|id|isDraft|isImmutable|isPrerelease|name|publishedAt|tagName|tarballUrl|targetCommitish|uploadUrl|url|zipballUrl) ;;
            *) echo "Unknown JSON field: \"$f\"" >&2; exit 1 ;;
          esac
        done
        jq -n \
          --arg tagTag "${FAKE_TAG_NAME:-windows-v26.11.42}" \
          --argjson pre "${FAKE_IS_PRE:-true}" \
          --argjson draft "${FAKE_IS_DRAFT:-false}" \
          --argjson id "${FAKE_RELEASE_ID:-500}" \
          --arg target "${FAKE_TARGET:-${FAKE_SHA:-}}" \
          --argjson assets "${FAKE_ASSETS:-[]}" \
          '{databaseId:$id,tagName:$tagTag,isPrerelease:$pre,isDraft:$draft,targetCommitish:$target,assets:$assets}'
        ;;
      download)
        dir=""
        while [[ $# -gt 0 ]]; do
          case "$1" in
            --dir) dir="$2"; shift 2 ;;
            *) shift ;;
          esac
        done
        [[ -n "$dir" ]] || { echo "stub gh: --dir required" >&2; exit 2; }
        tag="${FAKE_TAG_NAME:-windows-v26.11.42}"
        asset_v="${FAKE_ASSET_VERSION:-26.11.42}"
        base="https://github.com/2746267826/pim-windows/releases/download/${tag}"
        jq -n \
          --arg v "$asset_v" \
          --arg base "$base" \
          --arg w "${FAKE_URL_WINDOWS:-$base/pim-windows-v${asset_v}.zip}" \
          --arg s "${FAKE_URL_SHELL:-$base/pim-shell-windows-v${asset_v}.zip}" \
          --arg i "${FAKE_URL_INSTALLER:-$base/PIM-Setup-v${asset_v}.exe}" \
          '{version:$v,releasedAt:"2026-11-25T00:00:00Z",
            assets:{windows:{version:$v,url:$w},shellWindows:{version:$v,url:$s},installer:{version:$v,url:$i}}}' \
          > "$dir/version.json"
        ;;
      *) echo "stub gh: unsupported release $sub" >&2; exit 2 ;;
    esac
    ;;
  api)
    path="${1:-}"
    case "$path" in
      */releases/latest)
        case "${FAKE_LATEST_MODE:-none}" in
          none) not_found ;;
          error) echo "gh: Server Error (HTTP 500)" >&2; exit 1 ;;
          json) cat "${FAKE_LATEST_JSON:?FAKE_LATEST_JSON required}" ;;
          *) echo "stub gh: unknown FAKE_LATEST_MODE" >&2; exit 2 ;;
        esac
        ;;
      */git/ref/tags/*)
        if [[ "${FAKE_TAG_TYPE:-commit}" == "tag" ]]; then
          jq -n --arg sha "${FAKE_ANNOTATED_SHA:-aaaa}" '{object:{type:"tag",sha:$sha}}'
        else
          jq -n --arg sha "${FAKE_SHA:?FAKE_SHA required}" '{object:{type:"commit",sha:$sha}}'
        fi
        ;;
      */git/tags/*)
        jq -n --arg sha "${FAKE_SHA:?FAKE_SHA required}" '{object:{type:"commit",sha:$sha}}'
        ;;
      *) echo "stub gh: unsupported api $path" >&2; exit 2 ;;
    esac
    ;;
  run)
    sub="${1:-}"; shift || true
    case "$sub" in
      list)
        jq -n \
          --argjson id "${FAKE_RUN_ID:-999}" \
          --argjson number "${FAKE_RUN_NUMBER:-42}" \
          --arg sha "${FAKE_RUN_HEAD_SHA:-${FAKE_SHA}}" \
          --arg conclusion "${FAKE_RUN_CONCLUSION:-success}" \
          --arg status "${FAKE_RUN_STATUS:-completed}" \
          '[{databaseId:$id,event:"push",headBranch:"master",headSha:$sha,conclusion:$conclusion,number:$number,status:$status}]'
        ;;
      view)
        jq -n \
          --argjson id "${FAKE_RUN_ID:-999}" \
          --argjson number "${FAKE_RUN_NUMBER:-42}" \
          --arg sha "${FAKE_RUN_VIEW_HEAD_SHA:-${FAKE_SHA}}" \
          --arg conclusion "${FAKE_RUN_VIEW_CONCLUSION:-${FAKE_RUN_CONCLUSION:-success}}" \
          --arg status "${FAKE_RUN_VIEW_STATUS:-${FAKE_RUN_STATUS:-completed}}" \
          --arg branch "${FAKE_RUN_VIEW_BRANCH:-master}" \
          '{databaseId:$id,event:"push",headBranch:$branch,headSha:$sha,number:$number,status:$status,conclusion:$conclusion}'
        ;;
      *) echo "stub gh: unsupported run $sub" >&2; exit 2 ;;
    esac
    ;;
  *) echo "stub gh: unsupported command $cmd" >&2; exit 2 ;;
esac
STUB
chmod +x "$TMP/bin/gh"
export PATH="$TMP/bin:$PATH"

make_repo() { # <dir> <display-version>
  git init -q "$1"
  git -C "$1" config user.email test@example.com
  git -C "$1" config user.name test
  printf '%s\n' "$2" > "$1/VERSION"
  git -C "$1" add VERSION
  git -C "$1" commit -q -m "init"
}

write_latest() { # <file> <id> <tag> <title> <target> [body]
  jq -n \
    --argjson id "$2" \
    --arg tag "$3" \
    --arg name "$4" \
    --arg target "$5" \
    --arg body "${6:-}" \
    '{id:$id,tag_name:$tag,prerelease:false,draft:false,target_commitish:$target,name:$name,body:$body}' \
    > "$1"
}

ASSETS='[
  {"name":"pim-windows-v26.11.42.zip"},
  {"name":"pim-shell-windows-v26.11.42.zip"},
  {"name":"PIM-Setup-v26.11.42.exe"},
  {"name":"version.json"}
]'

run_case() { # <label> <expected rc|pattern> <out-file> <env...>
  local label="$1" want="$2" out="$3"; shift 3
  local rc=0
  env "$@" bash "$SCRIPT" --repo 2746267826/pim-windows --tag windows-v26.11.42 --version 1.1.0 > "$out" 2>&1 || rc=$?
  if [[ "$want" == "pass" ]]; then
    if [[ "$rc" -eq 0 ]]; then ok "$label"; else bad "$label（rc=$rc）：$(cat "$out")"; fi
  else
    if [[ "$rc" -eq 0 ]]; then
      bad "$label：期望失败，实际通过"
    elif ! grep -q "$want" "$out"; then
      bad "$label：期望错误包含 [$want]，实际：$(cat "$out")"
    else
      ok "$label"
    fi
  fi
}

echo "== validate-promote-target.sh =="

# 回归测试的前提：stub 与真实 gh 2.98 一样不接受 isLatest。
if gh release view --json isLatest >/dev/null 2>&1; then
  bad "gh stub 应拒绝不存在的 isLatest 字段"
else
  ok "gh stub 按真实字段形状拒绝 isLatest"
fi

REPO_GOOD="$TMP/repo-good"
make_repo "$REPO_GOOD" 1.1.0
SHA_GOOD="$(git -C "$REPO_GOOD" rev-parse HEAD)"

pushd "$REPO_GOOD" >/dev/null

run_case "完整来源证明（无 latest）→ 通过" pass "$TMP/out-good" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999
if grep -q '^run_id=999$' "$TMP/out-good" \
   && grep -q '^tag=windows-v26.11.42$' "$TMP/out-good" \
   && grep -q '^is_latest=false$' "$TMP/out-good"; then
  ok "输出 tag / run_id / is_latest=false"
else
  bad "输出缺少 tag / run_id / is_latest：$(cat "$TMP/out-good")"
fi

write_latest "$TMP/latest-same.json" 500 windows-v26.11.42 \
  "PIM Windows v1.1.0 (windows-v26.11.42)" "$SHA_GOOD"
run_case "目标就是当前 latest 的正式版重入 → 通过" pass "$TMP/out-formal" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=false FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-same.json"
if grep -q '^is_latest=true$' "$TMP/out-formal"; then
  ok "重入输出 is_latest=true"
else
  bad "重入输出缺少 is_latest=true：$(cat "$TMP/out-formal")"
fi

write_latest "$TMP/latest-older.json" 400 windows-v26.11.40 \
  "PIM Windows v1.0.0 (windows-v26.11.40)" "$SHA_GOOD"
run_case "更新的 CalVer + 更高显示版本 → 允许前进" pass "$TMP/out-newer" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-older.json"

write_latest "$TMP/latest-old-no-display.json" 410 windows-v26.11.41 \
  "PIM Windows windows-v26.11.41" master
run_case "latest 早于双轨版本号（无显示版本）→ 按更新的 CalVer 允许迁移" pass "$TMP/out-migration" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-old-no-display.json"

write_latest "$TMP/latest-newer.json" 501 windows-v26.11.43 \
  "PIM Windows v1.1.0 (windows-v26.11.43)" "$SHA_GOOD"
run_case "同显示版本的旧 CalVer tag → 拒绝回退 latest" "回退" "$TMP/out-older" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-newer.json"

write_latest "$TMP/latest-higher-display.json" 410 windows-v26.11.41 \
  "PIM Windows v1.2.0 (windows-v26.11.41)" "$SHA_GOOD"
run_case "新 CalVer 但显示版本更低 → 拒绝回退" "显示版本" "$TMP/out-display" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-higher-display.json"

write_latest "$TMP/latest-unparsable.json" 410 latest-2026 \
  "PIM Windows v1.1.0" "$SHA_GOOD"
run_case "latest tag 无法解析 CalVer → fail-closed" "CalVer" "$TMP/out-unparsable" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-unparsable.json"

run_case "/releases/latest API 错误 → fail-closed" "fail-closed" "$TMP/out-latest-error" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=error

printf '%s\n' '{"id": 1}' > "$TMP/latest-incomplete.json"
run_case "/releases/latest 对象不完整 → fail-closed" "不完整" "$TMP/out-latest-incomplete" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_RELEASE_ID=500 \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 \
  FAKE_LATEST_MODE=json FAKE_LATEST_JSON="$TMP/latest-incomplete.json"

run_case "targetCommitish 是分支名 → 拒绝" "targetCommitish" "$TMP/out-target-branch" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_TARGET=master \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "targetCommitish 指向别的 commit → 拒绝" "targetCommitish" "$TMP/out-target" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_TARGET=1111111111111111111111111111111111111111 \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "缺少 version.json 资产 → 拒绝" "version.json" "$TMP/out-assets-ledger" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true \
  FAKE_ASSETS='[{"name":"pim-windows-v26.11.42.zip"},{"name":"pim-shell-windows-v26.11.42.zip"},{"name":"PIM-Setup-v26.11.42.exe"}]' \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "缺安装器资产 → 拒绝" "安装器" "$TMP/out-assets" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true \
  FAKE_ASSETS='[{"name":"pim-windows-v26.11.42.zip"},{"name":"pim-shell-windows-v26.11.42.zip"},{"name":"version.json"}]' \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "version.json 的 version 与 tag 不符 → 拒绝" "不一致" "$TMP/out-version-json" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_ASSET_VERSION=26.11.41 \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "version.json 下载直链指向别的 tag → 拒绝" "下载直链" "$TMP/out-version-url" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_URL_WINDOWS="https://github.com/2746267826/pim-windows/releases/download/windows-v26.11.41/pim-windows-v26.11.42.zip" \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "run 号与 tag 不符 → 拒绝" "run number" "$TMP/out-run" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=41 FAKE_RUN_ID=999

run_case "run 列表 headSha 不是 tag commit → 拒绝" "没有成功的 master push" "$TMP/out-run-sha" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_HEAD_SHA=2222222222222222222222222222222222222222 \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "run 详情 headSha 不一致 → 拒绝" "headSha" "$TMP/out-run-view-sha" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_VIEW_HEAD_SHA=2222222222222222222222222222222222222222 \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "run 详情不是 master → 拒绝" "master 分支" "$TMP/out-run-view-branch" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_VIEW_BRANCH=develop \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "run 详情结论失败 → 拒绝" "不是成功构建" "$TMP/out-run-view-conclusion" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_VIEW_CONCLUSION=failure \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

run_case "annotated tag 解析后仍绑定 commit → 通过" pass "$TMP/out-annotated" \
  FAKE_SHA="$SHA_GOOD" FAKE_TAG_TYPE=tag FAKE_ANNOTATED_SHA=bbbb \
  FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999

popd >/dev/null

REPO_BAD_VERSION="$TMP/repo-bad-version"
make_repo "$REPO_BAD_VERSION" 2.0.0
SHA_BAD_VERSION="$(git -C "$REPO_BAD_VERSION" rev-parse HEAD)"
pushd "$REPO_BAD_VERSION" >/dev/null
run_case "tag commit 的 VERSION 不是目标版本 → 拒绝" "VERSION" "$TMP/out-version" \
  FAKE_SHA="$SHA_BAD_VERSION" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999
popd >/dev/null

REPO_DIRTY_VERSION="$TMP/repo-dirty-version"
make_repo "$REPO_DIRTY_VERSION" 1.1.0
SHA_DIRTY_VERSION="$(git -C "$REPO_DIRTY_VERSION" rev-parse HEAD)"
printf '1.2.0\n' > "$REPO_DIRTY_VERSION/VERSION"
pushd "$REPO_DIRTY_VERSION" >/dev/null
run_case "当前 checkout 的 VERSION 不是目标版本 → 拒绝" "当前 checkout" "$TMP/out-head-version" \
  FAKE_SHA="$SHA_DIRTY_VERSION" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999
popd >/dev/null

if [[ "$fail" -ne 0 ]]; then
  echo "validate-promote-target tests failed: ${fail} failed, ${pass} passed" >&2
  exit 1
fi
echo "validate-promote-target tests passed (${pass} checks)"
