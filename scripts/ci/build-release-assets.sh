#!/usr/bin/env bash
#
# 审计 F-06 / REQ-COM-04 / AC-COM-04：
# 发版时的「资产归一化 + version.json 生成」纯逻辑抽到这里，
# 这样它能在**没有发布权限的 PR** 上被真实执行和测试，而不是只在 master 发版那一刻跑。
#
# 用法：
#   build-release-assets.sh --version <ver> --tag <tag> --repository <owner/repo> \
#                           --dist <下载的产物目录> --out <输出目录> [--allow-missing-installer]
#
# 产出：
#   <out>/pim-windows-v<ver>.zip
#   <out>/pim-shell-windows-v<ver>.zip
#   <out>/PIM-Setup-v<ver>.exe          （存在安装器时）
#   <out>/version.json                  （客户端更新检查的数据源）
#
# 退出码：0 = 成功；1 = 输入不合法 / 必需产物缺失 / 脚本内部错误；2 = 用法错误。
#
# 失败即失败：缺 windows / shell 产物时**不生成** version.json 并 exit 1，
# 不允许“少一个资产也照样发版”。安装器缺失默认也失败，除非显式 --allow-missing-installer。
set -euo pipefail

VERSION=""
TAG=""
REPOSITORY=""
DIST=""
OUT=""
ALLOW_MISSING_INSTALLER=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --tag) TAG="$2"; shift 2 ;;
    --repository) REPOSITORY="$2"; shift 2 ;;
    --dist) DIST="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --allow-missing-installer) ALLOW_MISSING_INSTALLER=true; shift ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
done

die() { echo "build-release-assets: $*" >&2; exit 1; }

[[ -n "$VERSION" ]] || die "--version 必填（发版版本号）"
[[ -n "$TAG" ]] || die "--tag 必填（release tag；GitHub Releases 需要它）"
[[ -n "$REPOSITORY" ]] || die "--repository 必填（owner/repo，用于拼资产下载直链）"
[[ -n "$DIST" ]] || die "--dist 必填（下载产物所在目录）"
[[ -n "$OUT" ]] || die "--out 必填（输出目录）"
[[ -d "$DIST" ]] || die "--dist 目录不存在: $DIST"
command -v jq >/dev/null 2>&1 || die "jq 不可用，无法生成 version.json"

# 版本号只接受 <数字>.<数字>.<数字>[+后缀]，避免把奇怪的输入写进资产名与 version.json。
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][A-Za-z0-9._-]+)?$ ]]; then
  die "版本号格式不合法: $VERSION"
fi

mkdir -p "$OUT"

# 从一个目录里找唯一的匹配文件；找不到或找到多个都算失败。
find_one() {
  local pattern="$1" label="$2"
  local matches
  matches="$(find "$DIST" -type f -name "$pattern" | sort)"
  if [[ -z "$matches" ]]; then
    die "缺少必需的 $label 产物（在 $DIST 下找不到 $pattern）"
  fi
  local count
  count="$(printf '%s\n' "$matches" | wc -l)"
  if [[ "$count" -ne 1 ]]; then
    die "$label 产物不唯一（$pattern 匹配到 $count 个）：$(printf '%s' "$matches" | tr '\n' ' ')"
  fi
  printf '%s' "$matches"
}

WINDOWS_SRC="$(find_one 'pim-windows-v*.zip' 'Windows 守护程序')"
SHELL_SRC="$(find_one 'pim-shell-windows-v*.zip' 'Windows 壳')"
SETUP_SRC=""
SETUP_MATCHES="$(find "$DIST" -type f -name 'PIM-Setup-v*.exe' | sort)"
if [[ -n "$SETUP_MATCHES" ]]; then
  if [[ "$(printf '%s\n' "$SETUP_MATCHES" | wc -l)" -ne 1 ]]; then
    die "安装器产物不唯一：$(printf '%s' "$SETUP_MATCHES" | tr '\n' ' ')"
  fi
  SETUP_SRC="$SETUP_MATCHES"
elif [[ "$ALLOW_MISSING_INSTALLER" != true ]]; then
  die "缺少安装器产物（PIM-Setup-v*.exe）；如确属预期，请显式传 --allow-missing-installer"
fi

WINDOWS_ASSET="pim-windows-v${VERSION}.zip"
SHELL_ASSET="pim-shell-windows-v${VERSION}.zip"
SETUP_ASSET=""
[[ -n "$SETUP_SRC" ]] && SETUP_ASSET="PIM-Setup-v${VERSION}.exe"

cp "$WINDOWS_SRC" "$OUT/$WINDOWS_ASSET"
cp "$SHELL_SRC" "$OUT/$SHELL_ASSET"
[[ -n "$SETUP_SRC" ]] && cp "$SETUP_SRC" "$OUT/$SETUP_ASSET"

BASE_URL="https://github.com/${REPOSITORY}/releases/download/${TAG}"

# 注意：jq 里对象构造后的 `+` 必须写在同一个表达式内；把 `+` 留在花括号
# 外面（前一行以 `}` 收尾）会报 `jq: error: syntax error, unexpected '+'`。
jq -n \
  --arg v "$VERSION" --arg base "$BASE_URL" \
  --arg w "$WINDOWS_ASSET" --arg s "$SHELL_ASSET" --arg setup "$SETUP_ASSET" \
  '{version: $v, releasedAt: (now|todateiso8601),
    assets: ({windows:      {version: $v, url: ($base + "/" + $w)},
              shellWindows: {version: $v, url: ($base + "/" + $s)}}
             + (if $setup == "" then {} else {installer: {version: $v, url: ($base + "/" + $setup)}} end))}' \
  > "$OUT/version.json"

# 自检：version.json 必须可被解析，且 version 字段与输入一致。
if ! jq -e --arg v "$VERSION" '.version == $v and (.assets.windows.url | type == "string")' \
     "$OUT/version.json" >/dev/null; then
  die "生成的 version.json 自检失败: $(cat "$OUT/version.json")"
fi

{
  echo "版本: $VERSION"
  echo "tag:  $TAG"
  echo "资产:"
  echo "  $WINDOWS_ASSET"
  echo "  $SHELL_ASSET"
  [[ -n "$SETUP_ASSET" ]] && echo "  $SETUP_ASSET"
  echo "  version.json"
} >&2
