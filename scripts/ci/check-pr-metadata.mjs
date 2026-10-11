#!/usr/bin/env node
/**
 * 审计 F-12 / AC-COM-05：PR 元数据校验（双语四段 + 分支命名）。
 *
 * job 在全部 workflow event 上运行。`pull_request` 执行真实 payload
 * （`github.event.pull_request.title` / `.body` 与 `github.head_ref`）。
 * 其它 event 没有 PR body：`EVENT_NAME` 不是 `pull_request` 时步骤自身 exit 0
 * （AGENTS.md：push master 不依赖 PR body）。不把 job 标成 skipped。
 *
 * 检查项（全部来自 AGENTS.md 的既有约定，不新造规则）：
 *   1. 标题存在且中英双语（含 CJK 与拉丁字母）；
 *   2. 正文包含四个双语章节标题（技术修改 / 功能变化 / 如何体验 / 测试），
 *      标题格式与 `.github/pull_request_template.md` 一致，中英任意一侧都在即可；
 *   3. head 分支符合 `{agent}-{os}/{topic}`，或工具自动创建的 `task/<n>`。
 *
 * 不回写历史 PR、不改 ruleset —— 只对本次 payload 判定。
 *
 * 用法（CI 里由 ci.yml 的 pr-metadata job 调用）：
 *   PR_TITLE=... PR_BODY=... HEAD_REF=... node scripts/ci/check-pr-metadata.mjs
 * 退出码：0 = 通过；1 = 有违反（逐条打印原因）。
 */

import process from 'node:process';

/** 四个章节的标题锚点：中英任一语言出现即可（模板两侧都写了）。 */
const REQUIRED_SECTIONS = [
  { id: '技术修改', anchors: ['技术修改', 'Technical changes'] },
  { id: '功能变化', anchors: ['功能变化', 'Feature changes'] },
  { id: '如何体验', anchors: ['如何体验', 'How to try it'] },
  { id: '测试', anchors: ['测试', 'Tests'] },
];

const BRANCH_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*-(?:win|linux)\/[a-z0-9][a-z0-9.-]*$/;
const TOOL_BRANCH_PATTERN = /^task\/\d+$/;

export function checkPrMetadata({ title, body, headRef }) {
  const violations = [];
  const text = (value) => (typeof value === 'string' ? value : '');

  const prTitle = text(title).trim();
  const prBody = text(body);
  const branch = text(headRef).trim();

  if (prTitle === '') {
    violations.push('缺少 PR 标题（双语标题必须提供）。');
  } else {
    const hasCjk = /[\u4e00-\u9fff]/.test(prTitle);
    const hasLatin = /[A-Za-z]/.test(prTitle);
    if (!hasCjk || !hasLatin) {
      violations.push(`PR 标题必须中英双语（同时含中文与英文）：${JSON.stringify(prTitle)}`);
    }
  }

  const headingLines = prBody
    .split(/\r?\n/)
    .filter((line) => /^#{1,4}\s+/.test(line))
    .join('\n');
  for (const section of REQUIRED_SECTIONS) {
    const found = section.anchors.some((anchor) => headingLines.includes(anchor));
    if (!found) {
      violations.push(
        `PR 正文缺少章节「${section.id} / ${section.anchors[1]}」（四个双语章节都必须保留，`
          + '标题格式见 .github/pull_request_template.md）。',
      );
    }
  }

  if (branch === '') {
    violations.push('缺少 head 分支名（应为 {agent}-{os}/{topic}）。');
  } else if (!BRANCH_PATTERN.test(branch) && !TOOL_BRANCH_PATTERN.test(branch)) {
    violations.push(
      `head 分支不符合 {agent}-{os}/{topic}（os 为 win 或 linux，topic 为 kebab-case 英文）：${JSON.stringify(branch)}`,
    );
  }

  return violations;
}

function main() {
  const eventName = process.env.EVENT_NAME ?? '';
  if (eventName !== '' && eventName !== 'pull_request') {
    console.log(`pr-metadata: ${eventName} 没有 PR body，校验步骤自身退出 0`);
    return 0;
  }

  const violations = checkPrMetadata({
    title: process.env.PR_TITLE,
    body: process.env.PR_BODY,
    headRef: process.env.HEAD_REF,
  });

  if (violations.length > 0) {
    for (const violation of violations) {
      console.error(`::error::${violation}`);
    }
    console.error('');
    console.error(`pr-metadata: ${violations.length} 项不符合 AGENTS.md 的 PR 约定`);
    return 1;
  }

  console.log('pr-metadata: 双语四段与分支命名均符合 AGENTS.md');
  return 0;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  process.exit(main());
}
